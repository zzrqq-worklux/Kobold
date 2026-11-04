using System;
using System.Collections.Generic;
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
    /// group window owns the views and their visibility.
    /// </summary>
    public class WidgetGroupTabStrip : Grid
    {
        public const double RowHeight = 32;
        public const double LeftInset = 16;

        /// <summary>The press movement (DIP) that turns a tab press into a drag-out.</summary>
        public const double DragThreshold = GroupDragPolicy.DefaultDragThreshold;

        private const double TAB_GAP = 22;
        private const double ICON_SIZE = 11;

        private readonly IList<WidgetData> _members;
        private readonly List<Grid> _tabs = new List<Grid>();
        private readonly List<Border> _underlines = new List<Border>();
        private readonly List<TextBlock> _labels = new List<TextBlock>();
        private readonly List<IconPresenter> _icons = new List<IconPresenter>();
        private int _activeIndex = -1;

        // Tab drag gesture: the pressed tab, its anchor and whether the press
        // passed the threshold. A press/release that never crosses it stays a
        // click and is never reported as a drag.
        private int _dragIndex = -1;
        private Point _dragStart;
        private bool _isDraggingTab;

        /// <summary>A tab was left-clicked; the argument is the member index.</summary>
        public event Action<int> TabClicked;

        /// <summary>A tab was right-clicked; the argument is the member index.</summary>
        public event Action<int> TabRightClicked;

        /// <summary>
        /// A real tab drag finished: the member index and whether it was
        /// released outside the hosting window. A click never raises it.
        /// </summary>
        public event Action<int, bool> TabDragCompleted;

        public WidgetGroupTabStrip(IList<WidgetData> members)
        {
            _members = members ?? new List<WidgetData>();

            Height = RowHeight;
            Background = Brushes.Transparent;

            TabRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(LeftInset, 0, LeftInset, 0)
            };
            Children.Add(TabRow);

            for (int i = 0; i < _members.Count; i++)
            {
                BuildTab(i, _members[i]);
            }

            ApplyVisuals();
        }

        /// <summary>The horizontal host of the tab roots (32px strip, 16px left inset).</summary>
        public StackPanel TabRow { get; }

        /// <summary>The tab roots, one per member, in member order.</summary>
        public IReadOnlyList<Grid> TabButtons => _tabs;

        /// <summary>The 2px bottom underlines, one per member, in member order.</summary>
        public IReadOnlyList<Border> Underlines => _underlines;

        /// <summary>Repaints the active/inactive/hover states for the given member.</summary>
        public void SetActive(int index)
        {
            _activeIndex = index;
            ApplyVisuals();
        }

        /// <summary>True between an over-threshold tab press and its release.</summary>
        public bool IsTabDragActive => _dragIndex >= 0 && _isDraggingTab;

        /// <summary>
        /// Starts a tab drag gesture at the given strip-relative point. Kept
        /// public so the checks can drive the gesture without synthesizing a
        /// real mouse; the tab's own mouse handlers call it too.
        /// </summary>
        public bool BeginTabDrag(int index, Point point)
        {
            if (index < 0 || index >= _tabs.Count) return false;

            _dragIndex = index;
            _dragStart = point;
            _isDraggingTab = false;
            return true;
        }

        /// <summary>
        /// Follows the pressed tab. Returns true once the movement crossed
        /// <see cref="DragThreshold"/>; a sub-threshold move is not a drag.
        /// </summary>
        public bool UpdateTabDrag(Point point)
        {
            if (_dragIndex < 0) return false;

            if (!_isDraggingTab)
            {
                if (!GroupDragPolicy.IsDragOut(point.X - _dragStart.X, point.Y - _dragStart.Y)) return false;
                _isDraggingTab = true;
            }
            return true;
        }

        /// <summary>
        /// Ends the gesture. Returns false for a click (no threshold, no
        /// event); otherwise raises <see cref="TabDragCompleted"/> with the
        /// release point tested against the hosting window's bounds.
        /// </summary>
        public bool EndTabDrag(Point point)
        {
            int index = _dragIndex;
            bool wasDragging = _isDraggingTab;
            _dragIndex = -1;
            _isDraggingTab = false;
            if (index < 0 || !wasDragging) return false;

            TabDragCompleted?.Invoke(index, IsOutsideHost(point));
            return true;
        }

        /// <summary>Drops an in-flight gesture without reporting it (capture lost).</summary>
        public void CancelTabDrag()
        {
            _dragIndex = -1;
            _isDraggingTab = false;
        }

        /// <summary>
        /// True when the strip-relative point maps outside the hosting
        /// window's client box. A strip outside any window counts as inside.
        /// </summary>
        public bool IsOutsideHost(Point stripPoint)
        {
            Window host = Window.GetWindow(this);
            if (host == null) return false;

            Point inWindow = TranslatePoint(stripPoint, host);
            if (double.IsNaN(inWindow.X) || double.IsNaN(inWindow.Y)) return false;

            return inWindow.X < 0 || inWindow.Y < 0
                || inWindow.X > host.ActualWidth || inWindow.Y > host.ActualHeight;
        }

        /// <summary>
        /// Re-reads each tab's localized kind label and tooltip. Language
        /// switches call this through the group window's UpdateUI; nothing else
        /// about the strip changes.
        /// </summary>
        public void RefreshLocalization()
        {
            int count = Math.Min(_labels.Count, _members.Count);
            for (int i = 0; i < count; i++)
            {
                WidgetData member = _members[i];
                string label = KindLabel(member);
                _labels[i].Text = label;
                _tabs[i].ToolTip = member == null || string.IsNullOrWhiteSpace(member.Name)
                    ? label
                    : member.Name;
            }
        }

        private void BuildTab(int index, WidgetData member)
        {
            var tab = new Grid
            {
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, TAB_GAP, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Stretch
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
                Text = KindLabel(member),
                FontSize = UiTokens.FontBody,
                VerticalAlignment = VerticalAlignment.Center
            };

            content.Children.Add(icon);
            content.Children.Add(label);

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

            int tabIndex = index;
            tab.MouseLeftButtonDown += (s, e) =>
            {
                // The press both switches (click semantics) and arms the drag;
                // only a movement past the threshold turns it into a drag-out.
                BeginTabDrag(tabIndex, e.GetPosition(this));
                tab.CaptureMouse();
                TabClicked?.Invoke(tabIndex);
                e.Handled = true;
            };
            tab.MouseMove += (s, e) =>
            {
                if (_dragIndex != tabIndex || e.LeftButton != MouseButtonState.Pressed) return;
                if (UpdateTabDrag(e.GetPosition(this))) tab.Cursor = Cursors.SizeAll;
            };
            tab.MouseLeftButtonUp += (s, e) =>
            {
                if (_dragIndex != tabIndex) return;

                EndTabDrag(e.GetPosition(this));
                if (tab.IsMouseCaptured) tab.ReleaseMouseCapture();
                tab.Cursor = Cursors.Hand;
                e.Handled = true;
            };
            // Losing capture mid-gesture (window switch, Esc) drops the drag
            // without reporting it; the normal release clears the gesture first.
            tab.LostMouseCapture += (s, e) =>
            {
                if (_dragIndex == tabIndex) CancelTabDrag();
                tab.Cursor = Cursors.Hand;
            };
            // Hover only brightens the label: no pill or band appears.
            tab.MouseEnter += (s, e) => label.Foreground = ThemeManager.TextBrush;
            tab.MouseLeave += (s, e) => ApplyVisuals();
            tab.MouseRightButtonDown += (s, e) =>
            {
                TabRightClicked?.Invoke(tabIndex);
                e.Handled = true;
            };

            TabRow.Children.Add(tab);
            _tabs.Add(tab);
            _underlines.Add(underline);
            _labels.Add(label);
            _icons.Add(icon);
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

        /// <summary>The localized kind name a tab shows.</summary>
        internal static string KindLabel(WidgetData member)
        {
            if (member is TodoData) return Localization.Get("UI_WidgetKindTodo");
            if (member is NoteData) return Localization.Get("UI_WidgetKindNote");
            if (member is SearchData) return Localization.Get("UI_WidgetKindSearch");
            return Localization.Get("UI_WidgetKindFolder");
        }

        private static IconId IconFor(WidgetData member)
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
