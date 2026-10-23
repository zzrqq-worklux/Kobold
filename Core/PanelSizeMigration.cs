using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// One-shot fill of the persisted panel sizes. The only place that turns
    /// the old per-widget settings (or a fresh install) into
    /// <see cref="WidgetData.PanelWidth"/>/<see cref="WidgetData.PanelHeight"/>:
    /// a missing field is computed, an existing one is never touched, and an
    /// unknown kind is left alone.
    /// </summary>
    public static class PanelSizeMigration
    {
        // The fixed default size of a note panel (spec §6.5).
        private const double NOTE_DEFAULT_WIDTH = 300;
        private const double NOTE_DEFAULT_HEIGHT = 380;

        // Task 8: the search panel's default size (box + status + results).
        private const double SEARCH_DEFAULT_WIDTH = 422;
        private const double SEARCH_DEFAULT_HEIGHT = 562;

        /// <summary>
        /// Fills the missing size fields of one widget. Folders replay the old
        /// grid settings through <see cref="PanelLayoutAdapter.MigrateFolderSize"/>;
        /// todos take the fixed default width and a height built from the
        /// selected day's week count and list rows.
        /// </summary>
        public static void FillMissingSizes(WidgetData widget, double itemScale, string language, DateTime today)
        {
            if (widget == null) return;

            if (widget is FolderData folder)
            {
                var (w, h) = PanelLayoutAdapter.MigrateFolderSize(
                    Math.Max(1, folder.GridColumns), folder.MaxPanelRows, folder.Items?.Count ?? 0, itemScale);
                if (folder.PanelWidth == null) folder.PanelWidth = w;
                if (folder.PanelHeight == null) folder.PanelHeight = h;
                return;
            }

            if (widget is TodoData todo)
            {
                DateTime date = (todo.SelectedDate ?? today).Date;
                var culture = TodoCalendar.CultureForLanguage(language ?? "en");
                int weekCount = TodoCalendar.WeekCount(date.Year, date.Month, culture.DateTimeFormat.FirstDayOfWeek);

                int rows = 0;
                foreach (var item in TodoListOps.ForDay(todo.Items ?? new List<TodoItem>(), date, today.Date))
                {
                    rows += 1 + (item.Children?.Count ?? 0);
                }

                var (w, h) = PanelLayoutAdapter.DefaultTodoSize(weekCount, rows);
                if (todo.PanelWidth == null) todo.PanelWidth = w;
                if (todo.PanelHeight == null) todo.PanelHeight = h;
            }

            if (widget is NoteData note)
            {
                if (note.PanelWidth == null) note.PanelWidth = NOTE_DEFAULT_WIDTH;
                if (note.PanelHeight == null) note.PanelHeight = NOTE_DEFAULT_HEIGHT;
            }

            if (widget is SearchData search)
            {
                if (search.PanelWidth == null) search.PanelWidth = SEARCH_DEFAULT_WIDTH;
                if (search.PanelHeight == null) search.PanelHeight = SEARCH_DEFAULT_HEIGHT;
            }
        }
    }
}
