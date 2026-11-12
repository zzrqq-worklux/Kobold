using System.Windows;
using System.Windows.Controls;
using Kobold.Controls;
using Kobold.Core;

namespace Kobold.GroupUiSpike
{
    /// <summary>
    /// Everything the group window needs from the harness: the two member
    /// datasets plus the content box it starts with. The window owns the hidden
    /// host widgets and the real views, mirroring what T1/T2 will do.
    /// </summary>
    internal sealed class SpikeInput
    {
        public TodoData Todo;
        public NoteData Note;
        public double ContentWidth = 758;
        public double ContentHeight = 466;
    }

    /// <summary>
    /// The artifact contract the --check harness asserts against. It exists in
    /// the RED state too, so the checks compile and fail at runtime with
    /// "group window missing" instead of a compile error. The group window is
    /// discovered by name through <c>GroupSpikeWindow.Create(SpikeInput)</c>.
    /// </summary>
    internal interface ISpikeGroupWindow
    {
        Window Window { get; }

        TodoWidget TodoHost { get; }
        NoteWidget NoteHost { get; }
        TodoData TodoData { get; }
        NoteData NoteData { get; }
        TodoCalendarView TodoView { get; }
        NotePanelView NoteView { get; }

        FrameworkElement TabRow { get; }
        FrameworkElement TitleRow { get; }
        FrameworkElement ContentArea { get; }
        FrameworkElement TodoTab { get; }
        FrameworkElement NoteTab { get; }
        Border TodoUnderline { get; }
        Border NoteUnderline { get; }
        ContentControl TodoSlot { get; }
        ContentControl NoteSlot { get; }
        TextBlock TitleText { get; }

        bool IsPinned { get; }
        bool IsLocked { get; }
        bool IsActiveTodo { get; }

        /// <summary>PreviewKeyDown / PreviewMouseLeftButtonDown deliveries the
        /// window observed (proves the routing handlers are wired).</summary>
        int KeyEventsSeen { get; }
        int MouseEventsSeen { get; }

        void SetActive(int index);
        void RefreshTitle();
        void ApplyContentSize();
        bool TryResize(double width, double height);
        void ToggleTopmost();
        void TogglePin();
        void ToggleLock();
    }
}
