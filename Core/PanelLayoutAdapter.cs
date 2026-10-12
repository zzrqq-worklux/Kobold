using System;

namespace Kobold.Core
{
    /// <summary>
    /// Result of fitting the folder grid into a freely resized panel: the
    /// column count, the icon scale the cells render at, and whether the
    /// listing needs scrolling.
    /// </summary>
    public struct FolderLayout
    {
        /// <summary>Columns the grid holds (at least two).</summary>
        public int Columns;

        /// <summary>User-set icon scale (MIN_ITEM_SCALE..MAX_ITEM_SCALE, constant while resizing).</summary>
        public double Scale;

        /// <summary>Rows the panel fits at the current cell height (at least one).</summary>
        public int VisibleRows;

        /// <summary>True when the listing has more rows than the panel shows.</summary>
        public bool Scrolls;

        /// <summary>Rendered cell width (base width x scale, rounded up).</summary>
        public double CellWidth;

        /// <summary>Rendered cell height (base height x scale, rounded up).</summary>
        public double CellHeight;
    }

    /// <summary>
    /// Result of splitting a freely resized todo panel's height between the
    /// month calendar and the day list in the lower half.
    /// </summary>
    public struct TodoLayout
    {
        /// <summary>Calendar cell height (CELL_MIN..CELL_MAX).</summary>
        public double CellHeight;

        /// <summary>True when the second text line of a day cell fits and has data.</summary>
        public bool ShowDayLabels;

        /// <summary>Day-list rows the panel gives the list (0..LIST_MAX_ROWS).</summary>
        public int ListRows;

        /// <summary>True when the day list has more natural rows than it shows.</summary>
        public bool ListScrolls;
    }

    /// <summary>
    /// Vertical geometry of the day list and the add row: the list starts below the
    /// day header (fixed chrome plus the calendar) and the add row either follows
    /// the last shown row or pins to the bottom edge when the space runs out.
    /// </summary>
    public struct TodoListLayout
    {
        /// <summary>DIP from the content top to the list viewport top.</summary>
        public double ListTop;
        /// <summary>List viewport height (never beyond the rows it shows).</summary>
        public double Viewport;
        /// <summary>True when the list's full content is taller than the viewport.</summary>
        public bool Scrolls;
        /// <summary>True when the add row is pinned to the bottom edge.</summary>
        public bool Pinned;
    }

    /// <summary>
    /// Pure layout maths behind the freely resizable panels: how the folder
    /// grid fits a given content area at the user-set icon scale (the width
    /// adds columns and spacing, never bigger icons), how the todo panel
    /// splits its height between the calendar and the day list, and how the
    /// old per-widget size settings migrate to the new fixed sizes. The
    /// constants here are the single source the renderers consume, so no
    /// window is needed to test the rules and none of them can drift apart.
    /// </summary>
    public static class PanelLayoutAdapter
    {
        // Folder grid: every cell renders at the user-set icon scale (clamped
        // to MIN_ITEM_SCALE..MAX_ITEM_SCALE); the panel width only decides how
        // many columns fit, and extra width becomes the spacing the stretched
        // UniformGrid adds. The base cell is one folder tile plus its margin
        // (BASE_ITEM_WIDTH/HEIGHT + ITEM_MARGIN).
        /// <summary>Smallest icon scale the user can pick for the folder grid.</summary>
        public const double MIN_ITEM_SCALE = 0.9;

        /// <summary>Largest icon scale the user can pick for the folder grid.</summary>
        public const double MAX_ITEM_SCALE = 1.5;

        /// <summary>Fewest columns the folder grid keeps at any width.</summary>
        public const int MIN_COLUMNS = 2;

        /// <summary>Base cell width at scale 1 (folder tile + margin) = 76.</summary>
        public const double BASE_CELL_W = WidgetConstants.BASE_ITEM_WIDTH + WidgetConstants.ITEM_MARGIN;

        /// <summary>Base cell height at scale 1 (folder tile + margin) = 89.</summary>
        public const double BASE_CELL_H = WidgetConstants.BASE_ITEM_HEIGHT + WidgetConstants.ITEM_MARGIN;

        // The panel shell's border, 2 DIP across both sides; shared by the
        // old-size migration and the dynamic folder minimum.
        private const double CHROME_BORDER_W = 2;

        // Todo height split: the list starts at LIST_ROW_H per natural row
        // (capped at LIST_MAX_ROWS); the calendar takes the rest per week,
        // clamped to CELL_MIN..CELL_MAX. The fixed block is the chrome around
        // the calendar (see the individual parts below; sum = 133).
        /// <summary>Shortest calendar cell (below this the month cannot shrink further).</summary>
        public const double CELL_MIN = 22;

        /// <summary>Tallest calendar cell (the list takes the rest).</summary>
        public const double CELL_MAX = 36;

        /// <summary>Calendar cell height that still fits the second text line.</summary>
        public const double LABEL_MIN_CELL = 32;

        /// <summary>Height of one day-list row.</summary>
        public const double LIST_ROW_H = 30;

        /// <summary>Most day-list rows the panel shows at once.</summary>
        public const int LIST_MAX_ROWS = 6;

        /// <summary>Root margin above the calendar block = 8.</summary>
        public const double FIXED_ROOT_MARGIN_TOP = 8;

        /// <summary>Root margin below the add row = 10.</summary>
        public const double FIXED_ROOT_MARGIN_BOTTOM = 10;

        /// <summary>Root margin above and below the calendar block = 18 (8 + 10).</summary>
        public const double FIXED_ROOT_MARGIN = FIXED_ROOT_MARGIN_TOP + FIXED_ROOT_MARGIN_BOTTOM;

        /// <summary>Month navigation bar = 28.</summary>
        public const double FIXED_MONTH_NAV = 28;

        /// <summary>Weekday header row = 18.</summary>
        public const double FIXED_WEEKDAY_ROW = 18;

        /// <summary>Separator around the weekday row = 9 (1 + 4 + 4).</summary>
        public const double FIXED_SEPARATOR = 9;

        /// <summary>Day-of-week header = 24.</summary>
        public const double FIXED_DAY_HEADER = 24;

        /// <summary>Add-item row at the bottom = 36 (30 + 6).</summary>
        public const double FIXED_ADD_ROW = 36;

        /// <summary>Fixed chrome around calendar and list = 133.</summary>
        public const double FIXED_CONTENT_H = FIXED_ROOT_MARGIN + FIXED_MONTH_NAV + FIXED_WEEKDAY_ROW
                                              + FIXED_SEPARATOR + FIXED_DAY_HEADER + FIXED_ADD_ROW;

        /// <summary>Calendar cell height of the old fixed layout (migration source).</summary>
        public const double MIGRATION_CELL_H = 28;

        /// <summary>Width of every todo panel (the calendar needs seven even columns).</summary>
        public const double DEFAULT_WIDTH = 342;

        /// <summary>
        /// Fits the folder grid into the content area at the user's icon
        /// scale: the padding comes off first, the scale is clamped to
        /// MIN_ITEM_SCALE..MAX_ITEM_SCALE, the cell size follows from it, the
        /// grid takes as many columns as fit (never fewer than MIN_COLUMNS)
        /// and the visible rows follow from the fitted cell height. Widening
        /// the panel never grows the icons: it adds columns, or spacing
        /// through the grid's stretch. Scrolling is the natural row count
        /// against the visible rows - the folder panel has no per-widget row
        /// cap.
        /// </summary>
        public static FolderLayout AdaptFolder(double contentWidth, double contentHeight, int itemCount, double itemScale)
        {
            double usableW = contentWidth - WidgetConstants.PADDING;
            double usableH = contentHeight - WidgetConstants.PADDING;

            double scale = Clamp(itemScale, MIN_ITEM_SCALE, MAX_ITEM_SCALE);
            double cellW = Math.Ceiling(BASE_CELL_W * scale);
            double cellH = Math.Ceiling(BASE_CELL_H * scale);

            var layout = new FolderLayout
            {
                Columns = Math.Max(MIN_COLUMNS, (int)Math.Floor(usableW / cellW)),
                Scale = scale,
                CellWidth = cellW,
                CellHeight = cellH
            };
            layout.VisibleRows = Math.Max(1, (int)Math.Floor(usableH / layout.CellHeight));
            layout.Scrolls = PanelRows.RowsFor(itemCount, layout.Columns) > layout.VisibleRows;
            return layout;
        }

        /// <summary>
        /// The smallest panel a folder keeps at this icon scale: two columns
        /// wide (padding and the chrome border included) and one cell high
        /// plus the header.
        /// </summary>
        public static (double w, double h) MinFolderSize(double itemScale)
        {
            double scale = Clamp(itemScale, MIN_ITEM_SCALE, MAX_ITEM_SCALE);
            double cellW = Math.Ceiling(BASE_CELL_W * scale);
            double cellH = Math.Ceiling(BASE_CELL_H * scale);
            return (MIN_COLUMNS * cellW + WidgetConstants.PADDING + CHROME_BORDER_W,
                    WidgetConstants.HEADER_HEIGHT + cellH + WidgetConstants.PADDING);
        }

        /// <summary>
        /// Splits the todo panel's height: the day list gets its natural rows
        /// first (zero for an empty day, capped at LIST_MAX_ROWS), the calendar
        /// takes what is left per week clamped to CELL_MIN..CELL_MAX. When that
        /// would starve the calendar below CELL_MIN, the floor wins and the
        /// list shrinks to the rows that still fit. Day labels need available
        /// data and a cell at least LABEL_MIN_CELL high.
        /// </summary>
        public static TodoLayout AdaptTodo(double contentHeight, int weekCount, int naturalListRows, bool labelsAvailable)
        {
            if (weekCount < 1) weekCount = 1;
            int natural = naturalListRows < 0 ? 0 : naturalListRows;
            int listRows = Math.Min(natural, LIST_MAX_ROWS);

            double cal = contentHeight - FIXED_CONTENT_H - listRows * LIST_ROW_H;
            double cellH = Clamp(cal / weekCount, CELL_MIN, CELL_MAX);

            if (cal / weekCount < CELL_MIN)
            {
                cellH = CELL_MIN;
                double listSpace = contentHeight - FIXED_CONTENT_H - CELL_MIN * weekCount;
                listRows = ClampRows((int)Math.Floor(listSpace / LIST_ROW_H));
            }

            return new TodoLayout
            {
                CellHeight = cellH,
                ListRows = listRows,
                ListScrolls = natural > listRows,
                ShowDayLabels = labelsAvailable && cellH >= LABEL_MIN_CELL
            };
        }

        /// <summary>
        /// Fits the day list under the add row's block: the viewport takes what is left
        /// below the calendar, capped by the natural rows; when that is not enough the
        /// add row pins to the bottom edge (viewport shrinks, the list scrolls).
        /// </summary>
        public static TodoListLayout FitTodoList(
            double contentHeight, double calendarHeight, int naturalListRows, double addBlockHeight)
        {
            double listTop = FIXED_ROOT_MARGIN_TOP + FIXED_MONTH_NAV + FIXED_WEEKDAY_ROW
                             + calendarHeight + FIXED_SEPARATOR + FIXED_DAY_HEADER;
            int natural = naturalListRows < 0 ? 0 : naturalListRows;
            double content = natural * LIST_ROW_H;
            double needed = Math.Min(natural, LIST_MAX_ROWS) * LIST_ROW_H;
            double bottomEdge = contentHeight - FIXED_ROOT_MARGIN_BOTTOM;

            double viewport = bottomEdge - listTop - addBlockHeight;
            if (viewport < 0) viewport = 0;
            bool pinned = viewport < needed;
            if (!pinned) viewport = needed;

            return new TodoListLayout
            {
                ListTop = listTop,
                Viewport = viewport,
                Scrolls = content > viewport,
                Pinned = pinned
            };
        }

        /// <summary>
        /// One-shot migration of the old folder size settings: the stored grid
        /// columns and item scale are replayed at their old cell sizes (with
        /// padding, border and, when the listing was truncated, the footer).
        /// </summary>
        public static (double w, double h) MigrateFolderSize(int gridColumns, int maxPanelRows, int itemCount, double itemScale)
        {
            double itemW = Math.Ceiling(BASE_CELL_W * itemScale);
            double itemH = Math.Ceiling(BASE_CELL_H * itemScale);
            int rows = PanelRows.VisibleRows(itemCount, gridColumns, maxPanelRows);

            double w = gridColumns * itemW + WidgetConstants.PADDING + CHROME_BORDER_W;
            double h = WidgetConstants.HEADER_HEIGHT + rows * itemH + WidgetConstants.PADDING
                       + (PanelRows.NeedsScroll(itemCount, gridColumns, maxPanelRows)
                           ? WidgetConstants.FOOTER_HEIGHT
                           : 0);
            return (w, h);
        }

        /// <summary>
        /// One-shot migration of the old todo size settings: the old look used
        /// a 28-high calendar cell, the fixed chrome block and up to six list
        /// rows of 30.
        /// </summary>
        public static (double w, double h) DefaultTodoSize(int weekCount, int naturalListRows)
        {
            if (weekCount < 1) weekCount = 1;
            double h = WidgetConstants.HEADER_HEIGHT + FIXED_CONTENT_H
                       + weekCount * MIGRATION_CELL_H + ClampRows(naturalListRows) * LIST_ROW_H;
            return (DEFAULT_WIDTH, h);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }

        private static int ClampRows(int rows)
        {
            if (rows < 0) return 0;
            return rows > LIST_MAX_ROWS ? LIST_MAX_ROWS : rows;
        }
    }
}
