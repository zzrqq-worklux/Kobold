using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - Panel toggle, keyboard, folder icon mouse events, item hover/click, context menu
    /// </summary>
    public partial class FolderWidget
    {
        // Static brushes moved to ThemeManager for centralized cache
        
        #region Expand/Collapse
        
        /// <summary>
        /// Shows the expanded panel, using this widget's remembered position when it has
        /// one, otherwise the supplied default (below the island). Used by the island.
        /// </summary>
        public void ShowPanel(double defaultLeft, double defaultTop, bool activate = true)
        {
            UpdateUI();

            var (panelWidth, panelHeight) = CalculatePanelSize();
            ExpandedPanel.Width = panelWidth;
            ExpandedPanel.Height = panelHeight;
            ExpandedPanel.Visibility = Visibility.Visible;
            System.Windows.Controls.Canvas.SetLeft(ExpandedPanel, 0);
            System.Windows.Controls.Canvas.SetTop(ExpandedPanel, 0);
            Width = panelWidth;
            Height = panelHeight;

            double x = _data.PanelX ?? defaultLeft;
            double y = _data.PanelY ?? defaultTop;
            ClampToScreen(ref x, ref y, panelWidth, panelHeight);
            Left = x;
            Top = y;

            _isExpanded = true;
            if (!IsVisible)
            {
                // Opening several panels together ("Show All") must not activate each
                // one: activation would deactivate and hide the previous panel, which
                // shows up as flicker. Only a single, user-triggered open activates.
                if (!activate) ShowActivated = false;
                Show();
                ShowActivated = true;
            }
            if (activate) Activate();
            AnimationHelper.PanelOpen(ExpandedPanel);
        }

        /// <summary>Fades the panel out and hides its window.</summary>
        public void HidePanel()
        {
            if (!_isExpanded)
            {
                if (IsVisible) Hide();
                return;
            }

            ClearAllSelections();
            _isExpanded = false;
            AnimationHelper.PanelClose(ExpandedPanel, false, () =>
            {
                ExpandedPanel.Visibility = Visibility.Collapsed;
                Hide();
            });
        }

        private static void ClampToScreen(ref double x, ref double y, double width, double height)
        {
            double minX = SystemParameters.VirtualScreenLeft;
            double minY = SystemParameters.VirtualScreenTop;
            double maxX = minX + SystemParameters.VirtualScreenWidth - width;
            double maxY = minY + SystemParameters.VirtualScreenHeight - height;
            x = Math.Max(minX, Math.Min(x, maxX));
            y = Math.Max(minY, Math.Min(y, maxY));
        }

        #region Panel Drag (header handle)

        private void PanelHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (_data.IsLocked)
            {
                // Locked: the panel cannot move - flash the lock as feedback.
                FlashLockIndicator();
                e.Handled = true;
                return;
            }

            // Snapshot the anchor; a click without movement is not a drag.
            var cursor = System.Windows.Forms.Cursor.Position;
            _dragStartCursor = new Point(cursor.X, cursor.Y);
            _dragStartLeft = Left;
            _dragStartTop = Top;
            _dragDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX; // physical px per WPF unit
            _isDraggingWindow = false;
            Mouse.Capture(PanelHeader);
            Mouse.OverrideCursor = CursorHelper.GrabHand;
            e.Handled = true;
        }

        private void PanelHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_data.IsLocked || !ReferenceEquals(Mouse.Captured, PanelHeader)) return;

            var cursor = System.Windows.Forms.Cursor.Position;
            double dx = (cursor.X - _dragStartCursor.X) / _dragDpiScale;
            double dy = (cursor.Y - _dragStartCursor.Y) / _dragDpiScale;

            if (!_isDraggingWindow)
            {
                if (Math.Abs(dx) < DRAG_THRESHOLD && Math.Abs(dy) < DRAG_THRESHOLD) return;
                _isDraggingWindow = true;
            }

            Left = _dragStartLeft + dx;
            Top = _dragStartTop + dy;
            e.Handled = true;
        }

        private void PanelHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            Mouse.Capture(null);
            Mouse.OverrideCursor = null;

            if (_isDraggingWindow)
            {
                _isDraggingWindow = false;
                // Remember where the user parked this panel.
                _data.PanelX = Left;
                _data.PanelY = Top;
                OnDataChanged?.Invoke();
            }
            e.Handled = true;
        }

        private void PanelHeader_RightClick(object sender, MouseButtonEventArgs e)
        {
            // The desktop folder icon (old menu host) is gone in island mode, so the
            // widget menu - rename / color / lock / grid / size / delete - lives here.
            ShowFolderContextMenu();
            e.Handled = true;
        }

        #endregion

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Always clear selection when window loses focus
            ClearAllSelections();
            
            // Don't close panel if pinned
            if (_isExpanded && !_data.IsPanelPinned)
            {
                HidePanel();
            }
        }
        
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isExpanded)
            {
                if (_data.IsPanelPinned)
                {
                    // Panel pinned - just clear selection
                    ClearAllSelections();
                }
                else
                {
                    // Panel not pinned - hide it (also clears selection)
                    HidePanel();
                }
                e.Handled = true;
            }
        }
        
        private void PinButton_Click(object sender, MouseButtonEventArgs e)
        {
            _data.IsPanelPinned = !_data.IsPanelPinned;
            UpdatePinButtonVisual();
            OnDataChanged?.Invoke();
            e.Handled = true;
        }
        
        private void UpdatePinButtonVisual()
        {
            if (_data.IsPanelPinned)
            {
                // Pinned state - rotate to 0 (vertical) and highlight
                PinIconRotation.Angle = 0;
                PinIcon.Fill = ThemeManager.PinGoldBrush;
                PinIcon.Stroke = ThemeManager.PinDarkGoldBrush;
                PinButton.ToolTip = "Unpin panel (close on focus loss)";
            }
            else
            {
                // Unpinned state - rotate 45 degrees and dim
                PinIconRotation.Angle = 45;
                PinIcon.Fill = ThemeManager.PinUnpinnedFillBrush;
                PinIcon.Stroke = ThemeManager.PinUnpinnedStrokeBrush;
                PinButton.ToolTip = "Pin panel (keep open after restart)";
            }
        }
        
        private void LockButton_Click(object sender, MouseButtonEventArgs e)
        {
            _data.IsLocked = !_data.IsLocked;
            UpdateUI();
            OnDataChanged?.Invoke();
            e.Handled = true;
        }

        private void UpdateLockButtonVisual()
        {
            if (_data.IsLocked)
            {
                LockIcon.Fill = ThemeManager.PinGoldBrush;
                LockIcon.Stroke = ThemeManager.PinDarkGoldBrush;
                LockButton.ToolTip = Localization.Get("UI_UnlockTooltip");
            }
            else
            {
                LockIcon.Fill = ThemeManager.PinUnpinnedFillBrush;
                LockIcon.Stroke = ThemeManager.PinUnpinnedStrokeBrush;
                LockButton.ToolTip = Localization.Get("UI_LockTooltip");
            }

            // Locked panels cannot be dragged - show a forbidden cursor over the header.
            PanelHeader.Cursor = _data.IsLocked ? Cursors.No : CursorHelper.OpenHand;
        }

        private void FlashLockIndicator()
        {
            var pulse = new DoubleAnimationUsingKeyFrames();
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))));
            LockButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            LockButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
        
        #endregion

        #region Item Hover Events

        private void Item_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border b && !_isDraggingItem)
            {
                // Use cached hover brush from ThemeManager
                b.Background = ThemeManager.HoverBrush;
            }
        }

        private void Item_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border b && !_isDraggingItem)
                b.Background = ThemeManager.TransparentBrush;
        }
        
        /// <summary>
        /// Opens file on double-click (Windows Explorer style)
        /// </summary>
        private void Item_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.DataContext is DisplayItem item)
            {
                try 
                { 
                    Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true }); 
                } 
                catch (Exception ex) 
                { 
                    Debug.WriteLine($"[Kobold] Open failed: {ex.Message}"); 
                }
                e.Handled = true;
            }
        }

        private void Item_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.DataContext is DisplayItem item)
            {
                var menu = new ContextMenu();
                
                var openItem = new MenuItem { Header = Localization.Get("Menu_Open") };
                openItem.Click += (s, a) => { try { Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true }); } catch (Exception ex) { Debug.WriteLine($"[Kobold] Open failed: {ex.Message}"); } };
                
                var locItem = new MenuItem { Header = Localization.Get("Menu_OpenLocation") };
                locItem.Click += (s, a) => { try { Process.Start("explorer.exe", $"/select,\"{item.Path}\""); } catch (Exception ex) { Debug.WriteLine($"[Kobold] Open location failed: {ex.Message}"); } };
                
                // Store physically into Kobold storage (explicit action - by
                // default dropped files are references and stay in place).
                // Locked widgets forbid moving content in or out.
                var dataItem = _data.Items.FirstOrDefault(i => i.Path == item.Path);
                if (dataItem != null && dataItem.IsReference && !_data.IsLocked)
                {
                    var storeItem = new MenuItem { Header = Localization.Get("Menu_StoreItem") };
                    storeItem.Click += (s, a) => StoreItemIntoWidget(dataItem);
                    menu.Items.Add(storeItem);
                }

                // Eject (remove from widget; stored files go back to their original location)
                var remItem = new MenuItem
                {
                    Header = Localization.Get("Menu_RemoveItem"),
                    IsEnabled = !_data.IsLocked // locked widgets forbid moving content out
                };
                remItem.Click += (s, a) => 
                { 
                    var itemToRemove = _data.Items.FirstOrDefault(i => i.Path == item.Path);
                    if (itemToRemove != null)
                    {
                        EjectItem(itemToRemove);
                        _data.Items.Remove(itemToRemove);
                        UpdateUI();
                        OnDataChanged?.Invoke();
                    }
                };
                
                // Copy Path to clipboard
                var copyPathItem = new MenuItem { Header = Localization.Get("Menu_CopyPath") };
                copyPathItem.Click += (s, a) => 
                { 
                    try { System.Windows.Clipboard.SetText(item.Path); } catch (Exception ex) { Debug.WriteLine($"[Kobold] Clipboard failed: {ex.Message}"); }
                };
                
                // Rename item
                var renameItem = new MenuItem { Header = Localization.Get("Menu_RenameItem") };
                renameItem.Click += (s, a) => RenameItem(item);
                
                menu.Items.Add(openItem);
                menu.Items.Add(locItem);
                menu.Items.Add(renameItem);
                menu.Items.Add(copyPathItem);
                menu.Items.Add(new Separator());
                menu.Items.Add(remItem);
                menu.IsOpen = true;
                e.Handled = true;
            }
        }

        #endregion

        #region Context Menu

        private void ShowFolderContextMenu()
        {
            new MenuBuilder()
                .AddItem("Menu_Rename", ShowRenameDialog)
                .AddItem("Menu_ChangeColor", ShowColorPicker)
                .AddMenuItem(CreateGridSizeMenu())
                .AddMenuItem(CreateItemSizeMenu())
                .AddSeparator()
                .AddMenuItem(CreateDeleteMenuItem())
                .Show();
        }
        
        private MenuItem CreateGridSizeMenu()
        {
            var gridItem = new MenuItem { Header = Localization.Get("Menu_GridSize") };
            for (int cols = 2; cols <= 6; cols++)
            {
                int c = cols;
                var colItem = new MenuItem { Header = $"{cols} " + Localization.Get("Menu_Columns"), IsChecked = GridColumns == cols };
                colItem.Click += (s, a) => 
                { 
                    GridColumns = c;
                    UpdateUI();
                    OnDataChanged?.Invoke(); 
                };
                gridItem.Items.Add(colItem);
            }
            return gridItem;
        }
        
        private MenuItem CreateItemSizeMenu()
        {
            var sizeItem = new MenuItem { Header = Localization.Get("Menu_ItemSize") };
            var sizes = new[] { 
                ("Size_Small", 0.9), 
                ("Size_Normal", 1.0),
                ("Size_Default", 1.1),
                ("Size_Medium", 1.2),
                ("Size_Large", 1.3), 
                ("Size_ExtraLarge", 1.5) 
            };
            double currentScale = WidgetManager.Instance.Config.ItemScale;
            foreach (var (locKey, scale) in sizes)
            {
                double s = scale;
                var scaleItem = new MenuItem 
                { 
                    Header = Localization.Get(locKey), 
                    IsChecked = Math.Abs(currentScale - scale) < 0.05 
                };
                scaleItem.Click += (ss, aa) => ApplyItemScale(s);
                sizeItem.Items.Add(scaleItem);
            }
            return sizeItem;
        }
        
        private void ApplyItemScale(double scale)
        {
            WidgetManager.Instance.Config.ItemScale = scale;
            WidgetManager.Instance.SaveConfig();
            
            foreach (var widget in WidgetManager.Instance.Widgets)
            {
                widget.UpdateUI();
            }
        }
        
        private MenuItem CreateDeleteMenuItem()
        {
            var item = new MenuItem { Header = Localization.Get("Menu_Delete") };
            item.Click += (s, a) =>
            {
                if (MessageBox.Show(Localization.Format("Dialog_DeleteWidget", _data.Name), 
                    Localization.Get("Dialog_Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    EjectAllItems();
                    OnDeleted?.Invoke(this);
                    Close();
                }
            };
            return item;
        }
        
        /// <summary>
        /// Physically moves a referenced file/directory into Kobold storage and
        /// records its original path so eject can put it back later.
        /// </summary>
        private void StoreItemIntoWidget(WidgetItem witem)
        {
            string dest = StorageOps.MoveIntoStorage(witem.Path, Utils.GetStoragePath());
            if (dest == null)
            {
                // Keep as reference; the file is untouched
                System.Diagnostics.Debug.WriteLine($"[Kobold] Store failed: {witem.Path}");
                return;
            }
            witem.OriginalPath = witem.Path;
            witem.Path = dest;
            witem.IsReference = false;
            UpdateUI();
            OnDataChanged?.Invoke();
        }

        private void RenameItem(DisplayItem item)
        {
            string currentName = System.IO.Path.GetFileName(item.Path);
            string newName = ShowInputDialog(
                Localization.Get("Dialog_RenameItem_Title"),
                Localization.Get("Dialog_RenameItem_Prompt"),
                currentName);
            
            if (string.IsNullOrWhiteSpace(newName) || newName == currentName) return;
            
            try
            {
                string directory = System.IO.Path.GetDirectoryName(item.Path);
                string newPath = System.IO.Path.Combine(directory, newName);
                
                bool isDirectory = System.IO.Directory.Exists(item.Path);
                
                if (isDirectory)
                {
                    System.IO.Directory.Move(item.Path, newPath);
                }
                else
                {
                    System.IO.File.Move(item.Path, newPath);
                }
                
                // Update data
                var widgetItem = _data.Items.FirstOrDefault(i => i.Path == item.Path);
                if (widgetItem != null)
                {
                    widgetItem.Name = newName;
                    widgetItem.Path = newPath;
                }
                
                UpdateUI();
                OnDataChanged?.Invoke();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error renaming: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
        
        #region Panel Context Menu (New File/Folder)
        
        private void ExpandedPanel_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only show panel context menu if clicking on empty area (not on an item)
            var hit = e.OriginalSource as DependencyObject;
            
            while (hit != null && hit != ExpandedPanel)
            {
                if (hit is Border b && b.DataContext is DisplayItem)
                {
                    // Clicked on an item - don't show panel menu
                    return;
                }
                hit = VisualTreeHelper.GetParent(hit);
            }
            
            ShowPanelContextMenu();
            e.Handled = true;
        }
        
        private void ShowPanelContextMenu()
        {
            new MenuBuilder()
                .AddItem("Menu_NewFile", CreateNewFile)
                .AddItem("Menu_NewFolder", CreateNewFolder)
                .Show();
        }
        
        private void CreateNewFile()
        {
            string fileName = ShowInputDialog(
                Localization.Get("Dialog_NewFile_Title"),
                Localization.Get("Dialog_NewFile_Prompt"),
                "NewFile.txt");
            
            if (string.IsNullOrWhiteSpace(fileName)) return;
            
            try
            {
                // Use same storage path as other files so RestoreToDesktop works
                string filePath = System.IO.Path.Combine(Utils.GetStoragePath(), fileName);
                
                // Create empty file
                System.IO.File.WriteAllText(filePath, "");
                
                // Add to widget
                _data.Items.Add(new WidgetItem { Name = fileName, Path = filePath, IsReference = false });
                UpdateUI();
                OnDataChanged?.Invoke();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void CreateNewFolder()
        {
            string folderName = ShowInputDialog(
                Localization.Get("Dialog_NewFolder_Title"),
                Localization.Get("Dialog_NewFolder_Prompt"),
                "NewFolder");
            
            if (string.IsNullOrWhiteSpace(folderName)) return;
            
            try
            {
                // Use same storage path as other files so RestoreToDesktop works
                string folderPath = System.IO.Path.Combine(Utils.GetStoragePath(), folderName);
                
                // Create folder
                System.IO.Directory.CreateDirectory(folderPath);
                
                // Add to widget
                _data.Items.Add(new WidgetItem { Name = folderName, Path = folderPath, IsReference = false });
                UpdateUI();
                OnDataChanged?.Invoke();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private string ShowInputDialog(string title, string prompt, string defaultValue = "")
        {
            // Simple input dialog
            var inputWindow = new Window
            {
                Title = title,
                Width = 350,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45))
            };
            
            var stack = new StackPanel { Margin = new Thickness(16) };
            
            var label = new TextBlock { Text = prompt, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
            var textBox = new TextBox { Text = defaultValue, Padding = new Thickness(8, 4, 8, 4) };
            textBox.SelectAll();
            
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            
            var okButton = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = Localization.Get("Dialog_Cancel"), Width = 70, IsCancel = true };
            
            string result = null;
            okButton.Click += (s, e) => { result = textBox.Text; inputWindow.Close(); };
            cancelButton.Click += (s, e) => inputWindow.Close();
            
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            
            stack.Children.Add(label);
            stack.Children.Add(textBox);
            stack.Children.Add(buttonPanel);
            
            inputWindow.Content = stack;
            inputWindow.Loaded += (s, e) => textBox.Focus();
            inputWindow.ShowDialog();
            
            return result;
        }
        
        #endregion
    }
}


