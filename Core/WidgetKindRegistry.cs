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
