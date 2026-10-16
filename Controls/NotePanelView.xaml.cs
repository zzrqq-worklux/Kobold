using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// The note widget's content, the three states of spec 6.2 over one
    /// surface: the list (the top add row plus the cards), the reading view
    /// (the rendered, read-only document) and - T14 - the source editor. A
    /// renderer over <see cref="NoteOps"/> and <see cref="NoteMarkdownRenderer"/>:
    /// it paints what the pure helpers return (title, excerpt, display order,
    /// task progress, the rendered document) and never invents its own text
    /// rules. The list state (cards, the modified-time order, the row delete
    /// with its undo stack) landed with T11; the reading state and the task
    /// box write-back are T13; the editing state and the creation flows are
    /// T14/T15.
    /// </summary>
    public partial class NotePanelView : UserControl
    {
        /// <summary>The state the surface shows (spec 6.2).</summary>
        private enum State
        {
            List,
            Reading,
            Editing
        }

        /// <summary>The rendered document's body font (the repo's UI font).</summary>
        private static readonly FontFamily BodyFont = new FontFamily("Segoe UI");

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

        // The reading state: the note it renders and the state itself. A blank
        // view is the list; T14 flips to Editing from the reading header.
        private State _state = State.List;
        private NoteItem _current;

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
        /// Re-renders the active state from the data: call on load, after every
        /// edit and on theme/language changes. Cheap and idempotent; the guard
        /// keeps the view inert until it was attached.
        /// </summary>
        public void Refresh()
        {
            if (_data == null || _host == null) return;

            // One state at a time: the reading container draws over the list,
            // which stays built so a return is instant and its cards intact.
            bool reading = _state == State.Reading;
            ListHost.Visibility = reading ? Visibility.Collapsed : Visibility.Visible;
            ReadingHost.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;

            if (reading)
            {
                RenderReading();
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

        #region Reading state (T13)

        /// <summary>
        /// Drills into the reading view (spec 6.2/6.3): the list gives way to
        /// the note's rendered document. Reading writes nothing back - the
        /// read-only box is only the paint target.
        /// </summary>
        private void OpenNote(NoteItem note)
        {
            if (note == null) return;
            _current = note;
            _state = State.Reading;
            Refresh();
            // Every drill-in starts at the top: the scroller keeps its offset
            // across the state flip, so a note opened after a scrolled one must
            // not start mid-page.
            ReadingScroller.ScrollToTop();
        }

        /// <summary>
        /// Returns to the list (the header's back button and Backspace): the
        /// cards repaint so a task toggle's fresh modified time re-sorts them
        /// (spec 6.2's SortForDisplay order).
        /// </summary>
        private void BackToList()
        {
            _current = null;
            _state = State.List;
            Refresh();
        }

        /// <summary>Backspace leaves the reading view (spec 6.2). The tunnelling
        /// handler runs before the read-only box's own class handling, so the
        /// key never reaches the document; in the list state it stays untouched
        /// (the todo editor's ladder and the host's own keys keep it).</summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_state != State.Reading || e.Key != Key.Back) return;
            e.Handled = true;
            BackToList();
        }

        private void ReadingBack_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            BackToList();
        }

        /// <summary>
        /// Paints the current note into the read-only box: the AST comes from
        /// the parser, the style from the live theme and the callbacks route
        /// the task boxes and the links back to the data and the shell
        /// (spec 5.2/5.3).
        /// </summary>
        private void RenderReading()
        {
            if (_current == null) return;
            ReadingBox.Document = NoteMarkdownRenderer.Build(
                MarkdownParser.Parse(_current.Text), BuildRenderStyle(), ReadingCallbacks());
        }

        /// <summary>The renderer's outbound actions for the reading document.</summary>
        private NoteRenderCallbacks ReadingCallbacks()
        {
            return new NoteRenderCallbacks
            {
                ToggleTaskLine = ToggleTaskAt,
                OpenLink = OpenLink
            };
        }

        /// <summary>
        /// A task box was clicked (spec 5.3): the source line flips, the note
        /// becomes the newest modification and the document repaints through
        /// the usual save path. The scroll offset is read before the repaint
        /// and restored once the fresh document is laid out, so a click deep
        /// in a note does not throw the reader back to the top (a view that
        /// never scrolled - every off-screen check - restores 0 harmlessly).
        /// </summary>
        private void ToggleTaskAt(int line)
        {
            if (_current == null) return;

            double offset = ReadingScroller.VerticalOffset;
            _current.Text = NoteOps.ToggleTaskAt(_current.Text, line);
            _current.ModifiedAt = DateTime.Now;
            AfterEdit();
            Dispatcher.BeginInvoke(new Action(() => ReadingScroller.ScrollToVerticalOffset(offset)),
                DispatcherPriority.Background);
        }

        /// <summary>
        /// A link was clicked (spec 5.2): an http/https target opens in the
        /// default browser, anything else stays inert (the parser keeps the
        /// non-web targets literal). Mirrors the folder view's shell open.
        /// </summary>
        private static void OpenLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Note link open failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The render style off the live theme (design 5.1): the body and the
        /// secondary ink are theme tokens, the accent is the widget's own
        /// colour, the code band and the rules follow the theme, and the system
        /// high-contrast mode is passed through (the renderer then swaps the
        /// whole palette for the system roles).
        /// </summary>
        private NoteRenderStyle BuildRenderStyle()
        {
            return new NoteRenderStyle
            {
                Text = ThemeManager.TextBrush,
                Muted = ThemeManager.SecondaryTextBrush,
                Accent = WidgetAccentBrush(),
                CodeBackground = ThemeManager.ControlBackgroundBrush,
                Divider = ThemeManager.BorderBrush,
                HighContrast = ThemeManager.IsHighContrast,
                BodyFont = BodyFont,
                BaseFontSize = UiTokens.FontBody
            };
        }

        /// <summary>The widget colour as a frozen brush (the same read the todo
        /// view makes for its accent).</summary>
        private Brush WidgetAccentBrush()
        {
            var brush = new SolidColorBrush(Utils.HexToColor(_data.Color));
            brush.Freeze();
            return brush;
        }

        #endregion

        /// <summary>
        /// One card: rounded border with a hover backdrop over a stack of the
        /// title (14 semibold), the modified time and the two-line excerpt
        /// (both 11 secondary), plus the progress line when the note has task
        /// boxes. The click drills into the reader; the row menu deletes the
        /// note.
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
                OpenNote(note);
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
