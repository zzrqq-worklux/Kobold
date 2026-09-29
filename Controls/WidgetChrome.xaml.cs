using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// WidgetChrome - the shared, content-agnostic panel shell: rounded border
    /// + shadow, a 40px header (title / lock / pin / drag / right-click) and
    /// the HeaderLeading / HeaderTrailing / ContentHost / FooterHost slots.
    ///
    /// The chrome owns no widget data and moves no window: header drags are
    /// forwarded as events carrying the screen cursor, and the hosting window
    /// performs the actual move.
    /// </summary>
    public partial class WidgetChrome : UserControl
    {
        private const double MIN_PANEL_OPACITY = 0.2;
        private const double MAX_PANEL_OPACITY = 1.0;

        private bool _isLocked;
        private bool _isPinned;
        private bool _isDragActive;
        private double _panelOpacity = MAX_PANEL_OPACITY;

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

        public WidgetChrome()
        {
            InitializeComponent();

            // Never leave the grabbing cursor stuck if capture is lost mid-drag.
            Header.LostMouseCapture += (s, e) => Mouse.OverrideCursor = null;

            ApplyTheme();
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
        /// Repaints everything theme-dependent: panel backdrop, header tint,
        /// title and the lock/pin icon states.
        /// </summary>
        public void ApplyTheme()
        {
            UpdateLockVisual();
            UpdatePinVisual();
            UpdatePanelBackground();
            TitleText.Foreground = ThemeManager.TextBrush;
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
        /// Mirrors FolderWidget's panel colours with the theme's neutral panel
        /// surface (the chrome carries no widget colour of its own).
        /// </summary>
        private void UpdatePanelBackground()
        {
            bool isDark = ThemeManager.IsDarkTheme;
            Color baseColor = Utils.HexToColor(isDark ? UiTokens.Dark.Surface : UiTokens.Light.Surface);
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

            _isDragActive = true;
            Mouse.Capture(Header);
            Mouse.OverrideCursor = CursorHelper.GrabHand;
            HeaderDragStarted?.Invoke(ScreenCursor());
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

        private static Point ScreenCursor()
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            return new Point(cursor.X, cursor.Y);
        }

        #endregion
    }
}
