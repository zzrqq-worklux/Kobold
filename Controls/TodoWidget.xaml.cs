using System;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.Controls
{
    /// <summary>
    /// The todo widget shell: shared chrome + an empty content host for now
    /// (the calendar and day list live in TodoCalendarView, Task 2.4). Its
    /// widget menu is the common kind-aware menu built by WidgetMenuBuilder.
    /// </summary>
    public partial class TodoWidget : WidgetWindowBase
    {
        /// <summary>Content width of the finished todo panel (calendar + day list).</summary>
        private const double CONTENT_WIDTH = 340;

        public TodoWidget(TodoData data) : base(data)
        {
            InitializeComponent();
            AttachChrome(TodoChrome);
        }

        protected override (double width, double height) GetContentSize()
        {
            // No content yet: the window is just the 40px chrome header until
            // Task 2.4 computes the calendar + list height.
            return (CONTENT_WIDTH, 0);
        }

        public override void ShowWidgetMenu(Action onClosed = null)
        {
            WidgetMenuBuilder.Build(this, WidgetKindRegistry.Find(Data.Kind), onClosed).IsOpen = true;
        }
    }
}
