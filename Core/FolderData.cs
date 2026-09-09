using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FoldRa.Core
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
        /// False if file was physically moved to FoldRa storage
        /// </summary>
        public bool IsReference { get; set; }

        public WidgetItem()
        {
            Name = "";
            Path = "";
            IsReference = false;
        }

        public WidgetItem(string path, bool isReference = false)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            IsReference = isReference;
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
        public ObservableCollection<WidgetItem> Items { get; set; }

        public FolderData()
        {
            Id = Guid.NewGuid().ToString();
            Name = "New Folder";
            Color = "#3B82F6";
            PosX = 100;
            PosY = 100;
            IsExpanded = false;
            GridColumns = 3; // Default 3 columns
            IsLocked = false; // Unlocked by default
            IsPanelPinned = false; // Not pinned by default
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


