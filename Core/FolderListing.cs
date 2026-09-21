using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Kobold.Core
{
    /// <summary>
    /// One child of a browsed directory.
    /// </summary>
    public sealed class BrowseEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsDirectory { get; set; }
    }

    /// <summary>
    /// Result of reading a directory for the in-panel browser.
    /// </summary>
    public sealed class ListingResult
    {
        public List<BrowseEntry> Entries { get; set; }
        public int TotalCount { get; set; }
        public bool Truncated { get; set; }
        public bool Failed { get; set; }

        public ListingResult()
        {
            Entries = new List<BrowseEntry>();
        }
    }

    /// <summary>
    /// Pure helpers for the in-panel folder browser. Kept free of UI and storage
    /// dependencies so tests/FolderListingCheck can cover the rules.
    /// </summary>
    public static class FolderListing
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string a, string b);

        private static bool? _naturalSortAvailable;

        /// <summary>
        /// Reads one directory for the browser. Directories come first, each
        /// group sorted with the same natural order Explorer uses, hidden and
        /// system entries are skipped, and the result is cut at maxEntries
        /// (directories survive the cut because they sort first).
        /// </summary>
        public static ListingResult ListChildren(string directory, int maxEntries)
        {
            var result = new ListingResult();

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                result.Failed = true;
                return result;
            }

            List<BrowseEntry> dirs;
            List<BrowseEntry> files;
            try
            {
                dirs = ReadEntries(Directory.EnumerateDirectories(directory), true);
                files = ReadEntries(Directory.EnumerateFiles(directory), false);
            }
            catch (Exception)
            {
                // UnauthorizedAccessException / IOException / DirectoryNotFoundException:
                // the caller shows the "cannot open" empty state.
                result.Failed = true;
                return result;
            }

            dirs.Sort(CompareEntries);
            files.Sort(CompareEntries);

            result.TotalCount = dirs.Count + files.Count;

            int limit = Math.Max(0, maxEntries);
            foreach (var entry in dirs)
            {
                if (result.Entries.Count >= limit) break;
                result.Entries.Add(entry);
            }
            foreach (var entry in files)
            {
                if (result.Entries.Count >= limit) break;
                result.Entries.Add(entry);
            }

            result.Truncated = result.TotalCount > result.Entries.Count;
            return result;
        }

        private static List<BrowseEntry> ReadEntries(IEnumerable<string> paths, bool isDirectory)
        {
            var entries = new List<BrowseEntry>();
            foreach (var path in paths)
            {
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                    string name = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(name)) continue;

                    entries.Add(new BrowseEntry
                    {
                        Name = name,
                        Path = path,
                        IsDirectory = isDirectory
                    });
                }
                catch (Exception)
                {
                    // Unreadable entry: skip it, keep the rest of the listing.
                }
            }
            return entries;
        }

        private static int CompareEntries(BrowseEntry a, BrowseEntry b)
        {
            int cmp = CompareNames(a.Name, b.Name);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        }

        private static int CompareNames(string a, string b)
        {
            if (_naturalSortAvailable == null)
            {
                try
                {
                    StrCmpLogicalW("a", "b");
                    _naturalSortAvailable = true;
                }
                catch (Exception)
                {
                    _naturalSortAvailable = false;
                }
            }

            return _naturalSortAvailable.Value
                ? StrCmpLogicalW(a, b)
                : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
