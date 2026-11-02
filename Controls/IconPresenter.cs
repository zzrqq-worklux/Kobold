using System;
using System.Windows;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Core.Icons;

namespace Kobold.Controls
{
    /// <summary>
    /// Draws one baked 24x24 UI icon at a requested size. The geometry comes
    /// from <see cref="IconCatalog"/>; the style is either the global
    /// <see cref="IconStyleManager.Current"/> or an explicit local override.
    /// No filters, turbulence or runtime jitter are involved.
    /// </summary>
    public sealed class IconPresenter : FrameworkElement
    {
        private const double CanvasSize = 24.0;

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(IconId), typeof(IconPresenter),
                new FrameworkPropertyMetadata(IconId.Folder,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IconStyleProperty =
            DependencyProperty.Register(nameof(IconStyle), typeof(string), typeof(IconPresenter),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BrushProperty =
            DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(IconPresenter),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register(nameof(Size), typeof(double), typeof(IconPresenter),
                new FrameworkPropertyMetadata(16.0,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RotationProperty =
            DependencyProperty.Register(nameof(Rotation), typeof(double), typeof(IconPresenter),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(IconPresenter),
                new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

        public IconPresenter()
        {
            Focusable = false;
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>The icon to draw.</summary>
        public IconId Icon
        {
            get { return (IconId)GetValue(IconProperty); }
            set { SetValue(IconProperty, value); }
        }

        /// <summary>Local style override; null/empty follows the global style.</summary>
        public string IconStyle
        {
            get { return (string)GetValue(IconStyleProperty); }
            set { SetValue(IconStyleProperty, value); }
        }

        /// <summary>Stroke brush; null follows the live muted-text theme brush.</summary>
        public Brush Brush
        {
            get { return (Brush)GetValue(BrushProperty); }
            set { SetValue(BrushProperty, value); }
        }

        /// <summary>Rendered square size in DIPs.</summary>
        public double Size
        {
            get { return (double)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        /// <summary>Rotation around the icon centre (degrees).</summary>
        public double Rotation
        {
            get { return (double)GetValue(RotationProperty); }
            set { SetValue(RotationProperty, value); }
        }

        /// <summary>Stroke width in the 24x24 source space.</summary>
        public double StrokeThickness
        {
            get { return (double)GetValue(StrokeThicknessProperty); }
            set { SetValue(StrokeThicknessProperty, value); }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double size = EffectiveSize();
            return new Size(size, size);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            double size = EffectiveSize();
            if (size <= 0) return;

            string style = string.IsNullOrEmpty(IconStyle) ? IconStyleManager.Current : IconStyle;
            Geometry geometry = IconCatalog.Get(Icon, IconStyleIds.IsHandDrawn(style));

            Brush brush = Brush ?? ThemeManager.MutedTextBrush ?? Brushes.Black;
            double scale = size / CanvasSize;

            var transform = new TransformGroup();
            transform.Children.Add(new RotateTransform(Rotation, CanvasSize / 2, CanvasSize / 2));
            transform.Children.Add(new ScaleTransform(scale, scale));

            var pen = new Pen(brush, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            drawingContext.PushTransform(transform);
            drawingContext.DrawGeometry(null, pen, geometry);
            drawingContext.Pop();
        }

        private double EffectiveSize()
        {
            double size = Size;
            if (double.IsNaN(size) || double.IsInfinity(size) || size <= 0) return 0;
            return size;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            IconStyleManager.Changed += OnStyleChanged;
            ThemeManager.ThemeChanged += OnStyleChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            IconStyleManager.Changed -= OnStyleChanged;
            ThemeManager.ThemeChanged -= OnStyleChanged;
        }

        private void OnStyleChanged()
        {
            InvalidateVisual();
        }
    }
}
