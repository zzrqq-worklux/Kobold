using System;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.Controls
{
    /// <summary>
    /// The search widget: shared chrome + the file-search view. The view owns
    /// the debounced query loop and its background cancellation; the window
    /// only supplies the chrome and closes the view's subscriptions when the
    /// panel goes away.
    /// </summary>
    public partial class SearchWidget : WidgetWindowBase, IWidgetViewHost
    {
        /// <summary>Content block of the default search panel (box + results).</summary>
        private const double CONTENT_WIDTH = 420;
        private const double CONTENT_HEIGHT = 500;

        /// <summary>Kind minimum: the search box, one status line and a few rows.</summary>
        private const double MIN_WIDTH = 340;
        private const double MIN_HEIGHT = 320;

        private readonly SearchPanelView _view;

        public SearchWidget(SearchData data) : this(data, null)
        {
        }

        /// <summary>
        /// Builds the window around an existing live view (the internal
        /// hand-off from a released standalone window) or, with null, creates
        /// and attaches a fresh one. An adopted view only changes host - it is
        /// never re-attached; its service subscriptions stay alive.
        /// </summary>
        internal SearchWidget(SearchData data, IWidgetContentView contents) : base(data)
        {
            InitializeComponent();
            AttachChrome(SearchChrome);

            var view = contents as SearchPanelView;
            if (contents != null && view == null)
            {
                throw new ArgumentException(
                    "A search window can only adopt a SearchPanelView.", nameof(contents));
            }

            if (view != null)
            {
                _view = view;
                IWidgetContentView face = view;
                SearchChrome.ContentHost.Content = face.View;
                face.RebindHost(this);
            }
            else
            {
                _view = new SearchPanelView();
                SearchChrome.ContentHost.Content = _view;
                _view.Attach(data, this);
            }

            // A closed panel must not keep the view's service subscriptions,
            // the debounce timer or an in-flight query alive. A view already
            // released to another host is left alone.
            Closing += (s, e) =>
            {
                if (!HasReleasedContentView) _view.Detach();
            };
        }

        /// <summary>The search box + result list hosted in the chrome's content slot.</summary>
        public SearchPanelView PanelView => _view;

        /// <summary>
        /// The legacy path of <see cref="WidgetWindowBase.GetPanelSize"/> adds
        /// the 40 DIP chrome header to this block; a widget with persisted
        /// sizes never reads it (the migration supplies 422x562).
        /// </summary>
        protected override (double width, double height) GetContentSize()
        {
            return (CONTENT_WIDTH, CONTENT_HEIGHT);
        }

        /// <summary>The search panel's minimum; the base's defaults are folder/todo only.</summary>
        protected override (double width, double height) GetMinPanelSize()
        {
            return (MIN_WIDTH, MIN_HEIGHT);
        }

        /// <summary>
        /// Re-opening the panel refreshes the status line from the service's
        /// in-memory snapshot (no SQLite work on the UI thread).
        /// </summary>
        public override void UpdateUI()
        {
            base.UpdateUI();
            _view?.RefreshLocalization();
            _view?.RefreshStatus();
            // A live colour change arrives through RefreshTheme -> UpdateUI;
            // the accent must follow without closing and reopening the panel.
            _view?.RefreshAccent();
            // The comet wears the widget colour too: repaint it while a query
            // or an index scan/reconcile runs, so a menu colour change is
            // visible without closing the panel.
            if (SearchChrome != null && SearchChrome.IsGlowActive)
            {
                SearchChrome.ApplyGlowColor(Data?.Color);
            }
        }

        /// <summary>
        /// A hidden panel must not keep the loading glow running: deactivation
        /// hides the panel while a query or a scan may still be in flight.
        /// </summary>
        protected override void OnPanelHidden()
        {
            base.OnPanelHidden();
            // A released view belongs to its adopting host now; this window's
            // collapse must not stop the new chrome's glow.
            if (!HasReleasedContentView) _view?.Deactivate();
        }

        /// <summary>The search box takes focus as soon as the panel is shown.</summary>
        protected override void OnPanelShown()
        {
            base.OnPanelShown();
            // ShowPanel runs UpdateUI (and with it RefreshStatus) while
            // IsPanelOpen is still false, so a scan/reconcile glow was recorded
            // but not painted. The panel is open now: refresh again so a reopen
            // mid-scan relights the comet.
            _view?.Activate();
        }

        #region IWidgetViewHost

        WidgetData IWidgetViewHost.Data => Data;

        WidgetChrome IWidgetViewHost.Chrome => Chrome;

        bool IWidgetViewHost.IsPanelOpen => IsPanelOpen;

        double IWidgetViewHost.ContentWidth => WidgetViewHostMetrics.ContentWidth(this);

        double IWidgetViewHost.ContentHeight => WidgetViewHostMetrics.ContentHeight(this);

        (double width, double height) IWidgetViewHost.GetPanelSize() => GetPanelSize();

        void IWidgetViewHost.NotifyDataChanged() => NotifyDataChanged();

        void IWidgetViewHost.RefreshChromeTitle(string title)
        {
            if (Chrome == null) return;
            Chrome.Title = title ?? (Data == null ? string.Empty : Data.Name ?? string.Empty);
            Chrome.TitleText.ToolTip = null;
        }

        void IWidgetViewHost.PersistSelection()
        {
            // The search kind has no selection to remember.
        }

        void IWidgetViewHost.ShowWidgetMenu() => ShowWidgetMenu(null);

        #endregion

        public override void ShowWidgetMenu(Action onClosed = null)
        {
            WidgetMenuBuilder.Build(this, onClosed).IsOpen = true;
        }
    }
}
