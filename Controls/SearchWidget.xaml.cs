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
    public partial class SearchWidget : WidgetWindowBase
    {
        /// <summary>Content block of the default search panel (box + results).</summary>
        private const double CONTENT_WIDTH = 420;
        private const double CONTENT_HEIGHT = 500;

        /// <summary>Kind minimum: the search box, one status line and a few rows.</summary>
        private const double MIN_WIDTH = 340;
        private const double MIN_HEIGHT = 320;

        private readonly SearchPanelView _view;

        public SearchWidget(SearchData data) : base(data)
        {
            InitializeComponent();
            AttachChrome(SearchChrome);

            _view = new SearchPanelView();
            SearchChrome.ContentHost.Content = _view;
            _view.Attach(data, this);

            // A closed panel must not keep the view's service subscriptions,
            // the debounce timer or an in-flight query alive.
            Closing += (s, e) => _view.Detach();
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
            if (SearchChrome != null) SearchChrome.StopGlow();
        }

        /// <summary>The search box takes focus as soon as the panel is shown.</summary>
        protected override void OnPanelShown()
        {
            base.OnPanelShown();
            // ShowPanel runs UpdateUI (and with it RefreshStatus) while
            // IsPanelOpen is still false, so a scan/reconcile glow was recorded
            // but not painted. The panel is open now: refresh again so a reopen
            // mid-scan relights the comet.
            _view?.RefreshStatus();
            // ShowPanel raises this before Show(); queue the focus until the
            // window is actually visible.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible) _view?.FocusSearch();
            }));
        }

        public override void ShowWidgetMenu(Action onClosed = null)
        {
            WidgetMenuBuilder.Build(this, onClosed).IsOpen = true;
        }
    }
}
