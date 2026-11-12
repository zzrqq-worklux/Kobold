using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// Maps a widget datum to its content view. Every tab-capable kind has a
    /// real view; an unknown kind (and a null datum) has none.
    /// </summary>
    internal static class WidgetViewFactory
    {
        public static IWidgetContentView Create(WidgetData data)
        {
            if (data is TodoData) return new TodoCalendarView();
            if (data is NoteData) return new NotePanelView();
            if (data is SearchData) return new SearchPanelView();
            if (data is FolderData) return new FolderPanelView();
            return null;
        }
    }
}
