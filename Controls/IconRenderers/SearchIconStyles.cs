using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kobold.Controls.IconRenderers
{
    /// <summary>
    /// Classic search icon: a clean 64x64 magnifier - the lens ring and the
    /// handle carry the widget colour, with a light highlight inside the lens
    /// and along the handle. Sized to sit in the same tile canvas as the
    /// folder/todo/note glyphs.
    /// </summary>
    public class ClassicSearchIconRenderer : IconRendererBase
    {
        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);

            var ring = new SolidColorBrush(BaseColor);
            var shine = new SolidColorBrush(HighlightColor);

            // Handle first: the lens sits over its start so the joint reads
            // as one piece.
            canvas.Children.Add(new Line
            {
                X1 = 38, Y1 = 38, X2 = 54, Y2 = 54,
                Stroke = ring,
                StrokeThickness = 7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            canvas.Children.Add(new Line
            {
                X1 = 40, Y1 = 40, X2 = 52, Y2 = 52,
                Stroke = shine,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            var lens = new Ellipse
            {
                Width = 36,
                Height = 36,
                Stroke = ring,
                StrokeThickness = 6,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255))
            };
            Canvas.SetLeft(lens, 8);
            Canvas.SetTop(lens, 8);
            canvas.Children.Add(lens);

            // Lens highlight: a short arc in the upper-left, the classic
            // glass shine.
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 17 23 Q 21 15, 29 14"),
                Stroke = shine,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }
    }

    /// <summary>
    /// Hand-drawn search icon: the same magnifier with baked-in wobble (fixed
    /// paths, no runtime randomness), so it matches the hand-drawn folder /
    /// todo / note families. The outline is the widget colour and the glass
    /// shine is its highlight tint.
    /// </summary>
    public class HandDrawnSearchIconRenderer : IconRendererBase
    {
        private const string LensPath =
            "M 25.8,9.2 Q 38.6,8.4 43.1,20.3 Q 47.2,32.4 37.6,39.9 Q 27.2,46.2 16.6,38.4 " +
            "Q 7.3,31.3 9.4,20.6 Q 12.7,10 25.8,9.2 Z";

        private const string HandlePath =
            "M 40.1,41.4 Q 42.6,38.8 45.7,41.9 L 55.4,51.6 Q 58.3,54.5 55.1,57.3 " +
            "Q 52.2,60.2 49.3,57.2 L 39.6,47.5 Q 36.9,44.7 40.1,41.4 Z";

        private const string ShinePath =
            "M 15.2,23.4 Q 17.6,14.9 26.4,12.6";

        public override void Render(Canvas canvas, string color)
        {
            InitColors(color);

            var stroke = new SolidColorBrush(BaseColor);
            var shine = new SolidColorBrush(HighlightColor);

            canvas.Children.Add(CreatePath(HandlePath, null, stroke, 5.5));
            canvas.Children.Add(CreatePath(LensPath, null, stroke, 5.5));
            canvas.Children.Add(CreatePath(ShinePath, null, shine, 2.4));
        }

        private static Path CreatePath(string data, Brush fill, Brush stroke, double thickness)
        {
            return new Path
            {
                Data = Geometry.Parse(data),
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
