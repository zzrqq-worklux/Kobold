using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kobold.Core;
using Kobold.Controls;
using Kobold.Services;
using Kobold.Windows;

namespace Kobold.Core
{
    /// <summary>
    /// Manages all folder widgets - singleton pattern
    /// </summary>
    public class WidgetManager
    {
        private static WidgetManager _instance;
        private static readonly object _lock = new object();
        
        private AppConfig _config;
        private List<FolderWidget> _widgets = new List<FolderWidget>();
        private IslandWindow _island;

        private readonly IdleTrimPolicy _trimPolicy = new IdleTrimPolicy();
        private readonly System.Windows.Threading.DispatcherTimer _trimTimer;
        private DateTime _lastInteractionUtc = DateTime.UtcNow;

        public static WidgetManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new WidgetManager();
                        }
                    }
                }
                return _instance;
            }
        }

        public AppConfig Config => _config;
        public IReadOnlyList<FolderWidget> Widgets => _widgets.AsReadOnly();

        private WidgetManager()
        {
            _config = AppConfig.Load();
            Localization.CurrentLanguage = _config.Language;
            
            // Initialize theme system with current theme
            ThemeManager.Initialize(_config.Theme ?? "dark");

            // WPF keeps its render caches for the life of the process (hiding or
            // closing a window returns almost nothing), so a long-idle app hands
            // its working set back to the OS instead. See Services/MemoryTrimmer.
            _trimTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = IdleTrimPolicy.CheckInterval
            };
            _trimTimer.Tick += OnTrimTick;
        }

        /// <summary>
        /// Initializes and creates all widgets from config
        /// </summary>
        public void Initialize()
        {
            // Create widgets for each folder in config
            foreach (var folderData in _config.Folders)
            {
                CreateWidgetInternal(folderData);
            }

            // The island is the shared entry point that launches widget panels
            _island = new IslandWindow();
            _island.WidgetActivated += OnWidgetActivated;
            _island.WidgetMenuRequested += OnWidgetMenuRequested;
            RefreshIsland();
            _island.Show();

            // Reopen the panels that were still open when the app last closed.
            // No activation: starting up must not steal focus from other apps.
            foreach (var folder in StartupPanels.ToRestore(_config.Folders))
            {
                var widget = _widgets.FirstOrDefault(w => w.FolderId == folder.Id);
                if (widget != null) OpenPanel(widget, activate: false);
            }

            _trimTimer.Start();
        }

        /// <summary>Any input anywhere in the app restarts the idle window the trim uses.</summary>
        public void NotifyInteraction()
        {
            _lastInteractionUtc = DateTime.UtcNow;
            _trimPolicy.NotifyInteraction();
        }

        /// <summary>True while something in flight must not be paged out mid-way.</summary>
        private bool IsBusy()
        {
            if (DragDropSession.Current != null) return true;
            return _widgets.Any(w => w.IsModalOpen);
        }

        private void OnTrimTick(object sender, EventArgs e)
        {
            if (!_trimPolicy.ShouldTrim(DateTime.UtcNow - _lastInteractionUtc, IsBusy())) return;

            if (MemoryTrimmer.Trim()) _trimPolicy.NotifyTrimmed();
        }

        /// <summary>
        /// Refreshes the widget tiles shown by the island.
        /// </summary>
        public void RefreshIsland()
        {
            _island?.SetWidgets(_config.Folders);
        }

        /// <summary>
        /// Opens the clicked widget's panel centered below the island.
        /// </summary>
        private void OnWidgetActivated(string folderId)
        {
            var widget = _widgets.FirstOrDefault(w => w.FolderId == folderId);
            if (widget == null || _island == null) return;

            if (widget.IsPanelOpen)
            {
                widget.HidePanel();
                return;
            }

            OpenPanel(widget);
        }

        /// <summary>
        /// Opens a widget's menu (rename / colour / grid / size / delete) when its
        /// island tile is right-clicked, keeping the island expanded while the
        /// menu is up.
        /// </summary>
        private void OnWidgetMenuRequested(string folderId)
        {
            var widget = _widgets.FirstOrDefault(w => w.FolderId == folderId);
            if (widget == null || _island == null) return;

            _island.PauseAutoCollapse();
            widget.ShowWidgetMenu(() => _island.ResumeAutoCollapse());
        }

        /// <summary>Opens a widget's panel; defaults to just below the island when unplaced.</summary>
        private void OpenPanel(FolderWidget widget, bool activate = true)
        {
            if (_island == null) return;
            var (panelWidth, _) = widget.GetPanelSize();
            double left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - panelWidth) / 2);
            widget.ShowPanel(left, _island.PanelTop, activate);
        }

        /// <summary>
        /// Creates a new widget and adds it to config
        /// </summary>
        public FolderWidget CreateWidget(string name, string color, int posX, int posY, int gridColumns = 3)
        {
            var folderData = _config.AddFolder(name, color, posX, posY, gridColumns);
            folderData.IsPanelPinned = true; // opens pinned so it survives while being filled
            var widget = CreateWidgetInternal(folderData);
            RefreshIsland();
            OpenPanel(widget);
            SaveConfig();
            return widget;
        }

        /// <summary>
        /// Brings the island up so all widget entries are visible.
        /// </summary>
        public void ShowIsland()
        {
            _island?.ShowIsland();
        }

        /// <summary>Opens the settings window - shared by the tray menu and the island entry.</summary>
        public void ShowSettings()
        {
            new SettingsWindow().ShowDialog();
        }

        /// <summary>
        /// Creates a widget with the default name/color/position - shared by
        /// the tray menu and the island add entry.
        /// </summary>
        public void CreateWidgetWithDefaults()
        {
            int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            int screenHeight = (int)SystemParameters.PrimaryScreenHeight;
            int widgetCount = _widgets.Count;

            int posX = screenWidth / 2 - 50 + (widgetCount % 5) * 30;
            int posY = screenHeight / 2 - 50 + (widgetCount % 5) * 30;

            string color = UiTokens.FolderPalette[widgetCount % 5];
            string name = Localization.Get("UI_DefaultFolderName");
            int gridColumns = _config.DefaultGridColumns;

            CreateWidget(name, color, posX, posY, gridColumns);
        }

        /// <summary>Pushes the persisted island settings (collapse delay + position) to the island.</summary>
        public void ApplyIslandSettings()
        {
            _island?.ApplySettings();
        }

        /// <summary>Applies the saved "hide desktop icons" preference at startup.</summary>
        public void ApplyDesktopIconsOnStartup()
        {
            // Remember the state before this run changes anything.
            _config.HideIconsOriginal = DesktopIcons.ReadRaw();
            if (_config.HideDesktopIcons) DesktopIcons.SetHidden(true);
            _config.Save();
        }

        /// <summary>
        /// Restores the pre-run desktop-icon state on exit, but only while the
        /// preference is still "hidden" - if the user unchecked it we leave icons shown.
        /// </summary>
        public void RestoreDesktopIconsOnExit()
        {
            if (_config.HideDesktopIcons) DesktopIcons.Restore(_config.HideIconsOriginal);
        }

        /// <summary>
        /// Hides/shows the desktop icons directly and remembers the choice.
        /// Returns false when the system refused the change, so the caller can revert.
        /// </summary>
        public bool SetHideDesktopIcons(bool hide)
        {
            if (!DesktopIcons.SetHidden(hide)) return false;
            _config.HideDesktopIcons = hide;
            _config.Save();
            return true;
        }

        /// <summary>
        /// Creates a widget for existing folder data
        /// </summary>
        private FolderWidget CreateWidgetInternal(FolderData data)
        {
            var widget = new FolderWidget(data);
            
            widget.OnDeleted += (w) =>
            {
                _widgets.Remove(w);
                _config.RemoveFolder(w.FolderId);
                RefreshIsland();
            };
            
            widget.OnDataChanged += () =>
            {
                SaveConfig();
                RefreshIsland();
            };
            
            _widgets.Add(widget);
            
            return widget;
        }

        /// <summary>
        /// Removes a widget by ID
        /// </summary>
        public void RemoveWidget(string id)
        {
            var widget = _widgets.FirstOrDefault(w => w.FolderId == id);
            if (widget != null)
            {
                _widgets.Remove(widget);
                widget.Close();
                _config.RemoveFolder(id);
                RefreshIsland();
            }
        }

        /// <summary>
        /// Opens every widget's panel, one at a time. Rendering them all in the
        /// same frame flickers, so each is opened a short interval after the last.
        /// </summary>
        public void ShowAll()
        {
            var pending = _widgets.Where(w => !w.IsPanelOpen).ToList();
            if (pending.Count == 0) return;

            int index = 0;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            timer.Tick += (s, e) =>
            {
                if (index >= pending.Count)
                {
                    timer.Stop();
                    return;
                }
                OpenPanel(pending[index], activate: false);
                index++;
            };
            timer.Start();
        }

        /// <summary>
        /// Hides every widget's panel
        /// </summary>
        public void HideAll()
        {
            foreach (var widget in _widgets)
            {
                widget.HidePanel();
            }
        }

        /// <summary>
        /// Saves configuration
        /// </summary>
        public void SaveConfig()
        {
            _config.SaveDebounced();
        }

        /// <summary>
        /// Sets language and updates UI
        /// </summary>
        public void SetLanguage(string lang)
        {
            _config.Language = lang;
            Localization.CurrentLanguage = lang;
            _config.Save();
            
            // Update all widgets
            foreach (var widget in _widgets)
            {
                widget.UpdateUI();
            }
        }

        /// <summary>
        /// Refreshes all widgets UI with current theme
        /// </summary>
        public void RefreshAllWidgets()
        {
            foreach (var widget in _widgets)
            {
                widget.RefreshTheme();
            }
        }

        /// <summary>
        /// Final save for a Windows logoff/shutdown: the session may not live
        /// long enough for the debounced timer, and no further writes should
        /// start afterwards. The tray-exit path keeps its own Shutdown().
        /// </summary>
        public void BeginExitSave()
        {
            _config.BeginClosing();
        }

        /// <summary>
        /// Shuts down all widgets
        /// </summary>
        public void Shutdown()
        {
            _trimTimer.Stop();

            // Ensure config is saved before exit
            _config.Save();
            
            foreach (var widget in _widgets.ToList())
            {
                widget.Close();
            }
            _widgets.Clear();
        }
    }
}


