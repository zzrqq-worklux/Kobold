using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// Maps the Windows high-contrast system colors onto the semantic brush
    /// keys ThemeManager installs ('Kobold.Brush.*'). Pure so the mapping can
    /// be checked without toggling the actual system theme; the caller passes
    /// the live SystemColors values as hex.
    /// </summary>
    public static class HighContrastPalette
    {
        public static Dictionary<string, string> Build(string window, string windowText,
            string highlight, string highlightText, string grayText, string controlDark, string controlText)
        {
            return new Dictionary<string, string>
            {
                { "Background", window },
                { "Surface", window },
                { "SurfaceHover", highlight },
                { "Border", controlDark },
                { "TextPrimary", windowText },
                { "TextSecondary", windowText },
                { "TextMuted", grayText },
                { "TextFaint", grayText },
                { "ControlBackground", window },
                { "ControlBorder", controlDark },
                { "ControlBorderHover", controlText },
                { "ButtonSecondary", window },
                { "ButtonSecondaryHover", highlight },
                { "IconFill", windowText },
                { "IconStroke", controlDark },
                { "MissingBadge", grayText },
                { "PanelBorder", controlDark },
                { "DropIndicator", highlight },
                { "SelectionBorder", highlight },
                { "LassoFill", highlight },
                { "LassoStroke", highlight },
                { "ScrollThumb", controlDark },
                { "ScrollThumbHover", controlText },
                { "SliderTrack", controlDark },
                { "SliderThumb", controlText },
                { "IslandBackground", window },
                { "IslandBackgroundExpanded", window },
                { "IslandBorder", controlDark },
                { "IslandHover", highlight },
                { "IslandDivider", controlDark },
                { "IslandForeground", windowText },
                { "DragGhost", window },
                { "OverlayBackground", window },
                { "OverlayBorder", controlDark },
                { "OverlayHover", highlight },
                { "OverlayChecked", highlight },
                { "OverlayText", windowText },
                { "OverlayMuted", grayText },
                { "OverlayDivider", controlDark },
                { "TooltipBackground", window },
                { "TooltipBorder", controlDark }
            };
        }
    }
}
