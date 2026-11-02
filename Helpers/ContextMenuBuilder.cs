using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Core.Icons;
using Localization = Kobold.Core.Localization;

namespace Kobold.Helpers
{
    /// <summary>
    /// Builder pattern helper for creating context menus.
    /// SOLID: Single Responsibility - only handles menu building.
    /// </summary>
    public class MenuBuilder
    {
        private readonly ContextMenu _menu;

        /// <summary>
        /// Creates a menu builder. The constructor keeps the historical
        /// optional colour argument so call sites can migrate incrementally;
        /// Claude Warm no longer tints the menu surface.
        /// </summary>
        public MenuBuilder(string tintColor = null)
        {
            _menu = new ContextMenu();
        }

        /// <summary>
        /// Add a simple menu item with click handler. isEnabled=false renders it
        /// greyed out (used when a browse selection makes the action ambiguous).
        /// </summary>
        public MenuBuilder AddItem(string localizationKey, Action onClick, bool isEnabled = true,
            IconId? icon = null, Brush iconBrush = null)
        {
            var item = new MenuItem { Header = Localization.Get(localizationKey), IsEnabled = isEnabled };
            if (icon.HasValue) item.Icon = MenuIcons.Create(icon.Value, iconBrush);
            item.Click += (s, e) => onClick?.Invoke();
            _menu.Items.Add(item);
            return this;
        }

        /// <summary>
        /// Add a checkable menu item
        /// </summary>
        public MenuBuilder AddCheckItem(string header, bool isChecked, Action onClick, IconId? icon = null)
        {
            var item = new MenuItem { Header = header, IsChecked = isChecked };
            if (icon.HasValue) item.Icon = MenuIcons.Create(icon.Value);
            item.Click += (s, e) => onClick?.Invoke();
            _menu.Items.Add(item);
            return this;
        }

        /// <summary>
        /// Add a submenu with items
        /// </summary>
        public MenuBuilder AddSubmenu(string localizationKey, Action<SubmenuBuilder> configure,
            IconId? icon = null)
        {
            var submenu = new MenuItem { Header = Localization.Get(localizationKey) };
            if (icon.HasValue) submenu.Icon = MenuIcons.Create(icon.Value);
            var builder = new SubmenuBuilder(submenu);
            configure(builder);
            _menu.Items.Add(submenu);
            return this;
        }

        /// <summary>
        /// Add a separator
        /// </summary>
        public MenuBuilder AddSeparator()
        {
            _menu.Items.Add(new Separator());
            return this;
        }

        /// <summary>
        /// Add a pre-built MenuItem - for complex scenarios
        /// </summary>
        public MenuBuilder AddMenuItem(MenuItem item)
        {
            _menu.Items.Add(item);
            return this;
        }

        /// <summary>
        /// Build and show the menu
        /// </summary>
        public void Show()
        {
            Prepare(_menu);
            _menu.IsOpen = true;
        }

        /// <summary>
        /// Get the built menu without showing
        /// </summary>
        public ContextMenu Build()
        {
            Prepare(_menu);
            return _menu;
        }

        /// <summary>
        /// Shared menu preparation: always extend to the right of the cursor.
        /// The surface stays theme-neutral; the widget colour is an accent on
        /// the content, never a whole-menu wash.
        /// </summary>
        public static void Prepare(ContextMenu menu, string widgetColor = null)
        {
            ForceRightSidePlacement(menu);
        }

        private static void ForceRightSidePlacement(ContextMenu menu)
        {
            if (!SystemParameters.MenuDropAlignment) return;

            // Windows' MenuDropAlignment (left-handed/tablet setting) mirrors
            // mouse-anchored popups to the left of the cursor, and re-positioning
            // them here drops WPF's own screen constraint. So place the menu
            // ourselves: to the right when it fits, otherwise keep the mirrored
            // position, and lift it when it would overhang the work area bottom.
            menu.Opened += (s, e) =>
            {
                var cursor = System.Windows.Forms.Cursor.Position;
                var screen = System.Windows.Forms.Screen.FromPoint(cursor);
                double scale = VisualTreeHelper.GetDpi(menu).DpiScaleX;

                bool fitsRight = ScreenGeometry.FitsToTheRight(
                    cursor.X, menu.ActualWidth * scale, screen.WorkingArea.Right);
                menu.HorizontalOffset = fitsRight ? menu.ActualWidth : 0;

                double liftPx = ScreenGeometry.OverflowPastBottom(
                    cursor.Y, menu.ActualHeight * scale, screen.WorkingArea.Bottom);
                menu.VerticalOffset = liftPx > 0 ? -(liftPx / scale) : 0;
            };
        }

    }

    /// <summary>
    /// Builder for submenu items
    /// </summary>
    public class SubmenuBuilder
    {
        private readonly MenuItem _parent;

        public SubmenuBuilder(MenuItem parent)
        {
            _parent = parent;
        }

        /// <summary>
        /// Add a checkable item to submenu
        /// </summary>
        public SubmenuBuilder AddCheckItem(string header, bool isChecked, Action onClick, IconId? icon = null)
        {
            var item = new MenuItem { Header = header, IsChecked = isChecked };
            if (icon.HasValue) item.Icon = MenuIcons.Create(icon.Value);
            item.Click += (s, e) => onClick?.Invoke();
            _parent.Items.Add(item);
            return this;
        }

        /// <summary>
        /// Add a simple item to submenu
        /// </summary>
        public SubmenuBuilder AddItem(string header, Action onClick, IconId? icon = null)
        {
            var item = new MenuItem { Header = header };
            if (icon.HasValue) item.Icon = MenuIcons.Create(icon.Value);
            item.Click += (s, e) => onClick?.Invoke();
            _parent.Items.Add(item);
            return this;
        }
    }
}


