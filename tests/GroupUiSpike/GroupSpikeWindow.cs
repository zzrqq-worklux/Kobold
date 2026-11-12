using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kobold.Controls;
using Kobold.Core;
using Kobold.Core.Icons;
using Localization = Kobold.Core.Localization;

namespace Kobold.GroupUiSpike
{
    /// <summary>
    /// The T0 group window: one shell that hosts the real <see cref="TodoCalendarView"/>
    /// and <see cref="NotePanelView"/> behind two bare top tabs.
    ///
    /// The two widgets stay alive as hidden hosts (the views still bind to their
    /// concrete host types); only their content views move into this window's
    /// slots. Tab switching changes Visibility only - the view instances are
    /// never rebuilt - and the group window re-dispatches the window-level
    /// behaviour (outside-click blur, Ctrl+Z, Esc, note flush).
    ///
    /// Spike scope only: the group is not a production group model, positions
    /// are not persisted, and the title/menu affordances are minimal.
    /// </summary>
    internal sealed class GroupSpikeWindow : Window, ISpikeGroupWindow
    {
        private const double TAB_ROW_HEIGHT = 32;
        private const double TITLE_ROW_HEIGHT = 40;

        // The production chrome the todo view subtracts from the persisted
        // panel height: 40 header + 2 shell border.
        private const double CHROME_HEIGHT = 42;

        private const double DEFAULT_WIDTH = 760;
        private const double DEFAULT_HEIGHT = 540;

        // Member accent colours (UI1e): todo #6A9BCC, note #788C5D.
        private static readonly Color TodoAccent = Utils.HexToColor("#6A9BCC");
        private static readonly Color NoteAccent = Utils.HexToColor("#788C5D");

        private readonly SpikeInput _input;
        private readonly TodoWidget _todoHost;
        private readonly NoteWidget _noteHost;
        private readonly TodoCalendarView _todoView;
        private readonly NotePanelView _noteView;

        private readonly Grid _tabRow;
        private readonly Grid _titleRow;
        private readonly Grid _contentArea;
        private readonly ContentControl _todoSlot;
        private readonly ContentControl _noteSlot;
        private readonly TextBlock _titleText;

        private readonly Grid _todoTab;
        private readonly Grid _noteTab;
        private readonly Border _todoUnderline;
        private readonly Border _noteUnderline;
        private readonly TextBlock _todoLabel;
        private readonly TextBlock _noteLabel;
        private readonly IconPresenter _todoIcon;
        private readonly IconPresenter _noteIcon;

        private readonly Border _pinButton;
        private readonly Border _lockButton;
        private readonly Border _menuButton;
        private readonly IconPresenter _pinIcon;
        private readonly IconPresenter _lockIcon;

        private readonly IInputElement[] _lastFocus = new IInputElement[2];

        private int _active;
        private bool _pinned;
        private bool _locked;
        private bool _activatedOnce;
        private int _keyEventsSeen;
        private int _mouseEventsSeen;

        private GroupSpikeWindow(SpikeInput input)
        {
            _input = input;

            // Code-built window: give it a name scope so the harness can reach
            // named parts (TitleMenuButton) through FindName, as XAML would.
            NameScope.SetNameScope(this, new NameScope());

            Title = "Kobold 组件组（T0 Spike）";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            Background = ThemeManager.BackgroundBrush;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = DEFAULT_WIDTH;
            Height = DEFAULT_HEIGHT;
            FontFamily = ThemeManager.UiFontFamily;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            // The todo view reads the persisted panel size when its host is not
            // open (see TodoCalendarView.CurrentContentHeight) - seed it with the
            // group content box before the host is constructed and re-seed it on
            // every resize.
            _input.Todo.PanelWidth = _input.ContentWidth;
            _input.Todo.PanelHeight = _input.ContentHeight + CHROME_HEIGHT;

            // Hidden hosts: real widgets, never shown. The views are detached
            // from their chrome and re-parented into this window's slots.
            _todoHost = new TodoWidget(_input.Todo);
            _todoView = _todoHost.CalendarView;
            DetachFromHost(_todoHost, "TodoChrome", _todoView);

            _noteHost = new NoteWidget(_input.Note);
            _noteView = _noteHost.PanelView;
            DetachFromHost(_noteHost, "NoteChrome", _noteView);

            // Shell: 1px border + theme background, radius 10.
            var shell = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeManager.BorderBrush,
                Background = ThemeManager.BackgroundBrush,
                CornerRadius = new CornerRadius(UiTokens.RadiusWindow),
                SnapsToDevicePixels = true
            };
            Content = shell;

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TAB_ROW_HEIGHT) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TITLE_ROW_HEIGHT) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.Child = layout;

            // --- tab row ---------------------------------------------------
            _tabRow = new Grid { Margin = new Thickness(16, 0, 16, 0), Background = Brushes.Transparent };
            Grid.SetRow(_tabRow, 0);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
            _tabRow.Children.Add(tabs);

            _todoUnderline = NewUnderline(TodoAccent);
            _todoIcon = new IconPresenter
            {
                Icon = IconId.Calendar,
                Size = 11,
                StrokeThickness = 1.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            _todoLabel = new TextBlock
            {
                Text = Localization.Get("UI_WidgetKindTodo"),
                FontSize = UiTokens.FontBody,
                VerticalAlignment = VerticalAlignment.Center
            };
            _todoTab = NewTab(_todoIcon, _todoLabel, _todoUnderline, 0, "TodoTab");
            tabs.Children.Add(_todoTab);

            _noteUnderline = NewUnderline(NoteAccent);
            _noteIcon = new IconPresenter
            {
                Icon = IconId.File,
                Size = 11,
                StrokeThickness = 1.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            _noteLabel = new TextBlock
            {
                Text = Localization.Get("UI_WidgetKindNote"),
                FontSize = UiTokens.FontBody,
                VerticalAlignment = VerticalAlignment.Center
            };
            _noteTab = NewTab(_noteIcon, _noteLabel, _noteUnderline, 1, "NoteTab");
            tabs.Children.Add(_noteTab);

            layout.Children.Add(_tabRow);

            // --- title row -------------------------------------------------
            _titleRow = new Grid { Margin = new Thickness(16, 0, 12, 0) };
            Grid.SetRow(_titleRow, 1);
            _titleText = new TextBlock
            {
                FontSize = UiTokens.FontSubtitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeManager.TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _titleRow.Children.Add(_titleText);

            var affordances = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            _pinIcon = new IconPresenter { Icon = IconId.Pin, Size = 12, StrokeThickness = 1.6 };
            _pinButton = NewAffordance(_pinIcon, "固定 (Pin)", TogglePin);
            affordances.Children.Add(_pinButton);

            _lockIcon = new IconPresenter { Icon = IconId.Lock, Size = 12, StrokeThickness = 1.6 };
            _lockButton = NewAffordance(_lockIcon, "锁定 (Lock)", ToggleLock);
            affordances.Children.Add(_lockButton);

            _menuButton = NewAffordance(new IconPresenter
            {
                Icon = IconId.Chevron,
                Rotation = 90,
                Size = 12,
                StrokeThickness = 1.6
            }, "标题菜单（笔记）", OnTitleMenuClicked);
            // The harness reaches the real affordance by name (A13).
            RegisterName("TitleMenuButton", _menuButton);
            affordances.Children.Add(_menuButton);
            _titleRow.Children.Add(affordances);
            layout.Children.Add(_titleRow);

            // --- content ---------------------------------------------------
            _contentArea = new Grid();
            Grid.SetRow(_contentArea, 2);

            _todoSlot = NewSlot(_todoView, "TodoSlot");
            _noteSlot = NewSlot(_noteView, "NoteSlot");
            _contentArea.Children.Add(_todoSlot);
            _contentArea.Children.Add(_noteSlot);
            layout.Children.Add(_contentArea);

            // --- window-level routing (was the widgets' job) ---------------
            // A show/activate handshake can raise a transient Deactivated
            // before the window ever held focus; only a deactivation after the
            // first real activation hides (mirroring the widget panels).
            Activated += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => { _activatedOnce = true; }));
            Deactivated += (s, e) => { if (!_pinned && _activatedOnce) Hide(); };
            SizeChanged += (s, e) => ApplyContentSize();
            IsVisibleChanged += (s, e) => { if (!(bool)e.NewValue) SettleActiveNote(); };
            Closed += (s, e) =>
            {
                SettleActiveNote();
                try { _todoHost.Close(); } catch { }
                try { _noteHost.Close(); } catch { }
            };

            // Edits reach the hosts' change event; keep the context title live
            // without waiting for a tab switch (T2 should replace this with a
            // real title-changed event on the views).
            _todoHost.OnDataChanged += RefreshTitle;
            _noteHost.OnDataChanged += RefreshTitle;

            SetActive(0);
            UpdateAffordances();
        }

        /// <summary>Factory used by the harness (the window is internal).</summary>
        public static ISpikeGroupWindow Create(SpikeInput input)
        {
            if (input == null) throw new ArgumentNullException("input");
            return new GroupSpikeWindow(input);
        }

        #region ISpikeGroupWindow

        Window ISpikeGroupWindow.Window { get { return this; } }
        public TodoWidget TodoHost { get { return _todoHost; } }
        public NoteWidget NoteHost { get { return _noteHost; } }
        public TodoData TodoData { get { return _input.Todo; } }
        public NoteData NoteData { get { return _input.Note; } }
        public TodoCalendarView TodoView { get { return _todoView; } }
        public NotePanelView NoteView { get { return _noteView; } }

        public FrameworkElement TabRow { get { return _tabRow; } }
        public FrameworkElement TitleRow { get { return _titleRow; } }
        public FrameworkElement ContentArea { get { return _contentArea; } }
        public FrameworkElement TodoTab { get { return _todoTab; } }
        public FrameworkElement NoteTab { get { return _noteTab; } }
        public Border TodoUnderline { get { return _todoUnderline; } }
        public Border NoteUnderline { get { return _noteUnderline; } }
        public ContentControl TodoSlot { get { return _todoSlot; } }
        public ContentControl NoteSlot { get { return _noteSlot; } }
        public TextBlock TitleText { get { return _titleText; } }

        public bool IsPinned { get { return _pinned; } }
        public bool IsLocked { get { return _locked; } }
        public bool IsActiveTodo { get { return _active == 0; } }
        public int KeyEventsSeen { get { return _keyEventsSeen; } }
        public int MouseEventsSeen { get { return _mouseEventsSeen; } }

        /// <summary>Activates one member slot (0 = todo, 1 = note) without rebuilding.</summary>
        public void SetActive(int index)
        {
            if (index != 0 && index != 1) throw new ArgumentOutOfRangeException("index");

            if (index != _active)
            {
                RememberFocus(_active);
                SettleActiveNote();
                _active = index;
            }

            _todoSlot.Visibility = _active == 0 ? Visibility.Visible : Visibility.Collapsed;
            _noteSlot.Visibility = _active == 1 ? Visibility.Visible : Visibility.Collapsed;

            ApplyTabVisuals();
            UpdateAffordances();
            RefreshTitle();
            RestoreFocus();
        }

        /// <summary>Context title: todo date (M月d日 · 周X) / note ChromeTitle.</summary>
        public void RefreshTitle()
        {
            if (_titleText == null) return;
            _titleText.Text = _active == 0 ? TodoContextTitle() : NoteContextTitle();
        }

        /// <summary>Re-seeds the todo view's persisted panel box from the live content area.</summary>
        public void ApplyContentSize()
        {
            if (_contentArea == null) return;
            double width = _contentArea.ActualWidth;
            double height = _contentArea.ActualHeight;
            if (width <= 0 || height <= 0) return;

            _input.Todo.PanelWidth = width;
            _input.Todo.PanelHeight = height + CHROME_HEIGHT;
            _todoView.Refresh();
            _noteView.Refresh();
            RefreshTitle();
        }

        /// <summary>The lock refuses resizes (returns false, size unchanged).</summary>
        public bool TryResize(double width, double height)
        {
            if (_locked) return false;
            Width = width;
            Height = height;
            return true;
        }

        public void ToggleTopmost()
        {
            Topmost = !Topmost;
        }

        public void TogglePin()
        {
            _pinned = !_pinned;
            UpdateAffordances();
        }

        public void ToggleLock()
        {
            _locked = !_locked;
            UpdateAffordances();
        }

        #endregion

        #region Routing

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            _keyEventsSeen++;
            if (e.Handled) return;

            if (_active == 0)
            {
                // Mirrors TodoWidget.Window_PreviewKeyDown.
                if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control) return;
                if (Keyboard.FocusedElement is TextBox) return;
                if (_todoView.TryUndo()) e.Handled = true;
                return;
            }

            // Mirrors NoteWidget.Window_PreviewKeyDown.
            if (e.Key == Key.Escape && _noteView.IsTitleMenuOpen)
            {
                _noteView.CloseTitleMenu();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control) return;
            if (Keyboard.FocusedElement is TextBox) return;
            if (_noteView.TryUndo()) e.Handled = true;
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            _mouseEventsSeen++;
            if (e.Handled) return;

            // Mirrors TodoWidget.Window_PreviewMouseLeftButtonDown: a click
            // outside the focused inline editor blurs (and commits) it.
            if (_active == 0) _todoView.HandleMouseDownOutsideEditor(e.OriginalSource);
        }

        #endregion

        #region UI construction

        private Grid NewTab(IconPresenter icon, TextBlock label, Border underline, int index, string name)
        {
            var tab = new Grid
            {
                Name = name,
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 22, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            content.Children.Add(icon);
            content.Children.Add(label);
            tab.Children.Add(content);
            tab.Children.Add(underline);

            tab.MouseLeftButtonDown += (s, e) =>
            {
                SetActive(index);
                e.Handled = true;
            };
            // Hover only brightens the text (no pill, no band).
            tab.MouseEnter += (s, e) => label.Foreground = ThemeManager.TextBrush;
            tab.MouseLeave += (s, e) => ApplyTabVisuals();
            return tab;
        }

        private static Border NewUnderline(Color accent)
        {
            var brush = new SolidColorBrush(accent);
            brush.Freeze();
            return new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = brush,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
        }

        private static ContentControl NewSlot(FrameworkElement view, string name)
        {
            return new ContentControl
            {
                Name = name,
                Content = view,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Focusable = false
            };
        }

        private Border NewAffordance(IconPresenter icon, string tooltip, Action action)
        {
            var border = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = icon,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (action != null)
            {
                Border self = border;
                border.MouseLeftButtonDown += (s, e) =>
                {
                    action();
                    e.Handled = true;
                };
                border.MouseEnter += (s, e) => self.Background = ThemeManager.SurfaceHoverBrush;
                border.MouseLeave += (s, e) => self.Background = Brushes.Transparent;
            }
            return border;
        }

        private void ApplyTabVisuals()
        {
            ApplyOneTab(0, _todoLabel, _todoIcon, _todoUnderline, TodoAccent);
            ApplyOneTab(1, _noteLabel, _noteIcon, _noteUnderline, NoteAccent);
        }

        private void ApplyOneTab(int index, TextBlock label, IconPresenter icon, Border underline, Color accent)
        {
            bool active = _active == index;
            label.Foreground = active ? ThemeManager.TextBrush : ThemeManager.MutedTextBrush;
            label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;

            var brush = new SolidColorBrush(active ? accent : ThemeManager.MutedTextBrush.Color);
            brush.Freeze();
            icon.Brush = brush;
            underline.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateAffordances()
        {
            _pinIcon.Brush = _pinned ? ThemeManager.AccentBrush : ThemeManager.MutedTextBrush;
            _lockIcon.Brush = _locked ? ThemeManager.TextBrush : ThemeManager.MutedTextBrush;
            _pinButton.ToolTip = _pinned ? "取消固定 (Pin)" : "固定 (Pin)";
            _lockButton.ToolTip = _locked ? "解锁 (Lock)" : "锁定 (Lock)";

            // The note title dropdown is the note kind's own affordance; the todo
            // member has no title menu, so the chevron is not offered there.
            _menuButton.Visibility = _active == 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The title-row chevron: opens/closes the note title dropdown.</summary>
        private void OnTitleMenuClicked()
        {
            if (_active != 1) return;
            _noteView.ToggleTitleMenu(_menuButton);
        }

        #endregion

        #region Titles + focus + settle

        private string TodoContextTitle()
        {
            DateTime day = (_input.Todo.SelectedDate ?? DateTime.Today).Date;
            string weekday = "日一二三四五六"[(int)day.DayOfWeek].ToString();
            return day.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN")) + " · 周" + weekday;
        }

        private string NoteContextTitle()
        {
            string title = _noteView.ChromeTitle;
            if (string.IsNullOrWhiteSpace(title)) title = _input.Note.Name;
            return string.IsNullOrWhiteSpace(title) ? "—" : title;
        }

        private void RememberFocus(int index)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null) return;
            DependencyObject root = index == 0 ? (DependencyObject)_todoView : _noteView;
            if (!IsInside(focused, root)) return;
            _lastFocus[index] = Keyboard.FocusedElement;
        }

        private void RestoreFocus()
        {
            IInputElement target = _lastFocus[_active];
            if (target == null) return;

            var element = target as UIElement;
            if (element == null || !element.IsVisible) return;

            // Deferred: the slot's layout must settle before Focus() sticks.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                try { Keyboard.Focus(target); } catch { }
            }));
        }

        private void SettleActiveNote()
        {
            if (_active != 1) return;
            try { _noteView.FlushPendingEdit(); } catch { }
        }

        private static bool IsInside(DependencyObject child, DependencyObject root)
        {
            DependencyObject current = child;
            while (current != null)
            {
                if (ReferenceEquals(current, root)) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static void DetachFromHost(Window host, string chromeName, FrameworkElement view)
        {
            var chrome = host.FindName(chromeName) as WidgetChrome;
            if (chrome != null && ReferenceEquals(chrome.ContentHost.Content, view))
            {
                chrome.ContentHost.Content = null;
                return;
            }

            var presenter = view.Parent as ContentPresenter;
            if (presenter != null && ReferenceEquals(presenter.Content, view))
            {
                presenter.Content = null;
                return;
            }

            throw new InvalidOperationException("could not detach " + view.GetType().Name + " from its host");
        }

        #endregion
    }
}
