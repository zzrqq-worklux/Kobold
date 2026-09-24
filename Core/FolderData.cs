using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Kobold.Core
{
    /// <summary>
    /// Represents an item (file/folder shortcut) inside a folder widget
    /// </summary>
    public class WidgetItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        
        /// <summary>
        /// True if this is just a reference (file not moved to storage)
        /// False if file was physically moved to Kobold storage
        /// </summary>
        public bool IsReference { get; set; }

        /// <summary>
        /// Path the file was stored from (physical store only). Null for
        /// references and for legacy items stored before this field existed -
        /// those fall back to the desktop on eject.
        /// </summary>
        public string OriginalPath { get; set; }

        public WidgetItem()
        {
            Name = "";
            Path = "";
            IsReference = false;
            OriginalPath = null;
        }

        public WidgetItem(string path, bool isReference = false)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            IsReference = isReference;
            OriginalPath = null;
        }
    }

    /// <summary>
    /// Represents the data for a folder widget
    /// </summary>
    public class FolderData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }
        public int PosX { get; set; }
        public int PosY { get; set; }
        public bool IsExpanded { get; set; }
        public int GridColumns { get; set; } // Default 3, user can change
        public bool IsLocked { get; set; } // Widget lock state
        public bool IsPanelPinned { get; set; } // Keep panel always open
        public double? PanelX { get; set; } // Remembered panel top-left (null = never dragged)
        public double? PanelY { get; set; }

        /// <summary>
        /// DPI scale the panel position was captured at (null = legacy save,
        /// interpreted with the current window DPI).
        /// </summary>
        public double? PanelDpiScale { get; set; }

        /// <summary>
        /// Remembered browse chain (folder path per browsed level, root first).
        /// Empty = the panel shows the widget's own items.
        /// </summary>
        public List<string> BrowseStack { get; set; }

        public ObservableCollection<WidgetItem> Items { get; set; }

        public FolderData()
        {
            Id = Guid.NewGuid().ToString();
            Name = Localization.Get("UI_DefaultFolderName");
            Color = UiTokens.DefaultFolderColor;
            PosX = 100;
            PosY = 100;
            IsExpanded = false;
            GridColumns = 3; // Default 3 columns
            IsLocked = false; // Unlocked by default
            IsPanelPinned = false; // Not pinned by default
            BrowseStack = new List<string>();
            Items = new ObservableCollection<WidgetItem>();
        }

        /// <summary>
        /// Creates a new folder with specified name and color
        /// </summary>
        public static FolderData Create(string name, string color, int posX = 100, int posY = 100, int gridColumns = 3)
        {
            return new FolderData
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Color = color,
                PosX = posX,
                PosY = posY,
                IsExpanded = false,
                GridColumns = gridColumns,
                Items = new ObservableCollection<WidgetItem>()
            };
        }
    }
}


