using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kobold.Core;
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
        private readonly string _tintColor;

        /// <summary>
        /// Creates a menu builder. Pass the widget color to tint the menu so
        /// it reads as belonging to that widget (null keeps the default dark).
        /// </summary>
        public MenuBuilder(string tintColor = null)
        {
            _menu = new ContextMenu();
            _tintColor = tintColor;
        }

        /// <summary>
        /// Add a simple menu item with click handler
        /// </summary>
        public MenuBuilder AddItem(string localizationKey, Action onClick)
        {
            var item = new MenuItem { Header = Localization.Get(localizationKey) };
            item.Click += (s, e) => onClick?.Invoke();
            _menu.Items.Add(item);
            return this;
        }

        /// <summary>
        /// Add a checkable menu item
        /// </summary>
        public MenuBuilder AddCheckItem(string header, bool isChecked, Action onClick)
        {
            var item = new MenuItem { Header = header, IsChecked = isChecked };
            item.Click += (s, e) => onClick?.Invoke();
            _menu.Items.Add(item);
            return this;
        }

        /// <summary>
        /// Add a submenu with items
        /// </summary>
        public MenuBuilder AddSubmenu(string localizationKey, Action<SubmenuBuilder> configure)
        {
            var submenu = new MenuItem { Header = Localization.Get(localizationKey) };
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
            Prepare(_menu, _tintColor);
            _menu.IsOpen = true;
        }

        /// <summary>
        /// Get the built menu without showing
        /// </summary>
        public ContextMenu Build()
        {
            Prepare(_menu, _tintColor);
            return _menu;
        }

        /// <summary>
        /// Shared menu preparation: always extend to the right of the cursor,
        /// and tint with the widget color when one is given.
        /// </summary>
        public static void Prepare(ContextMenu menu, string widgetColor = null)
        {
            ForceRightSidePlacement(menu);
            if (!string.IsNullOrEmpty(widgetColor)) ApplyWidgetTint(menu, widgetColor);
        }

        private static void ForceRightSidePlacement(ContextMenu menu)
        {
            if (!SystemParameters.MenuDropAlignment) return;

            // Windows' MenuDropAlignment (left-handed/tablet setting) mirrors
            // mouse-anchored popups to the left of the cursor. Shift the menu
            // back so it always extends to the right. The offset equals the
            // menu width, which makes it idempotent across reopens.
            menu.Opened += (s, e) => menu.HorizontalOffset = menu.ActualWidth;
        }

        /// <summary>
        /// Tints a context menu with a widget color by overriding the
        /// 'Kobold.Brush.Overlay*' keys on the menu itself; the global
        /// templates resolve those keys first. The mix base is the active
        /// theme's surface color, so dark themes get a subtle hue and light
        /// themes a pastel wash. Strength lives in UiTokens.MenuTint*.
        /// </summary>
        private static void ApplyWidgetTint(ContextMenu menu, string widgetColor)
        {
            var tint = Utils.HexToColor(widgetColor);

            SetTint(menu, "OverlayBackground", ThemeManager.OverlayBackgroundBrush.Color, tint, UiTokens.MenuTintBackground);
            SetTint(menu, "OverlayHover", ThemeManager.OverlayHoverBrush.Color, tint, UiTokens.MenuTintState);
            SetTint(menu, "OverlayChecked", ThemeManager.OverlayCheckedBrush.Color, tint, UiTokens.MenuTintState);
            SetTint(menu, "OverlayBorder", ThemeManager.OverlayBorderBrush.Color, tint, UiTokens.MenuTintBorder);
            SetTint(menu, "OverlayDivider", ThemeManager.OverlayDividerBrush.Color, tint, UiTokens.MenuTintBorder);
        }

        private static void SetTint(ContextMenu menu, string key, Color baseColor, Color tint, double amount)
        {
            var brush = new SolidColorBrush(Utils.MixColor(baseColor, tint, amount));
            brush.Freeze();
            menu.Resources[ThemeManager.BrushKeyPrefix + key] = brush;
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
        public SubmenuBuilder AddCheckItem(string header, bool isChecked, Action onClick)
        {
            var item = new MenuItem { Header = header, IsChecked = isChecked };
            item.Click += (s, e) => onClick?.Invoke();
            _parent.Items.Add(item);
            return this;
        }

        /// <summary>
        /// Add a simple item to submenu
        /// </summary>
        public SubmenuBuilder AddItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => onClick?.Invoke();
            _parent.Items.Add(item);
            return this;
        }
    }
}


