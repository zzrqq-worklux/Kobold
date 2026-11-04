using System;
using System.Windows;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// What a content view needs from whatever hosts it: the widget data, the
    /// shared chrome, the live content box and the few host actions. A widget
    /// window implements it directly; a group window (T4) will too, which is
    /// why the views no longer take a concrete window.
    /// </summary>
    internal interface IWidgetViewHost
    {
        WidgetData Data { get; }

        WidgetChrome Chrome { get; }

        bool IsPanelOpen { get; }

        double ContentWidth { get; }

        double ContentHeight { get; }

        (double width, double height) GetPanelSize();

        void NotifyDataChanged();

        void RefreshChromeTitle(string title = null);

        void PersistSelection();

        void ShowWidgetMenu();
    }

    /// <summary>
    /// The one content-size rule every host shares: the live window size while
    /// its panel is open (and the window has a real layout), otherwise the
    /// remembered panel size - both minus the chrome shell's border and, on
    /// the height, the 40 DIP header. Clamped at zero.
    /// </summary>
    internal static class WidgetViewHostMetrics
    {
        public static (double width, double height) ContentSize(IWidgetViewHost host)
        {
            if (host == null) return (0, 0);

            double width;
            double height;
            var element = host as FrameworkElement;
            if (host.IsPanelOpen && element != null)
            {
                // The live size: the arranged box when the window was really
                // laid out, otherwise the explicitly set Width/Height (an
                // offscreen host in the checks never arranges).
                double liveWidth = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
                double liveHeight = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
                if (liveWidth > 0 && liveHeight > 0 &&
                    !double.IsNaN(liveWidth) && !double.IsNaN(liveHeight))
                {
                    width = liveWidth;
                    height = liveHeight;
                }
                else
                {
                    (width, height) = host.GetPanelSize();
                }
            }
            else
            {
                (width, height) = host.GetPanelSize();
            }

            WidgetChrome chrome = host.Chrome;
            Thickness border = chrome == null ? default(Thickness) : chrome.Shell.BorderThickness;
            width -= border.Left + border.Right;
            height -= border.Top + border.Bottom;
            height -= WidgetConstants.HEADER_HEIGHT;

            return (Math.Max(0, width), Math.Max(0, height));
        }

        public static double ContentWidth(IWidgetViewHost host)
        {
            return ContentSize(host).width;
        }

        public static double ContentHeight(IWidgetViewHost host)
        {
            return ContentSize(host).height;
        }
    }
}
