using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using FoldRa.Core;

namespace FoldRa.Controls.IconRenderers
{
    public class ClassicIconRenderer : IconRendererBase
    {
        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);
            
            // Main folder body with rounded corners
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(8, 4), IsClosed = true };
            figure.Segments.Add(new LineSegment(new Point(22, 4), true));
            figure.Segments.Add(new LineSegment(new Point(28, 12), true));
            figure.Segments.Add(new LineSegment(new Point(54, 12), true));
            figure.Segments.Add(new ArcSegment(new Point(60, 18), new Size(6, 6), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(60, 52), true));
            figure.Segments.Add(new ArcSegment(new Point(54, 58), new Size(6, 6), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(10, 58), true));
            figure.Segments.Add(new ArcSegment(new Point(4, 52), new Size(6, 6), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(4, 10), true));
            figure.Segments.Add(new ArcSegment(new Point(8, 4), new Size(6, 6), 0, false, SweepDirection.Clockwise, true));
            geometry.Figures.Add(figure);
            
            var gradientBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            gradientBrush.GradientStops.Add(new GradientStop(HighlightColor, 0));
            gradientBrush.GradientStops.Add(new GradientStop(LightColor, 0.15));
            gradientBrush.GradientStops.Add(new GradientStop(BaseColor, 0.4));
            gradientBrush.GradientStops.Add(new GradientStop(DarkColor, 0.85));
            gradientBrush.GradientStops.Add(new GradientStop(DarkerColor, 1));
            
            var folderPath = new Path
            {
                Data = geometry,
                Fill = gradientBrush,
                Effect = new DropShadowEffect { BlurRadius = 15, ShadowDepth = 5, Opacity = 0.45, Direction = 270, Color = Color.FromRgb(0, 0, 0) }
            };
            canvas.Children.Add(folderPath);
            
            // Top highlight line
            canvas.Children.Add(new Line
            {
                X1 = 10, Y1 = 15, X2 = 54, Y2 = 15,
                Stroke = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            
            // Inner content lines
            var lineBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            double[] lineWidths = { 36, 28, 20 };
            for (int i = 0; i < 3; i++)
            {
                canvas.Children.Add(new Line
                {
                    X1 = 14, Y1 = 26 + i * 9, X2 = 14 + lineWidths[i], Y2 = 26 + i * 9,
                    Stroke = lineBrush, StrokeThickness = 2.5,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                });
            }
            
            // Left edge highlight
            canvas.Children.Add(new Line
            {
                X1 = 5, Y1 = 14, X2 = 5, Y2 = 50,
                Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                StrokeThickness = 1
            });
        }
    }

    public class ModernIconRenderer : IconRendererBase
    {
        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);
            
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(4, 8), IsClosed = true };
            figure.Segments.Add(new LineSegment(new Point(24, 8), true));
            figure.Segments.Add(new LineSegment(new Point(28, 14), true));
            figure.Segments.Add(new LineSegment(new Point(60, 14), true));
            figure.Segments.Add(new LineSegment(new Point(60, 56), true));
            figure.Segments.Add(new LineSegment(new Point(4, 56), true));
            figure.Segments.Add(new LineSegment(new Point(4, 8), true));
            geometry.Figures.Add(figure);
            
            var folderPath = new Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(BaseColor),
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.4, Direction = 270 }
            };
            canvas.Children.Add(folderPath);
            
            // Tab notch
            var tab = new Rectangle { Width = 20, Height = 4, Fill = new SolidColorBrush(DarkColor), RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(tab, 6);
            Canvas.SetTop(tab, 10);
            canvas.Children.Add(tab);
        }
    }

    public class MinimalIconRenderer : IconRendererBase
    {
        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);
            bool isDark = ThemeManager.IsDarkTheme;
            
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(8, 12), IsClosed = true };
            figure.Segments.Add(new LineSegment(new Point(22, 12), true));
            figure.Segments.Add(new LineSegment(new Point(28, 18), true));
            figure.Segments.Add(new LineSegment(new Point(54, 18), true));
            figure.Segments.Add(new ArcSegment(new Point(58, 22), new Size(4, 4), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(58, 50), true));
            figure.Segments.Add(new ArcSegment(new Point(54, 54), new Size(4, 4), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(10, 54), true));
            figure.Segments.Add(new ArcSegment(new Point(6, 50), new Size(4, 4), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(6, 16), true));
            figure.Segments.Add(new ArcSegment(new Point(8, 12), new Size(4, 4), 0, false, SweepDirection.Clockwise, true));
            geometry.Figures.Add(figure);
            
            var folderPath = new Path
            {
                Data = geometry,
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(BaseColor),
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round,
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = isDark ? 0.4 : 0.2, Direction = 270 }
            };
            canvas.Children.Add(folderPath);
            
            var lineBrush = new SolidColorBrush(Color.FromArgb((byte)(isDark ? 150 : 100), BaseColor.R, BaseColor.G, BaseColor.B));
            for (int i = 0; i < 2; i++)
            {
                canvas.Children.Add(new Line
                {
                    X1 = 14, Y1 = 32 + i * 10, X2 = 50, Y2 = 32 + i * 10,
                    Stroke = lineBrush, StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                });
            }
        }
    }

    public class RoundedIconRenderer : IconRendererBase
    {
        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);
            bool isDark = ThemeManager.IsDarkTheme;
            
            var mainBody = new Rectangle
            {
                Width = 54, Height = 40, RadiusX = 10, RadiusY = 10,
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 4, Opacity = 0.5, Direction = 270 }
            };
            
            var gradientBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            gradientBrush.GradientStops.Add(new GradientStop(LightColor, 0));
            gradientBrush.GradientStops.Add(new GradientStop(BaseColor, 0.4));
            gradientBrush.GradientStops.Add(new GradientStop(DarkColor, 1));
            mainBody.Fill = gradientBrush;
            
            Canvas.SetLeft(mainBody, 5);
            Canvas.SetTop(mainBody, 16);
            canvas.Children.Add(mainBody);
            
            var tab = new Rectangle { Width = 24, Height = 12, RadiusX = 5, RadiusY = 5 };
            var tabGradient = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            tabGradient.GradientStops.Add(new GradientStop(LightColor, 0));
            tabGradient.GradientStops.Add(new GradientStop(BaseColor, 1));
            tab.Fill = tabGradient;
            Canvas.SetLeft(tab, 5);
            Canvas.SetTop(tab, 6);
            canvas.Children.Add(tab);
            
            canvas.Children.Add(new Line
            {
                X1 = 12, Y1 = 20, X2 = 52, Y2 = 20,
                Stroke = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            
            var innerDoc = new Rectangle
            {
                Width = 18, Height = 14, RadiusX = 3, RadiusY = 3,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(isDark ? 60 : 40), 255, 255, 255)),
                Stroke = new SolidColorBrush(Color.FromArgb((byte)(isDark ? 100 : 60), 255, 255, 255)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(innerDoc, 23);
            Canvas.SetTop(innerDoc, 30);
            canvas.Children.Add(innerDoc);
        }
    }

    public class FlatIconRenderer : IconRendererBase
    {
        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);
            
            var back = new Rectangle { Width = 52, Height = 38, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(DarkColor) };
            Canvas.SetLeft(back, 6);
            Canvas.SetTop(back, 18);
            canvas.Children.Add(back);
            
            var front = new Rectangle { Width = 52, Height = 34, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(BaseColor) };
            Canvas.SetLeft(front, 6);
            Canvas.SetTop(front, 22);
            canvas.Children.Add(front);
            
            var tab = new Rectangle { Width = 20, Height = 8, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(BaseColor) };
            Canvas.SetLeft(tab, 6);
            Canvas.SetTop(tab, 14);
            canvas.Children.Add(tab);
        }
    }

    public class GradientIconRenderer : IconRendererBase
    {
        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);
            var accentColor = Utils.LightenColor(BaseColor, 1.8);
            
            var shadow = new Rectangle
            {
                Width = 52, Height = 40, RadiusX = 6, RadiusY = 6,
                Fill = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0))
            };
            Canvas.SetLeft(shadow, 8);
            Canvas.SetTop(shadow, 20);
            canvas.Children.Add(shadow);
            
            var body = new Rectangle { Width = 52, Height = 40, RadiusX = 6, RadiusY = 6 };
            var bodyGradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            bodyGradient.GradientStops.Add(new GradientStop(accentColor, 0));
            bodyGradient.GradientStops.Add(new GradientStop(LightColor, 0.3));
            bodyGradient.GradientStops.Add(new GradientStop(BaseColor, 0.7));
            bodyGradient.GradientStops.Add(new GradientStop(DarkColor, 1));
            body.Fill = bodyGradient;
            Canvas.SetLeft(body, 6);
            Canvas.SetTop(body, 16);
            canvas.Children.Add(body);
            
            var tab = new Rectangle { Width = 22, Height = 10, RadiusX = 4, RadiusY = 4 };
            var tabGradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            tabGradient.GradientStops.Add(new GradientStop(accentColor, 0));
            tabGradient.GradientStops.Add(new GradientStop(LightColor, 1));
            tab.Fill = tabGradient;
            Canvas.SetLeft(tab, 6);
            Canvas.SetTop(tab, 8);
            canvas.Children.Add(tab);
            
            var shine = new Rectangle
            {
                Width = 44, Height = 8, RadiusX = 4, RadiusY = 4,
                Fill = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255))
            };
            Canvas.SetLeft(shine, 10);
            Canvas.SetTop(shine, 18);
            canvas.Children.Add(shine);
        }
    }
}


