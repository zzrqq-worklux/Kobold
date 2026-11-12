using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Core.Icons;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// The bare tab row of a group window: one icon + label tab per member,
    /// no pill/background, with the active member marked by a 2px
    /// member-coloured underline. A tab switch only repaints this strip; the
    /// group window owns the views and their visibility. Membership can be
    /// mutated in place (<see cref="AddTab"/>, <see cref="RemoveTab"/>,
    /// <see cref="MoveTab"/>) so the group window never has to rebuild its
    /// tab row, and the tab handlers resolve their member's current index at
    /// event time. An over-threshold drag also paints a 2px insertion line in
    /// the target gap and reports the requested move in place.
    /// </summary>
    public class WidgetGroupTabStrip : Grid
    {
        public const double RowHeight = 32;
        public const double LeftInset = 16;

        /// <summary>The press movement (DIP) that turns a tab press into a drag-out.</summary>
        public const double DragThreshold = GroupDragPolicy.DefaultDragThreshold;

        /// <summary>
        /// The host-edge band (DIP) a tab drag must leave before it counts as
        /// an outside/detach gesture. A point 1-7 DIP outside is still treated
        /// as in-group, so an accidental release on the border neither detaches
        /// nor reorders.
        /// </summary>
        public const double DetachEdgeMargin = 8;

        private const double TAB_GAP = 22;
        private const double ICON_SIZE = 11;
        private const double INSERT_LINE_WIDTH = 2;

        /// <summary>
        /// The widest a tab label may grow before it ellipsizes. Member names
        /// are free text, so one long name must not push the whole tab row.
        /// </summary>
        private const double LABEL_MAX_WIDTH = 160;

        private readonly List<WidgetData> _members;
        private readonly List<Grid> _tabs = new List<Grid>();
        private readonly List<Border> _underlines = new List<Border>();
        private readonly List<TextBlock> _labels = new List<TextBlock>();
        private readonly List<IconPresenter> _icons = new List<IconPresenter>();
        private readonly Border _insertIndicator;
        private int _activeIndex = -1;
        private bool _isReorderEnabled = true;

        // Tab drag gesture: the pressed tab, its anchor and whether the press
        // passed the threshold. A press/release that never crosses it stays a
        // click and is never reported as a drag. _dragOutside tracks the last
        // reported host-boundary state so the owner sees each crossing once.
        private int _dragIndex = -1;
        private Point _dragStart;
        private bool _isDraggingTab;
        private bool _dragOutside;

        // The ordered member ids at BeginTabDrag. The group window hands this
        // to the manager on release so a membership change mid-drag (another
        // merge/detach) cannot turn a stale source/target index into a move.
        private ReadOnlyCollection<string> _dragMemberSnapshot =
            new ReadOnlyCollection<string>(new string[0]);

        /// <summary>A tab was left-clicked; the argument is the member index.</summary>
        public event Action<int> TabClicked;

        /// <summary>A tab was right-clicked; the argument is the member index.</summary>
        public event Action<int> TabRightClicked;

        /// <summary>
        /// A real tab drag finished: the member index and whether it was
        /// released outside the hosting window. A click never raises it. An
        /// in-window release is still reported here before any reorder.
        /// </summary>
        public event Action<int, bool> TabDragCompleted;

        /// <summary>
        /// An in-window tab drag asked to move a member: the source index and
        /// the final index after the move. Never raised for a same-gap drop,
        /// for a click, or while <see cref="IsReorderEnabled"/> is false.
        /// </summary>
        public event Action<int, int> TabReorderRequested;

        /// <summary>
        /// An over-threshold tab drag crossed the hosting window's boundary in
        /// either direction: the dragged index and whether it is now outside.
        /// Raised once per crossing; the group uses it to show/hide the detach
        /// ghost (outside) without disturbing the P1 reorder behaviour.
        /// </summary>
        public event Action<int, bool> TabDragOutsideChanged;

        /// <summary>
        /// Every over-threshold tab drag move: the dragged index and the
        /// strip-relative point. The group converts it to screen coordinates to
        /// place the detach ghost at the cursor.
        /// </summary>
        public event Action<int, Point> TabDragMoved;

        public WidgetGroupTabStrip(IList<WidgetData> members)
        {
            // A private copy: Add/Remove/Move keep the visuals and this list
            // in step, and the owning window keeps its own member list.
            _members = members == null ? new List<WidgetData>() : new List<WidgetData>(members);

            Height = RowHeight;
            Background = Brushes.Transparent;

            TabRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(LeftInset, 0, LeftInset, 0)
            };
            Children.Add(TabRow);

            // A bare 2px line over the tab row; it only marks the insertion
            // gap and never takes a click.
            _insertIndicator = new Border
            {
                Width = INSERT_LINE_WIDTH,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Children.Add(_insertIndicator);

            for (int i = 0; i < _members.Count; i++)
            {
                BuildTab(_members[i]);
            }

            ApplyVisuals();
        }

        /// <summary>The horizontal host of the tab roots (32px strip, 16px left inset).</summary>
        public StackPanel TabRow { get; }

        /// <summary>The tab roots, one per member, in member order.</summary>
        public IReadOnlyList<Grid> TabButtons => _tabs;

        /// <summary>The 2px bottom underlines, one per member, in member order.</summary>
        public IReadOnlyList<Border> Underlines => _underlines;

        /// <summary>
        /// The current tab label strings in tab order; a check seam pinning
        /// that a tab shows its member's name rather than the kind label.
        /// </summary>
        internal IReadOnlyList<string> LabelTextsForChecks
        {
            get
            {
                var texts = new List<string>(_labels.Count);
                for (int i = 0; i < _labels.Count; i++) texts.Add(_labels[i].Text);
                return texts;
            }
        }

        /// <summary>The 2px insertion line; read-only test seam.</summary>
        public FrameworkElement InsertIndicator => _insertIndicator;

        /// <summary>True while the insertion line is shown over the tab row.</summary>
        public bool IsInsertIndicatorVisible =>
            _insertIndicator.Visibility == Visibility.Visible;

        /// <summary>
        /// Whether an in-window tab drag may report a reorder. A locked group
        /// pushes false through its UpdateUI; a false value also hides the
        /// insertion line and suppresses <see cref="TabReorderRequested"/>.
        /// </summary>
        public bool IsReorderEnabled
        {
            get { return _isReorderEnabled; }
            set
            {
                _isReorderEnabled = value;
                if (!value) HideInsertIndicator();
            }
        }

        /// <summary>
        /// Repaints the active/inactive/hover states for the given member. A
        /// -1 or out-of-range index clears every underline instead of
        /// throwing, so an empty group or a just-removed tab is a safe state.
        /// </summary>
        public void SetActive(int index)
        {
            _activeIndex = index >= 0 && index < _tabs.Count ? index : -1;
            ApplyVisuals();
        }

        /// <summary>Appends one tab/icon/label/underline for a new last member.</summary>
        public void AddTab(WidgetData member)
        {
            _members.Add(member);
            BuildTab(member);
            ApplyVisuals();
        }

        /// <summary>
        /// Removes the tab at <paramref name="index"/> and its visual arrays,
        /// keeping the active underline on the same member when it survives.
        /// </summary>
        public void RemoveTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            HideInsertIndicator();

            TabRow.Children.Remove(_tabs[index]);
            _tabs.RemoveAt(index);
            _underlines.RemoveAt(index);
            _labels.RemoveAt(index);
            _icons.RemoveAt(index);
            if (index < _members.Count) _members.RemoveAt(index);

            if (_dragIndex == index) CancelTabDrag();
            else if (_dragIndex > index) _dragIndex--;

            if (_activeIndex == index) _activeIndex = -1;
            else if (_activeIndex > index) _activeIndex--;

            ApplyVisuals();
        }

        /// <summary>
        /// Moves one tab and its member-list entry so <paramref name="to"/> is
        /// the final index. The active tab follows the same member, and the
        /// in-flight drag (if any) follows the moved tab.
        /// </summary>
        public void MoveTab(int from, int to)
        {
            HideInsertIndicator();

            int count = _tabs.Count;
            if (from < 0 || from >= count || to < 0 || to >= count || from == to) return;

            MoveEntry(_tabs, from, to);
            MoveEntry(_underlines, from, to);
            MoveEntry(_labels, from, to);
            MoveEntry(_icons, from, to);
            if (_members.Count == count) MoveEntry(_members, from, to);

            Grid tab = _tabs[to];
            TabRow.Children.Remove(tab);
            TabRow.Children.Insert(to, tab);

            _activeIndex = MoveIndex(_activeIndex, from, to);
            _dragIndex = MoveIndex(_dragIndex, from, to);

            ApplyVisuals();
        }

        /// <summary>True between an over-threshold tab press and its release.</summary>
        public bool IsTabDragActive => _dragIndex >= 0 && _isDraggingTab;

        /// <summary>
        /// The ordered member ids captured at the last <see cref="BeginTabDrag"/>
        /// (a deep ordinal copy). The group window passes it to the manager so
        /// a reorder is refused when the membership changed mid-drag.
        /// </summary>
        public IReadOnlyList<string> DragMemberSnapshot => _dragMemberSnapshot;

        /// <summary>
        /// The insertion gap (0..count) for a strip-relative pointer: the
        /// first tab whose centre is right of the pointer wins, otherwise the
        /// end gap. Tabs are laid out left to right, so this is the gap the
        /// line paints.
        /// </summary>
        public int GetInsertionIndex(Point stripPoint)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (stripPoint.X < TabCenterInStrip(i)) return i;
            }
            return _tabs.Count;
        }

        /// <summary>
        /// Starts a tab drag gesture at the given strip-relative point. Kept
        /// public so the checks can drive the gesture without synthesizing a
        /// real mouse; the tab's own mouse handlers call it too.
        /// </summary>
        public bool BeginTabDrag(int index, Point point)
        {
            if (index < 0 || index >= _tabs.Count) return false;

            HideInsertIndicator();
            CaptureDragSnapshot();
            _dragIndex = index;
            _dragStart = point;
            _isDraggingTab = false;
            _dragOutside = false;
            SetTabCursor(index, CursorHelper.OpenHand);
            return true;
        }

        /// <summary>Deep-copies the current member ids in ordinal order.</summary>
        private void CaptureDragSnapshot()
        {
            var ids = new string[_members.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = _members[i] == null ? null : _members[i].Id;
            }
            _dragMemberSnapshot = new ReadOnlyCollection<string>(ids);
        }

        /// <summary>
        /// Follows the pressed tab. Returns true once the movement crossed
        /// <see cref="DragThreshold"/>; a sub-threshold move is not a drag.
        /// An enabled reorder paints the insertion line for the current gap
        /// while the pointer stays inside the hosting window. The tab lifts to
        /// the grab hand on the same threshold crossing. Every over-threshold
        /// move reports its strip point, and each host-boundary crossing is
        /// reported once through <see cref="TabDragOutsideChanged"/>.
        /// </summary>
        public bool UpdateTabDrag(Point point)
        {
            if (_dragIndex < 0) return false;

            if (!_isDraggingTab)
            {
                if (!GroupDragPolicy.IsDragOut(point.X - _dragStart.X, point.Y - _dragStart.Y)) return false;
                _isDraggingTab = true;
                SetTabCursor(_dragIndex, CursorHelper.GrabHand);
            }

            bool physicallyOutside = IsOutsideHost(point);
            bool outside = IsOutsideHost(point, DetachEdgeMargin);
            if (_isReorderEnabled && !physicallyOutside)
            {
                ShowInsertIndicator(GetInsertionIndex(point));
            }
            else
            {
                HideInsertIndicator();
            }

            TabDragMoved?.Invoke(_dragIndex, point);

            if (outside != _dragOutside)
            {
                _dragOutside = outside;
                TabDragOutsideChanged?.Invoke(_dragIndex, outside);
            }
            return true;
        }

        /// <summary>
        /// Ends the gesture. Returns false for a click (no threshold, no
        /// event); otherwise raises <see cref="TabDragCompleted"/> with the
        /// release point tested against the hosting window's bounds plus
        /// <see cref="DetachEdgeMargin"/>, then - for a release physically
        /// inside the window with reorder enabled - raises
        /// <see cref="TabReorderRequested"/> when the final index differs. A
        /// release outside but inside the edge band is a no-op: no detach and
        /// no reorder.
        /// </summary>
        public bool EndTabDrag(Point point)
        {
            int index = _dragIndex;
            bool wasDragging = _isDraggingTab;
            bool wasOutside = _dragOutside;
            RestoreTabCursor(index);
            _dragIndex = -1;
            _isDraggingTab = false;
            _dragOutside = false;
            HideInsertIndicator();

            // The ghost must be gone before the completion/detach event runs.
            if (wasOutside) TabDragOutsideChanged?.Invoke(index, false);
            if (index < 0 || !wasDragging) return false;

            bool physicallyOutside = IsOutsideHost(point);
            bool outside = IsOutsideHost(point, DetachEdgeMargin);
            TabDragCompleted?.Invoke(index, outside);

            if (!outside && !physicallyOutside && _isReorderEnabled)
            {
                int insertion = GetInsertionIndex(point);
                int to = GroupDragPolicy.ReorderIndex(index, insertion, _tabs.Count);
                if (to != index) TabReorderRequested?.Invoke(index, to);
            }
            return true;
        }

        /// <summary>Drops an in-flight gesture without reporting it (capture lost).</summary>
        public void CancelTabDrag()
        {
            int index = _dragIndex;
            bool wasOutside = _dragOutside;
            RestoreTabCursor(index);
            _dragIndex = -1;
            _isDraggingTab = false;
            _dragOutside = false;
            HideInsertIndicator();

            if (wasOutside) TabDragOutsideChanged?.Invoke(index, false);
        }

        /// <summary>
        /// The one grab-hand affordance the strip shares with the header: the
        /// open hand at rest and the closed hand while a real drag runs. The
        /// restore is safe for every exit path (release, cancel, lost capture).
        /// </summary>
        private void RestoreTabCursor(int index)
        {
            SetTabCursor(index, CursorHelper.OpenHand);
        }

        private void SetTabCursor(int index, Cursor cursor)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _tabs[index].Cursor = cursor;
        }

        /// <summary>
        /// True when the strip-relative point maps outside the hosting
        /// window's client box. A strip outside any window counts as inside.
        /// </summary>
        public bool IsOutsideHost(Point stripPoint)
        {
            return IsOutsideHost(stripPoint, 0);
        }

        /// <summary>
        /// True when the strip-relative point maps outside the hosting
        /// window's client box by more than <paramref name="margin"/> DIP on
        /// the relevant axis. A point 1-7 DIP outside with the default 8 DIP
        /// detach margin still counts as inside, so the detach gesture only
        /// starts once the pointer has truly left the group.
        /// </summary>
        public bool IsOutsideHost(Point stripPoint, double margin)
        {
            Window host = Window.GetWindow(this);
            if (host == null) return false;

            Point inWindow = TranslatePoint(stripPoint, host);
            if (double.IsNaN(inWindow.X) || double.IsNaN(inWindow.Y)) return false;

            double band = margin > 0 ? margin : 0;
            return inWindow.X < -band || inWindow.Y < -band
                || inWindow.X > host.ActualWidth + band || inWindow.Y > host.ActualHeight + band;
        }

        /// <summary>
        /// Re-reads each tab's member name and tooltip. Language switches call
        /// this through the group window's UpdateUI; nothing else about the
        /// strip changes.
        /// </summary>
        public void RefreshLocalization()
        {
            int count = Math.Min(_labels.Count, _members.Count);
            for (int i = 0; i < count; i++)
            {
                WidgetData member = _members[i];
                string label = MemberLabel(member);
                _labels[i].Text = label;
                _tabs[i].ToolTip = TabToolTip(member, label);
            }
        }

        private void BuildTab(WidgetData member)
        {
            var tab = new Grid
            {
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, TAB_GAP, 0),
                // One grab-hand affordance for every group drag surface: the
                // open hand at rest, the closed hand once a drag starts.
                Cursor = CursorHelper.OpenHand,
                VerticalAlignment = VerticalAlignment.Stretch,
                // The handler looks this member up by identity at event time;
                // the construction index would be stale after a reorder.
                Tag = member
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var icon = new IconPresenter
            {
                Icon = IconFor(member),
                Size = ICON_SIZE,
                StrokeThickness = 1.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var label = new TextBlock
            {
                Text = MemberLabel(member),
                FontSize = UiTokens.FontBody,
                VerticalAlignment = VerticalAlignment.Center,
                // A long member name stays one line and ellipsizes; the tab
                // tooltip still carries the full name.
                MaxWidth = LABEL_MAX_WIDTH,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            content.Children.Add(icon);
            content.Children.Add(label);

            tab.ToolTip = TabToolTip(member, label.Text);

            var underline = new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = MemberBrush(member),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            tab.Children.Add(content);
            tab.Children.Add(underline);

            tab.MouseLeftButtonDown += (s, e) =>
            {
                // The press both switches (click semantics) and arms the drag;
                // only a movement past the threshold turns it into a drag-out.
                // A removed tab resolves to -1 and is a no-op.
                int current = IndexOfMember(member);
                if (current < 0) return;

                BeginTabDrag(current, e.GetPosition(this));
                tab.CaptureMouse();
                TabClicked?.Invoke(current);
                e.Handled = true;
            };
            tab.MouseMove += (s, e) =>
            {
                int current = IndexOfMember(member);
                if (current < 0 || _dragIndex != current || e.LeftButton != MouseButtonState.Pressed) return;
                UpdateTabDrag(e.GetPosition(this));
            };
            tab.MouseLeftButtonUp += (s, e) =>
            {
                int current = IndexOfMember(member);
                if (current < 0 || _dragIndex != current) return;

                EndTabDrag(e.GetPosition(this));
                if (tab.IsMouseCaptured) tab.ReleaseMouseCapture();
                e.Handled = true;
            };
            // Losing capture mid-gesture (window switch, Esc) drops the drag
            // without reporting it; the normal release clears the gesture first.
            tab.LostMouseCapture += (s, e) =>
            {
                int current = IndexOfMember(member);
                if (current >= 0 && _dragIndex == current) CancelTabDrag();
            };
            // Hover only brightens the label: no pill or band appears.
            tab.MouseEnter += (s, e) => label.Foreground = ThemeManager.TextBrush;
            tab.MouseLeave += (s, e) => ApplyVisuals();
            tab.MouseRightButtonDown += (s, e) =>
            {
                int current = IndexOfMember(member);
                if (current >= 0) TabRightClicked?.Invoke(current);
                e.Handled = true;
            };

            TabRow.Children.Add(tab);
            _tabs.Add(tab);
            _underlines.Add(underline);
            _labels.Add(label);
            _icons.Add(icon);
        }

        /// <summary>The tab's member index right now; -1 once it was removed.</summary>
        private int IndexOfMember(WidgetData member)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (ReferenceEquals(_members[i], member)) return i;
            }
            return -1;
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

        /// <summary>Shows the line in one insertion gap, wearing the dragged tab's colour.</summary>
        private void ShowInsertIndicator(int gap)
        {
            if (!_isReorderEnabled || _dragIndex < 0 || _dragIndex >= _members.Count || _tabs.Count == 0)
            {
                HideInsertIndicator();
                return;
            }

            double x = InsertionLineX(gap);
            _insertIndicator.Background = MemberBrush(_members[_dragIndex]);
            _insertIndicator.Margin = new Thickness(x, 0, 0, 0);
            _insertIndicator.Visibility = Visibility.Visible;
        }

        private void HideInsertIndicator()
        {
            _insertIndicator.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// The left margin that centres the 2px line in a gap: the first tab's
        /// left edge, the midpoint between two neighbouring tabs, or half a
        /// tab gap past the last tab.
        /// </summary>
        private double InsertionLineX(int gap)
        {
            int count = _tabs.Count;
            if (count == 0) return 0;

            double x;
            if (gap <= 0)
            {
                x = TabEdgeInStrip(0, false);
            }
            else if (gap >= count)
            {
                x = TabEdgeInStrip(count - 1, true) + TAB_GAP / 2;
            }
            else
            {
                x = (TabEdgeInStrip(gap - 1, true) + TabEdgeInStrip(gap, false)) / 2;
            }

            if (double.IsNaN(x) || double.IsInfinity(x)) x = LeftInset;
            return Math.Max(0, x - INSERT_LINE_WIDTH / 2);
        }

        private double TabCenterInStrip(int index)
        {
            return TabEdgeInStrip(index, false) + Math.Max(0, _tabs[index].ActualWidth) / 2;
        }

        private double TabEdgeInStrip(int index, bool right)
        {
            Grid tab = _tabs[index];
            double x = right ? Math.Max(0, tab.ActualWidth) : 0;
            return tab.TranslatePoint(new Point(x, 0), this).X;
        }

        private void ApplyVisuals()
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                bool active = i == _activeIndex;
                _labels[i].Foreground = active ? ThemeManager.TextBrush : ThemeManager.MutedTextBrush;
                _labels[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                _icons[i].Brush = active
                    ? MemberBrush(_members[i])
                    : ThemeManager.MutedTextBrush;
                _underlines[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// The text a tab shows: the member's own component name (trimmed)
        /// when it has one, otherwise the localized kind label. A group of
        /// folders therefore reads 季度资料 / 项目资料 instead of 文件夹.
        /// </summary>
        internal static string MemberLabel(WidgetData member)
        {
            if (member != null && !string.IsNullOrWhiteSpace(member.Name))
                return member.Name.Trim();
            return KindLabel(member);
        }

        /// <summary>
        /// The full name behind an ellipsized label; a member without a name
        /// falls back to the label itself so the tooltip is never empty.
        /// </summary>
        private static string TabToolTip(WidgetData member, string label)
        {
            if (member == null || string.IsNullOrWhiteSpace(member.Name)) return label;
            return member.Name.Trim();
        }

        /// <summary>The localized kind name; only a fallback for a nameless member.</summary>
        internal static string KindLabel(WidgetData member)
        {
            if (member is TodoData) return Localization.Get("UI_WidgetKindTodo");
            if (member is NoteData) return Localization.Get("UI_WidgetKindNote");
            if (member is SearchData) return Localization.Get("UI_WidgetKindSearch");
            return Localization.Get("UI_WidgetKindFolder");
        }

        internal static IconId IconFor(WidgetData member)
        {
            if (member is TodoData) return IconId.Calendar;
            if (member is NoteData) return IconId.File;
            if (member is SearchData) return IconId.Search;
            return IconId.Folder;
        }

        private static Brush MemberBrush(WidgetData member)
        {
            string color = member == null || string.IsNullOrEmpty(member.Color)
                ? UiTokens.DefaultFolderColor
                : member.Color;
            var brush = new SolidColorBrush(Utils.HexToColor(color));
            brush.Freeze();
            return brush;
        }
    }
}
