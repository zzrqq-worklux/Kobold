using System;
using System.Collections.Generic;
using Kobold.Core;

namespace Kobold.IslandLayoutCheck
{
    /// <summary>
    /// Checks for IslandLayout: the expanded-island width formula and the
    /// scrolling window used when the widget row no longer fits on screen.
    /// Exit code 0 = all green; 1 = failures. Run: dotnet run --project tests/IslandLayoutCheck
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
                Console.WriteLine("ALL ISLAND-LAYOUT CHECKS PASSED");
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
            // One wheel notch scrolls exactly one tile plus its gap.
            Check(IslandLayout.ScrollStep == WidgetConstants.ISLAND_TILE_SIZE + WidgetConstants.ISLAND_TILE_GAP,
                "wheel step equals one tile plus its gap");

            // Expanded content width: padding + desktop entry + widget tiles +
            // trailing divider (only when widgets exist) + add + settings.
            Check(IslandLayout.ContentWidth(0) == 185, "no widgets: fixed entries only");
            Check(IslandLayout.ContentWidth(1) == 250, "one widget adds a tile and the trailing divider");
            Check(IslandLayout.ContentWidth(5) == 458, "each further widget adds exactly one tile");

            // The row itself - what WPF measures and scrolls - is 24 narrower
            // than the capsule, which keeps 12px of breathing room on each side.
            Check(IslandLayout.RowWidth(0) == 161, "no widgets: the row holds just the fixed entries");
            Check(IslandLayout.RowWidth(1) == 226, "one widget adds a tile and the trailing divider to the row");
            Check(IslandLayout.RowWidth(5) == 434, "each further widget adds exactly one tile to the row");

            // The island is capped to a quarter of the screen so a wide row can
            // never blanket the top of the display - browser tabs live there and
            // a stray click on a tile would toggle a panel.
            Check(Math.Abs(IslandLayout.MaxWidth(1920) - 480) < 0.001,
                "a 1920 screen caps the island at a quarter of its width");
            Check(Math.Abs(IslandLayout.MaxWidth(1560) - 390) < 0.001,
                "a 1560 screen caps the island at a quarter of its width");
            Check(IslandLayout.MaxWidth(200) == IslandLayout.ContentWidth(0),
                "a tiny screen still fits the fixed entries, never less");

            // Documented boundaries: a quarter-width island on a 1920 screen
            // fits 5 widget tiles, on the 1560 screen 4; the next one is the
            // first that has to be scrolled to. The row, not the slightly wider
            // capsule, is what decides this.
            Check(IslandLayout.RowWidth(5) <= IslandLayout.MaxWidth(1920),
                "5 widgets still fit the quarter-width island on a 1920 screen");
            Check(IslandLayout.RowWidth(6) > IslandLayout.MaxWidth(1920),
                "6 widgets overflow it and have to scroll");
            Check(IslandLayout.RowWidth(4) <= IslandLayout.MaxWidth(1560),
                "4 widgets still fit the quarter-width island on a 1560 screen");
            Check(IslandLayout.RowWidth(5) > IslandLayout.MaxWidth(1560),
                "5 widgets overflow it and have to scroll");

            // The scroll offset is clamped to the row: never before its start,
            // never past its end, and a row that fits does not scroll at all.
            Check(IslandLayout.ClampOffset(-5, 200) == 0,
                "a negative offset snaps back to the row start");
            Check(IslandLayout.ClampOffset(120, 200) == 120,
                "an offset inside the row stays put");
            Check(IslandLayout.ClampOffset(260, 200) == 200,
                "an offset past the end snaps to the last position");
            Check(IslandLayout.ClampOffset(50, 0) == 0,
                "a row that fits does not scroll at all");

            // Wheel notches move whole tiles and clamp at both ends.
            Check(IslandLayout.ScrollTarget(0, -1, 200) == 0,
                "scrolling towards the start is a no-op there");
            Check(IslandLayout.ScrollTarget(0, 1, 200) == 52,
                "one notch forward moves exactly one tile");
            Check(IslandLayout.ScrollTarget(52, -1, 200) == 0,
                "one notch back returns to the start");
            Check(IslandLayout.ScrollTarget(30, 1, 200) == 82,
                "a notch moves a whole tile from any offset");
            Check(IslandLayout.ScrollTarget(190, 1, 200) == 200,
                "scrolling past the end clamps to the last position");
            Check(IslandLayout.ScrollTarget(10, 0, 200) == 10,
                "no notches leaves the offset alone");
        }
    }
}
