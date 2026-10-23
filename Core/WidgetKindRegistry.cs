using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// One widget kind: identity, localization/icon keys and the factory that
    /// builds its default data. Pure data, no UI.
    /// </summary>
    public sealed class WidgetKind
    {
        public string Id;
        public string NameKey;
        public string IconKey;
        public Func<WidgetData> CreateDefault;

        /// <summary>The concrete data type the converter instantiates for this kind.</summary>
        public Type DataType;
    }

    /// <summary>
    /// The widget kinds the app knows. Unknown ids simply have no entry.
    ///
    /// Adding a kind: (1) its data model in Core/, (2) an entry here
    /// (Id / NameKey / IconKey / DataType / CreateDefault), (3) its window
    /// factory in Controls/WidgetWindowFactory, (4) icon renderers mapped in
    /// IconRendererFactory.CreateByKey, (5) a default size in
    /// PanelSizeMigration, (6) an AppConfig validation/repair branch, (7) its
    /// window and view, (8) three-language keys in Localization, (9) checks +
    /// the Run-Checks matrix + README/CHANGELOG. The registry entry,
    /// WidgetKind.DataType and the factory registration absorb most of the
    /// wiring; what stays per-kind is "how much" (sizes, repair, UI), not "how".
    /// </summary>
    public static class WidgetKindRegistry
    {
        public static IReadOnlyList<WidgetKind> All { get; } = new List<WidgetKind>
        {
            new WidgetKind
            {
                Id = "folder",
                NameKey = "UI_DefaultFolderName",
                IconKey = "folder",
                CreateDefault = () => FolderData.Create(
                    Localization.Get("UI_DefaultFolderName"), UiTokens.DefaultFolderColor),
                DataType = typeof(FolderData),
            },
            new WidgetKind
            {
                Id = "todo",
                NameKey = "UI_DefaultTodoName",
                IconKey = "todo",
                CreateDefault = () => new TodoData(),
                DataType = typeof(TodoData),
            },
            new WidgetKind
            {
                Id = "note",
                NameKey = "UI_DefaultNoteName",
                IconKey = "note",
                CreateDefault = () => new NoteData(),
                DataType = typeof(NoteData),
            },
            new WidgetKind
            {
                Id = "search",
                NameKey = "UI_DefaultSearchName",
                IconKey = "search",
                CreateDefault = () => new SearchData(),
                DataType = typeof(SearchData),
            }
        }.AsReadOnly();

        public static WidgetKind Find(string id)
        {
            foreach (var kind in All)
            {
                if (string.Equals(kind.Id, id, StringComparison.Ordinal)) return kind;
            }
            return null;
        }
    }
}
