using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kobold.Core;
using Kobold.Controls.IconRenderers;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// Top-center "Dynamic Island" launcher.
    /// Collapsed it is a small pill; hovering expands it into a row of widget
    /// tiles. It is the sole entry point to widget panels.
    /// </summary>
    public partial class IslandWindow : Window
    {
        private const int ShadowPadding = 16;
        private const int TileIconSize = 32;
        private const double IslandDragThreshold = 4;

        private bool _isExpanded;
        private List<FolderData> _folders = new List<FolderData>();
        private readonly DispatcherTimer _leaveTimer;

        // Delays the desktop-tile single click so a double click can open the folder
        private readonly DispatcherTimer _desktopClickTimer;

        // Horizontal drag of the pill (only the capsule background, not the tiles)
        private bool _isDraggingIsland;
        private Point _islandDragCursor;
        private Point _islandDragLastCursor;
        private double _islandDragDpi = 1.0;

        /// <summary>Raised with the folder id when a widget tile is clicked.</summary>
        public event Action<string> WidgetActivated;

        public IslandWindow()
        {
            InitializeComponent();

            _leaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(GetCollapseDelayMs())
            };
            _leaveTimer.Tick += (s, e) => Collapse();

            ThemeManager.ThemeChanged += OnThemeChanged;
            Closed += (s, e) =>
            {
                _leaveTimer.Stop();
                ThemeManager.ThemeChanged -= OnThemeChanged;
            };

            PillShape.Cursor = CursorHelper.OpenHand;
            PillShape.LostMouseCapture += (s, e) => Mouse.OverrideCursor = null;

            DesktopTile.MouseEnter += (s, e) => DesktopTile.Background = ThemeManager.IslandHoverBrush;
            DesktopTile.MouseLeave += (s, e) => DesktopTile.Background = Brushes.Transparent;
            DesktopTile.MouseLeftButtonDown += DesktopTile_MouseLeftButtonDown;

            SettingsTile.MouseEnter += (s, e) => SettingsTile.Background = ThemeManager.IslandHoverBrush;
            SettingsTile.MouseLeave += (s, e) => SettingsTile.Background = Brushes.Transparent;
            SettingsTile.MouseLeftButtonDown += (s, e) =>
            {
                WidgetManager.Instance.ShowSettings();
                e.Handled = true;
            };

            AddTile.MouseEnter += (s, e) => AddTile.Background = ThemeManager.IslandHoverBrush;
            AddTile.MouseLeave += (s, e) => AddTile.Background = Brushes.Transparent;
            AddTile.MouseLeftButtonDown += (s, e) =>
            {
                WidgetManager.Instance.CreateWidgetWithDefaults();
                e.Handled = true;
            };

            _desktopClickTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime)
            };
            _desktopClickTimer.Tick += (s, e) =>
            {
                _desktopClickTimer.Stop();
                ToggleDesktopIcons();
            };
            UpdateDesktopTile();

            UpdateWindowGeometry();
        }

        /// <summary>Re-reads the persisted island settings (collapse delay + position).</summary>
        public void ApplySettings()
        {
            _leaveTimer.Interval = TimeSpan.FromMilliseconds(GetCollapseDelayMs());
            UpdateWindowGeometry();
        }

        private void OnThemeChanged()
        {
            Dispatcher.Invoke(() =>
            {
                PillShape.Fill = _isExpanded
                    ? ThemeManager.IslandBackgroundExpandedBrush
                    : ThemeManager.IslandBackgroundBrush;
            });
        }

        private static int GetCollapseDelayMs()
        {
            double seconds = 1.0;
            try { seconds = WidgetManager.Instance.Config.IslandCollapseDelay; }
            catch { }
            if (double.IsNaN(seconds)) seconds = 1.0;
            seconds = Math.Max(0.3, Math.Min(5.0, seconds));
            return (int)(seconds * 1000);
        }

        /// <summary>Y coordinate (DIP) where a panel opened from the island should start.</summary>
        public double PanelTop => WidgetConstants.ISLAND_EXPANDED_HEIGHT + WidgetConstants.ISLAND_PANEL_GAP;

        /// <summary>Feeds the widget list rendered as tiles when expanded.</summary>
        public void SetWidgets(IEnumerable<FolderData> folders)
        {
            _folders = folders.ToList();
            RebuildTiles();
            UpdateWindowGeometry();

            if (_isExpanded)
            {
                double width = GetExpandedWidth();
                AnimateTo(width, WidgetConstants.ISLAND_EXPANDED_HEIGHT, width, WidgetConstants.ISLAND_EXPANDED_HEIGHT);
            }
        }

        #region Hover State Machine

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            _leaveTimer.Stop();
            Expand();
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isExpanded || _isDraggingIsland) return;
            _leaveTimer.Start();
        }

        #region Horizontal Drag (pill background)

        private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            _leaveTimer.Stop();
            var cursor = System.Windows.Forms.Cursor.Position;
            _islandDragCursor = new Point(cursor.X, cursor.Y);
            _islandDragDpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _isDraggingIsland = false;
            PillShape.CaptureMouse();
            Mouse.OverrideCursor = CursorHelper.GrabHand;
            e.Handled = true;
        }

        private void Pill_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !PillShape.IsMouseCaptured) return;

            var cursor = System.Windows.Forms.Cursor.Position;
            double dx = (cursor.X - _islandDragCursor.X) / _islandDragDpi;
            if (!_isDraggingIsland)
            {
                if (Math.Abs(dx) < IslandDragThreshold) return;
                _isDraggingIsland = true;
                _islandDragLastCursor = _islandDragCursor;
            }

            // Per-step deltas with a fresh DPI reading keep the island under the
            // cursor across monitors with different scaling.
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double step = (cursor.X - _islandDragLastCursor.X) / dpi;
            _islandDragLastCursor = new Point(cursor.X, cursor.Y);

            double centerX = ClampCenterX(Left + Width / 2 + step, Width);
            Left = centerX - Width / 2;
            e.Handled = true;
        }

        private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            PillShape.ReleaseMouseCapture();
            Mouse.OverrideCursor = null;

            if (_isDraggingIsland)
            {
                _isDraggingIsland = false;
                try
                {
                    // Remember the pill center so it survives expanded-width changes.
                    WidgetManager.Instance.Config.IslandX = Left + Width / 2;
                    WidgetManager.Instance.SaveConfig();
                }
                catch { }
            }
            e.Handled = true;
        }

        #endregion

        #region Widget Row Scrolling

        private void IslandBody_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_isExpanded || TileScroller.ScrollableWidth <= 0) return;

            int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
            ScrollWidgets(e.Delta > 0 ? -notches : notches);
            e.Handled = true;
        }

        /// <summary>
        /// Scrolls the widget row by whole tiles, clamping at both ends. A row
        /// that fits on screen does not move.
        /// </summary>
        public void ScrollWidgets(int notches)
        {
            if (notches == 0) return;

            TileScroller.ScrollToHorizontalOffset(IslandLayout.ScrollTarget(
                TileScroller.HorizontalOffset, notches, TileScroller.ScrollableWidth));
        }

        #endregion

        private void Expand()
        {
            if (_isExpanded) return;
            _isExpanded = true;

            UpdateDesktopTile();
            AddTile.ToolTip = Localization.Get("Island_AddWidget");
            SettingsTile.ToolTip = Localization.Get("Settings_Title");
            PillShape.Fill = ThemeManager.IslandBackgroundExpandedBrush;
            double width = GetExpandedWidth();
            AnimateTo(width, WidgetConstants.ISLAND_EXPANDED_HEIGHT, width, WidgetConstants.ISLAND_EXPANDED_HEIGHT);
            ShowTiles();
        }

        /// <summary>Expands the island on demand (tray / second-instance activation).</summary>
        public void ShowIsland()
        {
            Expand();
        }

        private void Collapse()
        {
            _leaveTimer.Stop();
            if (!_isExpanded) return;
            _isExpanded = false;

            PillShape.Fill = ThemeManager.IslandBackgroundBrush;
            HideTiles();
            // The next expansion starts at the first tile again.
            TileScroller.ScrollToHorizontalOffset(0);
            AnimateTo(WidgetConstants.ISLAND_HOVER_WIDTH, WidgetConstants.ISLAND_HOVER_HEIGHT,
                      WidgetConstants.ISLAND_PILL_WIDTH, WidgetConstants.ISLAND_PILL_HEIGHT);
        }

        private void ShowTiles()
        {
            TileScroller.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(UiTokens.DurationNormalMs)))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            TilePanel.BeginAnimation(OpacityProperty, fade);
        }

        private void HideTiles()
        {
            TilePanel.BeginAnimation(OpacityProperty, null);
            TileScroller.Visibility = Visibility.Hidden; // keep measurable for width calc
        }

        #endregion

        #region Tiles

        private void RebuildTiles()
        {
            // Fixed entries: desktop (0) + divider (1); widget tiles are
            // inserted at index 2, and divider + add + settings stay last.
            while (TilePanel.Children.Count > 5)
            {
                TilePanel.Children.RemoveAt(2);
            }

            string iconStyle = WidgetManager.Instance.Config.IconStyle ?? "classic";
            int insertAt = 2;
            foreach (var folder in _folders)
            {
                TilePanel.Children.Insert(insertAt++, CreateTile(folder, iconStyle));
            }

            // Without widgets the trailing divider would sit right next to the
            // desktop divider - hide it so the entries stay separated once.
            SettingsDivider.Visibility = _folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DesktopTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2)
            {
                // Double click opens the folder; cancel the pending single-click toggle.
                _desktopClickTimer.Stop();
                OpenDesktopFolder();
            }
            else
            {
                _desktopClickTimer.Stop();
                _desktopClickTimer.Start();
            }
            e.Handled = true;
        }

        private void OpenDesktopFolder()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    UseShellExecute = true
                });
            }
            catch { /* Explorer unavailable - nothing else to do */ }
        }

        private void ToggleDesktopIcons()
        {
            bool hide = !WidgetManager.Instance.Config.HideDesktopIcons;
            if (!WidgetManager.Instance.SetHideDesktopIcons(hide))
            {
                MessageBox.Show(Localization.Get("Dialog_HideIconsFailed"), "Kobold",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            UpdateDesktopTile();
        }

        /// <summary>Reflects the hide-desktop-icons state on the desktop tile.</summary>
        private void UpdateDesktopTile()
        {
            bool hidden = WidgetManager.Instance.Config.HideDesktopIcons;
            DesktopSlash.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;

            string action = Localization.Get(hidden ? "Island_DesktopClickShow" : "Island_DesktopClickHide");
            DesktopTile.ToolTip = action + " · " + Localization.Get("Island_DesktopOpenHint");
        }

        private Border CreateTile(FolderData folder, string iconStyle)
        {
            var iconCanvas = new Canvas { Width = 64, Height = 64 };
            IconRendererFactory.Create(iconStyle).Render(iconCanvas, folder.Color);

            var tile = new Border
            {
                Width = WidgetConstants.ISLAND_TILE_SIZE,
                Height = WidgetConstants.ISLAND_TILE_SIZE,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = folder.Name,
                Margin = new Thickness(0, 0, WidgetConstants.ISLAND_TILE_GAP, 0),
                Child = new Viewbox { Width = TileIconSize, Height = TileIconSize, Child = iconCanvas }
            };

            tile.MouseEnter += (s, e) => tile.Background = ThemeManager.IslandHoverBrush;
            tile.MouseLeave += (s, e) => tile.Background = Brushes.Transparent;
            tile.MouseLeftButtonUp += (s, e) => ActivateTile(folder.Id);

            return tile;
        }

        private void ActivateTile(string folderId)
        {
            // Keep the island open so several panels can be toggled without re-hovering.
            WidgetActivated?.Invoke(folderId);
        }

        #endregion

        #region Geometry & Animation

        private double GetExpandedWidth()
        {
            // Capped to a quarter of the screen so the expanded island cannot
            // blanket the top of the display (browser tabs live there and a
            // stray click would toggle a panel); the row scrolls past the cap.
            return Math.Min(
                IslandLayout.ContentWidth(_folders.Count),
                IslandLayout.MaxWidth(SystemParameters.PrimaryScreenWidth));
        }

        /// <summary>
        /// Sizes the (never-animated) window so it can host the largest state plus
        /// room for the drop shadow, and places it at the remembered horizontal
        /// position (centered when none is set), always glued to the top.
        /// </summary>
        private void UpdateWindowGeometry()
        {
            double widest = Math.Max(WidgetConstants.ISLAND_HOVER_WIDTH, GetExpandedWidth());
            Width = widest + 2 * ShadowPadding;
            Height = WidgetConstants.ISLAND_EXPANDED_HEIGHT + ShadowPadding;

            double centerX = GetSavedCenterX() ?? (SystemParameters.PrimaryScreenWidth / 2);
            Left = ClampCenterX(centerX, Width) - Width / 2;
            Top = 0;
        }

        private static double? GetSavedCenterX()
        {
            try { return WidgetManager.Instance.Config.IslandX; }
            catch { return null; }
        }

        /// <summary>Keeps the whole island window (including shadow) on screen.</summary>
        private static double ClampCenterX(double centerX, double windowWidth)
        {
            double half = windowWidth / 2;
            double screen = SystemParameters.PrimaryScreenWidth;
            return Math.Max(half, Math.Min(centerX, screen - half));
        }

        /// <summary>
        /// Animates the inner hit-zone and pill between collapsed/expanded sizes.
        /// Only content geometry changes - the window itself never resizes, so the
        /// animation stays on WPF's render path instead of spamming SetWindowPos.
        /// </summary>
        private void AnimateTo(double zoneWidth, double zoneHeight, double pillWidth, double pillHeight)
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(UiTokens.DurationSlowMs));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            // Same corner radius as the expanded panel; the short collapsed pill
            // stays a capsule because the radius is capped at half its height.
            double radius = Math.Min(pillHeight / 2.0, WidgetConstants.PANEL_CORNER_RADIUS);

            double fromZoneW = IslandBody.Width, fromZoneH = IslandBody.Height;
            double fromPillW = PillShape.Width, fromPillH = PillShape.Height;
            double fromRadius = PillShape.RadiusY;

            IslandBody.Width = zoneWidth;
            IslandBody.Height = zoneHeight;
            PillShape.Width = pillWidth;
            PillShape.Height = pillHeight;
            PillShape.RadiusX = radius;
            PillShape.RadiusY = radius;

            IslandBody.BeginAnimation(WidthProperty, From(fromZoneW, zoneWidth, duration, ease));
            IslandBody.BeginAnimation(HeightProperty, From(fromZoneH, zoneHeight, duration, ease));
            PillShape.BeginAnimation(WidthProperty, From(fromPillW, pillWidth, duration, ease));
            PillShape.BeginAnimation(HeightProperty, From(fromPillH, pillHeight, duration, ease));
            PillShape.BeginAnimation(Rectangle.RadiusXProperty, From(fromRadius, radius, duration, ease));
            PillShape.BeginAnimation(Rectangle.RadiusYProperty, From(fromRadius, radius, duration, ease));
        }

        private static DoubleAnimation From(double from, double to, Duration duration, IEasingFunction ease)
        {
            return new DoubleAnimation(from, to, duration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
        }

        #endregion
    }
}
