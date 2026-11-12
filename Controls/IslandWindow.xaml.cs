using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kobold.Core;
using Kobold.Core.Icons;
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
        private const double AddMenuWidth = 280;
        private const double AddMenuShadowPadding = 10;

        private bool _isExpanded;
        private List<WidgetData> _widgets = new List<WidgetData>();

        // The rendered row is split into a layout signature and two content
        // signatures. A layout change rebuilds the row; a group-only content
        // change (rename/pin/merge/detach inside the same group) updates the
        // existing Borders in place while the group is collapsed.
        private string _layoutSignature;
        private string _standaloneContentSignature;
        private string _groupContentSignature;

        // The island projection keyed by group id: one folded tile per group
        // with its member descriptors. Replaced wholesale on every SetWidgets
        // so a dissolved group leaves no stale tile behind.
        private readonly List<WidgetGroupTileInfo> _groupTiles = new List<WidgetGroupTileInfo>();
        private readonly Dictionary<string, WidgetGroupTileInfo> _groupTilesById =
            new Dictionary<string, WidgetGroupTileInfo>(StringComparer.Ordinal);

        // The row actually rendered: standalone tiles plus one group tile at
        // the first member's config slot. Other members are hidden.
        private List<ProjectedTile> _projectedTiles = new List<ProjectedTile>();

        // Live panel visibility, keyed by widget id (standalone) or group id
        // (folded group tile). SetOpenState updates the existing tile in place;
        // a rebuild (icon style / widget add/remove) re-applies it.
        private readonly Dictionary<string, bool> _openStates =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _widgetColors =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly DispatcherTimer _leaveTimer;
        private readonly DispatcherTimer _expandTimer;

        // Fades the scroll hint out once the user stops interacting with it.
        private readonly DispatcherTimer _scrollIdleTimer;

        // True while a widget menu opened from a tile is up - the island must
        // not collapse underneath it.
        private bool _menuOpen;

        // The launcher card opened from the add tile; null while it is closed.
        private Popup _addWidgetPopup;

        // Guards the two-way sync between the tile scroller and its hint bar.
        private bool _syncingScrollBar;

        // Delays the desktop-tile single click so a double click can open the folder
        private readonly DispatcherTimer _desktopClickTimer;

        // Horizontal drag of the pill (only the capsule background, not the tiles)
        private bool _isDraggingIsland;
        private Point _islandDragCursor;
        private Point _islandDragLastCursor;
        private double _islandDragDpi = 1.0;

        /// <summary>Raised with the folder id when a widget tile is clicked.</summary>
        public event Action<string> WidgetActivated;

        /// <summary>Raised with the group id when a folded group tile is clicked.</summary>
        internal event Action<string> GroupTileClicked;

        /// <summary>Raised with the folder or group id when a tile is right-clicked.</summary>
        public event Action<string> WidgetMenuRequested;

        public IslandWindow()
        {
            InitializeComponent();

            _leaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(GetCollapseDelayMs())
            };
            _leaveTimer.Tick += (s, e) => Collapse();

            _expandTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(WidgetConstants.ISLAND_EXPAND_DELAY_MS)
            };
            _expandTimer.Tick += (s, e) => { _expandTimer.Stop(); Expand(); };

            _scrollIdleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(WidgetConstants.ISLAND_SCROLLBAR_IDLE_MS)
            };
            _scrollIdleTimer.Tick += (s, e) => FadeScrollBarIfIdle();

            ThemeManager.ThemeChanged += OnThemeChanged;
            SourceInitialized += (s, e) => WindowSwitcher.HideFromSwitcher(this); // stay out of Alt+Tab
            Closed += (s, e) =>
            {
                _leaveTimer.Stop();
                _expandTimer.Stop();
                _scrollIdleTimer.Stop();
                ThemeManager.ThemeChanged -= OnThemeChanged;
                WindowSwitcher.ReleaseOwner(this);
            };

            PillShape.Cursor = CursorHelper.OpenHand;
            PillShape.LostMouseCapture += (s, e) => Mouse.OverrideCursor = null;

            // Keep the hint bar and the tile scroller in step, both ways.
            TileScroller.ScrollChanged += (s, e) => SyncScrollBarFromScroller();
            TileScrollBar.ValueChanged += (s, e) => ScrollFromBar();
            TileScrollBar.MouseEnter += (s, e) => FlashScrollBar();

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
            // Open on mouse-up: a StaysOpen=false popup captures the mouse as
            // soon as it opens, so opening on mouse-down makes the same click's
            // release land "outside" and close the menu immediately.
            AddTile.MouseLeftButtonUp += (s, e) =>
            {
                ShowAddWidgetMenu();
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
            IconStyleManager.Set(WidgetManager.Instance.Config.IconStyle ?? IconStyleIds.Classic);

            UpdateWindowGeometry();
        }

        /// <summary>Re-reads the persisted island settings (collapse delay + position).</summary>
        public void ApplySettings()
        {
            _leaveTimer.Interval = TimeSpan.FromMilliseconds(GetCollapseDelayMs());
            UpdateWindowGeometry();
        }

        /// <summary>Rebuilds the tiles so a changed IconStyle shows immediately.</summary>
        public void RefreshIconStyle()
        {
            IconStyleManager.Set(WidgetManager.Instance.Config.IconStyle ?? IconStyleIds.Classic);
            RebuildTiles();
        }

        private void OnThemeChanged()
        {
            Dispatcher.Invoke(() =>
            {
                PillShape.Fill = _isExpanded
                    ? ThemeManager.IslandBackgroundExpandedBrush
                    : ThemeManager.IslandBackgroundBrush;
                UpdateAllTileOpenStates();
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

        /// <summary>
        /// Horizontal centre (DIP) of the island window - the capsule content is
        /// centred in it, so panels open around the same centre the drag uses.
        /// </summary>
        public double CenterX => Left + Width / 2;

        /// <summary>Steps the island out of the way of a fullscreen app (and back).</summary>
        public void SetTopmost(bool topmost)
        {
            if (Topmost == topmost) return;
            Topmost = topmost;
        }

        /// <summary>The work area (DIP) of the monitor the island currently sits on.</summary>
        private Rect GetWorkArea()
        {
            return MonitorInterop.WorkAreaDipForWindow(this);
        }

        /// <summary>
        /// Feeds the island the widget list and its group projection. A group
        /// with at least two members folds into one tile at its first member's
        /// config slot; its other members are hidden. Standalone widgets keep
        /// one tile each. The optional argument keeps every existing
        /// SetWidgets(list) call source-compatible.
        /// </summary>
        public void SetWidgets(IEnumerable<WidgetData> widgets,
            IReadOnlyList<WidgetGroupTileInfo> groups = null)
        {
            _widgets = widgets?.ToList() ?? new List<WidgetData>();

            _groupTiles.Clear();
            _groupTilesById.Clear();
            if (groups != null)
            {
                foreach (var group in groups)
                {
                    if (group == null || string.IsNullOrEmpty(group.GroupId)) continue;
                    if (group.MemberCount < 2) continue;
                    if (group.Members == null) group.Members = new List<WidgetGroupTileMember>();
                    _groupTiles.Add(group);
                    _groupTilesById[group.GroupId] = group;
                }
            }

            _projectedTiles = BuildProjectedTiles();

            // Drop visibility for hosts that no longer exist, and keep the
            // colours the standalone tile wash/dot need.
            var live = new HashSet<string>(_widgets.Select(w => w.Id), StringComparer.Ordinal);
            foreach (var group in _groupTiles) live.Add(group.GroupId);
            foreach (string id in _openStates.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _openStates.Remove(id);
            }

            _widgetColors.Clear();
            foreach (var widget in _widgets)
            {
                if (widget != null && !string.IsNullOrEmpty(widget.Id))
                    _widgetColors[widget.Id] = widget.Color;
            }

            // A layout change rebuilds the row. A same-group content change
            // (rename/pin/merge/detach) repaints the existing group tile in
            // place; a standalone visual change may rebuild the row.
            string layout = LayoutSignature(_projectedTiles);
            string standaloneContent = StandaloneContentSignature(_projectedTiles);
            string groupContent = GroupContentSignature(_projectedTiles);
            bool layoutChanged = layout != _layoutSignature
                || TilePanel.Children.Count != RenderedSlotCount();
            bool standaloneChanged = standaloneContent != _standaloneContentSignature;
            bool groupChanged = groupContent != _groupContentSignature;
            double middleWidthBefore = TileScroller.Width;

            bool rebuild = layoutChanged || standaloneChanged;
            if (rebuild)
            {
                RebuildTiles();
            }
            else if (groupChanged)
            {
                string iconStyle = WidgetManager.Instance.Config.IconStyle ?? "classic";
                if (!UpdateGroupTilesInPlace(iconStyle)) RebuildTiles();
            }

            // The island must not resize or run its expand animation for an
            // in-place open-state refresh either.
            bool geometryChanged = Math.Abs(TileScroller.Width - middleWidthBefore) > 0.01;
            if (rebuild || geometryChanged) UpdateWindowGeometry();

            if (_isExpanded && (rebuild || geometryChanged))
            {
                double width = GetActiveCapsuleWidth();
                double height = GetActiveCapsuleHeight();
                AnimateTo(width, height, width, height);
                SyncScrollBarFromScroller();
                FlashScrollBar();
            }
        }

        /// <summary>
        /// Mirrors one panel's visibility onto its island tile. Only the tile's
        /// background and dot change; the tile is never rebuilt.
        /// </summary>
        public void SetOpenState(string widgetId, bool isOpen)
        {
            if (string.IsNullOrEmpty(widgetId)) return;

            bool changed;
            if (isOpen)
            {
                changed = !_openStates.ContainsKey(widgetId);
                _openStates[widgetId] = true;
            }
            else
            {
                changed = _openStates.Remove(widgetId);
            }

            if (changed) UpdateTileOpenState(widgetId);
        }

        /// <summary>
        /// Replaces the whole visibility snapshot (startup restore / island
        /// refresh) and repaints the existing tiles in place.
        /// </summary>
        public void SetOpenStates(IEnumerable<string> openIds)
        {
            _openStates.Clear();
            if (openIds != null)
            {
                foreach (string id in openIds)
                {
                    if (!string.IsNullOrEmpty(id)) _openStates[id] = true;
                }
            }

            UpdateAllTileOpenStates();
        }

        #region Hover State Machine

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            _leaveTimer.Stop();
            if (_isExpanded)
            {
                // Coming back to the island brings its scroll hint up again.
                FlashScrollBar();
                return;
            }

            // Rest for a moment before expanding - see ISLAND_EXPAND_DELAY_MS.
            // ShowIsland() (tray / second instance) expands immediately instead.
            _expandTimer.Start();
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            _expandTimer.Stop();
            if (!_isExpanded || _isDraggingIsland || _menuOpen) return;
            _leaveTimer.Start();
        }

        /// <summary>
        /// Suppresses auto-collapse while a widget menu opened from a tile is
        /// up; <see cref="ResumeAutoCollapse"/> re-arms it when the menu closes.
        /// </summary>
        public void PauseAutoCollapse()
        {
            _leaveTimer.Stop();
            _menuOpen = true;
        }

        /// <summary>Resumes the usual auto-collapse once that menu has closed.</summary>
        public void ResumeAutoCollapse()
        {
            _menuOpen = false;
            _leaveTimer.Stop();
            if (!_isExpanded) return;

            // The cursor may have wandered off while the menu was up and the
            // usual MouseLeave was swallowed - collapse if it is outside now.
            var cursor = System.Windows.Forms.Cursor.Position;
            var dpi = VisualTreeHelper.GetDpi(this);
            if (!ScreenGeometry.ContainsPhysicalPoint(
                    new Rect(Left, Top, Width, Height), dpi.DpiScaleX, dpi.DpiScaleY, cursor.X, cursor.Y))
            {
                _leaveTimer.Start();
            }
        }

        #region Horizontal Drag (pill background)

        private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            _leaveTimer.Stop();
            _expandTimer.Stop(); // dragging the pill must not expand it mid-drag
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

            // Immediate feedback clamps the window's centre to the virtual
            // desktop; the drop re-clamps against the island monitor's work
            // area (mouse-up). The capsule content is centred in the window,
            // so the centre is the stable anchor while the window widens for
            // an expanded group.
            double center = CenterX + step;
            Left = IslandLayout.ClampCenter(
                center, Width, SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenWidth) - Width / 2;
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
                    // Snap the window's centre into the island monitor's work
                    // area and remember it. The capsule content is centred in
                    // the window, so the saved centre restores the same spot
                    // regardless of the active width.
                    Rect work = GetWorkArea();
                    double center = IslandLayout.ClampCenter(
                        CenterX, Width, work.Left, work.Width);
                    Left = center - Width / 2;
                    WidgetManager.Instance.Config.IslandX = center;
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
        /// Scrolls the widget tiles by whole tiles, clamping at both ends. A row
        /// that fits does not move.
        /// </summary>
        public void ScrollWidgets(int notches)
        {
            if (notches == 0) return;

            FlashScrollBar();
            TileScroller.ScrollToHorizontalOffset(IslandLayout.ScrollTarget(
                TileScroller.HorizontalOffset, notches, TileScroller.ScrollableWidth));
        }

        /// <summary>
        /// Mirrors the tile scroller onto its hint bar: thumb proportions, value
        /// and whether a bar is needed at all.
        /// </summary>
        private void SyncScrollBarFromScroller()
        {
            if (_syncingScrollBar) return;

            _syncingScrollBar = true;
            try
            {
                TileScrollBar.ViewportSize = TileScroller.ViewportWidth;
                TileScrollBar.Maximum = TileScroller.ScrollableWidth;
                TileScrollBar.Value = TileScroller.HorizontalOffset;
                TileScrollBar.Visibility = _isExpanded && TileScroller.ScrollableWidth > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (TileScrollBar.Visibility != Visibility.Visible) _scrollIdleTimer.Stop();
            }
            finally
            {
                _syncingScrollBar = false;
            }
        }

        /// <summary>Dragging the hint bar scrolls the tiles.</summary>
        private void ScrollFromBar()
        {
            if (_syncingScrollBar) return;
            FlashScrollBar();
            TileScroller.ScrollToHorizontalOffset(TileScrollBar.Value);
        }

        /// <summary>
        /// Brings the scroll hint up and restarts its idle timer. Design: a slim
        /// pill that appears while the user is scrolling or hovering the island
        /// and fades away shortly after they stop.
        /// </summary>
        private void FlashScrollBar()
        {
            if (!_isExpanded || TileScroller.ScrollableWidth <= 0)
            {
                _scrollIdleTimer.Stop();
                return;
            }

            TileScrollBar.IsHitTestVisible = true;
            TileScrollBar.Opacity = 1; // base value: what it settles on after the fade-in
            TileScrollBar.BeginAnimation(OpacityProperty, From(0, 1, FadeDuration(), FadeEase()));
            _scrollIdleTimer.Stop();
            _scrollIdleTimer.Start();
        }

        /// <summary>Fades the hint out unless the pointer is on it (or dragging it).</summary>
        private void FadeScrollBarIfIdle()
        {
            if (TileScrollBar.IsMouseOver || TileScrollBar.IsMouseCaptured)
            {
                _scrollIdleTimer.Start();
                return;
            }

            _scrollIdleTimer.Stop();
            TileScrollBar.IsHitTestVisible = false; // gone means click-through
            TileScrollBar.Opacity = 0;
            TileScrollBar.BeginAnimation(OpacityProperty, From(1, 0, FadeDuration(), FadeEase()));
        }

        private static Duration FadeDuration() =>
            new Duration(TimeSpan.FromMilliseconds(WidgetConstants.ISLAND_SCROLLBAR_FADE_MS));

        private static IEasingFunction FadeEase() => new QuadraticEase { EasingMode = EasingMode.EaseOut };

        #endregion

        private void Expand()
        {
            if (_isExpanded) return;
            _isExpanded = true;

            UpdateDesktopTile();
            AddTile.ToolTip = Localization.Get("Island_AddWidget");
            SettingsTile.ToolTip = Localization.Get("Settings_Title");
            PillShape.Fill = ThemeManager.IslandBackgroundExpandedBrush;
            double width = GetActiveCapsuleWidth();
            double height = GetActiveCapsuleHeight();
            AnimateTo(width, height, width, height);
            ShowTiles();
        }

        /// <summary>Expands the island on demand (tray / second-instance activation).</summary>
        public void ShowIsland()
        {
            Expand();
        }

        /// <summary>True while the island shows its expanded tile row.</summary>
        public bool IsExpanded => _isExpanded;

        /// <summary>Test seam: folds the island without a hover/timer round-trip.</summary>
        internal void CollapseForChecks() => Collapse();

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
            IslandRow.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(UiTokens.DurationNormalMs)))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            IslandRow.BeginAnimation(OpacityProperty, fade);
            SyncScrollBarFromScroller();
            FlashScrollBar();
        }

        private void HideTiles()
        {
            IslandRow.BeginAnimation(OpacityProperty, null);
            IslandRow.Visibility = Visibility.Hidden; // keep measurable for width calc

            _scrollIdleTimer.Stop();
            TileScrollBar.BeginAnimation(OpacityProperty, null);
            TileScrollBar.Opacity = 0;
            TileScrollBar.IsHitTestVisible = false;
            TileScrollBar.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region Tiles

        private void RebuildTiles()
        {
            TilePanel.Children.Clear();

            string iconStyle = WidgetManager.Instance.Config.IconStyle ?? "classic";
            _layoutSignature = LayoutSignature(_projectedTiles);
            _standaloneContentSignature = StandaloneContentSignature(_projectedTiles);
            _groupContentSignature = GroupContentSignature(_projectedTiles);
            foreach (var entry in _projectedTiles)
            {
                if (entry.Group != null)
                {
                    TilePanel.Children.Add(CreateGroupTile(entry.Group, iconStyle));

                    // An expanded group inserts its member icons immediately
                    // after the group tile, before the next projected tile.
                    if (entry.Group.IslandExpanded)
                    {
                        var members = entry.Group.Members ?? new List<WidgetGroupTileMember>();
                        foreach (var member in members)
                            TilePanel.Children.Add(CreateMemberTile(member, entry.Group, iconStyle));
                    }
                }
                else
                {
                    TilePanel.Children.Add(CreateTile(entry.Widget, iconStyle));
                }
            }

            // The middle section measures itself: its content, capped to a share
            // of the island monitor's work area. Anything wider scrolls, with
            // the hint bar below. An expanded group contributes one slot per
            // visible member icon.
            TileScroller.Width = IslandLayout.MiddleViewWidth(RenderedSlotCount(), GetWorkArea().Width);

            // Without tiles the trailing divider would sit right next to the
            // desktop divider - hide it so the entries stay separated once.
            SettingsDivider.Visibility = _projectedTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The number of tiles the row actually draws: one per projected entry
        /// plus one per member icon of an expanded group.
        /// </summary>
        private int RenderedSlotCount()
        {
            int count = 0;
            foreach (var entry in _projectedTiles)
            {
                count += 1;
                if (entry.Group != null && entry.Group.IslandExpanded)
                    count += entry.Group.MemberCount;
            }
            return count;
        }

        /// <summary>
        /// Repaints the group tiles in place when only the group content
        /// changed. Every Border stays; the open outline is re-applied after
        /// the thumbnail rebuild. Returns false when the row no longer matches
        /// the projection or any group is expanded (the member icons break the
        /// one-tile-per-entry index mapping), so the caller rebuilds the row.
        /// </summary>
        private bool UpdateGroupTilesInPlace(string iconStyle)
        {
            if (AnyGroupExpanded()) return false;
            if (TilePanel.Children.Count != _projectedTiles.Count) return false;

            for (int i = 0; i < _projectedTiles.Count; i++)
            {
                var entry = _projectedTiles[i];
                var tile = TilePanel.Children[i] as Border;
                if (tile == null) return false;

                if (entry.Group != null)
                {
                    if (!IsGroupTile(tile)) return false;
                    UpdateGroupTile(tile, entry.Group, iconStyle);
                    ApplyTileOpenState(tile);
                }
                else if (!string.Equals(tile.Tag as string, entry.Widget.Id, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            _groupContentSignature = GroupContentSignature(_projectedTiles);
            return true;
        }

        private bool AnyGroupExpanded()
        {
            foreach (var entry in _projectedTiles)
            {
                if (entry.Group != null && entry.Group.IslandExpanded) return true;
            }
            return false;
        }

        private static bool IsGroupTile(Border tile)
        {
            string tag = tile.Tag as string;
            return tag != null && tag.StartsWith("group:", StringComparison.Ordinal);
        }

        /// <summary>
        /// The rendered row: standalone widgets keep their slot, a group emits
        /// one folded tile at its first member's config slot and hides the rest.
        /// </summary>
        private List<ProjectedTile> BuildProjectedTiles()
        {
            var tiles = new List<ProjectedTile>();
            var emittedGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var widget in _widgets)
            {
                if (!HasWindowKind(widget)) continue;

                WidgetGroupTileInfo group;
                if (!string.IsNullOrEmpty(widget.GroupId)
                    && _groupTilesById.TryGetValue(widget.GroupId, out group))
                {
                    if (emittedGroups.Add(group.GroupId))
                        tiles.Add(new ProjectedTile { Group = group });
                    continue;
                }
                tiles.Add(new ProjectedTile { Widget = widget });
            }
            return tiles;
        }

        /// <summary>Unknown kinds have neither tile nor window; they only stay in the config.</summary>
        private static bool HasWindowKind(WidgetData widget)
        {
            return widget != null && WidgetKindRegistry.Find(widget.Kind) != null;
        }

        /// <summary>
        /// Opens the launcher card from the add tile: a header plus one row per
        /// kind, each with its icon, name and a one-line description. Clicking
        /// away closes it; Escape is best-effort (see the card handler).
        /// </summary>
        private void ShowAddWidgetMenu()
        {
            if (_addWidgetPopup != null)
            {
                CloseAddWidgetMenu();
                return;
            }

            var content = BuildAddWidgetContent();

            // The popup sizes to its child's layout box; the card's drop shadow
            // renders outside that box, so reserve a 10px band (same as menus).
            // Padding (not Margin) keeps the band inside the child the custom
            // placement callback measures, so the popup's right edge can line
            // up with the add tile's right edge.
            var host = new Border
            {
                Padding = new Thickness(AddMenuShadowPadding),
                Background = Brushes.Transparent,
                Child = content
            };

            var popup = new Popup
            {
                PlacementTarget = AddTile,
                Placement = PlacementMode.Custom,
                CustomPopupPlacementCallback = PlaceAddWidgetMenu,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = host
            };
            popup.Closed += (s, e) =>
            {
                if (ReferenceEquals(_addWidgetPopup, popup)) _addWidgetPopup = null;
                ResumeAutoCollapse();
            };

            _addWidgetPopup = popup;
            PauseAutoCollapse();
            popup.IsOpen = true;

            content.Focusable = true;
            content.Focus();
        }

        /// <summary>
        /// Right-aligns the launcher card under the add tile, clamped to the
        /// add tile's monitor work area. WPF's Bottom placement uses the
        /// system's MenuDropAlignment setting, which flips the anchor to the
        /// target's right edge; a custom placement keeps the geometry stable.
        /// </summary>
        private CustomPopupPlacement[] PlaceAddWidgetMenu(Size childSize, Size targetSize, Point offset)
        {
            if (childSize.Width <= 0)
            {
                return new[]
                {
                    new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Horizontal)
                };
            }

            Point targetTopLeft = AddTile.PointToScreen(new Point(0, 0));
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
                (int)Math.Round(targetTopLeft.X + targetSize.Width / 2),
                (int)Math.Round(targetTopLeft.Y + targetSize.Height / 2)));
            double left = IslandLayout.AddMenuLeft(
                targetTopLeft.X + targetSize.Width,
                childSize.Width,
                screen.WorkingArea.Left,
                screen.WorkingArea.Right);

            return new[]
            {
                new CustomPopupPlacement(
                    new Point(left - targetTopLeft.X, targetSize.Height),
                    PopupPrimaryAxis.Horizontal)
            };
        }

        private void CloseAddWidgetMenu()
        {
            var popup = _addWidgetPopup;
            if (popup == null) return;
            _addWidgetPopup = null;
            popup.IsOpen = false;
        }

        /// <summary>The add tile's launcher card; also the pin's entry point.</summary>
        public FrameworkElement BuildAddWidgetContent()
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            panel.Children.Add(new TextBlock
            {
                Tag = "IslandAddHeader",
                Text = Localization.Get("Island_NewWidgetHeader"),
                FontSize = 11,
                Foreground = ThemeManager.SecondaryTextBrush,
                Margin = new Thickness(8, 5, 8, 5)
            });

            string iconStyle = WidgetManager.Instance.Config.IconStyle ?? "classic";
            foreach (var kind in WidgetKindRegistry.All)
            {
                string kindId = kind.Id;
                var iconCanvas = new Canvas { Width = 64, Height = 64 };
                IconRendererFactory.CreateByKey(kind.IconKey, iconStyle)
                    .Render(iconCanvas, UiTokens.DefaultFolderColor);
                var iconView = new Viewbox { Width = 24, Height = 24, Child = iconCanvas };

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock
                {
                    Text = Localization.Get(KindLabelKey(kindId)),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeManager.TextBrush
                });
                string hintKey = HintKey(kindId);
                if (hintKey.Length > 0)
                {
                    text.Children.Add(new TextBlock
                    {
                        Text = Localization.Get(hintKey),
                        FontSize = 11,
                        Foreground = ThemeManager.SecondaryTextBrush
                    });
                }

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(iconView, 0);
                Grid.SetColumn(text, 1);
                grid.Children.Add(iconView);
                grid.Children.Add(text);

                var row = new Button
                {
                    Tag = kindId,
                    Content = grid,
                    MinHeight = 48,
                    Style = FindResource("Kobold.Style.LauncherItem") as Style
                };
                row.Click += (s, e) =>
                {
                    CloseAddWidgetMenu();
                    WidgetManager.Instance.CreateWidget(kindId);
                };
                panel.Children.Add(row);
            }

            var card = LauncherCard.Create(panel, AddMenuWidth);
            card.Tag = "IslandAddMenu";
            // Best effort: the island is not activatable; outside click (StaysOpen=false)
            // is the guaranteed close. Esc is smoke-only.
            card.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                CloseAddWidgetMenu();
            };
            return card;
        }

        private static string HintKey(string kindId)
        {
            switch (kindId)
            {
                case "folder": return "Island_NewFolderHint";
                case "todo": return "Island_NewTodoHint";
                case "note": return "Island_NewNoteHint";
                case "search": return "Island_NewSearchHint";
                default: return string.Empty;
            }
        }

        private static string KindLabelKey(string kindId)
        {
            switch (kindId)
            {
                case "folder": return "UI_WidgetKindFolder";
                case "todo": return "UI_WidgetKindTodo";
                case "note": return "UI_WidgetKindNote";
                case "search": return "UI_WidgetKindSearch";
                default: return kindId;
            }
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

        private Border CreateTile(WidgetData widget, string iconStyle)
        {
            var iconCanvas = new Canvas { Width = 64, Height = 64 };
            var kind = WidgetKindRegistry.Find(widget.Kind);
            IconRendererFactory.CreateByKey(kind != null ? kind.IconKey : "folder", iconStyle)
                .Render(iconCanvas, widget.Color);

            bool open = _openStates.ContainsKey(widget.Id);
            var accent = WidgetAccent.ReadableBrush(widget.Color);

            var dot = new Ellipse
            {
                Width = 3,
                Height = 3,
                Tag = "TileDot",
                Fill = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
                IsHitTestVisible = false,
                Visibility = open ? Visibility.Visible : Visibility.Collapsed
            };

            var content = new Grid();
            content.Children.Add(new Viewbox
            {
                Width = TileIconSize,
                Height = TileIconSize,
                Child = iconCanvas,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(dot);

            var tile = new Border
            {
                Width = WidgetConstants.ISLAND_TILE_SIZE,
                Height = WidgetConstants.ISLAND_TILE_SIZE,
                CornerRadius = new CornerRadius(8),
                Background = open ? TileOpenBackground(widget.Id) : Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = widget.Name,
                Margin = new Thickness(0, 0, WidgetConstants.ISLAND_TILE_GAP, 0),
                Tag = widget.Id,
                Child = content
            };

            tile.MouseEnter += (s, e) =>
            {
                tile.Background = _openStates.ContainsKey(widget.Id)
                    ? TileOpenBackground(widget.Id)
                    : ThemeManager.IslandHoverBrush;
            };
            tile.MouseLeave += (s, e) =>
            {
                tile.Background = _openStates.ContainsKey(widget.Id)
                    ? TileOpenBackground(widget.Id)
                    : Brushes.Transparent;
            };
            tile.MouseLeftButtonUp += (s, e) => ActivateTile(widget.Id);
            tile.MouseRightButtonUp += (s, e) =>
            {
                // The widget menu (rename / colour / lock / pin / delete) lives
                // on the widget; WidgetManager routes the request to it.
                WidgetMenuRequested?.Invoke(widget.Id);
                e.Handled = true;
            };

            return tile;
        }

        /// <summary>
        /// One 44x44 member icon of an expanded group: the kind's 32px icon in
        /// the member colour, the active member's open wash + dot and a gold
        /// upright pin on pinned members. The name is only a tooltip - the row
        /// never writes member names or counts as text. A click activates the
        /// member and leaves the expansion to the manager's persisted flag.
        /// </summary>
        private Border CreateMemberTile(WidgetGroupTileMember member,
            WidgetGroupTileInfo info, string iconStyle)
        {
            string memberId = member == null ? string.Empty : member.Id;
            string color = member == null ? null : member.Color;
            bool active = member != null && !string.IsNullOrEmpty(info.ActiveWidgetId)
                && string.Equals(info.ActiveWidgetId, memberId, StringComparison.Ordinal);

            var iconCanvas = new Canvas { Width = 64, Height = 64 };
            var kind = WidgetKindRegistry.Find(member == null ? null : member.Kind);
            IconRendererFactory.CreateByKey(kind != null ? kind.IconKey : "folder", iconStyle)
                .Render(iconCanvas, color ?? UiTokens.DefaultFolderColor);

            var dot = new Ellipse
            {
                Width = 3,
                Height = 3,
                Tag = "TileDot",
                Fill = WidgetAccent.ReadableBrush(color ?? UiTokens.DefaultFolderColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
                IsHitTestVisible = false,
                Visibility = active ? Visibility.Visible : Visibility.Collapsed
            };

            var pin = new IconPresenter
            {
                Icon = IconId.Pin,
                Size = 9,
                StrokeThickness = 1.7,
                Rotation = 0,
                Brush = ThemeManager.PinGoldBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                IsHitTestVisible = false,
                Visibility = member != null && member.IsPinned
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                Tag = "IslandMemberPin:" + memberId
            };

            var content = new Grid { IsHitTestVisible = false };
            content.Children.Add(new Viewbox
            {
                Width = TileIconSize,
                Height = TileIconSize,
                Child = iconCanvas,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(dot);
            content.Children.Add(pin);

            var tile = new Border
            {
                Width = WidgetConstants.ISLAND_TILE_SIZE,
                Height = WidgetConstants.ISLAND_TILE_SIZE,
                CornerRadius = new CornerRadius(8),
                Background = active ? TileOpenBackground(memberId) : Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = member == null ? null : member.Name,
                Margin = new Thickness(0, 0, WidgetConstants.ISLAND_TILE_GAP, 0),
                Tag = "island-member:" + memberId,
                Child = content
            };
            tile.MouseLeftButtonUp += (s, e) =>
            {
                WidgetActivated?.Invoke(memberId);
                e.Handled = true;
            };

            return tile;
        }

        /// <summary>
        /// One folded 44x44 group tile at the first member's slot: a neutral
        /// 34x34 plate holding a 2x2 thumbnail grid (11px member icons in
        /// GroupOrder, max four cells; 5+ draws the first three + "+N"). It has
        /// no Folder glyph, no count badge and no member-colour ring; the open
        /// state is a neutral outline keyed by the group id.
        /// </summary>
        private Border CreateGroupTile(WidgetGroupTileInfo info, string iconStyle)
        {
            var iconGrid = new UniformGrid
            {
                Rows = 2,
                Columns = 2,
                Margin = new Thickness(3),
                Tag = "GroupIconGrid"
            };

            var plate = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Tag = "GroupPlate",
                Child = iconGrid
            };

            var content = new Grid { IsHitTestVisible = false };
            content.Children.Add(plate);

            var tile = new Border
            {
                Width = WidgetConstants.ISLAND_TILE_SIZE,
                Height = WidgetConstants.ISLAND_TILE_SIZE,
                CornerRadius = new CornerRadius(10),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, WidgetConstants.ISLAND_TILE_GAP, 0),
                Tag = "group:" + info.GroupId,
                Child = content
            };

            tile.MouseEnter += (s, e) =>
            {
                tile.Background = ThemeManager.IslandHoverBrush;
            };
            tile.MouseLeave += (s, e) =>
            {
                tile.Background = _openStates.ContainsKey(info.GroupId)
                    ? ThemeManager.IslandHoverBrush
                    : Brushes.Transparent;
            };
            tile.MouseLeftButtonUp += (s, e) =>
            {
                // The manager toggles the group's persisted inline member-icon
                // expansion from this event; the group menu stays on right click.
                GroupTileClicked?.Invoke(info.GroupId);
                e.Handled = true;
            };
            tile.MouseRightButtonUp += (s, e) =>
            {
                WidgetMenuRequested?.Invoke(info.GroupId);
                e.Handled = true;
            };

            UpdateGroupTile(tile, info, iconStyle);
            ApplyTileOpenState(tile);
            return tile;
        }

        /// <summary>
        /// Repaints a folded group tile's plate, ordered 2x2 thumbnails, +N
        /// overflow and tooltip from the latest projection, keeping the tile's
        /// Border alive so a collapsed same-group refresh does not rebuild the row.
        /// </summary>
        private static void UpdateGroupTile(Border tile, WidgetGroupTileInfo info, string iconStyle)
        {
            if (tile == null || info == null) return;

            tile.ToolTip = info.MemberNames;

            var content = tile.Child as Grid;
            Border plate = null;
            if (content != null)
            {
                foreach (var child in content.Children)
                {
                    var border = child as Border;
                    if (border != null && "GroupPlate".Equals(border.Tag as string))
                    {
                        plate = border;
                        break;
                    }
                }
            }
            if (plate == null) return;

            plate.Background = GroupPlateBackground();
            plate.BorderBrush = GroupPlateBorderBrush();

            var iconGrid = plate.Child as Panel;
            if (iconGrid == null) return;
            iconGrid.Children.Clear();

            var members = info.Members ?? new List<WidgetGroupTileMember>();
            int iconCount = info.MemberCount > 4 ? 3 : Math.Min(info.MemberCount, 4);
            for (int i = 0; i < iconCount && i < members.Count; i++)
            {
                var member = members[i];
                var iconCanvas = new Canvas { Width = 64, Height = 64 };
                var kind = WidgetKindRegistry.Find(member.Kind);
                IconRendererFactory.CreateByKey(kind != null ? kind.IconKey : "folder", iconStyle)
                    .Render(iconCanvas, member.Color);

                iconGrid.Children.Add(new Viewbox
                {
                    Width = 11,
                    Height = 11,
                    Child = iconCanvas,
                    Tag = "GroupMemberIcon:" + member.Id,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (info.MemberCount > 4)
            {
                iconGrid.Children.Add(new TextBlock
                {
                    Text = "+" + (info.MemberCount - 3).ToString(CultureInfo.InvariantCulture),
                    Tag = "GroupOverflow",
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeManager.IslandForegroundBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        /// <summary>The neutral translucent wash inside the folded group plate.</summary>
        private static Brush GroupPlateBackground()
        {
            Color foreground = ThemeManager.IslandForegroundBrush.Color;
            return WidgetAccent.Frozen(Color.FromArgb(14, foreground.R, foreground.G, foreground.B));
        }

        /// <summary>The neutral 1px border of the folded group plate.</summary>
        private static Brush GroupPlateBorderBrush()
        {
            Color foreground = ThemeManager.IslandForegroundBrush.Color;
            return WidgetAccent.Frozen(Color.FromArgb(23, foreground.R, foreground.G, foreground.B));
        }

        /// <summary>
        /// The rendered row layout: standalone widget ids and, for a group, its
        /// id plus its island expansion flag. An expanded group also contributes
        /// its ordered member ids, because its member icons are part of the row;
        /// a collapsed group's members are hidden, so a same-group merge can
        /// still repaint its one tile in place. A change here (fold/unfold,
        /// expand/collapse, merge/detach, reorder) rebuilds the row in order.
        /// </summary>
        private static string LayoutSignature(IEnumerable<ProjectedTile> tiles)
        {
            var parts = new List<string>();
            foreach (var entry in tiles)
            {
                if (entry.Group != null)
                {
                    string part = "g\u001f" + entry.Group.GroupId + "\u001f"
                        + (entry.Group.IslandExpanded ? "1" : "0");
                    if (entry.Group.IslandExpanded)
                    {
                        var members = entry.Group.Members ?? new List<WidgetGroupTileMember>();
                        part += "\u001f" + string.Join("\u001e", members.Select(
                            member => member == null ? string.Empty : member.Id ?? string.Empty));
                    }
                    parts.Add(part);
                }
                else
                {
                    parts.Add("w\u001f" + (entry.Widget == null ? string.Empty : entry.Widget.Id));
                }
            }
            return string.Join("\u001d", parts);
        }

        /// <summary>
        /// Standalone tile visuals. A change here may rebuild the row; groups
        /// never participate.
        /// </summary>
        private static string StandaloneContentSignature(IEnumerable<ProjectedTile> tiles)
        {
            var parts = new List<string>();
            foreach (var entry in tiles)
            {
                if (entry.Group != null) continue;
                var widget = entry.Widget;
                parts.Add("w\u001f" +
                    (widget == null ? string.Empty : widget.Id) + "\u001f" +
                    (widget == null ? string.Empty : widget.Kind) + "\u001f" +
                    (widget == null ? string.Empty : widget.Color) + "\u001f" +
                    (widget == null ? string.Empty : widget.Name));
            }
            return string.Join("\u001d", parts);
        }

        /// <summary>
        /// The folded group tile's content: identity, member count and the
        /// ordered member descriptors (kind/colour/name/pin). A change here
        /// repaints the existing group tile in place.
        /// </summary>
        private static string GroupContentSignature(IEnumerable<ProjectedTile> tiles)
        {
            var parts = new List<string>();
            foreach (var entry in tiles)
            {
                if (entry.Group == null) continue;
                var group = entry.Group;
                var members = group.Members ?? new List<WidgetGroupTileMember>();
                parts.Add("g\u001f" + group.GroupId + "\u001f" + group.MemberCount + "\u001f"
                    + (group.ActiveWidgetId ?? string.Empty) + "\u001e"
                    + string.Join("\u001e", members.Select(member =>
                        (member.Id ?? string.Empty) + "\u001f" +
                        (member.Kind ?? string.Empty) + "\u001f" +
                        (member.Color ?? string.Empty) + "\u001f" +
                        (member.Name ?? string.Empty) + "\u001f" +
                        (member.IsPinned ? "1" : "0"))));
            }
            return string.Join("\u001d", parts);
        }

        private void UpdateTileOpenState(string hostId)
        {
            foreach (var child in TilePanel.Children)
            {
                var tile = child as Border;
                if (tile == null) continue;
                string tag = tile.Tag as string;
                // An inline member tile's wash is owned by its own active
                // member, never by the group's open state.
                if (tag != null && tag.StartsWith("island-member:", StringComparison.Ordinal))
                    continue;
                if (!string.Equals(tag, hostId, StringComparison.Ordinal)
                    && !string.Equals(tag, "group:" + hostId, StringComparison.Ordinal))
                    continue;
                ApplyTileOpenState(tile);
            }
        }

        private void UpdateAllTileOpenStates()
        {
            if (TilePanel == null) return;
            foreach (var child in TilePanel.Children)
            {
                var tile = child as Border;
                if (tile != null) ApplyTileOpenState(tile);
            }
        }

        private void ApplyTileOpenState(Border tile)
        {
            string tag = tile.Tag as string;
            // Member tiles are painted from their own active member; the group
            // open state must never repaint them.
            if (tag != null && tag.StartsWith("island-member:", StringComparison.Ordinal))
                return;
            bool isGroup = tag != null && tag.StartsWith("group:", StringComparison.Ordinal);
            string hostId = isGroup ? tag.Substring("group:".Length) : tag;
            bool open = !string.IsNullOrEmpty(hostId) && _openStates.ContainsKey(hostId);

            if (isGroup)
            {
                // The folded tile's open state is neutral: a 1.5px theme
                // foreground outline plus the lifted hover wash. Never a
                // member colour and never a dot.
                tile.BorderThickness = open ? new Thickness(1.5) : new Thickness(0);
                tile.BorderBrush = open ? ThemeManager.IslandForegroundBrush : Brushes.Transparent;
                tile.Background = open ? ThemeManager.IslandHoverBrush : Brushes.Transparent;
                return;
            }

            tile.Background = open ? TileOpenBackground(hostId) : Brushes.Transparent;

            var content = tile.Child as Grid;
            if (content == null) return;
            foreach (var child in content.Children)
            {
                var dot = child as Ellipse;
                if (dot == null || !"TileDot".Equals(dot.Tag as string)) continue;
                dot.Fill = WidgetAccent.ReadableBrush(WidgetColorOf(hostId));
                dot.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                break;
            }
        }

        private string WidgetColorOf(string widgetId)
        {
            string color;
            return !string.IsNullOrEmpty(widgetId) && _widgetColors.TryGetValue(widgetId, out color)
                ? color
                : UiTokens.DefaultFolderColor;
        }

        /// <summary>A 14% widget-colour wash over the island surface.</summary>
        private Brush TileOpenBackground(string widgetId)
        {
            return WidgetAccent.Frozen(WidgetAccent.Tint(
                ThemeManager.IslandBackgroundBrush.Color, WidgetColorOf(widgetId), 0.14));
        }

        private void ActivateTile(string widgetId)
        {
            // Keep the island open so several panels can be toggled without re-hovering.
            WidgetActivated?.Invoke(widgetId);
        }

        #endregion

        #region Geometry & Animation

        /// <summary>
        /// Width of the active capsule: one slot per rendered tile, including
        /// the member icons of every expanded group.
        /// </summary>
        private double GetActiveCapsuleWidth()
        {
            return Math.Max(WidgetConstants.ISLAND_HOVER_WIDTH,
                IslandLayout.ContentWidth(RenderedSlotCount(), GetWorkArea().Width));
        }

        private double GetActiveCapsuleHeight()
        {
            return WidgetConstants.ISLAND_EXPANDED_HEIGHT;
        }

        /// <summary>
        /// Sizes the (never-animated) window so it can host the largest state
        /// plus room for the drop shadow, and places it around the remembered
        /// centre. The capsule content is centred in the window, so the saved
        /// centre is the visual centre too; the whole window is clamped into
        /// the work area.
        /// </summary>
        private void UpdateWindowGeometry()
        {
            double activeWidth = GetActiveCapsuleWidth();
            Width = Math.Max(WidgetConstants.ISLAND_HOVER_WIDTH, activeWidth) + 2 * ShadowPadding;
            Height = Math.Max(WidgetConstants.ISLAND_EXPANDED_HEIGHT,
                WidgetConstants.ISLAND_HOVER_HEIGHT) + ShadowPadding;

            Rect work = GetWorkArea();
            double center = GetSavedCenterX() ?? work.Left + work.Width / 2;
            center = IslandLayout.ClampCenter(center, Width, work.Left, work.Width);
            Left = center - Width / 2;
            Top = 0; // glued to the top of the WPF virtual desktop, as before
        }

        private static double? GetSavedCenterX()
        {
            try { return WidgetManager.Instance.Config.IslandX; }
            catch { return null; }
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

        /// <summary>One rendered slot: either a standalone widget or a folded group tile.</summary>
        private sealed class ProjectedTile
        {
            public WidgetData Widget;
            public WidgetGroupTileInfo Group;
        }
    }
}
