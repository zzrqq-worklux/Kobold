using System;
using System.Windows.Controls;
using FoldRa.Core;

namespace FoldRa.Helpers
{
    /// <summary>
    /// Builder pattern helper for creating context menus.
    /// SOLID: Single Responsibility - only handles menu building.
    /// </summary>
    public class MenuBuilder
    {
        private readonly ContextMenu _menu;

        public MenuBuilder()
        {
            _menu = new ContextMenu();
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
            _menu.IsOpen = true;
        }

        /// <summary>
        /// Get the built menu without showing
        /// </summary>
        public ContextMenu Build()
        {
            return _menu;
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


