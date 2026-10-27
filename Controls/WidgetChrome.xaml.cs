using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>Which edge (or corner) of the panel a resize press grabbed.</summary>
    public enum WidgetResizeEdge
    {
        Left,
        Right,
        Top,
        Bottom,
        BottomRight
    }

    /// <summary>
    /// WidgetChrome - the shared, content-agnostic panel shell: rounded border
    /// + shadow, a 40px header (title / lock / pin / drag / right-click) and
    /// the HeaderLeading / HeaderTrailing / ContentHost / FooterHost slots.
    ///
    /// The chrome owns no widget data and moves no window: header drags and
    /// resize presses are forwarded as events carrying the screen cursor, and
    /// the hosting window performs the actual move/resize.
    /// </summary>
    public partial class WidgetChrome : UserControl
    {
        private const double MIN_PANEL_OPACITY = 0.2;
        private const double MAX_PANEL_OPACITY = 1.0;

        private bool _isLocked;
        private bool _isPinned;
        private bool _isDragActive;
        private bool _isResizeActive;
        private bool _titleMenuEnabled;
        private bool _titleChevronPressed;
        private double _panelOpacity = MAX_PANEL_OPACITY;
        private string _panelColor;

        /// <summary>Lock button pressed; the host toggles its own lock state and sets <see cref="IsLocked"/>.</summary>
        public event Action LockClicked;

        /// <summary>Pin button pressed; the host toggles its own pin state and sets <see cref="IsPinned"/>.</summary>
        public event Action PinClicked;

        /// <summary>Header mouse-down (locked headers flash instead); cursor is in screen pixels.</summary>
        public event Action<Point> HeaderDragStarted;

        /// <summary>Header mouse-move while the button is held; cursor is in screen pixels.</summary>
        public event Action<Point> HeaderDragMoved;

        /// <summary>Header mouse-up; cursor is in screen pixels.</summary>
        public event Action<Point> HeaderDragEnded;

        /// <summary>Header right-click; the host shows the widget menu.</summary>
        public event Action HeaderRightClicked;

        /// <summary>The title chevron was clicked; the note host opens its title menu.</summary>
        public event Action TitleMenuClicked;

        /// <summary>Resize press grabbed an edge/corner; cursor is in screen pixels.</summary>
        public event Action<WidgetResizeEdge, Point> ResizeStarted;

        /// <summary>Resize mouse-move while the button is held; cursor is in screen pixels.</summary>
        public event Action<Point> ResizeMoved;

        /// <summary>Resize mouse-up; cursor is in screen pixels.</summary>
        public event Action<Point> ResizeEnded;

        public WidgetChrome()
        {
            InitializeComponent();

            // Never leave the grabbing cursor stuck if capture is lost mid-drag.
            Header.LostMouseCapture += (s, e) => Mouse.OverrideCursor = null;

            // A lost capture ends a resize mid-flight: later moves must not be
            // forwarded to a host that still thinks the gesture is live.
            ResizeLeft.LostMouseCapture += (s, e) => _isResizeActive = false;
            ResizeRight.LostMouseCapture += (s, e) => _isResizeActive = false;
            ResizeTop.LostMouseCapture += (s, e) => _isResizeActive = false;
            ResizeBottom.LostMouseCapture += (s, e) => _isResizeActive = false;
            ResizeGrip.LostMouseCapture += (s, e) => _isResizeActive = false;

            ApplyTheme();

            // The glow ring is built from the chrome's own size: the geometry,
            // the dash pattern and the animation all follow its perimeter.
            ApplyGlowDefaults();
            ApplyGlowBrushes();
            SizeChanged += (s, e) => UpdateGlowGeometry();
            Loaded += (s, e) => UpdateGlowGeometry();
        }

        /// <summary>Header caption.</summary>
        public string Title
        {
            get => TitleText.Text;
            set => TitleText.Text = value ?? string.Empty;
        }

        /// <summary>Locked panels cannot be dragged and show a forbidden cursor over the header.</summary>
        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                if (_isLocked == value) return;
                _isLocked = value;
                UpdateLockVisual();
            }
        }

        /// <summary>Pinned panels stay open; the pin icon turns gold and upright.</summary>
        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned == value) return;
                _isPinned = value;
                UpdatePinVisual();
            }
        }

        /// <summary>
        /// The host widget's colour. The whole panel backdrop is tinted from it
        /// (same gradient maths as FolderWidget's panel); null/empty keeps the
        /// neutral surface so colourless usage is unchanged.
        /// </summary>
        public string PanelColor
        {
            get => _panelColor;
            set
            {
                if (string.Equals(_panelColor, value, StringComparison.Ordinal)) return;
                _panelColor = value;
                UpdatePanelBackground();
            }
        }

        /// <summary>Shows the title chevron; note widgets turn this on.</summary>
        public bool TitleMenuEnabled
        {
            get => _titleMenuEnabled;
            set
            {
                if (_titleMenuEnabled == value) return;
                _titleMenuEnabled = value;
                TitleChevron.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>The title area the note widget anchors its popup to.</summary>
        public FrameworkElement TitleMenuAnchor => TitleGroup;

        /// <summary>
        /// Repaints everything theme-dependent: panel backdrop, header tint,
        /// title, the title chevron and the lock/pin icon states.
        /// </summary>
        public void ApplyTheme()
        {
            UpdateLockVisual();
            UpdatePinVisual();
            UpdatePanelBackground();
            TitleText.Foreground = ThemeManager.TextBrush;
            TitleChevronGlyph.Foreground = ThemeManager.SecondaryTextBrush;

            // A high-contrast toggle changes the accent source: keep a running
            // comet on the new theme without stopping it.
            if (_isGlowActive) ApplyGlowBrushes();
        }

        /// <summary>
        /// Repaints the panel backdrop at the given opacity, clamped to the
        /// same 20-100% range as FolderWidget. Only the background alpha
        /// changes, so content stays fully crisp.
        /// </summary>
        public void ApplyOpacity(double opacity)
        {
            if (double.IsNaN(opacity)) opacity = MAX_PANEL_OPACITY;
            _panelOpacity = Math.Max(MIN_PANEL_OPACITY, Math.Min(MAX_PANEL_OPACITY, opacity));
            UpdatePanelBackground();
        }

        #region Theme and state visuals

        /// <summary>
        /// Mirrors FolderWidget's panel colours: the backdrop is tinted from
        /// <see cref="PanelColor"/> when one is set, otherwise from the
        /// theme's neutral panel surface.
        /// </summary>
        private void UpdatePanelBackground()
        {
            bool isDark = ThemeManager.IsDarkTheme;
            Color baseColor = string.IsNullOrEmpty(_panelColor)
                ? Utils.HexToColor(isDark ? UiTokens.Dark.Surface : UiTokens.Light.Surface)
                : Utils.HexToColor(_panelColor);
            Color darkColor = Utils.DarkenColor(baseColor, 0.3);

            var panelBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };

            if (isDark)
            {
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(210 * _panelOpacity), darkColor.R, darkColor.G, darkColor.B), 0));
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(230 * _panelOpacity), (byte)(darkColor.R * 0.6), (byte)(darkColor.G * 0.6), (byte)(darkColor.B * 0.6)), 1));
            }
            else
            {
                Color lightColor = Utils.LightenColor(baseColor, 0.6);
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(240 * _panelOpacity), 250, 250, 250), 0));
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(250 * _panelOpacity), lightColor.R, lightColor.G, lightColor.B), 1));
            }

            Shell.Background = panelBrush;

            Header.Background = new SolidColorBrush(isDark
                ? Color.FromArgb((byte)(50 * _panelOpacity), 255, 255, 255)
                : Color.FromArgb((byte)(40 * _panelOpacity), 0, 0, 0));
        }

        private void UpdateLockVisual()
        {
            if (_isLocked)
            {
                LockIcon.Fill = ThemeManager.PinGoldBrush;
                LockIcon.Stroke = ThemeManager.PinDarkGoldBrush;
                LockButton.ToolTip = Localization.Get("UI_UnlockTooltip");
            }
            else
            {
                LockIcon.Fill = ThemeManager.PinUnpinnedFillBrush;
                LockIcon.Stroke = ThemeManager.PinUnpinnedStrokeBrush;
                LockButton.ToolTip = Localization.Get("UI_LockTooltip");
            }

            // Locked panels cannot be dragged - show a forbidden cursor over the header.
            Header.Cursor = _isLocked ? Cursors.No : CursorHelper.OpenHand;

            // Locked panels cannot be resized either: the bands and grip
            // disappear and the whole layer stops hit-testing (no cursors).
            ResizeLayer.IsHitTestVisible = !_isLocked;
            ResizeGrip.Visibility = _isLocked ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdatePinVisual()
        {
            if (_isPinned)
            {
                // Pinned state - rotate to 0 (vertical) and highlight
                PinIconRotation.Angle = 0;
                PinIcon.Fill = ThemeManager.PinGoldBrush;
                PinIcon.Stroke = ThemeManager.PinDarkGoldBrush;
                PinButton.ToolTip = Localization.Get("UI_UnpinTooltip");
            }
            else
            {
                // Unpinned state - rotate 45 degrees and dim
                PinIconRotation.Angle = 45;
                PinIcon.Fill = ThemeManager.PinUnpinnedFillBrush;
                PinIcon.Stroke = ThemeManager.PinUnpinnedStrokeBrush;
                PinButton.ToolTip = Localization.Get("UI_PinTooltip");
            }
        }

        private void FlashLockIndicator()
        {
            var pulse = new DoubleAnimationUsingKeyFrames();
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))));
            LockButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            LockButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }

        #endregion

        #region Glow (the search widget's loading meteor: query, scan or reconcile)

        private readonly Dictionary<Path, DoubleAnimation> _glowAnimations =
            new Dictionary<Path, DoubleAnimation>();

        private string _glowColor;
        private bool _isGlowActive;

        // The timing the running clocks were built from. StartGlow compares
        // against these so a repeat call with unchanged parameters does not
        // rebuild the animations (a rebuild re-applies From = the dash length,
        // which snaps the comet back to the ring origin).
        private bool _glowApplied;
        private double _appliedGlowSeconds;
        private double _appliedGlowTailFraction;

        /// <summary>
        /// Seconds for one revolution of the loading comet. Values that are not
        /// positive fall back to the token default.
        /// </summary>
        public double GlowSeconds { get; set; } = UiTokens.GlowSeconds;

        /// <summary>
        /// Fraction of the ring perimeter the faint tail sweeps. Values outside
        /// (0, <see cref="UiTokens.GlowMaxTailFraction"/>] are clamped: a fraction
        /// of 1 would leave the tail layer with no gap and no travel.
        /// </summary>
        public double GlowTailFraction { get; set; } = UiTokens.GlowTailFraction;

        /// <summary>True while the loading glow is running (query, scan or reconcile).</summary>
        public bool IsGlowActive => _isGlowActive;

        /// <summary>
        /// Starts (or refreshes) the comet on the panel's outer rounded border:
        /// the layer becomes visible and its colour is repainted. Idempotent - a
        /// second call while the comet runs and its timing is unchanged only
        /// repaints the colour, so the dash phase keeps travelling instead of
        /// snapping back to the ring origin. A size change or a changed
        /// <see cref="GlowSeconds"/> / <see cref="GlowTailFraction"/> rebuilds the
        /// geometry and the clocks.
        /// </summary>
        public void StartGlow()
        {
            _isGlowActive = true;
            GlowLayer.Visibility = Visibility.Visible;
            ApplyGlowBrushes();

            if (_glowApplied
                && _appliedGlowSeconds == EffectiveGlowSeconds()
                && _appliedGlowTailFraction == EffectiveGlowTailFraction())
            {
                return;
            }

            UpdateGlowGeometry();
        }

        /// <summary>
        /// Hides the comet and drops its animation clocks, so a hidden panel
        /// does no animation work. The layer stays reusable: StartGlow rebuilds
        /// the geometry, the dash pattern and the clocks.
        /// </summary>
        public void StopGlow()
        {
            _isGlowActive = false;
            _glowApplied = false;
            if (GlowLayer != null) GlowLayer.Visibility = Visibility.Collapsed;

            Path[] meteors = MeteorPaths();
            for (int i = 0; i < meteors.Length; i++)
            {
                meteors[i].BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            }
            _glowAnimations.Clear();
        }

        /// <summary>
        /// Recolours the comet from the widget colour. High contrast follows the
        /// theme accent (the same rule as the search view's own accent): a
        /// missing or malformed colour falls back to the default folder colour.
        /// Safe to call before the layer is shown, so a live colour change can
        /// repaint a running comet.
        /// </summary>
        public void ApplyGlowColor(string widgetColor)
        {
            _glowColor = widgetColor;
            ApplyGlowBrushes();
        }

        /// <summary>The five glow paths, meteors in paint order (halo first).</summary>
        private Path[] MeteorPaths()
        {
            return new[] { GlowHalo, GlowTail, GlowMid, GlowHead };
        }

        /// <summary>Applies the token widths and the layer opacities once.</summary>
        private void ApplyGlowDefaults()
        {
            GlowTrack.StrokeThickness = UiTokens.GlowTrackThickness;
            GlowTrack.Opacity = 1;

            GlowHalo.StrokeThickness = UiTokens.GlowHaloThickness;
            GlowHalo.Opacity = UiTokens.GlowHaloAlpha;

            GlowTail.StrokeThickness = UiTokens.GlowCoreThickness;
            GlowTail.Opacity = UiTokens.GlowTailAlpha;

            GlowMid.StrokeThickness = UiTokens.GlowCoreThickness;
            GlowMid.Opacity = UiTokens.GlowMidAlpha;

            GlowHead.StrokeThickness = UiTokens.GlowCoreThickness;
            GlowHead.Opacity = 1;
        }

        private void ApplyGlowBrushes()
        {
            if (GlowLayer == null) return;

            Color accent = ResolveGlowAccent();
            SolidColorBrush solid = FrozenGlowBrush(accent, 1.0);
            GlowHalo.Stroke = solid;
            GlowTail.Stroke = solid;
            GlowMid.Stroke = solid;
            GlowHead.Stroke = solid;

            // The track is a dim wash over the normal border: its alpha (not
            // its Opacity) carries the dimming, so the border still reads.
            GlowTrack.Stroke = FrozenGlowBrush(accent, UiTokens.GlowTrackAlpha);
        }

        /// <summary>
        /// The accent the comet wears: the widget colour, or the theme accent in
        /// high contrast (the system highlight, like the search box's focus
        /// ring).
        /// </summary>
        private Color ResolveGlowAccent()
        {
            if (ThemeManager.IsHighContrast)
            {
                SolidColorBrush themeAccent = Application.Current == null
                    ? null
                    : Application.Current.TryFindResource("Kobold.Brush.Accent") as SolidColorBrush;
                if (themeAccent == null) themeAccent = ThemeManager.AccentBlueBrush;
                return themeAccent.Color;
            }

            string color = string.IsNullOrEmpty(_glowColor) ? UiTokens.DefaultFolderColor : _glowColor;
            return Utils.HexToColor(color);
        }

        private static SolidColorBrush FrozenGlowBrush(Color color, double alpha)
        {
            double clamped = Math.Max(0, Math.Min(1, alpha));
            var brush = new SolidColorBrush(
                Color.FromArgb((byte)Math.Round(byte.MaxValue * clamped), color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Rebuilds the five ring geometries from the chrome's own size. The
        /// centreline is inset by half the stroke width so the ink stays inside
        /// the window; the radius is clamped so a tiny panel still has a valid
        /// rounded rectangle.
        /// </summary>
        private void UpdateGlowGeometry()
        {
            if (GlowLayer == null) return;

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0) return;

            GlowTrack.Data = BuildGlowRing(width, height, GlowTrack.StrokeThickness / 2);

            // The meteors share one centreline: a wider halo is centred on the
            // same line as the 2.5 DIP core, not inset by its own width.
            double meteorInset = UiTokens.GlowCoreThickness / 2;
            Path[] meteors = MeteorPaths();
            for (int i = 0; i < meteors.Length; i++)
            {
                meteors[i].Data = BuildGlowRing(width, height, meteorInset);
            }

            // The dash pattern and its animation are derived from the
            // perimeter, so a resize has to rebuild both.
            RestartGlowAnimations();
        }

        private static RectangleGeometry BuildGlowRing(double width, double height, double inset)
        {
            double radius = Math.Min(UiTokens.GlowRadius, Math.Min(width, height) / 2 - inset);
            if (radius < 0) radius = 0;

            var rect = new Rect(inset, inset,
                Math.Max(0, width - 2 * inset), Math.Max(0, height - 2 * inset));
            var geometry = new RectangleGeometry(rect, radius, radius);
            geometry.Freeze();
            return geometry;
        }

        /// <summary>The perimeter of a rounded rectangle: runs plus quarter arcs.</summary>
        private static double RingPerimeter(RectangleGeometry geometry)
        {
            Rect rect = geometry.Rect;
            double radius = geometry.RadiusX;
            return 2 * (rect.Width + rect.Height) - 8 * radius + 2 * Math.PI * radius;
        }

        /// <summary>
        /// (Re)builds every meteor's dash pattern and one-perimeter animation.
        /// Dash units are multiples of the stroke width; the pattern length is
        /// exactly one perimeter, so the loop is seamless. Each layer starts one
        /// dash-length before the ring origin, which puts every leading edge on
        /// the same point: the bright head leads and the faint tail follows it,
        /// while the shrinking offset advances the pattern clockwise.
        /// </summary>
        private void RestartGlowAnimations()
        {
            if (!_isGlowActive) return;

            _appliedGlowSeconds = EffectiveGlowSeconds();
            _appliedGlowTailFraction = EffectiveGlowTailFraction();
            _glowApplied = true;

            ApplyGlowAnimation(GlowHalo, UiTokens.GlowHaloShare);
            ApplyGlowAnimation(GlowTail, UiTokens.GlowTailShare);
            ApplyGlowAnimation(GlowMid, UiTokens.GlowMidShare);
            ApplyGlowAnimation(GlowHead, UiTokens.GlowHeadShare);
        }

        /// <summary>One revolution in seconds, with a non-positive value repaired.</summary>
        private double EffectiveGlowSeconds()
        {
            double seconds = GlowSeconds;
            return double.IsNaN(seconds) || seconds <= 0 ? UiTokens.GlowSeconds : seconds;
        }

        /// <summary>
        /// The tail fraction every layer is built from, clamped to a usable range:
        /// a fraction of 1 would make the tail's dash as long as the whole ring,
        /// so that layer would have no gap left to travel through.
        /// </summary>
        private double EffectiveGlowTailFraction()
        {
            double fraction = GlowTailFraction;
            if (double.IsNaN(fraction) || fraction <= 0) fraction = UiTokens.GlowTailFraction;
            if (fraction > UiTokens.GlowMaxTailFraction) fraction = UiTokens.GlowMaxTailFraction;
            return fraction;
        }

        private void ApplyGlowAnimation(Path path, double share)
        {
            var geometry = path.Data as RectangleGeometry;
            if (geometry == null || path.StrokeThickness <= 0) return;

            double thickness = path.StrokeThickness;
            double perimeter = RingPerimeter(geometry);
            double periodUnits = perimeter / thickness;

            double dashUnits = EffectiveGlowTailFraction() * perimeter * share / thickness;
            // Every layer must keep a gap, or StrokeDashOffset has nothing to
            // move through and the layer silently disappears from the comet.
            if (dashUnits >= periodUnits) dashUnits = periodUnits * UiTokens.GlowMaxTailFraction;
            if (dashUnits <= 0) return;

            path.StrokeDashArray = new DoubleCollection { dashUnits, periodUnits - dashUnits };

            var animation = new DoubleAnimation
            {
                From = dashUnits,
                To = dashUnits - periodUnits,
                Duration = new Duration(TimeSpan.FromSeconds(EffectiveGlowSeconds())),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = null
            };

            _glowAnimations[path] = animation;
            path.BeginAnimation(Shape.StrokeDashOffsetProperty, animation);
        }

        #endregion

        #region Input forwarding

        private void LockButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            LockClicked?.Invoke();
            e.Handled = true;
        }

        private void PinButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            PinClicked?.Invoke();
            e.Handled = true;
        }

        private void TitleChevron_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Remember this press so a release over the chevron after a press
            // elsewhere never opens the menu.
            _titleChevronPressed = true;
            e.Handled = true; // never start a header drag from the chevron
        }

        private void TitleChevron_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_titleChevronPressed) return;

            _titleChevronPressed = false;
            TitleMenuClicked?.Invoke();
        }

        private void TitleChevron_MouseEnter(object sender, MouseEventArgs e)
        {
            TitleChevron.Background = ThemeManager.SurfaceHoverBrush;
        }

        private void TitleChevron_MouseLeave(object sender, MouseEventArgs e)
        {
            TitleChevron.Background = Brushes.Transparent;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (_isLocked)
            {
                // Locked: the panel cannot move - flash the lock as feedback.
                FlashLockIndicator();
                e.Handled = true;
                return;
            }

            // Capture first: Mouse.Capture re-enters WPF input dispatch and can
            // synchronously pump a pending MouseMove into Header_MouseMove, so
            // nothing may forward a drag before the host holds its anchor.
            // _isDragActive flips to true only after HeaderDragStarted: a move
            // pumped during the capture sees false and is dropped.
            Mouse.Capture(Header);
            Mouse.OverrideCursor = CursorHelper.GrabHand;
            HeaderDragStarted?.Invoke(ScreenCursor());
            _isDragActive = true;
            e.Handled = true;
        }

        private void Header_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragActive || e.LeftButton != MouseButtonState.Pressed) return;
            if (_isLocked || !ReferenceEquals(Mouse.Captured, Header)) return;

            HeaderDragMoved?.Invoke(ScreenCursor());
            e.Handled = true;
        }

        private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !_isDragActive) return;

            _isDragActive = false;
            Mouse.Capture(null);
            Mouse.OverrideCursor = null;
            HeaderDragEnded?.Invoke(ScreenCursor());
            e.Handled = true;
        }

        private void Header_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            HeaderRightClicked?.Invoke();
            e.Handled = true;
        }

        private void ResizeBand_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || _isLocked) return;

            var element = sender as FrameworkElement;
            WidgetResizeEdge edge;
            if (element == null || !Enum.TryParse(element.Tag as string, out edge)) return;

            // Capture first: Mouse.Capture re-enters WPF input dispatch and can
            // synchronously pump a pending MouseMove into ResizeBand_MouseMove,
            // so nothing may forward a resize before the host holds its anchor.
            // _isResizeActive flips to true only after ResizeStarted: a move
            // pumped during the capture sees false and is dropped. Same
            // ordering as the header drag.
            Mouse.Capture(element);
            ResizeStarted?.Invoke(edge, ScreenCursor());
            _isResizeActive = true;
            e.Handled = true;
        }

        private void ResizeBand_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizeActive || e.LeftButton != MouseButtonState.Pressed) return;
            if (!ReferenceEquals(Mouse.Captured, sender)) return;

            ResizeMoved?.Invoke(ScreenCursor());
            e.Handled = true;
        }

        private void ResizeBand_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !_isResizeActive) return;

            _isResizeActive = false;
            Mouse.Capture(null);
            ResizeEnded?.Invoke(ScreenCursor());
            e.Handled = true;
        }

        private void ResizeGrip_MouseEnter(object sender, MouseEventArgs e)
        {
            ResizeGripGlyph.Opacity = 0.7;
        }

        private void ResizeGrip_MouseLeave(object sender, MouseEventArgs e)
        {
            ResizeGripGlyph.Opacity = 0.35;
        }

        private static Point ScreenCursor()
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            return new Point(cursor.X, cursor.Y);
        }

        #endregion
    }
}
