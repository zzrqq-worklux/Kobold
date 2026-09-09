using System;
using System.Collections.Generic;
using System.Linq;
using FoldRa.Core;
using FoldRa.Controls;

namespace FoldRa.Core
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
        }

        /// <summary>
        /// Creates a new widget and adds it to config
        /// </summary>
        public FolderWidget CreateWidget(string name, string color, int posX, int posY, int gridColumns = 3)
        {
            var folderData = _config.AddFolder(name, color, posX, posY, gridColumns);
            return CreateWidgetInternal(folderData);
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
            };
            
            widget.OnDataChanged += () =>
            {
                SaveConfig();
            };
            
            _widgets.Add(widget);
            widget.Show();
            
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
            }
        }

        /// <summary>
        /// Shows all widgets
        /// </summary>
        public void ShowAll()
        {
            foreach (var widget in _widgets)
            {
                widget.Show();
                widget.Activate();
            }
        }

        /// <summary>
        /// Hides all widgets
        /// </summary>
        public void HideAll()
        {
            foreach (var widget in _widgets)
            {
                widget.Hide();
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


