using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.Controls
{
    /// <summary>
    /// One panel hosting several widget kinds as tabs. The layout is the shared
    /// chrome with a 32px bare tab row above its 40px header; every member owns
    /// a real view instance in its own slot, and switching tabs only toggles
    /// visibility - views are never recreated and the window never resizes.
    /// The group itself owns the panel geometry, open state, lock and pin.
    /// </summary>
    public partial class WidgetGroupWindow : WidgetWindowBase, IWidgetViewHost, IGroupDragHost
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
        private readonly IList<WidgetData> _members;
        private readonly ReadOnlyCollection<WidgetData> _membersView;
        private readonly IWidgetContentView[] _views;
        private readonly ContentControl[] _slots;
        private readonly IInputElement[] _lastFocus;

        private WidgetGroupTabStrip _tabStrip;
        private int _activeIndex = -1;

        /// <summary>
        /// Raised when the user asks to detach a tab (menu entry or a tab
        /// dragged outside the group), before the manager is called. A check
        /// can observe the request without a running manager; production goes
        /// on to <see cref="WidgetManager.DetachWidget"/>.
        /// </summary>
        public event Action<string> DetachRequested;

        public WidgetGroupWindow(WidgetGroup group, IList<WidgetData> members)
            : base(FirstMember(members), CreateState(group))
        {
            _group = group;
            _members = members;
            _membersView = new ReadOnlyCollection<WidgetData>(members);

            InitializeComponent();
            AttachChrome(GroupChrome);

            _views = new IWidgetContentView[_members.Count];
            _slots = new ContentControl[_members.Count];
            _lastFocus = new IInputElement[_members.Count];

            var contentHost = new Grid();
            GroupChrome.ContentHost.Content = contentHost;

            for (int i = 0; i < _members.Count; i++)
            {
                WidgetData member = _members[i];
                if (member == null)
                {
                    throw new ArgumentException("a group member cannot be null", "members");
                }

                IWidgetContentView view = WidgetViewFactory.Create(member);
                if (view == null)
                {
                    throw new ArgumentException(
                        "no content view for the group member kind '" + (member.Kind ?? member.GetType().Name) + "'",
                        "members");
                }

                view.Attach(member, this);

                var slot = new ContentControl
                {
                    Content = view.View,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Focusable = false,
                    Visibility = Visibility.Collapsed
                };

                contentHost.Children.Add(slot);
                _views[i] = view;
                _slots[i] = slot;
            }

            _tabStrip = new WidgetGroupTabStrip(_members);
            _tabStrip.TabClicked += SetActive;
            _tabStrip.TabRightClicked += index =>
            {
                SetActive(index);
                ShowWidgetMenu();
            };
            _tabStrip.TabDragCompleted += OnTabDragCompleted;
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

            Closing += (s, e) => DeactivateAllViews();

            SetActive(InitialIndex());
        }

        /// <summary>The group state this window drives.</summary>
        public WidgetGroup Group => _group;

        /// <summary>True while the group refuses merges and detaches.</summary>
        public bool IsGroupLocked => _group != null && _group.IsLocked;

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
        /// no-ops (v1 does not reorder tabs); an outside release detaches the
        /// dragged tab.
        /// </summary>
        private void OnTabDragCompleted(int index, bool releasedOutsideGroup)
        {
            if (_group == null || _group.IsLocked) return;
            if (!releasedOutsideGroup) return;

            SetActive(index);
            RequestDetach(index);
        }

        /// <summary>Re-reads the localized tab labels/tooltips (language switch).</summary>
        public void RefreshLocalization()
        {
            _tabStrip?.RefreshLocalization();
            UpdateUI();
        }

        /// <summary>The bare tab strip above the chrome header.</summary>
        public WidgetGroupTabStrip TabStrip => _tabStrip;

        /// <summary>The per-member content slots, in member order.</summary>
        public IReadOnlyList<ContentControl> ContentSlots => _slots;

        /// <summary>The view currently shown in the content host.</summary>
        internal IWidgetContentView ActiveView =>
            _activeIndex >= 0 && _views != null ? _views[_activeIndex] : null;

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

        private static (double width, double height) KindMin(WidgetData member)
        {
            if (member is TodoData) return (TODO_MIN_WIDTH, TODO_MIN_HEIGHT);
            if (member is NoteData) return (NOTE_MIN_WIDTH, NOTE_MIN_HEIGHT);
            if (member is SearchData) return (SEARCH_MIN_WIDTH, SEARCH_MIN_HEIGHT);
            return (DEFAULT_MIN_WIDTH, DEFAULT_MIN_HEIGHT);
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
            if (_views == null || index < 0 || index >= _views.Length)
            {
                throw new ArgumentOutOfRangeException("index");
            }

            if (index == _activeIndex)
            {
                ApplyActiveViewSize();
                UpdateUI();
                return;
            }

            int old = _activeIndex;
            if (old >= 0)
            {
                RememberFocus(old);
                _views[old].Deactivate();
            }

            _activeIndex = index;
            for (int i = 0; i < _slots.Length; i++)
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
            RestoreFocus(index);
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

        private void RestoreFocus(int index)
        {
            IInputElement target = _lastFocus[index];
            if (target == null) return;

            var element = target as UIElement;
            if (element == null || !element.IsVisible) return;

            // Deferred: the slot's layout must settle before Focus() sticks.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                try { Keyboard.Focus(target); } catch { }
            }));
        }

        private void ApplyActiveViewSize()
        {
            IWidgetContentView active = ActiveView;
            if (active == null) return;

            // The todo calendar owns its list viewport geometry; the other
            // views lay out naturally from the content box.
            var todo = active as TodoCalendarView;
            if (todo != null) todo.ApplyResizeLayout();
        }

        #endregion

        #region Chrome state

        public override void UpdateUI()
        {
            // AttachChrome runs UpdateUI before the member views exist.
            if (GroupChrome == null || _views == null || ActiveView == null) return;

            RefreshChromeTitle();
            GroupChrome.IsLocked = _group.IsLocked;
            GroupChrome.IsPinned = _group.IsPanelPinned;
            GroupChrome.PanelColor = ActiveView.Data == null ? null : ActiveView.Data.Color;
            GroupChrome.TitleMenuEnabled = ActiveView.HasTitleMenu;
            if (_tabStrip != null)
            {
                _tabStrip.RefreshLocalization();
                _tabStrip.SetActive(_activeIndex);
            }
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

            var note = ActiveView as NotePanelView;
            GroupChrome.TitleText.ToolTip = note != null && !note.IsHome ? resolved : null;
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
            // any open note title menu before the base fade starts.
            DeactivateAllViews();
            base.OnPanelHidden();
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

        private void DeactivateAllViews()
        {
            if (_views == null) return;
            for (int i = 0; i < _views.Length; i++)
            {
                if (_views[i] == null) continue;
                _views[i].Deactivate();
                _views[i].FlushPendingEdit();
            }
        }

        #endregion

        #region Menu

        /// <summary>
        /// The current/tab menu: one switch entry per member (checked on the
        /// active one), the live detach entry (T6) and hide-group. The shared
        /// builder keeps the structure in one place for the header and the
        /// island's member menu.
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
            menu.IsOpen = true;
        }

        #endregion

        #region Window-level input routing

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            IWidgetContentView active = ActiveView;
            if (active != null && active.HandlePreviewKeyDown(e))
            {
                e.Handled = true;
            }
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

            public string Id => _group.Id;
            public double? PanelX { get => _group.PanelX; set => _group.PanelX = value; }
            public double? PanelY { get => _group.PanelY; set => _group.PanelY = value; }
            public double? PanelDpiScale { get => _group.PanelDpiScale; set => _group.PanelDpiScale = value; }
            public double? PanelWidth { get => _group.PanelWidth; set => _group.PanelWidth = value; }
            public double? PanelHeight { get => _group.PanelHeight; set => _group.PanelHeight = value; }
            public bool IsExpanded { get => _group.IsExpanded; set => _group.IsExpanded = value; }
            public bool IsLocked { get => _group.IsLocked; set => _group.IsLocked = value; }
            public bool IsPanelPinned { get => _group.IsPanelPinned; set => _group.IsPanelPinned = value; }
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
