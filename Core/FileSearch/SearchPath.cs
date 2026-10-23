using System;
using System.IO;
using System.Text;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Pure path helpers for the file-search index. The index stores plain
    /// (non-extended) paths; the <c>\\?\</c> form is only used for the actual
    /// filesystem call, so paths longer than MAX_PATH still take part.
    /// Kept free of UI and storage dependencies.
    /// </summary>
    public static class SearchPath
    {
        private const string ExtendedPrefix = @"\\?\";
        private const string ExtendedUncPrefix = @"\\?\UNC\";

        private static readonly char[] Separators = { '\\', '/' };

        /// <summary>
        /// Absolute, plain form of a path: resolves . and .., drops the extended
        /// prefix and the trailing separator (except on a drive root). Input that
        /// the runtime refuses to normalize is kept as it came in.
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string value = path.Trim();

            // "C:" means "the current directory on C:" to GetFullPath; a configured
            // root or exclusion that names a drive always means the drive root.
            if (value.Length == 2 && value[1] == ':') value = value + "\\";

            try
            {
                value = Path.GetFullPath(value);
            }
            catch (Exception)
            {
                // ArgumentException / NotSupportedException / PathTooLongException /
                // SecurityException: keep the caller's text instead of failing.
            }

            return TrimTrailingSeparators(FromExtended(value));
        }

        /// <summary>Filesystem form of a path: <c>C:\x</c> becomes <c>\\?\C:\x</c>, a share becomes <c>\\?\UNC\...</c>.</summary>
        public static string ToExtended(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            if (path.StartsWith(ExtendedPrefix, StringComparison.OrdinalIgnoreCase)) return path;

            // A relative path has no device form; leave it to the caller.
            if (!Path.IsPathRooted(path)) return path;

            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return ExtendedUncPrefix + path.Substring(2);
            }
            return ExtendedPrefix + path;
        }

        /// <summary>Plain form of a path: removes the <c>\\?\</c> or <c>\\?\UNC\</c> prefix.</summary>
        public static string FromExtended(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(ExtendedUncPrefix.Length);
            }
            if (path.StartsWith(ExtendedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(ExtendedPrefix.Length);
            }
            return path;
        }

        /// <summary>
        /// Lowercase extension without the dot, or "" when the name has none.
        /// Parsed by hand because Path.GetExtension rejects characters that NTFS
        /// allows in a name (a quote, for one), and a scan must not die on those.
        /// </summary>
        public static string GetExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            int separator = path.LastIndexOfAny(Separators, path.Length - 1);
            int dot = path.LastIndexOf('.', path.Length - 1);

            // No dot, or a dot that starts the name (".gitignore") - not an extension.
            if (dot <= separator + 1) return string.Empty;

            return path.Substring(dot + 1).ToLowerInvariant();
        }

        /// <summary>
        /// True when <paramref name="path"/> is <paramref name="ancestor"/> itself or
        /// lives below it. The comparison is case-insensitive and stops at a separator,
        /// so <c>C:\p</c> never claims <c>C:\privateer</c>.
        /// </summary>
        public static bool IsUnder(string path, string ancestor)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ancestor)) return false;

            string child = TrimTrailingSeparators(FromExtended(path));
            string parent = TrimTrailingSeparators(FromExtended(ancestor));
            if (child.Length == 0 || parent.Length == 0) return false;

            if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;

            string prefix = parent.EndsWith("\\", StringComparison.Ordinal) ? parent : parent + "\\";
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Escapes the LIKE wildcards and the escape character itself, for use with
        /// <c>LIKE @pattern ESCAPE '\'</c>.
        /// </summary>
        public static string EscapeLike(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var escaped = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                if (c == '\\' || c == '%' || c == '_') escaped.Append('\\');
                escaped.Append(c);
            }
            return escaped.ToString();
        }

        private static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            string trimmed = path.TrimEnd(Separators);
            if (trimmed.Length == 0) return path;

            // Trimming "C:\" must not leave "C:", which means the current directory.
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
            return trimmed;
        }
    }
}
