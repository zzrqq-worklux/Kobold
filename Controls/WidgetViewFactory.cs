using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// Maps a widget datum to its content view. Folder joins in T3a; every
    /// other kind (and a null datum) has no view yet.
    /// </summary>
    internal static class WidgetViewFactory
    {
        public static IWidgetContentView Create(WidgetData data)
        {
            if (data is TodoData) return new TodoCalendarView();
            if (data is NoteData) return new NotePanelView();
            if (data is SearchData) return new SearchPanelView();
            return null;
        }
    }
}
