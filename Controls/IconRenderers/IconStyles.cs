using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Kobold.Controls.IconRenderers
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
}
