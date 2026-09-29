using System;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// The contract the widget manager uses to drive any widget window: panel
    /// visibility and size, the widget menu and the two change notifications.
    /// The shared implementation lives in <see cref="WidgetWindowBase"/>;
    /// FolderWidget implements it explicitly until it moves onto the base.
    /// </summary>
    public interface IWidgetWindow
    {
        string WidgetId { get; }
        WidgetData Data { get; }
        bool IsPanelOpen { get; }
        bool IsModalOpen { get; }

        (double width, double height) GetPanelSize();
        void ShowPanel(double defaultLeft, double defaultTop, bool activate = true);
        void HidePanel();
        void ShowWidgetMenu(Action onClosed);
        void UpdateUI();
        void RefreshTheme();

        event Action<IWidgetWindow> OnDeleted;
        event Action OnDataChanged;
    }
}
