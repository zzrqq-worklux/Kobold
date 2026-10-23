using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Kobold.Core.FileSearch;
using Microsoft.Win32.SafeHandles;

namespace Kobold.FileSearchCheck
{
    /// <summary>
    /// Checks for the file-search core layer: settings defaults, path helpers,
    /// type filter, directory exclusions, index entries and root resolution.
    /// Pure logic - no UI and no SQLite.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/FileSearchCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            _root = Path.Combine(Path.GetTempPath(), "kobold-filesearch-" + Guid.NewGuid().ToString("N"));
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

        private static bool PathsEqual(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
