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

        // Set by OverrideConfigPathsForChecks before the first Instance access:
        // the desktop self-checks point the singleton at a throwaway sandbox
        // so their saves can never rewrite the user's real config. Null in
        // production, where the shared %AppData% paths are used.
        private static string _configPathOverride;
        private static string _backupPathOverride;
        
        private AppConfig _config;
        private List<IWidgetWindow> _widgets = new List<IWidgetWindow>();
        private IslandWindow _island;
        private FullscreenWatcher _fullscreenWatcher;

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
        public IReadOnlyList<IWidgetWindow> Widgets => _widgets.AsReadOnly();

        /// <summary>
        /// Redirects the singleton's config/backup paths before it is first
        /// created. Desktop self-checks use this to keep their saves (debounced
        /// ones included) out of the user's real config; production code must
        /// never call it.
        /// </summary>
        public static void OverrideConfigPathsForChecks(string configPath, string backupPath)
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    throw new InvalidOperationException(
                        "OverrideConfigPathsForChecks must run before the first WidgetManager.Instance access");
                }
                _configPathOverride = configPath;
                _backupPathOverride = backupPath;
            }
        }

        private WidgetManager()
        {
            _config = _configPathOverride != null
                ? AppConfig.Load(_configPathOverride, _backupPathOverride)
                : AppConfig.Load();
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
            // One window per config entry of a known kind; unknown kinds stay
            // in the config untouched but get no window.
            foreach (var data in _config.Widgets)
            {
                CreateWidgetInternal(data);
            }

            // The island is the shared entry point that launches widget panels
            _island = new IslandWindow();
            _island.WidgetActivated += OnWidgetActivated;
            _island.WidgetMenuRequested += OnWidgetMenuRequested;
            RefreshIsland();
            _island.Show();

            // Keep the always-on-top island out of the way of fullscreen games/videos.
            _fullscreenWatcher = new FullscreenWatcher(avoid => _island?.SetTopmost(!avoid));
            _fullscreenWatcher.Start();

            // Reopen the panels that were still open when the app last closed.
            // No activation: starting up must not steal focus from other apps.
            foreach (var data in StartupPanels.ToRestore(_config.Widgets))
            {
                var widget = _widgets.FirstOrDefault(w => w.WidgetId == data.Id);
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
            _island?.SetWidgets(_config.Widgets);
        }

        /// <summary>
        /// Opens the clicked widget's panel centered below the island.
        /// </summary>
        private void OnWidgetActivated(string widgetId)
        {
            var widget = _widgets.FirstOrDefault(w => w.WidgetId == widgetId);
            if (widget == null || _island == null) return;

            if (widget.IsPanelOpen)
            {
                widget.HidePanel();
                return;
            }

            OpenPanel(widget);
        }

        /// <summary>
        /// Opens a widget's menu (rename / colour / grid / size / delete) when
        /// its island tile is right-clicked, keeping the island expanded while
        /// the menu is up.
        /// </summary>
        private void OnWidgetMenuRequested(string widgetId)
        {
            var widget = _widgets.FirstOrDefault(w => w.WidgetId == widgetId);
            if (widget == null || _island == null) return;

            _island.PauseAutoCollapse();
            widget.ShowWidgetMenu(() => _island.ResumeAutoCollapse());
        }

        /// <summary>Opens a widget's panel; defaults to centered below the island when unplaced.</summary>
        private void OpenPanel(IWidgetWindow widget, bool activate = true)
        {
            if (widget == null || _island == null) return;
            var (panelWidth, _) = widget.GetPanelSize();
            double left = _island.CenterX - panelWidth / 2; // clamped per-monitor inside ShowPanel
            widget.ShowPanel(left, _island.PanelTop, activate);
        }

        /// <summary>Toggles a widget's panel - shared by the widget menu.</summary>
        public void ToggleWidgetPanel(IWidgetWindow widget)
        {
            if (widget == null) return;
            if (widget.IsPanelOpen)
            {
                widget.HidePanel();
                return;
            }
            OpenPanel(widget);
        }

        /// <summary>
        /// New widgets cycle blue -&gt; red -&gt; green -&gt; purple -&gt; orange, the
        /// look the old 12-colour picker produced. The hexes are read from
        /// <see cref="UiTokens.FolderPalette"/> (pinned to the folder palette by
        /// tests/MenuRenderCheck) so they stay defined in one place.
        /// </summary>
        private static readonly string[] CreationColors =
        {
            UiTokens.FolderPalette[6], // blue
            UiTokens.FolderPalette[0], // red
            UiTokens.FolderPalette[4], // green
            UiTokens.FolderPalette[7], // purple
            UiTokens.FolderPalette[1]  // orange
        };

        /// <summary>
        /// Creates a widget of the given kind with the default name, palette
        /// colour and staggered position - shared by the island add menu and
        /// the tray new-widget submenu.
        /// </summary>
        public IWidgetWindow CreateWidget(string kindId)
        {
            var kind = WidgetKindRegistry.Find(kindId);
            if (kind == null) return null;

            var data = kind.CreateDefault();
            int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            int screenHeight = (int)SystemParameters.PrimaryScreenHeight;
            int widgetCount = _widgets.Count;

            data.PosX = screenWidth / 2 - 50 + (widgetCount % 5) * 30;
            data.PosY = screenHeight / 2 - 50 + (widgetCount % 5) * 30;
            data.Color = CreationColors[widgetCount % CreationColors.Length];
            data.Name = Localization.Get(kind.NameKey);
            data.IsPanelPinned = true; // opens pinned so it survives while being filled

            if (data is FolderData folder) folder.GridColumns = _config.DefaultGridColumns;

            // The one-time default size lands before the first save, so a new
            // widget opens at its migrated size and keeps it afterwards.
            PanelSizeMigration.FillMissingSizes(data, _config.ItemScale, _config.Language, DateTime.Today);

            _config.AddWidget(data);
            var widget = CreateWidgetInternal(data);
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

        /// <summary>Pushes the persisted island settings (collapse delay + position) to the island.</summary>
        public void ApplyIslandSettings()
        {
            _island?.ApplySettings();
        }

        /// <summary>Rebuilds the island tiles after the widget icon style changed.</summary>
        public void ApplyIconStyle()
        {
            _island?.RefreshIconStyle();
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
        /// Creates the window for an existing config entry and wires it to the
        /// manager. Unknown kinds have no window: they stay config-only.
        /// </summary>
        private IWidgetWindow CreateWidgetInternal(WidgetData data)
        {
            var widget = CreateWindow(data);
            if (widget == null) return null;

            widget.OnDeleted += (w) =>
            {
                _widgets.Remove(w);
                _config.RemoveWidget(w.WidgetId);
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
        /// The window class for a data entry, chosen through the kind registry
        /// so unknown kinds are skipped without touching their data.
        /// </summary>
        private static IWidgetWindow CreateWindow(WidgetData data)
        {
            if (data == null || WidgetKindRegistry.Find(data.Kind) == null) return null;

            if (data is FolderData folder) return new FolderWidget(folder);
            if (data is TodoData todo) return new TodoWidget(todo);
            return null;
        }

        /// <summary>
        /// Removes a widget by ID
        /// </summary>
        public void RemoveWidget(string id)
        {
            var widget = _widgets.FirstOrDefault(w => w.WidgetId == id);
            if (widget != null)
            {
                _widgets.Remove(widget);
                if (widget is Window window) window.Close();
                _config.RemoveWidget(id);
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
            _fullscreenWatcher?.Dispose();
            _fullscreenWatcher = null;
            _trimTimer.Stop();

            // Ensure config is saved before exit
            _config.Save();
            
            foreach (var widget in _widgets.ToList())
            {
                if (widget is Window window) window.Close();
            }
            _widgets.Clear();
        }
    }
}


