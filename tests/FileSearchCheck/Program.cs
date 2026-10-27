using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Kobold.Core;
using Kobold.Core.FileSearch;
using Microsoft.Data.Sqlite;
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

            // The usage pins read the WidgetManager singleton, whose first
            // access loads and later saves a config. Redirect it to a throwaway
            // sandbox here, before RunAll: a pin that reached Instance before
            // this install would make OverrideConfigPathsForChecks throw.
            WidgetManager.OverrideConfigPathsForChecks(
                Path.Combine(UsageSandboxDir(), "config.json"),
                Path.Combine(UsageSandboxDir(), "config.json.backup"));
            try { RunAll(); }
            catch (Exception ex)
            {
                Console.WriteLine("UNHANDLED " + ex.GetType().FullName);
                Console.WriteLine("MESSAGE " + ex.Message);
                Console.WriteLine("STACK " + ex.StackTrace);
                Errors.Add("unhandled " + ex.GetType().FullName);
            }
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

        /// <summary>The throwaway config directory the singleton is pinned to in Main.</summary>
        private static string UsageSandboxDir()
        {
            return Path.Combine(_root, "usage-config");
        }

        /// <summary>True when the file exists and already carries the marker.</summary>
        private static bool ConfigContains(string path, string marker)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).Contains(marker);
            }
            catch (IOException)
            {
                // AtomicFile may be replacing the file right now.
                return false;
            }
        }

        /// <summary>
        /// Waits for a scheduled debounced save to land: reads the sandbox file
        /// until it carries the marker. The force-save cap (ForceSaveIntervalMs,
        /// set before the record) is the flush helper - the record itself only
        /// schedules the write, so this proves the debounced path ran.
        /// </summary>
        private static bool WaitForConfigContains(string path, string marker, int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (ConfigContains(path, marker)) return true;
                Thread.Sleep(50);
            }
            return false;
        }

        private static void RunAll()
        {
            TypeFilterCases();
            ExcludeCases();
            UserExcludeCases();
            CacheDefaultExclusionCases();
            PathCases();
            ParentCases();
            SettingsDefaults();
            NormalizeCases();
            EntryCases();
            BadTimestampCases();
            RootCases();
            SearchDataModelCase();
            SearchDataCases();
            SettingsUiCases();
            ConfigCases();
            UsageCases();
            UsageKeepCases();
            UsageProbationCases();
            UsageGuardCases();
            UsageTopCases();
            StoreCases();
            StoreSchemaMigrationCases();
            StoreMigrationVersionResetCase();
            StoreMigrationVersionAheadCase();
            StoreSuffixCases();
            StoreDeleteBoundaryCases();
            BoundaryCases();
            StoreConcurrencyCase();
            StoreSnapshotCase();
            ScannerCases();
            ScannerSubtreeCases();
            ScannerBatchCases();
            ReconcileUnreadableCase();
            ReconcileWritesOnlyDifferencesCase();
            ReconcileSessionCases();
            ReconcileScannerDiffCases();
            ReconcileEmojiNameCases();
            ReconcileExcludedDirectoryCases();
            ReconcileNewDefaultExcludedCase();
            ReconcileRootRepairCase();
            ReconcileNoFolderRowsCase();
            ReconcilePartialListingCase();
            ReconcileOrphanRowCase();
            ReconcileCancelRollbackCase();
            StaleStagingTableCase();
            WatcherCases();
            WatcherStatDietCases();
            ServiceCases();
            ServiceIncrementalCases();
            ServiceFreshDatabaseCase();
            ServiceCorruptionCases();
            ServiceNoRootsCase();
            ServiceCancelCases();
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

        /// <summary>
        /// The environment-derived cache defaults: browser profiles, the Store apps'
        /// volatile directories, the Windows internet caches and IDE caches are out of
        /// scope without a user exclusion. Every probe is a fake child under the real
        /// environment root, so the pin does not depend on Chrome (or any app) being
        /// installed.
        /// </summary>
        private static void CacheDefaultExclusionCases()
        {
            var s = FileSearchSettings.CreateDefault();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var probes = new List<string>();
            AddCacheProbe(probes, local, "Google", "Chrome", "User Data", "KoboldFake", "Cache");
            AddCacheProbe(probes, local, "Microsoft", "Edge", "User Data", "KoboldFake");
            // Firefox keeps its profiles in Roaming; Profile Groups leave a Local copy.
            AddCacheProbe(probes, roaming, "Mozilla", "Firefox", "Profiles", "KoboldFake");
            AddCacheProbe(probes, local, "Mozilla", "Firefox", "Profiles", "KoboldFake");
            // Only the volatile children of a Store package are cache.
            AddCacheProbe(probes, local, "Packages", "KoboldFake", "LocalCache", "KoboldFake");
            AddCacheProbe(probes, local, "Packages", "KoboldFake", "TempState", "KoboldFake");
            AddCacheProbe(probes, local, "Packages", "KoboldFake", "AC", "KoboldFake");
            AddCacheProbe(probes, local, "Microsoft", "Windows", "INetCache", "KoboldFake");
            AddCacheProbe(probes, local, "Microsoft", "Windows", "WebCache", "KoboldFake");
            AddCacheProbe(probes, local, "JetBrains", "KoboldFake");
            AddCacheProbe(probes, roaming, "Code", "KoboldFake");
            AddCacheProbe(probes, profile, ".vscode", "KoboldFake");
            AddCacheProbe(probes, profile, ".cache", "KoboldFake");

            bool allExcluded = probes.Count >= 10;
            foreach (string probe in probes)
            {
                if (!SearchDirectoryFilter.IsExcluded(probe, null, s)) allExcluded = false;
            }
            Check(allExcluded, "search: cache paths are excluded by default");

            Check(!SearchDirectoryFilter.IsExcluded(Path.Combine(local, "KoboldFakeNormal"), null, s),
                "search: a normal folder under the same root is not excluded");

            // Narrowing the Store default: the package directory and the user-visible
            // LocalState files stay in scope, so only the volatile children are cache.
            string package = Path.Combine(local, "Packages", "KoboldFake");
            Check(!SearchDirectoryFilter.IsExcluded(package, null, s),
                "search: a Store package directory itself is not excluded");
            Check(!SearchDirectoryFilter.IsExcluded(Path.Combine(package, "LocalState"), "LocalState", s)
                  && !SearchDirectoryFilter.IsExcluded(
                      Path.Combine(package, "LocalState", "notes.txt"), null, s),
                "search: a Store package's LocalState files are still searched");
            Check(!SearchDirectoryFilter.IsExcluded(
                    Path.Combine(package, "LocalState", "LocalCache", "notes.txt"), null, s),
                "search: a LocalState child named LocalCache is not confused with the volatile one");

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Check(SearchDirectoryFilter.IsExcluded(Path.Combine(windows, "WinSxS", "x"), "x", s)
                  && SearchDirectoryFilter.IsExcluded(Path.Combine(Path.GetTempPath(), "KoboldFake"), null, s)
                  && SearchDirectoryFilter.IsExcluded(@"C:\p\node_modules\a", "a", s)
                  && SearchDirectoryFilter.IsExcluded(@"C:\p\.git\objects", "objects", s),
                "search: the existing default exclusions still match");
        }

        private static void AddCacheProbe(List<string> probes, string basePath, params string[] segments)
        {
            if (string.IsNullOrEmpty(basePath)) return;

            string path = basePath;
            for (int i = 0; i < segments.Length; i++) path = Path.Combine(path, segments[i]);
            probes.Add(path);
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

        /// <summary>
        /// The parent key of one indexed row: the containing directory ending with
        /// exactly one separator, or "" for a drive root. Every write derives it from
        /// the path (upsert) and the v1 backfill mirrors it in SQL, so this table is
        /// the contract both sides are pinned to.
        /// </summary>
        private static void ParentCases()
        {
            Check(SearchIndexStore.ParentOf(@"C:\dir\file.txt") == @"C:\dir\",
                "parent: a nested file is keyed by its directory");
            Check(SearchIndexStore.ParentOf(@"C:\dir") == @"C:\",
                "parent: a nested directory is keyed by its own parent");
            Check(SearchIndexStore.ParentOf(@"C:\file.txt") == @"C:\",
                "parent: a file in a drive root is keyed by the root");
            Check(SearchIndexStore.ParentOf(@"C:\") == "",
                "parent: the drive root has no parent");
            Check(SearchIndexStore.ParentOf(@"D:\") == "",
                "parent: another drive root has no parent");
            Check(SearchIndexStore.ParentOf(@"C:\dir\") == @"C:\",
                "parent: a stored trailing separator is stripped, never doubled");
            Check(SearchIndexStore.ParentOf(@"C:\dir\\") == @"C:\",
                "parent: every trailing separator is stripped before the last one counts");
            Check(SearchIndexStore.ParentOf(@"C:\\") == "",
                "parent: a drive root with a doubled separator still has no parent");
            Check(SearchIndexStore.ParentOf(@"\\server\share\dir\file") == @"\\server\share\dir\",
                "parent: a UNC file is keyed by its directory");
            Check(SearchIndexStore.ParentOf(@"\\server\share\dir") == @"\\server\share\",
                "parent: a UNC directory is keyed by its own parent");
            Check(SearchIndexStore.ParentOf(@"\\server\share") == "",
                "parent: a UNC share root has no parent (the server namespace is not a directory)");
            Check(SearchIndexStore.ParentOf(@"\\server\share\") == "",
                "parent: a UNC share root with a trailing separator has no parent either");
            Check(SearchIndexStore.ParentOf(string.Empty) == "",
                "parent: an empty path has no parent");
        }

        private static void SettingsDefaults()
        {
            var s = FileSearchSettings.CreateDefault();
            Check(s.Types != null && Array.IndexOf(s.Types, "pdf") >= 0 && Array.IndexOf(s.Types, "cs") >= 0,
                "settings: default types include the spec's document and code types");
            Check(s.IndexFolders && s.IndexExtensionless && !s.IndexAllFiles && s.ReconcileMinutes == 30,
                "settings: defaults are folders/extensionless on, all-files off, reconcile 30");
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

        /// <summary>
        /// Task 7: the pure usage update increments the entry a file already has
        /// (a different case is the same file), refreshes its timestamp and
        /// orders by count, then recency, then path.
        /// </summary>
        private static void UsageCases()
        {
            var list = new List<FrequentFile>();
            var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            FrequentFiles.Record(list, @"C:\usage\a.txt", start, FrequentFiles.Keep);
            FrequentFiles.Record(list, @"C:\usage\a.txt", start.AddMinutes(1), FrequentFiles.Keep);
            FrequentFiles.Record(list, @"C:\usage\b.txt", start.AddMinutes(2), FrequentFiles.Keep);
            FrequentFiles.Record(list, @"C:\USAGE\A.TXT", start.AddMinutes(3), FrequentFiles.Keep);

            var a = list.Find(f => f != null && f.Path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase));
            var b = list.Find(f => f != null && f.Path.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase));
            Check(list.Count == 2 && a != null && b != null && a.Count == 3 && b.Count == 1 && list[0] == a,
                "usage: an open increments and reorders the list");
            Check(a != null && a.LastOpenedUtc == start.AddMinutes(3),
                "usage: an open refreshes the last-opened timestamp");
            Check(a != null && string.Equals(a.Path, Path.GetFullPath(@"C:\usage\a.txt"),
                    StringComparison.OrdinalIgnoreCase),
                "usage: the stored path is absolute");
        }

        /// <summary>
        /// Task 7: the config keeps only the most-used entries, so a long tail of
        /// one-off opens can never grow without bound.
        /// </summary>
        private static void UsageKeepCases()
        {
            var list = new List<FrequentFile>();
            var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            // The i-th file is opened (i + 1) times: 30 files with counts 1..30.
            // The clock advances with every open, so recency is unambiguous.
            var now = start;
            for (int i = 0; i < 30; i++)
            {
                string path = @"C:\usage\keep\file" + i.ToString("D2") + ".txt";
                for (int open = 0; open <= i; open++)
                {
                    FrequentFiles.Record(list, path, now, FrequentFiles.Keep);
                    now = now.AddSeconds(1);
                }
            }

            Check(list.Count == FrequentFiles.Keep
                  && list[0].Count == 30
                  && list[list.Count - 1].Count == 11
                  && list.TrueForAll(f => f.Count >= 11),
                "usage: only the most-used entries are kept");
        }

        /// <summary>
        /// Task 7: the guarded public record ignores anything that is not an
        /// existing file, writes into the singleton's config (a throwaway
        /// sandbox) and the list survives a JSON round-trip. Task 7 fix round 1:
        /// an open only schedules the debounced write (never a synchronous one)
        /// and that write persists the block, while a default config writes no
        /// block at all.
        /// </summary>
        private static void UsageGuardCases()
        {
            // The singleton was redirected to this directory in Main.
            string sandbox = UsageSandboxDir();
            Directory.CreateDirectory(sandbox);
            string configPath = Path.Combine(sandbox, "config.json");

            string folder = Path.Combine(_root, "usage-folder");
            Directory.CreateDirectory(folder);
            string missing = Path.Combine(_root, "usage-missing.txt");
            string realFile = Path.Combine(_root, "usage-real.txt");
            File.WriteAllText(realFile, "recorded");

            try
            {
                AppConfig config = WidgetManager.Instance.Config;
                config.FrequentFiles = null;

                string bare = Newtonsoft.Json.JsonConvert.SerializeObject(new AppConfig());
                Check(!bare.Contains("FrequentFiles"),
                    "usage: a default config writes no frequent-files block");

                FrequentFiles.Record(folder);
                FrequentFiles.Record(missing);
                Check(config.FrequentFiles == null,
                    "usage: a missing file or a directory is not recorded");

                // Keep the force cap well clear of the observation below; the
                // idle debounce still lands the write and the wait polls for it.
                config.ForceSaveIntervalMs = 5000;
                FrequentFiles.Record(realFile);
                Check(!ConfigContains(configPath, "\"FrequentFiles\""),
                    "usage: an open does not write the config synchronously");

                bool recorded = config.FrequentFiles != null && config.FrequentFiles.Count == 1;
                Check(recorded && string.Equals(config.FrequentFiles[0].Path,
                        Path.GetFullPath(realFile), StringComparison.OrdinalIgnoreCase)
                      && config.FrequentFiles[0].Count == 1,
                    "usage: a real file is recorded once");
                Check(FrequentFiles.Top(10).Count == (recorded ? 1 : 0),
                    "usage: the top view reads the recorded entry");

                bool persisted = WaitForConfigContains(configPath, "\"FrequentFiles\"", 8000);
                var block = persisted
                    ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(configPath))["FrequentFiles"]
                        as Newtonsoft.Json.Linq.JArray
                    : null;
                Check(block != null && block.Count == 1
                      && string.Equals((string)block[0]["Path"], Path.GetFullPath(realFile),
                          StringComparison.OrdinalIgnoreCase),
                    "usage: the debounced save persists the block");
            }
            finally
            {
                try { File.Delete(realFile); } catch { }
                try { Directory.Delete(folder, true); } catch { }
            }

            var stored = new AppConfig();
            var stamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            stored.FrequentFiles = new List<FrequentFile>
            {
                new FrequentFile { Path = @"C:\usage\one.txt", Count = 4, LastOpenedUtc = stamp },
                new FrequentFile { Path = @"C:\usage\two.txt", Count = 2, LastOpenedUtc = stamp.AddDays(-1) }
            };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(stored);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<AppConfig>(json);
            Check(back.FrequentFiles != null && back.FrequentFiles.Count == 2
                  && back.FrequentFiles[0].Path == @"C:\usage\one.txt"
                  && back.FrequentFiles[0].Count == 4
                  && back.FrequentFiles[0].LastOpenedUtc == stamp
                  && back.FrequentFiles[1].Path == @"C:\usage\two.txt",
                "usage: the config round-trips the list");
        }

        /// <summary>
        /// Task 7 fix round 1: a full list keeps one probation slot. The
        /// newcomer survives at count 1 and the second-lowest entry (count 2)
        /// is evicted, so a first open is not undone on the spot.
        /// </summary>
        private static void UsageProbationCases()
        {
            var list = new List<FrequentFile>();
            var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            // FrequentFiles.Keep entries at counts 2..21: file00 is the lowest
            // and file01 the second-lowest.
            for (int i = 0; i < FrequentFiles.Keep; i++)
            {
                string path = @"C:\usage\probation\file" + i.ToString("D2") + ".txt";
                for (int open = 0; open < i + 2; open++)
                {
                    FrequentFiles.Record(list, path, now, FrequentFiles.Keep);
                    now = now.AddSeconds(1);
                }
            }

            Check(list.Count == FrequentFiles.Keep,
                "usage: the probation pin starts from a full list");

            FrequentFiles.Record(list, @"C:\usage\probation\newcomer.txt", now, FrequentFiles.Keep);
            var newcomer = list.Find(f => f != null && f.Path.EndsWith("newcomer.txt", StringComparison.OrdinalIgnoreCase));
            var lowest = list.Find(f => f != null && f.Path.EndsWith("file00.txt", StringComparison.OrdinalIgnoreCase));
            var secondLowest = list.Find(f => f != null && f.Path.EndsWith("file01.txt", StringComparison.OrdinalIgnoreCase));
            Check(list.Count == FrequentFiles.Keep && newcomer != null && newcomer.Count == 1
                  && lowest == null && secondLowest != null,
                "usage: a full list keeps one probation slot for a new file");
        }

        /// <summary>
        /// Task 7 fix round 1: the top view orders by count, then recency, then
        /// path ordinal, hands out copies of the entries, ignores a non-positive
        /// count and hides entries whose file is gone; the next record prunes
        /// those dead entries from the stored list.
        /// </summary>
        private static void UsageTopCases()
        {
            string dir = Path.Combine(_root, "usage-top");
            Directory.CreateDirectory(dir);
            string a = Path.Combine(dir, "a.txt");
            string b = Path.Combine(dir, "b.txt");
            string c = Path.Combine(dir, "c.txt");
            string d = Path.Combine(dir, "d.txt");
            foreach (var path in new[] { a, b, c, d }) File.WriteAllText(path, "x");
            string dead = Path.Combine(dir, "deleted.txt");

            AppConfig config = WidgetManager.Instance.Config;
            List<FrequentFile> saved = config.FrequentFiles;
            var t0 = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            try
            {
                config.FrequentFiles = new List<FrequentFile>
                {
                    new FrequentFile { Path = a, Count = 1, LastOpenedUtc = t0 },
                    new FrequentFile { Path = b, Count = 1, LastOpenedUtc = t0 },
                    new FrequentFile { Path = c, Count = 2, LastOpenedUtc = t0 },
                    new FrequentFile { Path = d, Count = 2, LastOpenedUtc = t0.AddMinutes(1) }
                };

                var top = FrequentFiles.Top(10);
                Check(top.Count == 4 && top[0].Path == d && top[1].Path == c
                      && top[2].Path == a && top[3].Path == b,
                    "usage: the top view orders by count, then recency, then path");

                top[0].Count = 99;
                top[0].Path = a;
                var live = config.FrequentFiles.Find(f => f.Path == d);
                Check(live != null && live.Count == 2 && live.Path == d,
                    "usage: the top view hands out copies, not the live entries");

                Check(FrequentFiles.Top(0).Count == 0 && FrequentFiles.Top(-1).Count == 0,
                    "usage: a non-positive top view is empty");

                // A deleted file disappears from the view at once ...
                config.FrequentFiles = new List<FrequentFile>
                {
                    new FrequentFile { Path = dead, Count = 9, LastOpenedUtc = t0 },
                    new FrequentFile { Path = a, Count = 1, LastOpenedUtc = t0 }
                };
                var pruned = FrequentFiles.Top(10);
                Check(pruned.Count == 1 && pruned[0].Path == a,
                    "usage: the top view hides a deleted file");

                // ... and the next record prunes it from the stored list.
                FrequentFiles.Record(a);
                Check(config.FrequentFiles.Count == 1 && config.FrequentFiles[0].Path == a
                      && config.FrequentFiles[0].Count == 2,
                    "usage: the next record prunes a deleted file");
            }
            finally
            {
                config.FrequentFiles = saved;
                try { Directory.Delete(dir, true); } catch { }
            }
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
                      && store.ReadStatus().SchemaVersion == 2 && store.ReadStatus().LastScanUtc != null,
                    "store: status exposes the scan counters, fingerprint, schema version and timestamp");
                Check(RowParent(db, @"C:\p\Alpha.txt") == @"C:\p\"
                      && RowParent(db, @"C:\p\子目录") == @"C:\p\"
                      && RowParent(db, @"C:\p\sub\Beta.txt") == @"C:\p\sub\",
                    "store: every published row carries the parent of its path");
                Check(ScalarCount(db,
                          "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_files_parent';") == 1,
                    "store: the published table carries the parent index");
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

        /// <summary>
        /// The v1 files table: today's schema without the Parent column, including the
        /// three indexes the v1 build created after a full scan.
        /// </summary>
        private const string LegacyFilesSql = @"
CREATE TABLE files (
    Path TEXT PRIMARY KEY COLLATE NOCASE,
    Name TEXT NOT NULL,
    NameLower TEXT NOT NULL,
    Extension TEXT NOT NULL,
    IsDirectory INTEGER NOT NULL,
    SizeBytes INTEGER NOT NULL,
    ModifiedUtc INTEGER NOT NULL,
    Root TEXT NOT NULL);
CREATE INDEX idx_files_name_lower ON files(NameLower);
CREATE INDEX idx_files_extension ON files(Extension);
CREATE INDEX idx_files_root ON files(Root);
CREATE TABLE meta (Key TEXT PRIMARY KEY, Value TEXT);";

        /// <summary>
        /// v1 -> v2 without a full rebuild: a database written before the Parent
        /// column existed gets the column added in place, every row backfilled with
        /// the value <see cref="SearchIndexStore.ParentOf"/> derives, the parent index
        /// created and the version bumped. The seeded rows are exactly the cases the
        /// backfill SQL has to agree with C# on (drive root, a file in that root, a
        /// nested directory and file, doubled trailing separators, a UNC file), and
        /// both sides are asserted. A rebuild would lose the published scan, so the
        /// row count and the scan timestamp are pinned too.
        /// </summary>
        private static void StoreSchemaMigrationCases()
        {
            string db = Path.Combine(_root, "migrate-v1.db");
            using (var connection = OpenSqlite(db))
            {
                ExecuteSql(connection, LegacyFilesSql);
                ExecuteSql(connection, @"INSERT INTO files
                    (Path, Name, NameLower, Extension, IsDirectory, SizeBytes, ModifiedUtc, Root) VALUES
                    ('C:\', 'C:\', 'c:\', '', 1, 0, 1700000000, 'C:\'),
                    ('C:\dir', 'dir', 'dir', '', 1, 0, 1700000000, 'C:\'),
                    ('C:\dir\file.txt', 'file.txt', 'file.txt', 'txt', 0, 10, 1700000000, 'C:\'),
                    ('C:\top.txt', 'top.txt', 'top.txt', 'txt', 0, 20, 1700000000, 'C:\'),
                    ('C:\\', 'C:\', 'c:\', '', 1, 0, 1700000000, 'C:\'),
                    ('C:\dir\\', 'dir', 'dir', '', 1, 0, 1700000000, 'C:\'),
                    ('\\server\share\dir\file.txt', 'file.txt', 'file.txt', 'txt', 0, 30, 1700000000,
                     '\\server\share');");
                ExecuteSql(connection, @"INSERT INTO meta (Key, Value) VALUES
                    ('schema_version', '1'), ('last_scan_utc', '2026-01-02T03:04:05.0000000Z'),
                    ('settings_fingerprint', 'v1-fp'), ('row_count', '7');");
                ExecuteSql(connection, "PRAGMA user_version=1;");
            }

            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                IndexStatus status = store.ReadStatus();
                Check(status.SchemaVersion == 2 && MetaValue(db, "schema_version") == "2",
                    "migrate: a v1 index is stamped as schema version 2");
                Check(ScalarCount(db, "PRAGMA user_version;") == 2 && status.HasIndex,
                    "migrate: user_version itself reports 2 and the index stays published");

                string[] paths =
                {
                    @"C:\", @"C:\dir", @"C:\dir\file.txt", @"C:\top.txt", @"C:\\", @"C:\dir\\",
                    @"\\server\share\dir\file.txt"
                };
                bool backfillMatchesParentOf = true;
                for (int i = 0; i < paths.Length; i++)
                {
                    if (!string.Equals(RowParent(db, paths[i]), SearchIndexStore.ParentOf(paths[i]),
                            StringComparison.Ordinal))
                    {
                        backfillMatchesParentOf = false;
                    }
                }
                Check(backfillMatchesParentOf,
                    "migrate: the SQL backfill matches ParentOf for every seeded row");
                Check(RowParent(db, @"C:\") == "" && RowParent(db, @"C:\dir") == @"C:\"
                      && RowParent(db, @"C:\dir\file.txt") == @"C:\dir\"
                      && RowParent(db, @"C:\top.txt") == @"C:\"
                      && RowParent(db, @"C:\\") == ""
                      && RowParent(db, @"C:\dir\\") == @"C:\"
                      && RowParent(db, @"\\server\share\dir\file.txt") == @"\\server\share\dir\",
                    "migrate: the backfilled parents are the expected values");
                Check(ScalarCount(db, "SELECT COUNT(*) FROM files;") == 7
                      && status.LastScanUtc != null && status.SettingsFingerprint == "v1-fp",
                    "migrate: the migration keeps every row and the published scan (no rebuild)");
                Check(ScalarCount(db,
                          "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_files_parent';") == 1,
                    "migrate: the migration creates the parent index");

                using (var session = store.OpenReconcileSession())
                {
                    var rootChildren = session.ReadChildren(@"C:\", CancellationToken.None);
                    var dirChildren = session.ReadChildren(@"C:\dir", CancellationToken.None);
                    var uncChildren = session.ReadChildren(@"\\server\share\dir", CancellationToken.None);
                    // The seeded "C:\dir\\" row has the backfilled parent "C:\" but is
                    // dropped by the belt-and-braces direct-child filter (its path has
                    // another separator after the parent), so it is not a child here.
                    Check(rootChildren.Count == 2 && HasEntry(rootChildren, @"C:\dir")
                          && HasEntry(rootChildren, @"C:\top.txt")
                          && !HasEntry(rootChildren, @"C:\dir\file.txt"),
                        "migrate: ReadChildren on the migrated drive root returns its direct children only");
                    Check(dirChildren.Count == 1 && HasEntry(dirChildren, @"C:\dir\file.txt"),
                        "migrate: ReadChildren on a migrated nested directory returns its file only");
                    Check(uncChildren.Count == 1 && HasEntry(uncChildren, @"\\server\share\dir\file.txt"),
                        "migrate: ReadChildren on a migrated UNC directory returns its file only");
                }

                // The migration is done: a second open is a no-op and must not re-stamp
                // or rebuild an already migrated index.
                DateTime? scanBefore = status.LastScanUtc;
                store.EnsureSchema();
                IndexStatus again = store.ReadStatus();
                Check(again.SchemaVersion == 2 && again.LastScanUtc == scanBefore
                      && ScalarCount(db, "SELECT COUNT(*) FROM files;") == 7,
                    "migrate: a second EnsureSchema on a v2 index neither migrates nor rebuilds");
            }
        }

        /// <summary>
        /// The guard is not column-presence. A v2 index reopened by a v1 build still has
        /// the Parent column (the v1 build does not know it), but the v1 8-column INSERT
        /// leaves it at the NOT NULL <c>''</c> default and the v1 EnsureSchema stamps
        /// <c>user_version</c> back to 1. Those rows are invisible to ReadChildren and
        /// would never be pruned, so a version below 2 must re-run the idempotent
        /// backfill even though the column exists. Non-vacuous: under the old
        /// column-only guard the row keeps Parent='' and stays invisible.
        /// </summary>
        private static void StoreMigrationVersionResetCase()
        {
            string db = Path.Combine(_root, "migrate-reset.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.Upsert(Entry(@"C:\dir\file.txt", "file.txt", "txt", false, 10, @"C:\"));
            }
            Check(MetaValue(db, "parent_filled") == "2",
                "migrate reset: the v2 stamp is written on a fresh index");
            Check(RowParent(db, @"C:\dir\file.txt") == @"C:\dir\",
                "migrate reset: a v2 upsert carries its parent");

            using (var connection = OpenSqlite(db))
            {
                // Exactly what a v1 build does to a v2 file: an 8-column INSERT (Parent
                // falls back to '') and a stamp reset to schema version 1.
                ExecuteSql(connection, @"INSERT INTO files
                    (Path, Name, NameLower, Extension, IsDirectory, SizeBytes, ModifiedUtc, Root)
                    VALUES ('C:\dir\new.txt', 'new.txt', 'new.txt', 'txt', 0, 5, 1700000000, 'C:\');");
                ExecuteSql(connection, "UPDATE meta SET Value = '1' WHERE Key = 'schema_version';");
                ExecuteSql(connection, "PRAGMA user_version=1;");
            }
            Check(RowParent(db, @"C:\dir\new.txt") == "",
                "migrate reset: the v1-written row starts on the empty default");
            Check(MetaValue(db, "parent_filled") == "2",
                "migrate reset: the reset leaves the old backfill stamp behind");

            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                Check(RowParent(db, @"C:\dir\new.txt") == @"C:\dir\",
                    "migrate reset: a column-present index with user_version 1 is backfilled again");
                Check(ScalarCount(db, "PRAGMA user_version;") == 2
                      && MetaValue(db, "schema_version") == "2",
                    "migrate reset: the repaired index is stamped as version 2 again");
                using (var session = store.OpenReconcileSession())
                {
                    var children = session.ReadChildren(@"C:\dir", CancellationToken.None);
                    Check(children.Count == 2 && HasEntry(children, @"C:\dir\new.txt"),
                        "migrate reset: the repaired row is visible to ReadChildren again");
                }
            }
        }

        /// <summary>
        /// The other direction: a file stamped version 2 whose table still lacks the
        /// column (an interrupted migration, or a hand-made table carrying the new
        /// stamp). The column is additive, so it must appear and be backfilled; the
        /// version alone must not be trusted.
        /// </summary>
        private static void StoreMigrationVersionAheadCase()
        {
            string db = Path.Combine(_root, "migrate-ahead.db");
            using (var connection = OpenSqlite(db))
            {
                ExecuteSql(connection, LegacyFilesSql);
                ExecuteSql(connection, @"INSERT INTO files
                    (Path, Name, NameLower, Extension, IsDirectory, SizeBytes, ModifiedUtc, Root) VALUES
                    ('C:\', 'C:\', 'c:\', '', 1, 0, 1700000000, 'C:\'),
                    ('C:\dir', 'dir', 'dir', '', 1, 0, 1700000000, 'C:\'),
                    ('C:\dir\file.txt', 'file.txt', 'file.txt', 'txt', 0, 10, 1700000000, 'C:\');");
                ExecuteSql(connection, @"INSERT INTO meta (Key, Value) VALUES
                    ('schema_version', '2'), ('parent_filled', '2');");
                ExecuteSql(connection, "PRAGMA user_version=2;");
            }
            Check(ScalarCount(db,
                      "SELECT COUNT(*) FROM pragma_table_info('files') WHERE name = 'Parent';") == 0,
                "migrate ahead: the seeded table really has no parent column");

            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                Check(ScalarCount(db,
                          "SELECT COUNT(*) FROM pragma_table_info('files') WHERE name = 'Parent';") == 1,
                    "migrate ahead: a version-2 stamp does not hide a missing column");
                Check(RowParent(db, @"C:\dir\file.txt") == @"C:\dir\"
                      && RowParent(db, @"C:\dir") == @"C:\"
                      && RowParent(db, @"C:\") == "",
                    "migrate ahead: the added column is backfilled for every row");
                Check(ScalarCount(db, "PRAGMA user_version;") == 2,
                    "migrate ahead: the version stays 2");
                using (var session = store.OpenReconcileSession())
                {
                    var children = session.ReadChildren(@"C:\dir", CancellationToken.None);
                    Check(children.Count == 1 && HasEntry(children, @"C:\dir\file.txt"),
                        "migrate ahead: ReadChildren works on the repaired table");
                }
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
                    Entry(@"C:\p\a_b", "a_b", "", true, 0, @"C:\p"),
                    Entry(@"C:\p\a_b\inside.txt", "inside.txt", "txt", false, 60, @"C:\p"),
                    Entry(@"C:\p\axb", "axb", "", false, 70, @"C:\p"),
                    Entry(@"D:\other.txt", "other.txt", "txt", false, 50, @"D:\"),
                });
                store.CompleteFullScan(0, 0, 0, "fp");

                // The subtree delete must seek, not scan: both the equality and the
                // prefix range are planned as index searches on the Path primary key
                // (SQLite reports them as MULTI-INDEX OR). A LIKE predicate on a NOCASE
                // column degrades to "SCAN files" here.
                string plan = QueryPlan(db,
                    "DELETE FROM files WHERE Path = @path OR (Path >= @start AND Path < @end);");
                Check(plan.IndexOf("SEARCH files USING", StringComparison.OrdinalIgnoreCase) >= 0
                      && plan.IndexOf("SCAN files", StringComparison.OrdinalIgnoreCase) < 0,
                    "store: the subtree delete seeks the Path index instead of scanning");

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

                store.DeletePathAndPrefix(@"C:\p\a_b");
                Check(store.Query("inside", 300, CancellationToken.None).TotalMatches == 0
                      && store.Query("axb", 300, CancellationToken.None).TotalMatches == 1,
                    "store: a wildcard-looking directory name matches literally, not as a LIKE pattern");

                store.DeletePathAndPrefix(@"C:\");
                Check(store.Query("root", 300, CancellationToken.None).TotalMatches == 0
                      && store.Query("y.txt", 300, CancellationToken.None).TotalMatches == 0,
                    "store: deleting the drive root removes the whole drive");
                Check(store.Query("other", 300, CancellationToken.None).TotalMatches == 1,
                    "store: deleting one drive root leaves other drives alone");
            }
        }

        /// <summary>
        /// Task 10 boundaries: the 300-row cap with one more match than the cap,
        /// name priority over a path-only match, IndexFolders=false still walking
        /// child files, a cancelled full scan keeping the published index, and
        /// null settings arrays / a null FileSearch object normalizing safely.
        /// </summary>
        private static void BoundaryCases()
        {
            string db = Path.Combine(_root, "limit.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                var rows = new List<SearchEntry>(301);
                for (int i = 0; i < 301; i++)
                {
                    string name = "needle" + i.ToString("D3") + ".txt";
                    rows.Add(Entry(@"C:\limit\" + name, name, "txt", false, i, @"C:\limit"));
                }
                store.UpsertScanBatch(rows);
                store.CompleteFullScan(0, 0, 0, "limit");

                var capped = store.Query("needle", 300, CancellationToken.None);
                Check(capped.TotalMatches == 301 && capped.Items.Count == 300 && capped.LimitReached,
                    "store: 301 matches with a 300 limit returns 300 rows, total 301 and the cap flag");
            }

            string rankDb = Path.Combine(_root, "rank.db");
            using (var store = new SearchIndexStore(rankDb))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                store.UpsertScanBatch(new[]
                {
                    Entry(@"C:\p\zzz\readme.txt", "readme.txt", "txt", false, 1, @"C:\p"),
                    Entry(@"C:\p\readme\other.txt", "other.txt", "txt", false, 1, @"C:\p"),
                });
                store.CompleteFullScan(0, 0, 0, "rank");

                var ranked = store.Query("readme", 300, CancellationToken.None);
                Check(ranked.TotalMatches == 2 && ranked.Items.Count == 2
                      && ranked.Items[0].Name == "readme.txt"
                      && ranked.Items[1].Name == "other.txt",
                    "store: a name match ranks before a path-only match");
            }

            string noFoldersTree = Path.Combine(_root, "boundary-nofolders");
            string noFoldersSub = Path.Combine(noFoldersTree, "sub");
            Directory.CreateDirectory(noFoldersSub);
            File.WriteAllText(Path.Combine(noFoldersSub, "inner.txt"), "x");

            var noFolders = FileSearchSettings.CreateDefault();
            noFolders.Roots = new[] { noFoldersTree };
            noFolders.IndexFolders = false;
            string noFoldersDb = Path.Combine(_root, "boundary-nofolders.db");
            using (var store = new SearchIndexStore(noFoldersDb))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                var scan = new DirectoryScanner(store, noFolders).Scan(new[] { noFoldersTree },
                    ScanMode.Full, CancellationToken.None);
                store.CompleteFullScan(scan.SkippedDirectories, scan.SkippedReparsePoints,
                    scan.SkippedLongPaths, noFolders.ComputeFingerprint(new[] { noFoldersTree }));

                Check(scan.Directories == 2 && scan.Files == 1,
                    "scan: IndexFolders=false still walks subdirectories and their files");
                Check(!HasRow(store, noFoldersSub, noFoldersTree),
                    "scan: IndexFolders=false writes no directory row");
                Check(HasRow(store, Path.Combine(noFoldersSub, "inner.txt"), noFoldersTree),
                    "scan: IndexFolders=false indexes the child file");
            }

            string cancelTree = Path.Combine(_root, "boundary-cancel");
            string stopDirectory = Path.Combine(cancelTree, "stop");
            Directory.CreateDirectory(stopDirectory);
            for (int i = 0; i < 60; i++)
            {
                File.WriteAllText(Path.Combine(cancelTree, "cancelprobe" + i.ToString("D2") + ".txt"), "x");
            }
            File.WriteAllText(Path.Combine(stopDirectory, "cancelprobe-stop.txt"), "x");

            var cancelSettings = FileSearchSettings.CreateDefault();
            cancelSettings.Roots = new[] { cancelTree };
            string cancelDb = Path.Combine(_root, "boundary-cancel.db");
            using (var store = new SearchIndexStore(cancelDb))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                store.UpsertScanBatch(new[]
                {
                    Entry(Path.Combine(cancelTree, "legacyneedle.txt"), "legacyneedle.txt", "txt", false, 1, cancelTree)
                });
                store.CompleteFullScan(0, 0, 0, "legacy");

                var cts = new CancellationTokenSource();
                var scanner = new DirectoryScanner(store, cancelSettings, null, path =>
                {
                    if (PathsEqual(path, stopDirectory))
                    {
                        cts.Cancel();
                        return true;
                    }
                    return false;
                });

                store.BeginFullScan();
                // Stage more than one 800-row batch before cancelling, so the
                // "no staged rows published" assertion is not vacuous: a real
                // flush has already written into files_new.
                var staged = new List<SearchEntry>(810);
                for (int i = 0; i < 810; i++)
                {
                    string name = "stagedprobe" + i.ToString("D4") + ".txt";
                    staged.Add(Entry(@"C:\staged\" + name, name, "txt", false, i, @"C:\staged"));
                }
                store.UpsertScanBatch(staged);

                bool cancelled = false;
                try { scanner.Scan(new[] { cancelTree }, ScanMode.Full, cts.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                store.AbortFullScan();

                Check(cancelled, "scan: a mid-walk cancellation aborts the full scan");
                Check(!store.IsFullScanActive, "scan: aborting clears the full-scan staging state");
                Check(store.Query("legacy", 300, CancellationToken.None).TotalMatches == 1,
                    "scan: a cancelled full scan keeps the previously published rows");
                Check(store.Query("stagedprobe", 300, CancellationToken.None).TotalMatches == 0,
                    "scan: a cancelled full scan publishes none of its batched staged rows");
            }

            string configDir = Path.Combine(_root, "boundary-config");
            Directory.CreateDirectory(configDir);
            string main = Path.Combine(configDir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{ ""FileSearch"": null }");
            var cfg = Kobold.Core.AppConfig.Load(main, backup);
            Check(cfg.FileSearch == null, "config: a null FileSearch object loads as unconfigured");

            File.WriteAllText(main, @"{ ""FileSearch"": { ""Configured"": true,
                ""Roots"": null, ""Types"": null, ""ExcludeDirectories"": null } }");
            cfg = Kobold.Core.AppConfig.Load(main, backup);
            Check(cfg.FileSearch != null && cfg.FileSearch.Types != null
                  && cfg.FileSearch.Types.Length == FileSearchSettings.DefaultTypes.Length,
                "config: a null Types array normalizes to the default types");
            Check(cfg.FileSearch != null && cfg.FileSearch.ExcludeDirectories != null
                  && cfg.FileSearch.ExcludeDirectories.Length == 0
                  && cfg.FileSearch.Roots != null && cfg.FileSearch.Roots.Length == 0,
                "config: null Roots/ExcludeDirectories arrays normalize to empty");

            var settings = new FileSearchSettings();
            settings.Types = null;
            settings.ExcludeDirectories = null;
            settings.Roots = null;
            settings.Normalize();
            Check(settings.Types != null && settings.Types.Length == FileSearchSettings.DefaultTypes.Length
                  && settings.ExcludeDirectories != null && settings.ExcludeDirectories.Length == 0
                  && settings.Roots != null && settings.Roots.Length == 0,
                "settings: Normalize repairs null arrays without throwing");
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
                Check(full.Directories == 3 && full.SkippedExcluded == 1 && full.SkippedDirectories == 0,
                    "scan: excluded node_modules increments SkippedExcluded, not SkippedDirectories");
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
                Check(denied.SkippedExcluded == 2 && denied.SkippedDirectories == 0 && denied.Files == 1,
                    "scan: a vetoed directory is counted as excluded, not as a permission skip");
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

                int before = store.Query("child", 50, CancellationToken.None).TotalMatches;
                var linked = new DirectoryScanner(store, settings,
                    isReparsePoint: path => PathsEqual(path, newDirectory))
                    .ScanSubtree(tree, newDirectory, CancellationToken.None);
                Check(linked.Files == 0 && linked.Directories == 0 && linked.SkippedReparsePoints == 1,
                    "subtree: a reparse-point start path is not followed");
                Check(store.Query("child", 50, CancellationToken.None).TotalMatches == before,
                    "subtree: skipping a reparse-point start path leaves the index unchanged");
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
        /// A reconcile must not prune rows under a directory it could not read: the
        /// walk reports the directory, the store marks that subtree as seen, and
        /// CompleteReconcile leaves those rows alone. A normal reconcile still drops
        /// a row that is genuinely gone.
        /// </summary>
        private static void ReconcileUnreadableCase()
        {
            string tree = Path.Combine(_root, "reconcile-unreadable-tree");
            string denied = Path.Combine(tree, "denied");
            Directory.CreateDirectory(denied);
            string hidden = Path.Combine(denied, "hidden.txt");
            File.WriteAllText(hidden, "x");
            string visible = Path.Combine(tree, "visible.txt");
            File.WriteAllText(visible, "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            string db = Path.Combine(_root, "reconcile-unreadable.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                var initial = new DirectoryScanner(store, settings).Scan(new[] { tree }, ScanMode.Full,
                    CancellationToken.None);
                store.CompleteFullScan(initial.SkippedDirectories, initial.SkippedReparsePoints,
                    initial.SkippedLongPaths, settings.ComputeFingerprint(new[] { tree }));
                Check(HasRow(store, hidden, tree),
                    "reconcile unreadable: the denied subtree is indexed before the pass");

                store.BeginReconcile();
                var guarded = new DirectoryScanner(store, settings,
                    isUnreadableDirectory: path => PathsEqual(path, denied));
                var reconcile = guarded.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                Check(reconcile.UnreadableDirectories.Count == 1
                      && PathsEqual(reconcile.UnreadableDirectories[0], denied),
                    "reconcile unreadable: the failed directory is reported by the walk");
                Check(reconcile.SkippedDirectories == 1 && reconcile.SkippedExcluded == 0,
                    "reconcile unreadable: an open failure counts as a directory skip, not an exclusion");

                store.MarkSeenPathsUnder(reconcile.UnreadableDirectories);
                store.CompleteReconcile(new[] { tree });
                Check(HasRow(store, hidden, tree),
                    "reconcile unreadable: rows under the unreadable directory survive completion");
                Check(HasRow(store, visible, tree),
                    "reconcile unreadable: normal rows are still seen and kept");

                string gone = Path.Combine(tree, "genuinely-gone.txt");
                store.Upsert(Entry(gone, "genuinely-gone.txt", "txt", false, 1, tree));
                store.BeginReconcile();
                var normal = new DirectoryScanner(store, settings).Scan(new[] { tree }, ScanMode.Reconcile,
                    CancellationToken.None);
                store.MarkSeenPathsUnder(normal.UnreadableDirectories);
                store.CompleteReconcile(new[] { tree });
                Check(!HasRow(store, gone, tree),
                    "reconcile unreadable: a normal reconcile still drops a genuinely missing row");
            }
        }

        /// <summary>
        /// The reconcile UPSERT only updates a row whose values changed. A trigger on
        /// the files table logs every UPDATE, so an unchanged tree must log zero and
        /// a changed size exactly one.
        /// </summary>
        private static void ReconcileWritesOnlyDifferencesCase()
        {
            string tree = Path.Combine(_root, "diff-tree");
            Directory.CreateDirectory(tree);
            string file = Path.Combine(tree, "diff-probe.txt");
            File.WriteAllText(file, "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            string db = Path.Combine(_root, "diff.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                var full = new DirectoryScanner(store, settings).Scan(new[] { tree }, ScanMode.Full,
                    CancellationToken.None);
                store.CompleteFullScan(full.SkippedDirectories, full.SkippedReparsePoints, full.SkippedLongPaths,
                    settings.ComputeFingerprint(new[] { tree }));

                using (var connection = OpenSqlite(db))
                {
                    ExecuteSql(connection, "CREATE TABLE update_log (Id INTEGER PRIMARY KEY AUTOINCREMENT);");
                    ExecuteSql(connection,
                        "CREATE TRIGGER files_updated AFTER UPDATE ON files " +
                        "BEGIN INSERT INTO update_log (Id) VALUES (NULL); END;");
                }

                var scanner = new DirectoryScanner(store, settings);
                store.BeginReconcile();
                scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                store.CompleteReconcile(new[] { tree });
                Check(ScalarCount(db, "SELECT COUNT(*) FROM update_log;") == 0,
                    "reconcile diff: an unchanged tree performs zero row updates");

                File.WriteAllText(file, "more bytes than the first scan saw");
                store.BeginReconcile();
                scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                store.CompleteReconcile(new[] { tree });
                Check(ScalarCount(db, "SELECT COUNT(*) FROM update_log;") == 1,
                    "reconcile diff: a changed size performs exactly one row update");
            }
        }

        /// <summary>
        /// The reconcile session is one connection and one transaction over a whole
        /// pass: children are read per directory, a commit publishes everything and a
        /// dispose without one leaves the index exactly as it was.
        /// </summary>
        private static void ReconcileSessionCases()
        {
            string tree = SearchPath.Normalize(Path.Combine(_root, "session-tree"));
            string sub = Path.Combine(tree, "Sub");
            string deep = Path.Combine(sub, "Deep");
            Directory.CreateDirectory(deep);
            string a = Path.Combine(tree, "a.txt");
            string b = Path.Combine(sub, "b.txt");
            string c = Path.Combine(deep, "c.txt");
            File.WriteAllText(a, "a");
            File.WriteAllText(b, "b");
            File.WriteAllText(c, "c");

            string db = Path.Combine(_root, "session.db");
            string rolledBack = Path.Combine(tree, "rolled-back.txt");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();

                using (var session = store.OpenReconcileSession())
                {
                    session.Upsert(Entry(tree, "session-tree", string.Empty, true, 0, tree));
                    session.Upsert(Entry(sub, "Sub", string.Empty, true, 0, tree));
                    session.Upsert(Entry(deep, "Deep", string.Empty, true, 0, tree));
                    session.Upsert(Entry(a, "a.txt", "txt", false, 1, tree));
                    session.Upsert(Entry(b, "b.txt", "txt", false, 2, tree));
                    session.Upsert(Entry(c, "c.txt", "txt", false, 3, tree));
                    session.Commit();
                }

                using (var session = store.OpenReconcileSession())
                {
                    bool threw = false;
                    try
                    {
                        using (var second = store.OpenReconcileSession())
                        {
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        threw = true;
                    }
                    Check(threw, "session: a second concurrent session throws instead of sharing the connection");
                }

                using (var session = store.OpenReconcileSession())
                {
                    var children = session.ReadChildren(tree, CancellationToken.None);
                    Check(children.Count == 2 && HasEntry(children, a) && HasEntry(children, sub)
                          && !HasEntry(children, deep) && !HasEntry(children, c),
                        "session: ReadChildren returns the direct children only (never grandchildren)");

                    var mixed = session.ReadChildren(tree.ToUpperInvariant(), CancellationToken.None);
                    Check(mixed.Count == 2 && HasEntry(mixed, a),
                        "session: ReadChildren matches mixed-case directory paths");

                    // The walk normalizes a directory without its trailing separator, but
                    // ReadChildren is called with whatever the caller has: both forms must
                    // read the same direct children (a "C:\\" root keeps its one separator).
                    var trailing = session.ReadChildren(tree + "\\", CancellationToken.None);
                    Check(trailing.Count == children.Count && HasEntry(trailing, a) && HasEntry(trailing, sub),
                        "session: ReadChildren treats a trailing separator like no separator");

                    var cancelled = new CancellationToken(true);
                    bool tokenThrew = false;
                    try { session.ReadChildren(tree, cancelled); }
                    catch (OperationCanceledException) { tokenThrew = true; }
                    Check(tokenThrew, "session: ReadChildren honours the pass token");
                }

                // A drive root keeps its own separator: "C:\" must not turn into "C:\\".
                using (var session = store.OpenReconcileSession())
                {
                    session.Upsert(Entry(@"C:\session-probe.txt", "session-probe.txt", "txt", false, 1, @"C:\"));
                    session.Commit();
                }
                using (var session = store.OpenReconcileSession())
                {
                    Check(HasEntry(session.ReadChildren(@"C:\", CancellationToken.None), @"C:\session-probe.txt"),
                        "session: a drive-root child is read from the drive's own range");
                }

                string added = Path.Combine(tree, "added.txt");
                using (var session = store.OpenReconcileSession())
                {
                    session.Upsert(Entry(added, "added.txt", "txt", false, 4, tree));
                    session.Upsert(Entry(a, "a.txt", "txt", false, 99, tree));
                    session.Commit();
                }
                Check(HasPath(store, added) && RowSize(store, a) == 99,
                    "session: Upsert inserts a new row and updates a changed one");

                long before = ScalarCount(db, "SELECT COUNT(*) FROM files;");
                using (var session = store.OpenReconcileSession())
                {
                    session.Upsert(Entry(rolledBack, "rolled-back.txt", "txt", false, 7, tree));
                    session.Upsert(Entry(a, "a.txt", "txt", false, 777, tree));
                    // Deliberately no Commit: the whole pass must disappear.
                }
                Check(ScalarCount(db, "SELECT COUNT(*) FROM files;") == before
                      && RowSize(store, a) == 99 && !HasPath(store, rolledBack),
                    "session: dispose without commit rolls the whole pass back");

                using (var session = store.OpenReconcileSession())
                {
                    session.Delete(c);
                    session.Commit();
                }
                Check(!HasPath(store, c) && HasPath(store, b),
                    "session: Delete removes one row and nothing else");

                using (var session = store.OpenReconcileSession())
                {
                    session.DeletePathAndPrefix(sub);
                    session.Commit();
                }
                Check(!HasPath(store, sub) && !HasPath(store, b) && HasPath(store, a),
                    "session: DeletePathAndPrefix removes the subtree and nothing beside it");
            }

            using (var reopened = new SearchIndexStore(db))
            {
                reopened.EnsureSchema();
                Check(HasPath(reopened, a) && !HasPath(reopened, c) && !HasPath(reopened, rolledBack),
                    "session: a commit is durable across a reopen and a rollback leaves no trace");
            }
        }

        /// <summary>
        /// The reconcile diff path: an unchanged tree writes nothing at all, and each
        /// kind of change writes exactly the row it touched. The fake records both the
        /// session calls and the legacy batch/seen writes, so "writes nothing" also
        /// fails when the walk falls back to upserting everything.
        /// </summary>
        private static void ReconcileScannerDiffCases()
        {
            string tree = SearchPath.Normalize(Path.Combine(_root, "diff-scan-tree"));
            string sub = Path.Combine(tree, "sub");
            Directory.CreateDirectory(sub);
            string a = Path.Combine(tree, "a.txt");
            File.WriteAllText(a, "hello");
            File.WriteAllText(Path.Combine(sub, "b.txt"), "hi");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };

            var store = new DiffStore();
            SeedChildren(store, tree, tree);
            var scanner = new DirectoryScanner(store, settings, reconcileSession: store);
            var walk = scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);

            Check(walk.Files == 2 && walk.Directories == 2,
                "reconcile diff: the walk still counts the files and directories it saw");
            Check(store.Upserts.Count == 0 && store.Deletes.Count == 0 && store.PrefixDeletes.Count == 0
                  && store.BatchedRows == 0 && store.SeenRows == 0,
                "reconcile diff: an unchanged tree writes nothing");

            // A new file in the tree root: creating it only touches the root's own
            // mtime, and the root row is not indexed, so this is exactly one upsert.
            store = new DiffStore();
            SeedChildren(store, tree, tree);
            string added = Path.Combine(tree, "added.txt");
            File.WriteAllText(added, "new");
            scanner = new DirectoryScanner(store, settings, reconcileSession: store);
            scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
            Check(store.Upserts.Count == 1 && PathsEqual(store.Upserts[0].Path, added)
                  && store.Deletes.Count == 0 && store.PrefixDeletes.Count == 0,
                "reconcile diff: a new file is exactly one upsert");
            File.Delete(added);

            store = new DiffStore();
            SeedChildren(store, tree, tree);
            File.WriteAllText(a, "much longer content than hello");
            scanner = new DirectoryScanner(store, settings, reconcileSession: store);
            scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
            Check(store.Upserts.Count == 1 && PathsEqual(store.Upserts[0].Path, a)
                  && store.Deletes.Count == 0 && store.PrefixDeletes.Count == 0,
                "reconcile diff: a size/mtime change is exactly one upsert");

            store = new DiffStore();
            string doomed = Path.Combine(tree, "doomed.txt");
            File.WriteAllText(doomed, "x");
            SeedChildren(store, tree, tree);
            File.Delete(doomed);
            scanner = new DirectoryScanner(store, settings, reconcileSession: store);
            scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
            Check(store.Deletes.Count == 1 && PathsEqual(store.Deletes[0], doomed)
                  && store.Upserts.Count == 0 && store.PrefixDeletes.Count == 0,
                "reconcile diff: a deleted file is exactly one delete");

            store = new DiffStore();
            SeedChildren(store, tree, tree);
            Directory.Delete(sub, true);
            scanner = new DirectoryScanner(store, settings, reconcileSession: store);
            scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
            Check(store.PrefixDeletes.Count == 1 && PathsEqual(store.PrefixDeletes[0], sub)
                  && store.Deletes.Count == 0 && store.Upserts.Count == 0,
                "reconcile diff: a deleted child directory is exactly one prefix delete");

            // Unreadable directory: its rows stay because the walk never saw them.
            string guardedTree = SearchPath.Normalize(Path.Combine(_root, "diff-scan-guarded"));
            string denied = Path.Combine(guardedTree, "denied");
            Directory.CreateDirectory(denied);
            File.WriteAllText(Path.Combine(denied, "kept.txt"), "x");
            string ghost = Path.Combine(denied, "vanished.txt");
            string ghostDir = Path.Combine(denied, "vanished-dir");

            var guardedSettings = FileSearchSettings.CreateDefault();
            guardedSettings.Roots = new[] { guardedTree };

            store = new DiffStore();
            SeedChildren(store, guardedTree, guardedTree);
            store.Children[denied].Add(Entry(ghost, "vanished.txt", "txt", false, 1, guardedTree));
            store.Children[denied].Add(Entry(ghostDir, "vanished-dir", string.Empty, true, 0, guardedTree));
            var guarded = new DirectoryScanner(store, guardedSettings,
                isUnreadableDirectory: path => PathsEqual(path, denied), reconcileSession: store);
            var guardedWalk = guarded.Scan(new[] { guardedTree }, ScanMode.Reconcile, CancellationToken.None);
            Check(guardedWalk.UnreadableDirectories.Count == 1,
                "reconcile diff: the unreadable directory is still reported by the walk");
            Check(store.Deletes.Count == 0 && store.PrefixDeletes.Count == 0,
                "reconcile diff: an unreadable directory prunes nothing");

            // Control: a readable walk of the same tree prunes those rows, so the pin
            // above is not vacuous.
            var control = new DiffStore();
            SeedChildren(control, guardedTree, guardedTree);
            control.Children[denied].Add(Entry(ghost, "vanished.txt", "txt", false, 1, guardedTree));
            var plain = new DirectoryScanner(control, guardedSettings, reconcileSession: control);
            plain.Scan(new[] { guardedTree }, ScanMode.Reconcile, CancellationToken.None);
            Check(control.Deletes.Count == 1 && PathsEqual(control.Deletes[0], ghost),
                "reconcile diff: a readable directory prunes the vanished file");
        }

        /// <summary>
        /// A non-BMP (surrogate-pair) name was missed by the old prefix-range
        /// <c>ReadChildren</c>, so a deleted emoji file kept its row and a deleted emoji
        /// directory leaked its whole subtree. The read is now one parent-key equality
        /// seek, and the prunes run against the real store so the SQL lookup and the
        /// walk are proven together.
        /// </summary>
        private static void ReconcileEmojiNameCases()
        {
            string tree = SearchPath.Normalize(Path.Combine(_root, "session-emoji-tree"));
            Directory.CreateDirectory(tree);
            string emojiFile = Path.Combine(tree, "\U0001F600.txt");
            File.WriteAllText(emojiFile, "x");
            string emojiDir = Path.Combine(tree, "\U0001F642");
            string emojiChild = Path.Combine(emojiDir, "inside.txt");
            Directory.CreateDirectory(emojiDir);
            File.WriteAllText(emojiChild, "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };

            string db = Path.Combine(_root, "session-emoji.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                using (var session = store.OpenReconcileSession())
                {
                    session.Upsert(DiskEntry(tree, emojiFile));
                    session.Upsert(DiskEntry(tree, emojiDir));
                    session.Upsert(DiskEntry(tree, emojiChild));
                    session.Commit();
                }

                using (var session = store.OpenReconcileSession())
                {
                    var children = session.ReadChildren(tree, CancellationToken.None);
                    Check(HasEntry(children, emojiFile) && HasEntry(children, emojiDir),
                        "session: ReadChildren returns non-BMP (emoji) direct children");
                }

                File.Delete(emojiFile);
                using (var counted = new CountingSession(store.OpenReconcileSession()))
                {
                    new DirectoryScanner(store, settings, reconcileSession: counted)
                        .Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                    Check(counted.Deletes.Count == 1 && PathsEqual(counted.Deletes[0], emojiFile)
                          && counted.PrefixDeletes.Count == 0,
                        "session: a deleted non-BMP file is exactly one delete");
                    counted.Commit();
                }
                Check(!HasPath(store, emojiFile), "session: the deleted non-BMP file row is gone");

                Directory.Delete(emojiDir, true);
                using (var counted = new CountingSession(store.OpenReconcileSession()))
                {
                    new DirectoryScanner(store, settings, reconcileSession: counted)
                        .Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                    Check(counted.PrefixDeletes.Count == 1 && PathsEqual(counted.PrefixDeletes[0], emojiDir)
                          && counted.Deletes.Count == 0,
                        "session: a deleted non-BMP directory is exactly one prefix delete");
                    counted.Commit();
                }
                Check(!HasPath(store, emojiDir) && !HasPath(store, emojiChild),
                    "session: a deleted non-BMP directory takes its indexed subtree with it");
            }
        }

        /// <summary>
        /// A settings exclusion is a decision: the rows a previous pass indexed under
        /// the folder are stale and are pruned by the prefix delete, like the legacy
        /// root-wide anti-join did. A veto (pause/stop here) is transient and must keep
        /// them, otherwise pressing pause would silently drop rows.
        /// </summary>
        private static void ReconcileExcludedDirectoryCases()
        {
            string tree = SearchPath.Normalize(Path.Combine(_root, "excluded-tree"));
            string kept = Path.Combine(tree, "kept");
            string excluded = Path.Combine(tree, "sub");
            Directory.CreateDirectory(kept);
            Directory.CreateDirectory(excluded);
            File.WriteAllText(Path.Combine(kept, "kept.txt"), "x");
            File.WriteAllText(Path.Combine(excluded, "cache.txt"), "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            settings.ExcludeDirectories = new[] { excluded };

            // ReadChildren(tree) is canned: the excluded child plus its old descendant
            // rows, which only a prefix delete can remove because the walk never reads
            // (or walks) the excluded directory.
            Func<DiffStore> seeded = () =>
            {
                var store = new DiffStore();
                store.Children[tree] = new List<SearchEntry>
                {
                    DiskEntry(tree, excluded), DiskEntry(tree, kept)
                };
                store.Children[excluded] = new List<SearchEntry>
                {
                    Entry(Path.Combine(excluded, "cache.txt"), "cache.txt", "txt", false, 1, tree)
                };
                store.Children[kept] = new List<SearchEntry>
                {
                    DiskEntry(tree, Path.Combine(kept, "kept.txt"))
                };
                return store;
            };

            var excludedStore = seeded();
            var walk = new DirectoryScanner(excludedStore, settings, reconcileSession: excludedStore)
                .Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
            Check(walk.SkippedExcluded == 1 && walk.Directories == 2,
                "reconcile excluded: the excluded child is skipped and the kept one is walked");
            Check(excludedStore.PrefixDeletes.Count == 1 && PathsEqual(excludedStore.PrefixDeletes[0], excluded)
                  && excludedStore.Deletes.Count == 0 && excludedStore.Upserts.Count == 0,
                "reconcile excluded: an excluded child directory is exactly one prefix delete");

            // Same tree, same canned rows, but the directory is vetoed (pause/stop
            // here) instead of excluded: it keeps everything it has indexed.
            var vetoSettings = FileSearchSettings.CreateDefault();
            vetoSettings.Roots = new[] { tree };

            var vetoedStore = seeded();
            new DirectoryScanner(vetoedStore, vetoSettings,
                    isForbiddenDirectory: path => PathsEqual(path, excluded), reconcileSession: vetoedStore)
                .Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
            Check(vetoedStore.PrefixDeletes.Count == 0 && vetoedStore.Deletes.Count == 0
                  && vetoedStore.Upserts.Count == 0,
                "reconcile excluded: a vetoed directory keeps its indexed rows");
        }

        /// <summary>
        /// A default that now excludes a cache tree must also clean the rows an older
        /// pass indexed under it: the reconcile prune is the only repair, and it must
        /// fire for a directory the walk never reads (the fake session supplies the
        /// parent's listing, because the real cache tree is outside the scratch root).
        /// </summary>
        private static void ReconcileNewDefaultExcludedCase()
        {
            string excluded;
            if (!TryCreateStoreScratch("LocalCache", out excluded))
            {
                Console.WriteLine("SKIP search: reconcile prunes rows under a newly default-excluded path (Packages is not writable)");
                return;
            }

            string packageRoot = Path.GetDirectoryName(excluded);
            string tree = SearchPath.Normalize(Path.Combine(_root, "cache-excluded-tree"));
            Directory.CreateDirectory(tree);

            try
            {
                var store = new DiffStore();
                store.Children[tree] = new List<SearchEntry> { DiskEntry(tree, excluded) };
                store.Children[excluded] = new List<SearchEntry>
                {
                    Entry(Path.Combine(excluded, "state.dat"), "state.dat", "dat", false, 1, tree)
                };

                var settings = FileSearchSettings.CreateDefault();
                settings.Roots = new[] { tree };

                var scanner = new DirectoryScanner(store, settings, reconcileSession: store);
                scanner.EnumerateForChecks = path => PathsEqual(path, tree)
                    ? new FileSystemInfo[] { new DirectoryInfo(excluded) }
                    : null;

                var walk = scanner.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                Check(walk.SkippedExcluded == 1 && walk.Directories == 1,
                    "search: the newly default-excluded cache path is skipped by the walk");
                Check(store.PrefixDeletes.Count == 1 && PathsEqual(store.PrefixDeletes[0], excluded)
                      && store.Deletes.Count == 0 && store.Upserts.Count == 0,
                    "search: reconcile prunes rows under a newly default-excluded path");
            }
            finally
            {
                TryDeleteTree(packageRoot);
            }
        }

        /// <summary>
        /// The upsert writes Root, so an otherwise unchanged row indexed under a stale
        /// root must be repaired; otherwise a settings change that re-homes a path would
        /// leave the old root on the row forever.
        /// </summary>
        private static void ReconcileRootRepairCase()
        {
            string tree = SearchPath.Normalize(Path.Combine(_root, "root-repair-tree"));
            Directory.CreateDirectory(tree);
            string file = Path.Combine(tree, "root-probe.txt");
            File.WriteAllText(file, "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };

            var store = new DiffStore();
            SearchEntry stale = DiskEntry(tree, file);
            stale.Root = @"C:\old-root";
            store.Children[tree] = new List<SearchEntry> { stale };

            new DirectoryScanner(store, settings, reconcileSession: store)
                .Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);

            Check(store.Upserts.Count == 1 && PathsEqual(store.Upserts[0].Path, file)
                  && PathsEqual(store.Upserts[0].Root, tree)
                  && store.Deletes.Count == 0 && store.PrefixDeletes.Count == 0,
                "reconcile diff: a stale Root on an otherwise unchanged row is repaired");
        }

        /// <summary>
        /// With folder rows disabled a child directory writes no row of its own. A row
        /// an earlier pass indexed (folder rows were on) must be dropped on its own: if
        /// it stayed in the parent's map the prune would prefix-delete the whole subtree
        /// and the child's own walk would rewrite every file in it.
        /// </summary>
        private static void ReconcileNoFolderRowsCase()
        {
            string tree = SearchPath.Normalize(Path.Combine(_root, "nofolders-diff-tree"));
            string sub = Path.Combine(tree, "sub");
            Directory.CreateDirectory(sub);
            string top = Path.Combine(tree, "top.txt");
            string inside = Path.Combine(sub, "inside.txt");
            File.WriteAllText(top, "x");
            File.WriteAllText(inside, "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            settings.IndexFolders = false;

            var store = new DiffStore();
            store.Children[tree] = new List<SearchEntry> { DiskEntry(tree, sub), DiskEntry(tree, top) };
            store.Children[sub] = new List<SearchEntry> { DiskEntry(tree, inside) };

            var walk = new DirectoryScanner(store, settings, reconcileSession: store)
                .Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);

            Check(walk.Files == 2 && walk.Directories == 2,
                "reconcile no-folders: the walk still counts both files and both directories");
            Check(store.PrefixDeletes.Count == 0 && store.Upserts.Count == 0,
                "reconcile no-folders: the parent prune does not prefix-delete the child subtree");
            Check(store.Deletes.Count == 1 && PathsEqual(store.Deletes[0], sub),
                "reconcile no-folders: only the stale child-directory row itself is deleted");
        }

        /// <summary>
        /// A listing that fails half-way has not seen the rest of its contents, so a
        /// reconcile must prune nothing for it (the <c>listedCompletely</c> branch). The
        /// scanner hook replaces the real enumeration, because a directory that is
        /// revoked mid-read cannot be produced on demand.
        /// </summary>
        private static void ReconcilePartialListingCase()
        {
            string tree = SearchPath.Normalize(Path.Combine(_root, "partial-listing-tree"));
            Directory.CreateDirectory(tree);
            string seen = Path.Combine(tree, "seen.txt");
            File.WriteAllText(seen, "x");
            string ghost = Path.Combine(tree, "ghost.txt");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };

            Func<DiffStore> seeded = () =>
            {
                var store = new DiffStore();
                store.Children[tree] = new List<SearchEntry>
                {
                    DiskEntry(tree, seen), Entry(ghost, "ghost.txt", "txt", false, 1, tree)
                };
                return store;
            };

            var partialStore = seeded();
            var partial = new DirectoryScanner(partialStore, settings, reconcileSession: partialStore);
            partial.EnumerateForChecks = path => PartialListing(seen);
            var walk = partial.Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);

            Check(walk.UnreadableDirectories.Count == 1 && PathsEqual(walk.UnreadableDirectories[0], tree),
                "reconcile partial: a half-listed directory is reported as unreadable");
            Check(partialStore.Deletes.Count == 0 && partialStore.PrefixDeletes.Count == 0,
                "reconcile partial: a half-listed directory prunes nothing");
            Check(partialStore.Upserts.Count == 0,
                "reconcile partial: the rows it did list are still diffed (no rewrite)");

            // Control: the same rows are pruned by a complete listing, so the pin above
            // is not vacuous.
            var control = seeded();
            new DirectoryScanner(control, settings, reconcileSession: control)
                .Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
            Check(control.Deletes.Count == 1 && PathsEqual(control.Deletes[0], ghost),
                "reconcile partial: a complete listing prunes the row the half-listing missed");
        }

        /// <summary>
        /// The per-directory diff only prunes inside directories it actually walks, so a
        /// row outside every configured root (another drive, a folder the user just
        /// removed from the settings) survives a reconcile. The legacy root-wide
        /// anti-join deleted it. The same pass still prunes inside the tree, so the pin
        /// is not vacuous.
        /// </summary>
        private static void ReconcileOrphanRowCase()
        {
            string tree = SearchPath.Normalize(Path.Combine(_root, "orphan-tree"));
            string outside = SearchPath.Normalize(Path.Combine(_root, "orphan-outside"));
            Directory.CreateDirectory(tree);
            Directory.CreateDirectory(outside);
            string baseFile = Path.Combine(tree, "orphan-base.txt");
            File.WriteAllText(baseFile, "x");
            string orphan = Path.Combine(outside, "orphan-outside.txt");
            string staleInside = Path.Combine(tree, "orphan-stale.txt");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };

            string db = Path.Combine(_root, "orphan.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                using (var session = store.OpenReconcileSession())
                {
                    session.Upsert(DiskEntry(tree, baseFile));
                    session.Upsert(Entry(orphan, "orphan-outside.txt", "txt", false, 1, outside));
                    session.Upsert(Entry(staleInside, "orphan-stale.txt", "txt", false, 1, tree));
                    session.Commit();
                }

                using (var session = store.OpenReconcileSession())
                {
                    new DirectoryScanner(store, settings, reconcileSession: session)
                        .Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                    session.Commit();
                }

                Check(HasPath(store, orphan),
                    "reconcile orphan: a row outside every walked directory survives the pass");
                Check(!HasPath(store, staleInside),
                    "reconcile orphan: the same pass still prunes inside the walked tree");
            }
        }

        /// <summary>
        /// The headline safety property of a session pass: what it already wrote is
        /// rolled back when the token fires mid-walk, so the index is exactly what it was
        /// before the pass. The veto callback is the deterministic cancel point the walk
        /// already exposes (the reconcile itself raises no progress events, so a
        /// progress-driven cancel is not available at store level).
        /// </summary>
        private static void ReconcileCancelRollbackCase()
        {
            string tree = SearchPath.Normalize(Path.Combine(_root, "rolled-tree"));
            string sub = Path.Combine(tree, "sub");
            Directory.CreateDirectory(sub);
            string kept = Path.Combine(tree, "kept.txt");
            string subFile = Path.Combine(sub, "sub.txt");
            File.WriteAllText(kept, "x");
            File.WriteAllText(subFile, "x");
            string gone = Path.Combine(tree, "gone.txt");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };

            string db = Path.Combine(_root, "rolled.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                using (var session = store.OpenReconcileSession())
                {
                    session.Upsert(DiskEntry(tree, kept));
                    session.Upsert(DiskEntry(tree, sub));
                    session.Upsert(DiskEntry(tree, subFile));
                    session.Upsert(Entry(gone, "gone.txt", "txt", false, 1, tree));
                    session.Commit();
                }

                var source = new CancellationTokenSource();
                int directoryChecks = 0;
                using (var session = store.OpenReconcileSession())
                {
                    var scanner = new DirectoryScanner(store, settings,
                        isForbiddenDirectory: path =>
                        {
                            // Root first, then sub: by the second check the root's diff
                            // has already issued the delete of "gone.txt" inside the
                            // session's transaction, so the rollback has real work.
                            if (++directoryChecks == 2) source.Cancel();
                            return false;
                        },
                        reconcileSession: session);

                    bool threw = false;
                    try { scanner.Scan(new[] { tree }, ScanMode.Reconcile, source.Token); }
                    catch (OperationCanceledException) { threw = true; }
                    Check(threw, "reconcile cancel: the walk observes a mid-pass cancel");
                    // No Commit on purpose: disposing rolls the whole pass back.
                }
                Check(HasPath(store, gone),
                    "reconcile cancel: a delete already issued by the pass is rolled back");

                using (var session = store.OpenReconcileSession())
                {
                    new DirectoryScanner(store, settings, reconcileSession: session)
                        .Scan(new[] { tree }, ScanMode.Reconcile, CancellationToken.None);
                    session.Commit();
                }
                Check(!HasPath(store, gone),
                    "reconcile cancel: an uncancelled pass does prune the stale row (control)");
            }
        }

        /// <summary>
        /// Fills a fake session with the tree as it is on disk: the rows a completed
        /// scan of this tree would have indexed. The entries come from the production
        /// factory, so name/size/timestamp match what the walk produces.
        /// </summary>
        private static void SeedChildren(DiffStore store, string root, string directory)
        {
            var rows = new List<SearchEntry>();
            foreach (string path in Directory.GetFileSystemEntries(directory))
            {
                FileSystemInfo info = Directory.Exists(path)
                    ? (FileSystemInfo)new DirectoryInfo(path)
                    : new FileInfo(path);

                SearchEntry entry;
                if (SearchEntry.TryCreate(root, info, out entry) && entry != null) rows.Add(entry);
            }
            store.Children[directory] = rows;

            foreach (string child in Directory.GetDirectories(directory))
            {
                SeedChildren(store, root, child);
            }
        }

        private static bool HasEntry(IReadOnlyList<SearchEntry> rows, string path)
        {
            if (rows == null) return false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null && PathsEqual(rows[i].Path, path)) return true;
            }
            return false;
        }


        /// <summary>A crashed full scan can leave files_new behind; reopening the store drops it.</summary>
        private static void StaleStagingTableCase()
        {
            string db = Path.Combine(_root, "stale-staging.db");
            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                store.BeginFullScan();
                // Deliberately no Abort/Complete: simulate a process that died mid-scan.
            }

            using (var store = new SearchIndexStore(db))
            {
                store.EnsureSchema();
                Check(!TableExists(db, "files_new"),
                    "store: EnsureSchema drops a leftover files_new staging table");
                Check(!store.IsFullScanActive,
                    "store: a reopened store is not left in a full-scan state");
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
        /// The stat diet and the per-path coalescing, asserted against a fake store:
        /// an excluded path never reaches it, an included path is exactly one write,
        /// a dotted directory still falls back to the folder gate, and a burst on one
        /// path collapses to one upsert. Events are injected synchronously, and files
        /// are created before Start, so no pin races the OS watcher. Counts are
        /// filtered by the injected path: the live watcher on the same root may write
        /// rows the pin does not own.
        /// </summary>
        private static void WatcherStatDietCases()
        {
            string root = Path.Combine(_root, "watch-diet-root");
            Directory.CreateDirectory(root);

            // A cache path the defaults exclude must cost the store nothing. The event
            // has to name an *indexable* existing file, otherwise it would be dropped
            // by the type filter or the vanished-file fallback with no exclusion at
            // all. It also has to sit in a Store volatile child: the package directory
            // and its LocalState are in scope by design.
            string storeCacheDir;
            if (!TryCreateStoreScratch("LocalCache", out storeCacheDir))
            {
                Console.WriteLine("SKIP search: an excluded watcher event never reaches the store (Packages is not writable)");
            }
            else
            {
                string storePackage = Path.GetDirectoryName(storeCacheDir);
                try
                {
                    string cacheFile = Path.Combine(storeCacheDir, "state.txt");
                    File.WriteAllText(cacheFile, "x");

                    var excludedStore = new DiffStore();
                    using (var host = new FileSystemWatcherHost(excludedStore, SettingsForRoot(storePackage)))
                    {
                        host.Start(new[] { storePackage });
                        host.ApplyBatchForChecks(new[]
                        {
                            WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, cacheFile)
                        });
                    }

                    Check(CountUpserts(excludedStore, cacheFile) == 0
                          && CountDeletes(excludedStore, cacheFile) == 0
                          && CountPrefixDeletes(excludedStore, cacheFile) == 0,
                        "search: an excluded watcher event never reaches the store");
                }
                finally
                {
                    TryDeleteTree(storePackage);
                }
            }

            // Control for the pin above: the same file name in a scratch directory
            // outside Packages reaches the store exactly once, so the exclusion is the
            // only difference between the two events.
            string controlDir = Path.Combine(root, "zebrawatch-store-control");
            Directory.CreateDirectory(controlDir);
            string controlFile = Path.Combine(controlDir, "state.txt");
            File.WriteAllText(controlFile, "x");
            var controlStore = new DiffStore();
            using (var host = new FileSystemWatcherHost(controlStore, SettingsForRoot(root)))
            {
                host.Start(new[] { root });
                host.ApplyBatchForChecks(new[]
                {
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, controlFile)
                });
            }
            Check(CountUpserts(controlStore, controlFile) == 1,
                "search: the same file name outside Packages is upserted once");

            string readme = Path.Combine(root, "zebrawatch-README");
            File.WriteAllText(readme, "x");
            var readmeStore = new DiffStore();
            using (var host = new FileSystemWatcherHost(readmeStore, SettingsForRoot(root)))
            {
                host.Start(new[] { root });
                host.ApplyBatchForChecks(new[]
                {
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, readme)
                });
            }
            SearchEntry readmeEntry = UpsertFor(readmeStore, readme);
            Check(readmeEntry != null && !readmeEntry.IsDirectory
                  && CountUpserts(readmeStore, readme) == 1
                  && CountDeletes(readmeStore, readme) == 0,
                "search: an extensionless file event still upserts");

            // A name with a dot is a directory too: the string type check says no, so
            // the single stat fallback must still index it while folders are on.
            string dotted = Path.Combine(root, "zebrawatch-v1.2");
            Directory.CreateDirectory(dotted);
            var dottedStore = new DiffStore();
            using (var host = new FileSystemWatcherHost(dottedStore, SettingsForRoot(root)))
            {
                host.Start(new[] { root });
                host.ApplyBatchForChecks(new[]
                {
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, dotted)
                });
            }
            SearchEntry dottedEntry = UpsertFor(dottedStore, dotted);
            Check(dottedEntry != null && dottedEntry.IsDirectory && CountUpserts(dottedStore, dotted) == 1,
                "search: a dotted directory event still upserts when folders are indexed");

            // A directory whose leaf looks like an included file goes the other way:
            // the string check says yes and the post-stat folder gate must still write
            // a folder row, not a file row and not nothing.
            string reportDir = Path.Combine(root, "zebrawatch-report.txt");
            Directory.CreateDirectory(reportDir);
            var reportStore = new DiffStore();
            using (var host = new FileSystemWatcherHost(reportStore, SettingsForRoot(root)))
            {
                host.Start(new[] { root });
                host.ApplyBatchForChecks(new[]
                {
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, reportDir)
                });
            }
            SearchEntry reportEntry = UpsertFor(reportStore, reportDir);
            Check(reportEntry != null && reportEntry.IsDirectory
                  && CountUpserts(reportStore, reportDir) == 1,
                "search: a directory whose name carries an included extension is indexed as a folder");

            // Control: an extensionless directory with folder rows disabled passes the
            // string type check and is then dropped by the post-stat gate - no row and
            // no delete either.
            string plainDir = Path.Combine(root, "zebrawatch-dirnofolders");
            Directory.CreateDirectory(plainDir);
            var noFolderSettings = SettingsForRoot(root);
            noFolderSettings.IndexFolders = false;
            var noFolderStore = new DiffStore();
            using (var host = new FileSystemWatcherHost(noFolderStore, noFolderSettings))
            {
                host.Start(new[] { root });
                host.ApplyBatchForChecks(new[]
                {
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, plainDir)
                });
            }
            Check(CountUpserts(noFolderStore, plainDir) == 0
                  && CountDeletes(noFolderStore, plainDir) == 0
                  && CountPrefixDeletes(noFolderStore, plainDir) == 0,
                "search: a folder event writes nothing when folder rows are disabled");

            // One path, two events, one batch: the last event wins.
            string burst = Path.Combine(root, "zebrawatch-burst.txt");
            File.WriteAllText(burst, "x");
            var burstStore = new DiffStore();
            using (var host = new FileSystemWatcherHost(burstStore, SettingsForRoot(root)))
            {
                host.Start(new[] { root });
                host.ApplyBatchForChecks(new[]
                {
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, burst),
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Changed, burst)
                });
            }
            Check(CountUpserts(burstStore, burst) == 1 && CountDeletes(burstStore, burst) == 0,
                "search: a burst on one path is applied once");

            // A Deleted superseded by a later Created collapses to the Created; when the
            // path no longer exists that is the vanished fallback, which must drop the
            // subtree rows a directory took with it, not just one row.
            string vanished = Path.Combine(root, "zebrawatch-gone.txt");
            var vanishedStore = new DiffStore();
            using (var host = new FileSystemWatcherHost(vanishedStore, SettingsForRoot(root)))
            {
                host.Start(new[] { root });
                host.ApplyBatchForChecks(new[]
                {
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Deleted, vanished),
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, vanished)
                });
            }
            Check(CountPrefixDeletes(vanishedStore, vanished) == 1
                  && CountDeletes(vanishedStore, vanished) == 0,
                "search: a vanished path deletes its subtree, not one row");

            // Coalescing must keep a Renamed event even when the destination also
            // arrives as Created in the same batch: the rename carries the old-path
            // delete and the subtree rescan, the Created carries neither. Injected
            // synchronously, so this is the batch and not an OS timing race.
            string renameOld = Path.Combine(root, "zebrawatch-renamesrc");
            Directory.CreateDirectory(renameOld);
            string renameChildOld = Path.Combine(renameOld, "zebrawatch-renamechild.txt");
            File.WriteAllText(renameChildOld, "x");
            string renameNew = Path.Combine(root, "zebrawatch-renamedst");
            string renameChildNew = Path.Combine(renameNew, "zebrawatch-renamechild.txt");
            Directory.Move(renameOld, renameNew);

            var renameStore = new DiffStore();
            using (var host = new FileSystemWatcherHost(renameStore, SettingsForRoot(root)))
            {
                host.Start(new[] { root });
                host.ApplyBatchForChecks(new[]
                {
                    RenameEvent(renameNew, renameOld),
                    WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, renameNew)
                });
            }
            Check(CountPrefixDeletes(renameStore, renameOld) == 1
                  && CountUpserts(renameStore, renameChildNew) == 1
                  && UpsertFor(renameStore, renameNew) != null
                  && CountUpserts(renameStore, renameChildOld) == 0,
                "search: a rename keeps its subtree rescan when a Created for the destination rides along");
        }

        /// <summary>
        /// Creates <c>%LocalAppData%\Packages\KoboldFileSearchCheck-&lt;guid&gt;\&lt;leaf&gt;</c>.
        /// False instead of an exception when Packages is missing or unwritable, so the
        /// Store-app pins skip on a locked-down machine. The caller deletes the whole
        /// package directory in a finally; the recognizable prefix and the finally
        /// keep a crashed run from leaving a stray probe behind.
        /// </summary>
        private static bool TryCreateStoreScratch(string leaf, out string path)
        {
            path = null;
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (local.Length == 0) return false;

                string packageRoot = Path.Combine(local, "Packages",
                    "KoboldFileSearchCheck-" + Guid.NewGuid().ToString("N"));
                string candidate = Path.Combine(packageRoot, leaf);
                Directory.CreateDirectory(candidate);
                path = candidate;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void TryDeleteTree(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { Directory.Delete(path, true); }
            catch (Exception) { }
        }

        private static SearchEntry UpsertFor(DiffStore store, string path)
        {
            foreach (SearchEntry entry in store.Upserts)
            {
                if (PathsEqual(entry.Path, path)) return entry;
            }
            return null;
        }

        private static int CountUpserts(DiffStore store, string path)
        {
            int count = 0;
            foreach (SearchEntry entry in store.Upserts)
            {
                if (PathsEqual(entry.Path, path)) count++;
            }
            return count;
        }

        private static int CountDeletes(DiffStore store, string path)
        {
            return CountPaths(store.Deletes, path);
        }

        private static int CountPrefixDeletes(DiffStore store, string path)
        {
            return CountPaths(store.PrefixDeletes, path);
        }

        private static int CountPaths(List<string> paths, string path)
        {
            int count = 0;
            foreach (string candidate in paths)
            {
                if (PathsEqual(candidate, path)) count++;
            }
            return count;
        }

        private static FileSearchSettings SettingsForRoot(string root)
        {
            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { root };
            return settings;
        }

        private static FileSystemWatcherHost.WatcherEvent WatchEvent(
            FileSystemWatcherHost.WatcherEventKind kind, string path)
        {
            return new FileSystemWatcherHost.WatcherEvent(kind, path, null);
        }

        private static FileSystemWatcherHost.WatcherEvent RenameEvent(string path, string oldPath)
        {
            return new FileSystemWatcherHost.WatcherEvent(
                FileSystemWatcherHost.WatcherEventKind.Renamed, path, oldPath);
        }

        /// <summary>
        /// The service side of the watcher: Ready starts incremental updates, a
        /// reconcile requested through the same path the timer uses removes a row
        /// the watcher never saw while keeping the rows it did index, and pause /
        /// resume stops and restarts the watcher. A scan whose configured roots all
        /// resolve to nothing ends NoIndex instead of publishing an empty index.
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

                // A row the watcher never saw (a file deleted while the watcher was
                // down) must be removed by the reconcile; real rows must survive. The
                // row lives inside the walked tree: a per-directory diff only prunes
                // what a directory it actually visited reports as missing.
                string deadPath = Path.Combine(tree, "zebrawatch-dead-" + Guid.NewGuid().ToString("N") + ".txt");
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
                Check(service.Status.State == FileSearchState.NoIndex
                      && !service.ReadStatus().HasIndex,
                    "service watch: a scan with no resolvable root ends NoIndex and publishes nothing");
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

        /// <summary>
        /// A full scan whose configured roots all resolve to nothing must not publish
        /// the empty staging table: the old index keeps serving and the status says
        /// Error (or NoIndex when nothing was ever published).
        /// </summary>
        private static void ServiceNoRootsCase()
        {
            string db = Path.Combine(_root, "service-noroots.db");
            string tree = Path.Combine(_root, "service-noroots-tree");
            Directory.CreateDirectory(tree);
            string probe = Path.Combine(tree, "noroots-probe.txt");
            File.WriteAllText(probe, "x");

            var good = FileSearchSettings.CreateDefault();
            good.Roots = new[] { tree };
            good.Configured = true;
            good.Enabled = true;

            using (var service = new FileSearchIndexService(db))
            {
                service.ApplySettings(good);
                service.StartFullScan(good, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready && HasRow(service, probe),
                    "service no roots: the baseline index is published");

                var missing = FileSearchSettings.CreateDefault();
                missing.Roots = new[] { Path.Combine(_root, "noroots-missing-" + Guid.NewGuid().ToString("N")) };
                missing.Configured = true;
                missing.Enabled = true;

                service.StartFullScan(missing, null);
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Error
                      || service.Status.State == FileSearchState.NoIndex,
                    "service no roots: a scan with no resolvable root ends Error/NoIndex");
                Check(HasRow(service, probe),
                    "service no roots: the previous index is not replaced by an empty one");
            }
        }

        /// <summary>
        /// A user cancel ends NoIndex when nothing was published and Ready (on the old
        /// index) otherwise; Pause keeps its own Paused ending. The old rows survive
        /// and no staged row is published.
        /// </summary>
        private static void ServiceCancelCases()
        {
            string tree = Path.Combine(_root, "service-cancel-tree");
            Directory.CreateDirectory(tree);
            File.WriteAllText(Path.Combine(tree, "cancel-base.txt"), "x");

            var settings = FileSearchSettings.CreateDefault();
            settings.Roots = new[] { tree };
            settings.Configured = true;
            settings.Enabled = true;
            settings.ReconcileMinutes = 0;

            // No published index yet: a cancel must end NoIndex and stay startable.
            string emptyDb = Path.Combine(_root, "service-cancel-empty.db");
            using (var service = new FileSearchIndexService(emptyDb))
            {
                bool cancelled = false;
                EventHandler<ScanProgress> cancelOnProgress = (s, p) =>
                {
                    if (cancelled) return;
                    cancelled = true;
                    service.CancelScan();
                };
                service.ProgressChanged += cancelOnProgress;
                service.StartFullScan(settings, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                service.ProgressChanged -= cancelOnProgress;

                Check(cancelled, "service cancel: the first scan reports progress before finishing");
                Check(service.Status.State == FileSearchState.NoIndex,
                    "service cancel: a cancelled first scan ends NoIndex");
                Check(service.Status.ScanCancelled,
                    "service cancel: the status reports the user cancellation");
                Check(!service.ReadStatus().HasIndex,
                    "service cancel: no index was published by the cancelled first scan");

                service.StartFullScan(settings, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready && !service.Status.ScanCancelled,
                    "service cancel: the service starts normally after a NoIndex cancel");
            }

            // An old index: a cancel ends Ready, keeps its rows and publishes nothing.
            string db = Path.Combine(_root, "service-cancel.db");
            using (var service = new FileSearchIndexService(db))
            {
                service.ApplySettings(settings);
                service.StartFullScan(settings, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready,
                    "service cancel: the baseline scan is ready");

                File.WriteAllText(Path.Combine(tree, "cancel-new.txt"), "x");
                bool cancelled = false;
                EventHandler<ScanProgress> cancelOnProgress = (s, p) =>
                {
                    if (cancelled) return;
                    cancelled = true;
                    service.CancelScan();
                };
                service.ProgressChanged += cancelOnProgress;
                service.StartFullScan(settings, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                service.ProgressChanged -= cancelOnProgress;

                Check(cancelled && service.Status.State == FileSearchState.Ready,
                    "service cancel: a cancel with an old index ends Ready");
                Check(service.Status.ScanCancelled,
                    "service cancel: the Ready status still reports the cancellation");
                Check(HasRow(service, Path.Combine(tree, "cancel-base.txt")),
                    "service cancel: the old index rows survive the cancel");
                Check(service.Search("cancel-new", 50, CancellationToken.None).TotalMatches == 0
                      && service.Search("cancel-base", 50, CancellationToken.None).TotalMatches == 1,
                    "service cancel: no staged row is published and the old rows stay searchable");
            }

            // Pause during an active scan keeps the Paused ending.
            string pauseDb = Path.Combine(_root, "service-cancel-pause.db");
            using (var service = new FileSearchIndexService(pauseDb))
            {
                service.ApplySettings(settings);
                service.StartFullScan(settings, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));

                bool paused = false;
                EventHandler<ScanProgress> pauseOnProgress = (s, p) =>
                {
                    if (paused) return;
                    paused = true;
                    service.Pause();
                };
                service.ProgressChanged += pauseOnProgress;
                service.StartFullScan(settings, new[] { tree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                service.ProgressChanged -= pauseOnProgress;

                Check(paused && service.Status.State == FileSearchState.Paused,
                    "service cancel: Pause during a scan still ends Paused");
                Check(!service.Status.ScanCancelled,
                    "service cancel: a pause is not reported as a user cancel");
            }

            // A cancelled reconcile must roll back, keep last_reconcile_utc and end
            // Ready. The session writes one transaction, so a cancel before the commit
            // leaves the index exactly as it was (the legacy batch path could leave
            // half-applied rows behind).
            string reconcileDb = Path.Combine(_root, "service-reconcile-cancel.db");
            string reconcileTree = Path.Combine(_root, "service-reconcile-cancel-tree");
            Directory.CreateDirectory(reconcileTree);
            File.WriteAllText(Path.Combine(reconcileTree, "reccancel-base.txt"), "x");

            var reconcileSettings = FileSearchSettings.CreateDefault();
            reconcileSettings.Roots = new[] { reconcileTree };
            reconcileSettings.Configured = true;
            reconcileSettings.Enabled = true;
            reconcileSettings.ReconcileMinutes = 0;

            using (var service = new FileSearchIndexService(reconcileDb))
            {
                service.ApplySettings(reconcileSettings);
                service.StartFullScan(reconcileSettings, new[] { reconcileTree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                Check(service.Status.State == FileSearchState.Ready,
                    "service reconcile cancel: the baseline scan is ready");

                // A completed pass first, so the timestamp the second pass must preserve
                // is a real one instead of null.
                service.RequestReconcile();
                service.WaitForIdle(TimeSpan.FromSeconds(30));
                DateTime? committedAt = service.ReadStatus().LastReconcileUtc;
                Check(committedAt.HasValue && service.Status.State == FileSearchState.Ready,
                    "service reconcile cancel: a completed pass records last_reconcile_utc");

                string stale = Path.Combine(reconcileTree, "reccancel-stale.txt");
                using (var side = new SearchIndexStore(reconcileDb))
                {
                    side.Upsert(Entry(stale, "reccancel-stale.txt", "txt", false, 1,
                        SearchPath.Normalize(reconcileTree)));
                }
                Check(HasRow(service, stale),
                    "service reconcile cancel: the stale row is indexed before the pass");

                // The reconcile walk reports no ProgressChanged of its own (it has no
                // progress sink), so the cancel hangs off the Reconciling transition -
                // the first deterministic moment of the pass.
                bool cancelObserved = false;
                EventHandler<FileSearchStatus> cancelOnReconciling = (sender, status) =>
                {
                    if (status.State != FileSearchState.Reconciling || cancelObserved) return;
                    cancelObserved = true;
                    service.CancelScan();
                };
                service.StatusChanged += cancelOnReconciling;
                service.RequestReconcile();
                service.WaitForIdle(TimeSpan.FromSeconds(30));
                service.StatusChanged -= cancelOnReconciling;

                Check(cancelObserved && service.Status.State == FileSearchState.Ready,
                    "service reconcile cancel: a cancelled reconcile ends Ready");
                Check(HasRow(service, stale),
                    "service reconcile cancel: the cancelled pass leaves the index unchanged");
                Check(service.ReadStatus().LastReconcileUtc == committedAt,
                    "service reconcile cancel: a cancelled pass does not move last_reconcile_utc");
            }

            // The same pass interrupted by Pause ends Paused and is rolled back too.
            string pausedDb = Path.Combine(_root, "service-reconcile-pause.db");
            string pausedTree = Path.Combine(_root, "service-reconcile-pause-tree");
            Directory.CreateDirectory(pausedTree);
            File.WriteAllText(Path.Combine(pausedTree, "recpause-base.txt"), "x");

            var pausedSettings = FileSearchSettings.CreateDefault();
            pausedSettings.Roots = new[] { pausedTree };
            pausedSettings.Configured = true;
            pausedSettings.Enabled = true;
            pausedSettings.ReconcileMinutes = 0;

            using (var service = new FileSearchIndexService(pausedDb))
            {
                service.ApplySettings(pausedSettings);
                service.StartFullScan(pausedSettings, new[] { pausedTree });
                service.WaitForIdle(TimeSpan.FromSeconds(20));
                service.RequestReconcile();
                service.WaitForIdle(TimeSpan.FromSeconds(30));
                DateTime? committedAt = service.ReadStatus().LastReconcileUtc;
                Check(committedAt.HasValue,
                    "service reconcile pause: the baseline reconcile records last_reconcile_utc");

                string stale = Path.Combine(pausedTree, "recpause-stale.txt");
                using (var side = new SearchIndexStore(pausedDb))
                {
                    side.Upsert(Entry(stale, "recpause-stale.txt", "txt", false, 1,
                        SearchPath.Normalize(pausedTree)));
                }
                Check(HasRow(service, stale),
                    "service reconcile pause: the stale row is indexed before the pass");

                bool pauseObserved = false;
                EventHandler<FileSearchStatus> pauseOnReconciling = (sender, status) =>
                {
                    if (status.State != FileSearchState.Reconciling || pauseObserved) return;
                    pauseObserved = true;
                    service.Pause();
                };
                service.StatusChanged += pauseOnReconciling;
                service.RequestReconcile();
                service.WaitForIdle(TimeSpan.FromSeconds(30));
                service.StatusChanged -= pauseOnReconciling;

                Check(pauseObserved && service.Status.State == FileSearchState.Paused,
                    "service reconcile pause: Pause during a reconcile ends Paused");
                Check(HasRow(service, stale),
                    "service reconcile pause: the paused pass leaves the index unchanged");
                Check(service.ReadStatus().LastReconcileUtc == committedAt,
                    "service reconcile pause: a paused pass does not move last_reconcile_utc");
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

        private static SqliteConnection OpenSqlite(string databasePath)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            return connection;
        }

        private static void ExecuteSql(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static long ScalarCount(string databasePath, string sql)
        {
            using (var connection = OpenSqlite(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        /// <summary>One statement's query plan, one detail string per plan row.</summary>
        private static string QueryPlan(string databasePath, string sql)
        {
            var details = new List<string>();
            using (var connection = OpenSqlite(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read()) details.Add(reader.GetString(3));
                }
            }
            return string.Join(" | ", details);
        }

        /// <summary>One meta value of the index, or null when the key is absent.</summary>
        private static string MetaValue(string databasePath, string key)
        {
            using (var connection = OpenSqlite(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Value FROM meta WHERE Key = @key;";
                command.Parameters.AddWithValue("@key", key);
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? null
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Stored Parent of one exact row, or null when the row (or, on a pre-migration
        /// index, the column) is absent. A missing column is a failed pin, not an abort.
        /// </summary>
        private static string RowParent(string databasePath, string path)
        {
            try
            {
                using (var connection = OpenSqlite(databasePath))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Parent FROM files WHERE Path = @path;";
                    command.Parameters.AddWithValue("@path", path);
                    object value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value
                        ? null
                        : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            }
            catch (SqliteException)
            {
                return null;
            }
        }

        private static bool TableExists(string databasePath, string name)
        {
            using (var connection = OpenSqlite(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
                command.Parameters.AddWithValue("@name", name);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
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

        /// <summary>
        /// A real session with a call log. The store-level pins need the real
        /// <c>ReadChildren</c> (one parent-key equality seek) but must count what the
        /// walk wrote; this delegates every write and records it.
        /// </summary>
        private sealed class CountingSession : IReconcileSession
        {
            private readonly IReconcileSession _inner;

            public readonly List<SearchEntry> Upserts = new List<SearchEntry>();
            public readonly List<string> Deletes = new List<string>();
            public readonly List<string> PrefixDeletes = new List<string>();

            public CountingSession(IReconcileSession inner)
            {
                _inner = inner;
            }

            public IReadOnlyList<SearchEntry> ReadChildren(string directory, CancellationToken token)
            {
                return _inner.ReadChildren(directory, token);
            }

            public void Upsert(SearchEntry entry)
            {
                Upserts.Add(entry);
                _inner.Upsert(entry);
            }

            public void Delete(string path)
            {
                Deletes.Add(path);
                _inner.Delete(path);
            }

            public void DeletePathAndPrefix(string path)
            {
                PrefixDeletes.Add(path);
                _inner.DeletePathAndPrefix(path);
            }

            public void Commit()
            {
                _inner.Commit();
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }

        /// <summary>Index entry for a path that exists on disk, exactly as a scan builds it.</summary>
        private static SearchEntry DiskEntry(string root, string path)
        {
            FileSystemInfo info = Directory.Exists(path)
                ? (FileSystemInfo)new DirectoryInfo(path)
                : (FileSystemInfo)new FileInfo(path);

            SearchEntry entry;
            if (!SearchEntry.TryCreate(root, info, out entry) || entry == null)
                throw new InvalidOperationException("Could not build an index entry for " + path);
            return entry;
        }

        /// <summary>
        /// A listing that yields its first entry and then fails, which is what a real
        /// directory does when it is revoked or disappears half-way through.
        /// </summary>
        private static IEnumerable<FileSystemInfo> PartialListing(string first)
        {
            yield return new FileInfo(first);
            throw new IOException("the listing was revoked after the first entry");
        }

        /// <summary>
        /// In-memory store and reconcile session for the diff pins. Every write of
        /// either path is recorded - the session calls and the legacy batch/seen
        /// calls - so a pin can prove that an unchanged tree writes nothing at all.
        /// </summary>
        private sealed class DiffStore : ISearchIndexStore, IReconcileSession
        {
            public readonly Dictionary<string, List<SearchEntry>> Children =
                new Dictionary<string, List<SearchEntry>>(StringComparer.OrdinalIgnoreCase);

            public readonly List<SearchEntry> Upserts = new List<SearchEntry>();
            public readonly List<string> Deletes = new List<string>();
            public readonly List<string> PrefixDeletes = new List<string>();
            public int BatchedRows;
            public int SeenRows;

            public string DatabasePath { get { return string.Empty; } }
            public bool IsFullScanActive { get { return false; } }

            public void Dispose() { }
            public void EnsureSchema() { }
            public IReconcileSession OpenReconcileSession() { return this; }
            public void BeginFullScan() { }
            public void CompleteFullScan(long skippedDirectories, long skippedReparsePoints,
                long skippedLongPaths, string fingerprint) { }
            public void AbortFullScan() { }
            public void BeginReconcile() { }
            public void MarkSeenPathsUnder(IReadOnlyList<string> prefixes) { }
            public void CompleteReconcile(IReadOnlyList<string> roots) { }
            public void AbortReconcile() { }
            public void Upsert(SearchEntry entry) { Upserts.Add(entry); }
            public void Delete(string path) { Deletes.Add(path); }
            public void DeletePathAndPrefix(string path) { PrefixDeletes.Add(path); }
            public void Commit() { }
            public void Checkpoint() { }
            public void Vacuum() { }
            public long GetDatabaseSizeBytes() { return 0; }
            public IndexStatus ReadStatus() { return new IndexStatus(); }

            public void UpsertScanBatch(IReadOnlyList<SearchEntry> entries)
            {
                if (entries != null) BatchedRows += entries.Count;
            }

            public void MarkSeenBatch(IReadOnlyList<string> paths)
            {
                if (paths != null) SeenRows += paths.Count;
            }

            public IReadOnlyList<SearchEntry> ReadChildren(string directory, CancellationToken token)
            {
                List<SearchEntry> rows;
                if (Children.TryGetValue(directory, out rows)) return rows;
                return new SearchEntry[0];
            }

            public SearchQueryResult Query(string text, int limit, CancellationToken token)
            {
                return SearchQueryResult.Empty;
            }
        }
    }
}
