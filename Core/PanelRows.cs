namespace Kobold.Core
{
    /// <summary>
    /// Row maths for the expanded panel: how many rows a listing needs, how
    /// many of them the panel shows before it scrolls, and whether it scrolls.
    /// The cap is a per-widget setting, so one panel can stay compact while
    /// another shows more. Pure so the rules are testable without a window.
    /// </summary>
    public static class PanelRows
    {
        /// <summary>Rows a panel shows when its own setting is missing or invalid.</summary>
        public const int DefaultMaxRows = 3;

        /// <summary>Smallest selectable cap (menu range).</summary>
        public const int MinAllowedRows = 1;

        /// <summary>Largest selectable cap (menu range).</summary>
        public const int MaxAllowedRows = 6;

        /// <summary>Rows the content occupies: one per column-count chunk, at least one (empty state).</summary>
        public static int RowsFor(int itemCount, int columns)
        {
            if (columns < 1) columns = 1;
            if (itemCount < 1) itemCount = 1;
            return (itemCount + columns - 1) / columns;
        }

        /// <summary>Rows the panel grows to: the content, capped by the widget's own setting.</summary>
        public static int VisibleRows(int itemCount, int columns, int maxRows)
        {
            int cap = Cap(maxRows);
            int rows = RowsFor(itemCount, columns);
            return rows < cap ? rows : cap;
        }

        /// <summary>True when the content needs more rows than the cap allows.</summary>
        public static bool NeedsScroll(int itemCount, int columns, int maxRows)
        {
            return RowsFor(itemCount, columns) > Cap(maxRows);
        }

        private static int Cap(int maxRows)
        {
            if (maxRows < MinAllowedRows) return DefaultMaxRows; // unset (0) or garbage
            return maxRows > MaxAllowedRows ? MaxAllowedRows : maxRows;
        }
    }
}
