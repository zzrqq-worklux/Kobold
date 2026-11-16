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
            { ".pptx", ".xlsx", ".docx", ".md", ".txt", ".json", ".csv", ".log", ".html", ".xml", ".ini", ".bat", ".ps1" };
        public const string DefaultExtension = ".txt";

        /// <summary>Typed extension → one leading dot, lower-case; blank → ""; letters/digits/'-' only, else null.
        /// Rename keeps the typed/case-preserved body when preserveCase is true.</summary>
        public static string NormalizeExtension(string raw, bool preserveCase = false)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string body = raw.Trim().TrimStart('.');
            if (body.Length == 0) return "";
            foreach (char c in body)
                if (!char.IsLetterOrDigit(c) && c != '-') return null;
            return "." + (preserveCase ? body : body.ToLowerInvariant());
        }

        /// <summary>
        /// Splits a file name into the stem the rename box edits and the suffix it
        /// keeps locked. Only the last dot counts, a leading dot belongs to a
        /// dotfile, and a trailing dot is part of the stem - so the extension
        /// shown in the lock is exactly the part a rename must not touch.
        /// </summary>
        public static void Split(string fileName, out string stem, out string extension)
        {
            string value = fileName ?? "";
            int dot = value.LastIndexOf('.');
            if (dot <= 0 || dot == value.Length - 1)
            {
                stem = value;
                extension = "";
                return;
            }
            stem = value.Substring(0, dot);
            extension = value.Substring(dot);
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
