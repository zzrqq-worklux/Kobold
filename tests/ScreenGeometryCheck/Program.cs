using System;
using System.Collections.Generic;
using System.Windows;
using Kobold.Core;

namespace Kobold.ScreenGeometryCheck
{
    /// <summary>
    /// Checks for ScreenGeometry.ContainsPhysicalPoint. Cursor positions come
    /// from WinForms in physical pixels, while WPF window bounds are DIPs - on a
    /// scaled display (e.g. 200%) they must be converted before comparing.
    /// Exit code 0 = all green; 1 = failures. Run: dotnet run --project tests/ScreenGeometryCheck
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
                Console.WriteLine("ALL SCREEN-GEOMETRY CHECKS PASSED");
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
            // A centered widget panel on a 200% display: DIPs (630,20) size 300x400
            // => physical pixels (1260,40) size 600x800.
            var window = new Rect(630, 20, 300, 400);

            Check(ScreenGeometry.ContainsPhysicalPoint(window, 2.0, 2.0, 1560, 440),
                "200%: physical window center counts as inside");
            Check(ScreenGeometry.ContainsPhysicalPoint(window, 2.0, 2.0, 1260, 40),
                "200%: physical top-left corner counts as inside");
            Check(!ScreenGeometry.ContainsPhysicalPoint(window, 2.0, 2.0, 1259, 40),
                "200%: point left of the window counts as outside");
            Check(!ScreenGeometry.ContainsPhysicalPoint(window, 2.0, 2.0, 1860, 40),
                "200%: point right of the window counts as outside");
            Check(!ScreenGeometry.ContainsPhysicalPoint(window, 2.0, 2.0, 1260, 39),
                "200%: point above the window counts as outside");

            Check(ScreenGeometry.ContainsPhysicalPoint(window, 1.0, 1.0, 700, 100),
                "100%: point inside counts as inside");
            Check(!ScreenGeometry.ContainsPhysicalPoint(window, 1.0, 1.0, 400, 100),
                "100%: point outside counts as outside");

            // The conversion is linear in the scale factor, so the intermediate
            // Windows scalings must work as well as 100% and 200%.
            Check(ScreenGeometry.ContainsPhysicalPoint(window, 1.25, 1.25, 975, 275),
                "125%: physical point inside counts as inside");
            Check(!ScreenGeometry.ContainsPhysicalPoint(window, 1.25, 1.25, 700, 275),
                "125%: physical point outside counts as outside");

            Check(ScreenGeometry.ContainsPhysicalPoint(window, 1.5, 1.5, 1170, 330),
                "150%: physical point inside counts as inside");
            Check(!ScreenGeometry.ContainsPhysicalPoint(window, 1.5, 1.5, 900, 330),
                "150%: physical point outside counts as outside");

            Check(!ScreenGeometry.ContainsPhysicalPoint(window, 0, 2.0, 1560, 440),
                "invalid scale is treated as outside");

            // Menu placement: a popup may open to the right of the cursor only
            // when it fits before the monitor's right work-area edge; otherwise
            // it would be clipped off-screen.
            Check(ScreenGeometry.FitsToTheRight(1000, 400, 1440),
                "popup fits when cursor + width stays inside the work area");
            Check(!ScreenGeometry.FitsToTheRight(1200, 400, 1440),
                "popup does not fit when it would cross the work-area edge");
            Check(ScreenGeometry.FitsToTheRight(1040, 400, 1440),
                "popup exactly touching the work-area edge counts as fitting");
            Check(!ScreenGeometry.FitsToTheRight(2000, 300, 1920),
                "cursor beyond the work-area edge does not fit");

            // Vertical counterpart: a popup near the bottom edge must be lifted
            // so its bottom stays inside the monitor's work area.
            Check(ScreenGeometry.OverflowPastBottom(700, 300, 1040) == 0,
                "popup fully above the work-area bottom needs no lift");
            Check(Math.Abs(ScreenGeometry.OverflowPastBottom(900, 300, 1040) - 160) < 0.001,
                "popup overhanging the work-area bottom reports the exact lift");
            Check(Math.Abs(ScreenGeometry.OverflowPastBottom(1040, 300, 1040) - 300) < 0.001,
                "popup starting at the work-area bottom reports its full height");
        }
    }
}
