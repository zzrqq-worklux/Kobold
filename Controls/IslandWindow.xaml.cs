using System;
using System.Collections.Generic;
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

        /// <summary>Raised with the folder id when a widget tile is right-clicked.</summary>
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
            ApplySystemTileIconStyle(WidgetManager.Instance.Config.IconStyle ?? "classic");

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
            RebuildTiles();
            ApplySystemTileIconStyle(WidgetManager.Instance.Config.IconStyle ?? "classic");
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

        /// <summary>Horizontal centre (DIP) of the island - where panels open centred.</summary>
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

        /// <summary>Feeds the widget list rendered as tiles when expanded.</summary>
        public void SetWidgets(IEnumerable<WidgetData> widgets)
        {
            _widgets = widgets?.ToList() ?? new List<WidgetData>();
            RebuildTiles();
            UpdateWindowGeometry();

            if (_isExpanded)
            {
                double width = GetExpandedWidth();
                AnimateTo(width, WidgetConstants.ISLAND_EXPANDED_HEIGHT, width, WidgetConstants.ISLAND_EXPANDED_HEIGHT);
                SyncScrollBarFromScroller();
                FlashScrollBar();
            }
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

            // Immediate feedback clamps to the virtual desktop; the drop
            // re-clamps against the island monitor's work area (mouse-up).
            double half = Width / 2;
            double minX = SystemParameters.VirtualScreenLeft + half;
            double maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - half;
            double centerX = Math.Max(minX, Math.Min(Left + Width / 2 + step, maxX));
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
                    // Snap the dragged spot onto the island monitor's work area
                    // and remember that centre - never the previously saved one,
                    // which is what UpdateWindowGeometry would restore.
                    double center = ClampCenterX(Left + Width / 2, Width);
                    Left = center - Width / 2;
                    // Remember the pill center so it survives expanded-width changes.
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
            var widgets = _widgets.Where(HasWindowKind).ToList();
            foreach (var widget in widgets)
            {
                TilePanel.Children.Add(CreateTile(widget, iconStyle));
            }

            // The middle section measures itself: its content, capped to a share
            // of the island monitor's work area. Anything wider scrolls, with
            // the hint bar below.
            TileScroller.Width = IslandLayout.MiddleViewWidth(widgets.Count, GetWorkArea().Width);

            // Without widgets the trailing divider would sit right next to the
            // desktop divider - hide it so the entries stay separated once.
            SettingsDivider.Visibility = widgets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            Visibility slash = hidden ? Visibility.Visible : Visibility.Collapsed;
            DesktopSlash.Visibility = slash;
            DesktopSlashHandDrawn.Visibility = slash;

            string action = Localization.Get(hidden ? "Island_DesktopClickShow" : "Island_DesktopClickHide");
            DesktopTile.ToolTip = action + " · " + Localization.Get("Island_DesktopOpenHint");
        }

        /// <summary>
        /// Shows the classic or hand-drawn glyphs on the three system tiles
        /// (desktop, settings, add). Only the exact "handdrawn" id picks the
        /// hand-drawn canvases; classic/unknown fall back to classic, matching
        /// <see cref="IconRendererFactory"/>.
        /// </summary>
        private void ApplySystemTileIconStyle(string style)
        {
            bool handDrawn = style == "handdrawn";
            Visibility classic = handDrawn ? Visibility.Collapsed : Visibility.Visible;
            Visibility drawn = handDrawn ? Visibility.Visible : Visibility.Collapsed;

            DesktopTileClassic.Visibility = classic;
            DesktopTileHandDrawn.Visibility = drawn;
            SettingsTileClassic.Visibility = classic;
            SettingsTileHandDrawn.Visibility = drawn;
            AddTileClassic.Visibility = classic;
            AddTileHandDrawn.Visibility = drawn;
        }

        private Border CreateTile(WidgetData widget, string iconStyle)
        {
            var iconCanvas = new Canvas { Width = 64, Height = 64 };
            var kind = WidgetKindRegistry.Find(widget.Kind);
            IconRendererFactory.CreateByKey(kind != null ? kind.IconKey : "folder", iconStyle)
                .Render(iconCanvas, widget.Color);

            var tile = new Border
            {
                Width = WidgetConstants.ISLAND_TILE_SIZE,
                Height = WidgetConstants.ISLAND_TILE_SIZE,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = widget.Name,
                Margin = new Thickness(0, 0, WidgetConstants.ISLAND_TILE_GAP, 0),
                Child = new Viewbox { Width = TileIconSize, Height = TileIconSize, Child = iconCanvas }
            };

            tile.MouseEnter += (s, e) => tile.Background = ThemeManager.IslandHoverBrush;
            tile.MouseLeave += (s, e) => tile.Background = Brushes.Transparent;
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

        private void ActivateTile(string widgetId)
        {
            // Keep the island open so several panels can be toggled without re-hovering.
            WidgetActivated?.Invoke(widgetId);
        }

        #endregion

        #region Geometry & Animation

        private double GetExpandedWidth()
        {
            // The middle (tile) section is capped to a share of the screen; the
            // fixed desktop/add/settings entries always stay in view, so the
            // capsule is the fixed parts plus that capped middle. The cap uses
            // the island monitor's work area, matching RebuildTiles, so the tile
            // scroller never outgrows the capsule.
            return IslandLayout.ContentWidth(_widgets.Count(HasWindowKind), GetWorkArea().Width);
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

            Rect work = GetWorkArea();
            double fallbackCenter = work.Left + work.Width / 2;
            double centerX = ClampCenterX(GetSavedCenterX() ?? fallbackCenter, Width);
            Left = centerX - Width / 2;
            Top = 0; // glued to the top of the WPF virtual desktop, as before
        }

        private static double? GetSavedCenterX()
        {
            try { return WidgetManager.Instance.Config.IslandX; }
            catch { return null; }
        }

        /// <summary>Keeps the whole island window (including shadow) on its monitor.</summary>
        private double ClampCenterX(double centerX, double windowWidth)
        {
            Rect work = GetWorkArea();
            return IslandLayout.ClampCenter(centerX, windowWidth, work.Left, work.Width);
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
