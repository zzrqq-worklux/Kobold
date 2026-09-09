using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FoldRa.Core;
using Localization = FoldRa.Core.Localization;

namespace FoldRa.Controls
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
        private Point _mouseDownPos;
        private Point _mouseDownScreenPos;
        private bool _isDraggingWindow = false;
        
        // Item drag-drop fields
        private bool _isDraggingItem = false;
        private Point _itemDragStartPos;
        private DisplayItem _draggedItem = null;
        
        // Constants from WidgetConstants for local access
        private const double DRAG_THRESHOLD = WidgetConstants.DRAG_THRESHOLD;
        private const int WIDGET_WIDTH = WidgetConstants.WIDGET_WIDTH;
        private const int WIDGET_HEIGHT = WidgetConstants.WIDGET_HEIGHT;
        private const int ICON_SPACING = WidgetConstants.ICON_SPACING;
        private const int DEFAULT_PANEL_LEFT = WidgetConstants.DEFAULT_PANEL_LEFT;
        private const int BASE_ITEM_WIDTH = WidgetConstants.BASE_ITEM_WIDTH;
        private const int BASE_ITEM_HEIGHT = WidgetConstants.BASE_ITEM_HEIGHT;
        private const int ITEM_MARGIN = WidgetConstants.ITEM_MARGIN;
        private const int HEADER_HEIGHT = WidgetConstants.HEADER_HEIGHT;
        private const int PADDING = WidgetConstants.PADDING;
        
        // Get scaled item dimensions
        private int GetScaledItemWidth() => (int)(BASE_ITEM_WIDTH * GetItemScale()) + ITEM_MARGIN;
        private int GetScaledItemHeight() => (int)(BASE_ITEM_HEIGHT * GetItemScale()) + ITEM_MARGIN;
        
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
            
            // Subscribe to theme changes for automatic updates
            ThemeManager.ThemeChanged += OnThemeChanged;
            
            Loaded += (s, e) =>
            {
                // Apply theme colors (includes DrawFolderIcon, UpdatePanelColor, UpdateUI)
                RefreshTheme();
                UpdatePinButtonVisual();
                
                // If panel was pinned, auto-open it after layout is ready
                if (_data.IsPanelPinned)
                {
                    // Wait for layout to complete before opening panel
                    Dispatcher.BeginInvoke(new Action(() => 
                    {
                        UpdateLayout(); // Force layout update
                        TogglePanel();
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                
                this.Opacity = 0;
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                this.BeginAnimation(OpacityProperty, fadeIn);
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
            FolderNameText.Text = _data.Name;
            PanelHeaderText.Text = _data.Name;
            
            // Badge
            if (_data.Items.Count > 0)
            {
                BadgeBorder.Visibility = Visibility.Visible;
                BadgeBorder.Background = new SolidColorBrush(Utils.HexToColor(_data.Color));
                BadgeText.Text = _data.Items.Count > 99 ? "99+" : _data.Items.Count.ToString();
            }
            else
            {
                BadgeBorder.Visibility = Visibility.Collapsed;
            }
            
            // Lock indicator
            LockBadge.Visibility = _data.IsLocked ? Visibility.Visible : Visibility.Collapsed;
            
            // Apply item scale transform BEFORE setting items
            double scale = GetItemScale();
            ItemsContainer.LayoutTransform = new ScaleTransform(scale, scale);
            
            // Items - include index for drag-drop reordering
            var textBrush = ThemeManager.TextBrush;
            
            var items = _data.Items.Select((item, index) => new DisplayItem
            {
                Name = GetDisplayName(string.IsNullOrEmpty(item.Name) ? item.Path : item.Name),
                Path = item.Path,
                Icon = null,
                Index = index,
                TextColor = textBrush
            }).ToList();
            
            // Set ItemsSource - BindableUniformGrid.BindableColumns is bound to GridColumns property
            ItemsContainer.ItemsSource = items;
            
            // Load icons asynchronously
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var item in items)
                {
                    try
                    {
                        var icon = GetFileIcon(item.Path);
                        if (icon != null)
                        {
                            icon.Freeze();
                            Dispatcher.BeginInvoke(new Action(() => item.Icon = icon), 
                                System.Windows.Threading.DispatcherPriority.Normal);
                        }
                    }
                    catch { /* Ignore icon load errors */ }
                }
            });
            
            // Dynamically resize panel if it is open to fit new content
            if (_isExpanded)
            {
                // Force layout update to ensure all elements are measured correctly before resizing
                UpdateLayout();
                
                var (panelWidth, panelHeight) = CalculatePanelSize();
                
                ExpandedPanel.Width = panelWidth;
                ExpandedPanel.Height = panelHeight;
                
                // Update Window Dimensions
                double totalWidth = WIDGET_WIDTH + ICON_SPACING + panelWidth;
                Width = totalWidth;
                Height = Math.Max(110, panelHeight);
                
                if (_openedToLeft)
                {
                    // Adjust window Left so that the folder icon stays stationary
                    Left = _originalLeft - panelWidth - ICON_SPACING;
                    
                    // Ensure Canvas positions are correct
                    System.Windows.Controls.Canvas.SetLeft(ExpandedPanel, 0);
                    System.Windows.Controls.Canvas.SetLeft(FolderIconGrid, panelWidth + ICON_SPACING);
                }
            }
            
            // Empty state
            EmptyState.Visibility = _data.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = Localization.Get("UI_DropHere");
            
            // Update panel colors
            UpdatePanelColor();
            
            // Apply item text colors after items are rendered
            Dispatcher.BeginInvoke(new Action(() => ApplyItemTextColors()), 
                System.Windows.Threading.DispatcherPriority.Loaded);
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


