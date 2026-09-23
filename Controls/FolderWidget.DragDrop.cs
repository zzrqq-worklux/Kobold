using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Helpers;
using Kobold.Services;

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

        // Set by our own Drop handler: a drop the app handled itself (reorder,
        // cross-widget merge) must never be mistaken for an external relocation.
        private bool _dropHandledInsideApp;

        // True when the drag was released outside this panel and not over another
        // widget - only then can the shell's effect mean "the file moved away".
        private bool _releasedOutsidePanel;

        // Files this drag un-hid so an external move does not carry the hidden
        // attribute into the new folder; hidden again when the items stay.
        private readonly List<string> _unhiddenForDrag = new List<string>();

        // Browse-mode drags carry the plain file paths plus the folder they came
        // from, so another browse panel knows what to move and from where.
        private const string BrowseDragPathsFormat = "KoboldBrowsePaths";
        private const string BrowseDragSourceFormat = "KoboldBrowseSource";
        
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
                // Double-click: folders are browsed in place, files keep opening in the shell
                if (e.ClickCount == 2)
                {
                    if (item.IsDirectory) EnterFolder(item.Path);
                    else OpenWithShell(item.Path);

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

                // Browsing drags move real files: the shell (or another browse
                // panel) takes them - there are no widget items to update.
                if (IsBrowsing)
                {
                    StartBrowseItemDrag((DependencyObject)sender, selectedItems);
                    return;
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
                
                // For external drop (file list). A locked widget never hands its
                // files to another program, so it publishes no file list at all.
                if (!_data.IsLocked)
                {
                    var fileList = new System.Collections.Specialized.StringCollection();
                    foreach (var sel in selectedItems)
                    {
                        fileList.Add(sel.Path);
                    }
                    dataObject.SetFileDropList(fileList);
                }
                
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
                _dropHandledInsideApp = false;
                _releasedOutsidePanel = false;
                _unhiddenForDrag.Clear();
                var session = DragDropSession.Begin(_data.Id, _data.IsLocked);

                // The shell reports what it actually did with the files (move,
                // copy or nothing) - that result decides what happens to the
                // items, so a drop into Explorer relocates the file instead of
                // fighting the shell for it. See Core/DragOutPolicy.
                DragDropEffects performed;
                try
                {
                    performed = DragDrop.DoDragDrop((DependencyObject)sender, dataObject, DragDropEffects.Move);
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
                        _unhiddenForDrag.Clear();
                        RemoveDraggedItems(selectedItems);
                    }
                    else
                    {
                        HandleDragOutcome(selectedItems, performed);
                    }
                }
                finally
                {
                    DragDropSession.End();
                    _unhiddenForDrag.Clear();
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
        /// Starts a drag from a browsed folder. Explorer and the desktop take the
        /// file list themselves; another browse panel moves the files into the
        /// folder it shows, and a widget at its root adds them as references.
        /// </summary>
        private void StartBrowseItemDrag(DependencyObject source, List<DisplayItem> items)
        {
            // Widget-item bookkeeping must not see this drag.
            _currentDragItems = null;

            try
            {
                string sourceFolder = CurrentBrowsePath;
                if (string.IsNullOrEmpty(sourceFolder) || items == null || items.Count == 0) return;

                var paths = new List<string>();
                foreach (var item in items)
                {
                    if (item != null && !string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
                }
                if (paths.Count == 0) return;

                var fileList = new System.Collections.Specialized.StringCollection();
                foreach (var path in paths) fileList.Add(path);

                var dataObject = new DataObject();
                dataObject.SetFileDropList(fileList);
                dataObject.SetData(BrowseDragPathsFormat, paths);
                dataObject.SetData(BrowseDragSourceFormat, sourceFolder);

                var dragWindow = CreateDragVisual(items);
                GiveFeedbackEventHandler feedbackHandler = (s, args) =>
                {
                    if (dragWindow != null && dragWindow.IsVisible)
                    {
                        var cursor = GetCursorInWindow();
                        dragWindow.Left = Left + cursor.X + 10;
                        dragWindow.Top = Top + cursor.Y + 10;
                    }

                    args.UseDefaultCursors = false;
                    Mouse.OverrideCursor = args.Effects == DragDropEffects.None
                        ? Cursors.No
                        : CursorHelper.GrabHand;
                    args.Handled = true;
                };

                var border = source as Border;
                if (border != null) border.GiveFeedback += feedbackHandler;
                try
                {
                    DragDrop.DoDragDrop(source, dataObject, DragDropEffects.Move | DragDropEffects.Copy);
                }
                finally
                {
                    if (border != null) border.GiveFeedback -= feedbackHandler;
                    dragWindow?.Close();
                    Mouse.OverrideCursor = null;
                }

                // Entries may have moved away: re-list what is still here.
                UpdateUI();
            }
            finally
            {
                _isDraggingItem = false;
                _draggedItem = null;
            }
        }

        /// <summary>True for a drag started by a browse panel (any panel).</summary>
        private static bool IsBrowseDrag(IDataObject data, out string sourceFolder)
        {
            sourceFolder = data == null ? null : data.GetData(BrowseDragSourceFormat) as string;
            return data != null && data.GetDataPresent(BrowseDragPathsFormat) && !string.IsNullOrEmpty(sourceFolder);
        }

        /// <summary>Highlights the panel while another browse panel hovers it.</summary>
        private void SetBrowseDropHighlight(bool on)
        {
            if (on) ExpandedPanel.BorderBrush = ThemeManager.AccentBlueBrush;
            else ExpandedPanel.ClearValue(Border.BorderBrushProperty);
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
            if (IsBrowsing)
            {
                // Entries dragged from another browse panel move into the folder
                // on screen; anything else is refused (browsing is a view, not a
                // drop target for widget items).
                bool accepted = IsBrowseDrag(e.Data, out var browseSource) &&
                                !BrowseMove.SameFolder(browseSource, CurrentBrowsePath);

                e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
                SetBrowseDropHighlight(accepted);
                DropIndicator.Visibility = Visibility.Collapsed;
                if (!accepted) DragDropSession.Current?.MarkTargetRefused();
                e.Handled = true;
                return;
            }

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

                    // The indicator's parent (ItemsHost) already starts below the
                    // header row, so no header-height correction is needed.
                    double rowTop = 0;

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
            SetBrowseDropHighlight(false);
            DragDropSession.Current?.ClearTargetState();
        }
        
        private void ItemsContainer_Drop(object sender, DragEventArgs e)
        {
            // Hide indicator
            DropIndicator.Visibility = Visibility.Collapsed;

            if (IsBrowsing)
            {
                HandleBrowseDrop(e);
                e.Handled = true;
                return;
            }

            // Items dragged from another widget: merge them into this one.
            if (TryGetCrossWidgetSource(e.Data, out var sourceId))
            {
                _dropHandledInsideApp = true;
                HandleCrossWidgetDrop(e, sourceId);
                e.Handled = true;
                return;
            }
            
            // Handle internal item reordering
            if (e.Data.GetDataPresent("KoboldItem"))
            {
                _dropHandledInsideApp = true;
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
        /// Moves entries dragged from another browse panel into the folder on
        /// screen. The shell performs the move, so collision prompts and progress
        /// stay exactly like Explorer's.
        /// </summary>
        private void HandleBrowseDrop(DragEventArgs e)
        {
            SetBrowseDropHighlight(false);

            if (!IsBrowseDrag(e.Data, out var sourceFolder) ||
                BrowseMove.SameFolder(sourceFolder, CurrentBrowsePath))
            {
                e.Effects = DragDropEffects.None;
                DragDropSession.Current?.MarkTargetRefused();
                return;
            }

            var paths = e.Data.GetData(BrowseDragPathsFormat) as List<string>;
            var movable = BrowseMove.MovableInto(paths, CurrentBrowsePath,
                p => File.Exists(p) || Directory.Exists(p));

            if (movable.Count == 0)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            bool moved = ShellFileOperations.Move(
                movable, CurrentBrowsePath, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            e.Effects = moved ? DragDropEffects.Move : DragDropEffects.None;

            // Deferred so the re-list does not run inside the OLE drag loop; the
            // listing may have gained entries (or kept some, when the shell
            // skipped or the user cancelled).
            Dispatcher.BeginInvoke(new Action(UpdateUI));
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
            // one that refused: that drop is ours to handle, never an external one.
            var session = DragDropSession.Current;
            if (session != null && (session.PendingMove || session.TargetRefused))
            {
                _releasedOutsidePanel = false;
                return;
            }

            _releasedOutsidePanel = IsCursorOutsidePanel();
            if (!_releasedOutsidePanel) return;

            // An external drop is about to happen. The file must not carry its
            // "managed by Kobold" hidden attribute into its new home, so make it
            // visible before the shell moves it; the post-drag handling hides it
            // again when the items stay here.
            UnhideDraggedSources(_currentDragItems);
        }

        /// <summary>
        /// Turns the finished drag into an action for the dragged items: the
        /// shell's own effect decides (move = the files were relocated, copy =
        /// they are still managed here, nothing = the panel's own restore).
        /// See Core/DragOutPolicy for the rules.
        /// </summary>
        private void HandleDragOutcome(List<DisplayItem> selectedItems, DragDropEffects performed)
        {
            var effect = ExternalDropEffect.None;
            if ((performed & DragDropEffects.Move) != 0) effect = ExternalDropEffect.Move;
            else if ((performed & DragDropEffects.Copy) != 0) effect = ExternalDropEffect.Copy;

            switch (DragOutPolicy.Resolve(effect, _dropHandledInsideApp, _releasedOutsidePanel, _data.IsLocked))
            {
                case DragOutAction.RemoveItems:
                    // The shell relocated the files - the items have left the widget.
                    _unhiddenForDrag.Clear();
                    RemoveDraggedItems(selectedItems);
                    break;

                case DragOutAction.EjectAndRemove:
                    // Nothing took the files: keep the restore behaviour.
                    _unhiddenForDrag.Clear();
                    EjectDraggedItemsOnRelease(selectedItems);
                    break;

                default:
                    // The items stay managed here, so their files stay hidden.
                    RestoreHiddenState();
                    break;
            }
        }

        /// <summary>Makes the dragged files visible for the moment they leave the panel.</summary>
        private void UnhideDraggedSources(List<DisplayItem> items)
        {
            if (items == null) return;

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.Path)) continue;
                if (!StorageOps.IsHidden(item.Path)) continue;
                if (StorageOps.SetHidden(item.Path, false)) _unhiddenForDrag.Add(item.Path);
            }
        }

        /// <summary>Hides the files this drag un-hid again - the items stay managed.</summary>
        private void RestoreHiddenState()
        {
            foreach (var path in _unhiddenForDrag)
            {
                if (File.Exists(path) || Directory.Exists(path)) StorageOps.SetHidden(path, true);
            }
            _unhiddenForDrag.Clear();
        }

        /// <summary>Cursor in physical pixels against the window bounds in DIPs.</summary>
        private bool IsCursorOutsidePanel()
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            return !ScreenGeometry.ContainsPhysicalPoint(
                bounds, dpi.DpiScaleX, dpi.DpiScaleY, cursor.X, cursor.Y);
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
        /// Restore path for a drop nothing accepted: the items are ejected
        /// (references are un-hidden, stored files move back to where they came
        /// from) and leave the widget. Only runs for a release outside the panel.
        /// </summary>
        private void EjectDraggedItemsOnRelease(List<DisplayItem> selectedItems)
        {
            if (selectedItems == null || selectedItems.Count == 0) return;

            // Locked widgets forbid moving content out
            if (_data.IsLocked) return;

            if (!IsCursorOutsidePanel()) return;

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


