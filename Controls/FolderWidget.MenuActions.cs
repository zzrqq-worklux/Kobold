using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;
using Kobold.Services;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - menus and file actions. Browsing may write to disk through
    /// explicit actions, but it never touches _data.Items.
    /// </summary>
    public partial class FolderWidget
    {
        private void ShowMessage(string message)
        {
            RunModal(() => MessageBox.Show(this, message, "Kobold", MessageBoxButton.OK, MessageBoxImage.Warning));
        }

        private IntPtr PanelHwnd
        {
            get
            {
                try { return new System.Windows.Interop.WindowInteropHelper(this).Handle; }
                catch (Exception) { return IntPtr.Zero; }
            }
        }

        private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

        private string NameErrorText(NameCheck check)
        {
            if (check == NameCheck.Exists) return Localization.Get("Dialog_NameExists");
            if (check == NameCheck.Invalid) return Localization.Get("Dialog_NameInvalid");
            return null;
        }

        private void RefreshPanel()
        {
            ClearAllSelections();
            UpdateUI();
        }

        // ---------- open / terminal / explorer / copy ----------

        private void OpenItem(DisplayItem item)
        {
            if (item == null) return;
            if (item.IsDirectory) EnterFolder(item.Path);
            else OpenWithShell(item.Path);
        }

        private void OpenTerminalFor(string path, bool isDirectory)
        {
            string directory = isDirectory ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return;

            string error;
            if (!TerminalLauncher.TryLaunch(directory, out error))
            {
                ShowMessage(Localization.Get("Dialog_TerminalNotFound"));
            }
        }

        private void OpenInExplorer(string path, bool isDirectory)
        {
            try
            {
                if (isDirectory) Process.Start("explorer.exe", "\"" + path + "\"");
                else OpenContainingFolder(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Open in Explorer failed: " + ex.Message);
            }
        }

        private void CopySelectedPaths(List<DisplayItem> selected)
        {
            if (selected == null || selected.Count == 0) return;

            string text = selected.Count == 1
                ? selected[0].Path
                : string.Join(Environment.NewLine, selected.Select(i => i.Path));
            CopyPathToClipboard(text);
        }

        // ---------- create / rename / delete ----------

        /// <summary>New-file dialog bound to a target directory (labels + the localised
        /// default name + the composed-name validator); returns the composed name or null.</summary>
        private string ShowNewFileDialog(string title, string directory)
        {
            string result = null;
            RunModal(() =>
            {
                result = DialogFactory.ShowNewFile(this, title,
                    Localization.Get("Dialog_LabelFileName"),
                    Localization.Get("Dialog_LabelExtension"),
                    Localization.Get("Dialog_WillCreateFormat"),
                    Localization.Get("UI_NewFileDefault"),
                    NewFileName.DefaultExtension,
                    NewFileName.CommonExtensions,
                    _data.Color,
                    n => NameErrorText(ShellFileOperations.ValidateName(n, directory, PathExists)));
            });
            return result;
        }

        private void CreateBrowseFolder(string directory)
        {
            string name = ShowInputModal(
                Localization.Get("Dialog_NewFolder_Title"),
                Localization.Get("Dialog_NewFolder_Prompt"),
                Localization.Get("UI_NewFolderDefault"),
                n => NameErrorText(ShellFileOperations.ValidateName(n, directory, PathExists)));
            if (name == null) return;

            string error;
            if (!ShellFileOperations.CreateFolder(directory, name, out error))
            {
                ShowMessage(Localization.Format("Dialog_CreateFailed", error));
                return;
            }
            RefreshPanel();
        }

        private void CreateBrowseTextFile(string directory)
        {
            string name = ShowNewFileDialog(Localization.Get("Dialog_NewFile_Title"), directory);
            if (string.IsNullOrWhiteSpace(name)) return;

            string error;
            if (!ShellFileOperations.CreateTextFile(directory, name, out error))
            {
                ShowMessage(Localization.Format("Dialog_CreateFailed", error));
                return;
            }
            RefreshPanel();
        }

        private void RenameBrowseItem(DisplayItem item)
        {
            if (item == null) return;

            string directory = Path.GetDirectoryName(item.Path);
            string currentName = Path.GetFileName(item.Path);

            string newName = ShowInputModal(
                Localization.Get("Dialog_RenameItem_Title"),
                Localization.Get("Dialog_RenameItem_Prompt"),
                currentName,
                n => NameErrorText(ShellFileOperations.ValidateName(n, directory, PathExists, item.Path)));
            if (string.IsNullOrEmpty(newName)) return;

            string error;
            if (!ShellFileOperations.Rename(item.Path, newName, out error))
            {
                ShowMessage(Localization.Format("Dialog_RenameFailed", error));
                return;
            }
            RefreshPanel();
        }

        private void DeleteToRecycleBin(List<DisplayItem> selected)
        {
            if (selected == null || selected.Count == 0) return;

            var paths = selected.Select(i => i.Path).ToList();
            foreach (var path in paths)
            {
                if (!ShellFileOperations.CanRecycle(path,
                        ShellFileOperations.GetDriveType, ShellFileOperations.IsSubstDrive))
                {
                    ShowMessage(Localization.Get("Dialog_RecycleNotLocal"));
                    return;
                }
            }

            string message = paths.Count == 1
                ? Localization.Format("Dialog_RecycleItem", Path.GetFileName(paths[0]))
                : Localization.Format("Dialog_RecycleItems", paths.Count);
            if (!ShowConfirmModal(message)) return;

            int failed = ShellFileOperations.Recycle(paths, PanelHwnd);
            RefreshPanel();
            if (failed > 0)
            {
                ShowMessage(Localization.Format("Dialog_RecycleFailed", failed));
            }
        }

        // ---------- menus ----------

        // The native shell menu must start only after the WPF menu has closed:
        // its modal loop would otherwise freeze the close animation behind it.
        // The builders stash the pending action; the Show* methods wire Closed.
        private Action _pendingNativeMenu;
        private bool _nativeMenuKeyboardAnchored;

        private void ShowBrowseItemMenu(DisplayItem item, FrameworkElement anchor = null)
        {
            if (item == null) return;

            // Explorer semantics: right-clicking an unselected item selects it first.
            if (!item.IsSelected)
            {
                ClearAllSelections();
                item.IsSelected = true;
            }

            _nativeMenuKeyboardAnchored = anchor != null;
            var menu = BuildBrowseItemMenu(item, GetSelectedItems());
            menu.Closed += (s, e) =>
            {
                Action run = _pendingNativeMenu;
                _pendingNativeMenu = null;
                if (run != null) run();
            };

            ShowMenu(menu, anchor);
        }

        private ContextMenu BuildBrowseItemMenu(DisplayItem item, List<DisplayItem> selected)
        {
            bool single = selected.Count == 1;

            _pendingNativeMenu = null;
            var builder = new MenuBuilder(_data.Color)
                .AddItem("Menu_Open", () => OpenItem(item), single)
                .AddItem("Menu_OpenTerminal", () => OpenTerminalFor(item.Path, item.IsDirectory), single)
                .AddItem("Menu_OpenInExplorer", () => OpenInExplorer(item.Path, item.IsDirectory), single)
                .AddItem("Menu_CopyPath", () => CopySelectedPaths(selected))
                .AddItem("Menu_Copy", () => CopySelection(selected, false), !_data.IsLocked)
                .AddItem("Menu_Cut", () => CopySelection(selected, true), !_data.IsLocked);

            if (IsColorable(item)) builder.AddMenuItem(CreateFolderColorMenu(item));

            return builder
                .AddSeparator()
                .AddItem("Menu_RenameItem", () => RenameBrowseItem(item), single)
                .AddItem("Menu_DeleteItem", () => DeleteToRecycleBin(selected))
                .AddSeparator()
                .AddItem("Menu_ShowMoreOptions",
                    () => { _pendingNativeMenu = () => ShowNativeItemMenu(item, _nativeMenuKeyboardAnchored); }, single)
                .Build();
        }

        private void ShowBrowseBackgroundMenu(FrameworkElement anchor = null, bool atTopLeft = false)
        {
            string directory = CurrentBrowsePath;
            if (directory == null) return;

            _nativeMenuKeyboardAnchored = anchor != null;
            var menu = BuildBrowseBackgroundMenu(directory, ClipboardHasFiles());
            menu.Closed += (s, e) =>
            {
                Action run = _pendingNativeMenu;
                _pendingNativeMenu = null;
                if (run != null) run();
            };

            ShowMenu(menu, anchor, atTopLeft);
        }

        private ContextMenu BuildBrowseBackgroundMenu(string directory, bool canPaste)
        {
            _pendingNativeMenu = null;
            var builder = new MenuBuilder(_data.Color)
                .AddItem("Menu_NewFolder", () => CreateBrowseFolder(directory))
                .AddItem("Menu_NewFile", () => CreateBrowseTextFile(directory));
            // Smoke fix B: no files on the clipboard means no Paste entry at
            // all; a locked widget still shows it, greyed out.
            if (canPaste) builder.AddItem("Menu_Paste", PasteHere, !_data.IsLocked);
            return builder
                .AddSeparator()
                .AddItem("Menu_OpenTerminal", () => OpenTerminalFor(directory, true))
                .AddItem("Menu_OpenInExplorer", () => OpenInExplorer(directory, true))
                .AddItem("Menu_CopyPath", () => CopyPathToClipboard(directory))
                .AddItem("Menu_Refresh", RefreshPanel)
                .AddSeparator()
                .AddItem("Menu_ShowMoreOptions",
                    () => { _pendingNativeMenu = () => ShowNativeBackgroundMenu(directory, _nativeMenuKeyboardAnchored); })
                .Build();
        }

        /// <summary>Root panel menu: new entries plus the reference paste.</summary>
        private ContextMenu BuildPanelContextMenu(bool canPaste)
        {
            var builder = new MenuBuilder(_data.Color)
                .AddItem("Menu_NewFile", CreateNewFile)
                .AddItem("Menu_NewFolder", CreateNewFolder);
            // Smoke fix B: same paste gate as the browse background menu.
            if (canPaste) builder.AddItem("Menu_Paste", PasteIntoWidget, !_data.IsLocked);
            return builder.Build();
        }

        private void ShowRootItemMenu(DisplayItem item, FrameworkElement anchor = null)
        {
            ShowMenu(BuildRootItemMenu(item), anchor);
        }

        /// <summary>Root (widget items) menu: the previous behaviour plus Open in Terminal.</summary>
        private ContextMenu BuildRootItemMenu(DisplayItem item)
        {
            var menu = new ContextMenu();

            var openItem = new MenuItem { Header = Localization.Get("Menu_Open") };
            openItem.Click += (s, a) => OpenWithShell(item.Path);

            var locItem = new MenuItem { Header = Localization.Get("Menu_OpenLocation") };
            locItem.Click += (s, a) => OpenContainingFolder(item.Path);

            var terminalItem = new MenuItem
            {
                Header = Localization.Get("Menu_OpenTerminal"),
                IsEnabled = item.IsDirectory && !item.IsMissing
            };
            terminalItem.Click += (s, a) => OpenTerminalFor(item.Path, true);

            // Store physically into Kobold storage (explicit action - by
            // default dropped files are references and stay in place).
            // Locked widgets forbid moving content in or out.
            var dataItem = _data.Items.FirstOrDefault(i => i.Path == item.Path);
            if (dataItem != null && dataItem.IsReference && !item.IsMissing && !_data.IsLocked)
            {
                var storeItem = new MenuItem { Header = Localization.Get("Menu_StoreItem") };
                storeItem.Click += (s, a) => StoreItemIntoWidget(dataItem);
                menu.Items.Add(storeItem);
            }

            // Stored item: move the file back while keeping the entry in the widget
            if (dataItem != null && !dataItem.IsReference && !_data.IsLocked)
            {
                var unstoreItem = new MenuItem { Header = Localization.Get("Menu_UnstoreItem") };
                unstoreItem.Click += (s, a) => UnstoreItem(dataItem);
                menu.Items.Add(unstoreItem);
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
                    NotifyDataChanged();
                }
            };

            var copyPathItem = new MenuItem { Header = Localization.Get("Menu_CopyPath") };
            copyPathItem.Click += (s, a) => CopyPathToClipboard(item.Path);

            var copyItem = new MenuItem
            {
                Header = Localization.Get("Menu_Copy"),
                IsEnabled = !_data.IsLocked // locked widgets refuse every clipboard action
            };
            copyItem.Click += (s, a) => CopySelection(item.IsSelected ? GetSelectedItems() : new List<DisplayItem> { item }, false);

            var renameItem = new MenuItem { Header = Localization.Get("Menu_RenameItem") };
            renameItem.Click += (s, a) => RenameItem(item);

            menu.Items.Add(openItem);
            menu.Items.Add(locItem);
            menu.Items.Add(terminalItem);
            menu.Items.Add(renameItem);
            menu.Items.Add(copyPathItem);
            menu.Items.Add(copyItem);
            if (IsColorable(item)) menu.Items.Add(CreateFolderColorMenu(item));
            menu.Items.Add(new Separator());
            menu.Items.Add(remItem);
            MenuBuilder.Prepare(menu, _data.Color);
            return menu;
        }

        // ---------- folder color ----------

        /// <summary>Only a folder that really exists can carry a custom icon.</summary>
        private static bool IsColorable(DisplayItem item)
        {
            return item != null && item.IsDirectory && !item.IsMissing &&
                   Directory.Exists(item.Path);
        }

        /// <summary>Swatch submenu: one entry per palette color, then "default".</summary>
        private MenuItem CreateFolderColorMenu(DisplayItem item)
        {
            var menu = new MenuItem { Header = Localization.Get("Menu_FolderColor") };
            string current = FolderColor.GetIconResource(item.Path);

            for (int i = 0; i < UiTokens.FolderIconPalette.Length; i++)
            {
                string color = UiTokens.FolderIconPalette[i];
                var swatch = new System.Windows.Shapes.Rectangle
                {
                    Width = 16,
                    Height = 16,
                    RadiusX = 3,
                    RadiusY = 3,
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(Utils.HexToColor(color))
                };

                var name = new TextBlock
                {
                    Text = Localization.Get(UiTokens.FolderIconPaletteNames[i]),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                // The swatch and its name ride in Header, not Icon: the app's
                // MenuItem template (App.xaml) renders only the header and the
                // submenu arrow, so an Icon would never show up.
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(swatch);
                header.Children.Add(name);

                var entry = new MenuItem
                {
                    Header = header,
                    IsChecked = IconBelongsTo(current, color)
                };
                entry.Click += (s, a) => ApplyFolderColor(item, color);
                menu.Items.Add(entry);
            }

            menu.Items.Add(new Separator());

            var reset = new MenuItem
            {
                Header = Localization.Get("Menu_FolderColorDefault"),
                IsEnabled = current != null
            };
            reset.Click += (s, a) => RestoreFolderColor(item);
            menu.Items.Add(reset);

            return menu;
        }

        /// <summary>True when the folder already shows the cached icon of this color.</summary>
        private static bool IconBelongsTo(string iconResource, string colorHex)
        {
            if (string.IsNullOrEmpty(iconResource)) return false;

            // Cache files are named "<RRGGBB>-<osBuild>.ico".
            string expected = colorHex.TrimStart('#').ToUpperInvariant() + "-";
            return Path.GetFileName(iconResource)
                .StartsWith(expected, StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyFolderColor(DisplayItem item, string colorHex)
        {
            string icon = FolderIconFactory.EnsureIcon(colorHex, Utils.GetFolderIconsPath());
            if (icon == null)
            {
                ShowMessage(Localization.Get("Dialog_FolderColorFailed"));
                return;
            }

            var result = FolderColor.Apply(item.Path, icon, 0);
            if (result == FolderColorResult.RefusedSpecialFolder)
            {
                ShowMessage(Localization.Get("Dialog_FolderColorRefused"));
                return;
            }
            if (result != FolderColorResult.Applied)
            {
                ShowMessage(Localization.Get("Dialog_FolderColorFailed"));
                return;
            }

            RefreshAfterFolderColor(item);
        }

        private void RestoreFolderColor(DisplayItem item)
        {
            if (FolderColor.Restore(item.Path) != FolderColorResult.Restored)
            {
                ShowMessage(Localization.Get("Dialog_FolderColorFailed"));
                return;
            }

            RefreshAfterFolderColor(item);
        }

        /// <summary>The folder's shell icon changed: drop the cached bitmap and redraw.</summary>
        private void RefreshAfterFolderColor(DisplayItem item)
        {
            ForgetIcon(item.Path);
            UpdateUI();
        }

        /// <summary>Opens at the cursor, or anchored to the element for keyboard use.</summary>
        private static void ShowMenu(ContextMenu menu, FrameworkElement anchor, bool atTopLeft = false)
        {
            if (anchor != null)
            {
                menu.PlacementTarget = anchor;
                menu.Placement = atTopLeft
                    ? System.Windows.Controls.Primitives.PlacementMode.RelativePoint
                    : System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.HorizontalOffset = atTopLeft ? 8 : 0;
                menu.VerticalOffset = atTopLeft ? 8 : 0;
            }
            menu.IsOpen = true;
        }

        // ---------- keyboard ----------

        /// <summary>F5 / Enter / Shift+F10 / Apps. Returns true when the key was consumed.</summary>
        private bool HandleMenuActionKey(KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                RefreshPanel();
                return true;
            }

            if (e.Key == Key.Enter)
            {
                var selected = GetSelectedItems();
                if (selected.Count == 1) OpenItem(selected[0]);
                return true;
            }

            bool menuKey = e.Key == Key.Apps ||
                (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
            if (!menuKey) return false;

            var items = GetSelectedItems();
            if (items.Count >= 1)
            {
                var item = items[0];
                if (IsBrowsing) ShowBrowseItemMenu(item, GetItemContainer(item));
                else ShowRootItemMenu(item, GetItemContainer(item));
            }
            else if (IsBrowsing)
            {
                ShowBrowseBackgroundMenu(ItemsScroller, true);
            }
            else
            {
                ShowPanelContextMenu();
            }
            return true;
        }

        private FrameworkElement GetItemContainer(DisplayItem item)
        {
            return ItemsContainer.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        }

        // ---------- native shell menu ----------

        private HwndSource _menuHookSource;

        private void EnsureMenuHook()
        {
            if (_menuHookSource != null) return;
            try
            {
                IntPtr handle = PanelHwnd;
                if (handle == IntPtr.Zero) return;

                var source = HwndSource.FromHwnd(handle);
                if (source == null) return;

                _menuHookSource = source;
                source.AddHook(MenuMessageHook);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Native-menu hook failed: " + ex.Message);
            }
        }

        private IntPtr MenuMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            IntPtr result;
            if (ShellContextMenuService.HandleMenuMessage(msg, wParam, lParam, out result))
            {
                handled = true;
                return result;
            }
            return IntPtr.Zero;
        }

        private static bool IsShiftDown()
        {
            return (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        }

        /// <summary>Mouse-opened native menus appear at the cursor, like Explorer.</summary>
        private static Point GetCursorPhysical()
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            return new Point(cursor.X, cursor.Y);
        }

        private void ShowNativeItemMenu(DisplayItem item, bool keyboardAnchored)
        {
            Point anchor = keyboardAnchored ? GetItemAnchorPhysical(item) : GetCursorPhysical();
            if (ShellContextMenuService.ShowForItem(
                    PanelHwnd, item.Path, (int)anchor.X, (int)anchor.Y, IsShiftDown()))
            {
                RefreshPanel();
            }
        }

        private void ShowNativeBackgroundMenu(string directory, bool keyboardAnchored)
        {
            Point anchor = keyboardAnchored
                ? ItemsScroller.PointToScreen(new Point(8, 8))
                : GetCursorPhysical();
            if (ShellContextMenuService.ShowForFolderBackground(
                    PanelHwnd, directory, (int)anchor.X, (int)anchor.Y, IsShiftDown()))
            {
                RefreshPanel();
            }
        }

        private Point GetItemAnchorPhysical(DisplayItem item)
        {
            var container = GetItemContainer(item);
            return container != null
                ? container.PointToScreen(new Point(0, container.ActualHeight))
                : ItemsScroller.PointToScreen(new Point(8, 8));
        }
    }
}
