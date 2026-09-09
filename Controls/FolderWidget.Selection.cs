using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FoldRa.Controls
{
    /// <summary>
    /// FolderWidget - Selection related methods (lasso selection, multi-select, clear)
    /// </summary>
    public partial class FolderWidget
    {
        #region Selection State
        
        private bool _isLassoSelecting = false;
        private Point _lassoStartPoint;
        
        #endregion
        
        #region Selection Methods
        
        private List<DisplayItem> GetSelectedItems()
        {
            var items = ItemsContainer.ItemsSource as List<DisplayItem>;
            if (items == null) return new List<DisplayItem>();
            return items.Where(i => i.IsSelected).ToList();
        }
        
        private void ClearAllSelections()
        {
            var items = ItemsContainer.ItemsSource as List<DisplayItem>;
            if (items == null) return;
            
            foreach (var item in items)
            {
                if (item.IsSelected)
                {
                    item.IsSelected = false;
                }
            }
            
            // Also hide selection rectangle if visible
            if (SelectionRect.Visibility == Visibility.Visible)
            {
                SelectionRect.Visibility = Visibility.Collapsed;
            }
        }
        
        /// <summary>
        /// Clears selection when clicking on panel background
        /// </summary>
        private void ExpandedPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only clear if clicking directly on panel background, not on items
            var hit = e.OriginalSource as DependencyObject;
            
            // Check if we clicked on an item
            while (hit != null && hit != ExpandedPanel)
            {
                if (hit is Border b && b.DataContext is DisplayItem)
                {
                    // Clicked on an item - don't clear
                    return;
                }
                hit = VisualTreeHelper.GetParent(hit);
            }
            
            // Clear selection if not clicking on an item
            if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
            {
                ClearAllSelections();
            }
        }
        
        #endregion
        
        #region Lasso Selection
        
        private void ItemsContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only start lasso if clicking on empty area (not on an item)
            var hitResult = VisualTreeHelper.HitTest(ItemsContainer, e.GetPosition(ItemsContainer));
            if (hitResult != null)
            {
                // Check if we hit an item border
                var element = hitResult.VisualHit as DependencyObject;
                while (element != null && element != ItemsContainer)
                {
                    if (element is Border b && b.DataContext is DisplayItem)
                    {
                        // Clicked on an item - don't start lasso
                        return;
                    }
                    element = VisualTreeHelper.GetParent(element);
                }
            }
            
            // Start lasso selection
            _isLassoSelecting = true;
            _lassoStartPoint = e.GetPosition(ItemsContainer);
            
            // Clear existing selection unless Ctrl is held
            if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
            {
                ClearAllSelections();
            }
            
            // Initialize selection rectangle
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            SelectionRect.Margin = new Thickness(_lassoStartPoint.X, _lassoStartPoint.Y, 0, 0);
            SelectionRect.Visibility = Visibility.Visible;
            
            Mouse.Capture(ItemsContainer);
            e.Handled = true;
        }
        
        private void ItemsContainer_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isLassoSelecting) return;
            
            var currentPoint = e.GetPosition(ItemsContainer);
            
            // Calculate rectangle bounds
            double x = Math.Min(_lassoStartPoint.X, currentPoint.X);
            double y = Math.Min(_lassoStartPoint.Y, currentPoint.Y);
            double width = Math.Abs(currentPoint.X - _lassoStartPoint.X);
            double height = Math.Abs(currentPoint.Y - _lassoStartPoint.Y);
            
            // Update selection rectangle
            SelectionRect.Margin = new Thickness(x, y, 0, 0);
            SelectionRect.Width = width;
            SelectionRect.Height = height;
            
            // Select items within rectangle
            var selectionBounds = new Rect(x, y, width, height);
            SelectItemsInRect(selectionBounds);
        }
        
        private void ItemsContainer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isLassoSelecting) return;
            
            _isLassoSelecting = false;
            SelectionRect.Visibility = Visibility.Collapsed;
            Mouse.Capture(null);
        }
        
        private void SelectItemsInRect(Rect selectionBounds)
        {
            // Minimum size to prevent selection on simple click
            if (selectionBounds.Width < 5 && selectionBounds.Height < 5)
                return;
            
            var items = ItemsContainer.ItemsSource as List<DisplayItem>;
            if (items == null) return;
            
            // Get the items panel (UniformGrid)
            var itemsPanel = GetItemsPanel(ItemsContainer);
            if (itemsPanel == null) return;
            
            for (int i = 0; i < items.Count; i++)
            {
                var container = ItemsContainer.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
                if (container == null) continue;
                
                // Get item bounds relative to ItemsContainer
                var itemBounds = container.TransformToAncestor(ItemsContainer)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                
                // Check if item intersects with selection rectangle
                bool intersects = selectionBounds.IntersectsWith(itemBounds);
                
                // If Ctrl is held, only add to selection, don't deselect
                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                {
                    if (intersects)
                        items[i].IsSelected = true;
                }
                else
                {
                    items[i].IsSelected = intersects;
                }
            }
        }
        
        private Panel GetItemsPanel(ItemsControl itemsControl)
        {
            var itemsPresenter = GetVisualChild<ItemsPresenter>(itemsControl);
            if (itemsPresenter == null) return null;
            return VisualTreeHelper.GetChild(itemsPresenter, 0) as Panel;
        }
        
        private T GetVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var descendant = GetVisualChild<T>(child);
                if (descendant != null) return descendant;
            }
            return null;
        }
        
        #endregion
    }
}


