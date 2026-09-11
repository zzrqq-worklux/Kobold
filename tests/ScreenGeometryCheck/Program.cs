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

            Check(!ScreenGeometry.ContainsPhysicalPoint(window, 0, 2.0, 1560, 440),
                "invalid scale is treated as outside");
        }
    }
}
