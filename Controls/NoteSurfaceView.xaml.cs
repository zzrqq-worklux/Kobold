using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// The note surface: the rendered state ⇄ the block-editing state (T4),
    /// plus the source box's quick-insert menu (T5) - one expanded card's
    /// content in the note list. Rendered, a single read-only,
    /// document-enabled RichTextBox paints the whole note through
    /// <see cref="NoteMarkdownRenderer"/>: its task boxes write the flipped
    /// source line back (spec 4.1/5.3) and its links keep their target for the
    /// browser. This first cut is the rendered state only;
    /// <see cref="NotePanelView"/> keeps its own three states until T6 retires
    /// them, so the two surfaces deliberately duplicate code for now.
    /// </summary>
    public partial class NoteSurfaceView : UserControl
    {
        /// <summary>The rendered document's body font (the repo's UI font).</summary>
        private static readonly FontFamily BodyFont = new FontFamily("Segoe UI");

        /// <summary>Fires when the floating ‹ is clicked: the host collapses
        /// the expanded card (spec 3.3).</summary>
        public Action CollapseRequested;

        /// <summary>Fires after a write-back: the host runs the usual save and
        /// island refresh (mirroring <see cref="NotePanelView"/>'s change
        /// event).</summary>
        public Action DataChanged;

        private NoteData _data;
        private NoteItem _note;

        public NoteSurfaceView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Binds the surface to one note of a widget's data and paints the
        /// rendered state: the document box is shown and the three editing
        /// panes are hidden again, so a reused surface never keeps a stale
        /// splice (spec 4.1).
        /// </summary>
        public void Attach(NoteData data, NoteItem note)
        {
            _data = data ?? new NoteData();
            _note = note;
            ShowRendered();
            RenderReading();
        }

        /// <summary>
        /// Re-renders off the live theme and language: the rendered state
        /// rebuilds its document. T4 keeps the editing state's box text and
        /// session intact through this path (spec 4.3).
        /// </summary>
        public void Repaint()
        {
            RenderReading();
        }

        /// <summary>
        /// The rendered state's focus target: Backspace is a tunnelling
        /// handler, so it only reaches the host from a focused descendant (the
        /// old reading view's lesson, spec 4.1).
        /// </summary>
        public void FocusReading()
        {
            ReadingBox.Focus();
        }

        private void CollapseButton_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CollapseRequested?.Invoke();
        }

        /// <summary>The rendered state: the document box alone is visible.</summary>
        private void ShowRendered()
        {
            ReadingBox.Visibility = Visibility.Visible;
            BeforeBox.Visibility = Visibility.Collapsed;
            EditBox.Visibility = Visibility.Collapsed;
            AfterBox.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Paints the note into the read-only box: the AST comes from the
        /// parser, the style from the live theme and the callbacks route the
        /// task boxes and the links back to the data and the shell
        /// (spec 4.1/5.2/5.3).
        /// </summary>
        private void RenderReading()
        {
            if (_note == null) return;
            ReadingBox.Document = NoteMarkdownRenderer.Build(
                MarkdownParser.Parse(_note.Text), BuildRenderStyle(), RenderCallbacks());
        }

        /// <summary>The renderer's outbound actions for the rendered document.</summary>
        private NoteRenderCallbacks RenderCallbacks()
        {
            return new NoteRenderCallbacks
            {
                ToggleTaskLine = ToggleTask,
                OpenLink = OpenLink
            };
        }

        /// <summary>
        /// A task box was clicked (spec 4.1/5.3): the source line flips, the
        /// note becomes the newest modification, the host is notified and the
        /// document repaints. The scroll offset is read before the repaint and
        /// restored once the fresh document is laid out, so a click deep in a
        /// note does not throw the reader back to the top (a surface that never
        /// scrolled - every off-screen check - restores 0 harmlessly).
        /// </summary>
        private void ToggleTask(int line)
        {
            if (_note == null) return;

            double offset = SurfaceScroller.VerticalOffset;
            _note.Text = NoteOps.ToggleTaskAt(_note.Text, line);
            _note.ModifiedAt = DateTime.Now;
            DataChanged?.Invoke();
            RenderReading();
            Dispatcher.BeginInvoke(new Action(() => SurfaceScroller.ScrollToVerticalOffset(offset)),
                DispatcherPriority.Background);
        }

        /// <summary>
        /// A link was clicked (spec 4.1/5.2): an http/https target opens in the
        /// default browser, anything else stays inert (the parser keeps the
        /// non-web targets literal). Mirrors the old reading view's link open.
        /// </summary>
        private static void OpenLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Note link open failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The render style off the live theme (design 5.1): the body and the
        /// secondary ink are theme tokens, the accent is the widget's own
        /// colour, the code band and the rules follow the theme, and the system
        /// high-contrast mode is passed through (the renderer then swaps the
        /// whole palette for the system roles).
        /// </summary>
        private NoteRenderStyle BuildRenderStyle()
        {
            return new NoteRenderStyle
            {
                Text = ThemeManager.TextBrush,
                Muted = ThemeManager.SecondaryTextBrush,
                Accent = WidgetAccentBrush(),
                CodeBackground = ThemeManager.ControlBackgroundBrush,
                Divider = ThemeManager.BorderBrush,
                HighContrast = ThemeManager.IsHighContrast,
                BodyFont = BodyFont,
                BaseFontSize = UiTokens.FontBody
            };
        }

        /// <summary>The widget colour as a frozen brush (the same read the old
        /// reading view makes for its accent).</summary>
        private Brush WidgetAccentBrush()
        {
            var brush = new SolidColorBrush(Utils.HexToColor(_data.Color));
            brush.Freeze();
            return brush;
        }
    }
}
