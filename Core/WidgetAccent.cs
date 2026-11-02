using System.Windows.Media;

namespace Kobold.Core
{
    /// <summary>
    /// Shared widget-colour accent maths. The widget colour is an accent, not
    /// a panel wash: it appears on the outer hairline, the header rail/dot,
    /// selection surfaces and the island's open-tile wash. Very light colours
    /// darken on the light theme and very dark colours lighten on the dark
    /// theme so an extreme pick never disappears.
    /// </summary>
    public static class WidgetAccent
    {
        public static Color Readable(string hex)
        {
            Color color = Utils.HexToColor(hex);
            double brightness = PerceivedBrightness(color);

            if (!ThemeManager.IsDarkTheme && brightness > 0.8)
                return Utils.DarkenColor(color, 0.6);

            if (ThemeManager.IsDarkTheme && brightness < 0.3)
                return Utils.LightenColor(color, 0.5);

            return color;
        }

        public static SolidColorBrush ReadableBrush(string hex)
        {
            var brush = new SolidColorBrush(Readable(hex));
            brush.Freeze();
            return brush;
        }

        public static Color Tint(Color surface, string hex, double amount)
        {
            return Utils.MixColor(surface, Readable(hex), amount);
        }

        public static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public static double PerceivedBrightness(Color color)
        {
            return (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        }
    }
}
