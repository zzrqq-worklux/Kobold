using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
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

        // >0 while one of our own modal dialogs is up: Deactivated must not
        // collapse the panel underneath it.
        private int _modalDepth;

        /// <summary>Raised when the widget was deleted by the user.</summary>
        public event Action<IWidgetWindow> OnDeleted;

        /// <summary>Raised when the widget's persisted data changed.</summary>
        public event Action OnDataChanged;

        protected WidgetWindowBase(WidgetData data)
        {
            _data = data;

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
                ThemeManager.ThemeChanged -= OnThemeChanged;
                WindowSwitcher.ReleaseOwner(this);
            };
            Deactivated += Window_Deactivated;
        }

        /// <summary>The shared panel chrome, set once by <see cref="AttachChrome"/>.</summary>
        protected WidgetChrome Chrome { get; private set; }

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
            chrome.ApplyOpacity(GetPanelOpacity());
            UpdateUI();
        }

        #region IWidgetWindow

        public string WidgetId => _data.Id;
        public WidgetData Data => _data;
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
            if (_data.PanelWidth.HasValue && _data.PanelHeight.HasValue)
            {
                var (minWidth, minHeight) = GetMinPanelSize();
                return (Math.Max(minWidth, _data.PanelWidth.Value),
                        Math.Max(minHeight, _data.PanelHeight.Value));
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

            double x = _data.PanelX ?? defaultLeft;
            double y = _data.PanelY ?? defaultTop;
            (x, y) = ResolvePanelPosition(x, y, panelWidth, panelHeight);
            Left = x;
            Top = y;

            _isExpanded = true;
            OnPanelShown();
            PersistPanelState(true);
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
            Chrome.IsLocked = _data.IsLocked;
            Chrome.IsPinned = _data.IsPanelPinned;
            Chrome.PanelColor = _data.Color;
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
            _data.IsLocked = !_data.IsLocked;
            UpdateUI();
            OnDataChanged?.Invoke();
        }

        /// <summary>Toggles "keep open" and remembers it.</summary>
        public void TogglePin()
        {
            _data.IsPanelPinned = !_data.IsPanelPinned;
            UpdateUI();
            OnDataChanged?.Invoke();
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
                if (_modalDepth == 0 && _isExpanded && !_data.IsPanelPinned && !IsActive)
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
            if (_data.IsExpanded == open) return;

            _data.IsExpanded = open;
            WidgetManager.Instance.SaveConfig();
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
                var device = PanelPlacement.ToDeviceRect(x, y, width, height, _data.PanelDpiScale ?? 0, currentScale);
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
            // Our own modal dialog is up: keep the panel alive underneath it.
            if (_modalDepth > 0) return;

            // Don't close the panel if it is pinned.
            if (_isExpanded && !_data.IsPanelPinned)
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
        }

        private void OnHeaderDragEnded(Point cursor)
        {
            // The drag is over even when it was a click: reset the anchor flag
            // before the early return so the next drag starts clean.
            _dragStarted = false;
            if (!_isDraggingWindow) return; // a click, not a drag

            _isDraggingWindow = false;
            // Remember where the user parked this panel, and the DPI scale its
            // (DIP) coordinates are relative to.
            _data.PanelX = Left;
            _data.PanelY = Top;
            _data.PanelDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            OnDataChanged?.Invoke();
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
            _data.PanelWidth = Width;
            _data.PanelHeight = Height;
            _data.PanelX = Left;
            _data.PanelY = Top;
            _data.PanelDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            WidgetManager.Instance.SaveConfig();
        }

        private static double ClampPanelSize(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(value, max));
        }

        #endregion
    }
}
