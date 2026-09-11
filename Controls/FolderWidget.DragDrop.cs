using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - Drag-Drop operations (file drop, item reordering, desktop restore)
    /// </summary>
    public partial class FolderWidget
    {
        // Stored drag items for multi-selection support
        private List<DisplayItem> _currentDragItems;
        private bool _itemDragCanceled;
        
        #region External File Drop
        
        /// <summary>
        /// Adds externally dropped files as pure references: the source file stays
        /// in place (path-safe for projects like Obsidian vaults), and its desktop
        /// icon is hidden when the setting is enabled. Physical storing is an
        /// explicit per-item action from the context menu instead.
        /// </summary>
        private void AddExternalPaths(string[] paths)
        {
            string storagePath = Utils.GetStoragePath();
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string publicDesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            bool hideDesktopSource = WidgetManager.Instance.Config.HideDesktopSourceOnStore;

            foreach (var sourcePath in paths)
            {
                try
                {
                    // Already in storage - reference the stored item
                    if (StorageOps.IsUnder(sourcePath, storagePath))
                    {
                        if (!_data.Items.Any(i => i.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            _data.Items.Add(new WidgetItem(sourcePath, false));
                        }
                        continue;
                    }

                    // Skip duplicates
                    if (_data.Items.Any(i => i.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // Pure reference - never move the source
                    _data.Items.Add(new WidgetItem(sourcePath, true));

                    // Optional: hide the desktop icon, keep the file intact.
                    // Both the user's and the shared desktop count as "desktop".
                    if (hideDesktopSource &&
                        (StorageOps.IsUnder(sourcePath, desktopPath) ||
                         StorageOps.IsUnder(sourcePath, publicDesktopPath)))
                    {
                        StorageOps.SetHidden(sourcePath, true);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Kobold] Drop failed: {ex.Message}");
                }
            }

            UpdateUI();
            OnDataChanged?.Invoke();
        }

        #endregion

        #region Item Drag (Reordering) & Selection

        private void Item_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && sender is Border b && b.DataContext is DisplayItem item)
            {
                // Double-click: Open file
                if (e.ClickCount == 2)
                {
                    try 
                    { 
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo 
                        { 
                            FileName = item.Path, 
                            UseShellExecute = true 
                        }); 
                    } 
                    catch (Exception ex) 
                    { 
                        System.Diagnostics.Debug.WriteLine($"[Kobold] Open failed: {ex.Message}"); 
                    }
                    e.Handled = true;
                    return;
                }
                
                // Single click: Selection handling
                _itemDragStartPos = e.GetPosition(this);
                _draggedItem = item;
                
                // Handle selection
                bool ctrlPressed = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
                
                if (ctrlPressed)
                {
                    // Ctrl+Click: Toggle selection
                    item.IsSelected = !item.IsSelected;
                }
                else
                {
                    // Normal click: If clicking unselected item, clear others and select this one
                    // If clicking selected item, keep selection (for multi-drag)
                    if (!item.IsSelected)
                    {
                        ClearAllSelections();
                        item.IsSelected = true;
                    }
                }
                
                Mouse.Capture(b);
            }
        }

        private void Item_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null)
                return;
            
            var currentPos = e.GetPosition(this);
            var diff = currentPos - _itemDragStartPos;
            
            // Check if drag threshold exceeded
            if (!_isDraggingItem && (Math.Abs(diff.X) > DRAG_THRESHOLD || Math.Abs(diff.Y) > DRAG_THRESHOLD))
            {
                _isDraggingItem = true;
                
                // Get all selected items (or just the dragged one if none selected)
                var selectedItems = GetSelectedItems();
                if (selectedItems.Count == 0)
                {
                    selectedItems = new List<DisplayItem> { _draggedItem };
                }
                
                // Store for use in Panel_QueryContinueDrag
                _currentDragItems = selectedItems;
                
                // Create drag visual
                var dragWindow = CreateDragVisual(selectedItems);
                
                // Start drag-drop operation with all selected items
                var dataObject = new DataObject();
                
                // For internal use
                dataObject.SetData("KoboldItems", selectedItems);
                dataObject.SetData("KoboldItem", _draggedItem); // Backward compatibility

                // Cross-widget move: where the items come from + a value snapshot
                dataObject.SetData("KoboldSourceFolderId", _data.Id);
                dataObject.SetData("KoboldWidgetItems", BuildDragSnapshot(selectedItems));
                
                // For external drop (file list)
                var fileList = new System.Collections.Specialized.StringCollection();
                foreach (var sel in selectedItems)
                {
                    fileList.Add(sel.Path);
                }
                dataObject.SetFileDropList(fileList);
                
                Mouse.Capture(null);
                
                // Use GiveFeedback to update drag visual position
                GiveFeedbackEventHandler feedbackHandler = (s, args) =>
                {
                    if (dragWindow != null && dragWindow.IsVisible)
                    {
                        var cursor = GetCursorInWindow();
                        dragWindow.Left = Left + cursor.X + 10;
                        dragWindow.Top = Top + cursor.Y + 10;
                    }

                    // Replace the OLE default drag cursor (arrow with a dashed
                    // box) - the ghost window is our drag visual.
                    args.UseDefaultCursors = false;
                    Mouse.OverrideCursor = args.Effects == DragDropEffects.None
                        ? Cursors.No
                        : CursorHelper.GrabHand;
                    args.Handled = true;
                };
                
                ((Border)sender).GiveFeedback += feedbackHandler;
                
                _itemDragCanceled = false;
                var session = DragDropSession.Begin(_data.Id, _data.IsLocked);
                try
                {
                    DragDrop.DoDragDrop((DependencyObject)sender, dataObject, DragDropEffects.Move);
                }
                finally
                {
                    ((Border)sender).GiveFeedback -= feedbackHandler;
                    dragWindow?.Close();
                    Mouse.OverrideCursor = null;
                }

                try
                {
                    // Another widget took the items over: forget them here without
                    // ejecting - the files themselves stay untouched.
                    if (session.AcceptedByOtherWidget)
                    {
                        RemoveDraggedItems(selectedItems);
                    }
                }
                finally
                {
                    DragDropSession.End();
                }
                
                _isDraggingItem = false;
                _draggedItem = null;
            }
        }

        private void Item_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Mouse.Capture(null);
            
            // Single click no longer opens - just ends drag tracking
            // Double click opens (handled separately)
            
            _isDraggingItem = false;
            _draggedItem = null;
        }
        
        /// <summary>
        /// Creates a transparent popup showing dragged item icons
        /// </summary>
        private Window CreateDragVisual(System.Collections.Generic.List<DisplayItem> items)
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                IsHitTestVisible = false,
                SizeToContent = SizeToContent.WidthAndHeight
            };
            
            // Create visual container
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Opacity = 0.7
            };
            
            // Show up to 4 icons
            int maxIcons = Math.Min(items.Count, 4);
            for (int i = 0; i < maxIcons; i++)
            {
                var item = items[i];
                var border = new Border
                {
                    Width = 48,
                    Height = 48,
                    Margin = new Thickness(i == 0 ? 0 : -20, 0, 0, 0), // Stack overlapping
                    Background = ThemeManager.DragGhostBrush,
                    CornerRadius = new CornerRadius(UiTokens.RadiusControl)
                };
                
                var img = new Image
                {
                    Source = item.Icon,
                    Width = 32,
                    Height = 32,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                
                border.Child = img;
                panel.Children.Add(border);
            }
            
            // Show count if more than 4 items
            if (items.Count > 4)
            {
                var countBadge = new Border
                {
                    Background = ThemeManager.AccentBlueBrush,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                countBadge.Child = new TextBlock
                {
                    Text = $"+{items.Count - 4}",
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold
                };
                panel.Children.Add(countBadge);
            }
            
            window.Content = panel;
            
            // Position at cursor
            var cursor = GetCursorInWindow();
            window.Left = Left + cursor.X + 10;
            window.Top = Top + cursor.Y + 10;
            
            window.Show();
            return window;
        }

        /// <summary>
        /// Cursor position in this window's DIP coordinates. Cursor.Position is
        /// in physical pixels, so it must be mapped through the window transform
        /// to stay correct on scaled displays (e.g. 200%).
        /// </summary>
        private Point GetCursorInWindow()
        {
            var physical = System.Windows.Forms.Cursor.Position;
            return PointFromScreen(new Point(physical.X, physical.Y));
        }
        
        // Selection methods moved to FolderWidget.Selection.cs
        
        #endregion

        #region Item Reorder Drop
        
        private void ItemsContainer_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("KoboldItem"))
            {
                // Items from another widget: they get merged, so there is no
                // insertion point to show - just accept or refuse.
                if (TryGetCrossWidgetSource(e.Data, out var sourceId))
                {
                    var session = DragDropSession.Current;
                    if (CanAcceptCrossWidgetDrag(session, sourceId))
                    {
                        e.Effects = DragDropEffects.Move;
                        session.MarkPendingMove();
                    }
                    else
                    {
                        e.Effects = DragDropEffects.None;
                        session?.MarkTargetRefused();
                    }

                    DropIndicator.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                    return;
                }

                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                
                // Show drop indicator at the shared edge between two items
                var pos = e.GetPosition(ItemsContainer);
                var (_, insertAfter, target) = GetItemAndInsertPosition(pos);
                
                if (target != null)
                {
                    // Map the item edge into the indicator's parent (the panel
                    // grid) so the line lands on the visual boundary at any scale.
                    var indicatorHost = (UIElement)DropIndicator.Parent;
                    double edgeX = insertAfter ? target.ActualWidth : 0;
                    var top = target.TranslatePoint(new Point(edgeX, 0), indicatorHost);
                    var bottom = target.TranslatePoint(new Point(edgeX, target.ActualHeight), indicatorHost);

                    // Mapped points start at the panel top, but the indicator's
                    // margin starts below the header row.
                    double rowTop = PanelHeader.ActualHeight;

                    // Center the line on the boundary and on the row height
                    double indicatorX = Math.Max(0, top.X - DropIndicator.Width / 2);
                    double indicatorY = Math.Max(0, top.Y - rowTop + (bottom.Y - top.Y - DropIndicator.Height) / 2);

                    DropIndicator.Margin = new Thickness(indicatorX, indicatorY, 0, 0);
                    DropIndicator.Visibility = Visibility.Visible;
                }
                else
                {
                    DropIndicator.Visibility = Visibility.Collapsed;
                }
            }
        }
        
        private void ItemsContainer_DragLeave(object sender, DragEventArgs e)
        {
            DropIndicator.Visibility = Visibility.Collapsed;
            DragDropSession.Current?.ClearTargetState();
        }
        
        private void ItemsContainer_Drop(object sender, DragEventArgs e)
        {
            // Hide indicator
            DropIndicator.Visibility = Visibility.Collapsed;

            // Items dragged from another widget: merge them into this one.
            if (TryGetCrossWidgetSource(e.Data, out var sourceId))
            {
                HandleCrossWidgetDrop(e, sourceId);
                e.Handled = true;
                return;
            }
            
            // Handle internal item reordering
            if (e.Data.GetDataPresent("KoboldItem"))
            {
                var draggedDisplayItem = e.Data.GetData("KoboldItem") as DisplayItem;
                if (draggedDisplayItem == null) return;
                
                var pos = e.GetPosition(ItemsContainer);
                var (targetDisplayItem, insertAfter, _) = GetItemAndInsertPosition(pos);
                
                var sourceItem = _data.Items.FirstOrDefault(i => i.Path == draggedDisplayItem.Path);
                if (sourceItem == null) return;
                
                int sourceIndex = _data.Items.IndexOf(sourceItem);
                
                if (targetDisplayItem != null && targetDisplayItem.Path != draggedDisplayItem.Path)
                {
                    var targetItem = _data.Items.FirstOrDefault(i => i.Path == targetDisplayItem.Path);
                    if (targetItem != null)
                    {
                        int targetIndex = _data.Items.IndexOf(targetItem);
                        
                        // Remove from old position
                        _data.Items.RemoveAt(sourceIndex);
                        
                        // Calculate new index (adjust if source was before target)
                        int newIndex = targetIndex;
                        if (sourceIndex < targetIndex)
                            newIndex--; // Adjust because we removed an item before target
                        
                        // Insert after if dropped on right half of target
                        if (insertAfter)
                            newIndex++;
                        
                        // Clamp to valid range
                        newIndex = Math.Max(0, Math.Min(newIndex, _data.Items.Count));
                        
                        _data.Items.Insert(newIndex, sourceItem);
                    }
                }
                else if (targetDisplayItem == null)
                {
                    // Dropped on empty area - move to end
                    _data.Items.RemoveAt(sourceIndex);
                    _data.Items.Add(sourceItem);
                }
                
                ForceRefreshUI();
                e.Handled = true;
                return;
            }
            
            // Handle external file drop (from desktop/explorer)
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Prevent drops if widget is locked
                if (_data.IsLocked)
                {
                    e.Handled = true;
                    return;
                }
                
                AddExternalPaths((string[])e.Data.GetData(DataFormats.FileDrop));
                ForceRefreshUI();
                e.Handled = true;
            }
        }
        
        /// <summary>
        /// Force complete UI refresh - fixes visual glitches on pinned panels
        /// </summary>
        private void ForceRefreshUI()
        {
            Dispatcher.Invoke(() =>
            {
                // Clear and rebuild ItemsSource for clean refresh
                ItemsContainer.ItemsSource = null;
                UpdateUI();
                OnDataChanged?.Invoke();
            }, System.Windows.Threading.DispatcherPriority.Send);
        }
        
        private (DisplayItem item, bool insertAfter, FrameworkElement target) GetItemAndInsertPosition(Point pos)
        {
            // Use the item container, not the first Border under the cursor: the
            // template nests Borders (icon, badges), which made the edge depend
            // on what was hovered. Containers give the full tile bounds, so both
            // neighbours share one stable boundary.
            var hitResult = VisualTreeHelper.HitTest(ItemsContainer, pos);
            if (hitResult == null) return (null, false, null);

            var container = ItemsControl.ContainerFromElement(ItemsContainer, hitResult.VisualHit) as FrameworkElement;
            if (container == null || !(container.DataContext is DisplayItem item)) return (null, false, null);

            var bounds = container.TransformToAncestor(ItemsContainer)
                .TransformBounds(new Rect(container.RenderSize));
            bool insertAfter = pos.X > bounds.Left + bounds.Width / 2;
            return (item, insertAfter, container);
        }
        
        private void Panel_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            // Esc cancels the drag - never treat that as a drop.
            if (e.EscapePressed)
            {
                _itemDragCanceled = true;
                return;
            }

            // Act on the mouse release only; modifier keys also raise this event.
            if (e.KeyStates != DragDropKeyStates.None || _itemDragCanceled) return;

            // The cursor is over another widget that can take the items, or over
            // one that refused: the post-drag handling decides, no eject here.
            var session = DragDropSession.Current;
            if (session != null && (session.PendingMove || session.TargetRefused)) return;

            EjectDraggedItemsOnRelease(_currentDragItems);
        }

        /// <summary>True when the drag data comes from a different widget.</summary>
        private bool TryGetCrossWidgetSource(IDataObject data, out string sourceId)
        {
            sourceId = data.GetData("KoboldSourceFolderId") as string;
            return !string.IsNullOrEmpty(sourceId) && sourceId != _data.Id;
        }

        /// <summary>Cross-widget drops need both widgets unlocked and a matching session.</summary>
        private bool CanAcceptCrossWidgetDrag(DragDropSession session, string sourceId)
        {
            return !_data.IsLocked &&
                   session != null &&
                   !session.SourceIsLocked &&
                   session.SourceFolderId == sourceId;
        }

        /// <summary>Merges items dragged from another widget and tells the source they were taken.</summary>
        private void HandleCrossWidgetDrop(DragEventArgs e, string sourceId)
        {
            var session = DragDropSession.Current;
            if (!CanAcceptCrossWidgetDrag(session, sourceId)) return;

            var incoming = e.Data.GetData("KoboldWidgetItems") as List<WidgetItem>;
            if (incoming == null || incoming.Count == 0) return;

            WidgetItems.MergeInto(_data.Items, incoming);
            session.MarkAcceptedByOtherWidget();
            ForceRefreshUI();
        }

        /// <summary>Value snapshot of the dragged items for a cross-widget move.</summary>
        private List<WidgetItem> BuildDragSnapshot(List<DisplayItem> selectedItems)
        {
            var snapshot = new List<WidgetItem>();
            foreach (var selected in selectedItems)
            {
                var source = _data.Items.FirstOrDefault(
                    i => i.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase));
                if (source != null) snapshot.Add(WidgetItems.Clone(source));
            }
            return snapshot;
        }

        /// <summary>
        /// Removes items another widget has taken over - without ejecting:
        /// references stay hidden, stored files stay in storage.
        /// </summary>
        private void RemoveDraggedItems(List<DisplayItem> selectedItems)
        {
            if (selectedItems == null || selectedItems.Count == 0) return;

            bool removed = false;
            foreach (var selected in selectedItems)
            {
                var item = _data.Items.FirstOrDefault(
                    i => i.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    _data.Items.Remove(item);
                    removed = true;
                }
            }

            if (removed) ForceRefreshUI();
        }

        /// <summary>
        /// Desktop restore: releasing outside this window ejects the dragged
        /// items (references are unhidden, stored files move back to origin).
        /// </summary>
        private void EjectDraggedItemsOnRelease(List<DisplayItem> selectedItems)
        {
            if (selectedItems == null || selectedItems.Count == 0) return;

            // Locked widgets forbid moving content out
            if (_data.IsLocked) return;

            // Cursor is in physical pixels, window bounds in DIPs (see ScreenGeometry).
            var cursor = System.Windows.Forms.Cursor.Position;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            if (ScreenGeometry.ContainsPhysicalPoint(
                    bounds, dpi.DpiScaleX, dpi.DpiScaleY, cursor.X, cursor.Y))
            {
                return;
            }

            bool changed = false;
            foreach (var selItem in selectedItems)
            {
                var itemToRemove = _data.Items.FirstOrDefault(i => i.Path == selItem.Path);
                if (itemToRemove != null)
                {
                    EjectItem(itemToRemove);
                    _data.Items.Remove(itemToRemove);
                    changed = true;
                }
            }

            if (changed)
            {
                // Deferred so the refresh does not run inside the OLE drag loop.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateUI();
                    OnDataChanged?.Invoke();
                }));
            }
        }

        #endregion
    }
}


