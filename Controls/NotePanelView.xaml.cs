using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// The note widget's content: a home list ⇄ a detail page (design §3.1).
    /// The home paints one card per note (the empty state over an empty list);
    /// a card click opens that note's detail, whose only child is the panel's
    /// permanent <see cref="NoteSurfaceView"/> host - its Child is set once and
    /// never re-parented, so the rendered state and the block edit live in the
    /// detail rather than in a list row. A renderer over <see cref="NoteOps"/>
    /// and <see cref="NoteMarkdownRenderer"/>: it paints what the pure helpers
    /// return (the card title, the excerpt, the display order, the task
    /// progress) and never invents its own text rules, while the surface owns
    /// the rendered document and its edit session. This view owns the
    /// orchestration: the home / detail switch, the blank-text rule and the
    /// title dropdown.
    /// </summary>
    public partial class NotePanelView : UserControl
    {
        /// <summary>The card's title size (T11: 14, semibold).</summary>
        private const double TITLE_FONT = 14;

        /// <summary>The card's time and progress size (T11: 11, secondary text).</summary>
        private const double META_FONT = 11;

        /// <summary>The card's excerpt size and its two-line budget.</summary>
        private const double EXCERPT_FONT = 12;
        private const double EXCERPT_LINE_H = 16;
        private const int EXCERPT_LINES = 2;

        /// <summary>The excerpt's character budget on a card (T11: 80).</summary>
        private const int EXCERPT_CHARS = 80;

        /// <summary>One delete undoable at most 20 times back.</summary>
        private const int UNDO_DEPTH = 20;

        /// <summary>The dropdown's minimum / maximum width and its list floor.</summary>
        private const double MENU_MIN_WIDTH = 220;
        private const double MENU_MAX_WIDTH = 360;
        private const double MENU_MIN_LIST_HEIGHT = 120;

        /// <summary>The check column of one dropdown note entry.</summary>
        private const double MENU_CHECK_WIDTH = 16;

        private NoteData _data;
        private NoteWidget _host;

        /// <summary>The opened note's id; null is the home, so the two states
        /// never show at the same time.</summary>
        private string _expandedId;

        /// <summary>The note id last mounted in the surface host; a rebuild
        /// for the same note (theme/language switch, another note's delete)
        /// must not reset its scroll position.</summary>
        private string _mountedNoteId;

        /// <summary>The note surface, built once and carried by the detail
        /// host (its single instance keeps the surface's session and document
        /// alive across a switch).</summary>
        private readonly NoteSurfaceView _surface;

        /// <summary>The detail's permanent host: its Child is always
        /// <see cref="_surface"/>, so mounting the surface again never
        /// re-parents it. Its padding band is the in-detail click-away.</summary>
        private readonly Border _surfaceHost;

        /// <summary>The multi-step undo stack (Ctrl+Z in the list context):
        /// every delete rides it with the index it came from; the newest 20
        /// survive.</summary>
        private readonly List<NoteRemoval> _removals = new List<NoteRemoval>();

        /// <summary>The open title dropdown, if any (rebuilt on every opening).</summary>
        private Popup _titleMenu;

        public NotePanelView()
        {
            InitializeComponent();

            _surface = new NoteSurfaceView();
            _surfaceHost = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(UiTokens.RadiusControl),
                Background = Brushes.Transparent,
                Child = _surface
            };
            DetailHost.Children.Add(_surfaceHost);

            // The surface's outbound actions: every write-back (the block
            // splice, a task toggle) is a data change the host persists, and
            // the title (note name) follows it live.
            _surface.DataChanged = () =>
            {
                _host?.NotifyDataChanged();
                _host?.RefreshChromeTitle();
            };

            // One subscription for the view's life: the menu is rebuilt per
            // opening from the current state, so Attach never adds a handler.
            Root.ContextMenuOpening += Root_ContextMenuOpening;
        }

        /// <summary>
        /// Binds the view to its widget's data and host window and paints the
        /// home once (mirroring <see cref="TodoCalendarView.Attach"/>).
        /// </summary>
        public void Attach(NoteData data, NoteWidget host)
        {
            _data = data ?? new NoteData();
            _host = host;
            _expandedId = null;
            _mountedNoteId = null;
            RebuildHome();
        }

        /// <summary>The expanded note's id; null is the home.</summary>
        public bool IsHome
        {
            get { return _expandedId == null; }
        }

        /// <summary>
        /// The chrome title for the current state (spec §3.2): the widget name
        /// at home, the note's own title - the untitled key when it is blank -
        /// in the detail.
        /// </summary>
        public string ChromeTitle
        {
            get
            {
                if (IsHome) return _data == null ? string.Empty : _data.Name ?? string.Empty;

                var note = FindExpanded();
                string title = note == null ? string.Empty : NoteOps.TitleOf(note.Text);
                return string.IsNullOrWhiteSpace(title) ? Localization.Get("UI_NoteUntitled") : title;
            }
        }

        /// <summary>
        /// Re-renders from the data: call on load, after every edit and on
        /// theme/language changes. An open block edit is only repainted - its
        /// typed text is the user's pending edit and is never rewritten off the
        /// data - while the detail's rendered state re-attaches the surface and
        /// the home rebuilds its cards. An open title dropdown never survives a
        /// refresh (it would paint the stale language / state). Cheap and
        /// idempotent; the guard keeps the view inert until it was attached.
        /// </summary>
        public void Refresh()
        {
            if (_data == null || _host == null) return;

            CloseTitleMenu();

            if (_surface.IsEditing)
            {
                _surface.Repaint();
                UpdateChromeTitle();
                return;
            }

            if (IsHome) RebuildHome();
            else MountDetail();
        }

        /// <summary>The multi-step undo (Ctrl+Z in the list context): pops the
        /// newest delete and puts the note back in its slot; repeatable up to
        /// <see cref="UNDO_DEPTH"/> steps. A focused text box never reaches
        /// here (its own text undo wins).</summary>
        public bool TryUndo()
        {
            int last = _removals.Count - 1;
            if (last < 0) return false;

            var removal = _removals[last];
            _removals.RemoveAt(last);
            _data.Notes.Insert(Math.Min(removal.Index, _data.Notes.Count), removal.Item);
            AfterEdit();
            return true;
        }

        /// <summary>
        /// Settles the detail's pending work before the panel goes away (the
        /// window closes, the panel collapses, spec 5.2): the surface splices
        /// what is still pending and renders back, and a text that came back
        /// blank drops its note. Idempotent, so the window's close and the
        /// panel's own collapse may both call it.
        /// </summary>
        public void FlushPendingEdit()
        {
            SettleExpandedEdit();
        }

        /// <summary>
        /// Opens (or closes) the title dropdown under <paramref name="anchor"/>
        /// (spec §3.2). The content is rebuilt per opening; the popup closes
        /// itself on an outside click / Esc, and closing while a block edit is
        /// open hands the focus back to its box.
        /// </summary>
        public void ToggleTitleMenu(UIElement anchor)
        {
            if (_titleMenu != null)
            {
                CloseTitleMenu();
                return;
            }
            if (anchor == null) return;

            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = BuildTitleMenuContent()
            };
            popup.Closed += (s, e) =>
            {
                if (ReferenceEquals(_titleMenu, popup)) _titleMenu = null;
                if (_surface.IsEditing) _surface.FocusEditBox();
            };

            _titleMenu = popup;
            popup.IsOpen = true;
        }

        /// <summary>True while the title dropdown is open (the popup never
        /// takes focus, so the window's Esc router reads this to close it).</summary>
        public bool IsTitleMenuOpen
        {
            get { return _titleMenu != null; }
        }

        /// <summary>Closes the title dropdown; idempotent (spec §3.2).</summary>
        public void CloseTitleMenu()
        {
            var menu = _titleMenu;
            if (menu == null) return;

            _titleMenu = null;
            menu.IsOpen = false;
        }

        /// <summary>
        /// Opens one note's detail by id (the dropdown / an external entry):
        /// the open note settles first; the current note is a no-op (the menu
        /// only closes).
        /// </summary>
        public void OpenNote(string id)
        {
            if (id == null || _data == null) return;
            if (id == _expandedId)
            {
                CloseTitleMenu();
                return;
            }

            foreach (var note in _data.Notes)
            {
                if (note != null && note.Id == id)
                {
                    Expand(note);
                    return;
                }
            }
        }

        /// <summary>
        /// The title dropdown's content (spec §3.2), freshly built: the action
        /// block (new always, back to the notes only in the detail) over the
        /// title-only note list (modified-time order, the current note checked
        /// and highlighted). Public so the pins can read the structure; the
        /// popup is the only production consumer.
        /// </summary>
        public FrameworkElement BuildTitleMenuContent()
        {
            var panel = new StackPanel { Margin = new Thickness(4) };

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };
            actions.Children.Add(BuildTitleMenuAction(
                "NoteMenuNew",
                "＋ " + Localization.Format("UI_NewWidgetFormat", Localization.Get("UI_WidgetKindNote")),
                StartNewNote));
            if (!IsHome)
            {
                actions.Children.Add(BuildTitleMenuAction(
                    "NoteMenuHome", Localization.Get("UI_NoteHome"), GoHome));
            }
            panel.Children.Add(actions);

            panel.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(2, 2, 2, 4),
                Background = ThemeManager.BorderBrush
            });

            var list = new StackPanel();
            foreach (var note in NoteOps.SortForDisplay(_data == null ? null : _data.Notes))
            {
                if (note == null) continue;
                list.Children.Add(BuildTitleMenuEntry(note));
            }
            panel.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(MENU_MIN_LIST_HEIGHT, ActualHeight * 0.45),
                Content = list
            });

            var root = LauncherCard.Create(panel,
                Math.Max(MENU_MIN_WIDTH, Math.Min(MENU_MAX_WIDTH, ActualWidth - 24)));
            root.Tag = "NoteTitleMenu";

            // The popup is its own focus scope: Esc must be handled here, the
            // panel's key router never sees it.
            root.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                CloseTitleMenu();
            };
            return root;
        }

        /// <summary>One flat action button of the dropdown (new / home).</summary>
        private Button BuildTitleMenuAction(string tag, string text, Action action)
        {
            var button = new Button
            {
                Tag = tag,
                Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
                Style = FindResource("Kobold.Style.LauncherItem") as Style,
                MinHeight = 32
            };
            button.Click += (s, e) =>
            {
                CloseTitleMenu();
                action();
            };
            return button;
        }

        /// <summary>
        /// One note entry: the check column (only the current note carries the
        /// mark and the highlight) and the title. Clicking it closes the
        /// dropdown and opens the note.
        /// </summary>
        private Button BuildTitleMenuEntry(NoteItem note)
        {
            bool current = note.Id == _expandedId;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MENU_CHECK_WIDTH) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var check = new TextBlock
            {
                Tag = "NoteMenuCheck",
                Text = current ? "✓" : string.Empty,
                Foreground = ThemeManager.SecondaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            string title = NoteOps.TitleOf(note.Text);
            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(title) ? Localization.Get("UI_NoteUntitled") : title,
                Foreground = ThemeManager.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var button = new Button
            {
                Tag = note.Id,
                Content = grid,
                Background = current ? ThemeManager.SurfaceHoverBrush : Brushes.Transparent,
                Style = FindResource("Kobold.Style.LauncherItem") as Style,
                MinHeight = 34
            };
            button.Click += (s, e) =>
            {
                CloseTitleMenu();
                OpenNote(note.Id);
            };
            return button;
        }

        /// <summary>
        /// Deletes one note (the row menu's action): the source list loses it,
        /// the deletion joins the undo stack (the oldest falls off past the
        /// depth) and the home repaints through the usual save path. Deleting
        /// the open note settles it first, so its pending edit is never thrown
        /// away with it.
        /// </summary>
        private void InvokeDelete(NoteItem note)
        {
            if (note == null) return;

            int index = _data.Notes.IndexOf(note);
            if (index < 0) return;

            if (note.Id == _expandedId)
            {
                SettleExpandedEdit();
                _expandedId = null;
                _mountedNoteId = null;
                index = _data.Notes.IndexOf(note);
                if (index < 0) return;
            }

            _data.Notes.RemoveAt(index);
            _removals.Add(new NoteRemoval(note, index));
            if (_removals.Count > UNDO_DEPTH) _removals.RemoveAt(0);
            AfterEdit();
        }

        /// <summary>
        /// Every data edit ends here: repaint, then the usual save + island
        /// refresh through the host's change event (mirroring
        /// <see cref="TodoCalendarView"/>).
        /// </summary>
        private void AfterEdit()
        {
            Refresh();
            _host?.NotifyDataChanged();
        }

        /// <summary>
        /// Rebuilds the home's cards in display order (spec §3.1): the home
        /// host shows, the detail host hides and the empty state follows the
        /// count in the live language. A null data list or a null entry paints
        /// nothing.
        /// </summary>
        private void RebuildHome()
        {
            ItemsPanel.Children.Clear();
            foreach (var note in NoteOps.SortForDisplay(_data == null ? null : _data.Notes))
            {
                if (note == null) continue;
                ItemsPanel.Children.Add(BuildCard(note));
            }

            EmptyState.Text = Localization.Get("UI_NoteEmptyHome");
            EmptyState.Visibility = ItemsPanel.Children.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            HomeHost.Visibility = Visibility.Visible;
            DetailHost.Visibility = Visibility.Collapsed;

            _expandedId = null;
            _mountedNoteId = null;
            UpdateChromeTitle();
        }

        /// <summary>
        /// Shows the detail for the expanded note (spec §3.1): the home host
        /// hides, the detail host shows its permanent surface host, and a new
        /// mount re-attaches the surface at the top. An open block edit is never
        /// re-attached (its box text belongs to the user); a missing note falls
        /// back home.
        /// </summary>
        private void MountDetail()
        {
            var note = FindExpanded();
            if (note == null)
            {
                GoHome();
                return;
            }

            HomeHost.Visibility = Visibility.Collapsed;
            DetailHost.Visibility = Visibility.Visible;

            if (!_surface.IsEditing)
            {
                bool newMount = note.Id != _mountedNoteId;
                _surface.Attach(_data, note);
                _mountedNoteId = note.Id;

                if (newMount)
                {
                    var scroller = _surface.FindName("SurfaceScroller") as ScrollViewer;
                    if (scroller != null) scroller.ScrollToTop();
                }
            }

            UpdateChromeTitle();
        }

        /// <summary>
        /// Opens one note's detail (spec §3.3): the open note settles first (its
        /// edit is flushed, never lost), the note takes the detail and the
        /// rendered document gets the focus - Backspace is a tunnelling
        /// handler, so it only reaches the view from a focused descendant.
        /// </summary>
        private void Expand(NoteItem note)
        {
            if (note == null || _data == null) return;

            CloseTitleMenu();
            SettleExpandedEdit();
            if (!_data.Notes.Contains(note))
            {
                GoHome();
                return;
            }

            _expandedId = note.Id;
            MountDetail();
            _surface.FocusReading();
            UpdateChromeTitle();
        }

        /// <summary>
        /// Returns to the home (spec §3.3): the open edit settles, a blank note
        /// is dropped through the host notification, and the card list rebuilds
        /// in display order. The home's scroll offset is restored when the
        /// rebuild allows it (a failure falls back to the top). Idempotent, so
        /// the dropdown, the blank menu and the rendered Esc may all call it.
        /// </summary>
        private void GoHome()
        {
            double offset = ItemsScroller.VerticalOffset;

            SettleExpandedEdit();
            _expandedId = null;
            _mountedNoteId = null;
            RebuildHome();

            ItemsScroller.ScrollToVerticalOffset(Math.Max(0, offset));
            UpdateChromeTitle();
        }

        /// <summary>
        /// Settles the detail's pending edit (spec 4.3/4.5): the surface
        /// splices what is still pending and renders the whole note again, then
        /// a text that came back blank drops the note from the data (and
        /// through the host's notification - a stray global save may already
        /// have written the note, so an in-memory-only removal would resurrect
        /// it on restart).
        /// </summary>
        private void SettleExpandedEdit()
        {
            var note = FindExpanded();
            _surface.LeaveEdit();

            if (note == null || !string.IsNullOrWhiteSpace(note.Text)) return;

            if (_data.Notes.Remove(note)) _host?.NotifyDataChanged();
            if (note.Id == _expandedId)
            {
                _expandedId = null;
                _mountedNoteId = null;
            }
        }

        /// <summary>The opened note off the data, or null when nothing is open
        /// (or the note is gone).</summary>
        private NoteItem FindExpanded()
        {
            if (_expandedId == null || _data == null) return null;
            foreach (var note in _data.Notes)
            {
                if (note != null && note.Id == _expandedId) return note;
            }
            return null;
        }

        /// <summary>
        /// The creation flow (spec §3.2/6), shared by the dropdown and the
        /// blank menu: the open note settles, a fresh empty note joins the head
        /// of the data, its detail opens and the whole text becomes one edit
        /// region. The note only lands with its first commit or leave, so
        /// opening it writes no notification.
        /// </summary>
        private void StartNewNote()
        {
            if (_data == null) return;

            CloseTitleMenu();
            SettleExpandedEdit();

            var now = DateTime.Now;
            var note = new NoteItem
            {
                Id = Guid.NewGuid().ToString(),
                Text = string.Empty,
                CreatedAt = now,
                ModifiedAt = now
            };
            _data.Notes.Insert(0, note);

            _expandedId = note.Id;
            MountDetail();
            _surface.BeginEditWhole();
            _surface.FocusEditBox();
            UpdateChromeTitle();
        }

        /// <summary>Pushes the state's title onto the host's chrome.</summary>
        private void UpdateChromeTitle()
        {
            _host?.RefreshChromeTitle();
        }

        /// <summary>
        /// The view's key router (spec §3.3): inside a block edit, Esc leaves
        /// that block alone - the surface splices the typed text back and
        /// renders the whole note, the detail staying open (Esc never loses
        /// text, and Backspace belongs to the text there). In the rendered
        /// detail, Esc and Backspace close the note back to the home. At home
        /// neither key does anything.
        /// </summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled) return;

            if (_surface.IsEditing)
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                _surface.LeaveEdit();
                return;
            }

            if (IsHome) return;
            if (e.Key != Key.Escape && e.Key != Key.Back) return;

            e.Handled = true;
            GoHome();
        }

        /// <summary>
        /// The panel's blank click (spec §3.3/user decision 7): while editing,
        /// a click outside the surface - the detail host's padding band or the
        /// panel margin - commits the block and renders back, still in the
        /// detail. In the rendered detail a blank click does nothing (it never
        /// collapses), and a click that belongs to the home's cards or to the
        /// surface keeps its owner.
        /// </summary>
        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled) return;

            var hit = (e.OriginalSource ?? e.Source) as DependencyObject;
            if (hit == null || !_surface.IsEditing) return;
            if (IsInsideSurface(hit)) return;

            _surface.LeaveEdit();
        }

        /// <summary>True when the hit chain reaches the surface itself - the
        /// host's padding band stops before it, so it is the click-away.</summary>
        private static bool IsInsideSurface(DependencyObject hit, DependencyObject surface, DependencyObject host)
        {
            var element = hit;
            while (element != null)
            {
                if (ReferenceEquals(element, surface)) return true;
                if (ReferenceEquals(element, host)) return false;
                element = NoteSurfaceView.ParentOf(element);
            }
            return false;
        }

        private bool IsInsideSurface(DependencyObject hit)
        {
            return IsInsideSurface(hit, _surface, _surfaceHost);
        }

        /// <summary>True when the hit chain reaches the detail's surface host
        /// (the note content owns its own right-click menus).</summary>
        private bool IsWithinSurfaceHost(DependencyObject hit)
        {
            var element = hit;
            while (element != null)
            {
                if (ReferenceEquals(element, _surfaceHost)) return true;
                element = NoteSurfaceView.ParentOf(element);
            }
            return false;
        }

        /// <summary>
        /// The panel's blank right-click (spec §3.3): the action menu is
        /// assigned outside the surface's content, so the note content keeps
        /// its own menus while the detail host's padding band - the only blank
        /// area in the detail - still offers the panel menu. Built per opening,
        /// so it always reflects the current language and state.
        /// </summary>
        private void Root_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (_data == null)
            {
                Root.ContextMenu = null;
                return;
            }

            var hit = (e.OriginalSource ?? e.Source) as DependencyObject;
            bool insideContent = hit != null
                && IsWithinSurfaceHost(hit)
                && !ReferenceEquals(hit, _surfaceHost);
            Root.ContextMenu = insideContent ? null : BuildBlankMenu();
        }

        /// <summary>
        /// The blank-area menu (spec §3.3): the create entry always, plus the
        /// way back to the notes in the detail (the home hides it, so its menu
        /// carries only the create entry).
        /// </summary>
        private ContextMenu BuildBlankMenu()
        {
            var builder = new MenuBuilder(_data == null ? null : _data.Color);

            var newItem = new MenuItem
            {
                Header = "＋ " + Localization.Format("UI_NewWidgetFormat", Localization.Get("UI_WidgetKindNote"))
            };
            newItem.Click += (s, e) =>
            {
                CloseTitleMenu();
                StartNewNote();
            };
            builder.AddMenuItem(newItem);

            if (!IsHome)
            {
                var home = new MenuItem { Header = Localization.Get("UI_NoteHome") };
                home.Click += (s, e) =>
                {
                    CloseTitleMenu();
                    GoHome();
                };
                builder.AddMenuItem(home);
            }

            return builder.Build();
        }

        /// <summary>
        /// One card face: rounded border with a hover backdrop over a stack of
        /// the title (14 semibold), the modified time and the two-line excerpt
        /// (both 11 secondary), plus the progress line when the note has task
        /// boxes. The click opens the note's detail; the row menu deletes it.
        /// </summary>
        private Border BuildCard(NoteItem note)
        {
            var content = new StackPanel();
            content.Children.Add(NewLine(NoteOps.TitleOf(note.Text), TITLE_FONT,
                FontWeights.SemiBold, ThemeManager.TextBrush));
            content.Children.Add(NewLine(note.ModifiedAt.ToString("g"), META_FONT,
                FontWeights.Normal, ThemeManager.SecondaryTextBrush));

            var excerpt = NewLine(NoteOps.ExcerptOf(note.Text, EXCERPT_CHARS), EXCERPT_FONT,
                FontWeights.Normal, ThemeManager.SecondaryTextBrush);
            excerpt.TextWrapping = TextWrapping.Wrap;
            excerpt.LineHeight = EXCERPT_LINE_H;
            excerpt.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            excerpt.MaxHeight = EXCERPT_LINE_H * EXCERPT_LINES;
            content.Children.Add(excerpt);

            var progress = NoteOps.TaskProgressOf(note.Text);
            if (progress.HasValue)
            {
                content.Children.Add(NewLine("✓ " + progress.Value.done + "/" + progress.Value.total,
                    META_FONT, FontWeights.Normal, ThemeManager.SecondaryTextBrush));
            }

            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(UiTokens.RadiusControl),
                Background = Brushes.Transparent,
                Child = content
            };
            card.MouseEnter += (s, e) => card.Background = ThemeManager.SurfaceHoverBrush;
            card.MouseLeave += (s, e) => card.Background = Brushes.Transparent;
            card.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                Expand(note);
            };
            card.ContextMenu = new MenuBuilder(_data.Color)
                .AddItem("Menu_DeleteItem", () => InvokeDelete(note))
                .Build();
            return card;
        }

        /// <summary>One single-line, ellipsis-trimmed card line.</summary>
        private static TextBlock NewLine(string text, double fontSize, FontWeight weight, Brush brush)
        {
            return new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = fontSize,
                FontWeight = weight,
                Foreground = brush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        /// <summary>One deleted note with the index it came from, so the undo
        /// can put it back where it was.</summary>
        private sealed class NoteRemoval
        {
            public NoteRemoval(NoteItem item, int index)
            {
                Item = item;
                Index = index;
            }

            public NoteItem Item { get; }

            public int Index { get; }
        }
    }
}
