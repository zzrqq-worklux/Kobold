using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FoldRa.Core;
using FoldRa.Helpers;
using Localization = FoldRa.Core.Localization;

namespace FoldRa.Controls
{
    /// <summary>
    /// FolderWidget - Dialogs (Rename, ColorPicker) and Shell Icon
    /// </summary>
    public partial class FolderWidget
    {
        #region Dialogs
        
        private void ShowRenameDialog()
        {
            var dlg = new Window 
            { 
                Title = Localization.Get("Dialog_Rename"), 
                Width = 380, Height = 180, 
                WindowStartupLocation = WindowStartupLocation.CenterScreen, 
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                Owner = this
            };
            
            // Apply dark styling
            var border = new Border
            {
                Background = ThemeManager.DialogBackgroundBrush,
                CornerRadius = new CornerRadius(8),
                BorderBrush = ThemeManager.DialogBorderBrush,
                BorderThickness = new Thickness(1)
            };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect 
            { 
                BlurRadius = 15, 
                ShadowDepth = 0, 
                Opacity = 0.5, 
                Color = Colors.Black 
            };
            
            var sp = new StackPanel { Margin = new Thickness(24) };
            var lbl = UIHelper.CreateLabel(Localization.Get("Dialog_EnterName"));
            lbl.Margin = new Thickness(0,0,0,12);
            
            var tb = UIHelper.CreateTextBox(_data.Name);
            tb.Padding = new Thickness(10,8,10,8);
            tb.SelectAll();
            
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
            
            var okBtn = UIHelper.CreateButton(Localization.Get("Dialog_OK"), true);
            okBtn.Width = 90;
            okBtn.Margin = new Thickness(0,0,10,0);
            okBtn.IsDefault = true;
            
            var cancelBtn = UIHelper.CreateButton(Localization.Get("Dialog_Cancel"), false);
            cancelBtn.Width = 90;
            cancelBtn.IsCancel = true;
            
            okBtn.Click += (s, e) => 
            { 
                if (!string.IsNullOrWhiteSpace(tb.Text))
                {
                    _data.Name = tb.Text.Trim(); 
                    UpdateUI();
                    OnDataChanged?.Invoke(); 
                    dlg.DialogResult = true;
                    dlg.Close(); 
                }
            };
            
            tb.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(tb.Text))
                    okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            sp.Children.Add(lbl);
            sp.Children.Add(tb);
            sp.Children.Add(btnPanel);
            
            border.Child = sp;
            dlg.Content = border;
            
            dlg.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            dlg.ShowDialog();
        }

        private void ShowColorPicker()
        {
            var dlg = new Window 
            { 
                Title = Localization.Get("Dialog_PickColor"), 
                Width = 340, Height= 220, 
                WindowStartupLocation = WindowStartupLocation.CenterScreen, 
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                Owner = this
            };
            
            // Apply dark styling
            var border = new Border
            {
                Background = ThemeManager.DialogBackgroundBrush,
                CornerRadius = new CornerRadius(8),
                BorderBrush = ThemeManager.DialogBorderBrush,
                BorderThickness = new Thickness(1)
            };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect 
            { 
                BlurRadius = 15, 
                ShadowDepth = 0, 
                Opacity = 0.5, 
                Color = Colors.Black 
            };
            
            var wp = new WrapPanel { Margin = new Thickness(20) };
            string[] colors = { "#3B82F6", "#EF4444", "#22C55E", "#F59E0B", "#8B5CF6", "#EC4899", "#06B6D4", "#84CC16", "#F97316", "#6366F1", "#14B8A6", "#A855F7" };
            
            foreach (var c in colors)
            {
                var colorBorder = new Border 
                { 
                    Width = 48, Height = 48, Margin = new Thickness(6), 
                    CornerRadius = new CornerRadius(8), 
                    Background = new SolidColorBrush(Utils.HexToColor(c)), 
                    Cursor = Cursors.Hand,
                    BorderThickness = c == _data.Color ? new Thickness(3) : new Thickness(1),
                    BorderBrush = c == _data.Color ? Brushes.White : new SolidColorBrush(Color.FromArgb(80, 255, 255, 255))
                };
                
                colorBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = 0.3 };
                
                string col = c;
                colorBorder.MouseEnter += (s, e) => ((Border)s).Opacity = 0.8;
                colorBorder.MouseLeave += (s, e) => ((Border)s).Opacity = 1.0;
                colorBorder.MouseLeftButtonUp += (s, e) => 
                { 
                    _data.Color = col; 
                    DrawFolderIcon();
                    RefreshTheme();  // This updates panel colors AND item text colors
                    OnDataChanged?.Invoke(); 
                    dlg.Close(); 
                };
                
                wp.Children.Add(colorBorder);
            }
            
            border.Child = wp;
            dlg.Content = border;
            dlg.ShowDialog();
        }
        
        #endregion

        #region Shell Icon

        // Cache icons by extension to avoid repeated Shell API calls
        private static readonly System.Collections.Generic.Dictionary<string, ImageSource> _iconCache 
            = new System.Collections.Generic.Dictionary<string, ImageSource>();
        private static readonly object _cacheLock = new object();

        private ImageSource GetFileIcon(string path)
        {
            try
            {
                // Create cache key based on extension (or folder marker)
                // Special case: shortcuts (.lnk) need full path as key since each has different target icon
                string cacheKey;
                if (System.IO.Directory.Exists(path))
                {
                    cacheKey = "::folder::";
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
                
                // Load icon from Shell
                var shinfo = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON;
                
                if (System.IO.Directory.Exists(path))
                    SHGetFileInfo(path, FILE_ATTRIBUTE_DIRECTORY, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags | SHGFI_USEFILEATTRIBUTES);
                else if (System.IO.File.Exists(path))
                    SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
                else
                    return null;
                
                if (shinfo.hIcon != IntPtr.Zero)
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    DestroyIcon(shinfo.hIcon);
                    
                    // Freeze for cross-thread access
                    src.Freeze();
                    
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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[FoldRa] Icon load failed: {ex.Message}"); }
            return null;
        }

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


