using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - live refresh of the browsed folder. While the panel is open
    /// on a folder a FolderWatcher follows it; changes are debounced, then the
    /// listing is re-read and re-rendered only when it really differs, so scroll
    /// position and surviving selections are kept.
    /// </summary>
    public partial class FolderWidget
    {
        private readonly FolderWatcher _browseWatcher =
            new FolderWatcher(WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS);

        private DispatcherTimer _browseRetryTimer;

        /// <summary>
        /// Brings the watcher in line with the panel: it follows the folder on
        /// screen exactly while the panel is expanded and browsing it.
        /// </summary>
        private void UpdateBrowseWatch()
        {
            string path = _isExpanded && IsBrowsing ? CurrentBrowsePath : null;
            if (path != null && !Directory.Exists(path)) path = null;

            if (path == null) _browseWatcher.Stop();
            else _browseWatcher.Start(path);
        }

        /// <summary>The watcher reports on a pool thread; the panel lives on the UI thread.</summary>
        private void OnBrowseFolderChanged()
        {
            Dispatcher.BeginInvoke(new Action(RefreshBrowseIfChanged));
        }

        private void RefreshBrowseIfChanged()
        {
            if (!_isExpanded || !IsBrowsing) return;

            // Never re-render under an active gesture or dialog: retry shortly.
            if (_isDraggingItem || _isLassoSelecting || _modalDepth > 0)
            {
                RetryBrowseRefresh();
                return;
            }

            string path = CurrentBrowsePath;
            if (path == null || !Directory.Exists(path))
            {
                // The folder itself is gone: show the "cannot open" state, then
                // stop following it (UpdateBrowseWatch disposes the watcher).
                UpdateUI();
                UpdateBrowseWatch();
                return;
            }

            var fresh = FolderListing.ListChildren(path, WidgetConstants.MAX_BROWSE_ENTRIES);
            if (FolderListing.SameEntries(_browseListing, fresh)) return;

            var selected = new HashSet<string>(
                GetSelectedItems().Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            double scrollOffset = ItemsScroller.VerticalOffset;

            UpdateUI();

            if (selected.Count > 0 && ItemsContainer.ItemsSource is List<DisplayItem> items)
            {
                foreach (var item in items)
                {
                    if (selected.Contains(item.Path)) item.IsSelected = true;
                }
            }

            ItemsScroller.ScrollToVerticalOffset(scrollOffset);
        }

        /// <summary>Retries the refresh while the user is mid-gesture.</summary>
        private void RetryBrowseRefresh()
        {
            if (_browseRetryTimer == null)
            {
                _browseRetryTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS)
                };
                _browseRetryTimer.Tick += (s, e) =>
                {
                    _browseRetryTimer.Stop();
                    RefreshBrowseIfChanged();
                };
            }

            _browseRetryTimer.Stop();
            _browseRetryTimer.Start();
        }
    }
}
