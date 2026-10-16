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
    /// base's OnDataChanged path. Task T10 builds the shell; T11 renders the
    /// list state (cards, the modified-time order, the row delete and its undo
    /// stack) - the reading/editing states and the creation flows arrive with
    /// T13/T14/T15.
    /// </summary>
    public partial class NoteWidget : WidgetWindowBase
    {
        /// <summary>Kind minimum (spec 6.5): chrome header + one card + the add row.</summary>
        private const double MIN_WIDTH = 260;
        private const double MIN_HEIGHT = 220;

        /// <summary>Content block of the default note panel (spec 6.5).</summary>
        private const double CONTENT_WIDTH = 300;
        private const double CONTENT_HEIGHT = 380;

        private readonly NotePanelView _view;

        public NoteWidget(NoteData data) : base(data)
        {
            InitializeComponent();
            AttachChrome(NoteChrome);

            _view = new NotePanelView();
            NoteChrome.ContentHost.Content = _view;
            _view.Attach(data, this);
        }

        /// <summary>The note list hosted in the chrome's content slot.</summary>
        public NotePanelView PanelView => _view;

        /// <summary>
        /// Ctrl+Z in the list context walks the undo stack back a step (the
        /// newest delete); a focused text box keeps its own text undo
        /// (mirroring <see cref="TodoWidget"/>).
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
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

        /// <summary>The note panel's minimum (spec 6.5): the base's defaults are folder/todo only.</summary>
        protected override (double width, double height) GetMinPanelSize()
        {
            return (MIN_WIDTH, MIN_HEIGHT);
        }

        public override void UpdateUI()
        {
            base.UpdateUI();
            _view?.Refresh();
        }

        public override void ShowWidgetMenu(Action onClosed = null)
        {
            WidgetMenuBuilder.Build(this, onClosed).IsOpen = true;
        }
    }
}
