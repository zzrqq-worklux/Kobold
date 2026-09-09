using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FoldRa.Core;
using Path = System.Windows.Shapes.Path;

namespace FoldRa.Controls
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
            
            // Refresh folder icon with new colors
            DrawFolderIcon();
            
            // Refresh items display (includes text color binding)
            UpdateUI();
        }
        
        /// <summary>
        /// Central method to apply all theme colors
        /// </summary>
        private void ApplyThemeColors()
        {
            var textBrush = ThemeManager.TextBrush;
            
            // Folder name (widget label) - always white for visibility on any wallpaper
            FolderNameText.Foreground = new SolidColorBrush(Colors.White);
            FolderNameText.Effect = null; // No effect for crisp text
            
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
            
            // Panel background
            var panelBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            
            if (isDark)
            {
                // Dark theme
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(210, darkColor.R, darkColor.G, darkColor.B), 0));
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(230, (byte)(darkColor.R * 0.6), (byte)(darkColor.G * 0.6), (byte)(darkColor.B * 0.6)), 1));
            }
            else
            {
                // Light theme - brighter, pastel-like colors
                Color lightColor = Utils.LightenColor(baseColor, 0.6);
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(240, 250, 250, 250), 0));
                panelBrush.GradientStops.Add(new GradientStop(Color.FromArgb(250, lightColor.R, lightColor.G, lightColor.B), 1));
            }
            
            ExpandedPanel.Background = panelBrush;
            
            // Panel header
            var headerBrush = new SolidColorBrush(isDark 
                ? Color.FromArgb(50, 255, 255, 255) 
                : Color.FromArgb(40, 0, 0, 0));
            PanelHeader.Background = headerBrush;
            
            // Header text color
            PanelHeaderText.Foreground = new SolidColorBrush(isDark ? Colors.White : Color.FromRgb(40, 40, 40));
            
            // Folder name text below icon - always white (already set in ApplyThemeColors)
            // No shadow effect for crisp, clean text
            
            var glowBrush = GlowEffect.Fill as RadialGradientBrush;
            if (glowBrush != null && glowBrush.GradientStops.Count > 0)
            {
                glowBrush.GradientStops[0].Color = Color.FromArgb(128, baseColor.R, baseColor.G, baseColor.B);
            }
        }

        private void DrawFolderIcon()
        {
            FolderIconCanvas.Children.Clear();
            
            string iconStyle = WidgetManager.Instance.Config.IconStyle ?? "classic";
            
            // Use IconRendererFactory - all icon drawing code is now in IconRenderers folder
            var renderer = IconRenderers.IconRendererFactory.Create(iconStyle);
            renderer.Render(FolderIconCanvas, _data.Color);
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
            int itemCount = Math.Max(1, _data.Items.Count); // At least 1 for empty state
            int rows = (int)Math.Ceiling((double)itemCount / cols);
            
            int width = cols * GetScaledItemWidth() + PADDING;
            int height = HEADER_HEIGHT + rows * GetScaledItemHeight() + PADDING;
            
            // Minimum dimensions
            width = Math.Max(width, 180);
            height = Math.Max(height, 120);
            
            return (width, height);
        }
        
        #endregion
    }
}


