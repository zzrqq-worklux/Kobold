namespace Kobold.Core
{
    /// <summary>
    /// Expanded-island layout math: how wide the launcher becomes for a given
    /// number of widgets, and how far its widget row scrolls once it no longer
    /// fits on screen. Kept free of WPF types so it can be checked directly.
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
        /// Share of the screen width the expanded island may occupy. Deliberately
        /// small: the island sits over the top of the display, where browser tabs
        /// and other window controls live, and a wide row makes a stray click hit
        /// a widget tile. The rest of the row scrolls.
        /// </summary>
        public const double MaxScreenFraction = 0.25;

        /// <summary>
        /// Width of the widget row itself - what WPF measures and scrolls. The
        /// trailing divider before the add/settings entries only appears once
        /// at least one widget exists.
        /// </summary>
        public static double RowWidth(int widgetCount)
        {
            if (widgetCount < 0) widgetCount = 0;

            return DesktopEntryWidth
                 + widgetCount * ScrollStep
                 + (widgetCount > 0 ? DividerWidth : 0)
                 + AddWidth
                 + SettingsWidth;
        }

        /// <summary>
        /// Width of the capsule for the given widget count: the row plus the
        /// padding that keeps it off the rounded edges.
        /// </summary>
        public static double ContentWidth(int widgetCount)
        {
            return RowWidth(widgetCount) + Padding;
        }

        /// <summary>
        /// Widest the island may become on a screen of the given width: a
        /// fraction of the screen (see <see cref="MaxScreenFraction"/>), but
        /// never narrower than the fixed entries it always shows.
        /// </summary>
        public static double MaxWidth(double screenWidth)
        {
            double fraction = screenWidth * MaxScreenFraction;
            double fixedEntries = ContentWidth(0);
            return fraction > fixedEntries ? fraction : fixedEntries;
        }

        /// <summary>Keeps a scroll offset inside the row - 0 to its scrollable width.</summary>
        public static double ClampOffset(double offset, double scrollableWidth)
        {
            if (scrollableWidth <= 0 || offset < 0) return 0;
            return offset > scrollableWidth ? scrollableWidth : offset;
        }

        /// <summary>
        /// Offset after rolling the wheel by whole tiles. Negative notches
        /// scroll towards the start of the row, positive ones towards its end.
        /// </summary>
        public static double ScrollTarget(double currentOffset, int notches, double scrollableWidth)
        {
            return ClampOffset(currentOffset + notches * ScrollStep, scrollableWidth);
        }
    }
}
