using System;
using System.Collections.Generic;
using System.IO;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Turns the configured roots into the directories a scan actually walks.
    /// An empty configuration means "everything searchable": every fixed NTFS
    /// drive. An explicit configuration keeps only directories that exist.
    /// </summary>
    public static class SearchRoots
    {
        /// <summary>
        /// Plain, de-duplicated roots. Configured entries are normalized and must
        /// exist as directories; with no configured entries every fixed NTFS drive
        /// root is returned instead.
        /// </summary>
        public static IReadOnlyList<string> Resolve(string[] configured)
        {
            var roots = new List<string>();

            if (configured == null || configured.Length == 0) return FixedNtfsRoots();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < configured.Length; i++)
            {
                string raw = configured[i];
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string path = SearchPath.Normalize(raw);
                if (path.Length == 0) continue;

                // The extended form also covers roots deeper than MAX_PATH.
                if (!Directory.Exists(SearchPath.ToExtended(path))) continue;
                if (!seen.Add(path)) continue;

                roots.Add(path);
            }
            return roots;
        }

        /// <summary>
        /// Root directories of the fixed disks that are NTFS. Volatile or unreadable
        /// drives (USB sticks, denied media, a drive removed mid-enumeration) are
        /// skipped, never fatal.
        /// </summary>
        public static IReadOnlyList<string> FixedNtfsRoots()
        {
            var roots = new List<string>();

            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception)
            {
                // No drive enumeration available: report "no roots" instead of throwing.
                return roots;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in drives)
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed) continue;
                    if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;

                    string root = SearchPath.Normalize(drive.RootDirectory.FullName);
                    if (root.Length == 0 || !seen.Add(root)) continue;
                    roots.Add(root);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (ArgumentException)
                {
                }
            }
            return roots;
        }
    }
}
