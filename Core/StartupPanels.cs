using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// Decides which widget panels a fresh launch reopens.
    /// </summary>
    public static class StartupPanels
    {
        /// <summary>
        /// The widgets whose panels were still open when the app last closed, in
        /// config order. A panel that auto-hid earlier is not in here: unpinned
        /// panels collapse when they lose focus, and at that moment they are
        /// closed as far as the user is concerned.
        /// </summary>
        public static List<FolderData> ToRestore(IEnumerable<FolderData> folders)
        {
            var restore = new List<FolderData>();
            if (folders == null) return restore;

            foreach (var folder in folders)
            {
                if (folder != null && folder.IsExpanded) restore.Add(folder);
            }
            return restore;
        }
    }
}
