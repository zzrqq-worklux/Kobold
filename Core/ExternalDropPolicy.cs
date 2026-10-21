using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>The internal drag formats that make a FileDrop payload an in-app drag, not an external one.</summary>
    [Flags]
    public enum InternalDragFormats
    {
        None = 0,
        KoboldItem = 1,
        KoboldItems = 2,
        KoboldSourceFolderId = 4,
        KoboldWidgetItems = 8,
        BrowseSource = 16,
        BrowsePaths = 32
    }

    /// <summary>
    /// The browse-mode verdict for an external FileDrop: accept/copy or refuse.
    /// Root-mode drops are handled by the existing root path and never call this.
    /// </summary>
    public static class ExternalDropPolicy
    {
        // The published drag format names. The UI sets and reads these, so the
        // mapping from a name to its marker lives here and nowhere else.
        public const string KoboldItemFormat = "KoboldItem";
        public const string KoboldItemsFormat = "KoboldItems";
        public const string KoboldSourceFolderFormat = "KoboldSourceFolderId";
        public const string KoboldWidgetItemsFormat = "KoboldWidgetItems";
        public const string BrowseSourceFormat = "KoboldBrowseSource";
        public const string BrowsePathsFormat = "KoboldBrowsePaths";

        /// <summary>Maps the published drag format names to their in-app marker flags.</summary>
        public static InternalDragFormats FromFormats(IEnumerable<string> formats)
        {
            InternalDragFormats flags = InternalDragFormats.None;
            if (formats == null) return flags;

            foreach (string format in formats)
            {
                if (string.Equals(format, KoboldItemFormat, StringComparison.Ordinal)) flags |= InternalDragFormats.KoboldItem;
                else if (string.Equals(format, KoboldItemsFormat, StringComparison.Ordinal)) flags |= InternalDragFormats.KoboldItems;
                else if (string.Equals(format, KoboldSourceFolderFormat, StringComparison.Ordinal)) flags |= InternalDragFormats.KoboldSourceFolderId;
                else if (string.Equals(format, KoboldWidgetItemsFormat, StringComparison.Ordinal)) flags |= InternalDragFormats.KoboldWidgetItems;
                else if (string.Equals(format, BrowseSourceFormat, StringComparison.Ordinal)) flags |= InternalDragFormats.BrowseSource;
                else if (string.Equals(format, BrowsePathsFormat, StringComparison.Ordinal)) flags |= InternalDragFormats.BrowsePaths;
            }
            return flags;
        }

        /// <summary>True only for a real external FileDrop: FileDrop present, no in-app drag marker.</summary>
        public static bool IsExternalFileDrop(bool hasFileDrop, InternalDragFormats internalFormats)
        {
            return hasFileDrop && internalFormats == InternalDragFormats.None;
        }

        /// <summary>
        /// True when a browse-mode external FileDrop should be accepted.
        /// File rows refuse (only folder rows and the background accept);
        /// a locked widget refuses everything.
        /// </summary>
        public static bool Accepts(bool browsing, bool locked, bool hasFileDrop, bool overFileRow)
        {
            return browsing && !locked && hasFileDrop && !overFileRow;
        }

        /// <summary>
        /// The folder a browse-mode external drop copies into: the folder row
        /// under the pointer when there is one, otherwise the current folder.
        /// </summary>
        public static string TargetDirectory(bool overFolderRow, string folderRowPath, string currentBrowsePath)
        {
            return overFolderRow && !string.IsNullOrEmpty(folderRowPath)
                ? folderRowPath
                : currentBrowsePath;
        }
    }
}
