using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// The panel state a <see cref="WidgetWindowBase"/> drives: a standalone
    /// widget reads/writes through a small adapter over its own
    /// <see cref="WidgetData"/>, a group window (T4) through its
    /// <see cref="Kobold.Core.WidgetGroup"/>. Every panel show/hide, geometry
    /// and lock/pin read or write goes through this seam so both window kinds
    /// share one panel implementation.
    /// </summary>
    internal interface IPanelState
    {
        string Id { get; }
        double? PanelX { get; set; }
        double? PanelY { get; set; }
        double? PanelDpiScale { get; set; }
        double? PanelWidth { get; set; }
        double? PanelHeight { get; set; }
        bool IsExpanded { get; set; }
        bool IsLocked { get; set; }
        bool IsPanelPinned { get; set; }
    }

    /// <summary>
    /// Shared behaviour for every widget window: the panel show/hide cycle and
    /// its animation, panel position/DPI restore, the configured opacity, the
    /// quiet-window owner and the lock/pin/drag handling of the shared chrome.
    /// Subclasses own the chrome instance (XAML), hand it to
    /// <see cref="AttachChrome"/> and give their content size through
    /// <see cref="GetContentSize"/>.
    /// </summary>
    public abstract class WidgetWindowBase : Window, IWidgetWindow
    {
        private const double DRAG_THRESHOLD = WidgetConstants.DRAG_THRESHOLD;

        // Kind minimum panel sizes (spec 3.2): the folder's 180x137 fallback
        // is overridden by FolderWidget with the engine's dynamic minimum for
        // the current icon scale; a todo panel's 342x337 fits the chrome
        // header (40) + the fixed content block (133) + a six-week calendar at
        // the 22 cell floor + one 30-high list row + the 2 border.
        private const double FOLDER_MIN_WIDTH = 180;
        private const double FOLDER_MIN_HEIGHT = 137;
        private const double TODO_MIN_WIDTH = 342;
        private const double TODO_MIN_HEIGHT = 337;

        private readonly WidgetData _data;

        // The panel state this window drives: an adapter over the datum for a
        // standalone widget, the WidgetGroup proxy for a group window (T4).
        private readonly IPanelState _state;

        // Panel drag state: screen cursor positions and the click-vs-drag flag.
        private Point _dragStartCursor;
        private Point _dragLastCursor;
        private double _dragDpiScale = 1.0;
        private bool _isDraggingWindow;

        // True only between HeaderDragStarted and HeaderDragEnded: a move is
        // never applied without its anchor, whatever order the chrome uses.
        private bool _dragStarted;

        // Panel resize state: the grabbed edge and the geometry snapshot taken
        // when the press started. Mirrors the drag anchor rule - a move is
        // never applied without its start.
        private WidgetResizeEdge _resizeEdge;
        private Point _resizeStartCursor;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private double _resizeStartLeft;
        private double _resizeStartTop;
        private double _resizeDpiScale = 1.0;
        private bool _isResizingWindow;
        private bool _resizeStarted;

        private bool _isExpanded;

        // True while this window wears the merge-target border hint.
        private bool _mergeHint;

        // The window the live header-drag hint is currently painted on; the
        // drag keeps it while the cursor stays over the same candidate.
        private IWidgetWindow _mergeHintTarget;

        // >0 while one of our own modal dialogs is up: Deactivated must not
        // collapse the panel underneath it.
        private int _modalDepth;

        /// <summary>Raised when the widget was deleted by the user.</summary>
        public event Action<IWidgetWindow> OnDeleted;

        /// <summary>Raised when the widget's persisted data changed.</summary>
        public event Action OnDataChanged;

        /// <summary>
        /// Raised when the expanded panel opens or closes. The island mirrors
        /// this on the widget's tile; it fires even when the window is shown
        /// without activation (startup restore / Show All).
        /// </summary>
        public event Action<bool> PanelVisibilityChanged;

        protected WidgetWindowBase(WidgetData data) : this(data, new DataPanelState(data))
        {
        }

        /// <summary>
        /// The group seam: the chrome and the modals stay bound to the
        /// first member datum, while every panel geometry/flag/id read and
        /// write goes through the group's state proxy. A standalone widget
        /// adapts its own datum, so every value read or written is the same
        /// field it used before.
        /// </summary>
        private protected WidgetWindowBase(WidgetData data, IPanelState state)
        {
            _data = data;
            _state = state;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = false;

            ThemeManager.ThemeChanged += OnThemeChanged;

            SourceInitialized += (s, e) => WindowSwitcher.HideFromSwitcher(this); // panels stay out of Alt+Tab
            Loaded += (s, e) => RefreshTheme();
            Closing += (s, e) =>
            {
                ClearMergeHint();
                ThemeManager.ThemeChanged -= OnThemeChanged;
                WindowSwitcher.ReleaseOwner(this);
            };
            Deactivated += Window_Deactivated;
        }

        /// <summary>The shared panel chrome, set once by <see cref="AttachChrome"/>.</summary>
        protected WidgetChrome Chrome { get; private set; }

        /// <summary>
        /// The shared chrome for a hosted content view (the search view drives
        /// the chrome's query glow); null before <see cref="AttachChrome"/> ran.
        /// Internal: only a view in this assembly (and the pins) needs it, so the
        /// window keeps its public surface unchanged.
        /// </summary>
        internal WidgetChrome PanelChrome => Chrome;

        /// <summary>
        /// Wires the chrome to this window's shared behaviour. Subclasses call
        /// this right after InitializeComponent, once the chrome exists.
        /// </summary>
        protected void AttachChrome(WidgetChrome chrome)
        {
            Chrome = chrome;
            chrome.LockClicked += ToggleLock;
            chrome.PinClicked += TogglePin;
            chrome.HeaderDragStarted += OnHeaderDragStarted;
            chrome.HeaderDragMoved += OnHeaderDragMoved;
            chrome.HeaderDragEnded += OnHeaderDragEnded;
            chrome.ResizeStarted += OnResizeStarted;
            chrome.ResizeMoved += OnResizeMoved;
            chrome.ResizeEnded += OnResizeEnded;
            chrome.HeaderRightClicked += () => ShowWidgetMenu();
            // Losing the header capture mid-drag (another window takes it, a
            // modal/panel teardown) drops the hover hint once the input turn
            // ends; on a normal release HeaderDragEnded has already cleared it,
            // so the deferred call is a no-op and cannot flicker the target.
            chrome.Header.LostMouseCapture += (s, e) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ClearMergeHint));
            chrome.ApplyOpacity(GetPanelOpacity());
            UpdateUI();
        }

        #region IWidgetWindow

        public string WidgetId => _state.Id;
        public virtual WidgetData Data => _data;
        public bool IsPanelOpen => _isExpanded;
        public bool IsModalOpen => _modalDepth > 0;

        /// <summary>
        /// Size (DIP) this widget's content needs; the 40px chrome header and
        /// the chrome shell's border are added on top by
        /// <see cref="GetPanelSize"/>.
        /// </summary>
        protected abstract (double width, double height) GetContentSize();

        /// <summary>
        /// Full panel size used for placement and the window itself. A widget
        /// with both persisted sizes uses them (clamped up to its kind
        /// minimum); otherwise the legacy path composes the content size plus
        /// the chrome header and the chrome shell's border. The shell border
        /// lives inside the window, so leaving it out would arrange the
        /// content 2 DIP short in each dimension and WPF's layout clip would
        /// cut the view's edge.
        /// </summary>
        public (double width, double height) GetPanelSize()
        {
            if (_state.PanelWidth.HasValue && _state.PanelHeight.HasValue)
            {
                var (minWidth, minHeight) = GetMinPanelSize();
                return (Math.Max(minWidth, _state.PanelWidth.Value),
                        Math.Max(minHeight, _state.PanelHeight.Value));
            }

            var (contentWidth, contentHeight) = GetContentSize();
            var border = Chrome == null ? new Thickness(0) : Chrome.Shell.BorderThickness;
            return (contentWidth + border.Left + border.Right,
                    WidgetConstants.HEADER_HEIGHT + contentHeight + border.Top + border.Bottom);
        }

        /// <summary>
        /// Shows the expanded panel, using this widget's remembered position
        /// when it has one, otherwise the supplied default (below the island).
        /// Used by the island and by the widget menu's show/hide entry.
        /// </summary>
        public void ShowPanel(double defaultLeft, double defaultTop, bool activate = true)
        {
            UpdateUI();
            Chrome.ApplyOpacity(GetPanelOpacity()); // pick up a settings change while closed

            var (panelWidth, panelHeight) = GetPanelSize();
            Width = panelWidth;
            Height = panelHeight;

            double x = _state.PanelX ?? defaultLeft;
            double y = _state.PanelY ?? defaultTop;
            (x, y) = ResolvePanelPosition(x, y, panelWidth, panelHeight);
            Left = x;
            Top = y;

            bool wasOpen = _isExpanded;
            _isExpanded = true;
            OnPanelShown();
            PersistPanelState(true);
            if (!wasOpen) PanelVisibilityChanged?.Invoke(true);
            if (!IsVisible)
            {
                // Opening several panels together ("Show All") must not activate
                // each one: activation would deactivate and hide the previous
                // panel, which shows up as flicker.
                if (!activate) ShowActivated = false;
                Show();
                ShowActivated = true;
            }
            if (activate) Activate();
            AnimationHelper.PanelOpen(Chrome.Shell);
        }

        /// <summary>Fades the panel out and hides its window.</summary>
        public void HidePanel()
        {
            if (!_isExpanded)
            {
                if (IsVisible) Hide();
                return;
            }

            _isExpanded = false;
            OnPanelHidden();
            PersistPanelState(false);
            PanelVisibilityChanged?.Invoke(false);
            AnimationHelper.PanelClose(Chrome.Shell, () => Hide());
        }

        /// <summary>Empty hook: a subclass may follow the panel's visibility (e.g. watchers).</summary>
        protected virtual void OnPanelShown() { }

        /// <summary>Empty hook: the counterpart of <see cref="OnPanelShown"/>.</summary>
        protected virtual void OnPanelHidden() { }

        public abstract void ShowWidgetMenu(Action onClosed = null);

        /// <summary>Pushes the widget's persisted state (title / colour / lock / pin) onto the chrome.</summary>
        public virtual void UpdateUI()
        {
            if (Chrome == null) return;
            Chrome.Title = _data.Name;
            Chrome.IsLocked = _state.IsLocked;
            Chrome.IsPinned = _state.IsPanelPinned;
            Chrome.PanelColor = _data.Color;
        }

        /// <summary>
        /// Highlights the shell border while this window is a merge target:
        /// the accent theme token on the existing border (no extra strip) and
        /// the chrome's normal border when cleared. Safe to call repeatedly.
        /// </summary>
        public void SetMergeHint(bool on)
        {
            if (_mergeHint == on) return;
            _mergeHint = on;
            if (Chrome == null) return;

            if (on)
            {
                Chrome.Shell.BorderBrush = ThemeManager.AccentBrush;
                return;
            }

            // UpdatePanelBorder owns the resting shell border (the panel
            // colour tint or the neutral token), so a full theme repaint
            // restores exactly what the chrome would paint anyway.
            Chrome.ApplyTheme();
        }

        /// <summary>Re-applies the theme and the configured panel opacity.</summary>
        public virtual void RefreshTheme()
        {
            if (Chrome == null) return;
            Chrome.ApplyTheme();
            Chrome.ApplyOpacity(GetPanelOpacity());
            UpdateUI();
        }

        #endregion

        #region Shared menu operations

        /// <summary>Raises <see cref="OnDataChanged"/> for menu edits made outside this class.</summary>
        public void NotifyDataChanged()
        {
            OnDataChanged?.Invoke();
        }

        /// <summary>Toggles the content lock and remembers it.</summary>
        public void ToggleLock()
        {
            _state.IsLocked = !_state.IsLocked;
            UpdateUI();
            OnDataChanged?.Invoke();
            OnPanelStateChanged();
        }

        /// <summary>Toggles "keep open" and remembers it.</summary>
        public void TogglePin()
        {
            _state.IsPanelPinned = !_state.IsPanelPinned;
            UpdateUI();
            OnDataChanged?.Invoke();
            OnPanelStateChanged();
        }

        /// <summary>Prompts for a new name; committed through the usual change path.</summary>
        public void BeginRename()
        {
            string newName = ShowInputModal(
                Localization.Get("Dialog_Rename"),
                Localization.Get("Dialog_EnterName"),
                _data.Name,
                null);

            if (string.IsNullOrWhiteSpace(newName)) return;

            _data.Name = newName.Trim();
            UpdateUI();
            OnDataChanged?.Invoke();
        }

        /// <summary>Shows the shared palette picker; picking repaints and remembers the colour.</summary>
        public void BeginColorChange()
        {
            RunModal(() => ColorPaletteDialog.Show(
                this,
                Localization.Get("Dialog_PickColor"),
                UiTokens.FolderPalette,
                _data.Color,
                allowDefault: false,
                defaultText: null,
                onPick: color =>
                {
                    _data.Color = color;
                    RefreshTheme();
                    OnDataChanged?.Invoke();
                },
                onDefault: null));
        }

        /// <summary>Confirms and then deletes the widget through the manager.</summary>
        public virtual void RequestDelete()
        {
            if (!ShowConfirmModal(Localization.Format("Dialog_DeleteWidget", _data.Name))) return;

            // Last chance for a subclass to keep its own content alive (the
            // folder ejects stored files back before the widget disappears).
            OnDeleting();
            OnDeleted?.Invoke(this);
            Close();
        }

        /// <summary>
        /// Empty hook: runs after the delete confirmation and before the widget
        /// is removed - a subclass may release its own content here.
        /// </summary>
        protected virtual void OnDeleting() { }

        /// <summary>
        /// Applies the configured opacity to an already-open panel (live
        /// preview from the settings dialog).
        /// </summary>
        public void ApplyPanelOpacity()
        {
            if (!_isExpanded || Chrome == null) return;
            Chrome.ApplyOpacity(GetPanelOpacity());
        }

        #endregion

        #region Modal guard

        /// <summary>Shows a modal dialog while shielding the panel from focus-loss collapse.</summary>
        protected void RunModal(Action show)
        {
            if (show == null) return;

            _modalDepth++;
            try
            {
                show();
            }
            finally
            {
                _modalDepth--;
                if (_modalDepth == 0 && _isExpanded && !_state.IsPanelPinned && !IsActive)
                {
                    HidePanel();
                }
            }
        }

        protected string ShowInputModal(string title, string prompt, string initialValue, Func<string, string> validate)
        {
            string result = null;
            RunModal(() => { result = DialogFactory.ShowInput(this, title, prompt, initialValue, validate,
                _data == null ? null : _data.Color); });
            return result;
        }

        protected bool ShowConfirmModal(string message)
        {
            var answer = MessageBoxResult.No;
            RunModal(() =>
            {
                answer = MessageBox.Show(this, message, Localization.Get("Dialog_Confirm"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
            });
            return answer == MessageBoxResult.Yes;
        }

        #endregion

        #region Shared panel mechanics

        /// <summary>
        /// Remembers whether the panel was open, so the next launch can reopen
        /// it (see StartupPanels). Saved directly rather than through
        /// OnDataChanged: the island's tiles do not depend on this.
        /// </summary>
        private void PersistPanelState(bool open)
        {
            if (_state.IsExpanded == open) return;

            _state.IsExpanded = open;
            WidgetManager.Instance.SaveConfig();
            OnPanelStateChanged();
        }

        /// <summary>
        /// Places a panel that has a remembered position: interprets it with the
        /// DPI scale it was saved at, picks the monitor it belongs to, clamps it
        /// into that monitor's work area and converts back to DIPs for the
        /// destination monitor. Falls back to the virtual-screen clamp whenever
        /// the monitor data is unavailable.
        /// </summary>
        private (double x, double y) ResolvePanelPosition(double x, double y, double width, double height)
        {
            try
            {
                double currentScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                var device = PanelPlacement.ToDeviceRect(x, y, width, height, _state.PanelDpiScale ?? 0, currentScale);
                var monitors = MonitorInterop.GetMonitors();
                if (monitors.Count == 0) return ClampToVirtualScreen(x, y, width, height);

                var clamped = PanelPlacement.ClampToMonitors(device, monitors);
                var target = MonitorInterop.GetMonitorForDevicePoint(
                    new Point(clamped.X + clamped.Width / 2, clamped.Y + clamped.Height / 2)) ?? monitors[0];
                var dip = PanelPlacement.ToDipRect(clamped, target);
                return (dip.X, dip.Y);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] panel placement fallback: {ex.Message}");
                return ClampToVirtualScreen(x, y, width, height);
            }
        }

        private static (double x, double y) ClampToVirtualScreen(double x, double y, double width, double height)
        {
            double minX = SystemParameters.VirtualScreenLeft;
            double minY = SystemParameters.VirtualScreenTop;
            double maxX = minX + SystemParameters.VirtualScreenWidth - width;
            double maxY = minY + SystemParameters.VirtualScreenHeight - height;
            return (Math.Max(minX, Math.Min(x, maxX)), Math.Max(minY, Math.Min(y, maxY)));
        }

        /// <summary>
        /// Moves the window back inside a monitor's work area after its size
        /// changed, so a panel that grew downward never hangs off the bottom
        /// (its add row and editors live at the very bottom). Same monitor
        /// maths as <see cref="ResolvePanelPosition"/>, but with the window's
        /// current position and DPI scale instead of a remembered placement.
        /// </summary>
        protected void FitPanelToWorkArea()
        {
            try
            {
                double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                var device = PanelPlacement.ToDeviceRect(Left, Top, Width, Height, 0, scale);
                var monitors = MonitorInterop.GetMonitors();
                if (monitors.Count == 0)
                {
                    var fallback = ClampToVirtualScreen(Left, Top, Width, Height);
                    Left = fallback.x;
                    Top = fallback.y;
                    return;
                }

                var clamped = PanelPlacement.ClampToMonitors(device, monitors);
                var target = MonitorInterop.GetMonitorForDevicePoint(
                    new Point(clamped.X + clamped.Width / 2, clamped.Y + clamped.Height / 2)) ?? monitors[0];
                var dip = PanelPlacement.ToDipRect(clamped, target);
                Left = dip.X;
                Top = dip.Y;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] panel fit fallback: {ex.Message}");
                var fallback = ClampToVirtualScreen(Left, Top, Width, Height);
                Left = fallback.x;
                Top = fallback.y;
            }
        }

        /// <summary>
        /// Panel opacity from config, clamped to the usable 20-100% range.
        /// Only the expanded panel is affected - content stays fully opaque.
        /// </summary>
        private double GetPanelOpacity()
        {
            double opacity = WidgetManager.Instance.Config.PanelOpacity;
            if (double.IsNaN(opacity)) opacity = 1.0;
            return Math.Max(0.2, Math.Min(1.0, opacity));
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Losing activation ends any live hover hint; a closing/source
            // panel must never leave another window's border lit.
            ClearMergeHint();

            // Our own modal dialog is up: keep the panel alive underneath it.
            if (_modalDepth > 0) return;

            // Don't close the panel if it is pinned.
            if (_isExpanded && !_state.IsPanelPinned)
            {
                HidePanel();
            }
        }

        private void OnThemeChanged()
        {
            Dispatcher.Invoke(() => RefreshTheme());
        }

        #endregion

        #region Header drag (chrome forwards screen cursor points)

        private void OnHeaderDragStarted(Point cursor)
        {
            // A fresh gesture never inherits a hint from a lost/cancelled drag.
            ClearMergeHint();

            // Snapshot the anchor; a click without movement is not a drag.
            _dragStarted = true;
            _dragStartCursor = cursor;
            _dragDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX; // physical px per WPF unit
            _isDraggingWindow = false;
        }

        private void OnHeaderDragMoved(Point cursor)
        {
            // No anchor yet (a move pumped before the start event): ignore it,
            // or the window would jump by the absolute cursor delta.
            if (!_dragStarted) return;

            if (!_isDraggingWindow)
            {
                double dx = (cursor.X - _dragStartCursor.X) / _dragDpiScale;
                double dy = (cursor.Y - _dragStartCursor.Y) / _dragDpiScale;
                if (Math.Abs(dx) < DRAG_THRESHOLD && Math.Abs(dy) < DRAG_THRESHOLD) return;
                _isDraggingWindow = true;
                _dragLastCursor = _dragStartCursor;
            }

            // Move by per-step deltas with a fresh DPI reading, so the window
            // keeps following the cursor across monitors with different scaling.
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Left += (cursor.X - _dragLastCursor.X) / dpi;
            Top += (cursor.Y - _dragLastCursor.Y) / dpi;
            _dragLastCursor = cursor;

            // Live drop hint: while a real drag is over an eligible panel, that
            // panel wears the accent border. Only a target change repaints.
            UpdateMergeHint(cursor);
        }

        private void OnHeaderDragEnded(Point cursor)
        {
            // The drag is over even when it was a click: reset the anchor flag
            // before the early return so the next drag starts clean.
            _dragStarted = false;
            if (!_isDraggingWindow)
            {
                ClearMergeHint();
                return; // a click, not a drag
            }

            _isDraggingWindow = false;
            // Remember where the user parked this panel, and the DPI scale its
            // (DIP) coordinates are relative to.
            _state.PanelX = Left;
            _state.PanelY = Top;
            _state.PanelDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            OnDataChanged?.Invoke();
            OnPanelStateChanged();

            try
            {
                // A real header drag is also the merge gesture: drop onto another
                // widget's panel and it joins that host instead of just moving.
                TryMergeFromHeaderDrag(cursor);
            }
            finally
            {
                // The merge may have closed/replaced the target host; clearing
                // the remembered instance is always safe.
                ClearMergeHint();
            }
        }

        /// <summary>
        /// The header-drag drop half of merge: find the first visible widget
        /// window under the release point, keep its hint lit and hand the
        /// merge to the manager. A group is never a source and the policy
        /// rejects locked/locked-group/Folder pairs; no target keeps the
        /// plain move.
        /// </summary>
        private void TryMergeFromHeaderDrag(Point cursor)
        {
            if (this is WidgetGroupWindow) return;
            double scale = _dragDpiScale > 0 ? _dragDpiScale : 1.0;
            if (!GroupDragPolicy.IsDragOut(
                    (cursor.X - _dragStartCursor.X) / scale,
                    (cursor.Y - _dragStartCursor.Y) / scale)) return;

            IWidgetWindow target = FindMergeTarget(cursor);
            if (target == null) return;

            SetMergeHintTarget(target);
            WidgetManager.Instance.MergeWidgets(this, target);
        }

        /// <summary>
        /// The live hover half of the merge hint: repaints only when the
        /// candidate under the cursor changes, so a move within one panel does
        /// not re-apply the chrome theme every frame.
        /// </summary>
        private void UpdateMergeHint(Point cursor)
        {
            if (this is WidgetGroupWindow) return;

            // The hint belongs to a merge-sized gesture: a window drag that
            // never reached the merge threshold (or came back inside it) shows
            // none.
            double scale = _dragDpiScale > 0 ? _dragDpiScale : 1.0;
            if (!GroupDragPolicy.IsDragOut(
                    (cursor.X - _dragStartCursor.X) / scale,
                    (cursor.Y - _dragStartCursor.Y) / scale))
            {
                ClearMergeHint();
                return;
            }

            SetMergeHintTarget(FindMergeTarget(cursor));
        }

        /// <summary>
        /// The eligible merge target under a screen point: the first visible
        /// candidate (the source is skipped by the policy) that passes
        /// <see cref="GroupDragPolicy.CanMerge"/>; locked/locked-group/Folder
        /// hosts never qualify, so they never hint.
        /// </summary>
        private IWidgetWindow FindMergeTarget(Point cursor)
        {
            IWidgetWindow found = GroupDragPolicy.FindTarget(cursor, this, MergeCandidates, ScreenBounds);
            if (found == null || !GroupDragPolicy.CanMerge(this, found)) return null;
            return found;
        }

        /// <summary>Moves the hint to one target; the same target is a no-op.</summary>
        private void SetMergeHintTarget(IWidgetWindow target)
        {
            if (ReferenceEquals(target, _mergeHintTarget)) return;

            var previous = _mergeHintTarget as WidgetWindowBase;
            _mergeHintTarget = target;
            if (previous != null) previous.SetMergeHint(false);

            var next = target as WidgetWindowBase;
            if (next != null) next.SetMergeHint(true);
        }

        private void ClearMergeHint() => SetMergeHintTarget(null);

        /// <summary>
        /// The windows a header drag may merge into. Production reads the live
        /// manager host list; the checks override it so the hint can be driven
        /// without registering windows with the singleton.
        /// </summary>
        internal IReadOnlyList<IWidgetWindow> MergeCandidatesOverride { get; set; }

        private IReadOnlyList<IWidgetWindow> MergeCandidates =>
            MergeCandidatesOverride ?? WidgetManager.Instance.Widgets;

        /// <summary>
        /// The window's physical screen rectangle (DPI-scaled), as the merge
        /// hit test expects. A never-shown or closing window has no rect.
        /// </summary>
        private static Rect ScreenBounds(IWidgetWindow window)
        {
            var host = window as Window;
            if (host == null || !host.IsVisible) return Rect.Empty;

            try
            {
                Point origin = host.PointToScreen(new Point(0, 0));
                DpiScale dpi = VisualTreeHelper.GetDpi(host);
                return new Rect(origin.X, origin.Y,
                    host.ActualWidth * dpi.DpiScaleX,
                    host.ActualHeight * dpi.DpiScaleY);
            }
            catch (Exception ex)
            {
                // A window without a live presentation source has no rect;
                // it simply is not a drop target.
                Debug.WriteLine("[Kobold] merge bounds fallback: " + ex.Message);
                return Rect.Empty;
            }
        }

        #endregion

        #region Resize (chrome forwards screen cursor points)

        protected virtual (double width, double height) GetMinPanelSize()
        {
            return _data is TodoData
                ? (TODO_MIN_WIDTH, TODO_MIN_HEIGHT)
                : (FOLDER_MIN_WIDTH, FOLDER_MIN_HEIGHT);
        }

        private void OnResizeStarted(WidgetResizeEdge edge, Point cursor)
        {
            // Snapshot the anchor; a click without movement is not a resize.
            _resizeStarted = true;
            _resizeEdge = edge;
            _resizeStartCursor = cursor;
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
            _resizeStartLeft = Left;
            _resizeStartTop = Top;
            _resizeDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _isResizingWindow = false;
        }

        private void OnResizeMoved(Point cursor)
        {
            // No anchor yet (a move pumped before the start event): ignore it,
            // or the window would jump by the absolute cursor delta.
            if (!_resizeStarted) return;

            _isResizingWindow = true;

            // Fresh DPI reading each step, so the panel keeps following the
            // cursor across monitors with different scaling; the start
            // snapshot only backs it up before the window has a live tree.
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (double.IsNaN(dpi) || dpi <= 0) dpi = _resizeDpiScale;

            double dx = (cursor.X - _resizeStartCursor.X) / dpi;
            double dy = (cursor.Y - _resizeStartCursor.Y) / dpi;

            var (minWidth, minHeight) = GetMinPanelSize();
            Rect work = MonitorInterop.WorkAreaDipForWindow(this);
            double maxWidth = work.Width;
            double maxHeight = work.Height;

            switch (_resizeEdge)
            {
                case WidgetResizeEdge.Right:
                    Width = ClampPanelSize(_resizeStartWidth + dx, minWidth, maxWidth);
                    break;
                case WidgetResizeEdge.Bottom:
                    Height = ClampPanelSize(_resizeStartHeight + dy, minHeight, maxHeight);
                    break;
                case WidgetResizeEdge.Left:
                {
                    // The right edge stays fixed: the left side absorbs the delta.
                    double newWidth = ClampPanelSize(_resizeStartWidth - dx, minWidth, maxWidth);
                    Left = _resizeStartLeft + (_resizeStartWidth - newWidth);
                    Width = newWidth;
                    break;
                }
                case WidgetResizeEdge.Top:
                {
                    // The bottom edge stays fixed: the top side absorbs the delta.
                    double newHeight = ClampPanelSize(_resizeStartHeight - dy, minHeight, maxHeight);
                    Top = _resizeStartTop + (_resizeStartHeight - newHeight);
                    Height = newHeight;
                    break;
                }
                case WidgetResizeEdge.BottomRight:
                    Width = ClampPanelSize(_resizeStartWidth + dx, minWidth, maxWidth);
                    Height = ClampPanelSize(_resizeStartHeight + dy, minHeight, maxHeight);
                    break;
            }

            OnPanelSizeChanged();
        }

        /// <summary>
        /// Empty hook: called after a resize move applied the new size, so a
        /// subclass can re-arrange its content for the live panel size.
        /// </summary>
        protected virtual void OnPanelSizeChanged() { }

        /// <summary>
        /// Called after every panel state write (persist, drag, resize, lock,
        /// pin). The group window (T5) mirrors the state onto its
        /// <see cref="WidgetGroup"/> here; standalone widgets need nothing.
        /// </summary>
        protected virtual void OnPanelStateChanged() { }

        private void OnResizeEnded(Point cursor)
        {
            // The gesture is over even when it was a click: reset the anchor
            // flag before the early return so the next press starts clean.
            _resizeStarted = false;
            if (!_isResizingWindow) return; // a click, not a resize

            _isResizingWindow = false;
            FitPanelToWorkArea();

            // Remember the chosen size and the fitted position. Saved directly
            // rather than through OnDataChanged: the island's tiles do not
            // depend on the panel geometry, so no rebuild is needed.
            _state.PanelWidth = Width;
            _state.PanelHeight = Height;
            _state.PanelX = Left;
            _state.PanelY = Top;
            _state.PanelDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            WidgetManager.Instance.SaveConfig();
            OnPanelStateChanged();
        }

        private static double ClampPanelSize(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(value, max));
        }

        #endregion

        /// <summary>
        /// Adapts a standalone widget's datum to the panel seam. C# has no
        /// structural interface implementation, so the base wraps the datum;
        /// every member forwards to the same data property, keeping the
        /// standalone behavior identical.
        /// </summary>
        private sealed class DataPanelState : IPanelState
        {
            private readonly WidgetData _data;

            public DataPanelState(WidgetData data)
            {
                _data = data;
            }

            public string Id => _data.Id;
            public double? PanelX { get => _data.PanelX; set => _data.PanelX = value; }
            public double? PanelY { get => _data.PanelY; set => _data.PanelY = value; }
            public double? PanelDpiScale { get => _data.PanelDpiScale; set => _data.PanelDpiScale = value; }
            public double? PanelWidth { get => _data.PanelWidth; set => _data.PanelWidth = value; }
            public double? PanelHeight { get => _data.PanelHeight; set => _data.PanelHeight = value; }
            public bool IsExpanded { get => _data.IsExpanded; set => _data.IsExpanded = value; }
            public bool IsLocked { get => _data.IsLocked; set => _data.IsLocked = value; }
            public bool IsPanelPinned { get => _data.IsPanelPinned; set => _data.IsPanelPinned = value; }
        }
    }
}
