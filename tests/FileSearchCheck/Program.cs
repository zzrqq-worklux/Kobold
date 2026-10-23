using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Kobold.Core;
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
            SearchDataCases();
            SettingsUiCases();
            ConfigCases();
            StoreCases();
            StoreSuffixCases();
            StoreDeleteBoundaryCases();
            StoreConcurrencyCase();
            StoreSnapshotCase();
            ScannerCases();
            ScannerSubtreeCases();
            ScannerBatchCases();
            WatcherCases();
            ServiceCases();
            ServiceIncrementalCases();
            ServiceFreshDatabaseCase();
            ServiceCorruptionCases();
            ServiceShutdownCase();
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

            var clone = s.Clone();
            if (clone.Types != null && clone.Types.Length > 0) clone.Types[0] = "zzz";
            Check(s.Types.Length > 0 && s.Types[0] != "zzz",
                "settings: clone copies the arrays instead of sharing them");
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

            // Task 9: the shared "checked roots -> config form" rule used by the
            // onboarding and the Settings section.
            Check(SearchRoots.ToConfiguredRoots(fixedRoots).Length == 0,
                "roots: every fixed root collapses to the all-fixed form");
            var explicitRoots = SearchRoots.ToConfiguredRoots(new[] { tree });
            Check(explicitRoots.Length == 1 && PathsEqual(explicitRoots[0], tree),
                "roots: an explicit selection stays explicit");
        }

        private static void SearchDataModelCase()
        {
            var data = new Kobold.Core.SearchData();
            Check(data.Kind == "search", "search data: the kind is search");
        }

        /// <summary>
        /// Task 8: the registered search kind drives the config round trip -
        /// SerializeObject/DeserializeObject keep "search" and the shared id.
        /// </summary>
        private static void SearchDataCases()
        {
            var data = new Kobold.Core.SearchData();
            Check(data.Kind == "search" && data.Id != null, "search data: kind and id are set");
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(data);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Kobold.Core.SearchData>(json);
            Check(back != null && back.Kind == "search", "search data: round-trips");
        }

        /// <summary>
        /// Task 9: a settings change that alters the index contents changes the
        /// fingerprint (which is what the service compares to flag NeedsRebuild).
        /// "All files" is one of them.
        /// </summary>
        private static void SettingsUiCases()
        {
            var s = FileSearchSettings.CreateDefault();
            s.Configured = true;
            s.Roots = new[] { @"C:\" };
            var f1 = s.ComputeFingerprint(new[] { @"C:\" });
            s.IndexAllFiles = true;
            var f2 = s.ComputeFingerprint(new[] { @"C:\" });
            Check(f1 != f2, "settings: all-files changes the fingerprint and marks rebuild");
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
                Check(full.SkippedEntries == 0, "scan: the result carries the skipped-entry count");
                Check(!HasRow(store, Path.Combine(tree, "node_modules", "pkg", "skip.js"), tree),
                    "scan: node_modules is not indexed");
                Check(HasRow(store, Path.Combine(docs, "report.PDF"), tree),
                    "scan: nested file is indexed under the scan root");
                Check(!HasRow(store, Path.Combine(tree, "app.exe"), tree),
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
                Check(!HasRow(store, Path.Combine(docs, "report.PDF"), tree),
                    "scan: files under a denied directory are not published");
                Check(HasRow(store, Path.Combine(tree, "README"), tree),
                    "scan: the rest of the tree is published when one directory is denied");

                var junction = new DirectoryScanner(store, settings, null, null, p => PathsEqual(p, empty));
                store.BeginFullScan();
                var linked = junction.Scan(new[] { tree }, ScanMode.Full, CancellationToken.None);
                store.AbortFullScan();
                Check(linked.SkippedReparsePoints == 1 && linked.Directories == 2,
                    "scan: a reparse point is skipped and counted");
                Check(HasRow(store, empty, tree),
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
                Check(!HasRow(store, Path.Combine(tree, "newfile.txt"), tree)
                      && HasRow(store, Path.Combine(tree, "README"), tree),
                    "scan: a cancelled scan leaves the published index intact");

                store.BeginFullScan();
                bool preCancelled = false;
                try { scanner.Scan(new[] { tree }, ScanMode.Full, new CancellationToken(true)); }
                catch (OperationCanceledException) { preCancelled = true; }
                store.AbortFullScan();
                Check(preCancelled, "scan: an already-cancelled token throws before walking");

                store.Upsert(Entry(Path.Combine(tree, "stale.txt"), "stale.txt", "txt", false, 1, tree));
                Check(HasRow(store, Path.Combine(tree, "stale.txt"), tree),
                    "reconcile: a live row can be seeded before the scan");

                store.BeginReconcile();
                var reconcile = scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                store.CompleteReconcile(new[] { tree });
                Check(reconcile.Files == 3, "reconcile: the scan records README, report.PDF and newfile.txt");
                Check(!HasRow(store, Path.Combine(tree, "stale.txt"), tree),
                    "reconcile: rows the scan did not see are dropped");
                Check(HasRow(store, Path.Combine(tree, "README"), tree)
                      && HasRow(store, Path.Combine(docs, "report.PDF"), tree)
                      && HasRow(store, Path.Combine(tree, "newfile.txt"), tree),
                    "reconcile: seen and freshly written rows survive");

                File.WriteAllText(Path.Combine(docs, "renamed.txt"), "x");
                var subtree = scanner.ScanSubtree(tree, docs, CancellationToken.None);
                Check(subtree.Files == 2 && subtree.Directories == 1,
                    "subtree: one directory is walked on its own");
                Check(HasRow(store, Path.Combine(docs, "renamed.txt"), tree),
                    "subtree: the new row is written live");
                Check(HasRow(store, Path.Combine(tree, "README"), tree)
                      && store.ReadStatus().SettingsFingerprint == fingerprint,
                    "subtree: the published index is not swapped or cleared");

                // Full mode stages into files_new, so a caller that forgot BeginFullScan
                // must be rejected before a single row can leak into the live table.
                string guardFile = Path.Combine(tree, "guard.txt");
                string keeper = Path.Combine(tree, "keeper.txt");
                File.WriteAllText(guardFile, "x");
                store.Upsert(Entry(keeper, "keeper.txt", "txt", false, 1, tree));
                bool withoutBegin = false;
                try { scanner.Scan(new[] { tree }, ScanMode.Full, CancellationToken.None); }
                catch (InvalidOperationException) { withoutBegin = true; }
                Check(withoutBegin, "scan: a full scan without BeginFullScan throws");
                Check(HasRow(store, keeper, tree) && !HasRow(store, guardFile, tree),
                    "scan: the rejected scan leaves the live index unchanged");
            }
        }

        /// <summary>
        /// A directory rename: the watcher deletes the old prefix and rescans the new
        /// directory. The start directory's own row must be written too, or the
        /// renamed folder stays unindexed until the next reconcile.
        /// </summary>
        private static void ScannerSubtreeCases()
        {
            string tree = Path.Combine(_root, "rename-tree");
            string oldDirectory = Path.Combine(tree, "old-name");
            string newDirectory = Path.Combine(tree, "new-name");
            Directory.CreateDirectory(oldDirectory);
            File.WriteAllText(Path.Combine(oldDirectory, "child.txt"), "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };

            string db = Path.Combine(_root, "rename.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                string fingerprint = settings.ComputeFingerprint(new[] { tree });
                store.BeginFullScan();
                var initial = new DirectoryScanner(store, settings).Scan(new[] { tree }, ScanMode.Full,
                    CancellationToken.None);
                store.CompleteFullScan(initial.SkippedDirectories, initial.SkippedReparsePoints,
                    initial.SkippedLongPaths, fingerprint);
                Check(HasRow(store, oldDirectory, tree), "subtree: the original directory row is indexed");

                Directory.Move(oldDirectory, newDirectory);
                store.DeletePathAndPrefix(oldDirectory);
                Check(!HasRow(store, oldDirectory, tree) && !HasRow(store, Path.Combine(newDirectory, "child.txt"), tree),
                    "subtree: the old prefix is gone before the rename repair");

                var subtree = new DirectoryScanner(store, settings).ScanSubtree(tree, newDirectory,
                    CancellationToken.None);
                Check(subtree.Files == 1 && subtree.Directories == 1,
                    "subtree: the renamed directory is walked");
                Check(HasRow(store, newDirectory, tree),
                    "subtree: the renamed directory row is written live");
                Check(HasRow(store, Path.Combine(newDirectory, "child.txt"), tree),
                    "subtree: the child file under the renamed directory is written live");
            }
        }

        /// <summary>
        /// More than one batch of rows: 810 files cross the 800-row flush inside the
        /// walk, so the reconcile path must mark every batch as seen or
        /// CompleteReconcile deletes the unmarked rows.
        /// </summary>
        private static void ScannerBatchCases()
        {
            const int FileCount = 810;

            string tree = Path.Combine(_root, "batch-tree");
            Directory.CreateDirectory(tree);
            for (int i = 0; i < FileCount; i++)
            {
                File.WriteAllText(Path.Combine(tree, "file" + i.ToString("D4") + ".txt"), "x");
            }

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };

            string db = Path.Combine(_root, "batch.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                string fingerprint = settings.ComputeFingerprint(new[] { tree });
                var scanner = new DirectoryScanner(store, settings);

                store.BeginFullScan();
                var full = scanner.Scan(new[] { tree }, ScanMode.Full, CancellationToken.None);
                store.CompleteFullScan(full.SkippedDirectories, full.SkippedReparsePoints,
                    full.SkippedLongPaths, fingerprint);
                Check(full.Files == FileCount && full.Directories == 1,
                    "batch: a full scan counts every file across two flushes");
                var all = store.Query("file", FileCount + 10, CancellationToken.None);
                Check(all.TotalMatches == FileCount && all.Items.Count == FileCount,
                    "batch: every flushed row of a full scan is published");

                store.BeginReconcile();
                var reconcile = scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                store.CompleteReconcile(new[] { tree });
                Check(reconcile.Files == FileCount, "reconcile: a batched scan counts every file");
                all = store.Query("file", FileCount + 10, CancellationToken.None);
                Check(all.TotalMatches == FileCount && all.Items.Count == FileCount,
                    "reconcile: every flushed batch is marked as seen and survives completion");
            }
        }

        /// <summary>
        /// The watcher host on a scratch tree: a created file is indexed, a rewrite
        /// updates its size, a renamed directory's subtree is re-indexed under the
        /// new name, and a delete removes the row. Roots that cannot be watched
        /// must degrade instead of throwing, and a healthy root keeps working in
        /// the same host as a failed one.
        /// </summary>
        private static void WatcherCases()
        {
            string root = Path.Combine(_root, "watch-root");
            Directory.CreateDirectory(root);
            string db = Path.Combine(_root, "watch.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                store.CompleteFullScan(0, 0, 0, "watch");
                var s = FileSearchSettings.CreateDefault();
                s.Roots = new[] { root };
                using (var host = new FileSystemWatcherHost(store, s, null))
                {
                    Check(host.Start(new[] { root }), "watch: a temp root starts");
                    Check(host.IncrementalAvailable, "watch: a healthy root reports incremental availability");

                    string created = Path.Combine(root, "zebrawatch-created.txt");
                    File.WriteAllText(created, "x");
                    Check(WaitUntil(() => HasRow(store, created, root), 5000),
                        "watch: created file appears in the index");

                    File.WriteAllText(created, new string('y', 4000));
                    Check(WaitUntil(() => RowSize(store, created) > 1000, 5000),
                        "watch: a changed file updates its indexed size");

                    string dir = Path.Combine(root, "zebrawatch-dir");
                    Directory.CreateDirectory(dir);
                    string inside = Path.Combine(dir, "zebrawatch-inside.txt");
                    File.WriteAllText(inside, "x");
                    string renamed = Path.Combine(root, "zebrawatch-renamed");
                    Directory.Move(dir, renamed);
                    string insideRenamed = Path.Combine(renamed, "zebrawatch-inside.txt");
                    Check(WaitUntil(() => HasRow(store, insideRenamed, root) && !HasRow(store, inside, root), 5000),
                        "watch: a renamed directory's subtree is re-indexed");

                    File.Delete(created);
                    Check(WaitUntil(() => !HasRow(store, created, root), 5000),
                        "watch: a deleted file disappears");
                }

                string missing = Path.Combine(_root, "zebrawatch-missing-" + Guid.NewGuid().ToString("N"));
                using (var host = new FileSystemWatcherHost(store, s, null))
                {
                    Check(!host.Start(new[] { missing }),
                        "watch: an invalid root reports failure instead of throwing");
                    Check(!host.IncrementalAvailable,
                        "watch: an invalid root degrades incremental updates");
                }

                using (var host = new FileSystemWatcherHost(store, s, null))
                {
                    string partial = Path.Combine(root, "zebrawatch-partial.txt");
                    Check(!host.Start(new[] { root, missing }),
                        "watch: one failed root reports a degraded host");
                    File.WriteAllText(partial, "x");
                    Check(WaitUntil(() => HasRow(store, partial, root), 5000),
                        "watch: a failed root does not stop the healthy roots");
                }

                // A renamed folder must still re-index its files when folder rows are
                // disabled: only the directory row is gated on IndexFolders, the files
                // must follow the rename.
                string noFoldersRoot = Path.Combine(_root, "watch-nofolders-root");
                Directory.CreateDirectory(noFoldersRoot);
                var noFolders = FileSearchSettings.CreateDefault();
                noFolders.Roots = new[] { noFoldersRoot };
                noFolders.IndexFolders = false;
                using (var host = new FileSystemWatcherHost(store, noFolders, null))
                {
                    Check(host.Start(new[] { noFoldersRoot }),
                        "watch: a no-folder root starts");

                    string oldDir = Path.Combine(noFoldersRoot, "nofolders-dir");
                    string oldChild = Path.Combine(oldDir, "nofolders-child.txt");
                    Directory.CreateDirectory(oldDir);
                    File.WriteAllText(oldChild, "x");
                    Check(WaitUntil(() => HasRow(store, oldChild, noFoldersRoot), 5000),
                        "watch: a file in a no-folder tree is indexed");

                    string renamedDir = Path.Combine(noFoldersRoot, "nofolders-renamed");
                    string renamedChild = Path.Combine(renamedDir, "nofolders-child.txt");
                    Directory.Move(oldDir, renamedDir);
                    Check(WaitUntil(() => HasRow(store, renamedChild, noFoldersRoot)
                                          && !HasRow(store, oldChild, noFoldersRoot), 5000),
                        "watch: a renamed folder's files are re-indexed with folder rows disabled");
                    Check(!HasRow(store, renamedDir, noFoldersRoot),
                        "watch: a renamed folder adds no directory row when folders are disabled");
                }

                // A same-volume move out of the watched tree must delete the old row
                // and must not index the destination under the watched root.
                string moveRoot = Path.Combine(_root, "watch-move-root");
                string moveOutside = Path.Combine(_root, "watch-move-outside");
                Directory.CreateDirectory(moveRoot);
                Directory.CreateDirectory(moveOutside);
                using (var host = new FileSystemWatcherHost(store, s, null))
                {
                    Check(host.Start(new[] { moveRoot }), "watch: a move root starts");

                    string moveSource = Path.Combine(moveRoot, "zebrawatch-move-me.txt");
                    string moveTarget = Path.Combine(moveOutside, "zebrawatch-move-me.txt");
                    File.WriteAllText(moveSource, "x");
                    Check(WaitUntil(() => HasRow(store, moveSource, moveRoot), 5000),
                        "watch: the move source is indexed");

                    File.Move(moveSource, moveTarget);
                    Check(WaitUntil(() => !HasRow(store, moveSource, moveRoot), 5000),
                        "watch: a move out of the root removes the old row");
                    Check(!HasPath(store, moveTarget),
                        "watch: a move out of the root does not index the outside path");
                }

                // A queue that is full while the worker is paused must report overflow
                // exactly once instead of blocking the watcher callback.
                string overflowRoot = Path.Combine(_root, "watch-overflow-root");
                Directory.CreateDirectory(overflowRoot);
                using (var host = new FileSystemWatcherHost(store, s, null, 1))
                {
                    int requested = 0;
                    host.ReconcileRequested += (sender, args) => Interlocked.Increment(ref requested);

                    // Pause before Start so the worker cannot drain a collection
                    // window while the two files are being written: the queue
                    // overflow is then deterministic, not a timing race.
                    host.PauseProcessing();
                    Check(host.Start(new[] { overflowRoot }), "watch: an overflow root starts");

                    string firstOverflow = Path.Combine(overflowRoot, "zebrawatch-overflow-1.txt");
                    string secondOverflow = Path.Combine(overflowRoot, "zebrawatch-overflow-2.txt");
                    File.WriteAllText(firstOverflow, "x");
                    File.WriteAllText(secondOverflow, "y");

                    Check(WaitUntil(() => Volatile.Read(ref requested) > 0, 5000),
                        "watch: a full queue requests a reconcile instead of blocking");

                    host.ResumeProcessing();
                    Check(WaitUntil(() => HasRow(store, firstOverflow, overflowRoot)
                                          || HasRow(store, secondOverflow, overflowRoot), 5000),
                        "watch: the paused queue drains after resume");
                }
            }
        }

        /// <summary>
        /// The service side of the watcher: Ready starts incremental updates, a
        /// reconcile requested through the same path the timer uses removes a row
        /// the watcher never saw while keeping the rows it did index, and pause /
        /// resume stops and restarts the watcher. An unwatchable root still scans
        /// but reports incremental updates unavailable.
        /// </summary>
        private static void ServiceIncrementalCases()
        {
            string db = Path.Combine(_root, "service-watch.db");
            string tree = Path.Combine(_root, "service-watch-tree");
            Directory.CreateDirectory(tree);
            File.WriteAllText(Path.Combine(tree, "zebrawatch-base.txt"), "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            settings.Configured = true;
            settings.Enabled = true;
            // The manual RequestReconcile below covers the reconcile path; the timer
            // itself would only fire after 30 seconds.
            settings.ReconcileMinutes = 0;

            using (var service = new FileSearchIndexService(db))
            {
                service.ApplySettings(settings);
                service.SyncWithWidgets(new WidgetData[] { new SearchData() });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.NoIndex,
                    "service watch: a fresh database has no index yet");

                service.StartFullScan(settings, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready && service.Status.IncrementalAvailable,
                    "service watch: a completed scan starts incremental updates");

                string created = Path.Combine(tree, "zebrawatch-incremental.txt");
                File.WriteAllText(created, "x");
                Check(WaitUntil(() => HasRow(service, created), 5000),
                    "service watch: a created file appears without a full scan");

                // A row the watcher never saw (a missed rename outside the queue)
                // must be removed by the reconcile; real rows must survive.
                string deadPath = @"C:\zebrawatch-dead-" + Guid.NewGuid().ToString("N") + ".txt";
                using (var side = new SearchIndexStore(db))
                {
                    side.Upsert(Entry(deadPath, Path.GetFileName(deadPath), "txt", false, 1,
                        SearchPath.Normalize(tree)));
                }
                Check(service.Search("zebrawatch-dead-", 50, CancellationToken.None).TotalMatches == 1,
                    "service watch: the stale probe row is visible before the reconcile");

                service.RequestReconcile();
                service.WaitForIdle(TimeSpan.FromSeconds(30));
                Check(service.Status.State == FileSearchState.Ready,
                    "service watch: a requested reconcile finishes ready");
                Check(service.Search("zebrawatch-dead-", 50, CancellationToken.None).TotalMatches == 0,
                    "service watch: a reconcile removes a row the watcher never saw");
                Check(HasRow(service, created),
                    "service watch: a reconcile keeps the watcher's rows");
                Check(service.Status.IncrementalAvailable,
                    "service watch: a reconcile keeps incremental updates available");

                service.Pause();
                Check(!service.Status.IncrementalAvailable,
                    "service watch: pause stops incremental updates");
                service.Resume();
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready && service.Status.IncrementalAvailable,
                    "service watch: resume restarts incremental updates");

                // The reconcile interval is applied at runtime: 10 -> 0 stops the
                // timer and 0 -> 2 starts it again, without waiting for a scan.
                var timerOn = FileSearchSettings.CreateDefault();
                timerOn.Roots = new[] { tree };
                timerOn.Configured = true;
                timerOn.Enabled = true;
                timerOn.ReconcileMinutes = 10;
                service.ApplySettings(timerOn);
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.ReconcileIntervalMinutes == 10,
                    "service watch: ApplySettings arms the periodic reconcile timer");

                var timerOff = FileSearchSettings.CreateDefault();
                timerOff.Roots = new[] { tree };
                timerOff.Configured = true;
                timerOff.Enabled = true;
                timerOff.ReconcileMinutes = 0;
                service.ApplySettings(timerOff);
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.ReconcileIntervalMinutes == 0,
                    "service watch: ApplySettings 10->0 stops the periodic timer");

                var timerFast = FileSearchSettings.CreateDefault();
                timerFast.Roots = new[] { tree };
                timerFast.Configured = true;
                timerFast.Enabled = true;
                timerFast.ReconcileMinutes = 2;
                service.ApplySettings(timerFast);
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.ReconcileIntervalMinutes == 2,
                    "service watch: ApplySettings 0->2 starts the periodic timer");

                // A request made while a full scan is already Indexing must survive
                // the scan: the overflow path cannot be lost just because the state
                // was busy at that moment.
                int busyReconciles = 0;
                EventHandler<FileSearchStatus> countBusy = (sender, status) =>
                {
                    if (status.State == FileSearchState.Reconciling)
                    {
                        Interlocked.Increment(ref busyReconciles);
                    }
                };
                service.StatusChanged += countBusy;
                service.StartFullScan(timerFast, new[] { tree });
                service.RequestReconcile();
                service.WaitForIdle(TimeSpan.FromSeconds(30));
                service.StatusChanged -= countBusy;

                Check(Volatile.Read(ref busyReconciles) > 0
                      && service.Status.State == FileSearchState.Ready,
                    "service watch: a reconcile requested during a full scan runs after it");

                // A request that arrives while the service cannot run one (paused)
                // must be replayed on the next Ready instead of being dropped.
                int reconcilingTransitions = 0;
                EventHandler<FileSearchStatus> countReconciling = (sender, status) =>
                {
                    if (status.State == FileSearchState.Reconciling)
                    {
                        Interlocked.Increment(ref reconcilingTransitions);
                    }
                };
                service.StatusChanged += countReconciling;
                service.Pause();
                service.RequestReconcile();
                service.Resume();
                service.WaitForIdle(TimeSpan.FromSeconds(30));
                service.StatusChanged -= countReconciling;

                Check(Volatile.Read(ref reconcilingTransitions) > 0
                      && service.Status.State == FileSearchState.Ready,
                    "service watch: a reconcile requested while paused runs after resume");
            }

            var bad = FileSearchSettings.CreateDefault();
            bad.Roots = new[] { Path.Combine(_root, "zebrawatch-missing-" + Guid.NewGuid().ToString("N")) };
            bad.Configured = true;
            bad.Enabled = true;
            bad.ReconcileMinutes = 10;
            using (var service = new FileSearchIndexService(Path.Combine(_root, "service-watch-bad.db")))
            {
                service.ApplySettings(bad);
                service.StartFullScan(bad, null);
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready && !service.Status.IncrementalAvailable,
                    "service watch: unwatchable roots degrade to periodic reconcile only");
            }
        }

        /// <summary>
        /// The global service lifecycle: a configured full scan, pause/resume,
        /// cancel keeps the published rows, startup on an existing database must
        /// NOT rescan, a settings-fingerprint change only flags NeedsRebuild, an
        /// explicit rebuild, and removing/adding the search widget closes and
        /// reopens the store without losing the database.
        /// </summary>
        private static void ServiceCases()
        {
            string db = Path.Combine(_root, "service.db");
            string tree = Path.Combine(_root, "service-tree");
            Directory.CreateDirectory(tree);
            File.WriteAllText(Path.Combine(tree, "serviceprobe.txt"), "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            settings.Configured = true;
            settings.Enabled = true;

            int progressEvents = 0;

            using (var service = new FileSearchIndexService(db))
            {
                service.ProgressChanged += (s, p) => Interlocked.Increment(ref progressEvents);

                service.ApplySettings(settings);
                service.StartFullScan(settings, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));

                Check(service.Status.State == FileSearchState.Ready,
                    "service: a completed full scan is ready");
                Check(service.Status.FilesIndexed >= 1,
                    "service: a full scan reports its file count");
                Check(service.Search("serviceprobe", 300, CancellationToken.None).TotalMatches == 1,
                    "service: a configured full scan publishes searchable rows");
                Check(progressEvents > 0, "service: the scan forwards progress events");
                Check(service.Status.IncrementalAvailable,
                    "service: a completed full scan starts the watcher");
                Check(PathsEqual(service.Status.DatabasePath, Path.GetFullPath(db)),
                    "service: the status names the database path");

                service.Pause();
                Check(service.Status.State == FileSearchState.Paused, "service: pause changes state");
                service.Resume();
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready, "service: resume returns to ready");

                // The scanner always emits a final progress snapshot before it
                // returns; cancelling from that callback must still abort the
                // staging table instead of publishing the new file. Pause first so
                // the watcher (started by the previous scan) cannot index the new
                // file in parallel and mask a wrongly published row.
                service.Pause();
                File.WriteAllText(Path.Combine(tree, "service-cancelled.txt"), "x");
                int before = service.Search("serviceprobe", 300, CancellationToken.None).TotalMatches;
                bool cancelledOnProgress = false;
                EventHandler<ScanProgress> cancelOnProgress = (s, p) =>
                {
                    if (cancelledOnProgress) return;
                    cancelledOnProgress = true;
                    service.CancelScan();
                };
                service.ProgressChanged += cancelOnProgress;
                service.StartFullScan(settings, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                service.ProgressChanged -= cancelOnProgress;

                Check(cancelledOnProgress, "service: a scan reports progress while it is still running");
                Check(service.Search("serviceprobe", 300, CancellationToken.None).TotalMatches == before
                      && service.Search("service-cancelled", 300, CancellationToken.None).TotalMatches == 0,
                    "service: cancelling on progress keeps the old rows and publishes no partial rows");
                Check(service.Status.State != FileSearchState.Error
                      && service.Status.FilesIndexed == 0
                      && service.Status.DirectoriesScanned == 0,
                    "service: cancelling a scan resets its progress counters");
            }

            var startupTransitions = new List<FileSearchState>();
            using (var service = new FileSearchIndexService(db))
            {
                service.StatusChanged += (s, status) =>
                {
                    lock (startupTransitions) startupTransitions.Add(status.State);
                };

                service.ApplySettings(settings);
                service.SyncWithWidgets(new WidgetData[] { new SearchData() });
                service.WaitForIdle(TimeSpan.FromSeconds(20));

                Check(service.Status.State == FileSearchState.Ready,
                    "service: startup on an existing index is ready");
                bool sawIndexing;
                lock (startupTransitions) sawIndexing = startupTransitions.Contains(FileSearchState.Indexing);
                Check(!sawIndexing, "service: startup on an existing index does not start a full scan");
                Check(!service.Status.NeedsRebuild,
                    "service: startup with the same settings needs no rebuild");
                Check(service.Search("serviceprobe", 300, CancellationToken.None).TotalMatches == 1,
                    "service: startup keeps the rows of the existing index");

                var changed = FileSearchSettings.CreateDefault();
                changed.Roots = new[] { tree };
                changed.Configured = true;
                changed.Enabled = true;
                changed.Types = new[] { "txt" };
                service.ApplySettings(changed);
                service.WaitForIdle(TimeSpan.FromSeconds(20));

                Check(service.Status.NeedsRebuild,
                    "service: a changed settings fingerprint flags a rebuild");
                Check(service.Search("serviceprobe", 300, CancellationToken.None).TotalMatches == 1,
                    "service: flagging a rebuild does not delete rows");
                Check(File.Exists(db), "service: flagging a rebuild keeps the database file");

                service.RequestRebuild();
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready && !service.Status.NeedsRebuild,
                    "service: an explicit rebuild finishes ready and clears the flag");
                Check(service.Search("serviceprobe", 300, CancellationToken.None).TotalMatches == 1,
                    "service: an explicit rebuild publishes the rows again");

                service.SyncWithWidgets(new WidgetData[0]);
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Unconfigured,
                    "service: removing the search widget unconfigures the service");
                Check(File.Exists(db), "service: removing the search widget keeps the database");
                Check(service.Search("serviceprobe", 300, CancellationToken.None).TotalMatches == 0,
                    "service: removing the search widget closes the store");

                service.SyncWithWidgets(new WidgetData[] { new SearchData() });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready,
                    "service: adding the search widget reopens the existing index");
                Check(service.Search("serviceprobe", 300, CancellationToken.None).TotalMatches == 1,
                    "service: reopening after the widget returns is searchable");

                // Remove and re-add before the queued disposal can run: the
                // disposal must not close the store the re-add is using.
                service.SyncWithWidgets(new WidgetData[0]);
                service.SyncWithWidgets(new WidgetData[] { new SearchData() });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready,
                    "service: an immediate remove/re-add ends ready");
                Check(service.Search("serviceprobe", 300, CancellationToken.None).TotalMatches == 1,
                    "service: an immediate remove/re-add keeps a usable store");

                service.ApplySettings(null);
                Check(service.Status.State == FileSearchState.Unconfigured,
                    "service: null settings are unconfigured");
            }
        }

        /// <summary>
        /// A configured search widget on a machine with no index yet: startup must
        /// create the schema, report NoIndex and wait for onboarding to start the
        /// first full scan (no implicit scan and no Indexing transition).
        /// </summary>
        private static void ServiceFreshDatabaseCase()
        {
            string db = Path.Combine(_root, "service-fresh.db");
            string tree = Path.Combine(_root, "service-fresh-tree");
            Directory.CreateDirectory(tree);
            File.WriteAllText(Path.Combine(tree, "fresh-probe.txt"), "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            settings.Configured = true;
            settings.Enabled = true;

            var states = new List<FileSearchState>();
            using (var service = new FileSearchIndexService(db))
            {
                service.StatusChanged += (s, status) =>
                {
                    lock (states) states.Add(status.State);
                };

                service.ApplySettings(settings);
                service.SyncWithWidgets(new WidgetData[] { new SearchData() });
                service.WaitForIdle(TimeSpan.FromSeconds(20));

                Check(service.Status.State == FileSearchState.NoIndex,
                    "service: a fresh configured database reports no index");
                bool sawIndexing;
                lock (states) sawIndexing = states.Contains(FileSearchState.Indexing);
                Check(!sawIndexing, "service: startup never starts an implicit full scan");
                Check(File.Exists(db), "service: startup creates the missing database");
                Check(!service.ReadStatus().HasIndex, "service: the fresh database has no published index");
                Check(service.Search("fresh-probe", 300, CancellationToken.None).TotalMatches == 0,
                    "service: the fresh database returns no rows");
            }
        }

        /// <summary>
        /// A garbage database is quarantined and recreated (then an explicit rebuild
        /// works); a non-corruption open failure (a directory at the DB path) must
        /// leave the path untouched. A transient busy/locked database is retried
        /// once and never moved (not simulated here: a deterministic external lock
        /// is not reliable in this harness).
        /// </summary>
        private static void ServiceCorruptionCases()
        {
            string tree = Path.Combine(_root, "service-corrupt-tree");
            Directory.CreateDirectory(tree);
            File.WriteAllText(Path.Combine(tree, "corrupt-probe.txt"), "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            settings.Configured = true;
            settings.Enabled = true;

            string db = Path.Combine(_root, "service-corrupt.db");
            File.WriteAllBytes(db, Encoding.UTF8.GetBytes(
                "this is definitely not a sqlite database, it is just text that should fail the header check"));
            using (var service = new FileSearchIndexService(db))
            {
                service.ApplySettings(settings);
                service.SyncWithWidgets(new WidgetData[] { new SearchData() });
                service.WaitForIdle(TimeSpan.FromSeconds(20));

                Check(service.Status.State == FileSearchState.Error
                      || service.Status.State == FileSearchState.NoIndex,
                    "service: a corrupt database is reported, not thrown");
                Check(Directory.GetFiles(_root, "service-corrupt.db.failed-*").Length >= 1,
                    "service: a corrupt database is kept as .failed-<timestamp> evidence");
                Check(!service.ReadStatus().HasIndex,
                    "service: the recreated corrupt database is empty");
                Check(service.Search("corrupt-probe", 300, CancellationToken.None).TotalMatches == 0,
                    "service: the recreated corrupt database has no rows");

                service.RequestRebuild();
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready
                      && service.Search("corrupt-probe", 300, CancellationToken.None).TotalMatches == 1,
                    "service: a rebuild after corruption publishes rows");
            }

            string notADatabase = Path.Combine(_root, "service-not-a-database");
            Directory.CreateDirectory(notADatabase);
            using (var service = new FileSearchIndexService(notADatabase))
            {
                service.ApplySettings(settings);
                service.SyncWithWidgets(new WidgetData[] { new SearchData() });
                service.WaitForIdle(TimeSpan.FromSeconds(20));

                Check(service.Status.State == FileSearchState.Error,
                    "service: a non-corruption open failure reports an error");
                Check(Directory.Exists(notADatabase),
                    "service: a non-corruption failure does not move the database path");
                Check(Directory.GetFiles(_root, "service-not-a-database.failed-*").Length == 0,
                    "service: a non-corruption failure creates no quarantine file");
            }
        }

        /// <summary>Shutdown is bounded by one timeout even with an open store to checkpoint.</summary>
        private static void ServiceShutdownCase()
        {
            string db = Path.Combine(_root, "service-shutdown.db");
            string tree = Path.Combine(_root, "service-shutdown-tree");
            Directory.CreateDirectory(tree);
            File.WriteAllText(Path.Combine(tree, "shutdown-probe.txt"), "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            settings.Configured = true;
            settings.Enabled = true;

            var service = new FileSearchIndexService(db);
            service.ApplySettings(settings);
            service.SyncWithWidgets(new WidgetData[] { new SearchData() });
            service.WaitForIdle(TimeSpan.FromSeconds(20));

            var clock = Stopwatch.StartNew();
            service.Shutdown(TimeSpan.FromSeconds(5));
            clock.Stop();
            Check(clock.Elapsed < TimeSpan.FromSeconds(5),
                "service: shutdown returns within its supplied timeout");
            service.Dispose(); // idempotent after Shutdown
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

        /// <summary>
        /// Polls a watcher-dependent condition with a generous bound. The watcher
        /// delivers asynchronously, so no assertion may depend on absolute timing;
        /// 50ms is short enough that a healthy host answers well inside 5s.
        /// </summary>
        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            do
            {
                try
                {
                    if (condition()) return true;
                }
                catch (Exception)
                {
                    // A query racing a store swap or a watcher write is retried.
                }
                Thread.Sleep(50);
            }
            while (clock.ElapsedMilliseconds < timeoutMs);

            try { return condition(); }
            catch (Exception) { return false; }
        }

        /// <summary>Indexed size of the exact row, or -1 when it is absent.</summary>
        private static long RowSize(SearchIndexStore store, string path)
        {
            var result = store.Query(Path.GetFileName(path), 50, CancellationToken.None);
            foreach (var item in result.Items)
            {
                if (PathsEqual(item.Path, path)) return item.SizeBytes;
            }
            return -1;
        }

        /// <summary>Exact path probe that ignores the Root column (moves between roots).</summary>
        private static bool HasPath(SearchIndexStore store, string path)
        {
            var result = store.Query(Path.GetFileName(path), 50, CancellationToken.None);
            foreach (var item in result.Items)
            {
                if (PathsEqual(item.Path, path)) return true;
            }
            return false;
        }

        private static bool HasRow(FileSearchIndexService service, string path)
        {
            var result = service.Search(Path.GetFileName(path), 300, CancellationToken.None);
            foreach (var item in result.Items)
            {
                if (PathsEqual(item.Path, path)) return true;
            }
            return false;
        }

        private static bool PathsEqual(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Exact-row probe that does not depend on the checkout path. The query uses
        /// the item's own file name (a token that cannot be a path fragment of the
        /// scratch root), and the answer is the stored Path/Root pair, not a count:
        /// a path-infix match can otherwise inflate counts when the repo path happens
        /// to contain the probe letters.
        /// </summary>
        private static bool HasRow(SearchIndexStore store, string path, string root)
        {
            var result = store.Query(Path.GetFileName(path), 300, CancellationToken.None);
            foreach (var item in result.Items)
            {
                if (PathsEqual(item.Path, path) && PathsEqual(item.Root, root)) return true;
            }
            return false;
        }
    }
}
