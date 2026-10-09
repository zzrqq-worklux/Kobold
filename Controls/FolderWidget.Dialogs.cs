using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - Shell icon loading
    /// </summary>
    public partial class FolderWidget
    {
        #region Shell Icon

        // Cache icons by extension to avoid repeated Shell API calls
        private static readonly System.Collections.Generic.Dictionary<string, ImageSource> _iconCache 
            = new System.Collections.Generic.Dictionary<string, ImageSource>();
        private static readonly object _cacheLock = new object();

        // Sentinel key: the generic "file" icon used for browse entries whose
        // real icon was not loaded (memory guard for very long listings).
        private const string GENERIC_FILE_ICON_KEY = "::file::";

        /// <summary>Drops one path's cached icon - its shell icon just changed.</summary>
        private static void ForgetIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            lock (_cacheLock)
            {
                _iconCache.Remove(path.ToLowerInvariant());
            }
        }

        private ImageSource GetFileIcon(string path)
        {
            try
            {
                string cacheKey;
                uint attributes = 0;
                bool byAttributes = false;

                if (string.Equals(path, GENERIC_FILE_ICON_KEY, StringComparison.Ordinal))
                {
                    // Attribute-based lookup on a name without extension -> the
                    // shell's generic "file" icon.
                    cacheKey = GENERIC_FILE_ICON_KEY;
                    byAttributes = true;
                }
                else if (System.IO.Directory.Exists(path))
                {
                    // A colored folder carries the System flag and a desktop.ini,
                    // and only then is its icon folder-specific: ask the shell for
                    // the real path so it reads that file. Every other folder
                    // shares one generic icon (memory guard for long listings).
                    if ((System.IO.File.GetAttributes(path) & System.IO.FileAttributes.System) != 0)
                    {
                        cacheKey = path.ToLowerInvariant();
                        byAttributes = false;
                    }
                    else
                    {
                        cacheKey = "::folder::";
                        attributes = FILE_ATTRIBUTE_DIRECTORY;
                        byAttributes = true;
                    }
                }
                else if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    cacheKey = path.ToLowerInvariant(); // Full path for shortcuts
                }
                else
                {
                    cacheKey = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? "::noext::";
                }

                // Check cache first
                lock (_cacheLock)
                {
                    if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
                    {
                        return cachedIcon;
                    }
                }

                if (!byAttributes && !System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) return null;

                // Load icon from Shell
                var shinfo = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON | (byAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
                string lookupPath = byAttributes && cacheKey != "::folder::" ? "file" : path;

                SHGetFileInfo(lookupPath, byAttributes ? attributes : 0, ref shinfo,
                    (uint)Marshal.SizeOf(shinfo), flags);
                
                if (shinfo.hIcon != IntPtr.Zero)
                {
                    // WPF bitmaps must be created on the UI thread - GetFileIcon runs
                    // on a background thread, so marshal the creation over.
                    IntPtr hIcon = shinfo.hIcon;
                    var src = Dispatcher.Invoke(new Func<ImageSource>(() =>
                    {
                        try
                        {
                            var created = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            created.Freeze();
                            return created;
                        }
                        finally
                        {
                            DestroyIcon(hIcon);
                        }
                    }));
                    
                    // Cache the icon
                    lock (_cacheLock)
                    {
                        // Prevent infinite growth - clear if too big
                        if (_iconCache.Count > 500)
                        {
                            _iconCache.Clear();
                        }
                        
                        if (!_iconCache.ContainsKey(cacheKey))
                            _iconCache[cacheKey] = src;
                    }
                    return src;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Kobold] Icon load failed: {ex.Message}"); }
            return null;
        }

        /// <summary>Generic file icon for browse entries whose real icon was skipped.</summary>
        private ImageSource GetGenericFileIcon() => GetFileIcon(GENERIC_FILE_ICON_KEY);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
        
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
        
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO 
        { 
            public IntPtr hIcon; public int iIcon; public uint dwAttributes; 
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; 
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; 
        }
        
        private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_DIRECTORY = 0x10;

        #endregion
    }
}
