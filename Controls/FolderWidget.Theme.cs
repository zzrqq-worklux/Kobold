using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Kobold.Core;
using Path = System.Windows.Shapes.Path;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - Theme related methods
    /// </summary>
    public partial class FolderWidget
    {
        /// <summary>
        /// Refreshes entire widget theme - called when theme changes or on startup
        /// </summary>
        public void RefreshTheme()
        {
            // Apply all theme colors synchronously
            ApplyThemeColors();
            
            // Refresh items display (includes text color binding)
            UpdateUI();
        }
        
        /// <summary>
        /// Central method to apply all theme colors
        /// </summary>
        private void ApplyThemeColors()
        {
            var textBrush = ThemeManager.TextBrush;
            
            // Panel header text
            PanelHeaderText.Foreground = textBrush;
            
            // Empty state text
            EmptyText.Foreground = textBrush;
            
            // Update panel background colors
            UpdatePanelColor();
        }
        
        /// <summary>
        /// Apply text colors to all items in the panel (for theme change updates)
        /// </summary>
        private void ApplyItemTextColors()
        {
            var textBrush = ThemeManager.TextBrush;
            
            // Update DisplayItem TextColor property - binding will handle the rest
            var items = ItemsContainer.ItemsSource as System.Collections.Generic.List<DisplayItem>;
            if (items != null)
            {
                foreach (var item in items)
                {
                    item.TextColor = textBrush;
                }
            }
        }
        
        private void UpdatePanelColor()
        {
            Color baseColor = Utils.HexToColor(_data.Color);
            Color darkColor = Utils.DarkenColor(baseColor, 0.3);
            
            // Use ThemeManager for theme state
            bool isDark = ThemeManager.IsDarkTheme;

            // The configured opacity scales the background ALPHA only, so panel
            // content (icons, labels) stays fully crisp while the backdrop goes
            // more or less transparent.
            double opacity = GetPanelOpacity();
            
            // Panel background
            var panelBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            
            if (isDark)
            {
                // Dark theme
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(210 * opacity), darkColor.R, darkColor.G, darkColor.B), 0));
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(230 * opacity), (byte)(darkColor.R * 0.6), (byte)(darkColor.G * 0.6), (byte)(darkColor.B * 0.6)), 1));
            }
            else
            {
                // Light theme - brighter, pastel-like colors
                Color lightColor = Utils.LightenColor(baseColor, 0.6);
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(240 * opacity), 250, 250, 250), 0));
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(250 * opacity), lightColor.R, lightColor.G, lightColor.B), 1));
            }
            
            ExpandedPanel.Background = panelBrush;
            
            // Panel header
            var headerBrush = new SolidColorBrush(isDark 
                ? Color.FromArgb((byte)(50 * opacity), 255, 255, 255) 
                : Color.FromArgb((byte)(40 * opacity), 0, 0, 0));
            PanelHeader.Background = headerBrush;
            
            // Header text color
            PanelHeaderText.Foreground = new SolidColorBrush(isDark ? Colors.White : Color.FromRgb(40, 40, 40));
        }

        /// <summary>
        /// Panel opacity from config, clamped to the usable 20-100% range.
        /// Only the expanded panel is affected - the folder icon stays opaque.
        /// </summary>
        private double GetPanelOpacity()
        {
            double opacity = WidgetManager.Instance.Config.PanelOpacity;
            if (double.IsNaN(opacity)) opacity = 1.0;
            return Math.Max(0.2, Math.Min(1.0, opacity));
        }

        /// <summary>
        /// Applies the configured panel opacity to an already-open panel by
        /// repainting the background with the matching alpha (content stays
        /// fully opaque). Used for live preview in the settings dialog.
        /// </summary>
        public void ApplyPanelOpacity()
        {
            if (!_isExpanded) return;
            UpdatePanelColor();
        }

        #region Helper Methods
        private UniformGrid FindUniformGrid(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is UniformGrid ug) return ug;
                var result = FindUniformGrid(child);
                if (result != null) return result;
            }
            return null;
        }

        private (int width, int height) CalculatePanelSize()
        {
            int cols = Math.Max(1, _data.GridColumns);
            int itemCount = Math.Max(1, _lastItemCount); // At least 1 for empty state

            // This widget's own row cap decides how tall the panel grows; any
            // extra rows scroll. The work-area ratio stays as an outer bound
            // for tiny screens combined with the largest icon size.
            int visibleRows = PanelRows.VisibleRows(itemCount, cols, _data.MaxPanelRows);

            int naturalHeight = HEADER_HEIGHT + visibleRows * GetScaledItemHeight() + PADDING;
            if (_lastFooterVisible) naturalHeight += WidgetConstants.FOOTER_HEIGHT;

            int maxHeight = Math.Max(120,
                (int)(SystemParameters.WorkArea.Height * WidgetConstants.PANEL_MAX_HEIGHT_RATIO));

            // The in-flow scrollbar needs its width added so items are not clipped.
            bool scrolls = PanelRows.NeedsScroll(itemCount, cols, _data.MaxPanelRows)
                || naturalHeight > maxHeight;

            int width = cols * GetScaledItemWidth() + PADDING + PANEL_BORDER;
            if (scrolls) width += (int)SystemParameters.VerticalScrollBarWidth;

            int height = Math.Min(naturalHeight, maxHeight);

            // Minimum dimensions
            width = Math.Max(width, 180);
            height = Math.Max(height, 120);

            return (width, height);
        }

        /// <summary>Panel size (DIP) for the current content - used to place the panel.</summary>
        public (int width, int height) GetPanelSize() => CalculatePanelSize();
        
        #endregion
    }
}


