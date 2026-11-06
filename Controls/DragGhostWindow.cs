using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.Controls
{
    /// <summary>
    /// The Explorer-folder-style ghost that follows the cursor while a tab is
    /// dragged outside its group (detach intent): a miniature of the standalone
    /// component at its pre-group panel size, rendered offscreen
    /// (<see cref="WidgetGhostRenderer"/>) and shown in a chrome-like shell,
    /// with the dragged tab's live view as the fallback. A top-level,
    /// non-activating, click-through, non-taskbar overlay; the group window
    /// creates it for the outside-drag gesture only and closes it on every exit
    /// path.
    /// </summary>
    internal sealed class DragGhostWindow : Window
    {
        /// <summary>Maximum shell size in DIP; the ghost never grows past this.</summary>
        internal const double GhostMaxWidth = 320;
        internal const double GhostMaxHeight = 230;

        /// <summary>Cursor lower-right offset in DIP.</summary>
        internal const double CursorOffset = 16;

        private const double HEADER_HEIGHT = 28;
        private const double SHELL_RADIUS = 12;

        private readonly Border _shell;
        private readonly Border _header;
        private readonly RowDefinition _headerRow;
        private readonly Border _body;
        private readonly Image _previewImage;
        private readonly TextBlock _label;
        private readonly Ellipse _accent;
        private readonly IconPresenter _kindIcon;

        private VisualBrush _previewBrush;
        private Window _source;

        internal DragGhostWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            Opacity = 0.7;
            Width = GhostMaxWidth;
            Height = GhostMaxHeight;

            _accent = new Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = ThemeManager.AccentBrush
            };

            _kindIcon = new IconPresenter
            {
                Size = 12,
                StrokeThickness = 1.7,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Brush = ThemeManager.SecondaryTextBrush
            };

            _label = new TextBlock
            {
                FontFamily = ThemeManager.DisplayFontFamily,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeManager.TextBrush
            };

            var headerContent = new Grid { VerticalAlignment = VerticalAlignment.Center };
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_accent, 0);
            Grid.SetColumn(_kindIcon, 1);
            Grid.SetColumn(_label, 2);
            headerContent.Children.Add(_accent);
            headerContent.Children.Add(_kindIcon);
            headerContent.Children.Add(_label);

            _header = new Border
            {
                Height = HEADER_HEIGHT,
                Padding = new Thickness(10, 0, 10, 0),
                Background = ThemeManager.Surface2Brush,
                CornerRadius = new CornerRadius(SHELL_RADIUS, SHELL_RADIUS, 0, 0),
                Child = headerContent
            };

            _previewImage = new Image
            {
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true
            };

            _body = new Border
            {
                Background = ThemeManager.BackgroundBrush,
                CornerRadius = new CornerRadius(0, 0, SHELL_RADIUS, SHELL_RADIUS),
                Child = _previewImage
            };

            var layout = new Grid();
            _headerRow = new RowDefinition { Height = new GridLength(HEADER_HEIGHT) };
            layout.RowDefinitions.Add(_headerRow);
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_header, 0);
            Grid.SetRow(_body, 1);
            layout.Children.Add(_header);
            layout.Children.Add(_body);

            _shell = new Border
            {
                CornerRadius = new CornerRadius(SHELL_RADIUS),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeManager.PanelBorderBrush,
                Background = ThemeManager.BackgroundBrush,
                Child = layout,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 2,
                    Opacity = 0.35,
                    Color = Colors.Black,
                    RenderingBias = RenderingBias.Performance
                }
            };
            Content = _shell;

            SourceInitialized += (s, e) =>
            {
                WindowSwitcher.HideFromSwitcher(this);
                WindowSwitcher.MakeClickThrough(this);
            };
            Closing += (s, e) => WindowSwitcher.ReleaseOwner(this);
        }

        /// <summary>The live element the fallback thumbnail is painted from (test/seam).</summary>
        internal FrameworkElement PreviewSource { get; private set; }

        /// <summary>The live fallback brush (null on the bitmap path) (test/seam).</summary>
        internal VisualBrush PreviewBrush => _previewBrush;

        /// <summary>The frozen original-size render (null on the fallback path) (test/seam).</summary>
        internal ImageSource PreviewBitmap { get; private set; }

        /// <summary>The body image's stretch mode (test/seam).</summary>
        internal Stretch BitmapStretch => _previewImage.Stretch;

        /// <summary>The current scaled header height (test/seam).</summary>
        internal double HeaderHeight => _header.Height;

        /// <summary>The screen cursor point the last placement used (test/seam).</summary>
        internal Point LastCursorScreen { get; private set; }

        /// <summary>
        /// Binds the ghost to the dragged tab's preview (the rendered bitmap,
        /// or the live view as a fallback), sizes it from the member's own
        /// pre-group panel size, places it at the cursor's lower-right and
        /// shows it (non-activating).
        /// </summary>
        internal void ShowFor(Window source, Point cursorScreen,
            FrameworkElement liveContent, WidgetData member, ImageSource previewBitmap)
        {
            Prepare(source, cursorScreen, liveContent, member, previewBitmap);
            if (!IsVisible) Show();
        }

        /// <summary>
        /// The bind/size/position half of <see cref="ShowFor"/>, without
        /// showing the window. Internal so the checks can pin the fallback
        /// without flashing a ghost on the desktop.
        /// </summary>
        internal void Prepare(Window source, Point cursorScreen,
            FrameworkElement liveContent, WidgetData member, ImageSource previewBitmap)
        {
            _source = source;
            UpdateMember(member);
            UpdatePreview(liveContent, previewBitmap);
            UpdateSize(member);
            PositionAt(cursorScreen);
        }

        /// <summary>Follows every drag move while the ghost is visible.</summary>
        internal void Follow(Point cursorScreen)
        {
            if (!IsVisible) return;
            PositionAt(cursorScreen);
        }

        /// <summary>
        /// Drops the bitmap, the live-view brush and its source before the
        /// window closes, so no render subscription can outlive the ghost (or
        /// race the group window's teardown at application shutdown).
        /// </summary>
        internal void DetachPreview()
        {
            _body.Background = ThemeManager.BackgroundBrush;
            _previewImage.Source = null;
            _previewImage.Visibility = Visibility.Collapsed;
            _previewBrush = null;
            PreviewSource = null;
            PreviewBitmap = null;
        }

        private void UpdateMember(WidgetData member)
        {
            Color accent = Utils.HexToColor(member == null || string.IsNullOrEmpty(member.Color)
                ? UiTokens.DefaultFolderColor
                : member.Color);
            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();

            _accent.Fill = accentBrush;
            _kindIcon.Icon = WidgetGroupTabStrip.IconFor(member);
            _kindIcon.Brush = accentBrush;
            _shell.BorderBrush = accentBrush;
            _label.Foreground = ThemeManager.TextBrush;

            string name = member == null ? null : member.Name;
            _label.Text = string.IsNullOrWhiteSpace(name)
                ? WidgetGroupTabStrip.KindLabel(member)
                : name;
        }

        private void UpdatePreview(FrameworkElement liveContent, ImageSource previewBitmap)
        {
            if (previewBitmap != null)
            {
                // The offscreen original-size render: the body shows the real
                // standalone component, letterboxed uniformly, never distorted.
                PreviewBitmap = previewBitmap;
                PreviewSource = null;
                _previewBrush = null;
                _body.Background = ThemeManager.BackgroundBrush;
                _previewImage.Source = previewBitmap;
                _previewImage.Visibility = Visibility.Visible;
                return;
            }

            // Render unavailable: fall back to the dragged tab's live view.
            PreviewBitmap = null;
            PreviewSource = liveContent;
            _previewImage.Source = null;
            _previewImage.Visibility = Visibility.Collapsed;
            _body.Background = ThemeManager.BackgroundBrush;
            if (liveContent != null)
            {
                _previewBrush = new VisualBrush(liveContent)
                {
                    Stretch = Stretch.Uniform,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                _body.Background = _previewBrush;
            }
            else
            {
                _previewBrush = null;
            }
        }

        /// <summary>
        /// Sizes the mini component from the member's own pre-group panel size:
        /// uniform scale into 320x230 (never upscaled), with the standalone
        /// 40 DIP header scaled by the same factor and floored for readability.
        /// </summary>
        private void UpdateSize(WidgetData member)
        {
            var (panelWidth, panelHeight) = WidgetGhostRenderer.OriginalPanelSize(member);
            double scale = Math.Min(1.0, Math.Min(GhostMaxWidth / panelWidth, GhostMaxHeight / panelHeight));
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1.0;

            double outerWidth = Math.Max(1, panelWidth * scale);
            double outerHeight = Math.Max(1, panelHeight * scale);
            double header = Math.Min(outerHeight - 1, Math.Max(18.0, WidgetConstants.HEADER_HEIGHT * scale));
            if (header < 1) header = 1;

            Width = outerWidth;
            Height = outerHeight;
            _header.Height = header;
            _headerRow.Height = new GridLength(header);
        }

        private void PositionAt(Point cursorScreen)
        {
            LastCursorScreen = cursorScreen;

            double outerWidth = Width;
            double outerHeight = Height;
            if (double.IsNaN(outerWidth) || outerWidth <= 0) outerWidth = GhostMaxWidth;
            if (double.IsNaN(outerHeight) || outerHeight <= 0) outerHeight = GhostMaxHeight;

            MonitorInfo monitor = MonitorInterop.GetMonitorForDevicePoint(cursorScreen);
            if (monitor == null)
            {
                // No monitor data: fall back to the source window's DPI and the
                // primary work area (DIP), still at the cursor's lower-right.
                double fallback = _source == null ? 1.0 : VisualTreeHelper.GetDpi(_source).DpiScaleX;
                if (double.IsNaN(fallback) || double.IsInfinity(fallback) || fallback <= 0) fallback = 1.0;

                Width = outerWidth;
                Height = outerHeight;
                Rect work = SystemParameters.WorkArea;
                Left = Math.Max(work.Left, Math.Min(cursorScreen.X / fallback + CursorOffset, work.Right - outerWidth));
                Top = Math.Max(work.Top, Math.Min(cursorScreen.Y / fallback + CursorOffset, work.Bottom - outerHeight));
                return;
            }

            double scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
            Rect placed = PlaceDevice(cursorScreen, new Size(outerWidth, outerHeight),
                monitor.WorkAreaDevice, scale);
            Rect dip = PanelPlacement.ToDipRect(placed, monitor);

            Left = dip.X;
            Top = dip.Y;
            Width = dip.Width;
            Height = dip.Height;
        }

        /// <summary>
        /// Pure placement: the cursor's lower-right by <see cref="CursorOffset"/>
        /// DIP (scaled to device pixels), clamped into the work area so the
        /// ghost never runs off-screen. Kept internal so the checks can pin the
        /// exact offset/clamp contract.
        /// </summary>
        internal static Rect PlaceDevice(Point cursorDevice, Size sizeDip, Rect workAreaDevice, double scale)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1.0;

            double width = sizeDip.Width * scale;
            double height = sizeDip.Height * scale;
            double x = cursorDevice.X + CursorOffset * scale;
            double y = cursorDevice.Y + CursorOffset * scale;

            if (width >= workAreaDevice.Width) x = workAreaDevice.X;
            else x = Math.Max(workAreaDevice.X, Math.Min(x, workAreaDevice.Right - width));
            if (height >= workAreaDevice.Height) y = workAreaDevice.Y;
            else y = Math.Max(workAreaDevice.Y, Math.Min(y, workAreaDevice.Bottom - height));

            return new Rect(x, y, width, height);
        }
    }
}
