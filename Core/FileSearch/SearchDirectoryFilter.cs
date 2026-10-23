using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Decides whether a directory is out of scope. Names match at any depth
    /// (a <c>node_modules</c> five levels down is still cache); full paths match
    /// the directory and its whole subtree, with a separator boundary so
    /// <c>C:\p\private</c> never claims <c>C:\p\privateer</c>.
    /// Kept free of UI and storage dependencies.
    /// </summary>
    public static class SearchDirectoryFilter
    {
        /// <summary>Cache, recycle and virtualisation directories that never hold user files.</summary>
        public static readonly string[] DefaultExcludedNames =
        {
            "$Recycle.Bin", "System Volume Information", "node_modules", ".git", ".svn",
            "blob_storage", "WXWorkCefCache", "WasmTtsEngine", "GPUCache", "Code Cache",
            "npm-cache", "NuGet", "pip", "Cargo"
        };

        /// <summary>
        /// OS directories that are large, volatile or inaccessible when a whole
        /// drive is scanned. %TEMP% normally resolves to
        /// <c>C:\Users\&lt;user&gt;\AppData\Local\Temp</c>.
        /// </summary>
        public static readonly string[] DefaultExcludedPaths = BuildDefaultExcludedPaths();

        private static readonly char[] Separators = { '\\', '/' };

        // The scanner asks per directory, so normalizing (GetFullPath) every user
        // entry on every call would dominate a full scan. The settings arrays are
        // replaced by Normalize, never edited in place, so the array itself is the key.
        private static readonly ConditionalWeakTable<string[], UserExclusions> UserCache =
            new ConditionalWeakTable<string[], UserExclusions>();

        /// <summary>User entries split once into names (match at any depth) and normalized paths.</summary>
        private sealed class UserExclusions
        {
            public static readonly UserExclusions Empty = new UserExclusions(new string[0], new string[0]);

            public UserExclusions(string[] names, string[] paths)
            {
                Names = names;
                Paths = paths;
            }

            public string[] Names { get; private set; }
            public string[] Paths { get; private set; }
        }

        /// <summary>
        /// True when the directory at <paramref name="path"/> (with the given leaf
        /// <paramref name="name"/>) is excluded by the defaults or by the user's
        /// additions. The caller stops descending on true.
        /// </summary>
        public static bool IsExcluded(string path, string name, FileSearchSettings settings)
        {
            string plainPath = SearchPath.FromExtended(path);
            string leaf = string.IsNullOrEmpty(name) ? LeafOf(plainPath) : name;

            if (plainPath.Length == 0 && string.IsNullOrEmpty(leaf)) return false;

            if (MatchesName(plainPath, leaf, DefaultExcludedNames)) return true;
            if (plainPath.Length > 0 && MatchesPath(plainPath, DefaultExcludedPaths)) return true;

            UserExclusions extra = GetUserExclusions(settings == null ? null : settings.ExcludeDirectories);
            if (MatchesName(plainPath, leaf, extra.Names)) return true;
            if (plainPath.Length > 0 && MatchesPath(plainPath, extra.Paths)) return true;

            return false;
        }

        private static UserExclusions GetUserExclusions(string[] configured)
        {
            if (configured == null || configured.Length == 0) return UserExclusions.Empty;
            return UserCache.GetValue(configured, BuildUserExclusions);
        }

        private static UserExclusions BuildUserExclusions(string[] configured)
        {
            var names = new List<string>(configured.Length);
            var paths = new List<string>(configured.Length);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in configured)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string candidate = TrimEntry(raw.Trim());
                if (candidate.Length == 0) continue;

                // A bare name applies at any depth. Anything with a separator is a
                // directory path, normalized like every path it is compared against.
                if (candidate.IndexOfAny(Separators) < 0)
                {
                    if (seenNames.Add(candidate)) names.Add(candidate);
                    continue;
                }

                string normalized = SearchPath.Normalize(candidate);
                if (normalized.Length > 0 && seenPaths.Add(normalized)) paths.Add(normalized);
            }

            return new UserExclusions(names.ToArray(), paths.ToArray());
        }

        /// <summary>
        /// Trailing separators are decoration on a user entry, but trimming a drive
        /// root to "C:" would turn it into a relative path, so that one keeps its slash.
        /// </summary>
        private static string TrimEntry(string value)
        {
            string trimmed = value.TrimEnd(Separators);
            if (trimmed.Length == 0) return string.Empty;
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
            return trimmed;
        }

        private static bool MatchesName(string path, string leaf, string[] names)
        {
            if (names == null) return false;

            for (int i = 0; i < names.Length; i++)
            {
                if (MatchesName(path, leaf, names[i])) return true;
            }
            return false;
        }

        private static bool MatchesName(string path, string leaf, string excluded)
        {
            if (string.IsNullOrEmpty(excluded)) return false;

            if (string.Equals(leaf, excluded, StringComparison.OrdinalIgnoreCase)) return true;
            return path.Length > 0 && HasSegment(path, excluded);
        }

        private static bool MatchesPath(string path, string[] excludedPaths)
        {
            if (excludedPaths == null) return false;

            for (int i = 0; i < excludedPaths.Length; i++)
            {
                string excluded = excludedPaths[i];
                if (string.IsNullOrEmpty(excluded)) continue;
                if (SearchPath.IsUnder(path, excluded)) return true;
            }
            return false;
        }

        private static bool HasSegment(string path, string name)
        {
            int index = 0;
            while (index < path.Length)
            {
                while (index < path.Length && IsSeparator(path[index])) index++;
                int start = index;
                while (index < path.Length && !IsSeparator(path[index])) index++;

                int length = index - start;
                if (length == name.Length &&
                    string.Compare(path, start, name, 0, length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string LeafOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            int cut = path.LastIndexOfAny(Separators);
            if (cut < 0 || cut + 1 >= path.Length) return path;
            return path.Substring(cut + 1);
        }

        private static bool IsSeparator(char c)
        {
            return c == '\\' || c == '/';
        }

        private static string[] BuildDefaultExcludedPaths()
        {
            var paths = new List<string>(5);
            try
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windows))
                {
                    AddPath(paths, Path.Combine(windows, "WinSxS"));
                    AddPath(paths, Path.Combine(windows, "System32", "config"));
                    AddPath(paths, Path.Combine(windows, "ServiceProfiles"));
                    AddPath(paths, Path.Combine(windows, "Temp"));
                }
                AddPath(paths, Path.GetTempPath());
            }
            catch (Exception)
            {
                // A locked-down shell can deny the special-folder lookup. The name
                // list still applies; a failed static initializer would not.
            }

            return paths.ToArray();
        }

        private static void AddPath(List<string> paths, string path)
        {
            string normalized = SearchPath.Normalize(path);
            if (normalized.Length > 0) paths.Add(normalized);
        }
    }
}
