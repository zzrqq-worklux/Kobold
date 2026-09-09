using System;
using System.IO;
using System.Windows.Media;

namespace FoldRa.Core
{
    public static class Utils
    {
        private static readonly object _lock = new object();

        /// <summary>
        /// Gets the application-specific AppData folder path
        /// </summary>
        public static string GetAppDataPath()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FoldRa"
            );
            
            EnsureDirectoryExists(path);
            return path;
        }

        /// <summary>
        /// Gets the configuration file path
        /// </summary>
        public static string GetConfigPath()
        {
            return Path.Combine(GetAppDataPath(), "config.json");
        }

        /// <summary>
        /// Gets the storage folder path for files moved into FoldRa
        /// </summary>
        public static string GetStoragePath()
        {
            string path = Path.Combine(GetAppDataPath(), "Storage");
            EnsureDirectoryExists(path);
            return path;
        }

        /// <summary>
        /// Checks if two paths are on the same drive
        /// </summary>
        public static bool IsSameDrive(string path1, string path2)
        {
            try
            {
                string root1 = Path.GetPathRoot(path1)?.ToUpperInvariant();
                string root2 = Path.GetPathRoot(path2)?.ToUpperInvariant();
                return !string.IsNullOrEmpty(root1) && root1 == root2;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a unique file path in storage (handles duplicates)
        /// </summary>
        public static string GetUniqueStoragePath(string originalPath)
        {
            string storagePath = GetStoragePath();
            string fileName = Path.GetFileName(originalPath);
            string destPath = Path.Combine(storagePath, fileName);
            
            // Handle duplicate names
            int counter = 1;
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            
            while (File.Exists(destPath) || Directory.Exists(destPath))
            {
                destPath = Path.Combine(storagePath, $"{nameWithoutExt} ({counter}){ext}");
                counter++;
            }
            
            return destPath;
        }

        /// <summary>
        /// Ensures a directory exists, creating it if necessary
        /// </summary>
        public static void EnsureDirectoryExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            
            lock (_lock)
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
        }

        /// <summary>
        /// Converts hex color string to WPF Color
        /// </summary>
        public static Color HexToColor(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 7)
            {
                return Color.FromRgb(59, 130, 246); // Default blue
            }

            try
            {
                hex = hex.TrimStart('#');
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return Color.FromRgb(r, g, b);
            }
            catch
            {
                return Color.FromRgb(59, 130, 246);
            }
        }

        /// <summary>
        /// Converts WPF Color to hex string
        /// </summary>
        public static string ColorToHex(Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        /// <summary>
        /// Creates a darker version of a color
        /// </summary>
        public static Color DarkenColor(Color color, double factor = 0.7)
        {
            return Color.FromRgb(
                (byte)(color.R * factor),
                (byte)(color.G * factor),
                (byte)(color.B * factor)
            );
        }

        /// <summary>
        /// Creates a lighter version of a color
        /// </summary>
        public static Color LightenColor(Color color, double factor = 0.3)
        {
            // Clamp factor to avoid overflow
            factor = Math.Min(factor, 1.0);
            return Color.FromRgb(
                (byte)Math.Min(255, color.R + (255 - color.R) * factor),
                (byte)Math.Min(255, color.G + (255 - color.G) * factor),
                (byte)Math.Min(255, color.B + (255 - color.B) * factor)
            );
        }
    }
}


