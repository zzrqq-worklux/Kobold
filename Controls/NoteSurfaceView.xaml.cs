using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// The note surface: the rendered state ⇄ the block-editing state (T4),
    /// plus the source box's quick-insert menu (T5) - one expanded card's
    /// content in the note list. Rendered, a single read-only,
    /// document-enabled RichTextBox paints the whole note through
    /// <see cref="NoteMarkdownRenderer"/>: its task boxes write the flipped
    /// source line back (spec 4.1/5.3) and its links keep their target for the
    /// browser. A click on a rendered line swaps that line's block into the
    /// three-pane splice (the before render / the source box / the after
    /// render, spec 4.3): the box types through a 400ms throttle straight back
    /// into the note and leaving renders the whole note again.
    /// <see cref="NotePanelView"/> keeps its own three states until T6 retires
    /// them, so the two surfaces deliberately duplicate code for now.
    /// </summary>
    public partial class NoteSurfaceView : UserControl
    {
        /// <summary>The rendered document's body font (the repo's UI font).</summary>
        private static readonly FontFamily BodyFont = new FontFamily("Segoe UI");

        /// <summary>The code block's family - the renderer's own monospace
        /// string, so the edit box matches the block it replaces.</summary>
        private static readonly FontFamily CodeFont =
            new FontFamily("Consolas, Courier New, Courier, monospace");

        /// <summary>How long typing idles before the region splices back
        /// (spec 4.3's ~400ms throttle).</summary>
        private const double ThrottleMs = 400;

        /// <summary>The quote block's left indent (the renderer's own pad).</summary>
        private const double QuoteIndent = 12;

        /// <summary>Fires when the floating ‹ is clicked: the host collapses
        /// the expanded card (spec 3.3).</summary>
        public Action CollapseRequested;

        /// <summary>Fires after a write-back: the host runs the usual save and
        /// island refresh (mirroring <see cref="NotePanelView"/>'s change
        /// event).</summary>
        public Action DataChanged;

        private NoteData _data;
        private NoteItem _note;

        /// <summary>True while a block edit session is open.</summary>
        private bool _editing;

        /// <summary>The edited region's source line range [start, end).</summary>
        private int _editStart;
        private int _editEnd;

        /// <summary>The box text the session entered with: the commit guard, so
        /// an untouched session never re-splices (spec 4.3).</summary>
        private string _editEntryText;

        /// <summary>The edited top-level block; null for a whole-text edit.
        /// Repaint re-applies the box's type from it.</summary>
        private MdBlock _editBlock;

        /// <summary>The AST before / after the edited block, captured on entry
        /// so a repaint reuses them (spec 4.3).</summary>
        private List<MdBlock> _beforeBlocks = new List<MdBlock>();
        private List<MdBlock> _afterBlocks = new List<MdBlock>();

        /// <summary>The typing throttle: restarted on every keystroke, its tick
        /// splices the region back (spec 4.3).</summary>
        private readonly DispatcherTimer _throttle;

        public NoteSurfaceView()
        {
            // Created before the XAML so a TextChanged raised while the tree
            // loads can never touch a null timer.
            _throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ThrottleMs) };
            _throttle.Tick += Throttle_Tick;
            InitializeComponent();
        }

        /// <summary>True while a block edit session is open (T6's seam).</summary>
        public bool IsEditing
        {
            get { return _editing; }
        }

        /// <summary>
        /// Binds the surface to one note of a widget's data and paints the
        /// rendered state: the document box is shown, the three editing panes
        /// are hidden and any edit session is dropped, so a reused surface
        /// never keeps a stale splice (spec 4.1).
        /// </summary>
        public void Attach(NoteData data, NoteItem note)
        {
            _data = data ?? new NoteData();
            _note = note;
            ResetEditState();
            ShowRendered();
            RenderReading();
        }

        /// <summary>
        /// Re-renders off the live theme and language: the rendered state
        /// rebuilds its document, while an edit session rebuilds the panes'
        /// style and the box's type but keeps the typed text and the session
        /// (spec 4.3).
        /// </summary>
        public void Repaint()
        {
            if (_editing)
            {
                PaintEditPanes();
                ApplyEditTypography(_editBlock);
                return;
            }
            RenderReading();
        }

        /// <summary>
        /// The rendered state's focus target: Backspace is a tunnelling
        /// handler, so it only reaches the host from a focused descendant (the
        /// old reading view's lesson, spec 4.1).
        /// </summary>
        public void FocusReading()
        {
            ReadingBox.Focus();
        }

        /// <summary>The editing state's focus target (spec 4.3).</summary>
        public void FocusEditBox()
        {
            EditBox.Focus();
        }

        /// <summary>
        /// The block edit entry (spec 4.2): the line's top-level block range
        /// lands in the box, its surrounding blocks render statically into the
        /// before / after panes and the caret lands on the clicked line. Called
        /// while editing, it splices the current region first and then switches
        /// to the new block (spec 4.3/4.4). False without a block range (an
        /// empty note, spec 5.4).
        /// </summary>
        public bool TryBeginEditAt(int line)
        {
            if (_note == null) return false;

            if (_editing) CommitRegion();

            string text = _note.Text ?? string.Empty;
            int index = NoteBlockRanges.RangeIndexAtLine(text, line);
            if (index < 0) return false;

            var ranges = NoteBlockRanges.RangesOf(text);
            var blocks = MarkdownParser.Parse(text);
            if (index >= ranges.Count || index >= blocks.Count) return false;

            _editStart = ranges[index].Start;
            _editEnd = ranges[index].End;
            _editBlock = blocks[index];
            _beforeBlocks = blocks.GetRange(0, index);
            _afterBlocks = blocks.GetRange(index + 1, blocks.Count - index - 1);

            EnterEditingState(line - _editStart);
            return true;
        }

        /// <summary>
        /// The whole text as one edit region (spec 6): the new-note flow uses
        /// it, and an empty text is legal (the box is then empty). Both
        /// surrounding renders stay empty; the box wears the body type.
        /// </summary>
        public void BeginEditWhole()
        {
            if (_note == null) return;

            if (_editing) CommitRegion();

            string text = _note.Text ?? string.Empty;
            _editStart = 0;
            _editEnd = NoteBlockRanges.LineCount(text);
            _editBlock = null;
            _beforeBlocks = new List<MdBlock>();
            _afterBlocks = new List<MdBlock>();

            EnterEditingState(0);
        }

        /// <summary>
        /// Splices whatever is still pending and renders the whole note again
        /// (spec 4.3). Idempotent: a surface that is not editing does nothing.
        /// </summary>
        public void LeaveEdit()
        {
            if (!_editing) return;

            double offset = SurfaceScroller.VerticalOffset;
            Flush();
            _editing = false;
            ShowRendered();
            RenderReading();
            Dispatcher.BeginInvoke(new Action(() => SurfaceScroller.ScrollToVerticalOffset(offset)),
                DispatcherPriority.Background);
        }

        /// <summary>
        /// Splices the throttle's pending region at once without changing the
        /// session (spec 5.2): the panel-hide / close flush. Idempotent, and a
        /// no-op when the box text never changed.
        /// </summary>
        public void Flush()
        {
            _throttle.Stop();
            CommitRegion();
        }

        /// <summary>
        /// Arms the edit state around the freshly assigned box text: the entry
        /// text becomes the commit guard, the throttle is disarmed (the text
        /// assignment itself must not schedule a splice), the panes render and
        /// the caret lands on the clicked line's offset
        /// (<paramref name="relativeLine"/>, clamped to the ends).
        /// </summary>
        private void EnterEditingState(int relativeLine)
        {
            EditBox.Text = NoteBlockRanges.ExtractLines(_note.Text, _editStart, _editEnd);
            _editEntryText = EditBox.Text;
            _editing = true;
            _throttle.Stop();

            PaintEditPanes();
            ApplyEditTypography(_editBlock);
            ShowEditing();
            SetCaret(relativeLine);
            FocusEditBox();
        }

        /// <summary>Paints the before / after renders: static documents (no
        /// callbacks), so a task box or link inside them never writes while a
        /// block is edited (spec 4.3).</summary>
        private void PaintEditPanes()
        {
            if (_note == null) return;
            var style = BuildRenderStyle();
            BeforeBox.Document = NoteMarkdownRenderer.Build(_beforeBlocks, style, null);
            AfterBox.Document = NoteMarkdownRenderer.Build(_afterBlocks, style, null);
        }

        /// <summary>
        /// The source box wears the edited block's own type (spec 4.2): a
        /// heading its ladder size and weight, a code block the renderer's
        /// monospace family, a quote the muted ink and the left indent,
        /// everything else (and a whole-text edit) the plain body type.
        /// </summary>
        private void ApplyEditTypography(MdBlock block)
        {
            EditBox.FontFamily = BodyFont;
            EditBox.FontSize = UiTokens.FontBody;
            EditBox.FontWeight = FontWeights.Normal;
            EditBox.Foreground = ThemeManager.TextBrush;
            EditBox.Padding = new Thickness(0);

            var heading = block as MdHeading;
            if (heading != null)
            {
                EditBox.FontSize = NoteMarkdownRenderer.HeadingFontSize(heading.Level, UiTokens.FontBody);
                EditBox.FontWeight = FontWeights.SemiBold;
                return;
            }

            if (block is MdCodeBlock)
            {
                EditBox.FontFamily = CodeFont;
                return;
            }

            if (block is MdQuote)
            {
                EditBox.Foreground = ThemeManager.SecondaryTextBrush;
                EditBox.Padding = new Thickness(QuoteIndent, 0, 0, 0);
            }
        }

        /// <summary>
        /// Splices the box text back over the region (spec 4.3/5.1): the guard
        /// skips an untouched box, a real change writes the note, stamps the
        /// modified time and notifies the host, and the region's end tracks the
        /// new line count so a repeated splice converges.
        /// </summary>
        private void CommitRegion()
        {
            if (!_editing || _note == null) return;
            if (EditBox.Text == _editEntryText) return;

            string updated = NoteBlockRanges.ReplaceLines(_note.Text, _editStart, _editEnd, EditBox.Text);
            if (updated != _note.Text)
            {
                _note.Text = updated;
                _note.ModifiedAt = DateTime.Now;
                DataChanged?.Invoke();
            }

            _editEnd = _editStart + NoteBlockRanges.LineCount(EditBox.Text);
            _editEntryText = EditBox.Text;
        }

        /// <summary>Restarts the typing throttle (spec 4.3).</summary>
        private void EditBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_editing) return;
            _throttle.Stop();
            _throttle.Start();
        }

        /// <summary>The throttle's tick: the pending region splices back.</summary>
        private void Throttle_Tick(object sender, EventArgs e)
        {
            _throttle.Stop();
            CommitRegion();
        }

        /// <summary>
        /// The rendered state's click-to-edit (spec 4.2/4.4), attached to the
        /// root so it covers the reading box and the before / after panes: a
        /// handled click (a task glyph, a link) and a drag-selection release
        /// are skipped, a click on a tagged line enters its block edit and a
        /// click with no tagged ancestor (the box itself, the chrome) does
        /// nothing.
        /// </summary>
        private void SurfaceRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled) return;

            // The off-screen checks raise the event by hand, so the original
            // source may be unset there; the real input route always fills it.
            object hit = e.OriginalSource ?? e.Source;
            if (SelectionIsNotEmpty(hit)) return;

            int line = TaggedLine(hit);
            if (line < 0) return;

            TryBeginEditAt(line);
        }

        /// <summary>
        /// The source line a hit stands for: the first tagged element up the hit
        /// chain, or -1 for a link (the renderer owns its click) and for a chain
        /// with no tag at all.
        /// </summary>
        private static int TaggedLine(object hit)
        {
            var element = hit as DependencyObject;
            while (element != null)
            {
                if (element is Hyperlink) return -1;

                int line = NoteMarkdownRenderer.GetSourceLine(element);
                if (line >= 0) return line;

                var glyph = element as FrameworkElement;
                if (glyph != null && glyph.Tag is int) return (int)glyph.Tag;

                element = ParentOf(element);
            }
            return -1;
        }

        /// <summary>True while the hit sits inside a box with a live drag
        /// selection: the release that ends a selection must not enter an
        /// edit.</summary>
        private static bool SelectionIsNotEmpty(object hit)
        {
            var element = hit as DependencyObject;
            while (element != null)
            {
                var box = element as RichTextBox;
                if (box != null) return !box.Selection.IsEmpty;
                element = ParentOf(element);
            }
            return false;
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

        /// <summary>Puts the caret on the clicked line's first character inside
        /// the box, clamped to the box's ends. The offset is counted off the
        /// text: the box carries no line metrics until it is laid out, and the
        /// entry must not depend on a layout pass.</summary>
        private void SetCaret(int relativeLine)
        {
            string text = EditBox.Text ?? string.Empty;
            int caret = 0;
            if (relativeLine > 0)
            {
                string[] lines = text.Split('\n');
                for (int i = 0; i < relativeLine && i < lines.Length; i++)
                    caret += lines[i].Length + 1;
            }
            if (caret > text.Length) caret = text.Length;
            EditBox.CaretIndex = caret;
        }

        /// <summary>The editing state: the reading box gives way to the three
        /// panes (spec 4.3).</summary>
        private void ShowEditing()
        {
            ReadingBox.Visibility = Visibility.Collapsed;
            BeforeBox.Visibility = Visibility.Visible;
            EditBox.Visibility = Visibility.Visible;
            AfterBox.Visibility = Visibility.Visible;
        }

        /// <summary>Drops every trace of an edit session (Attach's rule: a
        /// surface always lands in the rendered state).</summary>
        private void ResetEditState()
        {
            _throttle.Stop();
            _editing = false;
            _editStart = -1;
            _editEnd = -1;
            _editEntryText = null;
            _editBlock = null;
            _beforeBlocks = new List<MdBlock>();
            _afterBlocks = new List<MdBlock>();
            EditBox.Text = string.Empty;
        }

        private void CollapseButton_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CollapseRequested?.Invoke();
        }

        /// <summary>The rendered state: the document box alone is visible.</summary>
        private void ShowRendered()
        {
            ReadingBox.Visibility = Visibility.Visible;
            BeforeBox.Visibility = Visibility.Collapsed;
            EditBox.Visibility = Visibility.Collapsed;
            AfterBox.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Paints the note into the read-only box: the AST comes from the
        /// parser, the style from the live theme and the callbacks route the
        /// task boxes and the links back to the data and the shell
        /// (spec 4.1/5.2/5.3).
        /// </summary>
        private void RenderReading()
        {
            if (_note == null) return;
            ReadingBox.Document = NoteMarkdownRenderer.Build(
                MarkdownParser.Parse(_note.Text), BuildRenderStyle(), RenderCallbacks());
        }

        /// <summary>The renderer's outbound actions for the rendered document.</summary>
        private NoteRenderCallbacks RenderCallbacks()
        {
            return new NoteRenderCallbacks
            {
                ToggleTaskLine = ToggleTask,
                OpenLink = OpenLink
            };
        }

        /// <summary>
        /// A task box was clicked (spec 4.1/5.3): the source line flips, the
        /// note becomes the newest modification, the host is notified and the
        /// document repaints. The scroll offset is read before the repaint and
        /// restored once the fresh document is laid out, so a click deep in a
        /// note does not throw the reader back to the top (a surface that never
        /// scrolled - every off-screen check - restores 0 harmlessly).
        /// </summary>
        private void ToggleTask(int line)
        {
            if (_note == null) return;

            double offset = SurfaceScroller.VerticalOffset;
            _note.Text = NoteOps.ToggleTaskAt(_note.Text, line);
            _note.ModifiedAt = DateTime.Now;
            DataChanged?.Invoke();
            RenderReading();
            Dispatcher.BeginInvoke(new Action(() => SurfaceScroller.ScrollToVerticalOffset(offset)),
                DispatcherPriority.Background);
        }

        /// <summary>
        /// A link was clicked (spec 4.1/5.2): an http/https target opens in the
        /// default browser, anything else stays inert (the parser keeps the
        /// non-web targets literal). Mirrors the old reading view's link open.
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

        /// <summary>The widget colour as a frozen brush (the same read the old
        /// reading view makes for its accent).</summary>
        private Brush WidgetAccentBrush()
        {
            var brush = new SolidColorBrush(Utils.HexToColor(_data.Color));
            brush.Freeze();
            return brush;
        }
    }
}
