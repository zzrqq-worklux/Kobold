using System;
using System.IO;
using System.Windows.Media;

namespace Kobold.Core
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
                "Kobold"
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
        /// Gets the storage folder path for files moved into Kobold
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
            return GetUniqueStoragePath(originalPath, GetStoragePath());
        }

        /// <summary>
        /// Gets a unique file path inside storageDir (handles duplicates)
        /// </summary>
        public static string GetUniqueStoragePath(string originalPath, string storageDir)
        {
            string fileName = Path.GetFileName(originalPath);
            string destPath = Path.Combine(storageDir, fileName);
            
            // Handle duplicate names
            int counter = 1;
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            
            while (File.Exists(destPath) || Directory.Exists(destPath))
            {
                destPath = Path.Combine(storageDir, $"{nameWithoutExt} ({counter}){ext}");
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
        /// Converts a hex color string (#RRGGBB or #AARRGGBB) to a WPF Color.
        /// Falls back to the theme accent for malformed values.
        /// </summary>
        public static Color HexToColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return FallbackColor;

            hex = hex.TrimStart('#');
            try
            {
                if (hex.Length == 8)
                {
                    return Color.FromArgb(
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16),
                        Convert.ToByte(hex.Substring(6, 2), 16));
                }

                if (hex.Length == 6)
                {
                    return Color.FromRgb(
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
                }
            }
            catch (Exception)
            {
                // Fall through to the accent fallback for malformed values.
            }

            return FallbackColor;
        }

        private static readonly Color FallbackColor =
            (Color)ColorConverter.ConvertFromString(UiTokens.DefaultFolderColor);

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

        /// <summary>
        /// Linear blend between two colors: amount 0 returns the base color,
        /// 1 returns the tint. Out-of-range amounts are clamped; alpha is kept.
        /// </summary>
        public static Color MixColor(Color baseColor, Color tint, double amount)
        {
            amount = Math.Max(0.0, Math.Min(1.0, amount));
            return Color.FromArgb(
                baseColor.A,
                MixChannel(baseColor.R, tint.R, amount),
                MixChannel(baseColor.G, tint.G, amount),
                MixChannel(baseColor.B, tint.B, amount)
            );
        }

        private static byte MixChannel(byte from, byte to, double amount)
        {
            return (byte)Math.Round(from + (to - from) * amount);
        }
    }
}


