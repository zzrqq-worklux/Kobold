using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// One panel hosting several widget kinds as tabs. The layout is the shared
    /// chrome with a 32px bare tab row above its 40px header; every member owns
    /// a real view instance in its own slot, and switching tabs only toggles
    /// visibility - views are never recreated and the window never resizes.
    /// The group itself owns the panel geometry, open state, lock and pin.
    /// </summary>
    public partial class WidgetGroupWindow : WidgetWindowBase, IWidgetViewHost, IGroupDragHost, IFolderPanelHost
    {
        private const double TAB_ROW_HEIGHT = WidgetGroupTabStrip.RowHeight;

        // The shell is 760x540 at rest: 2 border + 32 tab row + 40 header +
        // the 466 content block.
        private const double CONTENT_WIDTH = 758;
        private const double CONTENT_HEIGHT = 466;

        private const double TODO_MIN_WIDTH = 342;
        private const double TODO_MIN_HEIGHT = 337;
        private const double NOTE_MIN_WIDTH = 260;
        private const double NOTE_MIN_HEIGHT = 220;
        private const double SEARCH_MIN_WIDTH = 340;
        private const double SEARCH_MIN_HEIGHT = 320;
        private const double DEFAULT_MIN_WIDTH = 300;
        private const double DEFAULT_MIN_HEIGHT = 220;

        private readonly WidgetGroup _group;
        private readonly List<WidgetData> _members;
        private readonly ReadOnlyCollection<WidgetData> _membersView;
        private readonly List<IWidgetContentView> _views;
        private readonly List<ContentControl> _slots;
        private readonly List<IInputElement> _lastFocus;
        private readonly Grid _contentHost;

        private WidgetGroupTabStrip _tabStrip;
        private int _activeIndex = -1;

        // One epoch per group surface: a tab switch takes a token before it
        // touches the views, deferred focus restoration runs only for the
        // latest token, and a hide/close stales every pending request.
        private readonly SurfaceSwitchGate _activationGate = new SurfaceSwitchGate();

        // The Explorer-like ghost worn while a tab is dragged outside the
        // group (detach intent). Only this window ever creates it; it is
        // closed on every drag exit and on hide/close.
        private DragGhostWindow _dragGhost;
        private Point _lastTabDragScreen;
        private bool _hasTabDragScreen;

        /// <summary>
        /// Raised when the user asks to detach a tab (menu entry or a tab
        /// dragged outside the group), before the manager is called. A check
        /// can observe the request without a running manager; production goes
        /// on to <see cref="WidgetManager.DetachWidget"/>.
        /// </summary>
        public event Action<string> DetachRequested;

        public WidgetGroupWindow(WidgetGroup group, IList<WidgetData> members)
            : this(group, members, null)
        {
        }

        /// <summary>
        /// Builds a group around live member views: a non-null
        /// <paramref name="contents"/> entry must already be attached to the
        /// same member, and is moved into its slot through
        /// <see cref="IWidgetContentView.RebindHost"/> instead of being
        /// re-attached. A null entry creates and attaches a fresh view. The
        /// public constructor passes null, so every member is fresh.
        /// </summary>
        internal WidgetGroupWindow(WidgetGroup group, IList<WidgetData> members, IList<IWidgetContentView> contents)
            : base(FirstMember(members), CreateState(group))
        {
            _group = group;
            _members = new List<WidgetData>(members);
            _membersView = new ReadOnlyCollection<WidgetData>(_members);

            // The base constructor built the group state before this member
            // copy existed; hand it the live list so its derived pin follows
            // membership and detach/merge changes.
            var state = PanelState as GroupPanelState;
            if (state != null) state.Members = _members;

            InitializeComponent();
            AttachChrome(GroupChrome);

            _views = new List<IWidgetContentView>(_members.Count);
            _slots = new List<ContentControl>(_members.Count);
            _lastFocus = new List<IInputElement>(_members.Count);

            _contentHost = new Grid();
            GroupChrome.ContentHost.Content = _contentHost;

            for (int i = 0; i < _members.Count; i++)
            {
                WidgetData member = _members[i];
                if (member == null)
                {
                    throw new ArgumentException("a group member cannot be null", "members");
                }

                IWidgetContentView view = contents != null && i < contents.Count ? contents[i] : null;
                bool adopted = view != null;

                if (adopted)
                {
                    RequireSameMember(view, member, "contents");
                }
                else
                {
                    view = WidgetViewFactory.Create(member);
                    if (view == null)
                    {
                        throw new ArgumentException(
                            "no content view for the group member kind '" + (member.Kind ?? member.GetType().Name) + "'",
                            "members");
                    }

                    view.Attach(member, this);
                }

                var slot = new ContentControl
                {
                    Content = view.View,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Focusable = false,
                    Visibility = Visibility.Collapsed
                };

                _contentHost.Children.Add(slot);
                _views.Add(view);
                _slots.Add(slot);
                _lastFocus.Add(null);

                if (adopted)
                {
                    view.RebindHost(this);
                }
            }

            _tabStrip = new WidgetGroupTabStrip(_members);
            _tabStrip.TabClicked += SetActive;
            _tabStrip.TabRightClicked += index =>
            {
                SetActive(index);
                ShowWidgetMenu();
            };
            _tabStrip.TabDragCompleted += OnTabDragCompleted;
            _tabStrip.TabReorderRequested += OnTabReorderRequested;
            _tabStrip.TabDragOutsideChanged += OnTabDragOutsideChanged;
            _tabStrip.TabDragMoved += OnTabDragMoved;
            GroupChrome.SetTabRow(_tabStrip, TAB_ROW_HEIGHT);

            GroupChrome.TitleMenuClicked += () =>
            {
                if (ActiveView != null)
                {
                    ActiveView.ToggleTitleMenu(GroupChrome.TitleMenuAnchor);
                }
            };

            // Any size route (drag, programmatic, startup restore) re-fits the
            // active view; the base's resize hook only covers the drag path.
            SizeChanged += (s, e) => ApplyActiveViewSize();

            // The folder's native shell menu needs the real group HWND, which
            // only exists after the window is shown. The active folder tab is
            // the only one allowed to hook it.
            SourceInitialized += (s, e) =>
            {
                var folder = ActiveView as IFolderPanelLifecycle;
                if (folder != null) folder.HandleSourceInitialized();
            };

            // A host deactivation clears the active folder's selection; the
            // other views keep their own (unchanged) lifecycle.
            Deactivated += (s, e) =>
            {
                var folder = ActiveView as IFolderPanelLifecycle;
                if (folder != null) folder.HandleDeactivated();
            };

            Closing += (s, e) =>
            {
                HideDragGhost();
                DeactivateAllViews();
            };

            SetActive(InitialIndex());
        }

        /// <summary>The group state this window drives.</summary>
        public WidgetGroup Group => _group;

        /// <summary>True while the group refuses merges and detaches.</summary>
        public bool IsGroupLocked => _group != null && _group.IsLocked;

        /// <summary>Test seam: true while the tab-detach drag ghost is on screen.</summary>
        internal bool IsDragGhostVisible => _dragGhost != null && _dragGhost.IsVisible;

        /// <summary>Test seam: the live ghost window for the outside tab drag.</summary>
        internal DragGhostWindow DragGhost => _dragGhost;

        /// <summary>The member data in tab order.</summary>
        public IReadOnlyList<WidgetData> Members => _membersView;

        /// <summary>The tab currently shown; -1 before the first switch.</summary>
        public int ActiveIndex => _activeIndex;

        /// <summary>The widget id of the tab currently shown, or null.</summary>
        public string ActiveMemberId
        {
            get
            {
                if (_members == null || _activeIndex < 0 || _activeIndex >= _members.Count) return null;
                WidgetData member = _members[_activeIndex];
                return member == null ? null : member.Id;
            }
        }

        /// <summary>Test seam: the token of the latest activation request; 0 before one.</summary>
        internal int ActivationTokenForChecks => _activationGate.CurrentToken;

        /// <summary>Test seam: true while <paramref name="token"/> is still the latest request.</summary>
        internal bool IsActivationTokenCurrentForChecks(int token) => _activationGate.IsCurrent(token);

        /// <summary>The tab index of a member id; -1 when the id is not in this group.</summary>
        public int IndexOfMember(string widgetId)
        {
            if (string.IsNullOrEmpty(widgetId) || _members == null) return -1;

            for (int i = 0; i < _members.Count; i++)
            {
                WidgetData member = _members[i];
                if (member != null && string.Equals(member.Id, widgetId, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        /// <summary>True when the widget is one of this group's tabs.</summary>
        public bool ContainsWidget(string widgetId) => IndexOfMember(widgetId) >= 0;

        /// <summary>Switches to a member's tab; a non-member id is a no-op.</summary>
        public void SetActiveByWidgetId(string widgetId)
        {
            int index = IndexOfMember(widgetId);
            if (index < 0) return;
            SetActive(index);
        }

        /// <summary>True when the detach entry is offered: unlocked with two or more tabs.</summary>
        public bool CanDetach => _group != null && !_group.IsLocked
            && _members != null && !GroupDragPolicy.ShouldDissolve(_members.Count);

        /// <summary>
        /// Detaches one tab through the manager. The request is raised first
        /// so a caller (or a check) can observe it; the manager performs the
        /// host rebuild.
        /// </summary>
        public bool RequestDetach(int index)
        {
            HideDragGhost();

            if (!CanDetach || index < 0 || index >= _members.Count) return false;

            WidgetData member = _members[index];
            if (member == null || string.IsNullOrEmpty(member.Id)) return false;

            DetachRequested?.Invoke(member.Id);
            return WidgetManager.Instance.DetachWidget(_group.Id, member.Id);
        }

        /// <summary>Detaches the tab currently shown (the menu entry's action).</summary>
        public bool RequestDetachActive() => RequestDetach(_activeIndex);

        /// <summary>
        /// A tab drag finished. A locked group and an in-window release are
        /// no-ops for detach; only an outside release detaches the dragged
        /// tab. An in-window release arrives here once as a completed drag and
        /// is reordered separately through <see cref="TabReorderRequested"/>.
        /// The detach ghost is gone before either outcome runs.
        /// </summary>
        private void OnTabDragCompleted(int index, bool releasedOutsideGroup)
        {
            HideDragGhost();

            if (_group == null || _group.IsLocked) return;
            if (!releasedOutsideGroup) return;

            SetActive(index);
            RequestDetach(index);
        }

        /// <summary>
        /// An in-strip reorder request: the group window itself is untouched
        /// (no close/recreate, no visibility switch, no resize); the manager
        /// moves the member/view/slot/tab entries and persists GroupOrder.
        /// A locked group refuses the request. No ghost is ever worn for it.
        /// </summary>
        private void OnTabReorderRequested(int from, int to)
        {
            HideDragGhost();

            if (_group == null || _group.IsLocked) return;
            WidgetManager.Instance.ReorderGroupMember(
                _group.Id, from, to, _tabStrip == null ? null : _tabStrip.DragMemberSnapshot);
        }

        /// <summary>
        /// Every over-threshold tab move. The group converts the strip point to
        /// a screen point through its own DPI context, remembers it for a ghost
        /// that appears later, and moves a visible ghost with the cursor.
        /// </summary>
        private void OnTabDragMoved(int index, Point stripPoint)
        {
            if (_tabStrip == null) return;

            try
            {
                _lastTabDragScreen = _tabStrip.PointToScreen(stripPoint);
                _hasTabDragScreen = true;
            }
            catch (InvalidOperationException)
            {
                // The strip has no live presentation source (window closing).
                _hasTabDragScreen = false;
                return;
            }

            if (_dragGhost != null && _dragGhost.IsVisible)
            {
                _dragGhost.Follow(_lastTabDragScreen);
            }
        }

        /// <summary>
        /// The drag crossed the group boundary. Outside means detach intent:
        /// the group wears the Explorer-like ghost of the dragged tab's live
        /// view. Re-entering, a locked group and a non-detachable member close
        /// it again; an in-window drag never shows it.
        /// </summary>
        private void OnTabDragOutsideChanged(int index, bool outside)
        {
            if (!outside)
            {
                HideDragGhost();
                return;
            }

            if (_group == null || _group.IsLocked || !CanDetach)
            {
                HideDragGhost();
                return;
            }
            if (_views == null || index < 0 || index >= _views.Count || index >= _members.Count)
            {
                HideDragGhost();
                return;
            }

            IWidgetContentView view = _views[index];
            if (view == null || view.View == null)
            {
                HideDragGhost();
                return;
            }

            if (!_hasTabDragScreen)
            {
                HideDragGhost();
                return;
            }

            // The pressed tab is switched active on press; this makes the
            // live view arranged even for a programmatic drag, so the
            // VisualBrush shows real content rather than an empty slot.
            SetActive(index);
            UpdateLayout();

            // The ghost previews the component at its own pre-group standalone
            // size (an offscreen clone render); a failed render falls back to
            // the live tab view inside the same original-size shell.
            ImageSource preview = WidgetGhostRenderer.Render(_members[index]);

            if (_dragGhost == null)
            {
                _dragGhost = new DragGhostWindow();
            }
            _dragGhost.ShowFor(this, _lastTabDragScreen, view.View, _members[index], preview);
        }

        /// <summary>Closes and forgets the detach ghost; safe on every exit path.</summary>
        private void HideDragGhost()
        {
            if (_dragGhost == null) return;

            DragGhostWindow ghost = _dragGhost;
            _dragGhost = null;
            try
            {
                ghost.DetachPreview();
                ghost.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] drag ghost close: " + ex.Message);
            }
        }

        /// <summary>Re-reads the localized tab labels/tooltips (language switch).</summary>
        public void RefreshLocalization()
        {
            _tabStrip?.RefreshLocalization();
            PushReorderEnabled();
            UpdateUI();
        }

        /// <summary>The bare tab strip above the chrome header.</summary>
        public WidgetGroupTabStrip TabStrip => _tabStrip;

        /// <summary>The per-member content slots, in member order.</summary>
        public IReadOnlyList<ContentControl> ContentSlots => _slots;

        /// <summary>The live member views, in tab order (one per slot).</summary>
        internal IReadOnlyList<IWidgetContentView> Views => _views;

        /// <summary>The view currently shown in the content host.</summary>
        internal IWidgetContentView ActiveView =>
            _activeIndex >= 0 && _views != null && _activeIndex < _views.Count
                ? _views[_activeIndex]
                : null;

        /// <summary>The active member datum, not the first member's.</summary>
        public override WidgetData Data
        {
            get
            {
                IWidgetContentView active = ActiveView;
                return active != null
                    ? active.Data
                    : _members != null && _members.Count > 0 ? _members[0] : null;
            }
        }

        #region IWidgetViewHost

        WidgetChrome IWidgetViewHost.Chrome => GroupChrome;

        bool IWidgetViewHost.IsPanelOpen => IsPanelOpen;

        double IWidgetViewHost.ContentWidth => ContentWidth;

        double IWidgetViewHost.ContentHeight => ContentHeight;

        (double width, double height) IWidgetViewHost.GetPanelSize() => GetPanelSize();

        void IWidgetViewHost.NotifyDataChanged()
        {
            base.NotifyDataChanged();
            RefreshChromeTitle();
            _tabStrip?.SetActive(_activeIndex);
        }

        void IWidgetViewHost.RefreshChromeTitle(string title)
        {
            RefreshChromeTitle(title);
        }

        void IWidgetViewHost.PersistSelection()
        {
            // Keep the standalone selection save semantics; T5 moves group
            // selection persistence onto the group/member model.
            WidgetManager.Instance.SaveConfig();
        }

        void IWidgetViewHost.ShowWidgetMenu() => ShowWidgetMenu(null);

        #endregion

        #region IFolderPanelHost

        IntPtr IFolderPanelHost.PanelHwnd
        {
            get
            {
                try { return new WindowInteropHelper(this).Handle; }
                catch (Exception) { return IntPtr.Zero; }
            }
        }

        bool IFolderPanelHost.IsLocked => _group != null && _group.IsLocked;

        bool IFolderPanelHost.IsPanelPinned => _group != null && _group.IsPanelPinned;

        bool IFolderPanelHost.IsModalOpen => IsModalOpen;

        void IFolderPanelHost.RunModal(Action show) => RunModal(show);

        void IFolderPanelHost.HidePanel() => HidePanel();

        void IFolderPanelHost.FitPanelToWorkArea() => FitPanelToWorkArea();

        #endregion

        #region Panel sizing

        protected override (double width, double height) GetContentSize()
        {
            // The legacy path adds the 40px header and the 2px border; the
            // tab row rides in the content height so 760x540 is the rest size.
            return (CONTENT_WIDTH, CONTENT_HEIGHT + TAB_ROW_HEIGHT);
        }

        protected override (double width, double height) GetMinPanelSize()
        {
            double width = 0;
            double height = 0;
            for (int i = 0; i < _members.Count; i++)
            {
                var (minWidth, minHeight) = KindMin(_members[i]);
                if (minWidth > width) width = minWidth;
                if (minHeight > height) height = minHeight;
            }
            return (width, height + TAB_ROW_HEIGHT);
        }

        private (double width, double height) KindMin(WidgetData member)
        {
            if (member is TodoData) return (TODO_MIN_WIDTH, TODO_MIN_HEIGHT);
            if (member is NoteData) return (NOTE_MIN_WIDTH, NOTE_MIN_HEIGHT);
            if (member is SearchData) return (SEARCH_MIN_WIDTH, SEARCH_MIN_HEIGHT);
            if (member is FolderData) return PanelLayoutAdapter.MinFolderSize(GetItemScale());
            return (DEFAULT_MIN_WIDTH, DEFAULT_MIN_HEIGHT);
        }

        private double GetItemScale()
        {
            try { return WidgetManager.Instance.Config.ItemScale; }
            catch { return 1.1; }
        }

        private double ContentWidth
        {
            get
            {
                double width = ActualWidth > 0 ? ActualWidth : Width;
                Thickness border = GroupChrome == null ? default(Thickness) : GroupChrome.Shell.BorderThickness;
                return Math.Max(0, width - border.Left - border.Right);
            }
        }

        private double ContentHeight
        {
            get
            {
                double height = ActualHeight > 0 ? ActualHeight : Height;
                Thickness border = GroupChrome == null ? default(Thickness) : GroupChrome.Shell.BorderThickness;
                return Math.Max(0, height - border.Top - border.Bottom
                    - TAB_ROW_HEIGHT - WidgetConstants.HEADER_HEIGHT);
            }
        }

        #endregion

        #region Tab switching

        /// <summary>
        /// Shows one member's slot and hides the others. Focus is remembered
        /// inside the leaving view and restored when it is still visible; the
        /// real view instances and the window size never change.
        /// </summary>
        public void SetActive(int index)
        {
            if (_views == null || index < 0 || index >= _views.Count)
            {
                throw new ArgumentOutOfRangeException("index");
            }

            if (index == _activeIndex)
            {
                ApplyActiveViewSize();
                UpdateUI();
                return;
            }

            // The token must be taken before the leaving view is deactivated:
            // any deferred focus work from this switch is then guarded by the
            // latest request, not by an older one still waiting on the queue.
            int token = _activationGate.Begin();

            int old = _activeIndex;
            if (old >= 0)
            {
                RememberFocus(old);
                _views[old].Deactivate();
            }

            _activeIndex = index;
            for (int i = 0; i < _slots.Count; i++)
            {
                _slots[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_members[index] != null)
            {
                _group.ActiveWidgetId = _members[index].Id;
            }

            _views[index].Activate();
            ApplyActiveViewSize();
            UpdateUI();
            RestoreFocus(index, token);
        }

        private int InitialIndex()
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i] != null && _members[i].Id == _group.ActiveWidgetId)
                {
                    return i;
                }
            }
            return 0;
        }

        private void RememberFocus(int index)
        {
            if (_views[index] == null || _views[index].View == null) return;
            if (_views[index].View.IsKeyboardFocusWithin)
            {
                _lastFocus[index] = Keyboard.FocusedElement;
            }
        }

        private void RestoreFocus(int index, int token)
        {
            IInputElement target = _lastFocus[index];
            if (target == null) return;

            // Deferred: the slot's layout must settle before Focus() sticks.
            // The check runs inside the callback because a newer switch or a
            // hide/close can land between queueing and dispatch.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!_activationGate.IsCurrent(token)) return;

                var element = target as UIElement;
                if (element == null || !element.IsVisible) return;
                if (PresentationSource.FromVisual(element) == null) return;

                try { Keyboard.Focus(target); } catch { }
            }));
        }

        private void ApplyActiveViewSize()
        {
            IWidgetContentView active = ActiveView;
            if (active == null) return;

            // The folder adapts its columns/scale to the group content box;
            // the todo calendar owns its list viewport geometry; the other
            // views lay out naturally.
            var folder = active as IFolderPanelLifecycle;
            if (folder != null)
            {
                folder.ApplyAdaptiveLayout();
                return;
            }

            var todo = active as TodoCalendarView;
            if (todo != null) todo.ApplyResizeLayout();
        }

        #endregion

        #region In-place membership

        /// <summary>
        /// Appends one member and its live view without rebuilding the
        /// window. An adopted <paramref name="existingView"/> must already be
        /// attached to the same member and only changes host; a fresh view is
        /// attached normally. The active tab is kept (the first tab becomes
        /// active only when there was none). With <paramref name="grow"/> the
        /// group grows to fit a larger member but never shrinks; with
        /// grow:false the tab/view/slot is added but the group keeps its exact
        /// geometry (a merge into an existing group lets that group's size win).
        /// No show/open animation runs.
        /// </summary>
        internal void AddMember(WidgetData member, IWidgetContentView existingView = null, bool grow = true)
        {
            if (member == null)
            {
                throw new ArgumentException("a group member cannot be null", "member");
            }
            if (string.IsNullOrEmpty(member.Id))
            {
                throw new ArgumentException("a group member needs an id", "member");
            }
            if (IndexOfMember(member.Id) >= 0)
            {
                throw new ArgumentException("the widget is already a group member", "member");
            }

            IWidgetContentView view;
            if (existingView != null)
            {
                RequireSameMember(existingView, member, "existingView");
                view = existingView;
                view.RebindHost(this);
            }
            else
            {
                view = WidgetViewFactory.Create(member);
                if (view == null)
                {
                    throw new ArgumentException(
                        "no content view for the group member kind '" + (member.Kind ?? member.GetType().Name) + "'",
                        "member");
                }

                view.Attach(member, this);
            }

            var slot = new ContentControl
            {
                Content = view.View,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Focusable = false,
                Visibility = Visibility.Collapsed
            };

            _contentHost.Children.Add(slot);
            _members.Add(member);
            _views.Add(view);
            _slots.Add(slot);
            _lastFocus.Add(null);

            _tabStrip.AddTab(member);
            if (grow) GrowForMember(member);

            if (_activeIndex < 0)
            {
                SetActive(0);
            }

            UpdateUI();
        }

        /// <summary>
        /// Removes one member and hands its exact live view back to the
        /// caller (the manager moves it into a standalone window). The view is
        /// deactivated and flushed first; its slot is emptied and detached
        /// from the content grid. An active removal activates the nearest
        /// survivor, an earlier removal shifts the active index down, and an
        /// emptied group keeps its window open with no active member.
        /// </summary>
        internal IWidgetContentView RemoveMember(string widgetId)
        {
            int index = IndexOfMember(widgetId);
            if (index < 0) return null;

            IWidgetContentView view = _views[index];
            ContentControl slot = _slots[index];
            bool wasActive = index == _activeIndex;

            if (view != null)
            {
                view.Deactivate();
                view.FlushPendingEdit();
            }
            if (slot != null)
            {
                slot.Content = null;
                _contentHost.Children.Remove(slot);
            }

            _members.RemoveAt(index);
            _views.RemoveAt(index);
            _slots.RemoveAt(index);
            _lastFocus.RemoveAt(index);
            _tabStrip.RemoveTab(index);

            if (_members.Count == 0)
            {
                _activeIndex = -1;
                _group.ActiveWidgetId = null;
                _tabStrip.SetActive(-1);
                UpdateUI();
                return view;
            }

            if (wasActive)
            {
                _activeIndex = -1;
                SetActive(Math.Min(index, _members.Count - 1));
            }
            else if (index < _activeIndex)
            {
                _activeIndex--;
                UpdateUI();
            }
            else
            {
                UpdateUI();
            }

            return view;
        }

        /// <summary>
        /// Moves one tab so <paramref name="to"/> is its final index. The
        /// member/view/slot/focus bookkeeping and the tab visual move
        /// together, the active member follows its own tab, and every member
        /// gets its new <see cref="WidgetData.GroupOrder"/>. No view is
        /// recreated or hidden and the window is never resized.
        /// </summary>
        internal void ReorderMember(int from, int to)
        {
            int count = _members.Count;
            if (from < 0 || from >= count || to < 0 || to >= count || from == to) return;

            MoveEntry(_members, from, to);
            MoveEntry(_views, from, to);
            MoveEntry(_slots, from, to);
            MoveEntry(_lastFocus, from, to);

            _tabStrip.MoveTab(from, to);
            _activeIndex = MoveIndex(_activeIndex, from, to);

            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i] != null) _members[i].GroupOrder = i;
            }

            _tabStrip.SetActive(_activeIndex);
            RefreshChromeTitle();
        }

        /// <summary>
        /// Grows the grouped panel for one new member: never below the live
        /// group size, the member's own remembered panel size or the kind
        /// minimum (plus the tab row on the height). A smaller member leaves
        /// the size alone. The open fade is never started here.
        /// </summary>
        private void GrowForMember(WidgetData member)
        {
            var (minWidth, minHeight) = KindMin(member);
            double targetWidth = Math.Max(CurrentPanelWidth(), minWidth);
            double targetHeight = Math.Max(CurrentPanelHeight(), minHeight + TAB_ROW_HEIGHT);

            if (member.PanelWidth.HasValue)
            {
                targetWidth = Math.Max(targetWidth, member.PanelWidth.Value);
            }
            if (member.PanelHeight.HasValue)
            {
                targetHeight = Math.Max(targetHeight, member.PanelHeight.Value + TAB_ROW_HEIGHT);
            }

            _group.PanelWidth = targetWidth;
            _group.PanelHeight = targetHeight;

            var (width, height) = GetPanelSize();
            Width = width;
            Height = height;

            if (IsVisible) FitPanelToWorkArea();
        }

        private double CurrentPanelWidth()
        {
            double width = Width;
            if (double.IsNaN(width) || width <= 0) width = _group.PanelWidth ?? 0;
            return width;
        }

        private double CurrentPanelHeight()
        {
            double height = Height;
            if (double.IsNaN(height) || height <= 0) height = _group.PanelHeight ?? 0;
            return height;
        }

        /// <summary>A non-null live view must belong to the member it is added for.</summary>
        private static void RequireSameMember(IWidgetContentView view, WidgetData member, string parameterName)
        {
            WidgetData data = view == null ? null : view.Data;
            if (data == null)
            {
                throw new ArgumentException("the existing view is not attached", parameterName);
            }
            if (!ReferenceEquals(data, member) &&
                !string.Equals(data.Id, member.Id, StringComparison.Ordinal))
            {
                throw new ArgumentException("the existing view belongs to a different widget", parameterName);
            }
        }

        /// <summary>Reorders one list entry; <paramref name="to"/> is the final index.</summary>
        private static void MoveEntry<T>(List<T> list, int from, int to)
        {
            T entry = list[from];
            list.RemoveAt(from);
            list.Insert(to, entry);
        }

        /// <summary>Maps an index through the same move (unchanged for -1).</summary>
        private static int MoveIndex(int index, int from, int to)
        {
            if (index < 0) return index;
            if (index == from) return to;
            if (from < index && to >= index) return index - 1;
            if (from > index && to <= index) return index + 1;
            return index;
        }

        #endregion

        #region Chrome state

        /// <summary>
        /// Toggles the pin of the tab currently shown only. The group pin is
        /// re-derived from every member, so pinning one tab keeps the window
        /// open while the others stay their own pin state.
        /// </summary>
        public override void TogglePin()
        {
            WidgetData active = ActiveView == null ? null : ActiveView.Data;
            if (active == null) return;

            active.IsPanelPinned = !active.IsPanelPinned;
            _group.IsPanelPinned = GroupHostPolicy.AnyMemberPinned(_members);
            UpdateUI();
            NotifyDataChanged();
            OnPanelStateChanged();
        }

        public override void UpdateUI()
        {
            // A locked group never paints the insertion line or reorders.
            PushReorderEnabled();

            // AttachChrome runs UpdateUI before the member views exist.
            if (GroupChrome == null || _views == null || ActiveView == null) return;

            // The group field is compatibility: every member pin is the source
            // of truth, so a pin toggled outside this window (the island, a
            // config repair) is reflected the next time the chrome repaints.
            _group.IsPanelPinned = GroupHostPolicy.AnyMemberPinned(_members);

            RefreshChromeTitle();
            GroupChrome.IsLocked = _group.IsLocked;
            GroupChrome.IsPinned = ActiveView.Data != null && ActiveView.Data.IsPanelPinned;
            GroupChrome.PanelColor = ActiveView.Data == null ? null : ActiveView.Data.Color;
            GroupChrome.TitleMenuEnabled = ActiveView.HasTitleMenu;
            if (_tabStrip != null)
            {
                _tabStrip.RefreshLocalization();
                _tabStrip.SetActive(_activeIndex);
            }
        }

        /// <summary>Projects the group lock onto the strip's reorder flag.</summary>
        private void PushReorderEnabled()
        {
            if (_tabStrip != null) _tabStrip.IsReorderEnabled = _group == null || !_group.IsLocked;
            // A lock toggled mid-drag must drop the detach ghost immediately:
            // a locked group cannot detach, and no new outside event follows.
            if (_group != null && _group.IsLocked) HideDragGhost();
        }

        /// <summary>
        /// Pushes the active context title onto the shared chrome: the todo
        /// date, the note title (or home name), the search name. A note detail
        /// also carries its full title as the header tooltip.
        /// </summary>
        internal void RefreshChromeTitle(string title = null)
        {
            if (GroupChrome == null || ActiveView == null) return;

            string resolved = title ?? ActiveView.ChromeTitle ?? string.Empty;
            GroupChrome.Title = resolved;

            // The note detail carries its full title; the folder carries its
            // selected path (or current browse path) as the tooltip.
            string tooltip = null;
            var note = ActiveView as NotePanelView;
            if (note != null && !note.IsHome)
            {
                tooltip = resolved;
            }
            else
            {
                var folder = ActiveView as IFolderPanelLifecycle;
                if (folder != null) tooltip = folder.ChromeTitleTooltip;
            }
            GroupChrome.TitleText.ToolTip = tooltip;
        }

        protected override void OnPanelShown()
        {
            base.OnPanelShown();
            if (ActiveView != null)
            {
                ActiveView.Activate();
                ApplyActiveViewSize();
            }
        }

        protected override void OnPanelHidden()
        {
            // The whole group is leaving: settle every member's edit and close
            // any open note title menu before the base fade starts. The active
            // folder keeps its chrome during the fade; close/tab switches still
            // use the full deactivation.
            DeactivateAllViews(keepActiveFolderChrome: true);
            base.OnPanelHidden();
        }

        protected override void OnPanelHiding()
        {
            // HidePanel reaches here before its expanded/fade branch, so the
            // ghost is dropped on every hide path.
            HideDragGhost();
            base.OnPanelHiding();
        }

        protected override void OnPanelSizeChanged()
        {
            base.OnPanelSizeChanged();
            ApplyActiveViewSize();
        }

        protected override void OnPanelStateChanged()
        {
            base.OnPanelStateChanged();
            // T5 mirrors the group geometry onto WidgetGroup; T4 never writes
            // a member's PanelWidth/PanelHeight from the group shell.
        }

        /// <summary>
        /// A group delete must let every folder member restore its stored
        /// files before the group members disappear.
        /// </summary>
        protected override void OnDeleting()
        {
            base.OnDeleting();
            if (_views == null) return;

            for (int i = 0; i < _views.Count; i++)
            {
                var folder = _views[i] as IFolderPanelLifecycle;
                if (folder != null) folder.OnDeleting();
            }
        }

        /// <summary>
        /// The shared item-size menu reached an open group: re-adapt the active
        /// folder in place, never resize the group (the standalone folder may
        /// grow its own panel to its new minimum; the group keeps its size).
        /// </summary>
        internal void ApplyItemScaleChange()
        {
            var folder = ActiveView as IFolderPanelLifecycle;
            if (folder != null)
            {
                folder.ApplyAdaptiveLayout();
                return;
            }
            UpdateUI();
        }

        private void DeactivateAllViews(bool keepActiveFolderChrome = false)
        {
            // Hide/close: a queued focus request from this surface must never
            // fire after the group has left the screen.
            _activationGate.Invalidate();
            if (_views == null) return;
            for (int i = 0; i < _views.Count; i++)
            {
                if (_views[i] == null) continue;

                if (keepActiveFolderChrome && i == _activeIndex)
                {
                    var folder = _views[i] as IFolderPanelLifecycle;
                    if (folder != null)
                    {
                        folder.DeactivateForPanelHide();
                        _views[i].FlushPendingEdit();
                        continue;
                    }
                }

                _views[i].Deactivate();
                _views[i].FlushPendingEdit();
            }
        }

        #endregion

        #region Menu

        /// <summary>
        /// Test seam: when set, an opened menu is handed to this callback
        /// instead of a real popup, so checks can inspect it without pumping a
        /// live ContextMenu.
        /// </summary>
        internal Action<ContextMenu> MenuOpenedForChecks { get; set; }

        private void OpenMenu(ContextMenu menu)
        {
            if (menu == null) return;
            if (MenuOpenedForChecks != null)
            {
                MenuOpenedForChecks(menu);
                return;
            }
            menu.IsOpen = true;
        }

        /// <summary>
        /// The current/tab menu: one switch entry per member (checked on the
        /// active one), the live detach entry (T6) and hide-group. The shared
        /// builder keeps the structure in one place for the tab strip and the
        /// island's member menu; the title row opens the active member's menu
        /// instead (see <see cref="ShowHeaderMenu"/>).
        /// </summary>
        internal ContextMenu BuildWidgetMenu(Action onClosed = null)
        {
            return WidgetGroupMenuBuilder.Build(
                _members,
                _activeIndex,
                _group != null && _group.IsLocked,
                SetActive,
                () => RequestDetachActive(),
                HidePanel,
                onClosed);
        }

        public override void ShowWidgetMenu(Action onClosed = null)
        {
            ContextMenu menu = BuildWidgetMenu(onClosed);
            OpenMenu(menu);
        }

        /// <summary>
        /// The title row opens the active member's normal widget menu. Rename,
        /// colour, font, item size and holiday region act on that member's
        /// datum; lock stays the group lock (like the header lock button), pin
        /// toggles the active member, and delete removes only that member.
        /// </summary>
        protected override void ShowHeaderMenu()
        {
            WidgetData data = Data;
            if (data == null) return;

            var actions = new WidgetMenuActions
            {
                IsPanelOpen = IsPanelOpen,
                IsLocked = _group != null && _group.IsLocked,
                ToggleVisibility = () => WidgetManager.Instance.ToggleWidgetPanel(this),
                Rename = () => RenameActiveMember(data),
                ChangeColor = () => ChangeActiveMemberColor(data),
                ToggleLock = ToggleLock,
                TogglePin = TogglePin,
                Delete = () => DeleteActiveMember(data),
                Changed = () =>
                {
                    // The standalone hosts refresh their own view inside
                    // UpdateUI; the group must refresh the active member here
                    // so a font/holiday edit repaints immediately.
                    RefreshActiveView();
                    UpdateUI();
                    NotifyDataChanged();
                }
            };

            OpenMenu(WidgetMenuBuilder.Build(data, actions, null));
        }

        private void RenameActiveMember(WidgetData data)
        {
            string newName = ShowInputModal(
                Localization.Get("Dialog_Rename"),
                Localization.Get("Dialog_EnterName"),
                data.Name,
                null);

            if (string.IsNullOrWhiteSpace(newName)) return;

            data.Name = newName.Trim();
            UpdateUI();
            NotifyDataChanged();
        }

        private void ChangeActiveMemberColor(WidgetData data)
        {
            RunModal(() => ColorPaletteDialog.Show(
                this,
                Localization.Get("Dialog_PickColor"),
                UiTokens.FolderPalette,
                data.Color,
                allowDefault: false,
                defaultText: null,
                onPick: color =>
                {
                    data.Color = color;
                    RefreshTheme();
                    RefreshActiveView();
                    NotifyDataChanged();
                },
                onDefault: null));
        }

        private void RefreshActiveView()
        {
            IWidgetContentView active = ActiveView;
            if (active != null) active.Refresh();
        }

        private void DeleteActiveMember(WidgetData data)
        {
            if (!ShowConfirmModal(Localization.Format("Dialog_DeleteWidget", data.Name))) return;

            // The manager takes the member out of the group; DiscardContentView
            // runs the folder's OnDeleting so stored files are ejected first.
            WidgetManager.Instance.RemoveWidget(data.Id);
        }

        #endregion

        #region Window-level input routing

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (CancelTabDragFromEscape(e.Key))
            {
                e.Handled = true;
                return;
            }

            IWidgetContentView active = ActiveView;
            if (active != null && active.HandlePreviewKeyDown(e))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// Escape abandons an in-flight tab drag before the active view sees
        /// the key: the gesture is dropped without a completion/detach and the
        /// detach ghost is closed. Returns false for any other key or when no
        /// drag is running, so normal key routing is untouched. Kept internal
        /// so a check can verify the cancel without an OS key event.
        /// </summary>
        internal bool CancelTabDragFromEscape(Key key)
        {
            if (key != Key.Escape || _tabStrip == null || !_tabStrip.IsTabDragActive) return false;

            _tabStrip.CancelTabDrag();
            HideDragGhost();
            return true;
        }

        /// <summary>
        /// The bubbling half of the folder input: clipboard, Backspace, Esc,
        /// F5/Enter/Apps. Todo/Note/Search do not implement the optional
        /// lifecycle interface, so they are untouched.
        /// </summary>
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            var folder = ActiveView as IFolderPanelLifecycle;
            if (folder != null) folder.HandleKeyDown(e);
        }

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            IWidgetContentView active = ActiveView;
            if (active != null)
            {
                active.HandleMouseDownOutsideEditor(e.OriginalSource);
            }
        }

        #endregion

        /// <summary>
        /// The panel geometry proxy: the group owns the shell's id, placement,
        /// size, open state, lock and pin, while the base keeps reading the
        /// first member's name/colour for its chrome fallbacks.
        /// </summary>
        private sealed class GroupPanelState : IPanelState
        {
            private readonly WidgetGroup _group;

            public GroupPanelState(WidgetGroup group)
            {
                _group = group;
            }

            /// <summary>
            /// The live member list, assigned by the window after its copy.
            /// While null (the base constructor's first UpdateUI) the derived
            /// pin is simply false.
            /// </summary>
            public IList<WidgetData> Members { get; set; }

            public string Id => _group.Id;
            public double? PanelX { get => _group.PanelX; set => _group.PanelX = value; }
            public double? PanelY { get => _group.PanelY; set => _group.PanelY = value; }
            public double? PanelDpiScale { get => _group.PanelDpiScale; set => _group.PanelDpiScale = value; }
            public double? PanelWidth { get => _group.PanelWidth; set => _group.PanelWidth = value; }
            public double? PanelHeight { get => _group.PanelHeight; set => _group.PanelHeight = value; }
            public bool IsExpanded { get => _group.IsExpanded; set => _group.IsExpanded = value; }
            public bool IsLocked { get => _group.IsLocked; set => _group.IsLocked = value; }

            /// <summary>
            /// Any pinned member keeps the whole group open on blur. The setter
            /// still writes the group's compatibility field.
            /// </summary>
            public bool IsPanelPinned
            {
                get => GroupHostPolicy.AnyMemberPinned(Members);
                set => _group.IsPanelPinned = value;
            }
        }

        private static WidgetData FirstMember(IList<WidgetData> members)
        {
            if (members == null)
            {
                throw new ArgumentException("a group needs a member list", "members");
            }
            if (members.Count < 2)
            {
                throw new ArgumentException("a group needs at least two members", "members");
            }
            if (members[0] == null)
            {
                throw new ArgumentException("the first group member cannot be null", "members");
            }
            return members[0];
        }

        private static IPanelState CreateState(WidgetGroup group)
        {
            if (group == null)
            {
                throw new ArgumentException("a group needs its group state", "group");
            }
            return new GroupPanelState(group);
        }
    }
}
