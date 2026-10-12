using System;
using System.Windows.Input;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.Controls
{
    /// <summary>
    /// The todo widget: shared chrome + the calendar/day-list content view.
    /// The view is a renderer over the pure Todo* logic; all item/child edits
    /// go through it and are persisted via the base's OnDataChanged path,
    /// while selected-day changes use the lighter debounced save only.
    /// </summary>
    public partial class TodoWidget : WidgetWindowBase
    {
        /// <summary>Content width of the finished todo panel (calendar + day list).</summary>
        private const double CONTENT_WIDTH = 340;

        private readonly TodoCalendarView _view;

        public TodoWidget(TodoData data) : base(data)
        {
            InitializeComponent();
            AttachChrome(TodoChrome);

            _view = new TodoCalendarView();
            TodoChrome.ContentHost.Content = _view;
            _view.Attach(data, this);
        }

        /// <summary>The calendar + day list hosted in the chrome's content slot.</summary>
        public TodoCalendarView CalendarView => _view;

        /// <summary>
        /// Every click first passes the view: a click outside the focused
        /// inline editor blurs it, and the deferred blur commit auto-saves.
        /// </summary>
        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _view?.HandleMouseDownOutsideEditor(e.OriginalSource);
        }

        protected override (double width, double height) GetContentSize()
        {
            return (CONTENT_WIDTH, _view?.DesiredHeight ?? 0);
        }

        /// <summary>
        /// A resize move changed the panel size: re-adapt the view's layout
        /// (calendar cells, day labels, list rows) to the live height without
        /// rebuilding the day list. The view never resizes the panel back -
        /// the user's size is fixed (Task A4.1).
        /// </summary>
        protected override void OnPanelSizeChanged()
        {
            _view?.ApplyResizeLayout();
        }

        /// <summary>
        /// Remembers the selected day through the debounced save only - a
        /// selection change must not force an island rebuild (same path as
        /// the base's panel-state persistence).
        /// </summary>
        public void PersistSelection()
        {
            WidgetManager.Instance.SaveConfig();
        }

        public override void UpdateUI()
        {
            base.UpdateUI();
            _view?.Refresh();
        }

        public override void ShowWidgetMenu(Action onClosed = null)
        {
            WidgetMenuBuilder.Build(this, WidgetKindRegistry.Find(Data.Kind), onClosed).IsOpen = true;
        }
    }
}
