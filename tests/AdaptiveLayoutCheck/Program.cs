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
            MigrationSizes();
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
