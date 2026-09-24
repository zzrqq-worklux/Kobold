using System;
using System.Collections.Generic;
using System.Linq;
using Kobold.Core;

namespace Kobold.HighContrastCheck
{
    /// <summary>
    /// Behaviour checks for HighContrastPalette - the pure mapping from the
    /// system high-contrast colors onto the semantic brush keys ThemeManager
    /// uses. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/HighContrastCheck
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
                Console.WriteLine("ALL HIGH-CONTRAST CHECKS PASSED");
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
            string W = "#101010", WT = "#F0F0F0", H = "#FFD700", HT = "#000000",
                G = "#808080", CD = "#404040", CT = "#E0E0E0";
            var map = HighContrastPalette.Build(W, WT, H, HT, G, CD, CT);

            Check(map["Background"] == W && map["Surface"] == W && map["IslandBackground"] == W,
                "window surfaces use the system window color");
            Check(map["TextPrimary"] == WT && map["IslandForeground"] == WT && map["OverlayText"] == WT,
                "primary text uses the system window text color");
            Check(map["TextSecondary"] == WT && map["TextMuted"] == G && map["OverlayMuted"] == G,
                "secondary/muted text maps to system text/gray");
            Check(map["Border"] == CD && map["ControlBorder"] == CD && map["TooltipBorder"] == CD,
                "borders use the system control dark color");
            Check(map["SurfaceHover"] == H && map["OverlayHover"] == H && map["IslandHover"] == H &&
                  map["SelectionBorder"] == H && map["DropIndicator"] == H,
                "hover/selection states use the system highlight color");
            Check(map["ScrollThumbHover"] == CT && map["SliderThumb"] == CT && map["ControlBorderHover"] == CT,
                "active states use the system control text color");
            Check(map.Count >= 35, "covers the full semantic brush set");
            Check(map.Values.All(v => v.Length == 7 && v[0] == '#'), "all values are #RRGGBB");
        }
    }
}
