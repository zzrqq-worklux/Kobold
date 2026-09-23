using System;
using System.Collections.Generic;
using System.IO;

namespace Kobold.Core
{
    /// <summary>
    /// Pure helpers for dragging entries from one browsed folder into another.
    /// The actual move is done by the shell (Services/ShellFileOperations);
    /// this only decides which paths are worth moving.
    /// </summary>
    public static class BrowseMove
    {
        /// <summary>
        /// The paths a browse drag would actually move into targetFolder:
        /// existing items whose parent folder is not already the target.
        /// </summary>
        public static List<string> MovableInto(IEnumerable<string> paths, string targetFolder, Func<string, bool> exists)
        {
            var movable = new List<string>();
            if (paths == null) return movable;

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (exists != null && !exists(path)) continue;
                if (SameFolder(ParentOf(path), targetFolder)) continue;
                movable.Add(path);
            }
            return movable;
        }

        /// <summary>Case-insensitive folder comparison that tolerates trailing separators.</summary>
        public static bool SameFolder(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

            try
            {
                return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ParentOf(string path)
        {
            try { return Path.GetDirectoryName(path); }
            catch (Exception) { return null; }
        }

        private static string Normalize(string folder)
        {
            return Path.GetFullPath(folder).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
