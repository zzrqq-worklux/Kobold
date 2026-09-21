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

        /// <summary>
        /// ms the cursor must rest on the island before it expands. Without it a
        /// cursor merely crossing the top of the screen (where browser tabs live)
        /// would expand the island underneath the next click.
        /// </summary>
        public const int ISLAND_EXPAND_DELAY_MS = 200;

        /// <summary>ms the scroll hint stays up after the last interaction before it fades away.</summary>
        public const int ISLAND_SCROLLBAR_IDLE_MS = 1500;

        /// <summary>ms of the scroll hint's fade in/out animation.</summary>
        public const int ISLAND_SCROLLBAR_FADE_MS = 150;

        #endregion

        #region Folder Browse

        /// <summary>Max entries a browse listing returns (the rest is reported as a footer hint)</summary>
        public const int MAX_BROWSE_ENTRIES = 200;

        /// <summary>Max real shell icons loaded for one browse listing (the rest fall back)</summary>
        public const int MAX_BROWSE_ICON_LOADS = 60;

        /// <summary>Height of the "more items" footer row when a listing is truncated</summary>
        public const int FOOTER_HEIGHT = 20;

        /// <summary>Panel height cap as a share of the work area</summary>
        public const double PANEL_MAX_HEIGHT_RATIO = 0.6;

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
        
        /// <summary>Default folder color (blue) - same token as UiTokens.DefaultFolderColor</summary>
        public const string DEFAULT_FOLDER_COLOR = UiTokens.DefaultFolderColor;
        
        /// <summary>Panel corner radius</summary>
        public const int PANEL_CORNER_RADIUS = (int)UiTokens.RadiusPanel;
        
        /// <summary>Item corner radius</summary>
        public const int ITEM_CORNER_RADIUS = (int)UiTokens.RadiusControl;
        
        #endregion
    }
}


