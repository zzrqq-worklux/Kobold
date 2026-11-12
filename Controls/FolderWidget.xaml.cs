using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.Controls
{
    /// <summary>Standalone folder window: shared chrome, host adaptation and lifecycle.</summary>
    public partial class FolderWidget : WidgetWindowBase, IWidgetViewHost, IFolderPanelHost
    {
        private readonly FolderData _folderData;
        private readonly FolderPanelView _view;

        public FolderWidget(FolderData data) : this(data, null)
        {
        }

        /// <summary>
        /// Builds the standalone host around a live FolderPanelView released by
        /// a group, or a fresh view when <paramref name="contents"/> is null.
        /// The released view is rebound, never re-attached or rebuilt.
        /// </summary>
        internal FolderWidget(FolderData data, IWidgetContentView contents) : base(data)
        {
            InitializeComponent();
            _folderData = data;
            Left = data.PosX;
            Top = data.PosY;

            AttachChrome(FolderChrome);

            var live = contents as FolderPanelView;
            if (contents != null && live == null)
            {
                throw new ArgumentException(
                    "The folder widget can only adopt a FolderPanelView.", nameof(contents));
            }
            WidgetData liveData = live == null ? null : ((IWidgetContentView)live).Data;
            if (liveData != null && data != null
                && !ReferenceEquals(liveData, data)
                && !string.Equals(liveData.Id, data.Id, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The released folder view belongs to a different widget.", nameof(contents));
            }

            _view = live ?? new FolderPanelView();
            FolderChrome.ContentHost.Content = _view;
            if (live != null) ((IWidgetContentView)_view).RebindHost(this);
            else _view.Attach(data, this);

            // A standalone folder is its own active surface even before the
            // first show; the group keeps its inactive tabs inert.
            _view.Activate();

            SourceInitialized += (s, e) => _view.HandleSourceInitialized();
            Closing += (s, e) =>
            {
                if (!HasReleasedContentView) _view.Detach();
            };
        }

        public FolderPanelView PanelView => _view;
        public FrameworkElement PanelShell => Chrome.Shell;
        public string FolderId => _folderData.Id;
        public int GridColumns => _view.GridColumns;
        public bool IsBrowsing => _view.IsBrowsing;

        public void EnterFolder(string path) => _view.EnterFolder(path);
        public void GoBack() => _view.GoBack();

        private void Window_Deactivated(object sender, EventArgs e) => _view.HandleDeactivated();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_view.HandlePreviewKeyDown(e)) e.Handled = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e) => _view.HandleKeyDown(e);

        protected override void OnPanelShown() => _view.Activate();
        protected override void OnPanelHidden() => _view.DeactivateForPanelHide();
        protected override void OnPanelSizeChanged() => _view.ApplyAdaptiveLayout();
        protected override void OnDeleting() => _view.OnDeleting();

        public override void UpdateUI()
        {
            base.UpdateUI();
            _view?.UpdateUI();
        }

        public override void RefreshTheme()
        {
            if (_view == null)
            {
                base.RefreshTheme();
                return;
            }

            // Same order as the pre-extraction override: the window state first,
            // then the view's theme paint and item rebuild.
            base.UpdateUI();
            _view.RefreshTheme();
        }

        public override void ShowWidgetMenu(Action onClosed = null)
        {
            WidgetMenuBuilder.Build(this, onClosed).IsOpen = true;
        }

        public void ApplyItemScaleChange()
        {
            if (IsPanelOpen)
            {
                var (minWidth, minHeight) = GetMinPanelSize();
                if (Width < minWidth || Height < minHeight)
                {
                    Width = Math.Max(Width, minWidth);
                    Height = Math.Max(Height, minHeight);
                    FitPanelToWorkArea();
                }
            }
            UpdateUI();
        }

        private double GetItemScale()
        {
            try { return WidgetManager.Instance.Config.ItemScale; }
            catch { return 1.1; }
        }

        protected override (double width, double height) GetMinPanelSize()
            => PanelLayoutAdapter.MinFolderSize(GetItemScale());

        protected override (double width, double height) GetContentSize()
            => _view == null ? (0, 0) : _view.GetContentSize();

        internal void RefreshChromeTitle(string title = null)
        {
            if (title == null)
            {
                _view?.RefreshChromeTitle();
                return;
            }
            if (Chrome == null) return;
            Chrome.Title = title;
            Chrome.TitleText.ToolTip = null;
        }

        #region IWidgetViewHost / IFolderPanelHost

        WidgetData IWidgetViewHost.Data => Data;
        WidgetChrome IWidgetViewHost.Chrome => Chrome;
        bool IWidgetViewHost.IsPanelOpen => IsPanelOpen;
        double IWidgetViewHost.ContentWidth => WidgetViewHostMetrics.ContentWidth(this);
        double IWidgetViewHost.ContentHeight => WidgetViewHostMetrics.ContentHeight(this);
        (double width, double height) IWidgetViewHost.GetPanelSize() => GetPanelSize();
        void IWidgetViewHost.NotifyDataChanged() => NotifyDataChanged();
        void IWidgetViewHost.RefreshChromeTitle(string title) => RefreshChromeTitle(title);
        void IWidgetViewHost.PersistSelection() => WidgetManager.Instance.SaveConfig();
        void IWidgetViewHost.ShowWidgetMenu() => ShowWidgetMenu(null);

        IntPtr IFolderPanelHost.PanelHwnd
        {
            get
            {
                try { return new System.Windows.Interop.WindowInteropHelper(this).Handle; }
                catch (Exception) { return IntPtr.Zero; }
            }
        }

        bool IFolderPanelHost.IsLocked => _folderData.IsLocked;
        bool IFolderPanelHost.IsPanelPinned => _folderData.IsPanelPinned;
        bool IFolderPanelHost.IsModalOpen => IsModalOpen;
        void IFolderPanelHost.RunModal(Action show) => RunModal(show);
        void IFolderPanelHost.HidePanel() => HidePanel();
        void IFolderPanelHost.FitPanelToWorkArea() => FitPanelToWorkArea();

        #endregion

        // Legacy reflection pins still enter through the window facade.
        private MenuItem CreateFolderColorEntry(DisplayItem item) => _view.CreateFolderColorEntry(item);
        private ContextMenu BuildRootItemMenu(DisplayItem item) => _view.BuildRootItemMenu(item);
        private ContextMenu BuildBrowseItemMenu(DisplayItem item, System.Collections.Generic.List<DisplayItem> selected)
            => _view.BuildBrowseItemMenu(item, selected);
    }
}
