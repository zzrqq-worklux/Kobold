using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Kobold.Core;

namespace Kobold.Controls.IconRenderers
{
    /// <summary>
    /// Classic todo icon: a native WPF port of the reference calendar SVG
    /// (shapes + LinearGradientBrush; the feDropShadow is approximated with
    /// offset translucent rounded rectangles, no filter). Elements are
    /// authored in the 200x200 reference space and scaled into the 64x64 tile
    /// canvas through one root transform. Paper and ink follow the theme; the
    /// header, date blocks and check mark carry the widget colour.
    /// </summary>
    public class ClassicTodoIconRenderer : IconRendererBase
    {
        private const double Scale = 0.38;
        private const double OffsetX = -6.0;
        private const double OffsetY = -7.9;

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
            var paperSolid = new SolidColorBrush(paperTop);
            var white = new SolidColorBrush(Colors.White);

            // Drop shadow: the reference rect plus its feDropShadow stood in
            // for by a second, offset translucent rounded rect.
            AddRect(root, 32, 48, 140, 130, 12, new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)));
            AddRect(root, 35, 53, 140, 130, 12, new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)));

            // Paper body.
            var paper = new Rectangle
            {
                Width = 140,
                Height = 130,
                RadiusX = 12,
                RadiusY = 12,
                Fill = paperBrush,
                Stroke = inkBrush,
                StrokeThickness = 4.5
            };
            Canvas.SetLeft(paper, 30);
            Canvas.SetTop(paper, 45);
            root.Children.Add(paper);

            // Header bar: widget colour replaces the reference red.
            root.Children.Add(new Path
            {
                Data = Geometry.Parse("M 30 57 Q 30 45, 42 45 L 158 45 Q 170 45, 170 57 L 170 80 L 30 80 Z"),
                Fill = new LinearGradientBrush(BaseColor, Utils.DarkenColor(BaseColor, 0.2),
                    new Point(0.5, 0), new Point(0.5, 1))
            });
            root.Children.Add(new Path
            {
                Data = Geometry.Parse("M 32 50 Q 32 47, 42 47 L 158 47 Q 168 47, 168 50 L 168 55 L 32 55 Z"),
                Fill = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255))
            });

            // Binding rings stay neutral metal.
            AddRing(root, 58);
            AddRing(root, 130);

            // Title lines on the header.
            AddLine(root, 50, 65, 90, 65, white, 6);
            AddLine(root, 50, 72, 75, 72, new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)), 5);

            // Grid.
            for (int i = 0; i < 4; i++) AddLine(root, 45, 95 + i * 20, 155, 95 + i * 20, gridBrush, 4.5);
            AddLine(root, 80, 85, 80, 160, gridBrush, 4.5);
            AddLine(root, 115, 85, 115, 160, gridBrush, 4.5);

            // Date blocks: widget colour at ~0.8 alpha replaces the yellow.
            var dateBrush = new SolidColorBrush(Color.FromArgb(0xCC, BaseColor.R, BaseColor.G, BaseColor.B));
            for (int i = 0; i < 3; i++) AddRect(root, 50 + i * 35, 100, 22, 10, 2, dateBrush);

            // List rows; the tick is the widget colour in both styles.
            AddCheckRow(root, 127, 145, paperSolid, inkBrush, true);
            AddCheckRow(root, 147, 130, paperSolid, inkBrush, false);
            AddCheckRow(root, 162, 110, paperSolid, inkBrush, false);

            // Bottom shine.
            root.Children.Add(new Path
            {
                Data = Geometry.Parse("M 30 165 Q 100 172, 170 165"),
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)),
                StrokeThickness = 4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        private void AddCheckRow(Canvas root, double lineY, double lineEndX,
            Brush paper, Brush ink, bool done)
        {
            var box = new Rectangle
            {
                Width = 14,
                Height = 14,
                RadiusX = 3,
                RadiusY = 3,
                Fill = paper,
                Stroke = ink,
                StrokeThickness = 4
            };
            Canvas.SetLeft(box, 50);
            Canvas.SetTop(box, lineY - 7);
            root.Children.Add(box);

            if (done)
            {
                root.Children.Add(new Path
                {
                    Data = Geometry.Parse("M 53 127 L 57 131 L 63 121"),
                    Stroke = new SolidColorBrush(BaseColor),
                    StrokeThickness = 5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                });
            }

            AddLine(root, 72, lineY, lineEndX, lineY, ink, 6);
        }

        private static void AddRing(Canvas root, double x)
        {
            var metal = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0xF0, 0xF0, 0xF0), 0));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0xBD, 0xC3, 0xC7), 0.5));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0x7F, 0x8C, 0x8D), 1));

            var ring = new Rectangle
            {
                Width = 12,
                Height = 18,
                RadiusX = 4,
                RadiusY = 4,
                Fill = metal,
                Stroke = new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D)),
                StrokeThickness = 4
            };
            Canvas.SetLeft(ring, x);
            Canvas.SetTop(ring, 36);
            root.Children.Add(ring);

            AddRect(root, x + 2, 38, 8, 14, 2, new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)));
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
    /// Hand-drawn todo style: the reference doodle calendar with the wobble
    /// baked into fixed path data (deterministic offline generator, no runtime
    /// random, no SVG filter), already scaled and centred in the 64x64 tile
    /// canvas. Paper and ink follow the theme; the circled date, the number
    /// doodle and the tick carry the widget colour.
    /// </summary>
    public class HandDrawnTodoIconRenderer : IconRendererBase
    {
        // Generated by the throwaway script (%TEMP%\task-3.3-gen-handdrawn.ps1,
        // seed 20261009, anchors wobbled +-2, controls +-1.5, then scaled
        // 200 -> 64 with scale 0.38; circle constants are centre + diameter).
        private const string ShadowPath =
            "M 13.81,9.88 Q 34.49,7.22 55.04,8.94 Q 59.3,11.47 59.51,52.06 Q 59.21,59.25 53.42,58.26 Q 34.79,60.41 15.01,58.24 Q 10.1,57.27 8.88,51.55 Q 9.47,11.77 13.81,9.88 Z";
        private const string PaperPath =
            "M 11.53,7.6 Q 32.21,4.94 52.76,6.66 Q 57.02,9.19 57.23,49.78 Q 56.93,56.97 51.14,55.98 Q 32.51,58.13 12.73,55.96 Q 7.82,54.99 6.6,49.27 Q 7.19,9.49 11.53,7.6 Z";
        private const string TitleLine1 =
            "M 17.21,18.61 L 27.47,19.1 L 37.18,18.52 L 46.84,18.94";
        private const string TitleLine2 =
            "M 16.72,22.51 L 24.25,22.79 L 32.08,22.29";
        private const string DoodlePath =
            "M 46.05,26.43 L 47.05,26.28 L 48.46,25.86 L 46.92,27.17 L 46.07,27.76 L 47.53,28.19 L 49.16,28.09 Q 48.88,30.09 47.56,30.04 L 46.24,29.11 L 45.55,28.98";
        private const string CheckPath =
            "M 14.96,32.88 L 15.25,34.14 L 15.97,35.83 L 18.12,33.62 L 19.52,30.92";
        private const string ListLine1 =
            "M 22.98,32.67 L 33.58,33.08 L 43.64,33.21";
        private const string ListLine2 =
            "M 22.79,42.93 L 31.23,42.87 L 40.11,42.47";
        private const string ListLine3 =
            "M 22.94,51.76 L 27.48,52.13 L 31.92,52.36";

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

            canvas.Children.Add(CreatePath(Geometry.Parse(PaperPath), paperBrush, inkBrush, 2.0));

            AddCircle(canvas, 20.35, 10.82, 4.6, paperBrush, inkBrush, 1.6, null);
            AddCircle(canvas, 43, 10.74, 4.41, paperBrush, inkBrush, 1.6, null);

            canvas.Children.Add(CreatePath(Geometry.Parse(TitleLine1), null, inkBrush, 2.2));
            canvas.Children.Add(CreatePath(Geometry.Parse(TitleLine2), null, inkBrush, 1.8));

            AddCircle(canvas, 46.85, 30.35, 8.96, null, accentBrush, 1.6, new DoubleCollection { 1.3, 0.7 });
            canvas.Children.Add(CreatePath(Geometry.Parse(DoodlePath), null, accentBrush, 1.5));

            AddCheckBox(canvas, 13.04, 32.07, 5.91, 6.21, paperBrush, inkBrush);
            canvas.Children.Add(CreatePath(Geometry.Parse(CheckPath), null, accentBrush, 1.9));
            canvas.Children.Add(CreatePath(Geometry.Parse(ListLine1), null, inkBrush, 1.9));

            AddCheckBox(canvas, 12.81, 41.44, 6.05, 6.06, paperBrush, inkBrush);
            canvas.Children.Add(CreatePath(Geometry.Parse(ListLine2), null, inkBrush, 1.9));

            AddCheckBox(canvas, 12.8, 50.87, 6.08, 6.0, paperBrush, inkBrush);
            canvas.Children.Add(CreatePath(Geometry.Parse(ListLine3), null, inkBrush, 1.9));

            AddDot(canvas, 56.48, 16.55, 2.3, inkBrush);
            AddDot(canvas, 58.89, 20.29, 2.44, inkBrush);
            AddDot(canvas, 4.99, 47.22, 2.47, inkBrush);
            AddDot(canvas, 3.43, 50.69, 2.43, inkBrush);
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

        /// <summary>Circle constants are centre + diameter in the 64x64 canvas.</summary>
        private static void AddCircle(Canvas canvas, double centerX, double centerY, double diameter,
            Brush fill, Brush stroke, double thickness, DoubleCollection dashes)
        {
            var circle = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = thickness
            };
            if (dashes != null) circle.StrokeDashArray = dashes;
            Canvas.SetLeft(circle, centerX - diameter / 2);
            Canvas.SetTop(circle, centerY - diameter / 2);
            canvas.Children.Add(circle);
        }

        private static void AddCheckBox(Canvas canvas, double x, double y, double width, double height,
            Brush paper, Brush ink)
        {
            var box = new Rectangle
            {
                Width = width,
                Height = height,
                RadiusX = 1.2,
                RadiusY = 1.2,
                Fill = paper,
                Stroke = ink,
                StrokeThickness = 1.6
            };
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            canvas.Children.Add(box);
        }

        private static void AddDot(Canvas canvas, double centerX, double centerY, double diameter, Brush fill)
        {
            var dot = new Ellipse { Width = diameter, Height = diameter, Fill = fill };
            Canvas.SetLeft(dot, centerX - diameter / 2);
            Canvas.SetTop(dot, centerY - diameter / 2);
            canvas.Children.Add(dot);
        }
    }
}
