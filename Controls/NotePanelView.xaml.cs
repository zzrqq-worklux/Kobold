using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.Controls
{
    /// <summary>
    /// The note widget's content: the top add row plus the note list. A
    /// renderer over <see cref="NoteOps"/> - it paints what the pure helpers
    /// return (title, excerpt, display order, task progress) and never invents
    /// its own text rules. The list state (cards, the modified-time order, the
    /// row delete with its undo stack) lands with T11; the reading and editing
    /// states and the creation flows are T13/T14.
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

        // The multi-step undo stack (Ctrl+Z in the list context): every delete
        // rides it with the index it came from; the newest 20 survive.
        private readonly List<NoteRemoval> _removals = new List<NoteRemoval>();

        public NotePanelView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Binds the view to its widget's data and host window and paints the
        /// list once (mirroring <see cref="TodoCalendarView.Attach"/>).
        /// </summary>
        public void Attach(NoteData data, NoteWidget host)
        {
            _data = data ?? new NoteData();
            _host = host;
            Refresh();
        }

        /// <summary>
        /// Re-renders the list from the data: call on load, after every edit and
        /// on theme/language changes. Cheap and idempotent; the guard keeps the
        /// view inert until it was attached.
        /// </summary>
        public void Refresh()
        {
            if (_data == null || _host == null) return;
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
        /// Deletes one note (the row menu's action): the source list loses it,
        /// the deletion joins the undo stack (the oldest falls off past the
        /// depth) and the list repaints through the usual save path.
        /// </summary>
        private void InvokeDelete(NoteItem note)
        {
            if (note == null) return;
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
        /// Rebuilds the cards in display order. An empty list adds nothing here:
        /// the top add row is then the panel's only content.
        /// </summary>
        private void RebuildList()
        {
            ItemsPanel.Children.Clear();
            foreach (var note in NoteOps.SortForDisplay(_data.Notes))
            {
                if (note == null) continue;
                ItemsPanel.Children.Add(BuildCard(note));
            }
        }

        /// <summary>
        /// One card: rounded border with a hover backdrop over a stack of the
        /// title (14 semibold), the modified time and the two-line excerpt
        /// (both 11 secondary), plus the progress line when the note has task
        /// boxes. The row menu deletes the note; the click that opens the
        /// reader is T13's.
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
