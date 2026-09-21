using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kobold.Core;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - Main class with core functionality
    /// Partial classes: Theme.cs, Dialogs.cs, Interactions.cs
    /// Implements INotifyPropertyChanged for data binding
    /// </summary>
    public partial class FolderWidget : Window, INotifyPropertyChanged
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
        private bool _isExpanded = false;
        private Point _dragStartCursor;      // Screen cursor position at mouse-down (click vs drag)
        private Point _dragLastCursor;       // Screen cursor position of the last drag step
        private double _dragDpiScale = 1.0;   // Physical pixels per WPF unit (cursor -> window)
        private bool _isDraggingWindow = false;
        
        // Item drag-drop fields
        private bool _isDraggingItem = false;
        private Point _itemDragStartPos;
        private DisplayItem _draggedItem = null;

        private int _lastItemCount;
        private bool _lastFooterVisible;
        
        // Constants from WidgetConstants for local access
        private const double DRAG_THRESHOLD = WidgetConstants.DRAG_THRESHOLD;
        private const int BASE_ITEM_WIDTH = WidgetConstants.BASE_ITEM_WIDTH;
        private const int BASE_ITEM_HEIGHT = WidgetConstants.BASE_ITEM_HEIGHT;
        private const int ITEM_MARGIN = WidgetConstants.ITEM_MARGIN;
        private const int HEADER_HEIGHT = WidgetConstants.HEADER_HEIGHT;
        private const int PADDING = WidgetConstants.PADDING;

        // The panel's own border (1 DIP per side) sits outside the scroll viewport,
        // so it has to be budgeted on top of PADDING or the last column overflows.
        private const int PANEL_BORDER = 2;
        
        // Get scaled item dimensions. The items sit under a LayoutTransform, so the
        // margin scales with the content: the real footprint is (base + margin) x
        // scale, rounded UP. Rounding down leaves the panel a fraction of a pixel
        // short of a whole number of rows - the ScrollViewer then shows a scrollbar
        // that steals width, which makes every cell narrower than its item and clips
        // the item border on the right.
        private int GetScaledItemWidth() => (int)Math.Ceiling((BASE_ITEM_WIDTH + ITEM_MARGIN) * GetItemScale());
        private int GetScaledItemHeight() => (int)Math.Ceiling((BASE_ITEM_HEIGHT + ITEM_MARGIN) * GetItemScale());
        
        private double GetItemScale()
        {
            try { return WidgetManager.Instance.Config.ItemScale; }
            catch { return 1.3; } // Default if manager not ready
        }
        
        #endregion

        #region Properties and Events

        public event Action<FolderWidget> OnDeleted;
        public event Action OnDataChanged;
        public FolderData Data => _data;
        public string FolderId => _data.Id;
        public bool IsPanelOpen => _isExpanded;
        
        /// <summary>
        /// Grid columns for XAML binding - automatically updates UniformGrid
        /// </summary>
        public int GridColumns
        {
            get => _data?.GridColumns ?? 3;
            set
            {
                if (_data != null && _data.GridColumns != value)
                {
                    _data.GridColumns = value;
                    OnPropertyChanged(nameof(GridColumns));
                }
            }
        }
        
        #endregion

        #region Constructor

        public FolderWidget(FolderData data)
        {
            InitializeComponent();
            _data = data;
            Left = data.PosX;
            Top = data.PosY;
            
            // Never leave the grabbing cursor stuck if capture is lost mid-drag.
            PanelHeader.LostMouseCapture += (s, e) => System.Windows.Input.Mouse.OverrideCursor = null;
            
            // Subscribe to theme changes for automatic updates
            ThemeManager.ThemeChanged += OnThemeChanged;
            
            Loaded += (s, e) =>
            {
                // Apply theme colors (panel colors + item text) and pin state
                RefreshTheme();
                UpdatePinButtonVisual();
            };
            
            Closing += (s, e) =>
            {
                // Unsubscribe from theme changes
                ThemeManager.ThemeChanged -= OnThemeChanged;
            };
        }
        
        private void OnThemeChanged()
        {
            Dispatcher.Invoke(() => RefreshTheme());
        }
        
        #endregion

        #region UI Updates

        public void UpdateUI()
        {
            PanelHeaderText.Text = IsBrowsing ? BrowsePath.Label(CurrentBrowsePath) : _data.Name;
            PanelHeaderText.ToolTip = IsBrowsing ? CurrentBrowsePath : null;
            BackButton.Visibility = IsBrowsing ? Visibility.Visible : Visibility.Collapsed;
            BackButton.ToolTip = Localization.Get("UI_BrowseBack");
            PathButton.Visibility = IsBrowsing ? Visibility.Visible : Visibility.Collapsed;
            PathButton.ToolTip = IsBrowsing ? CurrentBrowsePath : null;
            UpdatePinButtonVisual();
            BadgeHelpTitle.Text = Localization.Get("UI_BadgeHelpTitle");
            BadgeHelpStored.Text = Localization.Get("UI_BadgeHelpStored");
            BadgeHelpMissing.Text = Localization.Get("UI_BadgeHelpMissing");

            // Lock indicator (header button)
            UpdateLockButtonVisual();

            // Apply item scale transform BEFORE setting items
            double scale = GetItemScale();
            ItemsContainer.LayoutTransform = new ScaleTransform(scale, scale);

            var items = IsBrowsing ? BuildBrowseItems() : BuildRootItems();
            _lastItemCount = items.Count;

            // Set ItemsSource - BindableUniformGrid.BindableColumns is bound to GridColumns property
            ItemsContainer.ItemsSource = items;
            LoadItemIcons(items);

            // Must run before sizing: CalculatePanelSize reads the footer's height
            UpdateEmptyState(items.Count);
            UpdateMoreItemsFooter(items.Count);

            // Panel-only: keep the window sized to the panel while open
            if (_isExpanded)
            {
                UpdateLayout();

                var (panelWidth, panelHeight) = CalculatePanelSize();

                ExpandedPanel.Width = panelWidth;
                ExpandedPanel.Height = panelHeight;

                Width = panelWidth;
                Height = panelHeight;

                System.Windows.Controls.Canvas.SetLeft(ExpandedPanel, 0);
                System.Windows.Controls.Canvas.SetTop(ExpandedPanel, 0);
            }

            // Update panel colors
            UpdatePanelColor();

            // Apply item text colors after items are rendered
            Dispatcher.BeginInvoke(new Action(() => ApplyItemTextColors()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

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

            _lastFooterVisible = show;
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


