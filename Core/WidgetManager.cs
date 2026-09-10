using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kobold.Core;
using Kobold.Controls;

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
            RefreshIsland();
            _island.Show();
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

        /// <summary>Pushes the persisted island settings (collapse delay + position) to the island.</summary>
        public void ApplyIslandSettings()
        {
            _island?.ApplySettings();
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
        /// Shuts down all widgets
        /// </summary>
        public void Shutdown()
        {
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


