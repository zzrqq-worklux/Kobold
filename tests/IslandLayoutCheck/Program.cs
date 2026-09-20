using System;
using System.Collections.Generic;
using Kobold.Core;

namespace Kobold.IslandLayoutCheck
{
    /// <summary>
    /// Checks for IslandLayout: the island is three parts - a fixed desktop
    /// entry, a scrollable middle that holds the widget tiles (capped to a
    /// fraction of the screen) and the fixed add/settings entries. These checks
    /// cover the middle-section widths and the scroll math.
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

            // The middle section: one tile plus its gap per widget.
            Check(IslandLayout.MiddleContentWidth(0) == 0, "no widgets: the middle section is empty");
            Check(IslandLayout.MiddleContentWidth(3) == 156, "three widgets measure three tiles");
            Check(IslandLayout.MiddleContentWidth(20) == 1040, "each widget adds exactly one tile");

            // The middle section is capped to a quarter of the screen, with one
            // tile as the floor so a tiny screen still shows something.
            Check(Math.Abs(IslandLayout.MiddleMaxWidth(1920) - 480) < 0.001,
                "a 1920 screen caps the middle section at a quarter of its width");
            Check(Math.Abs(IslandLayout.MiddleMaxWidth(1560) - 390) < 0.001,
                "a 1560 screen caps the middle section at a quarter of its width");
            Check(IslandLayout.MiddleMaxWidth(200) == IslandLayout.ScrollStep,
                "a tiny screen still shows at least one tile");

            // Fit boundaries: on a 1560 screen seven tiles fit the capped middle
            // section and the eighth scrolls; on a 1920 screen nine fit.
            Check(IslandLayout.MiddleContentWidth(7) <= IslandLayout.MiddleViewWidth(7, 1560),
                "7 widgets fit the middle section on a 1560 screen");
            Check(IslandLayout.MiddleContentWidth(8) > IslandLayout.MiddleViewWidth(8, 1560),
                "the 8th widget scrolls the middle section on a 1560 screen");
            Check(IslandLayout.MiddleContentWidth(9) <= IslandLayout.MiddleViewWidth(9, 1920),
                "9 widgets fit the middle section on a 1920 screen");
            Check(IslandLayout.MiddleContentWidth(10) > IslandLayout.MiddleViewWidth(10, 1920),
                "the 10th widget scrolls the middle section on a 1920 screen");

            // The viewport never exceeds the cap and never exceeds the content.
            Check(IslandLayout.MiddleViewWidth(3, 1560) == 156,
                "a small middle section is as wide as its content");
            Check(IslandLayout.MiddleViewWidth(20, 1560) == 390,
                "a large middle section stops at the cap");

            // Capsule width: padding + fixed desktop entry + the middle section
            // + trailing divider (only when widgets exist) + add + settings.
            Check(IslandLayout.ContentWidth(0, 1560) == 185, "no widgets: fixed entries only");
            Check(IslandLayout.ContentWidth(3, 1560) == 354, "three widgets: fixed entries plus three tiles");
            Check(IslandLayout.ContentWidth(20, 1560) == 588,
                "a capped middle section keeps the capsule at its maximum width");

            // The scroll offset is clamped to the scrollable middle: never before
            // its start, never past its end, and a middle that fits does not move.
            Check(IslandLayout.ClampOffset(-5, 200) == 0,
                "a negative offset snaps back to the middle start");
            Check(IslandLayout.ClampOffset(120, 200) == 120,
                "an offset inside the middle stays put");
            Check(IslandLayout.ClampOffset(260, 200) == 200,
                "an offset past the end snaps to the last position");
            Check(IslandLayout.ClampOffset(50, 0) == 0,
                "a middle that fits does not scroll at all");

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
