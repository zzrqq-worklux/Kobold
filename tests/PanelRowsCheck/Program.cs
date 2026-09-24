using System;
using System.Collections.Generic;
using Kobold.Core;

namespace Kobold.PanelRowsCheck
{
    /// <summary>
    /// Behaviour checks for PanelRows - the pure row maths that caps how many
    /// item rows a panel grows to before it scrolls. Each widget carries its
    /// own cap (1-6 rows, default 3, unset = 0 tolerated). Exit code 0 = all
    /// green; 1 = failures. Run: dotnet run --project tests/PanelRowsCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            RunAll();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL PANEL-ROWS CHECKS PASSED");
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

        private static void RunAll()
        {
            // Rows come from the item count and the widget's column count; an
            // empty panel still occupies one row (its empty-state message).
            Check(PanelRows.RowsFor(0, 3) == 1, "an empty panel still needs one row");
            Check(PanelRows.RowsFor(3, 3) == 1, "a full first row is one row");
            Check(PanelRows.RowsFor(4, 3) == 2, "the first item of a second row adds a row");
            Check(PanelRows.RowsFor(9, 3) == 3 && PanelRows.RowsFor(10, 3) == 4,
                "rows round up with the column count");
            Check(PanelRows.RowsFor(10, 0) == 10, "columns below one are treated as a single column");

            // The per-widget cap limits the rows the panel grows to...
            Check(PanelRows.VisibleRows(10, 3, 3) == 3, "the cap hides the fourth row");
            Check(PanelRows.VisibleRows(6, 3, 3) == 2, "fewer rows than the cap is not padded");
            Check(PanelRows.VisibleRows(10, 3, 4) == 4, "a larger per-widget cap shows more rows");
            Check(PanelRows.VisibleRows(10, 3, 2) == 2, "a smaller per-widget cap shows fewer rows");

            // ...while the full row count decides whether scrolling is needed.
            Check(!PanelRows.NeedsScroll(9, 3, 3), "exactly the cap does not scroll");
            Check(PanelRows.NeedsScroll(10, 3, 3), "one row past the cap scrolls");
            Check(!PanelRows.NeedsScroll(10, 3, 4) && PanelRows.NeedsScroll(10, 3, 2),
                "the scroll decision follows the per-widget cap");

            // Invalid stored values fall back to the default; the menu offers 1-6.
            Check(PanelRows.VisibleRows(10, 3, 0) == PanelRows.DefaultMaxRows,
                "an unset cap falls back to the default");
            Check(PanelRows.VisibleRows(10, 3, -5) == PanelRows.DefaultMaxRows,
                "a negative cap falls back to the default");
            Check(PanelRows.VisibleRows(30, 3, 99) == PanelRows.MaxAllowedRows,
                "a cap above the menu range clamps to the maximum");
        }
    }
}
