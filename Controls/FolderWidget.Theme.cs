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
        public override void RefreshTheme()
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
            
            // Panel shell, title, lock/pin icons and header tint: the chrome
            // repaints all of them from its own state (same maths as the old
            // hand-built shell).
            Chrome.ApplyTheme();

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

            // The chrome owns the backdrop gradient and the header tint (the
            // exact folder maths are pinned in XamlLoadCheck); pushing the
            // colour and the configured opacity repaints both. The opacity
            // scales the background ALPHA only, so panel content stays crisp.
            Chrome.PanelColor = _data.Color;
            Chrome.ApplyOpacity(GetPanelOpacity());

            // Fix round 15 (R3): the item-selection visual follows the widget's
            // own colour (the raw base colour, no readability fallback). The
            // ItemTemplate's DynamicResource lookups resolve these keys up the
            // tree, so overriding them on the chrome's shell repaints the
            // selection on every colour, theme and opacity path that lands here.
            var selectionBorder = new SolidColorBrush(baseColor);
            selectionBorder.Freeze();
            Chrome.Shell.Resources["Kobold.Brush.SelectionBorder"] = selectionBorder;

            var selectionFill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            selectionFill.GradientStops.Add(new GradientStop(Color.FromArgb(0x15, baseColor.R, baseColor.G, baseColor.B), 0));
            selectionFill.GradientStops.Add(new GradientStop(Color.FromArgb(0x25, baseColor.R, baseColor.G, baseColor.B), 1));
            selectionFill.Freeze();
            Chrome.Shell.Resources["Kobold.Brush.SelectionFill"] = selectionFill;
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

        /// <summary>
        /// Content size handed to the base: the legacy panel maths minus the
        /// chrome header and the shell's border, both of which
        /// <see cref="WidgetWindowBase.GetPanelSize"/> adds back. Exact parity
        /// with the old CalculatePanelSize for every input (pinned in
        /// XamlLoadCheck).
        /// </summary>
        protected override (double width, double height) GetContentSize()
        {
            var (width, height) = CalculatePanelSize();
            var border = Chrome == null ? new Thickness(0) : Chrome.Shell.BorderThickness;
            return (width - border.Left - border.Right,
                    height - HEADER_HEIGHT - border.Top - border.Bottom);
        }

        #endregion
    }
}


