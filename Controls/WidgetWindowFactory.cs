using System;
using System.Collections.Generic;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// The kind-to-window registry: every widget kind registers its window
    /// constructor here, so adding a kind never edits the manager. A kind
    /// with no registration (or a null datum) has no window.
    /// </summary>
    public static class WidgetWindowFactory
    {
        private static readonly Dictionary<string, Func<WidgetData, IWidgetWindow>> Factories =
            new Dictionary<string, Func<WidgetData, IWidgetWindow>>(StringComparer.Ordinal)
        {
            { "folder", data => new FolderWidget((FolderData)data) },
            { "todo", data => new TodoWidget((TodoData)data) },
            { "note", data => new NoteWidget((NoteData)data) },
            { "search", data => new SearchWidget((SearchData)data) },
        };

        public static IWidgetWindow Create(WidgetData data)
        {
            if (data == null || string.IsNullOrEmpty(data.Kind)) return null;
            Func<WidgetData, IWidgetWindow> factory;
            return Factories.TryGetValue(data.Kind, out factory) ? factory(data) : null;
        }

        /// <summary>
        /// The shared window for a tab group. Groups are not a widget kind, so
        /// they are not in the registry; the manager creates them directly from
        /// the group state plus its ordered member data.
        /// </summary>
        public static IWidgetWindow CreateGroup(WidgetGroup group, IList<WidgetData> members)
            => new WidgetGroupWindow(group, members);
    }
}
