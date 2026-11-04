using System.Windows;
using System.Windows.Input;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// The uniform entry point a window (standalone or a group tab) drives a
    /// widget's content through. A view owns its own chrome title, its preview
    /// key/mouse handling and its flush/undo, so the host window only forwards
    /// the events and calls the lifecycle hooks.
    /// </summary>
    internal interface IWidgetContentView
    {
        WidgetData Data { get; }

        FrameworkElement View { get; }

        string ChromeTitle { get; }

        bool HasTitleMenu { get; }

        void Attach(WidgetData data, IWidgetViewHost host);

        void Activate();

        void Deactivate();

        void Refresh();

        void RefreshChromeTitle();

        void ToggleTitleMenu(FrameworkElement anchor);

        bool HandlePreviewKeyDown(KeyEventArgs e);

        void HandleMouseDownOutsideEditor(object source);

        void FlushPendingEdit();

        bool TryUndo();
    }
}
