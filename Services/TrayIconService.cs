using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using Kobold.Core;
using Kobold.Core.Icons;
using Kobold.Helpers;
using Kobold.Services.Update;
using Localization = Kobold.Core.Localization;

namespace Kobold.Services
{
    /// <summary>
    /// Manages the system tray icon and menu using Hardcodet.NotifyIcon.Wpf
    /// </summary>
    public class TrayIconService : IDisposable
    {
        private TaskbarIcon _trayIcon;
        private bool _disposed = false;
        private bool _saveFailureReported = false;
        private MenuItem _updateMenuItem;
        private Separator _updateMenuSeparator;
        private string _updateBalloonVersion;

        public TrayIconService()
        {
            InitializeTrayIcon();
            AppConfig.SaveFailed += OnSaveFailed;
            // The update service may report from a background thread; the
            // handler marshals before touching the menu or the balloon.
            UpdateService.Instance.StateChanged += OnUpdateStateChanged;
        }

        /// <summary>
        /// Tells the user once per run that a save did not land; repeated
        /// failures would otherwise spam balloons for every retry.
        /// </summary>
        private void OnSaveFailed(string message)
        {
            if (_saveFailureReported) return;
            _saveFailureReported = true;

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _trayIcon.ShowBalloonTip(
                        Localization.Get("Tray_SaveFailedTitle"),
                        Localization.Format("Tray_SaveFailedBody", message),
                        BalloonIcon.Warning);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Kobold] save-failure balloon failed: {ex.Message}");
                }
            }));
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new TaskbarIcon
            {
                Icon = CreateDefaultIcon(),
                ToolTipText = GetToolTipText(),
                ContextMenu = BuildContextMenu(),
                MenuActivation = PopupActivationMode.RightClick
            };
            
            _trayIcon.TrayMouseDoubleClick += (s, e) => WidgetManager.Instance.ShowIsland();
        }

        private string GetToolTipText()
        {
            return Localization.Get("UI_AppName") + " - " + Localization.Get("UI_Tagline");
        }

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();

            // An update entry sits at the very top while one is actionable; the
            // fields let a progress tick rewrite the label without rebuilding
            // the whole menu.
            _updateMenuItem = null;
            _updateMenuSeparator = null;
            string updateText = UpdateStateRules.TrayText(
                UpdateService.Instance.Status, UpdateService.Instance.IsSelfUpdatable);
            if (updateText != null)
            {
                _updateMenuItem = CreateUpdateMenuItem(updateText);
                _updateMenuSeparator = new Separator();
                menu.Items.Add(_updateMenuItem);
                menu.Items.Add(_updateMenuSeparator);
            }

            // Add New Widget: a submenu with one entry per widget kind
            var addItem = new MenuItem 
            { 
                Header = Localization.Get("Tray_AddWidget"),
                FontWeight = FontWeights.Bold,
                Icon = MenuIcons.Create(IconId.Plus)
            };
            foreach (var kindItem in WidgetMenuBuilder.BuildKindChoices(
                kind => WidgetManager.Instance.CreateWidget(kind)))
            {
                addItem.Items.Add(kindItem);
            }
            
            // Show All
            var showAllItem = new MenuItem
            {
                Header = Localization.Get("Tray_ShowAll"),
                Icon = MenuIcons.Create(IconId.Eye)
            };
            showAllItem.Click += (s, e) => WidgetManager.Instance.ShowAll();
            
            // Hide All
            var hideAllItem = new MenuItem
            {
                Header = Localization.Get("Tray_HideAll"),
                Icon = MenuIcons.Create(IconId.EyeOff)
            };
            hideAllItem.Click += (s, e) => WidgetManager.Instance.HideAll();
            
            // Settings
            var settingsItem = new MenuItem
            {
                Header = Localization.Get("Tray_Settings"),
                Icon = MenuIcons.Create(IconId.Settings)
            };
            settingsItem.Click += (s, e) => ShowSettings();
            
            // Exit
            var exitItem = new MenuItem
            {
                Header = Localization.Get("Tray_Exit"),
                Icon = MenuIcons.Create(IconId.Close, ThemeManager.DangerBrush)
            };
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

        private MenuItem CreateUpdateMenuItem(string header)
        {
            var item = new MenuItem
            {
                Header = header,
                FontWeight = FontWeights.Bold,
                Icon = MenuIcons.Create(IconId.Refresh)
            };
            item.Click += (s, e) => OnUpdateMenuClick();
            return item;
        }

        /// <summary>
        /// Rewrites just the update entry on a state/progress change: add it at
        /// the top when an offer appears, update its label while downloading,
        /// and remove it once the state is no longer actionable.
        /// </summary>
        private void RefreshUpdateMenu()
        {
            if (_trayIcon == null || _trayIcon.ContextMenu == null) return;
            ContextMenu menu = _trayIcon.ContextMenu;

            string text = UpdateStateRules.TrayText(
                UpdateService.Instance.Status, UpdateService.Instance.IsSelfUpdatable);
            if (text == null)
            {
                if (_updateMenuItem != null)
                {
                    menu.Items.Remove(_updateMenuItem);
                    if (_updateMenuSeparator != null) menu.Items.Remove(_updateMenuSeparator);
                    _updateMenuItem = null;
                    _updateMenuSeparator = null;
                }
                return;
            }

            if (_updateMenuItem == null)
            {
                _updateMenuItem = CreateUpdateMenuItem(text);
                _updateMenuSeparator = new Separator();
                menu.Items.Insert(0, _updateMenuSeparator);
                menu.Items.Insert(0, _updateMenuItem);
                return;
            }

            _updateMenuItem.Header = text;
        }

        private void OnUpdateMenuClick()
        {
            // The item's action follows the live state, not the label that was
            // showing when the menu opened. A non-updatable copy never starts a
            // download it could not apply; it opens the download page.
            UpdateStatus status = UpdateService.Instance.Status;
            switch (UpdateStateRules.TrayAction(status, UpdateService.Instance.IsSelfUpdatable))
            {
                case UpdateTrayAction.Download:
                    _ = UpdateService.Instance.DownloadNow();
                    break;
                case UpdateTrayAction.Apply:
                    UpdateService.Instance.ApplyNow();
                    break;
                case UpdateTrayAction.OpenDownloadPage:
                    OpenUpdatePage();
                    break;
            }
        }

        private void OpenUpdatePage()
        {
            try
            {
                System.Diagnostics.Process.Start(UpdateService.DownloadPageUrl);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] could not open the download page: " + ex.Message);
            }
        }

        private void OnUpdateStateChanged()
        {
            Application app = Application.Current;
            if (app == null) return;

            // Decide at the state-change moment, not when the dispatcher gets
            // round to it: automatic installation moves Available to
            // Downloading almost immediately, and re-reading the live status
            // there would swallow the once-per-version balloon.
            UpdateStatus changed = UpdateService.Instance.Status;
            string offeredVersion = changed.State == UpdateState.Available
                ? changed.Version
                : null;

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                RefreshUpdateMenu();
                if (offeredVersion != null) ShowUpdateBalloon(offeredVersion);
            }));
        }

        /// <summary>
        /// One balloon per release: a later progress tick or a repeat offer of
        /// the same version must not buzz again.
        /// </summary>
        private void ShowUpdateBalloon(string version)
        {
            if (string.IsNullOrEmpty(version)) return;
            if (string.Equals(_updateBalloonVersion, version, StringComparison.Ordinal)) return;
            _updateBalloonVersion = version;

            try
            {
                _trayIcon.ShowBalloonTip(
                    Localization.Get("Tray_UpdateBalloonTitle"),
                    Localization.Format("Tray_UpdateBalloonBodyFormat", version),
                    BalloonIcon.Info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Kobold] update balloon failed: {ex.Message}");
            }
        }

        private void RefreshMenu()
        {
            _trayIcon.ToolTipText = GetToolTipText();
            _trayIcon.ContextMenu = BuildContextMenu();
        }

        private void ShowSettings()
        {
            WidgetManager.Instance.ShowSettings();
            RefreshMenu(); // Refresh menu in case language changed
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private Icon CreateDefaultIcon()
        {
            // 专用托盘图标：更简单的字形，16 px 下比完整 App 图标更清楚。
            // 缺失时继续走下面的主图标回退链，旧部署不受影响。
            try
            {
                string trayPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "tray.ico");
                if (System.IO.File.Exists(trayPath))
                {
                    return new Icon(trayPath, System.Windows.Forms.SystemInformation.SmallIconSize);
                }

                var trayResource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/tray.ico"));
                if (trayResource != null)
                {
                    return new Icon(trayResource.Stream, System.Windows.Forms.SystemInformation.SmallIconSize);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tray icon load error: {ex.Message}");
            }

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
                    AppConfig.SaveFailed -= OnSaveFailed;
                    UpdateService.Instance.StateChanged -= OnUpdateStateChanged;
                    _updateMenuItem = null;
                    _updateMenuSeparator = null;
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


