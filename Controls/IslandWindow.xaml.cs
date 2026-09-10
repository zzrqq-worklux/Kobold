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

namespace Kobold.Controls
{
    /// <summary>
    /// Top-center "Dynamic Island" launcher.
    /// Collapsed it is a small pill; hovering expands it into a row of widget
    /// tiles. It is the sole entry point to widget panels.
    /// </summary>
    public partial class IslandWindow : Window
    {
        private const int AnimationMs = 200;
        private const int ShadowPadding = 16;
        private const int TileIconSize = 32;

        private static readonly SolidColorBrush CollapsedBrush = Freeze(Color.FromArgb(0xCC, 0x20, 0x20, 0x20));
        private static readonly SolidColorBrush ExpandedBrush = Freeze(Color.FromArgb(0xE6, 0x20, 0x20, 0x20));
        private static readonly SolidColorBrush TileHoverBrush = Freeze(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        private bool _isExpanded;
        private List<FolderData> _folders = new List<FolderData>();
        private readonly DispatcherTimer _collapseTimer;
        private readonly DispatcherTimer _leaveTimer;

        /// <summary>Raised with the folder id when a widget tile is clicked.</summary>
        public event Action<string> WidgetActivated;

        public IslandWindow()
        {
            InitializeComponent();

            _collapseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(WidgetConstants.ISLAND_COLLAPSE_DELAY_MS)
            };
            _collapseTimer.Tick += (s, e) => Collapse();

            _leaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(WidgetConstants.ISLAND_LEAVE_DELAY_MS)
            };
            _leaveTimer.Tick += (s, e) => Collapse();

            Closed += (s, e) =>
            {
                _collapseTimer.Stop();
                _leaveTimer.Stop();
            };

            UpdateWindowGeometry();
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
            if (!_isExpanded) return;
            _collapseTimer.Stop();
            _leaveTimer.Start();
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isExpanded) RestartIdleTimer();
        }

        private void Expand()
        {
            if (_isExpanded) return;
            _isExpanded = true;

            PillShape.Fill = ExpandedBrush;
            double width = GetExpandedWidth();
            AnimateTo(width, WidgetConstants.ISLAND_EXPANDED_HEIGHT, width, WidgetConstants.ISLAND_EXPANDED_HEIGHT);
            ShowTiles();

            RestartIdleTimer();
        }

        /// <summary>Expands the island on demand (tray / second-instance activation).</summary>
        public void ShowIsland()
        {
            Expand();
        }

        private void Collapse()
        {
            _collapseTimer.Stop();
            _leaveTimer.Stop();
            if (!_isExpanded) return;
            _isExpanded = false;

            PillShape.Fill = CollapsedBrush;
            HideTiles();
            AnimateTo(WidgetConstants.ISLAND_HOVER_WIDTH, WidgetConstants.ISLAND_HOVER_HEIGHT,
                      WidgetConstants.ISLAND_PILL_WIDTH, WidgetConstants.ISLAND_PILL_HEIGHT);
        }

        private void RestartIdleTimer()
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        private void ShowTiles()
        {
            TilePanel.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(140)))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            TilePanel.BeginAnimation(OpacityProperty, fade);
        }

        private void HideTiles()
        {
            TilePanel.BeginAnimation(OpacityProperty, null);
            TilePanel.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region Tiles

        private void RebuildTiles()
        {
            TilePanel.Children.Clear();

            string iconStyle = WidgetManager.Instance.Config.IconStyle ?? "classic";
            foreach (var folder in _folders)
            {
                TilePanel.Children.Add(CreateTile(folder, iconStyle));
            }
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

            tile.MouseEnter += (s, e) => tile.Background = TileHoverBrush;
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
            int count = Math.Max(1, _folders.Count);
            return 24 + count * (WidgetConstants.ISLAND_TILE_SIZE + WidgetConstants.ISLAND_TILE_GAP);
        }

        /// <summary>
        /// Sizes the (never-animated) window so it can host the largest state plus
        /// room for the drop shadow, and keeps it glued to the top-center of the screen.
        /// </summary>
        private void UpdateWindowGeometry()
        {
            double widest = Math.Max(WidgetConstants.ISLAND_HOVER_WIDTH, GetExpandedWidth());
            Width = widest + 2 * ShadowPadding;
            Height = WidgetConstants.ISLAND_EXPANDED_HEIGHT + ShadowPadding;
            Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
            Top = 0;
        }

        /// <summary>
        /// Animates the inner hit-zone and pill between collapsed/expanded sizes.
        /// Only content geometry changes - the window itself never resizes, so the
        /// animation stays on WPF's render path instead of spamming SetWindowPos.
        /// </summary>
        private void AnimateTo(double zoneWidth, double zoneHeight, double pillWidth, double pillHeight)
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(AnimationMs));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            double radius = pillHeight / 2.0;

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

        private static SolidColorBrush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        #endregion
    }
}
