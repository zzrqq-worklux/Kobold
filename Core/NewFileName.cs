using System;

namespace Kobold.Core
{
    /// <summary>
    /// Pure naming rules for the new-file dialog: which extensions the picker
    /// offers, how a typed extension normalizes, and how the final file name
    /// combines a stem with an extension (append, never double). Kept free of
    /// UI so tests/ShellOpsCheck can pin the dot/case/dedupe cases.
    /// </summary>
    public static class NewFileName
    {
        public static readonly string[] CommonExtensions =
            { ".txt", ".md", ".json", ".csv", ".log", ".html", ".xml", ".ini", ".bat", ".ps1" };
        public const string DefaultExtension = ".txt";

        /// <summary>Typed extension → one leading dot, lower-case; blank → ""; letters/digits/'-' only, else null.</summary>
        public static string NormalizeExtension(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string body = raw.Trim().TrimStart('.');
            if (body.Length == 0) return "";
            foreach (char c in body)
                if (!char.IsLetterOrDigit(c) && c != '-') return null;
            return "." + body.ToLowerInvariant();
        }

        /// <summary>Final name: append the extension unless the name already ends with it (case-insensitive).</summary>
        public static string Compose(string name, string extension)
        {
            string stem = name ?? "";
            string ext = extension ?? "";
            if (ext.Length == 0) return stem;
            return stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? stem : stem + ext;
        }
    }
}
