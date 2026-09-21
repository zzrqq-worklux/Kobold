using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kobold.Core;

namespace Kobold.FolderListingCheck
{
    /// <summary>
    /// Checks for FolderListing (ordering, filtering, truncation, error tolerance)
    /// and BrowsePath (the path menu's level labels). Pure logic, no UI.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/FolderListingCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            _root = Path.Combine(Path.GetTempPath(), "kobold-folderlisting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            try
            {
                DirectoriesComeFirstRegardlessOfName();
                NaturalSortOrdersNumbers();
                HiddenEntriesAreFiltered();
                SystemEntriesAreFiltered();
                TruncationKeepsDirectoriesAndCountsAll();
                EmptyDirectoryIsNotAFailure();
                MissingDirectoryFails();
                NullOrEmptyInputFails();
                NameIsTheRawFileName();
                BrowsePathLabelsEveryLevel();
            }
            finally
            {
                TryDelete(_root);
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL FOLDER-LISTING CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        private static string NewDir(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DirectoriesComeFirstRegardlessOfName()
        {
            string dir = NewDir("case-dirs-first");
            Directory.CreateDirectory(Path.Combine(dir, "zfolder"));
            File.WriteAllText(Path.Combine(dir, "afile.txt"), "");

            var result = FolderListing.ListChildren(dir, 100);

            Check(!result.Failed && result.Entries.Count == 2, "dirs-first: two entries returned");
            if (result.Entries.Count < 2)
            {
                Errors.Add("dirs-first: not enough entries to check the ordering");
                return;
            }

            Check(result.Entries[0].Name == "zfolder" && result.Entries[0].IsDirectory,
                "dirs-first: directory sorts before a file that precedes it alphabetically");
            Check(result.Entries[1].Name == "afile.txt" && !result.Entries[1].IsDirectory,
                "dirs-first: file follows");
        }

        private static void NaturalSortOrdersNumbers()
        {
            string dir = NewDir("case-natural-sort");
            File.WriteAllText(Path.Combine(dir, "file10.txt"), "");
            File.WriteAllText(Path.Combine(dir, "file2.txt"), "");
            File.WriteAllText(Path.Combine(dir, "file1.txt"), "");

            var names = FolderListing.ListChildren(dir, 100).Entries.Select(e => e.Name).ToList();

            Check(names.SequenceEqual(new[] { "file1.txt", "file2.txt", "file10.txt" }),
                "natural sort: file1 < file2 < file10 (got " + string.Join(",", names) + ")");
        }

        private static void HiddenEntriesAreFiltered()
        {
            string dir = NewDir("case-hidden");
            string visible = Path.Combine(dir, "visible.txt");
            string hidden = Path.Combine(dir, "hidden.txt");
            File.WriteAllText(visible, "");
            File.WriteAllText(hidden, "");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var names = FolderListing.ListChildren(dir, 100).Entries.Select(e => e.Name).ToList();

            Check(names.Count == 1 && names[0] == "visible.txt", "hidden entries are filtered out");
        }

        private static void SystemEntriesAreFiltered()
        {
            string dir = NewDir("case-system");
            string system = Path.Combine(dir, "system.txt");
            File.WriteAllText(Path.Combine(dir, "normal.txt"), "");
            File.WriteAllText(system, "");
            File.SetAttributes(system, FileAttributes.System);

            var names = FolderListing.ListChildren(dir, 100).Entries.Select(e => e.Name).ToList();

            Check(names.Count == 1 && names[0] == "normal.txt", "system entries are filtered out");
        }

        private static void TruncationKeepsDirectoriesAndCountsAll()
        {
            string dir = NewDir("case-truncation");
            for (int i = 0; i < 4; i++) Directory.CreateDirectory(Path.Combine(dir, "dir" + i));
            for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(dir, "file" + i + ".txt"), "");

            var result = FolderListing.ListChildren(dir, 5);

            Check(result.Entries.Count == 5, "truncation: returns exactly maxEntries entries");
            Check(result.TotalCount == 8, "truncation: TotalCount is the real total (got " + result.TotalCount + ")");
            Check(result.Truncated, "truncation: Truncated is true");
            if (result.Entries.Count < 5)
            {
                Errors.Add("truncation: not enough entries to check which ones survived the cut");
                return;
            }

            Check(result.Entries.Take(4).All(e => e.IsDirectory),
                "truncation: all four directories survive the cut");
            Check(!result.Entries[4].IsDirectory, "truncation: the fifth slot is a file");
        }

        private static void EmptyDirectoryIsNotAFailure()
        {
            string dir = NewDir("case-empty");

            var result = FolderListing.ListChildren(dir, 100);

            Check(!result.Failed && result.Entries.Count == 0 && result.TotalCount == 0 && !result.Truncated,
                "empty directory: no entries, no failure, no truncation");
        }

        private static void MissingDirectoryFails()
        {
            var result = FolderListing.ListChildren(Path.Combine(_root, "does-not-exist"), 100);

            Check(result.Failed && result.Entries.Count == 0, "missing directory: Failed = true");
        }

        private static void NullOrEmptyInputFails()
        {
            Check(FolderListing.ListChildren(null, 100).Failed, "null directory: Failed = true");
            Check(FolderListing.ListChildren("   ", 100).Failed, "blank directory: Failed = true");
        }

        private static void NameIsTheRawFileName()
        {
            string dir = NewDir("case-raw-name");
            File.WriteAllText(Path.Combine(dir, "shortcut.lnk"), "");

            var result = FolderListing.ListChildren(dir, 100);
            if (result.Entries.Count != 1)
            {
                Errors.Add("raw name: expected exactly one entry, got " + result.Entries.Count);
                return;
            }

            Check(result.Entries[0].Name == "shortcut.lnk", "Name keeps the raw file name (.lnk not stripped here)");
        }

        private static void BrowsePathLabelsEveryLevel()
        {
            string dir = NewDir("case-browse-path");
            string deep = Path.Combine(dir, "level2");
            Directory.CreateDirectory(deep);

            Check(BrowsePath.Label(dir) == "case-browse-path",
                "browse-path: a normal directory is labelled with its own name");
            Check(BrowsePath.Label(dir + Path.DirectorySeparatorChar) == "case-browse-path",
                "browse-path: a trailing separator does not blank the label");
            Check(BrowsePath.Label(@"C:\") == @"C:\",
                "browse-path: a drive root falls back to the full path");

            var stack = new List<string> { dir, deep };
            var ancestry = BrowsePath.Ancestry("WORKING", stack);

            Check(BrowsePath.Ancestry("WORKING", new List<string>()).Count == 1,
                "browse-path: with an empty stack only the root row exists");

            Check(BrowsePath.MenuHeader("my_project") == "my__project",
                "browse-path: menu headers escape underscores so names are not mangled");
            Check(BrowsePath.MenuHeader("plain") == "plain" && BrowsePath.MenuHeader(null) == null,
                "browse-path: headers without underscores pass through unchanged");
            Check(ancestry.Count == 3, "browse-path: ancestry is the widget root plus every browsed level");
            if (ancestry.Count < 3)
            {
                Errors.Add("browse-path: not enough rows to check the labels");
                return;
            }

            Check(ancestry[0] == "WORKING", "browse-path: the first row is the widget root label");
            Check(ancestry[1] == "case-browse-path" && ancestry[2] == "level2",
                "browse-path: the remaining rows follow the stack top-down");
        }

        private static void TryDelete(string path)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
