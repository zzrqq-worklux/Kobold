namespace Kobold.Core
{
    /// <summary>
    /// Island layout math. The island is three parts: a fixed desktop entry, a
    /// scrollable middle that holds the widget tiles, and the fixed add/settings
    /// entries. Only the middle section is capped (see
    /// <see cref="MaxScreenFraction"/>) and scrolled - the fixed parts always
    /// stay in view. Kept free of WPF types so it can be checked directly.
    /// </summary>
    public static class IslandLayout
    {
        /// <summary>Slack that keeps the row from sitting flush with the capsule edges (12px each side).</summary>
        public const double Padding = 24;

        /// <summary>Divider entry: the 1px line plus its 6px margins.</summary>
        public const double DividerWidth = 13;

        /// <summary>Width of the settings entry.</summary>
        public const double SettingsWidth = 44;

        /// <summary>Width of the add-widget entry (tile plus the gap after it).</summary>
        public const double AddWidth = 52;

        /// <summary>One wheel notch scrolls one tile plus its gap.</summary>
        public const double ScrollStep = WidgetConstants.ISLAND_TILE_SIZE + WidgetConstants.ISLAND_TILE_GAP;

        /// <summary>Desktop entry: its tile, the gap after it, and the divider that follows.</summary>
        public const double DesktopEntryWidth = WidgetConstants.ISLAND_TILE_SIZE + WidgetConstants.ISLAND_TILE_GAP + DividerWidth;

        /// <summary>
        /// Share of the screen width the middle (widget) section may occupy.
        /// Deliberately small: the island sits over the top of the display,
        /// where browser tabs and other window controls live, and a wide tile
        /// row makes a stray click hit a widget.
        /// </summary>
        public const double MaxScreenFraction = 0.25;

        /// <summary>Width of the widget tiles - one tile plus its gap each.</summary>
        public static double MiddleContentWidth(int widgetCount)
        {
            if (widgetCount < 0) widgetCount = 0;
            return widgetCount * ScrollStep;
        }

        /// <summary>
        /// Widest the middle section may become on a screen of the given width:
        /// a fraction of the screen, with one tile as the floor so even a tiny
        /// screen still shows something.
        /// </summary>
        public static double MiddleMaxWidth(double screenWidth)
        {
            double fraction = screenWidth * MaxScreenFraction;
            return fraction > ScrollStep ? fraction : ScrollStep;
        }

        /// <summary>Visible width of the middle section: its content, capped.</summary>
        public static double MiddleViewWidth(int widgetCount, double screenWidth)
        {
            double content = MiddleContentWidth(widgetCount);
            double cap = MiddleMaxWidth(screenWidth);
            return content < cap ? content : cap;
        }

        /// <summary>
        /// Capsule width: padding + fixed desktop entry + the middle section +
        /// the trailing divider (only when widgets exist) + add + settings.
        /// </summary>
        public static double ContentWidth(int widgetCount, double screenWidth)
        {
            return Padding
                 + DesktopEntryWidth
                 + MiddleViewWidth(widgetCount, screenWidth)
                 + (widgetCount > 0 ? DividerWidth : 0)
                 + AddWidth
                 + SettingsWidth;
        }

        /// <summary>Keeps a scroll offset inside the middle - 0 to its scrollable width.</summary>
        public static double ClampOffset(double offset, double scrollableWidth)
        {
            if (scrollableWidth <= 0 || offset < 0) return 0;
            return offset > scrollableWidth ? scrollableWidth : offset;
        }

        /// <summary>
        /// Offset after rolling the wheel by whole tiles. Negative notches
        /// scroll towards the start of the tiles, positive ones towards their end.
        /// </summary>
        public static double ScrollTarget(double currentOffset, int notches, double scrollableWidth)
        {
            return ClampOffset(currentOffset + notches * ScrollStep, scrollableWidth);
        }
    }
}
