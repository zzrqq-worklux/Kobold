using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// Pure helpers for widget item collections. Kept free of UI and storage
    /// dependencies so the cross-widget move rules can be unit-checked by
    /// tests/WidgetItemsCheck (usage: Controls/FolderWidget.DragDrop.cs).
    /// </summary>
    public static class WidgetItems
    {
        /// <summary>
        /// Creates an independent copy. Used to snapshot the selection at drag
        /// start so the drop target never aliases items still owned by the source.
        /// </summary>
        public static WidgetItem Clone(WidgetItem source)
        {
            if (source == null) return null;

            return new WidgetItem
            {
                Name = source.Name,
                Path = source.Path,
                IsReference = source.IsReference,
                OriginalPath = source.OriginalPath
            };
        }

        /// <summary>
        /// Appends the incoming items that are not already in the target
        /// (paths compared case-insensitively) and returns how many were added.
        /// </summary>
        public static int MergeInto(IList<WidgetItem> target, IEnumerable<WidgetItem> incoming)
        {
            if (target == null || incoming == null) return 0;

            int added = 0;
            foreach (var item in incoming)
            {
                if (item == null || string.IsNullOrEmpty(item.Path)) continue;
                if (ContainsPath(target, item.Path)) continue;

                target.Add(item);
                added++;
            }
            return added;
        }

        private static bool ContainsPath(IList<WidgetItem> items, string path)
        {
            foreach (var item in items)
            {
                if (item != null && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
