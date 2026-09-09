using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using FoldRa.Core;
using FoldRa.Controls;
using FoldRa.Windows;
using Localization = FoldRa.Core.Localization;

namespace FoldRa.Services
{
    /// <summary>
    /// Manages the system tray icon and menu using Hardcodet.NotifyIcon.Wpf
    /// </summary>
    public class TrayIconService : IDisposable
    {
        private TaskbarIcon _trayIcon;
        private bool _disposed = false;

        public TrayIconService()
        {
            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new TaskbarIcon
            {
                Icon = CreateDefaultIcon(),
                ToolTipText = "FoldRa - Desktop Folder Widgets",
                ContextMenu = BuildContextMenu(),
                MenuActivation = PopupActivationMode.RightClick
            };
            
            _trayIcon.TrayMouseDoubleClick += (s, e) => AddNewWidget();
        }

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();
            
            // Add New Widget
            var addItem = new MenuItem 
            { 
                Header = Localization.Get("Tray_AddWidget"),
                FontWeight = FontWeights.Bold
            };
            addItem.Click += (s, e) => AddNewWidget();
            
            // Show All
            var showAllItem = new MenuItem { Header = Localization.Get("Tray_ShowAll") };
            showAllItem.Click += (s, e) => WidgetManager.Instance.ShowAll();
            
            // Hide All
            var hideAllItem = new MenuItem { Header = Localization.Get("Tray_HideAll") };
            hideAllItem.Click += (s, e) => WidgetManager.Instance.HideAll();
            
            // Settings
            var settingsItem = new MenuItem { Header = Localization.Get("Tray_Settings") };
            settingsItem.Click += (s, e) => ShowSettings();
            
            // Exit
            var exitItem = new MenuItem { Header = Localization.Get("Tray_Exit") };
            exitItem.Click += (s, e) =>
            {
                WidgetManager.Instance.Shutdown();
                Application.Current.Shutdown();
            };
            
            // Build menu
            menu.Items.Add(addItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(showAllItem);
            menu.Items.Add(hideAllItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(settingsItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);
            
            return menu;
        }

        private void RefreshMenu()
        {
            _trayIcon.ContextMenu = BuildContextMenu();
        }

        private void ShowSettings()
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.ShowDialog();
            RefreshMenu(); // Refresh menu in case language changed
        }

        private void AddNewWidget()
        {
            int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            int screenHeight = (int)SystemParameters.PrimaryScreenHeight;
            
            int widgetCount = WidgetManager.Instance.Widgets.Count;
            int posX = screenWidth / 2 - 50 + (widgetCount % 5) * 30;
            int posY = screenHeight / 2 - 50 + (widgetCount % 5) * 30;
            
            string[] colors = { "#3B82F6", "#22C55E", "#EF4444", "#F59E0B", "#8B5CF6" };
            string color = colors[widgetCount % colors.Length];
            
            string name = Localization.CurrentLanguage == "tr" ? "Yeni Klasör" : "New Folder";
            int gridColumns = WidgetManager.Instance.Config.DefaultGridColumns;
            WidgetManager.Instance.CreateWidget(name, color, posX, posY, gridColumns);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private Icon CreateDefaultIcon()
        {
            try
            {
                // Try 1: Relative to BaseDirectory/Resources
                string iconPath1 = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.ico");
                if (System.IO.File.Exists(iconPath1))
                {
                    return new Icon(iconPath1);
                }
                
                // Try 2: Directly in BaseDirectory
                string iconPath2 = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (System.IO.File.Exists(iconPath2))
                {
                    return new Icon(iconPath2);
                }
                
                // Try 3: Pack URI (embedded resource)
                var streamResourceInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/icon.ico"));
                if (streamResourceInfo != null)
                {
                    return new Icon(streamResourceInfo.Stream);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Icon load error: {ex.Message}");
            }
            
            // Fallback: Create a simple icon programmatically (with proper handle cleanup)
            try
            {
                using (var bitmap = new Bitmap(16, 16))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(System.Drawing.Color.FromArgb(59, 130, 246));
                    }
                    IntPtr hIcon = bitmap.GetHicon();
                    Icon icon = Icon.FromHandle(hIcon).Clone() as Icon;
                    DestroyIcon(hIcon); // Fix memory leak
                    return icon;
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _trayIcon?.Dispose();
                }
                _disposed = true;
            }
        }

        ~TrayIconService()
        {
            Dispose(false);
        }
    }
}


