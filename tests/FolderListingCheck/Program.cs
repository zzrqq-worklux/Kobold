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
                BrowseStackNormalization();
                BrowseMoveFilters();
                SameEntriesComparison();
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

        private static void BrowseStackNormalization()
        {
            string level1 = NewDir("case-stack-l1");
            string level2 = Path.Combine(level1, "l2");
            string level3 = Path.Combine(level2, "l3");
            Directory.CreateDirectory(level2);
            Directory.CreateDirectory(level3);
            string file = Path.Combine(level1, "file.txt");
            File.WriteAllText(file, "");

            Check(BrowsePath.NormalizeStack(new List<string> { level1, level2, level3 }, Directory.Exists).Count == 3,
                "stack: a fully valid stack is kept as-is");

            var kept = BrowsePath.NormalizeStack(new List<string> { level1, level2, Path.Combine(level2, "gone") }, Directory.Exists);
            Check(kept.Count == 2 && kept[1] == level2,
                "stack: a missing last level is dropped (the panel reopens one level up)");

            kept = BrowsePath.NormalizeStack(new List<string> { level1, Path.Combine(level1, "gone"), level3 }, Directory.Exists);
            Check(kept.Count == 1 && kept[0] == level1,
                "stack: everything after the first missing level is dropped");

            Check(BrowsePath.NormalizeStack(new List<string> { Path.Combine(_root, "gone") }, Directory.Exists).Count == 0,
                "stack: a stack with nothing valid falls back to the widget root");
            Check(BrowsePath.NormalizeStack(new List<string>(), Directory.Exists).Count == 0,
                "stack: an empty stack stays empty");
            Check(BrowsePath.NormalizeStack(null, Directory.Exists).Count == 0,
                "stack: a null stack stays empty");
            Check(BrowsePath.NormalizeStack(new List<string> { level1, file }, Directory.Exists).Count == 1,
                "stack: a file is not a browsable level");
            Check(BrowsePath.NormalizeStack(new List<string> { level1, "   " }, Directory.Exists).Count == 1,
                "stack: a blank level ends the valid prefix");
        }

        private static void BrowseMoveFilters()
        {
            string source = NewDir("case-move-source");
            string target = NewDir("case-move-target");
            string file = Path.Combine(source, "a.txt");
            File.WriteAllText(file, "");
            string folder = Path.Combine(source, "sub");
            Directory.CreateDirectory(folder);

            Func<string, bool> exists = p => File.Exists(p) || Directory.Exists(p);

            Check(BrowseMove.MovableInto(new[] { file, folder }, target, exists).Count == 2,
                "browse-move: existing files and folders move");

            string already = Path.Combine(target, "there.txt");
            File.WriteAllText(already, "");
            Check(BrowseMove.MovableInto(new[] { already }, target, exists).Count == 0,
                "browse-move: an item already in the target folder is skipped");
            Check(BrowseMove.MovableInto(new[] { already }, target + Path.DirectorySeparatorChar, exists).Count == 0,
                "browse-move: a trailing separator on the target still matches");
            Check(BrowseMove.MovableInto(new[] { already }, target.ToUpperInvariant(), exists).Count == 0,
                "browse-move: the folder comparison ignores case");

            Check(BrowseMove.MovableInto(new[] { Path.Combine(source, "gone.txt") }, target, exists).Count == 0,
                "browse-move: a path that does not exist is skipped");
            Check(BrowseMove.MovableInto(new[] { file, "   ", null }, target, exists).Count == 1,
                "browse-move: blank entries are skipped");
            Check(BrowseMove.MovableInto(null, target, exists).Count == 0,
                "browse-move: a null list moves nothing");
        }

        private static void SameEntriesComparison()
        {
            string a = NewDir("case-same-a");
            string b = NewDir("case-same-b");
            File.WriteAllText(Path.Combine(a, "one.txt"), "");
            Directory.CreateDirectory(Path.Combine(a, "sub"));
            File.WriteAllText(Path.Combine(b, "one.txt"), "");
            Directory.CreateDirectory(Path.Combine(b, "sub"));

            var left = FolderListing.ListChildren(a, 100);
            var right = FolderListing.ListChildren(b, 100);

            // The browser only ever compares two reads of the same folder, so the
            // full path is part of the identity: same names in another folder are
            // not the same listing.
            Check(FolderListing.SameEntries(left, FolderListing.ListChildren(a, 100)),
                "same-entries: a re-read of the same folder matches");
            Check(!FolderListing.SameEntries(left, right),
                "same-entries: listings of different folders never match");
            Check(FolderListing.SameEntries(left, left), "same-entries: a listing matches itself");
            Check(!FolderListing.SameEntries(null, left) && !FolderListing.SameEntries(left, null),
                "same-entries: null never matches a listing");
            Check(FolderListing.SameEntries(null, null), "same-entries: two nulls match (nothing to redraw)");

            File.WriteAllText(Path.Combine(b, "two.txt"), "");
            Check(!FolderListing.SameEntries(left, FolderListing.ListChildren(b, 100)),
                "same-entries: an added file is a difference");

            var reordered = FolderListing.ListChildren(a, 100);
            reordered.Entries.Reverse();
            Check(!FolderListing.SameEntries(left, reordered), "same-entries: order matters");

            var caseChanged = FolderListing.ListChildren(a, 100);
            caseChanged.Entries[0].Path = caseChanged.Entries[0].Path.ToUpperInvariant();
            Check(FolderListing.SameEntries(left, caseChanged),
                "same-entries: paths compare case-insensitively (Windows)");

            var kindChanged = FolderListing.ListChildren(a, 100);
            kindChanged.Entries[0].IsDirectory = !kindChanged.Entries[0].IsDirectory;
            Check(!FolderListing.SameEntries(left, kindChanged), "same-entries: a changed kind is a difference");

            var unreadable = FolderListing.ListChildren(Path.Combine(_root, "does-not-exist"), 100);
            Check(!FolderListing.SameEntries(left, unreadable),
                "same-entries: an unreadable folder never matches a listing");

            Check(!FolderListing.SameEntries(left, FolderListing.ListChildren(a, 1)),
                "same-entries: a different total count is a difference");
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
