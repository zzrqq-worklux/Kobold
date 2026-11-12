using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;
using Newtonsoft.Json.Linq;

namespace Kobold.Controls
{
    internal interface IFolderPanelHost : IWidgetViewHost
    {
        IntPtr PanelHwnd { get; }
        bool IsLocked { get; }
        bool IsPanelPinned { get; }
        bool IsModalOpen { get; }
        void RunModal(Action show);
        void HidePanel();
        void FitPanelToWorkArea();
    }

    /// <summary>
    /// The optional tab-surface hooks a group host drives the folder through:
    /// key routing, host deactivation, the source-initialized HWND and the
    /// resize re-adapt. Todo/Note/Search views do not implement it, so the
    /// group never grows requirements on them.
    /// </summary>
    internal interface IFolderPanelLifecycle
    {
        void HandleKeyDown(KeyEventArgs e);
        void HandleDeactivated();
        void HandleSourceInitialized();
        void ApplyAdaptiveLayout();
        void OnDeleting();
        void DeactivateForPanelHide();
        string ChromeTitleTooltip { get; }
    }

    /// <summary>The folder content, hosted by a standalone or shared chrome.</summary>
    public partial class FolderPanelView : UserControl, IWidgetContentView, IFolderPanelLifecycle, INotifyPropertyChanged
    {
        private FolderData _data;
        private IWidgetViewHost _host;

        // The explicit active state: only the active tab owns the chrome
        // accessories, the shell subscriptions, the native menu hook and the
        // browse watcher. Attach/RebindHost never install any of them.
        private bool _isActive;
        private WidgetChrome _installedChrome;
        private WidgetChrome _subscribedChrome;
        private StackPanel _trailingSlot;

        private WidgetChrome Chrome => _host?.Chrome;
        private IFolderPanelHost FolderHost => _host as IFolderPanelHost;
        private bool IsPanelOpen => _host != null && _host.IsPanelOpen;
        private bool IsLocked => FolderHost != null ? FolderHost.IsLocked : (_data != null && _data.IsLocked);
        private bool IsPanelPinned => FolderHost != null ? FolderHost.IsPanelPinned : (_data != null && _data.IsPanelPinned);
        private IntPtr PanelHwnd => FolderHost?.PanelHwnd ?? IntPtr.Zero;
        private bool IsModalOpen => FolderHost != null && FolderHost.IsModalOpen;
        private Window HostWindow => Window.GetWindow(this);

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool _isDraggingItem;
        private Point _itemDragStartPos;
        private DisplayItem _draggedItem;
        private int _lastItemCount;
        private int _iconLoadGeneration;
        private int _adaptiveColumns = WidgetConstants.DEFAULT_GRID_COLUMNS;
        private const double DRAG_THRESHOLD = WidgetConstants.DRAG_THRESHOLD;

        public FolderPanelView()
        {
            InitializeComponent();

            // The content stays in the view; only the header/footer accessories
            // move into a real host's chrome. A chrome-less preview gets the same
            // visible content surface.
            SlotSource.Children.Remove(ContentPanel);
            RootGrid.Children.Insert(0, ContentPanel);
            _browseWatcher.Changed += OnBrowseFolderChanged;
        }

        public int GridColumns => _adaptiveColumns;
        public bool HasTitleMenu => false;

        /// <summary>
        /// Read-only state for the control plane: identity, current browse path,
        /// whether the live directory watcher runs, the last rendered item
        /// count and the explicit active-tab flag. UI thread only.
        /// </summary>
        internal JObject FolderDiagnosticsSnapshot()
        {
            string browsePath = CurrentBrowsePath;
            return new JObject
            {
                ["widgetId"] = _data == null ? JValue.CreateNull() : new JValue(_data.Id),
                ["name"] = _data == null ? JValue.CreateNull() : new JValue(_data.Name),
                ["browsePath"] = browsePath == null ? JValue.CreateNull() : new JValue(browsePath),
                ["watcherActive"] = _browseWatcher.IsWatching,
                ["itemCount"] = _lastItemCount,
                ["active"] = _isActive
            };
        }

        public string ChromeTitle
        {
            get
            {
                var selected = GetSelectedItems();
                if (selected.Count == 1) return GetDisplayName(selected[0].Path);
                if (selected.Count > 1)
                    return Localization.Format("UI_SelectedCountFormat", selected.Count);
                return IsBrowsing ? BrowsePath.Label(CurrentBrowsePath) : _data?.Name;
            }
        }

        WidgetData IWidgetContentView.Data => _data;
        FrameworkElement IWidgetContentView.View => this;

        public void Attach(FolderData data, FolderWidget host)
        {
            AttachCore(data, host);
        }

        void IWidgetContentView.Attach(WidgetData data, IWidgetViewHost host)
        {
            AttachCore(data as FolderData, host);
        }

        private void AttachCore(FolderData data, IWidgetViewHost host)
        {
            // Attach never installs chrome: a new group member starts inert,
            // and only Activate may claim the active tab's slots.
            DetachHost();
            _isActive = false;
            _data = data ?? new FolderData();
            _host = host;
            _browseStack.Clear();
            _browseStack.AddRange(BrowsePath.NormalizeStack(_data.BrowseStack, System.IO.Directory.Exists));
            UpdateUI();
        }

        void IWidgetContentView.RebindHost(IWidgetViewHost host)
        {
            // Move the one live view to the new host: drop the old chrome
            // ownership/subscriptions, never rebuild the view, and stay
            // inactive until the new host activates the tab.
            DetachHost();
            _isActive = false;
            _host = host;
            Refresh();
        }

        private void AttachHost()
        {
            WidgetChrome chrome = Chrome;
            if (chrome != null && !ReferenceEquals(_subscribedChrome, chrome))
            {
                DetachShellSubscriptions();
                chrome.Shell.MouseLeftButtonDown += PanelShell_MouseLeftButtonDown;
                chrome.Shell.MouseRightButtonDown += PanelShell_MouseRightButtonDown;
                _subscribedChrome = chrome;
            }
            EnsureMenuHook();
        }

        private void DetachShellSubscriptions()
        {
            WidgetChrome chrome = _subscribedChrome;
            _subscribedChrome = null;
            if (chrome == null) return;

            chrome.Shell.MouseLeftButtonDown -= PanelShell_MouseLeftButtonDown;
            chrome.Shell.MouseRightButtonDown -= PanelShell_MouseRightButtonDown;
        }

        private void DetachHost()
        {
            Deactivate();
            RemoveMenuHook();
            _browseWatcher.Stop();
            _browseRetryTimer?.Stop();
        }

        internal void Detach()
        {
            Deactivate();
            DetachShellSubscriptions();
            RemoveMenuHook();
            _browseWatcher.Changed -= OnBrowseFolderChanged;
            _browseWatcher.Dispose();
            _host = null;
        }

        public void Activate()
        {
            _isActive = true;
            InstallChromeSlots();
            AttachHost();

            // Do not rebuild the whole list on every tab switch: a browse
            // refresh only re-renders when the directory really changed (and
            // preserves scroll/selection); root items were built by Attach.
            if (IsBrowsing) RefreshBrowseIfChanged();
            else RefreshChromeTitle();
            UpdateBrowseWatch();
        }

        public void Deactivate()
        {
            DeactivateCore(removeChrome: true);
        }

        /// <summary>
        /// The panel's close fade is still showing this surface: stop input and
        /// watching, but keep the installed header/footer accessories in place
        /// so the fading header does not lose its buttons. A tab switch or a
        /// real close still uses the full <see cref="Deactivate"/>.
        /// </summary>
        internal void DeactivateForPanelHide()
        {
            DeactivateCore(removeChrome: false);
        }

        private void DeactivateCore(bool removeChrome)
        {
            _isActive = false;
            ClearAllSelections();
            _browseWatcher.Stop();
            _browseRetryTimer?.Stop();
            RemoveMenuHook();

            if (!removeChrome) return;
            DetachShellSubscriptions();
            RemoveChromeSlots();
        }

        void IFolderPanelLifecycle.HandleKeyDown(KeyEventArgs e) => HandleKeyDown(e);
        void IFolderPanelLifecycle.HandleDeactivated() => ClearAllSelections();
        void IFolderPanelLifecycle.HandleSourceInitialized() => EnsureMenuHook();
        void IFolderPanelLifecycle.ApplyAdaptiveLayout() => ApplyAdaptiveLayout();
        void IFolderPanelLifecycle.OnDeleting() => OnDeleting();
        void IFolderPanelLifecycle.DeactivateForPanelHide() => DeactivateForPanelHide();
        string IFolderPanelLifecycle.ChromeTitleTooltip => ChromeTitleTooltip;

        internal void HandleSourceInitialized() => EnsureMenuHook();
        internal void HandleDeactivated() => ClearAllSelections();

        public void Refresh()
        {
            if (_data == null || _host == null) return;
            UpdateUI();
        }

        void IWidgetContentView.RefreshChromeTitle() => RefreshChromeTitle();
        void IWidgetContentView.ToggleTitleMenu(FrameworkElement anchor) { }
        void IWidgetContentView.HandleMouseDownOutsideEditor(object source) { }
        public void FlushPendingEdit() { }
        public bool TryUndo() => false;

        private void NotifyDataChanged() => _host?.NotifyDataChanged();

        private void RunModal(Action show)
        {
            if (FolderHost != null) FolderHost.RunModal(show);
            else show?.Invoke();
        }

        private void HidePanel() => FolderHost?.HidePanel();

        private double GetItemScale()
        {
            try { return WidgetManager.Instance.Config.ItemScale; }
            catch { return 1.1; }
        }

        private void InstallChromeSlots()
        {
            WidgetChrome chrome = Chrome;
            if (chrome == null) return;

            // A host hand-off never moves an element with a logical parent:
            // release any old chrome first, then claim the new one.
            if (_installedChrome != null && !ReferenceEquals(_installedChrome, chrome))
            {
                RemoveChromeSlots();
            }
            if (ReferenceEquals(_installedChrome, chrome)) return;

            DetachFromParent(BackButton);
            DetachFromParent(PathButton);
            DetachFromParent(HelpButton);
            DetachFromParent(MoreItemsText);

            chrome.HeaderLeading.Content = BackButton;

            var trailing = _trailingSlot;
            if (trailing == null)
            {
                trailing = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _trailingSlot = trailing;
            }
            trailing.Children.Remove(PathButton);
            trailing.Children.Remove(HelpButton);
            trailing.Children.Add(PathButton);
            trailing.Children.Add(HelpButton);
            chrome.HeaderTrailing.Content = trailing;
            chrome.FooterHost.Content = MoreItemsText;
            RootGrid.Children.Remove(SlotSource);
            _installedChrome = chrome;
        }

        private void RemoveChromeSlots()
        {
            WidgetChrome chrome = _installedChrome;
            _installedChrome = null;
            if (chrome == null) return;

            if (ReferenceEquals(chrome.HeaderLeading.Content, BackButton))
            {
                chrome.HeaderLeading.Content = null;
            }

            StackPanel trailing = _trailingSlot;
            _trailingSlot = null;
            if (trailing != null && ReferenceEquals(chrome.HeaderTrailing.Content, trailing))
            {
                trailing.Children.Remove(PathButton);
                trailing.Children.Remove(HelpButton);
                chrome.HeaderTrailing.Content = null;
            }

            if (ReferenceEquals(chrome.FooterHost.Content, MoreItemsText))
            {
                chrome.FooterHost.Content = null;
            }
        }

        /// <summary>Removes an accessory from a staging panel or a slot parent.</summary>
        private static void DetachFromParent(FrameworkElement element)
        {
            if (element == null) return;

            var panel = element.Parent as Panel;
            if (panel != null)
            {
                panel.Children.Remove(element);
                return;
            }

            var content = element.Parent as ContentControl;
            if (content != null && ReferenceEquals(content.Content, element))
            {
                content.Content = null;
            }
        }

        public void UpdateUI()
        {
            RefreshChromeTitle();
            BackButton.Visibility = IsBrowsing ? Visibility.Visible : Visibility.Collapsed;
            BackButton.ToolTip = Localization.Get("UI_BrowseBack");
            PathButton.Visibility = IsBrowsing ? Visibility.Visible : Visibility.Collapsed;
            PathButton.ToolTip = IsBrowsing ? CurrentBrowsePath : null;
            BadgeHelpTitle.Text = Localization.Get("UI_BadgeHelpTitle");
            BadgeHelpStored.Text = Localization.Get("UI_BadgeHelpStored");
            BadgeHelpMissing.Text = Localization.Get("UI_BadgeHelpMissing");

            var items = IsBrowsing ? BuildBrowseItems() : BuildRootItems();
            _lastItemCount = items.Count;
            ItemsContainer.ItemsSource = items;

            UpdateEmptyState(items.Count);
            UpdateMoreItemsFooter(items.Count);
            ApplyAdaptiveLayout();
            LoadItemIcons(items);
            UpdatePanelColor();

            Dispatcher.BeginInvoke(new Action(() => ApplyItemTextColors()),
                System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateBrowseWatch();
        }

        internal string ChromeTitleTooltip
        {
            get
            {
                var selected = GetSelectedItems();
                if (selected.Count == 1) return selected[0].Path;
                return selected.Count == 0 && IsBrowsing ? CurrentBrowsePath : null;
            }
        }

        internal void RefreshChromeTitle()
        {
            // An inactive tab must never overwrite the active tab's title.
            if (!_isActive || Chrome == null) return;

            Chrome.Title = ChromeTitle;
            Chrome.TitleText.ToolTip = ChromeTitleTooltip;
        }

        internal void ApplyAdaptiveLayout()
        {
            double contentW = _host?.ContentWidth ?? 0;
            double contentH = _host?.ContentHeight ?? 0;
            if (MoreItemsText.Visibility == Visibility.Visible) contentH -= WidgetConstants.FOOTER_HEIGHT;
            var layout = PanelLayoutAdapter.AdaptFolder(contentW, contentH, _lastItemCount, GetItemScale());
            ItemsContainer.LayoutTransform = new ScaleTransform(layout.Scale, layout.Scale);
            if (_adaptiveColumns != layout.Columns)
            {
                _adaptiveColumns = layout.Columns;
                OnPropertyChanged(nameof(GridColumns));
            }
        }

        private void LoadItemIcons(List<DisplayItem> items)
        {
            var realIcons = new List<DisplayItem>();
            var extraFolders = new List<DisplayItem>();
            var genericFiles = new List<DisplayItem>();

            if (!IsBrowsing)
            {
                realIcons.AddRange(items);
            }
            else
            {
                int fileBudget = WidgetConstants.MAX_BROWSE_ICON_LOADS;
                foreach (var item in items)
                {
                    if (item.IsDirectory) extraFolders.Add(item);
                    else if (realIcons.Count < fileBudget) realIcons.Add(item);
                    else genericFiles.Add(item);
                }
            }

            int generation = ++_iconLoadGeneration;
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var item in realIcons)
                {
                    if (generation != System.Threading.Volatile.Read(ref _iconLoadGeneration)) return;
                    ApplyIcon(item, GetFileIcon(item.Path));
                }
                foreach (var folder in extraFolders)
                {
                    if (generation != System.Threading.Volatile.Read(ref _iconLoadGeneration)) return;
                    ApplyIcon(folder, GetFileIcon(folder.Path));
                }
                if (genericFiles.Count > 0)
                {
                    if (generation != System.Threading.Volatile.Read(ref _iconLoadGeneration)) return;
                    var generic = GetGenericFileIcon();
                    foreach (var item in genericFiles) ApplyIcon(item, generic);
                }
            });
        }

        private void ApplyIcon(DisplayItem item, ImageSource icon)
        {
            if (icon == null) return;

            try
            {
                if (!icon.IsFrozen) icon.Freeze();
                Dispatcher.BeginInvoke(new Action(() => item.Icon = icon),
                    System.Windows.Threading.DispatcherPriority.Normal);
            }
            catch { }
        }

        private void UpdateEmptyState(int itemCount)
        {
            if (itemCount > 0)
            {
                EmptyState.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyState.Visibility = Visibility.Visible;
            if (!IsBrowsing)
            {
                EmptyText.Text = Localization.Get("UI_DropHere");
                return;
            }

            EmptyText.Text = Localization.Get(_browseListing != null && _browseListing.Failed
                ? "UI_FolderAccessDenied"
                : "UI_FolderEmpty");
        }

        private void UpdateMoreItemsFooter(int itemCount)
        {
            bool show = IsBrowsing && _browseListing != null && _browseListing.Truncated;
            MoreItemsText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                MoreItemsText.Text = Localization.Format("UI_BrowseMoreItems",
                    _browseListing.TotalCount - itemCount);
            }
        }

        private string GetDisplayName(string path)
        {
            string fileName = System.IO.Path.GetFileName(path);
            if (fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                return fileName.Substring(0, fileName.Length - 4);
            return fileName;
        }
    }
}
