using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Kobold.Core.FileSearch;
using Microsoft.Win32.SafeHandles;

namespace Kobold.FileSearchCheck
{
    /// <summary>
    /// Checks for the file-search core layer: settings defaults, path helpers,
    /// type filter, directory exclusions, index entries, root resolution, the
    /// SQLite index store (schema, scans, reconcile, query) and the directory
    /// scanner.
    /// No UI. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/FileSearchCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            // %TEMP% is a production default exclusion, so a tree created there would
            // be invisible to the scanner. The scratch tree lives in the repo instead,
            // under the git-ignored .superpowers directory.
            _root = Path.Combine(FindRepoRoot(), ".superpowers", "sdd", "file-search-check",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            try { RunAll(); }
            finally { try { Directory.Delete(_root, true); } catch { } }
            Console.WriteLine();
            if (Errors.Count == 0) { Console.WriteLine("ALL FILE-SEARCH CHECKS PASSED"); return 0; }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what); else Errors.Add(what);
        }

        /// <summary>
        /// The check project runs from tests/FileSearchCheck/bin/&lt;config&gt;/net48, so
        /// the repo root is the nearest ancestor that still carries Kobold.csproj.
        /// </summary>
        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Kobold.csproj")))
            {
                directory = directory.Parent;
            }
            if (directory == null)
            {
                throw new InvalidOperationException(
                    "Kobold.csproj was not found above " + AppDomain.CurrentDomain.BaseDirectory);
            }
            return directory.FullName;
        }

        private static void RunAll()
        {
            TypeFilterCases();
            ExcludeCases();
            UserExcludeCases();
            PathCases();
            SettingsDefaults();
            NormalizeCases();
            EntryCases();
            BadTimestampCases();
            RootCases();
            SearchDataModelCase();
            ConfigCases();
            StoreCases();
            StoreSuffixCases();
            StoreDeleteBoundaryCases();
            StoreConcurrencyCase();
            StoreSnapshotCase();
            ScannerCases();
        }

        private static void TypeFilterCases()
        {
            var s = FileSearchSettings.CreateDefault();
            Check(SearchTypeFilter.IsFileIncluded(@"C:\a\report.PDF", s), "type: default includes PDF case-insensitively");
            Check(!SearchTypeFilter.IsFileIncluded(@"C:\a\app.exe", s), "type: default excludes EXE");
            Check(SearchTypeFilter.IsFileIncluded(@"C:\a\README", s), "type: extensionless included by default");
            s.IndexExtensionless = false;
            Check(!SearchTypeFilter.IsFileIncluded(@"C:\a\README", s), "type: extensionless can be disabled");
            s = FileSearchSettings.CreateDefault();
            s.IndexAllFiles = true;
            Check(SearchTypeFilter.IsFileIncluded(@"C:\a\app.exe", s), "type: all-files includes EXE");
            Check(SearchTypeFilter.IsFileIncluded(@"C:\a\archive.tar.gz", s), "type: all-files includes multi-dot names");
            s.IndexAllFiles = false;
            Check(SearchTypeFilter.IsFileIncluded(@"C:\a\archive.tar.gz", s), "type: .tar.gz is classified by the last extension (gz)");
        }

        private static void ExcludeCases()
        {
            var s = FileSearchSettings.CreateDefault();
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Check(SearchDirectoryFilter.IsExcluded(Path.Combine(windows, "WinSxS", "x"), "x", s),
                "exclude: WinSxS full path is excluded");
            Check(SearchDirectoryFilter.IsExcluded(@"C:\p\node_modules\a", "a", s),
                "exclude: node_modules name is excluded at any depth");
            Check(SearchDirectoryFilter.IsExcluded(@"C:\p\.git\objects", "objects", s),
                "exclude: a child of .git is excluded by its parent path");
            Check(!SearchDirectoryFilter.IsExcluded(@"C:\p\src", "src", s),
                "exclude: normal source directory is not excluded");
            s.ExcludeDirectories = new[] { @"C:\p\private" };
            Check(SearchDirectoryFilter.IsExcluded(@"C:\p\private\a", "a", s),
                "exclude: user full path is excluded with boundary");
            Check(!SearchDirectoryFilter.IsExcluded(@"C:\p\privateer", "privateer", s),
                "exclude: user path does not match a sibling with the same prefix");
            s.ExcludeDirectories = new[] { "generated" };
            Check(SearchDirectoryFilter.IsExcluded(@"C:\p\x\generated", "generated", s),
                "exclude: user name is excluded");
        }

        private static void UserExcludeCases()
        {
            var s = FileSearchSettings.CreateDefault();
            s.ExcludeDirectories = new[] { "C:/p/private" };
            Check(SearchDirectoryFilter.IsExcluded(@"C:\p\private\a", "a", s),
                "exclude: a forward-slash user path excludes the same subtree");

            s.ExcludeDirectories = new[] { @"C:\p\.\private" };
            Check(SearchDirectoryFilter.IsExcluded(@"C:\p\private\a", "a", s),
                "exclude: a dot-segment user path excludes the same subtree");

            s.ExcludeDirectories = new[] { "generated\\" };
            Check(SearchDirectoryFilter.IsExcluded(@"C:\x\generated", "generated", s),
                "exclude: a trailing separator makes a bare name, not a path");

            s.ExcludeDirectories = new[] { "private" };
            Check(!SearchDirectoryFilter.IsExcluded(@"C:\p\privateer", "privateer", s),
                "exclude: a bare user name still respects the name boundary");

            s.ExcludeDirectories = new[] { @"C:\" };
            Check(SearchDirectoryFilter.IsExcluded(@"C:\p", "p", s),
                "exclude: a user drive root stays a path, not a name");
        }

        private static void PathCases()
        {
            Check(SearchPath.GetExtension(@"C:\a\report.PDF") == "pdf", "path: extension is lowercase without the dot");
            Check(SearchPath.GetExtension(@"C:\a\README") == "", "path: extensionless is empty");
            Check(SearchPath.GetExtension(@"C:\a\archive.tar.gz") == "gz", "path: last extension wins");
            Check(SearchPath.GetExtension(@"C:\a\.gitignore") == "", "path: a leading-dot name has no extension");
            Check(SearchPath.IsUnder(@"C:\p\a\b.txt", @"C:\p"), "path: IsUnder matches an ancestor");
            Check(!SearchPath.IsUnder(@"C:\privateer\a.txt", @"C:\p"), "path: IsUnder respects the separator boundary");
            Check(SearchPath.IsUnder(@"C:\p\a", @"C:\p\"), "path: IsUnder tolerates a trailing separator on the ancestor");
            string extended = SearchPath.ToExtended(@"C:\p\a.txt");
            Check(extended == @"\\?\C:\p\a.txt", "path: normal path gets the \\\\?\\ prefix");
            Check(SearchPath.FromExtended(extended) == @"C:\p\a.txt", "path: extended prefix is removed for storage");
            Check(SearchPath.ToExtended(@"\\server\share\a") == @"\\?\UNC\server\share\a", "path: UNC gets the UNC extended form");
            Check(SearchPath.FromExtended(@"\\?\UNC\server\share\a") == @"\\server\share\a", "path: UNC extended form goes back to a share path");
            Check(SearchPath.Normalize(@"C:\p\a\..\b\") == @"C:\p\b", "path: normalize resolves . and .. and trims the trailing separator");
            Check(SearchPath.EscapeLike(@"a%b_c\d") == @"a\%b\_c\\d", "path: LIKE wildcards and the escape character are escaped");
        }

        private static void SettingsDefaults()
        {
            var s = FileSearchSettings.CreateDefault();
            Check(s.Types != null && Array.IndexOf(s.Types, "pdf") >= 0 && Array.IndexOf(s.Types, "cs") >= 0,
                "settings: default types include the spec's document and code types");
            Check(s.IndexFolders && s.IndexExtensionless && !s.IndexAllFiles && s.ReconcileMinutes == 10,
                "settings: defaults are folders/extensionless on, all-files off, reconcile 10");
            string a = s.ComputeFingerprint(new[] { @"C:\" });
            s.Types = new[] { "pdf" };
            string b = s.ComputeFingerprint(new[] { @"C:\" });
            Check(a != b, "settings: fingerprint changes with types");
            s = FileSearchSettings.CreateDefault();
            string c = s.ComputeFingerprint(new[] { @"c:\", @"D:\" });
            string d = s.ComputeFingerprint(new[] { @"D:\", @"C:\" });
            Check(c == d, "settings: fingerprint does not depend on root order or case");
        }

        private static void NormalizeCases()
        {
            var s = FileSearchSettings.CreateDefault();
            s.Types = new[] { ".PDF", "pdf", " Cs ", "" };
            s.Roots = new[] { @"C:\p", @"c:\P", " " };
            s.ExcludeDirectories = new[] { "node_modules", "NODE_MODULES" };
            s.ReconcileMinutes = 9999;
            s.Normalize();
            Check(s.Types.Length == 2 && s.Types[0] == "pdf" && s.Types[1] == "cs",
                "settings: types are lowercased, dot-free and deduped");
            Check(s.Roots.Length == 1 && s.Roots[0] == @"C:\p" && s.ExcludeDirectories.Length == 1,
                "settings: roots and excludes are trimmed and deduped");
            Check(s.ReconcileMinutes == 1440, "settings: reconcile minutes are clamped to a day");

            var t = FileSearchSettings.CreateDefault();
            t.Types = new string[0];
            t.Normalize();
            Check(t.Types.Length == FileSearchSettings.DefaultTypes.Length,
                "settings: an empty type list falls back to the defaults");
        }

        private static void EntryCases()
        {
            string file = Path.Combine(_root, "Sample.PDF");
            File.WriteAllText(file, "abc");
            SearchEntry entry;
            Check(SearchEntry.TryCreate(_root, new FileInfo(file), out entry), "entry: a readable file creates an entry");
            if (entry != null)
            {
                Check(PathsEqual(entry.Path, file), "entry: the stored path is the plain path");
                Check(entry.Name == "Sample.PDF" && entry.NameLower == "sample.pdf",
                    "entry: name and lowercase name are filled");
                Check(entry.Extension == "pdf" && !entry.IsDirectory && entry.SizeBytes == 3,
                    "entry: extension, file flag and size are filled");
                Check(PathsEqual(entry.Root, _root), "entry: the root is stored as a plain path");
                Check(entry.ModifiedUtc > 1600000000L, "entry: modified time is unix seconds UTC");
            }

            string folder = Path.Combine(_root, "Folder");
            Directory.CreateDirectory(folder);
            Check(SearchEntry.TryCreate(_root, new DirectoryInfo(folder), out entry)
                && entry.IsDirectory && entry.SizeBytes == 0 && entry.Extension == "",
                "entry: a directory has no size and no extension");
            Check(!SearchEntry.TryCreate(_root, null, out entry) && entry == null, "entry: a null info is rejected");
            Check(!SearchEntry.TryCreate(_root, new FileInfo(Path.Combine(_root, "missing.txt")), out entry),
                "entry: a file that vanished is rejected");

            var extended = new FileInfo(SearchPath.ToExtended(file));
            Check(SearchEntry.TryCreate(SearchPath.ToExtended(_root), extended, out entry)
                && entry.Path == file && entry.Root == _root,
                "entry: extended paths are stored in their plain form");
        }

        private static void BadTimestampCases()
        {
            string file = Path.Combine(_root, "BadStamp.txt");
            File.WriteAllText(file, "x");

            // NTFS stores any FILETIME; one just past DateTime.MaxValue makes the
            // runtime throw ArgumentOutOfRangeException when the entry is read.
            if (!TryPoisonTimestamp(file))
            {
                Console.WriteLine("SKIP entry: this volume refused an out-of-range timestamp");
                return;
            }

            bool readThrows = false;
            try
            {
                _ = new FileInfo(file).LastWriteTimeUtc;
            }
            catch (ArgumentOutOfRangeException)
            {
                readThrows = true;
            }
            if (!readThrows)
            {
                Console.WriteLine("SKIP entry: this runtime tolerates an out-of-range timestamp");
                return;
            }

            SearchEntry entry;
            bool created;
            try
            {
                created = SearchEntry.TryCreate(_root, new FileInfo(file), out entry);
            }
            catch (Exception ex)
            {
                Check(false, "entry: an out-of-range timestamp is rejected instead of thrown (" + ex.GetType().Name + ")");
                return;
            }
            Check(!created && entry == null, "entry: an out-of-range timestamp is rejected instead of thrown");
        }

        private static bool TryPoisonTimestamp(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    // One tick past DateTime.MaxValue expressed as a FILETIME.
                    long beyondMax = 2650467744000000001L;
                    return SetFileTime(stream.SafeFileHandle, IntPtr.Zero, IntPtr.Zero, ref beyondMax);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileTime(SafeFileHandle handle, IntPtr creationTime,
            IntPtr lastAccessTime, ref long lastWriteTime);

        private static void RootCases()
        {
            string tree = Path.Combine(_root, "roots");
            Directory.CreateDirectory(tree);

            var resolved = SearchRoots.Resolve(new[] { tree });
            Check(resolved.Count == 1 && PathsEqual(resolved[0], tree) && resolved[0].IndexOf(@"\\?\") < 0,
                "roots: a configured existing directory resolves to its plain path");

            var dropped = SearchRoots.Resolve(new[] { Path.Combine(tree, "missing"), tree });
            Check(dropped.Count == 1 && PathsEqual(dropped[0], tree), "roots: missing directories are dropped");

            var deduped = SearchRoots.Resolve(new[] { tree, tree.ToUpperInvariant() });
            Check(deduped.Count == 1, "roots: duplicates are removed case-insensitively");

            var fixedRoots = SearchRoots.FixedNtfsRoots();
            Check(fixedRoots.Count > 0, "roots: fixed NTFS roots are found on this machine");
            bool plainAndRooted = true;
            foreach (var r in fixedRoots)
            {
                if (r.IndexOf(@"\\?\") >= 0 || !Path.IsPathRooted(r)) plainAndRooted = false;
            }
            Check(plainAndRooted, "roots: fixed roots are plain rooted paths");
        }

        private static void SearchDataModelCase()
        {
            var data = new Kobold.Core.SearchData();
            Check(data.Kind == "search", "search data: the kind is search");
        }

        private static void ConfigCases()
        {
            string dir = Path.Combine(_root, "config");
            Directory.CreateDirectory(dir);
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{ ""FileSearch"": { ""Configured"": true, ""IndexAllFiles"": true,
                ""Types"": [""PDF"", "".CS""], ""ReconcileMinutes"": -5 } }");
            var cfg = Kobold.Core.AppConfig.Load(main, backup);
            Check(cfg.FileSearch != null && cfg.FileSearch.Configured && cfg.FileSearch.IndexAllFiles,
                "config: FileSearch round-trips its booleans");
            Check(cfg.FileSearch != null && Array.IndexOf(cfg.FileSearch.Types, "pdf") >= 0
                  && Array.IndexOf(cfg.FileSearch.Types, "cs") >= 0,
                "config: types are normalized to lowercase without dots");
            Check(cfg.FileSearch != null && cfg.FileSearch.ReconcileMinutes == 0,
                "config: negative reconcile minutes normalize to 0");

            File.WriteAllText(main, @"{ ""Language"": ""en"" }");
            cfg = Kobold.Core.AppConfig.Load(main, backup);
            Check(cfg.FileSearch == null, "config: a missing FileSearch stays null (unconfigured)");

            cfg.FileSearch = FileSearchSettings.CreateDefault();
            cfg.FileSearch.Configured = true;
            cfg.Save();
            var reloaded = Kobold.Core.AppConfig.Load(main, backup);
            Check(reloaded.FileSearch != null && reloaded.FileSearch.Configured,
                "config: a saved FileSearch reloads");
        }

        private static void StoreCases()
        {
            string db = Path.Combine(_root, "store.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.EnsureSchema();
                store.BeginFullScan();
                var rows = new List<SearchEntry>
                {
                    Entry(@"C:\p\Alpha.txt", "Alpha.txt", "txt", false, 10, @"C:\"),
                    Entry(@"C:\p\alphabet.md", "alphabet.md", "md", false, 20, @"C:\"),
                    Entry(@"C:\p\子目录", "子目录", "", true, 0, @"C:\"),
                    Entry(@"C:\p\sub\Beta.txt", "Beta.txt", "txt", false, 30, @"C:\"),
                };
                store.UpsertScanBatch(rows);
                store.CompleteFullScan(2, 1, 0, "fp");
                Check(store.ReadStatus().HasIndex, "store: full scan publishes an index");
                Check(store.ReadStatus().SkippedDirectories == 2 && store.ReadStatus().SettingsFingerprint == "fp"
                      && store.ReadStatus().SchemaVersion == 1 && store.ReadStatus().LastScanUtc != null,
                    "store: status exposes the scan counters, fingerprint, schema version and timestamp");
                Check(store.GetDatabaseSizeBytes() > 0, "store: the database size covers the main file");

                var q = store.Query("alpha", 300, CancellationToken.None);
                Check(q.TotalMatches == 2 && q.Items.Count == 2 && q.Items[0].Name == "Alpha.txt",
                    "store: name prefix ranks before name infix and is case-insensitive");
                Check(!q.LimitReached, "store: the reached flag stays false while everything fits");
                q = store.Query("beta", 300, CancellationToken.None);
                Check(q.Items.Count == 1 && q.Items[0].Name == "Beta.txt", "store: path/name match works");
                q = store.Query("子", 300, CancellationToken.None);
                Check(q.Items.Count == 1 && q.Items[0].IsDirectory, "store: Chinese names and folders are searchable");
                q = store.Query("%", 300, CancellationToken.None);
                Check(q.TotalMatches == 0, "store: LIKE wildcards are escaped");
                q = store.Query("a", 1, CancellationToken.None);
                Check(q.Items.Count == 1 && q.LimitReached, "store: the 300/limit cap is reported");
                q = store.Query("   ", 300, CancellationToken.None);
                Check(q.Items.Count == 0 && q.TotalMatches == 0, "store: a blank query returns an empty result");

                store.Delete(@"c:\P\ALPHABET.MD");
                Check(store.Query("alpha", 300, CancellationToken.None).TotalMatches == 1,
                    "store: deleting one path keeps its siblings");
                store.DeletePathAndPrefix(@"C:\p\sub");
                Check(store.Query("beta", 300, CancellationToken.None).TotalMatches == 0,
                    "store: deleting a directory removes its prefix subtree");
                store.Checkpoint();
                store.Vacuum();
            }

            string db2 = Path.Combine(_root, "cancel.db");
            using (var store = new SearchIndexStore(db2))
            {
                Check(!store.ReadStatus().HasIndex, "store: a missing database reports no index");
                Check(store.Query("a", 300, CancellationToken.None).TotalMatches == 0,
                    "store: querying before the schema exists returns nothing");
                store.EnsureSchema();
                store.BeginFullScan();
                store.UpsertScanBatch(new[] { Entry(@"C:\old\a.txt", "a.txt", "txt", false, 1, @"C:\") });
                store.CompleteFullScan(0, 0, 0, "old");
                store.BeginFullScan();
                store.UpsertScanBatch(new[] { Entry(@"C:\new\b.txt", "b.txt", "txt", false, 1, @"C:\") });
                store.AbortFullScan();
                Check(store.Query("a", 300, CancellationToken.None).TotalMatches == 1,
                    "store: aborting a full scan keeps the old index intact");
            }

            string db3 = Path.Combine(_root, "reconcile.db");
            using (var store = new SearchIndexStore(db3))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                store.UpsertScanBatch(new[]
                {
                    Entry(@"C:\r\keep.txt", "keep.txt", "txt", false, 1, @"C:\r"),
                    Entry(@"C:\r\gone.txt", "gone.txt", "txt", false, 1, @"C:\r"),
                    Entry(@"C:\other\x.txt", "x.txt", "txt", false, 1, @"C:\other"),
                });
                store.CompleteFullScan(0, 0, 0, "fp");

                store.BeginReconcile();
                store.MarkSeenBatch(new[] { @"C:\r\keep.txt", @"C:\r\new.txt" });
                store.UpsertScanBatch(new[] { Entry(@"C:\r\new.txt", "new.txt", "txt", false, 1, @"C:\r") });
                store.CompleteReconcile(new[] { @"C:\r" });
                Check(store.Query("gone", 300, CancellationToken.None).TotalMatches == 0,
                    "store: reconcile drops rows that were not seen under its roots");
                Check(store.Query("keep", 300, CancellationToken.None).TotalMatches == 1
                      && store.Query("new", 300, CancellationToken.None).TotalMatches == 1,
                    "store: reconcile keeps seen and freshly written rows");
                Check(store.Query("x.txt", 300, CancellationToken.None).TotalMatches == 1,
                    "store: reconcile leaves roots it was not given alone");
                Check(store.ReadStatus().LastReconcileUtc != null, "store: reconcile writes its timestamp");

                store.BeginReconcile();
                store.MarkSeenBatch(new[] { @"C:\r\keep.txt" });
                store.AbortReconcile();
                store.BeginReconcile();
                store.MarkSeenBatch(new[] { @"C:\r\keep.txt", @"C:\r\new.txt" });
                store.CompleteReconcile(new[] { @"C:\r" });
                Check(store.Query("keep", 300, CancellationToken.None).TotalMatches == 1,
                    "store: a reconcile can start again after an abort");
            }
        }

        private static void StoreSuffixCases()
        {
            string db = Path.Combine(_root, "suffix.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                store.UpsertScanBatch(new[]
                {
                    Entry(@"C:\p\Alpha.txt", "Alpha.txt", "txt", false, 10, @"C:\"),
                    Entry(@"C:\p\BetaAlpha.txt", "BetaAlpha.txt", "txt", false, 20, @"C:\"),
                    Entry(@"C:\Projects\Alpha\report.txt", "report.txt", "txt", false, 30, @"C:\"),
                });
                store.CompleteFullScan(0, 0, 0, "fp");

                var q = store.Query("alpha", 300, CancellationToken.None);
                Check(q.TotalMatches == 3 && q.Items.Count == 3
                      && q.Items[0].Name == "Alpha.txt"
                      && q.Items[1].Name == "BetaAlpha.txt"
                      && q.Items[2].Name == "report.txt",
                    "store: name prefix, then name infix, then path-only match");
                Check(!q.LimitReached, "store: the suffix phase reports no truncation when it fits");

                q = store.Query("alpha", 2, CancellationToken.None);
                Check(q.TotalMatches == 3 && q.Items.Count == 2 && q.LimitReached
                      && q.Items[0].Name == "Alpha.txt" && q.Items[1].Name == "BetaAlpha.txt",
                    "store: the suffix phase fills the remaining limit after the prefix rows");
            }
        }

        private static void StoreDeleteBoundaryCases()
        {
            string db = Path.Combine(_root, "boundary.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                store.UpsertScanBatch(new[]
                {
                    Entry(@"C:\p\sub", "sub", "", true, 0, @"C:\p"),
                    Entry(@"C:\p\sub\Beta.txt", "Beta.txt", "txt", false, 10, @"C:\p"),
                    Entry(@"C:\p\submarine\Gamma.txt", "Gamma.txt", "txt", false, 20, @"C:\p"),
                    Entry(@"C:\Root.txt", "Root.txt", "txt", false, 30, @"C:\"),
                    Entry(@"C:\x\y.txt", "y.txt", "txt", false, 40, @"C:\"),
                    Entry(@"D:\other.txt", "other.txt", "txt", false, 50, @"D:\"),
                });
                store.CompleteFullScan(0, 0, 0, "fp");

                store.DeletePathAndPrefix(@"C:\p\sub\");
                var subRows = store.Query("sub", 300, CancellationToken.None);
                bool directoryRowGone = subRows.TotalMatches == 1;
                foreach (var row in subRows.Items)
                {
                    if (row.IsDirectory) directoryRowGone = false;
                }
                Check(directoryRowGone,
                    "store: deleting a directory with a trailing separator removes the directory row");
                Check(store.Query("beta", 300, CancellationToken.None).TotalMatches == 0,
                    "store: deleting a directory with a trailing separator removes its children");
                Check(store.Query("gamma", 300, CancellationToken.None).TotalMatches == 1,
                    "store: deleting C:\\p\\sub leaves C:\\p\\submarine");
                Check(store.Query("submarine", 300, CancellationToken.None).TotalMatches == 1,
                    "store: a prefix delete does not claim a sibling with the same stem");

                store.DeletePathAndPrefix(@"C:\");
                Check(store.Query("root", 300, CancellationToken.None).TotalMatches == 0
                      && store.Query("y.txt", 300, CancellationToken.None).TotalMatches == 0,
                    "store: deleting the drive root removes the whole drive");
                Check(store.Query("other", 300, CancellationToken.None).TotalMatches == 1,
                    "store: deleting one drive root leaves other drives alone");
            }
        }

        private static void StoreSnapshotCase()
        {
            string db = Path.Combine(_root, "snapshot.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                PublishSnapGeneration(store, 200);

                Exception failure = null;
                var writer = new Thread(() =>
                {
                    try
                    {
                        for (int round = 0; round < 8; round++)
                        {
                            PublishSnapGeneration(store, round % 2 == 0 ? 300 : 200);
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });

                int probes = 0;
                writer.Start();
                try
                {
                    do
                    {
                        var result = store.Query("snap", 1000, CancellationToken.None);
                        if (result.TotalMatches != result.Items.Count
                            || (result.TotalMatches != 200 && result.TotalMatches != 300))
                        {
                            failure = new InvalidOperationException(
                                "count and rows came from different scan generations ("
                                + result.TotalMatches + "/" + result.Items.Count + ")");
                        }
                        probes++;
                    }
                    while (writer.IsAlive);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                writer.Join();

                Check(failure == null && probes > 0,
                    "store: one query sees one snapshot while a full scan swaps the table"
                    + (failure == null ? string.Empty : " (" + failure.Message + ")"));
            }
        }

        /// <summary>
        /// End-to-end scanner checks against a real tree in the repo scratch root:
        /// default type/name filtering, a denied directory, an injected reparse
        /// point, deterministic cancellation and the reconcile/subtree write paths.
        /// </summary>
        private static void ScannerCases()
        {
            string tree = Path.Combine(_root, "tree");
            string docs = Path.Combine(tree, "docs");
            string empty = Path.Combine(tree, "empty");
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(Path.Combine(tree, "node_modules", "pkg"));
            Directory.CreateDirectory(empty);
            File.WriteAllText(Path.Combine(tree, "README"), "x");
            File.WriteAllText(Path.Combine(docs, "report.PDF"), "x");
            File.WriteAllText(Path.Combine(tree, "node_modules", "pkg", "skip.js"), "x");
            File.WriteAllText(Path.Combine(tree, "app.exe"), "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            string fingerprint = settings.ComputeFingerprint(new[] { tree });

            string db = Path.Combine(_root, "scan.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();

                var progress = new List<ScanProgress>();
                var scanner = new DirectoryScanner(store, settings, progress.Add);
                store.BeginFullScan();
                var full = scanner.Scan(new[] { tree }, ScanMode.Full, CancellationToken.None);
                store.CompleteFullScan(full.SkippedDirectories, full.SkippedReparsePoints,
                    full.SkippedLongPaths, fingerprint);
                Check(full.Files == 2 && !full.Cancelled, "scan: default types index README + PDF, skip EXE");
                Check(full.Directories == 3 && full.SkippedDirectories == 1,
                    "scan: excluded node_modules is skipped and counted");
                Check(full.SkippedReparsePoints == 0 && full.SkippedLongPaths == 0,
                    "scan: a clean tree reports no skipped reparse points or long paths");
                Check(store.Query("skip", 300, CancellationToken.None).TotalMatches == 0,
                    "scan: node_modules is not indexed");
                Check(store.Query("report", 300, CancellationToken.None).TotalMatches == 1,
                    "scan: nested file is indexed");
                Check(store.Query("app", 300, CancellationToken.None).TotalMatches == 0,
                    "scan: a file type outside the list is not indexed");
                Check(progress.Count >= 1 && progress[progress.Count - 1].Files == full.Files
                      && progress[progress.Count - 1].Directories == full.Directories
                      && progress[progress.Count - 1].SkippedEntries == 0,
                    "scan: the final progress snapshot carries the result counters and no skipped entries");

                var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { docs };
                var guarded = new DirectoryScanner(store, settings, null, p => forbidden.Contains(p));
                store.BeginFullScan();
                var denied = guarded.Scan(new[] { tree }, ScanMode.Full, CancellationToken.None);
                store.CompleteFullScan(denied.SkippedDirectories, denied.SkippedReparsePoints,
                    denied.SkippedLongPaths, fingerprint);
                Check(denied.SkippedDirectories == 2 && denied.Files == 1,
                    "scan: a denied directory is skipped and counted");
                Check(store.Query("report", 300, CancellationToken.None).TotalMatches == 0,
                    "scan: files under a denied directory are not published");
                Check(store.Query("readme", 300, CancellationToken.None).TotalMatches == 1,
                    "scan: the rest of the tree is published when one directory is denied");

                var junction = new DirectoryScanner(store, settings, null, null, p => PathsEqual(p, empty));
                store.BeginFullScan();
                var linked = junction.Scan(new[] { tree }, ScanMode.Full, CancellationToken.None);
                store.AbortFullScan();
                Check(linked.SkippedReparsePoints == 1 && linked.Directories == 2,
                    "scan: a reparse point is skipped and counted");
                Check(store.Query("empty", 300, CancellationToken.None).TotalMatches == 1,
                    "scan: an aborted scan keeps the previously published rows");

                File.WriteAllText(Path.Combine(tree, "newfile.txt"), "x");
                var cts = new CancellationTokenSource();
                var cancelling = new DirectoryScanner(store, settings, null, p =>
                {
                    if (PathsEqual(p, docs)) cts.Cancel();
                    return false;
                });
                store.BeginFullScan();
                bool cancelled = false;
                try { cancelling.Scan(new[] { tree }, ScanMode.Full, cts.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                store.AbortFullScan();
                Check(cancelled, "scan: cancellation on a directory stops the scan");
                Check(store.Query("newfile", 300, CancellationToken.None).TotalMatches == 0
                      && store.Query("readme", 300, CancellationToken.None).TotalMatches == 1,
                    "scan: a cancelled scan leaves the published index intact");

                store.BeginFullScan();
                bool preCancelled = false;
                try { scanner.Scan(new[] { tree }, ScanMode.Full, new CancellationToken(true)); }
                catch (OperationCanceledException) { preCancelled = true; }
                store.AbortFullScan();
                Check(preCancelled, "scan: an already-cancelled token throws before walking");

                store.Upsert(Entry(Path.Combine(tree, "stale.txt"), "stale.txt", "txt", false, 1, tree));
                Check(store.Query("stale", 300, CancellationToken.None).TotalMatches == 1,
                    "reconcile: a live row can be seeded before the scan");

                store.BeginReconcile();
                var reconcile = scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                store.CompleteReconcile(new[] { tree });
                Check(reconcile.Files == 3, "reconcile: the scan records README, report.PDF and newfile.txt");
                Check(store.Query("stale", 300, CancellationToken.None).TotalMatches == 0,
                    "reconcile: rows the scan did not see are dropped");
                Check(store.Query("readme", 300, CancellationToken.None).TotalMatches == 1
                      && store.Query("report", 300, CancellationToken.None).TotalMatches == 1
                      && store.Query("newfile", 300, CancellationToken.None).TotalMatches == 1,
                    "reconcile: seen and freshly written rows survive");

                File.WriteAllText(Path.Combine(docs, "renamed.txt"), "x");
                var subtree = scanner.ScanSubtree(tree, docs, CancellationToken.None);
                Check(subtree.Files == 2 && subtree.Directories == 1,
                    "subtree: one directory is walked on its own");
                Check(store.Query("renamed", 300, CancellationToken.None).TotalMatches == 1,
                    "subtree: the new row is written live");
                Check(store.Query("readme", 300, CancellationToken.None).TotalMatches == 1
                      && store.ReadStatus().SettingsFingerprint == fingerprint,
                    "subtree: the published index is not swapped or cleared");
            }
        }

        private static void PublishSnapGeneration(SearchIndexStore store, int count)
        {
            store.BeginFullScan();
            var rows = new List<SearchEntry>(count);
            for (int i = 0; i < count; i++)
            {
                rows.Add(Entry(@"C:\s\snap" + i + ".txt", "snap" + i + ".txt", "txt", false, i, @"C:\s"));
            }
            store.UpsertScanBatch(rows);
            store.CompleteFullScan(0, 0, 0, "gen" + count);
        }

        private static void StoreConcurrencyCase()
        {
            string db = Path.Combine(_root, "concurrent.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                var rows = new List<SearchEntry>();
                for (int i = 0; i < 300; i++)
                {
                    rows.Add(Entry(@"C:\c\row" + i + ".txt", "row" + i + ".txt", "txt", false, i, @"C:\c"));
                }
                store.UpsertScanBatch(rows);
                store.CompleteFullScan(0, 0, 0, "fp");

                Exception failure = null;
                var writer = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 120; i++)
                        {
                            store.Upsert(Entry(@"C:\c\live" + i + ".txt", "live" + i + ".txt", "txt", false, i, @"C:\c"));
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });

                int probes = 0;
                writer.Start();
                try
                {
                    do
                    {
                        var result = store.Query("row", 10, CancellationToken.None);
                        if (result.TotalMatches < 300 || result.Items.Count != 10)
                        {
                            failure = new InvalidOperationException("a query saw an incomplete index during a write");
                        }
                        probes++;
                    }
                    while (writer.IsAlive);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                writer.Join();

                Check(failure == null && probes > 0,
                    "store: read-only queries run while another thread writes"
                    + (failure == null ? string.Empty : " (" + failure.GetType().Name + ")"));
            }
        }

        private static SearchEntry Entry(string path, string name, string extension, bool isDirectory,
            long size, string root)
        {
            return new SearchEntry
            {
                Path = path,
                Name = name,
                NameLower = name.ToLowerInvariant(),
                Extension = extension,
                IsDirectory = isDirectory,
                SizeBytes = size,
                ModifiedUtc = 1700000000,
                Root = root
            };
        }

        private static bool PathsEqual(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
