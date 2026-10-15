using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Kobold.Core;

namespace Kobold.Controls.IconRenderers
{
    /// <summary>
    /// Classic note icon: a page with a rounded body, a folded top-right
    /// corner, four ruled lines (the last one short and indented) and a
    /// widget-coloured margin bookmark down the left. Authored in a 100x100
    /// reference space and scaled into the 64x64 tile canvas through one root
    /// transform (the sheet is centred, so the offsets are zero). Paper and
    /// ink follow the theme; the bookmark and the title rule carry the widget
    /// colour.
    /// </summary>
    public class ClassicNoteIconRenderer : IconRendererBase
    {
        private const double Scale = 0.64;
        private const double OffsetX = 0.0;
        private const double OffsetY = 0.0;

        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);

            bool highContrast = ThemeManager.IsHighContrast;
            Color paperTop = highContrast
                ? SystemColors.WindowColor
                : (ThemeManager.IsDarkTheme ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
            Color paperBottom = highContrast
                ? SystemColors.WindowColor
                : (ThemeManager.IsDarkTheme ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xE8, 0xE8, 0xE8));
            Color ink = highContrast
                ? SystemColors.WindowTextColor
                : (ThemeManager.IsDarkTheme ? Color.FromRgb(0xE0, 0xE0, 0xE0) : Color.FromRgb(0x34, 0x49, 0x5E));

            var root = new Canvas
            {
                Width = 64,
                Height = 64,
                RenderTransform = new MatrixTransform(Scale, 0, 0, Scale, OffsetX, OffsetY)
            };
            canvas.Children.Add(root);

            var inkBrush = new SolidColorBrush(ink);
            var gridBrush = new SolidColorBrush(ink) { Opacity = highContrast ? 1.0 : 0.65 };
            var paperBrush = new LinearGradientBrush(paperTop, paperBottom, new Point(0.5, 0), new Point(0.5, 1));
            var accentBrush = new SolidColorBrush(BaseColor);

            // Drop shadow: the reference page rect plus its feDropShadow stood
            // in for by two offset translucent rounded rects.
            AddRect(root, 16, 8, 72, 88, 8, new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)));
            AddRect(root, 17, 9, 72, 88, 8, new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)));

            // Page body: rounded on three corners, cut back at the top-right.
            root.Children.Add(new Path
            {
                Data = Geometry.Parse("M 22 6 L 70 6 L 86 22 L 86 86 Q 86 94, 78 94 L 22 94 Q 14 94, 14 86 L 14 14 Q 14 6, 22 6 Z"),
                Fill = paperBrush,
                Stroke = inkBrush,
                StrokeThickness = 2.8
            });

            // Folded corner: the outer triangle sits back over the cut edge.
            root.Children.Add(new Path
            {
                Data = Geometry.Parse("M 70 6 L 86 6 L 86 22 Z"),
                Fill = new SolidColorBrush(Color.FromArgb(0x33, ink.R, ink.G, ink.B)),
                Stroke = inkBrush,
                StrokeThickness = 2.8,
                StrokeLineJoin = PenLineJoin.Round
            });

            // Margin bookmark: the widget colour down the left of the sheet.
            AddRect(root, 22, 14, 8, 72, 3, accentBrush);

            // Ruled lines; the title rule and the short indented tail carry the
            // accent where the brief asks for line emphasis.
            AddLine(root, 40, 30, 74, 30, accentBrush, 3.4);
            AddLine(root, 40, 46, 80, 46, gridBrush, 2.4);
            AddLine(root, 40, 62, 80, 62, gridBrush, 2.4);
            AddLine(root, 52, 78, 80, 78, gridBrush, 2.4);

            // Top shine.
            root.Children.Add(new Path
            {
                Data = Geometry.Parse("M 22 9 Q 46 6, 68 9"),
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)),
                StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        private static void AddRect(Canvas root, double x, double y, double width, double height,
            double radius, Brush fill)
        {
            var rect = new Rectangle { Width = width, Height = height, RadiusX = radius, RadiusY = radius, Fill = fill };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            root.Children.Add(rect);
        }

        private static void AddLine(Canvas root, double x1, double y1, double x2, double y2,
            Brush stroke, double thickness)
        {
            root.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }
    }

    /// <summary>
    /// Hand-drawn note style: the same page doodled with the wobble baked into
    /// fixed path data (deterministic offline generator, no runtime random, no
    /// SVG filter), already scaled and centred in the 64x64 tile canvas. Paper
    /// and ink follow the theme; the margin bookmark and the title rule carry
    /// the widget colour.
    /// </summary>
    public class HandDrawnNoteIconRenderer : IconRendererBase
    {
        // Generated by the throwaway script (%TEMP%\opencode\task-4-gen-note.ps1,
        // seed 20261015, anchors wobbled +-0.7, quadratic controls +-0.6),
        // authored directly in the 64x64 tile canvas.
        private const string ShadowPath =
            "M 14.01,7.63 L 46.13,7 L 56.68,17.66 L 56.25,53.71 Q 54.87,57.7 49.94,59.58 L 16.24,59.09 Q 8.61,57.61 10.65,53.64 L 10.44,13.08 Q 8.7,5.85 14.01,7.63 Z";
        private const string PaperPath =
            "M 13.49,6.46 L 45.6,6.67 L 55.07,15.48 L 54.83,52.21 Q 54.75,58.19 49.56,58.41 L 15.1,58.07 Q 8.62,57.69 9.49,51.7 L 8.93,11.69 Q 9.34,5.83 13.49,6.46 Z";
        private const string FoldPath =
            "M 45.6,6.67 L 54.36,5.49 L 55.07,15.48 Z";
        private const string StripPath =
            "M 14.6,11.51 L 18.37,12.49 L 17.58,52.46 L 14.63,51.34 Z";
        private const string TitleLine =
            "M 23.32,16.67 L 36.66,17.66 L 46.35,16.52";
        private const string BodyLine1 =
            "M 24.47,27.21 L 34.1,27.22 L 44.65,27.41 L 51.64,27.35";
        private const string BodyLine2 =
            "M 24.56,36.84 L 34.5,37.46 L 43.72,36.83 L 50.43,37.01";
        private const string BodyLine3 =
            "M 31.86,46.99 L 41.4,47.41 L 49.36,46.86";

        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);

            bool highContrast = ThemeManager.IsHighContrast;
            Color paper = highContrast
                ? SystemColors.WindowColor
                : (ThemeManager.IsDarkTheme ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xFD, 0xFB, 0xF7));
            Color ink = highContrast
                ? SystemColors.WindowTextColor
                : (ThemeManager.IsDarkTheme ? Color.FromRgb(0xE0, 0xE0, 0xE0) : Color.FromRgb(0x2E, 0x40, 0x53));

            var paperBrush = new SolidColorBrush(paper);
            var inkBrush = new SolidColorBrush(ink);
            var accentBrush = new SolidColorBrush(BaseColor);

            // Offset translucent shadow (the reference offset underlay).
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse(ShadowPath),
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0))
            });

            canvas.Children.Add(CreatePath(Geometry.Parse(PaperPath), paperBrush, inkBrush, 1.8));

            canvas.Children.Add(CreatePath(Geometry.Parse(FoldPath),
                new SolidColorBrush(Color.FromArgb(0x33, ink.R, ink.G, ink.B)), inkBrush, 1.6));

            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse(StripPath),
                Fill = accentBrush
            });

            canvas.Children.Add(CreatePath(Geometry.Parse(TitleLine), null, accentBrush, 1.9));
            canvas.Children.Add(CreatePath(Geometry.Parse(BodyLine1), null, inkBrush, 1.7));
            canvas.Children.Add(CreatePath(Geometry.Parse(BodyLine2), null, inkBrush, 1.7));
            canvas.Children.Add(CreatePath(Geometry.Parse(BodyLine3), null, inkBrush, 1.7));
        }

        private static Path CreatePath(Geometry geometry, Brush fill, Brush stroke, double thickness)
        {
            return new Path
            {
                Data = geometry,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }
    }
}
