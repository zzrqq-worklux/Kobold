using System;
using System.Collections.Generic;
using Kobold.Core;

namespace Kobold.AdaptiveLayoutCheck
{
    /// <summary>
    /// Behaviour checks for PanelLayoutAdapter - the pure layout maths behind
    /// the freely resizable folder and todo panels: the folder grid at the
    /// user-set icon scale (constant cells, columns that grow with the width,
    /// visible rows, scrolling, the dynamic minimum panel), the todo height
    /// split between the calendar and the day list (cell 22..36, 0..6 list
    /// rows, the 32 day-label threshold) and the one-shot size migration from
    /// the old per-widget settings. Expected values are pinned by the
    /// approved spec.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/AdaptiveLayoutCheck
    /// </summary>
    internal static class Program
    {
        private const double Tolerance = 1e-9;
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            RunAll();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL ADAPTIVE-LAYOUT CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        private static bool Near(double actual, double expected)
        {
            return Math.Abs(actual - expected) <= Tolerance;
        }

        private static void RunAll()
        {
            FolderFixedScale();
            FolderColumnBoundaries();
            FolderVisibleRows();
            FolderColumnMonotonicity();
            FolderScrollSemantics();
            FolderMinimumSize();
            TodoHeightAllocation();
            TodoLabelThreshold();
            TodoListFitting();
            MigrationSizes();
            GridNavigationMatrix();
        }

        // The folder grid follows usable = content - PADDING; the icon scale
        // is the user's configured item scale clamped to 0.9..1.5, the cell
        // size follows from it and the columns grow with the usable width
        // (never fewer than two). Widening the panel adds columns/spacing, it
        // never changes the icons.
        private static void FolderFixedScale()
        {
            var atDefault = PanelLayoutAdapter.AdaptFolder(435, 300, 5, 1.1);
            Check(atDefault.Columns == 4, "435-wide folder at 1.1: four columns (419/84)");
            Check(Near(atDefault.Scale, 1.1), "435-wide folder at 1.1: scale stays the set 1.1");
            Check(atDefault.CellWidth == 84, "435-wide folder at 1.1: cell width 84");
            Check(atDefault.CellHeight == 98, "435-wide folder at 1.1: cell height 98");
            Check(atDefault.VisibleRows == 2, "435-wide folder at 1.1: two rows fit (284/98)");
            Check(!atDefault.Scrolls, "five items in four columns over two rows do not scroll");

            var floored = PanelLayoutAdapter.AdaptFolder(150, 300, 5, 0.5);
            Check(Near(floored.Scale, 0.9), "a 0.5 request clamps to the 0.9 floor");
            Check(floored.CellWidth == 69, "a 0.5 request: cell width 69 at the floor");
            Check(floored.CellHeight == 81, "a 0.5 request: cell height 81 at the floor");
            Check(floored.Columns == 2, "a 0.5 request on a 150-wide panel floors at two columns");
            Check(floored.VisibleRows == 3, "a 0.5 request: three rows fit (284/81)");
            Check(!floored.Scrolls, "five items in two columns over three rows do not scroll");

            var ceiling = PanelLayoutAdapter.AdaptFolder(500, 300, 5, 2.0);
            Check(Near(ceiling.Scale, 1.5), "a 2.0 request clamps to the 1.5 ceiling");
            Check(ceiling.CellWidth == 114, "a 2.0 request: cell width 114 at the ceiling");
            Check(ceiling.CellHeight == 134, "a 2.0 request: cell height 134 at the ceiling");
            Check(ceiling.Columns == 4, "a 2.0 request on a 500-wide panel: four columns (484/114)");
            Check(ceiling.VisibleRows == 2, "a 2.0 request: two rows fit (284/134)");
            Check(!ceiling.Scrolls, "five items in four columns over two rows do not scroll");

            var narrow = PanelLayoutAdapter.AdaptFolder(435, 300, 5, 1.1);
            var wide = PanelLayoutAdapter.AdaptFolder(478, 300, 5, 1.1);
            Check(Near(narrow.Scale, wide.Scale) && Near(wide.Scale, 1.1),
                "widening the panel keeps the icon scale at 1.1");
            Check(narrow.Columns == 4 && wide.Columns == 5,
                "widening 435 -> 478 adds the fifth column (462/84)");
        }

        // At 1.1 the fifth column starts at a panel width of 436 (usable 420
        // = 5 x 84); at 1.5 the third column starts at 358 (usable 342
        // = 3 x 114).
        private static void FolderColumnBoundaries()
        {
            Check(PanelLayoutAdapter.AdaptFolder(435, 300, 5, 1.1).Columns == 4,
                "at 1.1 a 435-wide panel holds four columns (usable 419)");
            Check(PanelLayoutAdapter.AdaptFolder(436, 300, 5, 1.1).Columns == 5,
                "at 1.1 a 436-wide panel starts the fifth column (usable 420)");

            Check(PanelLayoutAdapter.AdaptFolder(357, 300, 5, 1.5).Columns == 2,
                "at 1.5 a 357-wide panel holds two columns (usable 341)");
            Check(PanelLayoutAdapter.AdaptFolder(358, 300, 5, 1.5).Columns == 3,
                "at 1.5 a 358-wide panel starts the third column (usable 342)");
        }

        private static void FolderVisibleRows()
        {
            var shortPanel = PanelLayoutAdapter.AdaptFolder(150, 90, 1, 1.1);
            Check(shortPanel.VisibleRows == 1, "a panel too short for a row still shows one row");
        }

        private static void FolderColumnMonotonicity()
        {
            int previous = 0;
            bool nonDecreasing = true;
            bool atLeastTwo = true;
            for (int width = 100; width <= 500; width++)
            {
                var layout = PanelLayoutAdapter.AdaptFolder(width, 300, 5, 1.1);
                if (layout.Columns < previous) nonDecreasing = false;
                if (layout.Columns < 2) atLeastTwo = false;
                previous = layout.Columns;
            }
            Check(nonDecreasing, "columns never shrink as the folder widens (100..500)");
            Check(atLeastTwo, "columns stay at two or more across the width sweep");
        }

        // Scrolling compares the natural content rows against the visible ones;
        // the folder's retired 1..6 per-widget cap does not apply here (the
        // 14-item fixture proves the engine keeps seven visible rows).
        private static void FolderScrollSemantics()
        {
            var scrolling = PanelLayoutAdapter.AdaptFolder(186, 154, 5, 1.1);
            Check(scrolling.Columns == 2 && scrolling.VisibleRows == 1,
                "the scrolling fixture lands on two columns and one visible row");
            Check(scrolling.Scrolls, "five items over two columns and one visible row scroll");
            Check(scrolling.Scrolls == PanelRows.RowsFor(5, 2) > 1,
                "scrolling follows the PanelRows row count");

            var tall = PanelLayoutAdapter.AdaptFolder(150, 616, 14, 0.9);
            Check(tall.Columns == 2 && tall.VisibleRows == 7,
                "the tall fixture lands on two columns and seven visible rows (600/81)");
            Check(!tall.Scrolls, "14 items over two columns and seven visible rows do not scroll");
            Check(tall.Scrolls == PanelRows.RowsFor(14, 2) > tall.VisibleRows,
                "not scrolling follows the PanelRows row count without the 1..6 cap");
        }

        // MinFolderSize = two cell widths + padding + the 2 DIP chrome border
        // wide, one cell high + header + padding: 156x137 at 0.9 up to
        // 246x190 at 1.5. Every scale's minimum fits two columns and a row and
        // never overflows the width.
        private static void FolderMinimumSize()
        {
            var (w09, h09) = PanelLayoutAdapter.MinFolderSize(0.9);
            Check(Near(w09, 156) && Near(h09, 137), "MinFolderSize(0.9) is 156x137");
            var (w11, h11) = PanelLayoutAdapter.MinFolderSize(1.1);
            Check(Near(w11, 186) && Near(h11, 154), "MinFolderSize(1.1) is 186x154");
            var (w15, h15) = PanelLayoutAdapter.MinFolderSize(1.5);
            Check(Near(w15, 246) && Near(h15, 190), "MinFolderSize(1.5) is 246x190");

            var (wLow, hLow) = PanelLayoutAdapter.MinFolderSize(0.5);
            Check(Near(wLow, 156) && Near(hLow, 137),
                "MinFolderSize clamps a 0.5 request down to the 0.9 floor");
            var (wHigh, hHigh) = PanelLayoutAdapter.MinFolderSize(2.0);
            Check(Near(wHigh, 246) && Near(hHigh, 190),
                "MinFolderSize clamps a 2.0 request up to the 1.5 ceiling");

            double[] scales = { 0.9, 1.0, 1.1, 1.2, 1.3, 1.5 };
            foreach (double s in scales)
            {
                var (w, h) = PanelLayoutAdapter.MinFolderSize(s);
                var layout = PanelLayoutAdapter.AdaptFolder(w, h, 5, s);
                Check(layout.Columns >= 2 && layout.VisibleRows >= 1,
                    "at scale " + s + " the minimum panel holds two columns and a row");
                double rendered = layout.Columns * 76 * layout.Scale;
                Check(rendered <= w - 18 + 0.01,
                    "at scale " + s + " the minimum panel does not clip horizontally");
            }
        }

        // The todo panel gives the day list its natural rows first (capped at
        // six), then the calendar takes the rest, clamped to 22..36 per week.
        private static void TodoHeightAllocation()
        {
            var spacious = PanelLayoutAdapter.AdaptTodo(343, 5, 1, true);
            Check(Near(spacious.CellHeight, 36), "todo 343 high: cell height clamps to 36");
            Check(spacious.ShowDayLabels, "todo 343 high: day labels show");
            Check(spacious.ListRows == 1, "todo 343 high: one list row");
            Check(!spacious.ListScrolls, "todo 343 high: the one list row fits");

            var labelsHidden = PanelLayoutAdapter.AdaptTodo(303, 5, 1, true);
            Check(Near(labelsHidden.CellHeight, 28), "todo 303 high: cell height 28");
            Check(!labelsHidden.ShowDayLabels, "todo below the 32 threshold hides the day labels");

            var emptyDay = PanelLayoutAdapter.AdaptTodo(273, 5, 0, true);
            Check(emptyDay.ListRows == 0, "an empty day asks for no list rows");
            Check(Near(emptyDay.CellHeight, 28), "an empty day leaves 28 per calendar week");
            Check(!emptyDay.ListScrolls, "an empty day does not scroll");

            var floorCase = PanelLayoutAdapter.AdaptTodo(243, 5, 0, true);
            Check(Near(floorCase.CellHeight, 22), "todo 243 high: cell height clamps to 22");

            var ceilingCase = PanelLayoutAdapter.AdaptTodo(500, 5, 1, true);
            Check(Near(ceilingCase.CellHeight, 36), "todo 500 high: cell height clamps to 36");

            var negative = PanelLayoutAdapter.AdaptTodo(273, 5, -3, true);
            Check(negative.ListRows == 0 && !negative.ListScrolls,
                "a negative list-row request reads as an empty day");

            var shrink = PanelLayoutAdapter.AdaptTodo(290, 5, 3, true);
            Check(Near(shrink.CellHeight, 22), "the calendar floor holds at 22 when three list rows would starve it");
            Check(shrink.ListRows == 1, "the starved list shrinks to one row");
            Check(shrink.ListScrolls, "the shrunk list reports scrolling");

            var capped = PanelLayoutAdapter.AdaptTodo(500, 5, 9, true);
            Check(capped.ListRows == 6, "nine natural list rows cap at six");
            Check(capped.ListScrolls, "nine natural rows over six shown scroll");
            Check(Near(capped.CellHeight, 36), "todo 500 high with six list rows: cell height 36");

            // A manually taller panel lifts the six-row cap: the list takes
            // every natural row before the calendar keeps its 36 maximum.
            var expanded = PanelLayoutAdapter.AdaptTodo(650, 5, 9, true, int.MaxValue);
            Check(expanded.ListRows == 9, "a manually taller panel shows all nine rows");
            Check(!expanded.ListScrolls, "the taller panel's nine rows all fit");
            Check(Near(expanded.CellHeight, 36), "the taller panel still keeps the 36 cell maximum");

            var expandedFit = PanelLayoutAdapter.FitTodoList(650, 180, 9, 36, int.MaxValue);
            Check(Near(expandedFit.Viewport, 270), "a manually taller panel gives the list nine rows");
            Check(!expandedFit.Scrolls, "nine rows fit the taller list viewport");
            Check(!expandedFit.Pinned, "the add row follows all nine rows");

            var listFirst = PanelLayoutAdapter.AdaptTodo(343, 5, 2, true);
            Check(listFirst.ListRows == 2, "the list takes its natural rows first");
            Check(Near(listFirst.CellHeight, 30), "the calendar gets the remaining 30 per week");
        }

        // Day labels need both available data and a calendar cell at least 32
        // high; 31.9 (contentHeight 322.5) hides them, 32 (323) shows them.
        private static void TodoLabelThreshold()
        {
            var justBelow = PanelLayoutAdapter.AdaptTodo(322.5, 5, 1, true);
            Check(Near(justBelow.CellHeight, 31.9), "the 31.9 cell-height fixture is exact");
            Check(!justBelow.ShowDayLabels, "a 31.9 cell height hides the day labels");
            Check(justBelow.ListRows == 1 && !justBelow.ListScrolls,
                "the threshold fixture keeps its single list row");

            var atThreshold = PanelLayoutAdapter.AdaptTodo(323, 5, 1, true);
            Check(Near(atThreshold.CellHeight, 32), "the 32 cell-height fixture is exact");
            Check(atThreshold.ShowDayLabels, "a 32 cell height shows the day labels");

            var labelsUnavailable = PanelLayoutAdapter.AdaptTodo(500, 5, 1, false);
            Check(!labelsUnavailable.ShowDayLabels, "missing label data hides the day labels even at 36");

            var weekFloor = PanelLayoutAdapter.AdaptTodo(343, 0, 1, true);
            Check(Near(weekFloor.CellHeight, 36), "a missing week count is treated as one week");
        }

        // Task A4.2: the day list fits under the add row's block - the
        // viewport takes what is left below the calendar, capped by the
        // natural rows; when that is not enough the add row pins to the
        // bottom edge and the list scrolls. A negative row count reads as an
        // empty day; the viewport never goes below zero.
        private static void TodoListFitting()
        {
            var one = PanelLayoutAdapter.FitTodoList(295, 132, 1, 36);
            Check(Near(one.ListTop, 219), "fit 295/132 with one row: list top 219");
            Check(Near(one.Viewport, 30), "fit 295/132 with one row: viewport 30");
            Check(!one.Scrolls, "fit 295/132 with one row: does not scroll");
            Check(!one.Pinned, "fit 295/132 with one row: the add row follows the list");

            var many = PanelLayoutAdapter.FitTodoList(295, 132, 7, 36);
            Check(Near(many.Viewport, 30), "fit 295/132 with seven rows: viewport 30");
            Check(many.Scrolls, "fit 295/132 with seven rows: 210 of content scrolls in 30");
            Check(many.Pinned, "fit 295/132 with seven rows: the add row pins to the bottom");

            var empty = PanelLayoutAdapter.FitTodoList(295, 132, 0, 66);
            Check(Near(empty.Viewport, 0), "fit 295/132 with no rows and the revealed input: viewport 0");
            Check(!empty.Scrolls, "fit 295/132 with no rows: nothing scrolls");
            Check(!empty.Pinned, "fit 295/132 with no rows: no pinning (nothing to pin over)");

            var rich = PanelLayoutAdapter.FitTodoList(495, 180, 1, 36);
            Check(Near(rich.ListTop, 267), "fit 495/180 with one row: list top 267");
            Check(Near(rich.Viewport, 30), "fit 495/180 with one row: the viewport caps at the row");
            Check(!rich.Pinned, "fit 495/180 with one row: the spare space stays below the add row");

            var three = PanelLayoutAdapter.FitTodoList(295, 132, 3, 36);
            Check(Near(three.Viewport, 30), "fit 295/132 with three rows: viewport 30");
            Check(three.Pinned, "fit 295/132 with three rows: the add row pins to the bottom");
            Check(three.Scrolls, "fit 295/132 with three rows: 90 of content scrolls in 30");

            // The pinned viewport keeps the fractional space (a half row)
            // instead of rounding up to a whole row.
            var half = PanelLayoutAdapter.FitTodoList(280, 132, 3, 36);
            Check(Near(half.ListTop, 219), "fit 280/132 with three rows: list top 219");
            Check(Near(half.Viewport, 15), "fit 280/132 with three rows: viewport 15 (a half row)");
            Check(half.Pinned, "fit 280/132 with three rows: the add row pins to the bottom");
            Check(half.Scrolls, "fit 280/132 with three rows: 90 of content scrolls in 15");

            var squeezed = PanelLayoutAdapter.FitTodoList(200, 132, 1, 36);
            Check(Near(squeezed.Viewport, 0), "fit 200/132 with one row: the viewport clamps to 0");
            Check(squeezed.Pinned, "fit 200/132 with one row: pinned");
            Check(squeezed.Scrolls, "fit 200/132 with one row: the one row scrolls in the zero viewport");

            var negative = PanelLayoutAdapter.FitTodoList(295, 132, -3, 36);
            Check(Near(negative.Viewport, 0), "fit 295/132 with -3 rows: the viewport stays 0");
            Check(!negative.Pinned, "fit 295/132 with -3 rows: a negative count reads as an empty day");
            Check(!negative.Scrolls, "fit 295/132 with -3 rows: an empty day does not scroll");

            Check(Near(one.ListTop - 132, 87), "the list top minus the calendar is the fixed 87");
        }

        // Migration recreates the old look once: the folder formula mirrors
        // the old scaled grid (plus borders and the truncation footer), the
        // todo default uses the fixed 133 block and 28-high calendar cells.
        private static void MigrationSizes()
        {
            var (w1, h1) = PanelLayoutAdapter.MigrateFolderSize(3, 3, 5, 1.1);
            Check(Near(w1, 270) && Near(h1, 252), "migrating a 3x3/1.1 folder gives 270x252");

            var (w2, h2) = PanelLayoutAdapter.MigrateFolderSize(4, 1, 30, 1.3);
            Check(Near(w2, 414) && Near(h2, 192), "migrating a 4-column/1.3 folder with a footer gives 414x192");

            var (w3, h3) = PanelLayoutAdapter.MigrateFolderSize(3, 3, 0, 1.1);
            Check(Near(w3, 270) && Near(h3, 154), "migrating an empty folder reserves one row");

            var (tw1, th1) = PanelLayoutAdapter.DefaultTodoSize(5, 1);
            Check(Near(tw1, 342) && Near(th1, 343), "default todo size for five weeks with one list row is 342x343");

            var (tw2, th2) = PanelLayoutAdapter.DefaultTodoSize(5, 0);
            Check(Near(tw2, 342) && Near(th2, 313), "default todo size for five weeks with an empty day is 342x313");

            var (tw3, th3) = PanelLayoutAdapter.DefaultTodoSize(6, 3);
            Check(Near(tw3, 342) && Near(th3, 431), "default todo size for six weeks with three list rows is 342x431");

            var (tw4, th4) = PanelLayoutAdapter.DefaultTodoSize(0, 0);
            Check(Near(tw4, 342) && Near(th4, 201), "default todo size floors the week count at one");
        }

        // T7 (user spec 2026-10-13): the arrow-key motion over the folder
        // item grid. Left/Right are the neighbouring cell - the two row ends
        // meet, so a row's last cell steps on to the next row's first cell
        // and a row's first cell back to the previous row's last; Up/Down
        // jump a whole row of columns and stop at the top/bottom edge; a
        // partial last row takes Down from anywhere above on its last entry.
        // The degenerate inputs (empty list, fewer than one column, an index
        // outside 0..count-1) return the index untouched.
        private static void GridNavigationMatrix()
        {
            // Left/Right: the neighbouring cell, clamped at both grid ends.
            Check(GridNavigation.Move(1, 10, 4, GridDirection.Left) == 0,
                "grid (1,10,4): Left walks to 0");
            Check(GridNavigation.Move(1, 10, 4, GridDirection.Right) == 2,
                "grid (1,10,4): Right walks to 2");
            Check(GridNavigation.Move(0, 10, 4, GridDirection.Left) == 0,
                "grid (0,10,4): Left stays on the first entry");
            Check(GridNavigation.Move(9, 10, 4, GridDirection.Right) == 9,
                "grid (9,10,4): Right stays on the last entry");

            // The row ends meet: (3,10,4) is a row's last cell, (4,10,4) the
            // next row's first.
            Check(GridNavigation.Move(3, 10, 4, GridDirection.Right) == 4,
                "grid (3,10,4): Right crosses to the next row's first cell");
            Check(GridNavigation.Move(4, 10, 4, GridDirection.Left) == 3,
                "grid (4,10,4): Left crosses to the previous row's last cell");

            // Up/Down: one full row of columns; the edges keep the cell.
            Check(GridNavigation.Move(2, 10, 4, GridDirection.Down) == 6,
                "grid (2,10,4): Down jumps to 6 (same column)");
            Check(GridNavigation.Move(6, 10, 4, GridDirection.Up) == 2,
                "grid (6,10,4): Up jumps to 2 (same column)");
            Check(GridNavigation.Move(1, 10, 4, GridDirection.Up) == 1,
                "grid (1,10,4): Up stays on the top row");
            Check(GridNavigation.Move(9, 10, 4, GridDirection.Down) == 9,
                "grid (9,10,4): Down stays on the full last row");

            // Partial last row (10 items: two full rows of four plus two):
            // Down lands on its last entry - (7) sits above a column the
            // partial row does not have, (5) likewise.
            Check(GridNavigation.Move(7, 10, 4, GridDirection.Down) == 9,
                "grid (7,10,4): Down falls to the partial row's last entry");
            Check(GridNavigation.Move(5, 10, 4, GridDirection.Down) == 9,
                "grid (5,10,4): Down falls to the partial row's last entry");
            Check(GridNavigation.Move(8, 10, 4, GridDirection.Down) == 8,
                "grid (8,10,4): Down inside the partial last row stays");
            Check(GridNavigation.Move(0, 3, 4, GridDirection.Down) == 0,
                "grid (0,3,4): Down on a single-row grid stays");

            // One column: the grid degenerates to a linear list.
            Check(GridNavigation.Move(1, 5, 1, GridDirection.Down) == 2,
                "grid (1,5,1): one column walks down like a list");
            Check(GridNavigation.Move(3, 5, 1, GridDirection.Up) == 2,
                "grid (3,5,1): one column walks up like a list");

            // Degenerate inputs stay untouched.
            Check(GridNavigation.Move(0, 0, 4, GridDirection.Right) == 0,
                "grid: an empty list returns the index");
            Check(GridNavigation.Move(0, -3, 4, GridDirection.Right) == 0,
                "grid: a negative count returns the index");
            Check(GridNavigation.Move(2, 10, 0, GridDirection.Down) == 2,
                "grid: zero columns returns the index");
            Check(GridNavigation.Move(-1, 10, 4, GridDirection.Right) == -1,
                "grid: index -1 returns the index");
            Check(GridNavigation.Move(10, 10, 4, GridDirection.Right) == 10,
                "grid: index count returns the index");
        }
    }
}
