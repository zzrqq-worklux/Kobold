using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// Pure helpers for the panel's path menu: the labels shown for each browsed
    /// level, from the widget root down to the folder on screen. Kept free of UI
    /// so tests/FolderListingCheck can cover the drive-root and trailing-separator
    /// cases that would otherwise produce empty rows.
    /// </summary>
    public static class BrowsePath
    {
        /// <summary>
        /// Label for one browsed level: the folder's own name, or the full path
        /// when it has none (a drive root - a UNC share root keeps its share name).
        /// </summary>
        public static string Label(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            // A trailing separator would make GetFileName return "" for an ordinary
            // folder, so trim it first - but fall back to the original path, because
            // trimming a drive root leaves "C:" which is not a sensible label.
            string trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                          System.IO.Path.AltDirectorySeparatorChar);
            string name = System.IO.Path.GetFileName(trimmed);

            return string.IsNullOrEmpty(name) ? path : name;
        }

        /// <summary>
        /// Labels for the whole chain, root first: the widget's own name followed
        /// by every path on the browse stack (top-down).
        /// </summary>
        public static List<string> Ancestry(string rootLabel, IList<string> stack)
        {
            var labels = new List<string> { rootLabel ?? string.Empty };

            if (stack != null)
            {
                foreach (var path in stack) labels.Add(Label(path));
            }

            return labels;
        }

        /// <summary>
        /// Header text for one menu row. WPF menu items read a single underscore as
        /// an access-key marker, so a folder named "my_project" would show up as
        /// "myproject"; doubling it escapes the underscore.
        /// </summary>
        public static string MenuHeader(string label)
        {
            return string.IsNullOrEmpty(label) ? label : label.Replace("_", "__");
        }
    }
}
