using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kobold.Core;

namespace Kobold.MonitorPlacementCheck
{
    /// <summary>
    /// Behaviour checks for PanelPlacement - the pure half of monitor-aware
    /// panel positioning. Uses fabricated monitor layouts, so it runs exactly
    /// the same on any machine. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/MonitorPlacementCheck
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
                Console.WriteLine("ALL MONITOR-PLACEMENT CHECKS PASSED");
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

        private static readonly MonitorInfo Primary = new MonitorInfo
        {
            DeviceName = @"\\.\DISPLAY1",
            IsPrimary = true,
            BoundsDevice = new Rect(0, 0, 1920, 1080),
            WorkAreaDevice = new Rect(0, 0, 1920, 1040),
            DpiScale = 1.0
        };

        private static readonly MonitorInfo Secondary = new MonitorInfo
        {
            DeviceName = @"\\.\DISPLAY2",
            IsPrimary = false,
            BoundsDevice = new Rect(1920, 0, 2560, 1440),
            WorkAreaDevice = new Rect(1920, 0, 2560, 1400),
            DpiScale = 1.5
        };

        private static void RunAll()
        {
            var monitors = new List<MonitorInfo> { Primary, Secondary };

            // 1. Legacy save (no recorded DPI) is interpreted with the current scale.
            var r = PanelPlacement.ToDeviceRect(100, 100, 300, 200, 0, 1.5);
            Check(r == new Rect(150, 150, 450, 300), "legacy save falls back to the current DPI");

            // 2. A recorded DPI is used verbatim.
            r = PanelPlacement.ToDeviceRect(100, 100, 300, 200, 1.0, 1.5);
            Check(r == new Rect(100, 100, 300, 200), "saved scale is used verbatim");

            // 3. A panel inside the secondary monitor clamps to its work area.
            var panel = new Rect(2000, 1300, 400, 200); // bottom sticks out
            var clamped = PanelPlacement.ClampToMonitors(panel, monitors);
            Check(clamped.X == 2000 && clamped.Bottom <= 1400, "clamps into the overlapping monitor work area");

            // 4. A panel on the primary monitor stays there.
            Check(PanelPlacement.ClampToMonitors(new Rect(100, 100, 200, 200), monitors).X == 100,
                "a panel within the primary work area is not moved");

            // 5. Straddling panels go to the monitor with the larger overlap.
            var across = new Rect(1800, 300, 600, 200); // 120 on primary, 480 on secondary
            var acrossClamped = PanelPlacement.ClampToMonitors(across, monitors);
            Check(acrossClamped.X >= 1920, "the monitor with the larger overlap wins for straddling panels");

            // 6. A fully off-screen panel is pulled into the nearest work area.
            var offClamped = PanelPlacement.ClampToMonitors(new Rect(9000, 9000, 300, 200), monitors);
            Check(offClamped.Right <= 4480 && offClamped.Bottom <= 1400,
                "an off-screen panel is pulled into the nearest work area");

            // 7. A panel larger than the work area anchors at its origin so the
            //    header (the drag handle) stays reachable.
            var hugeClamped = PanelPlacement.ClampToMonitors(new Rect(2500, 100, 2600, 2000), monitors);
            Check(hugeClamped.X == 1920 && hugeClamped.Y == 0, "an oversized panel anchors at the work-area origin");

            // 8. Device-to-DIP uses the destination monitor's scale.
            var dip = PanelPlacement.ToDipRect(new Rect(1950, 150, 450, 300), Secondary);
            Check(Near(dip.X, 1300) && Near(dip.Y, 100) && Near(dip.Width, 300) && Near(dip.Height, 200),
                "device-to-DIP uses the target monitor scale");

            // 9. No monitor data (interop unavailable) leaves the rect untouched.
            Check(PanelPlacement.ClampToMonitors(new Rect(1, 2, 3, 4), new List<MonitorInfo>()) == new Rect(1, 2, 3, 4),
                "empty monitor list leaves the rect untouched");

            // 10. Invalid scales fall back to 1.0.
            Check(PanelPlacement.ToDeviceRect(10, 10, 10, 10, 0, 0) == new Rect(10, 10, 10, 10),
                "invalid scales fall back to 1.0");

            // 11. A panel saved at 100% restores to the same physical spot at 150%.
            var saved = PanelPlacement.ToDeviceRect(1930, 200, 300, 200, 1.0, 1.5);
            var clampedSaved = PanelPlacement.ClampToMonitors(saved, monitors);
            Check(clampedSaved.X == 1930 && clampedSaved.Y == 200,
                "a panel saved at 100% restores to the same physical spot at 150%");
            var back = PanelPlacement.ToDipRect(clampedSaved, Secondary);
            Check(Near(back.X, 1930.0 / 1.5) && Near(back.Y, 200.0 / 1.5) &&
                  Near(back.Width, 200) && Near(back.Height, 200.0 / 1.5),
                "and converts back with the destination scale");

            // 12. Live interop: at least one sane monitor must be visible.
            var live = MonitorInterop.GetMonitors();
            Check(live.Count >= 1, "enumerates at least one monitor");
            Check(live.All(m => m.BoundsDevice.Width > 0 && m.BoundsDevice.Height > 0 && m.DpiScale > 0),
                "monitor bounds and DPI are sane");
            Check(live.Any(m => m.IsPrimary), "one monitor is primary");
        }

        private static bool Near(double a, double b)
        {
            return Math.Abs(a - b) < 0.001;
        }
    }
}
