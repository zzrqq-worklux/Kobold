using System;
using System.Collections.Generic;
using Kobold.Core;

namespace Kobold.AdaptiveLayoutCheck
{
    /// <summary>
    /// Behaviour checks for PanelLayoutAdapter - the pure layout maths behind
    /// the freely resizable folder and todo panels: the folder grid (columns,
    /// scale, visible rows, scrolling), the todo height split between the
    /// calendar and the day list (cell 22..36, 0..6 list rows, the 32 day-label
    /// threshold) and the one-shot size migration from the old per-widget
    /// settings. Expected values are pinned by the approved spec.
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
            FolderNarrowAndWide();
            FolderColumnSteps();
            FolderVisibleRows();
            FolderColumnMonotonicity();
            FolderScrollSemantics();
            TodoHeightAllocation();
            TodoLabelThreshold();
            MigrationSizes();
        }

        // The folder grid follows usable = content - PADDING, columns =
        // max(2, ceil(usableW / 112)) and scale = clamp(usableW / columns / 76).
        private static void FolderNarrowAndWide()
        {
            var narrow = PanelLayoutAdapter.AdaptFolder(150, 300, 5);
            Check(narrow.Columns == 2, "narrow folder: two columns");
            Check(Near(narrow.Scale, 0.9), "narrow folder: scale clamps to the 0.9 floor");
            Check(narrow.CellWidth == 69, "narrow folder: cell width 69");
            Check(narrow.CellHeight == 81, "narrow folder: cell height 81");
            Check(narrow.VisibleRows == 3, "narrow folder: three rows fit");
            Check(!narrow.Scrolls, "narrow folder: five items in two columns do not scroll");

            var wide = PanelLayoutAdapter.AdaptFolder(240, 300, 5);
            Check(wide.Columns == 2, "wide folder: still two columns");
            Check(Near(wide.Scale, 1.45), "wide folder: scale clamps to the 1.45 ceiling");
            Check(wide.CellWidth == 111, "wide folder: cell width 111");
            Check(wide.CellHeight == 130, "wide folder: cell height 130");
            Check(wide.VisibleRows == 2, "wide folder: two rows fit");
            Check(wide.Scrolls, "wide folder: the third content row scrolls");
        }

        // The 224/225 usable-width boundary is where the third column appears
        // and the icon scale steps back down (1.45 -> 75/76).
        private static void FolderColumnSteps()
        {
            var atBoundary = PanelLayoutAdapter.AdaptFolder(241, 300, 5);
            Check(atBoundary.Columns == 3, "usable width 225 starts the third column");
            Check(Near(atBoundary.Scale, 225.0 / 228.0), "at the boundary the scale is 225/228");
            Check(atBoundary.CellWidth == 75, "at the boundary the cell width is 75");
            Check(atBoundary.CellHeight == 88, "at the boundary the cell height is 88");

            var midColumn = PanelLayoutAdapter.AdaptFolder(340, 300, 5);
            Check(midColumn.Columns == 3, "a 340-wide folder keeps three columns");
            Check(Near(midColumn.Scale, 324.0 / 228.0), "340-wide folder: scale is 324/228");
            Check(midColumn.CellWidth == 108, "340-wide folder: cell width 108");
            Check(midColumn.CellHeight == 127, "340-wide folder: cell height 127");
        }

        private static void FolderVisibleRows()
        {
            var shortPanel = PanelLayoutAdapter.AdaptFolder(150, 90, 1);
            Check(shortPanel.VisibleRows == 1, "a panel too short for a row still shows one row");
        }

        private static void FolderColumnMonotonicity()
        {
            int previous = 0;
            bool nonDecreasing = true;
            bool atLeastTwo = true;
            for (int width = 100; width <= 500; width++)
            {
                var layout = PanelLayoutAdapter.AdaptFolder(width, 300, 5);
                if (layout.Columns < previous) nonDecreasing = false;
                if (layout.Columns < 2) atLeastTwo = false;
                previous = layout.Columns;
            }
            Check(nonDecreasing, "columns never shrink as the folder widens (100..500)");
            Check(atLeastTwo, "columns stay at two or more across the width sweep");
        }

        // Scrolling compares the natural content rows against the visible ones;
        // the folder's 1..6 per-widget cap does not apply here.
        private static void FolderScrollSemantics()
        {
            var scrolling = PanelLayoutAdapter.AdaptFolder(150, 200, 10);
            Check(scrolling.Columns == 2 && scrolling.VisibleRows == 2,
                "the scrolling fixture lands on two columns and two visible rows");
            Check(scrolling.Scrolls, "ten items over two columns and two visible rows scroll");
            Check(scrolling.Scrolls == PanelRows.RowsFor(10, scrolling.Columns) > scrolling.VisibleRows,
                "scrolling follows the PanelRows row count");

            var fitting = PanelLayoutAdapter.AdaptFolder(150, 200, 4);
            Check(!fitting.Scrolls, "four items over two columns and two visible rows do not scroll");
            Check(fitting.Scrolls == PanelRows.RowsFor(4, fitting.Columns) > fitting.VisibleRows,
                "not scrolling follows the PanelRows row count");

            var tall = PanelLayoutAdapter.AdaptFolder(150, 450, 10);
            Check(tall.VisibleRows == 5 && !tall.Scrolls,
                "ten items fit in five visible rows without the PanelRows 1..6 cap");
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
    }
}
