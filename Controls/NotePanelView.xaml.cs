using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// The note widget's content: the list, with one card at a time expanded in
    /// place (spec 6.2). The panel carries a single <see cref="NoteSurfaceView"/>
    /// for its whole life - a plain Border host, whose Child is set once and
    /// never re-parented, is mounted in the expanded row and re-attached per
    /// note, so the rendered state and the block edit live inside a card rather
    /// than over a surface of their own. A renderer over <see cref="NoteOps"/>
    /// and <see cref="NoteMarkdownRenderer"/>: it paints what the pure helpers
    /// return (the card title, the excerpt, the display order, the task
    /// progress) and never invents its own text rules, while the surface owns
    /// the rendered document and its edit session. This view owns the
    /// orchestration: which card is expanded, the switch between cards, the
    /// collapse and the blank-text rule.
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

        private NoteData _data;
        private NoteWidget _host;

        /// <summary>The expanded note's id; null is the plain list, so at most
        /// one card is expanded at a time.</summary>
        private string _expandedId;

        /// <summary>The note surface, built once and carried by the expanded
        /// row's host (its single instance keeps the surface's session and
        /// document alive across a collapse).</summary>
        private readonly NoteSurfaceView _surface;

        /// <summary>The expanded row's permanent host: its Child is always
        /// <see cref="_surface"/>, so mounting the surface again never
        /// re-parents it. It keeps the card face's metrics, so the expanded row
        /// starts exactly where the card stood.</summary>
        private readonly Border _surfaceHost;

        /// <summary>The multi-step undo stack (Ctrl+Z in the list context):
        /// every delete rides it with the index it came from; the newest 20
        /// survive.</summary>
        private readonly List<NoteRemoval> _removals = new List<NoteRemoval>();

        public NotePanelView()
        {
            InitializeComponent();

            _surface = new NoteSurfaceView();
            _surfaceHost = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(UiTokens.RadiusControl),
                Background = Brushes.Transparent,
                Child = _surface
            };

            // The surface's two outbound actions: the floating ‹ asks for the
            // collapse, and every write-back (the block splice, a task toggle)
            // is a data change the host persists. The notification is the whole
            // handler - the list is rebuilt on the collapse, never mid-edit.
            _surface.CollapseRequested = CollapseExpanded;
            _surface.DataChanged = () => _host?.NotifyDataChanged();
        }

        /// <summary>
        /// Binds the view to its widget's data and host window and paints the
        /// list once (mirroring <see cref="TodoCalendarView.Attach"/>).
        /// </summary>
        public void Attach(NoteData data, NoteWidget host)
        {
            _data = data ?? new NoteData();
            _host = host;

            // The create entry lives on the list's blank space: the cards carry
            // their own row menus (the nearest ContextMenu wins) and only the
            // collapsed list offers the blank menu, so an expanded card (whose
            // surface owns the click inside it) keeps its own gestures.
            Root.ContextMenuOpening += (s, e) =>
            {
                Root.ContextMenu = _expandedId == null ? BuildBlankMenu() : null;
            };

            Refresh();
        }

        /// <summary>
        /// Re-renders the list from the data: call on load, after every edit and
        /// on theme/language changes. An open block edit is only repainted - its
        /// typed text is the user's pending edit and is never rewritten off the
        /// data - while every other state rebuilds the rows (the expanded row's
        /// mount re-attaches the surface, which is what re-renders it). Cheap
        /// and idempotent; the guard keeps the view inert until it was attached.
        /// </summary>
        public void Refresh()
        {
            if (_data == null || _host == null) return;

            if (_surface.IsEditing)
            {
                _surface.Repaint();
                return;
            }
            RebuildList();
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
        /// Settles the expanded card's pending work before the panel goes away
        /// (the window closes, the panel collapses, spec 5.2): the surface
        /// splices what is still pending and renders back, and a text that came
        /// back blank drops its note. Idempotent, so the window's close and the
        /// panel's own collapse may both call it.
        /// </summary>
        public void FlushPendingEdit()
        {
            SettleExpandedEdit();
        }

        /// <summary>
        /// Deletes one note (the row menu's action): the source list loses it,
        /// the deletion joins the undo stack (the oldest falls off past the
        /// depth) and the list repaints through the usual save path. Deleting
        /// the expanded note settles it first, so its pending edit is never
        /// thrown away with the card.
        /// </summary>
        private void InvokeDelete(NoteItem note)
        {
            if (note == null) return;
            if (note.Id == _expandedId) CollapseExpanded();

            int index = _data.Notes.IndexOf(note);
            if (index < 0) return;

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
        /// Rebuilds the rows in display order (spec 6.2): the expanded note's
        /// row is the surface's host - re-attached so the surface renders the
        /// note - and every other row is its card face. An empty list adds
        /// nothing: the top add row is then the panel's only content.
        /// </summary>
        private void RebuildList()
        {
            ItemsPanel.Children.Clear();

            NoteItem expanded = null;
            foreach (var note in NoteOps.SortForDisplay(_data.Notes))
            {
                if (note == null) continue;
                if (_expandedId != null && note.Id == _expandedId)
                {
                    expanded = note;
                    ItemsPanel.Children.Add(_surfaceHost);
                    continue;
                }
                ItemsPanel.Children.Add(BuildCard(note));
            }

            if (expanded == null)
            {
                // The expanded note is gone (deleted, undone away or discarded
                // as blank): the list is the plain one again.
                _expandedId = null;
                return;
            }

            // Only a rendered surface is attached: while editing, the session
            // (and its box text) belongs to the user - see Refresh.
            if (_surface.IsEditing) return;

            _surface.Attach(_data, expanded);

            // The surface's instance is reused, so its scroller keeps the last
            // note's offset: every mount starts the note at the top. The inner
            // scrollers are pass-throughs inside this list's scroller, so the
            // expanded row is pulled into the list's own view here.
            var scroller = _surface.FindName("SurfaceScroller") as ScrollViewer;
            if (scroller != null) scroller.ScrollToTop();
            _surfaceHost.BringIntoView();
        }

        /// <summary>
        /// Expands one note in place (spec 4.1): the open card settles first
        /// (its edit is flushed, never lost), then this note's row becomes the
        /// surface's host and the rendered document takes the focus - Backspace
        /// is a tunnelling handler, so it only reaches the view from a focused
        /// descendant.
        /// </summary>
        private void Expand(NoteItem note)
        {
            if (note == null) return;
            if (_expandedId != null) CollapseExpanded();

            _expandedId = note.Id;
            RebuildList();
            _surface.FocusReading();
        }

        /// <summary>
        /// Collapses the expanded card back to its face (spec 3.3/4.1): the open
        /// block settles, a blank text drops its note, and the list rebuilds in
        /// display order. Idempotent, so the surface's ‹, the keys and a click
        /// on the blank area may all call it.
        /// </summary>
        private void CollapseExpanded()
        {
            SettleExpandedEdit();
            _expandedId = null;
            RebuildList();
        }

        /// <summary>
        /// Settles the expanded note's pending edit (spec 4.3/4.5): the surface
        /// splices what is still pending and renders the whole note again, then
        /// a text that came back blank drops the note from the data. That drop
        /// is a data change, so it converges through the host's notification (a
        /// stray global save may already have written the note, so an
        /// in-memory-only removal would resurrect it on restart).
        /// </summary>
        private void SettleExpandedEdit()
        {
            var note = FindExpanded();
            _surface.LeaveEdit();

            if (note == null || !string.IsNullOrWhiteSpace(note.Text)) return;
            if (_data.Notes.Remove(note)) _host?.NotifyDataChanged();
        }

        /// <summary>The expanded note off the data, or null when nothing is
        /// expanded (or the note is gone).</summary>
        private NoteItem FindExpanded()
        {
            if (_expandedId == null) return null;
            foreach (var note in _data.Notes)
            {
                if (note != null && note.Id == _expandedId) return note;
            }
            return null;
        }

        /// <summary>
        /// The creation flow (spec 6), shared by the ＋ row and the list's blank
        /// menu: a fresh empty note joins the head of the data, expands in place
        /// and opens as one whole-text edit region. The note only lands with its
        /// first commit or leave, so opening it writes no notification. A card
        /// already open settles first, so the half-typed note is never lost.
        /// </summary>
        private void StartNewNote()
        {
            if (_data == null) return;
            if (_expandedId != null) CollapseExpanded();

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
            RebuildList();              // mounts the host and attaches the surface
            _surface.BeginEditWhole();
            _surface.FocusEditBox();
        }

        /// <summary>
        /// The view's key router (spec 4.4): inside a block edit, Esc leaves
        /// that block alone - the surface splices the typed text back and
        /// renders the whole note, the card staying expanded (Esc never loses
        /// text, and Backspace belongs to the text there). In the rendered
        /// state, Esc and Backspace collapse the card.
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

            if (_expandedId == null) return;
            if (e.Key != Key.Escape && e.Key != Key.Back) return;

            e.Handled = true;
            CollapseExpanded();
        }

        /// <summary>
        /// A click on the list's blank area collapses the expanded card (the
        /// design's other exit, spec 4.4): a click that belongs to a card face,
        /// to the expanded row's host (the surface and everything it renders,
        /// whatever the hit element) or to the add pill keeps the card open.
        /// The hit chain is walked off the visual / logical parents; a chain
        /// that never reaches this root is content too, never the blank space.
        /// </summary>
        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled || _expandedId == null) return;

            var element = (e.OriginalSource ?? e.Source) as DependencyObject;
            while (element != null)
            {
                if (ReferenceEquals(element, Root))
                {
                    CollapseExpanded();
                    return;
                }
                if (ReferenceEquals(element, AddPill) || IsCardRow(element)) return;
                element = ParentOf(element);
            }
        }

        /// <summary>True for a row of the list - a card face or the expanded
        /// note's host border (the two kinds of ItemsPanel child).</summary>
        private bool IsCardRow(DependencyObject element)
        {
            var border = element as Border;
            return border != null && ReferenceEquals(border.Parent, ItemsPanel);
        }

        /// <summary>The hit chain's next step: a text element's parent, a
        /// framework content element's parent, otherwise the visual tree.</summary>
        private static DependencyObject ParentOf(DependencyObject element)
        {
            var text = element as TextElement;
            if (text != null) return text.Parent;

            var content = element as FrameworkContentElement;
            if (content != null) return content.Parent;

            if (element is Visual) return VisualTreeHelper.GetParent(element);
            return LogicalTreeHelper.GetParent(element);
        }

        private void AddPill_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            StartNewNote();
        }

        /// <summary>
        /// The list's blank-area menu (spec 6): the one create entry, which runs
        /// the ＋ row's flow. Built per right-click so it always reflects the
        /// current language (the todo day-area pattern); the cards carry their
        /// own row menus and take precedence.
        /// </summary>
        private ContextMenu BuildBlankMenu()
        {
            var entry = new MenuItem
            {
                Header = Localization.Format("UI_NewWidgetFormat", Localization.Get("UI_WidgetKindNote"))
            };
            entry.Click += (s, e) => StartNewNote();
            return new MenuBuilder(_data.Color).AddMenuItem(entry).Build();
        }

        /// <summary>
        /// One card face: rounded border with a hover backdrop over a stack of
        /// the title (14 semibold), the modified time and the two-line excerpt
        /// (both 11 secondary), plus the progress line when the note has task
        /// boxes. The click expands the note in place; the row menu deletes it.
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
