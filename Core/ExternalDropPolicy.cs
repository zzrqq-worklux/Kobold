using System;

namespace Kobold.Core
{
    /// <summary>
    /// The browse-mode verdict for an external FileDrop: accept/copy or refuse.
    /// Root-mode drops are handled by the existing root path and never call this.
    /// </summary>
    public static class ExternalDropPolicy
    {
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
