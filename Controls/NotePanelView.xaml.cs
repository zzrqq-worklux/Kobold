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
using Localization = Kobold.Core.Localization;

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
    /// box write-back are T13; the editing state, the [source|preview] segment
    /// and the creation flow are T14 (the source box's insert menu is T15).
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

        // The editing state (T14): whether this session created the open note
        // (Esc then has no earlier text to restore, so the note goes) and which
        // half of the [source|preview] segment paints. The box text itself is
        // the pending edit - it is only read at the commit.
        private bool _editingIsNew;
        private bool _previewMode;

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

            // The create entry lives on the list's blank space: the cards carry
            // their own row menus (the nearest ContextMenu wins) and only the
            // list state offers the blank menu, so the reader and the editor
            // keep their own surfaces (the todo day-area pattern).
            Root.ContextMenuOpening += (s, e) =>
            {
                Root.ContextMenu = _state == State.List ? BuildBlankMenu() : null;
            };

            // The source editor's right-click quick-insert menu (spec 6.4):
            // rebuilt on every opening so the labels and the widget tint are
            // current (the list's blank-area pattern).
            _editingBox.ContextMenuOpening += EditingBox_ContextMenuOpening;

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

            // The segment's labels follow the active language, and Refresh is
            // the language switch's repaint path (also outside the editor).
            PaintSegmentLabels();

            // One state at a time: the reading and the editing containers draw
            // over the list, which stays built so a return is instant and its
            // cards intact.
            bool reading = _state == State.Reading;
            bool editing = _state == State.Editing;
            ListHost.Visibility = _state == State.List ? Visibility.Visible : Visibility.Collapsed;
            ReadingHost.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
            EditingHost.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;

            if (reading)
            {
                RenderReading();
                return;
            }
            if (editing)
            {
                // The editor's own surface only: the box text is the user's
                // pending edit and is never rewritten off the data here (a
                // theme switch or a widget update repaints through this path).
                RenderEditing();
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

        /// <summary>
        /// The editing state's key router: Esc rolls the edit back from
        /// whichever half is up. The preview's read-only document is the focus
        /// target there (the collapsed source box cannot hold focus), so the
        /// cancel lives on this view-level tunnelling handler rather than on
        /// the box. Backspace is the reading state's alone - in the editor it
        /// belongs to the text (spec 6.2).
        /// </summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_state == State.Reading && e.Key == Key.Back)
            {
                e.Handled = true;
                BackToList();
                return;
            }

            if (_state == State.Editing && e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelEdit();
            }
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

        #region Editing state (T14)

        /// <summary>
        /// The reading header's ✎ (spec 6.2): the open note's source opens in
        /// the editor. Nothing is open (or the note is gone) → nothing.
        /// </summary>
        private void ReadingEdit_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_current == null) return;
            OpenEditor(_current);
        }

        /// <summary>
        /// Opens the source editor on a note: its own text lands in the box,
        /// the pending-edit flags reset and the surface repaints. Every entry
        /// (the reading header's ✎) starts on the source half - the mode is a
        /// session choice, not stored state.
        /// </summary>
        private void OpenEditor(NoteItem note)
        {
            if (note == null) return;
            _current = note;
            _editingIsNew = false;
            _previewMode = false;
            _state = State.Editing;

            // The box carries its quick-insert menu from here on: WPF's own
            // text-editor handler only steps aside (letting the opening event
            // reach us, and opening what we build) for a box that already has a
            // custom ContextMenu - with none it opens its built-in
            // Cut/Copy/Paste menu and marks the event handled.
            _editingBox.ContextMenu = BuildEditorMenu();
            _editingBox.Text = note.Text ?? string.Empty;
            Refresh();
            _editingBox.Focus();
        }

        /// <summary>
        /// The creation flow (spec 6.3), shared by the ＋ row and the list's
        /// blank menu: a fresh empty note joins the data and the editor opens
        /// on it. The note only lives through the edit - an empty commit (and
        /// Esc) drops it again, so empty notes never accumulate.
        /// </summary>
        private void StartNewNote()
        {
            if (_data == null) return;

            var now = DateTime.Now;
            var note = new NoteItem
            {
                Id = Guid.NewGuid().ToString(),
                Text = string.Empty,
                CreatedAt = now,
                ModifiedAt = now
            };
            _data.Notes.Add(note);

            _current = note;
            _editingIsNew = true;
            _previewMode = false;
            _state = State.Editing;
            _editingBox.ContextMenu = BuildEditorMenu();   // the same carry, see OpenEditor
            _editingBox.Text = string.Empty;
            Refresh();
            _editingBox.Focus();
        }

        /// <summary>
        /// Settles the open editor (spec 6.2/6.3): the box text trimmed empty
        /// drops the note (one created for this session and an existing note
        /// emptied by hand follow the same rule), otherwise a changed text is
        /// written back with a fresh modified stamp. Leaving without a change
        /// writes nothing. Idempotent, so the header's two commit buttons, the
        /// panel's collapse and the window's closing may all call it.
        /// </summary>
        public void CommitPendingEdit()
        {
            if (_state != State.Editing) return;

            var note = _current;
            string text = _editingBox.Text ?? string.Empty;
            LeaveEditing();

            if (note == null) return;

            if (text.Trim().Length == 0)
            {
                _data.Notes.Remove(note);
                AfterEdit();
                return;
            }

            if (note.Text != text)
            {
                note.Text = text;
                note.ModifiedAt = DateTime.Now;
                AfterEdit();
                return;
            }

            // Nothing changed: no write, no stamp - just the list. The order
            // is the proof (the note keeps its old slot).
            Refresh();
        }

        /// <summary>
        /// Esc (spec 6.2): the session's edits roll back. The open note keeps
        /// its text and its stamp; a note created for this session has no
        /// earlier text to restore, so it is dropped - and that drop is a data
        /// change, so it converges through the host's change notification (the
        /// usual save path). A stray global save between the add and the cancel
        /// may already have written the placeholder, so an in-memory-only
        /// removal would resurrect it on restart.
        /// </summary>
        private void CancelEdit()
        {
            if (_state != State.Editing) return;

            var note = _current;
            bool isNew = _editingIsNew;
            LeaveEditing();

            if (!isNew || note == null)
            {
                // The open note keeps its data: nothing to converge.
                Refresh();
                return;
            }

            _data.Notes.Remove(note);
            AfterEdit();
        }

        /// <summary>Leaves the editing state (list, no open note, no session
        /// flags); what happens to the note itself is the caller's business.</summary>
        private void LeaveEditing()
        {
            _state = State.List;
            _current = null;
            _editingIsNew = false;
            _previewMode = false;
        }

        private void EditingBack_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CommitPendingEdit();
        }

        private void EditingDone_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CommitPendingEdit();
        }

        private void AddPill_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            StartNewNote();
        }

        private void SourceTab_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SetPreview(false);
        }

        private void PreviewTab_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SetPreview(true);
        }

        /// <summary>The [source|preview] segment (spec 6.2): one text, two paint
        /// targets - the box keeps the text either way. Coming back to the
        /// source half restores the caret: the segment click moved the focus
        /// away, and typing must not need a second click.</summary>
        private void SetPreview(bool preview)
        {
            _previewMode = preview;
            ApplyEditingSurface();
            if (preview)
            {
                RenderEditorPreview();
                return;
            }
            _editingBox.Focus();
        }

        /// <summary>
        /// Repaints the editor's own surface (not the text): the segment's
        /// active half and the preview document when the preview is up.
        /// </summary>
        private void RenderEditing()
        {
            ApplyEditingSurface();
            if (_previewMode) RenderEditorPreview();
        }

        /// <summary>Which half of the segment paints: the active one takes the
        /// hover wash and the primary ink, the other stays flat and muted.</summary>
        private void ApplyEditingSurface()
        {
            _editingBox.Visibility = _previewMode ? Visibility.Collapsed : Visibility.Visible;
            _previewBox.Visibility = _previewMode ? Visibility.Visible : Visibility.Collapsed;

            SourceTab.Background = _previewMode ? Brushes.Transparent : ThemeManager.SurfaceHoverBrush;
            PreviewTab.Background = _previewMode ? ThemeManager.SurfaceHoverBrush : Brushes.Transparent;
            SourceTabText.Foreground = _previewMode ? ThemeManager.SecondaryTextBrush : ThemeManager.TextBrush;
            PreviewTabText.Foreground = _previewMode ? ThemeManager.TextBrush : ThemeManager.SecondaryTextBrush;
        }

        /// <summary>The segment's two labels off the localization table (spec
        /// 6.2; all three dictionaries carry the keys). Painted on every
        /// Refresh, so a language switch follows even outside the editor.</summary>
        private void PaintSegmentLabels()
        {
            SourceTabText.Text = Localization.Get("UI_NoteSourceTab");
            PreviewTabText.Text = Localization.Get("UI_NotePreviewTab");
        }

        /// <summary>
        /// Paints the preview from the BOX text - the single source of both
        /// halves: the T12 renderer's AST, the live theme's style and the
        /// callbacks routing the task boxes back into the box (spec 6.2).
        /// </summary>
        private void RenderEditorPreview()
        {
            _previewBox.Document = NoteMarkdownRenderer.Build(
                MarkdownParser.Parse(_editingBox.Text ?? string.Empty),
                BuildRenderStyle(), PreviewCallbacks());
        }

        /// <summary>The renderer's outbound actions for the editor preview.</summary>
        private NoteRenderCallbacks PreviewCallbacks()
        {
            return new NoteRenderCallbacks
            {
                ToggleTaskLine = TogglePreviewTask,
                OpenLink = OpenLink
            };
        }

        /// <summary>
        /// A task box was clicked in the preview (spec 6.2/5.3): the source line
        /// flips in the BOX text (recomputed from it, per the design's note) and
        /// the preview repaints from the fresh text. The note's data stays
        /// untouched - the commit is what lands the edit.
        /// </summary>
        private void TogglePreviewTask(int line)
        {
            _editingBox.Text = NoteOps.ToggleTaskAt(_editingBox.Text, line);
            RenderEditorPreview();
        }

        /// <summary>
        /// The list's blank-area menu (spec 6.3): the one create entry, which
        /// runs the ＋ row's flow. Built per right-click so it always reflects
        /// the current language (the todo day-area pattern); the cards carry
        /// their own row menus and take precedence.
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

        #endregion

        #region Editor quick-insert menu (T15)

        /// <summary>
        /// The source box asks for its menu: the editing state refills the
        /// quick-insert menu the box already carries, every other state leaves
        /// the box's own menu alone (the list's blank-area pattern, spec 6.4).
        /// </summary>
        private void EditingBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (_state != State.Editing)
            {
                _editingBox.ContextMenu = null;
                return;
            }

            var menu = _editingBox.ContextMenu;
            if (menu == null)
            {
                // The box carries one from the moment the editor opened
                // (OpenEditor); only a menu cleared since then lands here.
                menu = new ContextMenu();
                _editingBox.ContextMenu = menu;
            }
            FillEditorMenu(menu);
        }

        /// <summary>
        /// The editor's right-click quick-insert menu (spec 6.4), freshly built.
        /// The box carries one from the moment the editor opens (see
        /// <see cref="OpenEditor"/>) and
        /// <see cref="EditingBox_ContextMenuOpening"/> refills it in place on
        /// every opening.
        /// </summary>
        private ContextMenu BuildEditorMenu()
        {
            var menu = new ContextMenu();
            FillEditorMenu(menu);
            return menu;
        }

        /// <summary>
        /// Fills <paramref name="menu"/> with the editor's entries: the standard
        /// four clipboard commands (headers localized, the behaviour is WPF's
        /// own), a separator, the 标题 and 格式 submenus, then the seven flat
        /// inserts. Every insert entry runs one <see cref="NoteEditorOps"/> op
        /// over the box, the placeholder words come from the localization table,
        /// and the widget colour wash comes from the shared
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
                CommandTarget = _editingBox
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
        /// Runs one insert op over the source box (spec 6.4): the op reads the
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
            string text = _editingBox.Text ?? string.Empty;
            var result = op(text, _editingBox.SelectionStart, _editingBox.SelectionLength);

            _editingBox.BeginChange();
            try
            {
                _editingBox.SelectionStart = 0;
                _editingBox.SelectionLength = text.Length;
                _editingBox.SelectedText = result.Item1;
            }
            finally
            {
                _editingBox.EndChange();
            }

            int start = Math.Min(Math.Max(result.Item2, 0), _editingBox.Text.Length);
            int length = Math.Min(Math.Max(result.Item3, 0), _editingBox.Text.Length - start);
            _editingBox.SelectionStart = start;
            _editingBox.SelectionLength = length;
            _editingBox.Focus();
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
