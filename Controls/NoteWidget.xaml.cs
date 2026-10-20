using System;
using System.Windows.Controls;
using System.Windows.Input;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.Controls
{
    /// <summary>
    /// The note widget: shared chrome + the note list view. The view is a
    /// renderer over the pure Note* logic and persists its edits through the
    /// base's OnDataChanged path. The shipped workbench is a home list ⇄
    /// detail page pair: the home's cards, the detail's rendered document
    /// (with its task boxes), the click-to-edit block splice, the title
    /// dropdown and the editor's insert menu all live in
    /// <see cref="NotePanelView"/> and its one surface.
    /// </summary>
    public partial class NoteWidget : WidgetWindowBase
    {
        /// <summary>Kind minimum (spec 6.5): chrome header + one card.</summary>
        private const double MIN_WIDTH = 260;
        private const double MIN_HEIGHT = 220;

        /// <summary>Content block of the default note panel (spec 6.5).</summary>
        private const double CONTENT_WIDTH = 300;
        private const double CONTENT_HEIGHT = 380;

        private readonly NotePanelView _view;

        /// <summary>The widget's data (the base keeps its own copy private);
        /// the chrome title falls back to its name before the view is bound.</summary>
        private readonly NoteData _noteData;

        public NoteWidget(NoteData data) : base(data)
        {
            InitializeComponent();
            // Before AttachChrome: its UpdateUI runs RefreshChromeTitle, which
            // reads the name off this field while the view is still unbound.
            _noteData = data;
            AttachChrome(NoteChrome);

            _view = new NotePanelView();
            NoteChrome.ContentHost.Content = _view;
            _view.Attach(data, this);

            // The title chevron (§3.2): only the note kind enables it, and only
            // the chevron opens the dropdown - the title text stays the drag
            // area of the header.
            NoteChrome.TitleMenuEnabled = true;
            NoteChrome.TitleMenuClicked += () => _view.ToggleTitleMenu(NoteChrome.TitleMenuAnchor);

            // The pending edit settles before the window goes (spec 5.2): a
            // closed/collapsed panel must never lose half-typed text. The
            // flush's own change notification only arms the debounce, so the
            // close path lands a synchronous save as well - on a Windows
            // session end the config is already closing and a debounced write
            // would be dropped, and on tray exit the process may go before the
            // timer fires. An open title dropdown never outlives the panel.
            Closing += (s, e) =>
            {
                _view?.CloseTitleMenu();
                _view?.FlushPendingEdit();
                WidgetManager.SaveConfigIfLoaded();
            };
        }

        /// <summary>The note list hosted in the chrome's content slot.</summary>
        public NotePanelView PanelView => _view;

        /// <summary>
        /// Esc closes an open title dropdown before any other key work: the
        /// popup is a separate focus scope that never takes focus, so this
        /// window-level tunnelling handler is the reliable path - it fires no
        /// matter which element holds focus (the chevron, the header, a text
        /// box), and it stops the panel's own Esc router from navigating home
        /// behind the open menu. Ctrl+Z in the list context walks the undo stack
        /// back a step (the newest delete); a focused text box keeps its own
        /// text undo (mirroring <see cref="TodoWidget"/>).
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _view != null && _view.IsTitleMenuOpen)
            {
                _view.CloseTitleMenu();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control) return;
            if (Keyboard.FocusedElement is TextBox) return;
            if (_view != null && _view.TryUndo()) e.Handled = true;
        }

        /// <summary>
        /// The legacy path of <see cref="WidgetWindowBase.GetPanelSize"/> adds
        /// the 40 DIP chrome header to this block; a widget with persisted
        /// sizes never reads it (the migration supplies the kind default).
        /// </summary>
        protected override (double width, double height) GetContentSize()
        {
            return (CONTENT_WIDTH, CONTENT_HEIGHT);
        }

        /// <summary>
        /// The panel is collapsing: an open title dropdown closes and the
        /// pending edit is settled (spec 5.2 - 收起面板不丢字).
        /// <see cref="WidgetWindowBase.HidePanel"/> is not
        /// virtual and its fade hides the window only once the animation
        /// completes, so the flush rides this hook, which the base calls
        /// synchronously at the top of the collapse, before the fade.
        /// </summary>
        protected override void OnPanelHidden()
        {
            _view?.CloseTitleMenu();
            _view?.FlushPendingEdit();
            base.OnPanelHidden();
        }

        /// <summary>
        /// Pushes the current state's title onto the shared chrome (§3.2): the
        /// widget name at home, the note's own title (or the untitled key) in
        /// the detail, with its full text as the detail tooltip. Home clears
        /// the tooltip so the widget's own tooltip semantics are untouched.
        /// Only the chrome changes - the view is never called back, so the
        /// navigation paths may call this freely.
        /// </summary>
        internal void RefreshChromeTitle()
        {
            if (Chrome == null) return;

            string title = _view != null ? _view.ChromeTitle : _noteData == null ? null : _noteData.Name;
            Chrome.Title = title ?? string.Empty;
            Chrome.TitleText.ToolTip = _view == null || _view.IsHome ? null : title;
        }

        /// <summary>The note panel's minimum (spec 6.5): the base's defaults are folder/todo only.</summary>
        protected override (double width, double height) GetMinPanelSize()
        {
            return (MIN_WIDTH, MIN_HEIGHT);
        }

        public override void UpdateUI()
        {
            base.UpdateUI();
            _view?.Refresh();
            RefreshChromeTitle();
        }

        public override void ShowWidgetMenu(Action onClosed = null)
        {
            WidgetMenuBuilder.Build(this, onClosed).IsOpen = true;
        }
    }
}
