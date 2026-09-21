using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - In-panel folder browsing. The browser is a read-only view
    /// over the file system: it never touches _data.Items and never writes files.
    /// </summary>
    public partial class FolderWidget
    {
        #region Browse State

        // Empty = the widget's own items (root). Last element = the folder on screen.
        private readonly List<string> _browseStack = new List<string>();
        private ListingResult _browseListing;

        // True between a press on the chevron and its release, so a release that
        // merely happens to land on the chevron does not open the path menu.
        private bool _pathButtonPressed;

        private string CurrentBrowsePath =>
            _browseStack.Count == 0 ? null : _browseStack[_browseStack.Count - 1];

        /// <summary>True while the panel shows a folder instead of the widget's items.</summary>
        public bool IsBrowsing => _browseStack.Count > 0;

        public void EnterFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            _browseStack.Add(path);
            ClearAllSelections();
            UpdateUI();
            ItemsScroller.ScrollToTop();
        }

        public void GoBack()
        {
            if (_browseStack.Count == 0) return;

            JumpToLevel(_browseStack.Count - 1);
        }

        /// <summary>Browsing is transient - the panel always reopens at the root.</summary>
        private void ResetBrowse()
        {
            _browseStack.Clear();
            _browseListing = null;
        }

        private void BackButton_Click(object sender, MouseButtonEventArgs e)
        {
            GoBack();
            e.Handled = true; // must not start a panel drag through the header
        }

        /// <summary>
        /// Swallows the press so the header cannot start a panel drag (the chevron
        /// sits inside the drag handle) without opening the menu yet.
        /// </summary>
        private void PathButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _pathButtonPressed = true;
            e.Handled = true;
        }

        /// <summary>
        /// Opens the breadcrumb menu on release: a menu opened on the press is
        /// dismissed again by the release right after it, which makes the button
        /// feel unresponsive.
        /// </summary>
        private void PathButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_pathButtonPressed) return; // press started elsewhere, released here
            _pathButtonPressed = false;

            ShowPathMenu();
            e.Handled = true;
        }

        /// <summary>
        /// Breadcrumb menu for the current path: the widget's own items first, then
        /// every browsed level. The folder on screen is the current row; clicking any
        /// row above it jumps straight to that level instead of walking back one
        /// folder at a time.
        /// </summary>
        private void ShowPathMenu()
        {
            if (!IsBrowsing) return;

            var labels = BrowsePath.Ancestry(_data.Name, _browseStack);
            var menu = new ContextMenu();

            for (int i = 0; i < labels.Count; i++)
            {
                int levelsToKeep = i;                   // 0 = the widget's own items
                bool isCurrent = i == labels.Count - 1; // the folder on screen

                var item = new MenuItem
                {
                    Header = BrowsePath.MenuHeader(labels[i]),
                    IsEnabled = !isCurrent,
                    FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal
                };
                if (!isCurrent) item.Click += (s, a) => JumpToLevel(levelsToKeep);

                menu.Items.Add(item);
            }

            MenuBuilder.Prepare(menu, _data.Color);
            menu.IsOpen = true;
        }

        /// <summary>Cuts the browse stack back to the given depth and re-renders.</summary>
        private void JumpToLevel(int levelsToKeep)
        {
            int keep = Math.Max(0, Math.Min(levelsToKeep, _browseStack.Count));
            if (keep == _browseStack.Count) return;

            _browseStack.RemoveRange(keep, _browseStack.Count - keep);
            ClearAllSelections();
            UpdateUI();
            ItemsScroller.ScrollToTop();
        }

        private void MoreItemsText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!IsBrowsing) return;

            OpenWithShell(CurrentBrowsePath); // a directory opens in Explorer
            e.Handled = true;
        }

        #endregion

        #region Item Building

        private List<DisplayItem> BuildRootItems()
        {
            var textBrush = ThemeManager.TextBrush;

            return _data.Items.Select((item, index) =>
            {
                bool isFile = System.IO.File.Exists(item.Path);
                bool isDirectory = !isFile && System.IO.Directory.Exists(item.Path);

                return new DisplayItem
                {
                    Name = GetDisplayName(string.IsNullOrEmpty(item.Name) ? item.Path : item.Name),
                    Path = item.Path,
                    Icon = null,
                    Index = index,
                    TextColor = textBrush,
                    IsStored = !item.IsReference,
                    IsDirectory = isDirectory,
                    IsMissing = !isDirectory && !isFile
                };
            }).ToList();
        }

        private List<DisplayItem> BuildBrowseItems()
        {
            var textBrush = ThemeManager.TextBrush;
            _browseListing = FolderListing.ListChildren(CurrentBrowsePath, WidgetConstants.MAX_BROWSE_ENTRIES);

            var items = new List<DisplayItem>();
            foreach (var entry in _browseListing.Entries)
            {
                items.Add(new DisplayItem
                {
                    Name = GetDisplayName(entry.Path),
                    Path = entry.Path,
                    Icon = null,
                    Index = items.Count,
                    TextColor = textBrush,
                    IsStored = false,
                    IsMissing = false,
                    IsDirectory = entry.IsDirectory
                });
            }
            return items;
        }

        #endregion

        #region Shell Helpers

        private static void OpenWithShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Open failed: {ex.Message}");
            }
        }

        private static void OpenContainingFolder(string path)
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Open location failed: {ex.Message}");
            }
        }

        private static void CopyPathToClipboard(string path)
        {
            try
            {
                Clipboard.SetText(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Clipboard failed: {ex.Message}");
            }
        }

        #endregion
    }
}
