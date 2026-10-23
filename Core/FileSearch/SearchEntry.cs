using System;
using System.IO;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// One indexed record. Paths are stored in plain form (no <c>\\?\</c> prefix)
    /// and <see cref="ModifiedUtc"/> is Unix seconds, which is what the store
    /// persists as INTEGER. Built by the scanner and the watcher through the
    /// shared <see cref="TryCreate"/> factory.
    /// </summary>
    public sealed class SearchEntry
    {
        public string Path { get; set; }
        public string Name { get; set; }

        /// <summary>Lowercase name, the collation key the store queries on.</summary>
        public string NameLower { get; set; }

        public string Extension { get; set; }
        public bool IsDirectory { get; set; }
        public long SizeBytes { get; set; }

        /// <summary>Last write time in Unix seconds (UTC); 0 when unknown.</summary>
        public long ModifiedUtc { get; set; }

        /// <summary>The resolved scan root this record belongs to.</summary>
        public string Root { get; set; }

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Reads one filesystem item into an entry. Returns false when the item
        /// cannot be read (deleted between enumeration and stat, denied, media
        /// error) so a scan keeps going; the caller skips the record.
        /// </summary>
        public static bool TryCreate(string root, FileSystemInfo info, out SearchEntry entry)
        {
            entry = null;
            if (info == null) return false;

            try
            {
                string plainPath = SearchPath.FromExtended(info.FullName);
                if (string.IsNullOrEmpty(plainPath)) return false;

                FileAttributes attributes = info.Attributes;

                // .NET Framework reports (FileAttributes)(-1) instead of throwing when
                // the item disappeared between enumeration and this stat.
                if ((int)attributes == -1) return false;

                bool isDirectory = (attributes & FileAttributes.Directory) != 0 || info is DirectoryInfo;

                string name = info.Name;
                if (string.IsNullOrEmpty(name)) name = SearchPath.FromExtended(info.FullName);

                long size = 0;
                var file = info as FileInfo;
                if (!isDirectory && file != null) size = file.Length;

                long modifiedUtc;
                try
                {
                    modifiedUtc = ToUnixSeconds(info.LastWriteTimeUtc);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Corrupt or FAT-era volume timestamps can sit outside DateTime's
                    // range. This one record is unusable; the rest of the scan must
                    // still run.
                    return false;
                }

                entry = new SearchEntry
                {
                    Path = plainPath,
                    Name = name,
                    NameLower = name.ToLowerInvariant(),
                    Extension = isDirectory ? string.Empty : SearchPath.GetExtension(plainPath),
                    IsDirectory = isDirectory,
                    SizeBytes = size,
                    ModifiedUtc = modifiedUtc,
                    Root = SearchPath.FromExtended(root)
                };
                return true;
            }
            catch (IOException)
            {
                // Deleted, moved or unreadable between enumeration and stat.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static long ToUnixSeconds(DateTime value)
        {
            // Normalize to UTC before the epoch math so a Local-kind value from a
            // custom FileSystemInfo cannot shift the timestamp.
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return (long)Math.Floor((utc - UnixEpoch).TotalSeconds);
        }
    }
}
