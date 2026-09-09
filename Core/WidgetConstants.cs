namespace FoldRa.Core
{
    /// <summary>
    /// Centralized widget constants - Single source of truth for all size/layout values.
    /// SOLID: Single Responsibility - one place for all constants.
    /// </summary>
    public static class WidgetConstants
    {
        #region Widget Dimensions
        
        /// <summary>Widget icon area width</summary>
        public const int WIDGET_WIDTH = 96;
        
        /// <summary>Widget icon area height</summary>
        public const int WIDGET_HEIGHT = 110;
        
        /// <summary>Space between widget icon and panel</summary>
        public const int ICON_SPACING = 8;
        
        /// <summary>Default panel left position (WIDGET_WIDTH + ICON_SPACING)</summary>
        public const int DEFAULT_PANEL_LEFT = 104;
        
        #endregion
        
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


