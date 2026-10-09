using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - keyboard, item hover/click and the panel's context menus.
    /// The panel show/hide cycle, placement, locking and the header drag are
    /// shared with every widget and live in <see cref="WidgetWindowBase"/>.
    /// </summary>
    public partial class FolderWidget
    {
        #region Panel deactivation

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Always clear the selection when the window loses focus - the
            // base's own Deactivated handler takes care of the modal guard and
            // hiding an unpinned panel (it runs on the same event).
            ClearAllSelections();
        }

        #endregion

        #region Keyboard

        /// <summary>
        /// Space previews the selected item through QuickLook. Handled in the
        /// tunnelling event because the ScrollViewer inside the panel also claims
        /// Space (page down) once it has focus.
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;

            // Holding Space repeats this event; QuickLook toggles on a repeated
            // request, so without this the preview would strobe.
            if (e.IsRepeat) return;

            var item = GetSelectedItems().FirstOrDefault();
            if (item == null) return;

            // Folders and missing entries have nothing to preview - leave Space to
            // the scroller instead.
            if (item.IsDirectory || !System.IO.File.Exists(item.Path)) return;

            if (QuickLookPreview.Request(item.Path)) e.Handled = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (HandleMenuActionKey(e))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Back && IsBrowsing)
            {
                GoBack();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && IsPanelOpen)
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

        private void HelpButton_Click(object sender, MouseButtonEventArgs e)
        {
            // The tooltip is the help; swallow the click so it does not start a
            // panel drag through the header.
            e.Handled = true;
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
        
        private void Item_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.DataContext is DisplayItem item)
            {
                if (IsBrowsing) ShowBrowseItemMenu(item);
                else ShowRootItemMenu(item);
                e.Handled = true;
            }
        }

        #endregion

        #region Context Menu

        /// <summary>
        /// Shows the shared widget menu (show/hide / rename / colour / lock /
        /// pin / grid / size / rows / delete) at the cursor. Shared by the
        /// panel header and the island tiles; onClosed fires once the menu is
        /// gone.
        /// </summary>
        public override void ShowWidgetMenu(Action onClosed = null)
        {
            var menu = WidgetMenuBuilder.Build(this, WidgetKindRegistry.Find(_data.Kind), onClosed);
            menu.IsOpen = true;
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
                // Keep as reference; the file is untouched. Tell the user why
                // nothing happened (usually missing rights on public-desktop
                // shortcuts, or a file that is in use).
                System.Diagnostics.Debug.WriteLine($"[Kobold] Store failed: {witem.Path}");
                ShowMessage(Localization.Get("Dialog_StoreFailed"));
                return;
            }
            witem.OriginalPath = witem.Path;
            witem.Path = dest;
            witem.IsReference = false;
            UpdateUI();
            NotifyDataChanged();
        }

        private void RenameItem(DisplayItem item)
        {
            string currentName = System.IO.Path.GetFileName(item.Path);
            string newName = ShowInputModal(
                Localization.Get("Dialog_RenameItem_Title"),
                Localization.Get("Dialog_RenameItem_Prompt"),
                currentName,
                null);
            
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
                NotifyDataChanged();
            }
            catch (Exception ex)
            {
                ShowMessage($"Error renaming: {ex.Message}");
            }
        }

        #endregion
        
        #region Panel Context Menu (New File/Folder)
        
        private void PanelShell_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only show panel context menu if clicking on empty area (not on an item)
            var hit = e.OriginalSource as DependencyObject;
            
            while (hit != null && hit != Chrome.Shell)
            {
                if (hit is Border b && b.DataContext is DisplayItem)
                {
                    // Clicked on an item - don't show panel menu
                    return;
                }
                hit = VisualTreeHelper.GetParent(hit);
            }
            
            // Browsing writes to the current folder through its own actions, so
            // the empty-area menu is the browse background menu instead.
            if (IsBrowsing)
            {
                ShowBrowseBackgroundMenu();
                e.Handled = true;
                return;
            }

            ShowPanelContextMenu();
            e.Handled = true;
        }
        
        private void ShowPanelContextMenu()
        {
            new MenuBuilder(_data.Color)
                .AddItem("Menu_NewFile", CreateNewFile)
                .AddItem("Menu_NewFolder", CreateNewFolder)
                .Show();
        }
        
        private void CreateNewFile()
        {
            string fileName = ShowInputModal(
                Localization.Get("Dialog_NewFile_Title"),
                Localization.Get("Dialog_NewFile_Prompt"),
                "NewFile.txt",
                null);
            
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
                NotifyDataChanged();
            }
            catch (Exception ex)
            {
                ShowMessage($"Error creating file: {ex.Message}");
            }
        }
        
        private void CreateNewFolder()
        {
            string folderName = ShowInputModal(
                Localization.Get("Dialog_NewFolder_Title"),
                Localization.Get("Dialog_NewFolder_Prompt"),
                "NewFolder",
                null);
            
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
                NotifyDataChanged();
            }
            catch (Exception ex)
            {
                ShowMessage($"Error creating folder: {ex.Message}");
            }
        }
        
        #endregion
    }
}
