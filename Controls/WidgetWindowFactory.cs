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
        private static readonly Dictionary<string, Func<WidgetData, IWidgetContentView, IWidgetWindow>> Factories =
            new Dictionary<string, Func<WidgetData, IWidgetContentView, IWidgetWindow>>(StringComparer.Ordinal)
        {
            { "folder", (data, contents) => { RejectContents(contents, "folder"); return new FolderWidget((FolderData)data); } },
            { "todo", (data, contents) => new TodoWidget((TodoData)data, contents) },
            { "note", (data, contents) => new NoteWidget((NoteData)data, contents) },
            { "search", (data, contents) => new SearchWidget((SearchData)data, contents) },
        };

        public static IWidgetWindow Create(WidgetData data)
        {
            return Create(data, null);
        }

        /// <summary>
        /// Builds the kind's window around an existing live view, or a fresh
        /// view when <paramref name="contents"/> is null. The todo/note/search
        /// kinds adopt through their constructor overload; a kind that cannot
        /// host a released view (folder, currently) rejects it.
        /// </summary>
        internal static IWidgetWindow Create(WidgetData data, IWidgetContentView contents)
        {
            if (data == null || string.IsNullOrEmpty(data.Kind)) return null;

            Func<WidgetData, IWidgetContentView, IWidgetWindow> factory;
            if (!Factories.TryGetValue(data.Kind, out factory))
            {
                // An unknown kind has no window; it must not silently drop a
                // live view either.
                RejectContents(contents, data.Kind);
                return null;
            }
            return factory(data, contents);
        }

        /// <summary>
        /// A non-null live view can only move into a kind whose window knows
        /// how to adopt it; every other kind is a programming error.
        /// </summary>
        private static void RejectContents(IWidgetContentView contents, string kind)
        {
            if (contents == null) return;
            throw new ArgumentException(
                "The '" + kind + "' kind cannot adopt a released content view.", nameof(contents));
        }

        /// <summary>
        /// The shared window for a tab group. Groups are not a widget kind, so
        /// they are not in the registry; the manager creates them directly from
        /// the group state plus its ordered member data.
        /// </summary>
        public static IWidgetWindow CreateGroup(WidgetGroup group, IList<WidgetData> members)
            => CreateGroup(group, members, null);

        /// <summary>
        /// The group window around optional live member views: a non-null
        /// entry is adopted through <see cref="IWidgetContentView.RebindHost"/>;
        /// a null entry builds the member's fresh view. Internal because
        /// <see cref="IWidgetContentView"/> is internal (the same reason the
        /// per-kind adoption overload is internal).
        /// </summary>
        internal static IWidgetWindow CreateGroup(
            WidgetGroup group, IList<WidgetData> members, IList<IWidgetContentView> contents = null)
            => new WidgetGroupWindow(group, members, contents);
    }
}
