using System;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Decides whether a file belongs in the index. "All files" wins over the
    /// list, a name without an extension follows IndexExtensionless, and a
    /// multi-dot name is classified by its last extension only.
    /// </summary>
    public static class SearchTypeFilter
    {
        public static bool IsFileIncluded(string path, FileSearchSettings settings)
        {
            if (settings == null) return false;
            if (settings.IndexAllFiles) return true;

            string extension = SearchPath.GetExtension(path);
            if (extension.Length == 0) return settings.IndexExtensionless;

            string[] types = settings.Types;
            if (types == null) return false;

            for (int i = 0; i < types.Length; i++)
            {
                if (string.Equals(types[i], extension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
