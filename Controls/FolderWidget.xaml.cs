using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - Main class with core functionality
    /// Partial classes: Theme.cs, Dialogs.cs, Interactions.cs
    /// Implements INotifyPropertyChanged for data binding; the shared panel
    /// behaviour (show/hide, placement, lock/pin/drag, modals) lives in
    /// <see cref="WidgetWindowBase"/>.
    /// </summary>
    public partial class FolderWidget : WidgetWindowBase, INotifyPropertyChanged
    {
        #region INotifyPropertyChanged
        
        public event PropertyChangedEventHandler PropertyChanged;
        
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
        #endregion
        
        #region Fields
        
        private FolderData _data;
        
        // Item drag-drop fields
        private bool _isDraggingItem = false;
        private Point _itemDragStartPos;
        private DisplayItem _draggedItem = null;

        private int _lastItemCount;

        // The adaptive layout's current column count (bound by the items grid).
        private int _adaptiveColumns = WidgetConstants.DEFAULT_GRID_COLUMNS;

        // Constants from WidgetConstants for local access
        private const double DRAG_THRESHOLD = WidgetConstants.DRAG_THRESHOLD;

        // Legacy item scale from config; only the engine's fallback size path
        // reads it now (the live layout scales its own cells).
        private double GetItemScale()
        {
            try { return WidgetManager.Instance.Config.ItemScale; }
            catch { return 1.3; } // Default if manager not ready
        }
        
        #endregion

        #region Properties

        public string FolderId => _data.Id;

        /// <summary>
        /// The panel's shell element (the chrome's border): its backdrop,
        /// hit-test root and the carrier of the widget-local selection brush
        /// resources. Exposed for the XamlLoadCheck pin that used to name
        /// ExpandedPanel directly.
        /// </summary>
        public FrameworkElement PanelShell => Chrome.Shell;

        /// <summary>
        /// Grid columns for XAML binding - derived from the panel size by the
        /// layout adapter. Read-only: the retired menu was the only writer.
        /// </summary>
        public int GridColumns => _adaptiveColumns;
        
        #endregion

        #region Constructor

        public FolderWidget(FolderData data) : base(data)
        {
            InitializeComponent();
            _data = data;
            Left = data.PosX;
            Top = data.PosY;

            // Browsing is remembered across hiding the panel and restarting the
            // app; a folder that is gone falls back to the widget's own items.
            // Must run before AttachChrome: its UpdateUI reads the browse stack.
            _browseStack.AddRange(BrowsePath.NormalizeStack(_data.BrowseStack, System.IO.Directory.Exists));

            // The shared chrome owns the shell, the lock/pin buttons and the
            // header drag/right-click input; AttachChrome wires all of it and
            // calls UpdateUI. The folder keeps its own panel-background input.
            InstallChromeSlots();
            AttachChrome(FolderChrome);
            Chrome.Shell.MouseLeftButtonDown += PanelShell_MouseLeftButtonDown;
            Chrome.Shell.MouseRightButtonDown += PanelShell_MouseRightButtonDown;

            // Folder changes on disk refresh the browse view (see Watch.cs).
            _browseWatcher.Changed += OnBrowseFolderChanged;

            // The native shell menu (Task 5) needs the panel's HWND to forward
            // WM_DRAWITEM/WM_MEASUREITEM/WM_INITMENUPOPUP to IContextMenu2/3.
            SourceInitialized += (s, e) => EnsureMenuHook();

            Closing += (s, e) => _browseWatcher.Dispose();
        }

        /// <summary>
        /// Moves the folder's header buttons, content grid and footer hint from
        /// the XAML staging grid into the chrome's slots. The chrome exposes
        /// its slots as fields (ContentPresenter), so the assignment has to
        /// happen here; the layout each slot used to have is unchanged.
        /// </summary>
        private void InstallChromeSlots()
        {
            // Detach from the staging grid first: WPF refuses to hand an element
            // that already has a logical parent to a ContentPresenter.
            SlotSource.Children.Remove(BackButton);
            SlotSource.Children.Remove(PathButton);
            SlotSource.Children.Remove(HelpButton);
            SlotSource.Children.Remove(ContentPanel);
            SlotSource.Children.Remove(MoreItemsText);

            FolderChrome.HeaderLeading.Content = BackButton;

            // Path + Help share the trailing slot (the old header had them in
            // separate columns; a horizontal stack keeps the same order/spacing
            // before the chrome's lock/pin).
            var trailing = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            trailing.Children.Add(PathButton);
            trailing.Children.Add(HelpButton);
            FolderChrome.HeaderTrailing.Content = trailing;

            FolderChrome.ContentHost.Content = ContentPanel;
            FolderChrome.FooterHost.Content = MoreItemsText;

            // The staging grid has served its purpose; drop it from the window.
            RootGrid.Children.Remove(SlotSource);
        }
        
        #endregion

        #region UI Updates

        public override void UpdateUI()
        {
            // Name / lock / pin / colour first; the browse title below wins
            // over the base's widget name while a folder is on screen.
            base.UpdateUI();

            Chrome.Title = IsBrowsing ? BrowsePath.Label(CurrentBrowsePath) : _data.Name;
            Chrome.TitleText.ToolTip = IsBrowsing ? CurrentBrowsePath : null;
            BackButton.Visibility = IsBrowsing ? Visibility.Visible : Visibility.Collapsed;
            BackButton.ToolTip = Localization.Get("UI_BrowseBack");
            PathButton.Visibility = IsBrowsing ? Visibility.Visible : Visibility.Collapsed;
            PathButton.ToolTip = IsBrowsing ? CurrentBrowsePath : null;
            BadgeHelpTitle.Text = Localization.Get("UI_BadgeHelpTitle");
            BadgeHelpStored.Text = Localization.Get("UI_BadgeHelpStored");
            BadgeHelpMissing.Text = Localization.Get("UI_BadgeHelpMissing");

            var items = IsBrowsing ? BuildBrowseItems() : BuildRootItems();
            _lastItemCount = items.Count;

            // Set ItemsSource - BindableUniformGrid.BindableColumns is bound to GridColumns property
            ItemsContainer.ItemsSource = items;

            // Footer visibility first: the engine's content height depends on it.
            UpdateEmptyState(items.Count);
            UpdateMoreItemsFooter(items.Count);

            // The single re-arrange entry: columns and scale both come from the
            // engine for the panel's current (live or persisted) size.
            ApplyAdaptiveLayout();

            LoadItemIcons(items);

            // Update panel colors
            UpdatePanelColor();

            // Apply item text colors after items are rendered
            Dispatcher.BeginInvoke(new Action(() => ApplyItemTextColors()),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // Enter/leave folders and theme changes all land here; keep the
            // watcher following the folder that is on screen now.
            UpdateBrowseWatch();
        }

        /// <summary>
        /// The single re-arrange entry: fits the grid into the panel's content
        /// area through the pure engine and publishes the column count and the
        /// icon scale the renderer consumes.
        /// </summary>
        private void ApplyAdaptiveLayout()
        {
            double panelW, panelH;
            if (IsPanelOpen) { panelW = Width; panelH = Height; }     // during a drag: the live window size
            else { (panelW, panelH) = GetPanelSize(); }               // before opening: the persisted size
            var border = Chrome.Shell.BorderThickness;
            double contentW = panelW - border.Left - border.Right;
            double contentH = panelH - WidgetConstants.HEADER_HEIGHT - border.Top - border.Bottom;
            if (MoreItemsText.Visibility == Visibility.Visible) contentH -= WidgetConstants.FOOTER_HEIGHT;
            var layout = PanelLayoutAdapter.AdaptFolder(contentW, contentH, _lastItemCount);
            ItemsContainer.LayoutTransform = new ScaleTransform(layout.Scale, layout.Scale);
            if (_adaptiveColumns != layout.Columns)
            {
                _adaptiveColumns = layout.Columns;
                OnPropertyChanged(nameof(GridColumns));
            }
        }

        protected override void OnPanelSizeChanged() => ApplyAdaptiveLayout();

        private void LoadItemIcons(List<DisplayItem> items)
        {
            var realIcons = new List<DisplayItem>();
            var extraFolders = new List<DisplayItem>();
            var genericFiles = new List<DisplayItem>();

            if (!IsBrowsing)
            {
                realIcons.AddRange(items); // root mode is unchanged: every icon loads
            }
            else
            {
                // The budget counts files only: directories share one cached icon,
                // so a folder-heavy listing must not spend the budget on them.
                int fileBudget = WidgetConstants.MAX_BROWSE_ICON_LOADS;
                foreach (var item in items)
                {
                    if (item.IsDirectory) extraFolders.Add(item);
                    else if (realIcons.Count < fileBudget) realIcons.Add(item);
                    else genericFiles.Add(item);
                }
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var item in realIcons) ApplyIcon(item, GetFileIcon(item.Path));
                foreach (var folder in extraFolders) ApplyIcon(folder, GetFileIcon(folder.Path));
                if (genericFiles.Count > 0)
                {
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
            catch { /* Ignore icon load errors */ }
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
        
        /// <summary>
        /// Gets display name for a file - removes .lnk extension from shortcuts
        /// </summary>
        private string GetDisplayName(string path)
        {
            string fileName = System.IO.Path.GetFileName(path);
            
            // Remove .lnk extension from shortcuts for cleaner display
            if (fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return fileName.Substring(0, fileName.Length - 4);
            }
            
            return fileName;
        }

        #endregion
    }
}


