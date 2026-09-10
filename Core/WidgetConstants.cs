namespace Kobold.Core
{
    /// <summary>
    /// Centralized widget constants - Single source of truth for all size/layout values.
    /// SOLID: Single Responsibility - one place for all constants.
    /// </summary>
    public static class WidgetConstants
    {
        #region Item Dimensions
        
        /// <summary>Base item width at scale 1.0</summary>
        public const int BASE_ITEM_WIDTH = 72;
        
        /// <summary>Base item height at scale 1.0</summary>
        public const int BASE_ITEM_HEIGHT = 85;
        
        /// <summary>Margin between items</summary>
        public const int ITEM_MARGIN = 4;
        
        #endregion
        
        #region Panel Layout
        
        /// <summary>Panel header height</summary>
        public const int HEADER_HEIGHT = 40;
        
        /// <summary>Panel padding</summary>
        public const int PADDING = 16;
        
        /// <summary>Default grid columns</summary>
        public const int DEFAULT_GRID_COLUMNS = 3;
        
        #endregion
        
        #region Island (Top-Center Launcher)

        /// <summary>Collapsed pill width</summary>
        public const int ISLAND_PILL_WIDTH = 120;

        /// <summary>Collapsed pill height</summary>
        public const int ISLAND_PILL_HEIGHT = 8;

        /// <summary>Window width while collapsed - transparent hot zone for hover</summary>
        public const int ISLAND_HOVER_WIDTH = 300;

        /// <summary>Window height while collapsed - transparent hot zone for hover</summary>
        public const int ISLAND_HOVER_HEIGHT = 10;

        /// <summary>Expanded capsule height</summary>
        public const int ISLAND_EXPANDED_HEIGHT = 56;

        /// <summary>Size of a single widget tile in the expanded island</summary>
        public const int ISLAND_TILE_SIZE = 44;

        /// <summary>Gap between widget tiles</summary>
        public const int ISLAND_TILE_GAP = 8;

        /// <summary>Vertical gap between the island and an opened panel</summary>
        public const int ISLAND_PANEL_GAP = 8;

        /// <summary>Default ms after the mouse leaves the island before it collapses (configurable in settings)</summary>
        public const int ISLAND_LEAVE_DELAY_MS = 1000;

        #endregion

        #region Drag-Drop
        
        /// <summary>Minimum distance to start drag operation</summary>
        public const double DRAG_THRESHOLD = 10;
        
        #endregion
        
        #region Animation
        
        /// <summary>Standard animation duration in milliseconds</summary>
        public const int ANIMATION_DURATION_MS = 200;
        
        /// <summary>Hover animation duration in milliseconds</summary>
        public const int HOVER_DURATION_MS = 100;
        
        #endregion
        
        #region Visual
        
        /// <summary>Default folder color (blue)</summary>
        public const string DEFAULT_FOLDER_COLOR = "#3B82F6";
        
        /// <summary>Panel corner radius</summary>
        public const int PANEL_CORNER_RADIUS = 16;
        
        /// <summary>Item border corner radius</summary>
        public const int ITEM_CORNER_RADIUS = 10;
        
        #endregion
    }
}


