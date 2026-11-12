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

            // P2 Task C: inline member tiles extend the existing row, so the
            // capsule is left-anchored and the whole row is clamped into the
            // work area. An inside row keeps its left edge; an overflowing row
            // shifts left just enough; an oversized row anchors to the margin.
            Check(IslandLayout.ClampAnchoredLeft(100, 600, 0, 1920, 16) == 100,
                "island layout: an inside row keeps its left edge");
            Check(IslandLayout.ClampAnchoredLeft(1500, 600, 0, 1920, 16) == 1304,
                "island layout: an overflowing row shifts left just enough");
            Check(IslandLayout.ClampAnchoredLeft(-50, 300, 0, 1920, 16) == 16,
                "island layout: the left edge clamps to the margin");
            Check(IslandLayout.ClampAnchoredLeft(100, 2000, 0, 1920, 16) == 16,
                "island layout: an oversized row anchors left");
            Check(IslandLayout.ClampAnchoredLeft(-2000, 600, -1920, 1920, 16) == -1904,
                "island layout: the clamp works on a monitor left of the primary");
            Check(IslandLayout.MiddleContentWidth(1 + 3) == 208,
                "island layout: an expanded three-member group adds three member slots");

            // The pill drag's virtual-screen clamp works on the capsule's LEFT
            // edge (margin 0): at either screen edge the capsule keeps its edge.
            // A centre clamp would jump it inward by half its width.
            Check(IslandLayout.ClampAnchoredLeft(0, 456, 0, 1920, 0) == 0,
                "island layout: a left-edge drag keeps the capsule's left edge");
            Check(IslandLayout.ClampAnchoredLeft(1900, 456, 0, 1920, 0) == 1464,
                "island layout: a right-edge drag clamps the capsule's right edge");

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

            // The add-widget launcher card opens under the add tile with its
            // right edge on the tile's right edge. The left edge is clamped to
            // the monitor's work area so the card never leaves the screen (the
            // popup callback works in physical screen pixels).
            Check(IslandLayout.AddMenuLeft(1300, 300, 0, 1560) == 1000,
                "the add menu right-aligns under its tile");
            Check(IslandLayout.AddMenuLeft(100, 300, 0, 1560) == 0,
                "an add menu at the left edge clamps to the work area");
            Check(IslandLayout.AddMenuLeft(1700, 300, 0, 1560) == 1260,
                "an add menu past the right edge clamps to the work area");
            Check(IslandLayout.AddMenuLeft(-100, 300, -1920, 0) == -400,
                "the add menu clamp works left of the primary monitor");
            Check(IslandLayout.AddMenuLeft(1000, 2000, 0, 1560) == 0,
                "a menu wider than the work area starts at the work-area left");

            // Dropping the island clamps the dragged centre into the island
            // monitor's work area: a position inside stays put, the edges
            // clamp by half the window, and a window at least as wide as the
            // work area sits centred. (Work-area coordinates, not screen ones:
            // a second monitor left of the primary has a negative workLeft.)
            Check(IslandLayout.ClampCenter(960, 500, 0, 1920) == 960,
                "a dragged centre inside the work area stays put");
            Check(IslandLayout.ClampCenter(100, 500, 0, 1920) == 250,
                "a dragged centre at the left edge clamps by half the window");
            Check(IslandLayout.ClampCenter(1900, 500, 0, 1920) == 1670,
                "a dragged centre at the right edge clamps by half the window");
            Check(IslandLayout.ClampCenter(960, 2000, 0, 1920) == 960,
                "a window wider than the work area sits centred");
            Check(IslandLayout.ClampCenter(100, 500, 1920, 1920) == 2170,
                "the clamp works in a second monitor's own coordinates");
            Check(IslandLayout.ClampCenter(-1900, 500, -1920, 1920) == -1670,
                "the clamp works left of the primary monitor too");

            // P2: a group folds to one island tile. Its three members no longer
            // contribute three slots - the folded tile occupies one middle slot
            // and the other members leave the row.
            var group = new WidgetGroup { Id = "g1", ActiveWidgetId = "m1" };
            var grouped = new List<WidgetData>
            {
                new FolderData { Id = "m1", GroupId = "g1", GroupOrder = 0 },
                new FolderData { Id = "m2", GroupId = "g1", GroupOrder = 1 },
                new FolderData { Id = "m3", GroupId = "g1", GroupOrder = 2 }
            };
            var projected = GroupHostPolicy.BuildIslandGroupTiles(
                new[] { group }, grouped, id => false);
            Check(projected.Count == 1,
                "island: a three-member group folds into one group tile");
            Check(IslandLayout.MiddleContentWidth(projected.Count)
                  == IslandLayout.MiddleContentWidth(1),
                "island: the folded group tile occupies one middle slot");
            Check(Math.Abs(IslandLayout.ContentWidth(projected.Count, 1560)
                           - IslandLayout.ContentWidth(1, 1560)) < 0.001,
                "island: grouping one member slot does not widen the capsule");
        }
    }
}
