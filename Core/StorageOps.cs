using System;
using System.IO;

namespace Kobold.Core
{
    /// <summary>
    /// File operations behind widget storing/restoring. Pure and parameterized
    /// (no globals) so tests/StorageOpsCheck can exercise it on temp folders.
    /// </summary>
    public static class StorageOps
    {
        /// <summary>
        /// Physically moves a file or directory into storageDir, resolving name
        /// collisions. Returns the new path, or null if nothing was moved.
        /// </summary>
        public static string MoveIntoStorage(string sourcePath, string storageDir)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath)) return null;
                Utils.EnsureDirectoryExists(storageDir);

                string destPath = Utils.GetUniqueStoragePath(sourcePath, storageDir);

                if (Directory.Exists(sourcePath))
                {
                    Directory.Move(sourcePath, destPath);
                    return destPath;
                }
                if (File.Exists(sourcePath))
                {
                    File.Move(sourcePath, destPath);
                    return destPath;
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Moves a stored file/directory back to its original path.
        /// Fails (returns false) when originalPath is null/empty, when the
        /// original location is occupied, or when the move throws - the caller
        /// then decides on a fallback (e.g. desktop).
        /// </summary>
        public static bool TryRestoreToOriginal(string storedPath, string originalPath)
        {
            try
            {
                if (string.IsNullOrEmpty(originalPath)) return false;
                if (File.Exists(originalPath) || Directory.Exists(originalPath)) return false;

                string parentDir = Path.GetDirectoryName(originalPath);
                if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir)) return false;

                if (Directory.Exists(storedPath))
                {
                    Directory.Move(storedPath, originalPath);
                    return true;
                }
                if (File.Exists(storedPath))
                {
                    File.Move(storedPath, originalPath);
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when path is located under root (case-insensitive, boundary-safe)
        /// </summary>
        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sets or clears the hidden attribute on a file or directory.
        /// Returns false when the path does not exist (safe no-op).
        /// </summary>
        public static bool SetHidden(string path, bool hidden)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return false;

                var attrs = File.GetAttributes(path);
                if (hidden)
                {
                    if ((attrs & FileAttributes.Hidden) == 0)
                        File.SetAttributes(path, attrs | FileAttributes.Hidden);
                }
                else
                {
                    if ((attrs & FileAttributes.Hidden) != 0)
                        File.SetAttributes(path, attrs & ~FileAttributes.Hidden);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when the path exists and carries the hidden attribute
        /// </summary>
        public static bool IsHidden(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return false;
                return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
