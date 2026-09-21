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
    /// FolderWidget - Dialogs (Rename, ColorPicker) and Shell Icon
    /// </summary>
    public partial class FolderWidget
    {
        #region Dialogs

        private void ShowRenameDialog()
        {
            string newName = ShowInputModal(
                Localization.Get("Dialog_Rename"),
                Localization.Get("Dialog_EnterName"),
                _data.Name,
                null);

            if (string.IsNullOrWhiteSpace(newName)) return;

            _data.Name = newName.Trim();
            UpdateUI();
            OnDataChanged?.Invoke();
        }

        private void ShowColorPicker()
        {
            var dlg = new Window
            {
                Title = Localization.Get("Dialog_PickColor"),
                Width = 340,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                Owner = this
            };

            var wrapPanel = new WrapPanel { Margin = new Thickness(UiTokens.Space4) };

            foreach (var color in UiTokens.FolderPalette)
            {
                wrapPanel.Children.Add(CreateColorSwatch(color, dlg));
            }

            dlg.Content = new Border
            {
                Background = ThemeManager.DialogBackgroundBrush,
                CornerRadius = new CornerRadius(UiTokens.RadiusWindow),
                BorderBrush = ThemeManager.DialogBorderBrush,
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 0,
                    Opacity = 0.5,
                    Color = Colors.Black
                },
                Child = wrapPanel
            };

            dlg.ShowDialog();
        }

        private Border CreateColorSwatch(string color, Window dialog)
        {
            bool isCurrent = string.Equals(color, _data.Color, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 48,
                Height = 48,
                Margin = new Thickness(6),
                CornerRadius = new CornerRadius(UiTokens.RadiusControl),
                Background = new SolidColorBrush(Utils.HexToColor(color)),
                Cursor = Cursors.Hand,
                BorderThickness = isCurrent ? new Thickness(3) : new Thickness(1),
                BorderBrush = isCurrent ? ThemeManager.WhiteBrush : ThemeManager.IconStrokeBrush
            };

            swatch.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 2,
                Opacity = 0.3
            };

            swatch.MouseEnter += (s, e) => swatch.Opacity = 0.8;
            swatch.MouseLeave += (s, e) => swatch.Opacity = 1.0;
            swatch.MouseLeftButtonUp += (s, e) =>
            {
                _data.Color = color;
                RefreshTheme();  // This updates panel colors AND item text colors
                OnDataChanged?.Invoke();
                dialog.Close();
            };

            return swatch;
        }

        #endregion

        #region Shell Icon

        // Cache icons by extension to avoid repeated Shell API calls
        private static readonly System.Collections.Generic.Dictionary<string, ImageSource> _iconCache 
            = new System.Collections.Generic.Dictionary<string, ImageSource>();
        private static readonly object _cacheLock = new object();

        // Sentinel key: the generic "file" icon used for browse entries whose
        // real icon was not loaded (memory guard for very long listings).
        private const string GENERIC_FILE_ICON_KEY = "::file::";

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
                    cacheKey = "::folder::";
                    attributes = FILE_ATTRIBUTE_DIRECTORY;
                    byAttributes = true;
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

                if (!byAttributes && !System.IO.File.Exists(path)) return null;

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
