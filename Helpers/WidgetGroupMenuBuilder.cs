using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Kobold.Controls;
using Kobold.Core;

namespace Kobold.Helpers
{
    /// <summary>
    /// Builds the group window's current/tab menu: one switch entry per
    /// member (checked on the active one), the live detach entry (T6 replaces
    /// the T4 placeholder) and hide-group. The window owns the actions, so
    /// the same menu serves the header right-click and the island's member
    /// tile (T5 routes both here). Tab reorder and group-to-group merge are
    /// deliberately absent.
    /// </summary>
    public static class WidgetGroupMenuBuilder
    {
        /// <summary>The detach entry's header (pinned by the menu checks).</summary>
        public const string DetachHeader = "分离此组件";

        /// <summary>The hide-group entry's header (pinned by the menu checks).</summary>
        public const string HideHeader = "隐藏整组";

        /// <summary>
        /// The menu for a group's members in tab order. <paramref name="isLocked"/>
        /// comes from the group (a locked group refuses detach even though its
        /// pair could otherwise split); <paramref name="onClosed"/> fires once
        /// the menu is gone (the island uses it to resume auto-collapse).
        /// </summary>
        public static ContextMenu Build(
            IList<WidgetData> members,
            int activeIndex,
            bool isLocked,
            Action<int> onSwitch,
            Action onDetach,
            Action onHide,
            Action onClosed)
        {
            var menu = new ContextMenu();
            int count = members == null ? 0 : members.Count;

            for (int i = 0; i < count; i++)
            {
                int index = i;
                WidgetData member = members[i];
                var item = new MenuItem
                {
                    Header = "切换到该标签 · " + WidgetGroupTabStrip.KindLabel(member),
                    IsCheckable = true,
                    IsChecked = i == activeIndex,
                    ToolTip = member == null ? null : member.Name
                };
                item.Click += (s, e) =>
                {
                    menu.IsOpen = false;
                    onSwitch?.Invoke(index);
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());

            var detach = new MenuItem
            {
                Header = DetachHeader,
                IsEnabled = !isLocked && count >= 2,
                ToolTip = "将此标签拖出组外，或从此处分离开"
            };
            detach.Click += (s, e) =>
            {
                menu.IsOpen = false;
                onDetach?.Invoke();
            };
            menu.Items.Add(detach);

            var hide = new MenuItem { Header = HideHeader };
            hide.Click += (s, e) =>
            {
                menu.IsOpen = false;
                onHide?.Invoke();
            };
            menu.Items.Add(hide);

            if (onClosed != null)
            {
                menu.Closed += (s, e) => onClosed();
            }
            return menu;
        }
    }
}
