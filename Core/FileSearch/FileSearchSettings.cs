using System;
using System.Collections.Generic;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Global file-search configuration. Serialized with the rest of AppConfig;
    /// a null object in the config means "not configured yet". Kept free of UI,
    /// storage and SQLite types so tests/FileSearchCheck can cover the rules.
    /// </summary>
    public sealed class FileSearchSettings
    {
        public bool Enabled { get; set; }
        public bool Configured { get; set; }

        /// <summary>Configured roots (drives or directories); empty = every fixed NTFS drive.</summary>
        public string[] Roots { get; set; }

        /// <summary>When true the type list is ignored and every file is indexed.</summary>
        public bool IndexAllFiles { get; set; }

        /// <summary>Lowercase extensions without the dot.</summary>
        public string[] Types { get; set; }

        public bool IndexFolders { get; set; }
        public bool IndexExtensionless { get; set; }

        /// <summary>Extra excluded directories: a bare name matches at any depth, a full path matches its subtree.</summary>
        public string[] ExcludeDirectories { get; set; }

        /// <summary>Minutes between background reconciles; 0 disables them.</summary>
        public int ReconcileMinutes { get; set; }

        public static readonly string[] DefaultTypes =
        {
            "pdf","doc","docx","xls","xlsx","ppt","pptx","txt","md","rtf","csv",
            "jpg","jpeg","png","gif","bmp","webp","svg","heic",
            "mp4","mkv","avi","mov","mp3","wav","flac",
            "zip","7z","rar","tar","gz",
            "cs","js","ts","py","java","json","xml","yaml","yml","sql","html","css",
            "lnk","url"
        };

        public static FileSearchSettings CreateDefault()
        {
            return new FileSearchSettings
            {
                Enabled = true,
                Configured = false,
                Roots = new string[0],
                IndexAllFiles = false,
                Types = (string[])DefaultTypes.Clone(),
                IndexFolders = true,
                IndexExtensionless = true,
                ExcludeDirectories = new string[0],
                ReconcileMinutes = 10
            };
        }

        /// <summary>
        /// Repairs a settings object read from config: blank/duplicate entries are
        /// dropped, extensions lose their dot and lowercase form, and the reconcile
        /// interval is clamped to one day.
        /// </summary>
        public void Normalize()
        {
            Roots = Clean(Roots);
            ExcludeDirectories = Clean(ExcludeDirectories);
            Types = CleanExtensions(Types);
            if (Types.Length == 0 && !IndexAllFiles) Types = (string[])DefaultTypes.Clone();
            if (ReconcileMinutes < 0) ReconcileMinutes = 0;
            if (ReconcileMinutes > 1440) ReconcileMinutes = 1440;
        }

        /// <summary>
        /// Signature of everything that shapes the index contents. The store keeps
        /// the fingerprint of the published scan; a mismatch means "rebuild needed".
        /// </summary>
        public string ComputeFingerprint(IReadOnlyList<string> resolvedRoots)
        {
            var parts = new List<string>();
            if (resolvedRoots != null)
            {
                foreach (var r in resolvedRoots)
                {
                    if (!string.IsNullOrEmpty(r)) parts.Add(r.TrimEnd('\\').ToLowerInvariant());
                }
            }
            parts.Sort(StringComparer.Ordinal);

            parts.Add(IndexAllFiles ? "all" : string.Join(",", Types ?? new string[0]));
            parts.Add("folders=" + IndexFolders);
            parts.Add("extless=" + IndexExtensionless);

            var excluded = new List<string>();
            if (ExcludeDirectories != null)
            {
                foreach (var e in ExcludeDirectories)
                {
                    if (!string.IsNullOrEmpty(e)) excluded.Add(e);
                }
            }
            excluded.Sort(StringComparer.OrdinalIgnoreCase);
            parts.Add(string.Join("|", excluded));

            return string.Join(";", parts);
        }

        private static string[] Clean(string[] values)
        {
            if (values == null || values.Length == 0) return new string[0];

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string>(values.Length);
            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string value = raw.Trim();
                if (!seen.Add(value)) continue;
                kept.Add(value);
            }
            return kept.ToArray();
        }

        private static string[] CleanExtensions(string[] values)
        {
            if (values == null || values.Length == 0) return new string[0];

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<string>(values.Length);
            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string value = raw.Trim().TrimStart('.').ToLowerInvariant();
                if (value.Length == 0 || !seen.Add(value)) continue;
                kept.Add(value);
            }
            return kept.ToArray();
        }
    }
}
