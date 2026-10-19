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
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// The note surface: the rendered state ⇄ the block-editing state, plus the
    /// source box's quick-insert menu - one expanded card's content in the note
    /// list. Rendered, a single read-only, document-enabled RichTextBox paints
    /// the whole note through <see cref="NoteMarkdownRenderer"/>: its task boxes
    /// write the flipped source line back (spec 4.1/5.3) and its links keep
    /// their target for the browser. A click on a rendered line swaps that
    /// line's block into the three-pane splice (the before render / the source
    /// box / the after render, spec 4.3): the box types through a 400ms
    /// throttle straight back into the note and leaving renders the whole note
    /// again. <see cref="NotePanelView"/> owns one instance for its whole life
    /// and mounts it in the expanded row (the shipped workbench design).
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

        /// <summary>The document text the session entered with: the before /
        /// after panes were painted from it, so their line tags still name its
        /// blocks. A pane click resolves against this snapshot, never against
        /// the edited document, whose line numbers have moved (spec 4.3).</summary>
        private string _entryText;

        /// <summary>The edited block's index in <see cref="_entryText"/> (-1
        /// for a whole-text edit): the anchor the after pane's block indices
        /// are counted from.</summary>
        private int _entryIndex = -1;

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

            // The box refills its quick-insert menu in place on every opening
            // (T5); the menu itself is carried from the moment an edit opens
            // (EnterEditingState).
            EditBox.ContextMenuOpening += EditBox_ContextMenuOpening;
        }

        /// <summary>True while a block edit session is open (the surface's editing seam).</summary>
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
            if (index >= ranges.Count) return false;
            return BeginEditAtIndex(index, line - ranges[index].Start);
        }

        /// <summary>
        /// Opens the block at <paramref name="index"/> of the current text with
        /// the caret <paramref name="relativeLine"/> lines into it: the one
        /// entry both the line click and a pane switch land on. The session's
        /// entry snapshot is refreshed here, so the panes this entry paints are
        /// the ones a later pane click resolves against.
        /// </summary>
        private bool BeginEditAtIndex(int index, int relativeLine)
        {
            if (_note == null) return false;

            string text = _note.Text ?? string.Empty;
            var ranges = NoteBlockRanges.RangesOf(text);
            var blocks = MarkdownParser.Parse(text);
            if (index < 0 || index >= ranges.Count || index >= blocks.Count) return false;

            _editStart = ranges[index].Start;
            _editEnd = ranges[index].End;
            _editBlock = blocks[index];
            _beforeBlocks = blocks.GetRange(0, index);
            _afterBlocks = blocks.GetRange(index + 1, blocks.Count - index - 1);
            _entryText = text;
            _entryIndex = index;

            if (relativeLine < 0) relativeLine = 0;
            EnterEditingState(relativeLine);
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
            _entryText = text;
            _entryIndex = -1;

            EnterEditingState(0);
        }

        /// <summary>
        /// Splices whatever is still pending and renders the whole note again
        /// (spec 4.3). Idempotent: a surface that is not editing does nothing.
        /// </summary>
        public void LeaveEdit()
        {
            if (!_editing) return;

            Flush();
            _editing = false;
            ShowRendered();
            RenderReading();

            // Leaving is a return to the rendered document, so its box takes
            // the focus: the panel's Esc / Backspace router is a tunnelling
            // handler and only sees keys from a focused descendant.
            FocusReading();
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
            // The box carries its quick-insert menu from the moment the edit
            // opens: WPF's own text-editor handler only steps aside (letting the
            // opening event reach us, and opening what we build) for a box that
            // already has a custom ContextMenu - with none it opens its built-in
            // Cut/Copy/Paste menu and marks the event handled. Both entries
            // (TryBeginEditAt / BeginEditWhole) land here.
            EditBox.ContextMenu = BuildEditorMenu();
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
        /// The rendered / editing states' click handler, attached to the root
        /// so it covers the reading box and the before / after panes: a handled
        /// click (a task glyph, a link) and a drag-selection release are
        /// skipped, a click on a tagged line opens its block edit, a click in a
        /// pane while editing switches to the block it names, and a click with
        /// no tagged ancestor (the box itself, the chrome) does nothing.
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

            // While editing, a click in either static pane names a block of the
            // *entry* document. Resolve it before the splice: the line tags are
            // stale the moment the edited region changes height (spec 4.3).
            if (_editing)
            {
                TrySwitchPaneEdit(line);
                return;
            }

            TryBeginEditAt(line);
        }

        /// <summary>
        /// A click on the before / after pane while a block is edited (spec
        /// 4.3/4.4): the click names a block in the entry document, the splice
        /// commits, and the edit reopens on the same block by identity - the
        /// before pane's block index is unchanged, while the after pane's block
        /// sits behind however many top-level blocks the edit produced. That
        /// count is read off the spliced text (the block total less the entry
        /// snapshot's after blocks), never off the raw line count: consecutive
        /// lines parse as one paragraph, so a growing edit can produce fewer
        /// blocks than lines. Resolving the identity first is what keeps a
        /// click after a growing edit from landing on the first block of the
        /// edit itself. The panes tile the entry document around the edited
        /// block, so the clicked entry line alone says which pane it came from.
        /// </summary>
        private bool TrySwitchPaneEdit(int line)
        {
            if (_entryText == null || _entryIndex < 0) return false;

            var entryRanges = NoteBlockRanges.RangesOf(_entryText);
            int entryIndex = NoteBlockRanges.RangeIndexAtLine(_entryText, line);
            if (entryIndex < 0 || entryIndex >= entryRanges.Count) return false;
            if (entryIndex == _entryIndex) return false; // the edited block is in the box

            bool afterPane = entryIndex > _entryIndex;
            int relative = line - entryRanges[entryIndex].Start;
            int after = entryIndex - _entryIndex - 1;
            if (afterPane && (after < 0 || after >= _afterBlocks.Count)) return false;

            CommitRegion();

            int target = afterPane
                ? MarkdownParser.Parse(_note.Text ?? string.Empty).Count - _afterBlocks.Count + after
                : entryIndex;
            return BeginEditAtIndex(target, relative);
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
        /// framework content element's parent, otherwise the visual tree. The
        /// note panel's blank-area click walks the same chain, so this is the
        /// one shared helper.</summary>
        internal static DependencyObject ParentOf(DependencyObject element)
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
            _entryText = null;
            _entryIndex = -1;
            _beforeBlocks = new List<MdBlock>();
            _afterBlocks = new List<MdBlock>();
            EditBox.Text = string.Empty;
        }

        #region Editor quick-insert menu (T5)

        /// <summary>
        /// The source box asks for its menu: the editing state refills the
        /// quick-insert menu the box already carries, and every other state
        /// leaves the box with none (so a stale menu never opens after the edit
        /// closed, Spec 6.4).
        /// </summary>
        private void EditBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (!_editing)
            {
                EditBox.ContextMenu = null;
                return;
            }

            var menu = EditBox.ContextMenu;
            if (menu == null)
            {
                // The box carries one from the moment the edit opened
                // (EnterEditingState); only a menu cleared since then lands here.
                menu = new ContextMenu();
                EditBox.ContextMenu = menu;
            }
            FillEditorMenu(menu);
        }

        /// <summary>
        /// The edit box's right-click quick-insert menu (Spec 6.4), freshly
        /// built. The box carries one from the moment the edit opens (see
        /// <see cref="EnterEditingState"/>) and
        /// <see cref="EditBox_ContextMenuOpening"/> refills it in place on every
        /// opening.
        /// </summary>
        private ContextMenu BuildEditorMenu()
        {
            var menu = new ContextMenu();
            FillEditorMenu(menu);
            return menu;
        }

        /// <summary>
        /// Fills <paramref name="menu"/> with the edit box's entries: the
        /// standard four clipboard commands (headers localized, the behaviour is
        /// WPF's own), a separator, the 标题 and 格式 submenus, then the seven
        /// flat inserts. Every insert entry runs one <see cref="NoteEditorOps"/>
        /// op over the box, the placeholder words come from the localization
        /// table, and the widget colour wash comes from the shared
        /// <see cref="MenuBuilder"/> (no template is touched).
        ///
        /// The refill is in place because the instance is the one the box
        /// already carries: WPF's own text-editor menu handler grabs the box's
        /// ContextMenu before the opening event reaches this view and opens that
        /// very instance afterwards, so swapping in a fresh one would leave its
        /// bookkeeping (and the menu the user sees) behind.
        /// </summary>
        private void FillEditorMenu(ContextMenu menu)
        {
            menu.Items.Clear();
            menu.Items.Add(CommandEntry("Menu_Cut", ApplicationCommands.Cut));
            menu.Items.Add(CommandEntry("Menu_Copy", ApplicationCommands.Copy));
            menu.Items.Add(CommandEntry("Menu_Paste", ApplicationCommands.Paste));
            menu.Items.Add(CommandEntry("Menu_SelectAll", ApplicationCommands.SelectAll));
            menu.Items.Add(new Separator());

            var heading = new MenuItem { Header = Localization.Get("Menu_Heading") };
            for (int level = 1; level <= 6; level++)
            {
                int chosen = level;
                var entry = new MenuItem { Header = Localization.Format("Menu_InsertHeading", chosen) };
                entry.Click += (s, e) => ApplyEditorOp(
                    (text, start, length) => NoteEditorOps.InsertHeading(text, start, length, chosen));
                heading.Items.Add(entry);
            }
            menu.Items.Add(heading);

            var format = new MenuItem { Header = Localization.Get("Menu_Format") };
            format.Items.Add(InsertEntry("Menu_FormatBold",
                (text, start, length) => NoteEditorOps.WrapBold(text, start, length, Localization.Get("Note_PlaceholderBold"))));
            format.Items.Add(InsertEntry("Menu_FormatItalic",
                (text, start, length) => NoteEditorOps.WrapItalic(text, start, length, Localization.Get("Note_PlaceholderItalic"))));
            format.Items.Add(InsertEntry("Menu_FormatStrike",
                (text, start, length) => NoteEditorOps.WrapStrike(text, start, length, Localization.Get("Note_PlaceholderStrike"))));
            format.Items.Add(InsertEntry("Menu_FormatCode",
                (text, start, length) => NoteEditorOps.WrapInlineCode(text, start, length, Localization.Get("Note_PlaceholderInlineCode"))));
            format.Items.Add(InsertEntry("Menu_FormatLink",
                (text, start, length) => NoteEditorOps.WrapLink(text, start, length, Localization.Get("Note_PlaceholderLink"))));
            menu.Items.Add(format);

            string firstColumn = Localization.Format("Note_TableColumnFormat", 1);
            string secondColumn = Localization.Format("Note_TableColumnFormat", 2);

            menu.Items.Add(InsertEntry("Menu_InsertBullets",
                (text, start, length) => NoteEditorOps.InsertBullet(text, start, length)));
            menu.Items.Add(InsertEntry("Menu_InsertNumbers",
                (text, start, length) => NoteEditorOps.InsertNumbered(text, start, length)));
            menu.Items.Add(InsertEntry("Menu_InsertTasks",
                (text, start, length) => NoteEditorOps.InsertTask(text, start, length)));
            menu.Items.Add(InsertEntry("Menu_InsertQuote",
                (text, start, length) => NoteEditorOps.InsertQuote(text, start, length)));
            menu.Items.Add(InsertEntry("Menu_InsertCode",
                (text, start, length) => NoteEditorOps.InsertCodeBlock(text, start, length)));
            menu.Items.Add(InsertEntry("Menu_InsertTable",
                (text, start, length) => NoteEditorOps.InsertTable(text, start, length, firstColumn, secondColumn)));
            menu.Items.Add(InsertEntry("Menu_InsertDivider",
                (text, start, length) => NoteEditorOps.InsertDivider(text, start, length)));

            MenuBuilder.Prepare(menu, _data.Color);
        }

        /// <summary>One of the standard four: the header is ours, the behaviour
        /// is WPF's own routed command aimed at the box (the menu itself takes
        /// the focus, so the target is pinned rather than inferred).</summary>
        private MenuItem CommandEntry(string labelKey, ICommand command)
        {
            return new MenuItem
            {
                Header = Localization.Get(labelKey),
                Command = command,
                CommandTarget = EditBox
            };
        }

        /// <summary>One insert entry: its localized label and the op it runs.</summary>
        private MenuItem InsertEntry(string labelKey, Func<string, int, int, (string, int, int)> op)
        {
            var item = new MenuItem { Header = Localization.Get(labelKey) };
            item.Click += (s, e) => ApplyEditorOp(op);
            return item;
        }

        /// <summary>
        /// Runs one insert op over the source box (Spec 6.4): the op reads the
        /// box's text and selection, and its result replaces the whole text
        /// through the box's own editing stack - <c>SelectedText</c> over the
        /// full range, never a raw <c>Text</c> set - so Ctrl+Z rolls one insert
        /// back as a single step. The replacement rides one change block
        /// (<c>TextBoxBase.BeginChange</c> / <c>EndChange</c>): WPF records an
        /// undo unit only while a parent unit is open, so the block is what
        /// turns the write-back into that one native unit (a bare programmatic
        /// write registers none and flushes the stack). The op's caret /
        /// selection follows the block, and the box takes the focus back (the
        /// menu click moved it away).
        /// </summary>
        private void ApplyEditorOp(Func<string, int, int, (string, int, int)> op)
        {
            string text = EditBox.Text ?? string.Empty;
            var result = op(text, EditBox.SelectionStart, EditBox.SelectionLength);

            EditBox.BeginChange();
            try
            {
                EditBox.SelectionStart = 0;
                EditBox.SelectionLength = text.Length;
                EditBox.SelectedText = result.Item1;
            }
            finally
            {
                EditBox.EndChange();
            }

            int start = Math.Min(Math.Max(result.Item2, 0), EditBox.Text.Length);
            int length = Math.Min(Math.Max(result.Item3, 0), EditBox.Text.Length - start);
            EditBox.SelectionStart = start;
            EditBox.SelectionLength = length;
            EditBox.Focus();
        }

        #endregion

        /// <summary>
        /// The floating ‹ (spec 3.3): the host collapses the expanded card.
        /// Handled here on the button-up, not the button-down - the card face
        /// behind the collapsed row expands on its own button-up, so collapsing
        /// on the down would let the release immediately re-expand the card.
        /// The event is marked handled so the surface's click-to-edit handler
        /// never sees it.
        /// </summary>
        private void CollapseButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
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
        /// document repaints. The fresh document then takes the focus back: the
        /// glyph that was clicked belonged to the old document. The host's list
        /// scroller owns the scroll offset in the workbench (the surface's own
        /// scroller is a pass-through there), so the surface restores nothing.
        /// </summary>
        private void ToggleTask(int line)
        {
            if (_note == null) return;

            _note.Text = NoteOps.ToggleTaskAt(_note.Text, line);
            _note.ModifiedAt = DateTime.Now;
            DataChanged?.Invoke();
            RenderReading();
            FocusReading();
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
