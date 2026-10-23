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

        /// <summary>
        /// The config form of a user's checked root selection: exactly every
        /// fixed NTFS root collapses to the empty array (the config's
        /// "all fixed drives" form) and is then kept in sync with drives
        /// attached later. Anything else - a partial fixed selection, an extra
        /// directory, an empty enumeration of fixed roots - stays explicit.
        /// The onboarding and the Settings section share this rule so the two
        /// editors cannot drift apart.
        /// </summary>
        public static string[] ToConfiguredRoots(IEnumerable<string> selected)
        {
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (selected != null)
            {
                foreach (var raw in selected)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    string value = raw.Trim();
                    if (!seen.Add(RootKey(value))) continue;
                    values.Add(value);
                }
            }

            IReadOnlyList<string> fixedRoots = FixedNtfsRoots();
            // No fixed root is enumerable: never interpret an explicit
            // selection as the "all fixed roots" form.
            if (fixedRoots.Count == 0 || values.Count != fixedRoots.Count) return values.ToArray();

            var fixedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in fixedRoots) fixedKeys.Add(RootKey(root));

            foreach (var value in values)
            {
                if (!fixedKeys.Contains(RootKey(value))) return values.ToArray();
            }
            return new string[0];
        }

        private static string RootKey(string root)
        {
            return root.Trim().TrimEnd('\\').ToLowerInvariant();
        }
    }
}
