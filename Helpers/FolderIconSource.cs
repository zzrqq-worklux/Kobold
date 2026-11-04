using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kobold.Core;

namespace Kobold.Helpers
{
    /// <summary>
    /// Reads the icon file a colored folder's desktop.ini points at.
    ///
    /// The shell's SHGetFileInfo caches a folder icon and can keep returning
    /// the previous color after desktop.ini changes. The app writes standalone
    /// .ico files, so reading that file directly makes a color change visible
    /// immediately and gives the cache a key that follows the icon, not the
    /// folder path.
    /// </summary>
    public static class FolderIconSource
    {
        /// <summary>
        /// A cache key for the folder's custom .ico, including its last-write
        /// stamp; null when the folder has no standalone .ico.
        /// </summary>
        public static string GetCacheKey(string folderPath)
        {
            string iconFile = FolderColor.GetIconFilePath(folderPath);
            if (!IsUsableIconFile(iconFile)) return null;

            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(iconFile); }
            catch (Exception) { stamp = DateTime.MinValue; }

            return "icon:" + iconFile.ToLowerInvariant() + ":" + stamp.Ticks;
        }

        /// <summary>
        /// The current custom icon, or null when the folder has no standalone
        /// .ico (a DLL/EXE resource falls back to the shell).
        /// </summary>
        public static ImageSource Load(string folderPath)
        {
            string iconFile = FolderColor.GetIconFilePath(folderPath);
            if (!IsUsableIconFile(iconFile)) return null;

            try
            {
                var decoder = BitmapDecoder.Create(
                    new Uri(iconFile, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                BitmapFrame frame = PickFrame(decoder.Frames);
                if (frame == null) return null;
                if (!frame.IsFrozen) frame.Freeze();
                return frame;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsUsableIconFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase)) return false;

            try { return File.Exists(path); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The shell uses the 32px large icon; pick the smallest frame at least
        /// that big, or the largest available for a tiny icon.
        /// </summary>
        private static BitmapFrame PickFrame(IReadOnlyList<BitmapFrame> frames)
        {
            if (frames == null || frames.Count == 0) return null;

            BitmapFrame atLeast32 = null;
            int best = int.MaxValue;
            BitmapFrame largest = null;
            int largestWidth = -1;

            for (int i = 0; i < frames.Count; i++)
            {
                BitmapFrame frame = frames[i];
                if (frame == null) continue;

                int width = frame.PixelWidth;
                if (width >= 32 && width < best)
                {
                    best = width;
                    atLeast32 = frame;
                }
                if (width > largestWidth)
                {
                    largestWidth = width;
                    largest = frame;
                }
            }

            return atLeast32 ?? largest;
        }
    }
}
