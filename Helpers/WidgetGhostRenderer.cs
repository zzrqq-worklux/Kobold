using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kobold.Controls;
using Kobold.Core;

namespace Kobold.Helpers
{
    /// <summary>
    /// Renders the tab-detach ghost preview: a fresh, offscreen clone of the
    /// component at its pre-group standalone size (<see cref="WidgetData.PanelWidth"/>
    /// / <see cref="WidgetData.PanelHeight"/>), captured into a frozen bitmap.
    /// The clone is attached to a tiny host that has no chrome, no persistence
    /// and no live window, so no Loaded event (and therefore no search service)
    /// can start; it is detached and dropped before this returns. A failure
    /// returns null and the ghost falls back to the live tab view.
    /// </summary>
    internal static class WidgetGhostRenderer
    {
        /// <summary>Standalone size used when a legacy member has no remembered size.</summary>
        internal const double DefaultPanelWidth = 360;
        internal const double DefaultPanelHeight = 420;

        private const double BORDER = 1;

        /// <summary>
        /// Test seam: whether the last rendered clone was in a loaded tree. It
        /// must stay false - a loaded search clone would touch the service.
        /// </summary>
        internal static bool LastCloneWasLoaded { get; private set; }

        /// <summary>
        /// Test seam: frequent-file rows painted into the last search clone's
        /// empty-query list (0 for other kinds or when the config is empty).
        /// </summary>
        internal static int LastSearchFrequentRows { get; private set; }

        /// <summary>
        /// The member's own standalone size, falling back per dimension to
        /// <see cref="DefaultPanelWidth"/> / <see cref="DefaultPanelHeight"/>.
        /// </summary>
        internal static (double width, double height) OriginalPanelSize(WidgetData member)
        {
            double width = member == null ? 0 : member.PanelWidth ?? 0;
            double height = member == null ? 0 : member.PanelHeight ?? 0;

            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) width = DefaultPanelWidth;
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0) height = DefaultPanelHeight;
            return (width, height);
        }

        /// <summary>
        /// The standalone chrome's content box: the full panel minus the 1px
        /// border on each side and the 40 DIP header, clamped to at least 1x1.
        /// </summary>
        internal static (double width, double height) ContentSize(double panelWidth, double panelHeight)
        {
            double width = Math.Max(1, panelWidth - BORDER * 2);
            double height = Math.Max(1, panelHeight - WidgetConstants.HEADER_HEIGHT - BORDER * 2);
            return (width, height);
        }

        /// <summary>
        /// Builds the frozen preview bitmap, or null when the kind has no view,
        /// the layout fails or the render resources are unavailable.
        /// </summary>
        internal static ImageSource Render(WidgetData member)
        {
            LastCloneWasLoaded = false;
            LastSearchFrequentRows = 0;
            if (member == null) return null;

            IWidgetContentView view = null;
            try
            {
                view = WidgetViewFactory.Create(member);
                if (view == null) return null;

                var (panelWidth, panelHeight) = OriginalPanelSize(member);
                var (contentWidth, contentHeight) = ContentSize(panelWidth, panelHeight);
                var host = new GhostRenderHost(
                    member, panelWidth, panelHeight, contentWidth, contentHeight);

                // Attach only: no Activate/Refresh, so search never reads the
                // process service. The view is never added to a live window,
                // so Loaded (and its service subscription) cannot fire.
                view.Attach(member, host);

                // The empty-query search body is normally painted by
                // OnLoaded -> UpdateStatus (or an empty-query RunQuery), which
                // a never-loaded clone does not reach. PrepareForPreview paints
                // the same localized frequent-file list without the service.
                var search = view as SearchPanelView;
                if (search != null)
                {
                    search.PrepareForPreview();
                    LastSearchFrequentRows = search.FrequentRowCountForPreview;
                }

                FrameworkElement element = view.View;
                if (element == null) return null;

                element.Measure(new Size(contentWidth, contentHeight));
                element.Arrange(new Rect(0, 0, contentWidth, contentHeight));
                element.UpdateLayout();

                int pixelWidth = Math.Max(1, (int)Math.Ceiling(contentWidth));
                int pixelHeight = Math.Max(1, (int)Math.Ceiling(contentHeight));
                var bitmap = new RenderTargetBitmap(
                    pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                bitmap.Freeze();

                LastCloneWasLoaded = element.IsLoaded;
                return bitmap;
            }
            catch (Exception ex)
            {
                // A failed preview must never break the detach gesture.
                Debug.WriteLine("[Kobold] ghost render fallback: " + ex.Message);
                return null;
            }
            finally
            {
                Cleanup(view);
            }
        }

        /// <summary>
        /// Drops the clone's subscriptions/timers before the local reference
        /// goes away. The render root never had a parent, so dropping it here
        /// is the whole visual teardown; only the frozen bitmap is retained.
        /// </summary>
        private static void Cleanup(IWidgetContentView view)
        {
            if (view == null) return;

            try
            {
                var search = view as SearchPanelView;
                if (search != null)
                {
                    search.Detach();
                    return;
                }

                view.Deactivate();
                view.FlushPendingEdit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] ghost render cleanup: " + ex.Message);
            }
        }

        /// <summary>
        /// The tiny host the clone sees: the member data, no chrome, the
        /// original standalone panel size and content box, and inert actions.
        /// </summary>
        private sealed class GhostRenderHost : IWidgetViewHost
        {
            private readonly WidgetData _data;
            private readonly double _panelWidth;
            private readonly double _panelHeight;
            private readonly double _contentWidth;
            private readonly double _contentHeight;

            public GhostRenderHost(
                WidgetData data, double panelWidth, double panelHeight,
                double contentWidth, double contentHeight)
            {
                _data = data;
                _panelWidth = panelWidth;
                _panelHeight = panelHeight;
                _contentWidth = contentWidth;
                _contentHeight = contentHeight;
            }

            public WidgetData Data => _data;
            public WidgetChrome Chrome => null;
            public bool IsPanelOpen => true;
            public double ContentWidth => _contentWidth;
            public double ContentHeight => _contentHeight;

            public (double width, double height) GetPanelSize() => (_panelWidth, _panelHeight);

            public void NotifyDataChanged() { }

            public void RefreshChromeTitle(string title = null) { }

            public void PersistSelection() { }

            public void ShowWidgetMenu() { }
        }
    }
}
