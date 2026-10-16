using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// The colours, type and contrast mode the note renderer paints with. The
    /// reading/editing views build it from the theme tokens plus the widget
    /// colour; the checks build it by hand. Plain public fields on purpose: it
    /// is a parameter bag, never a bindable model, and it deliberately holds no
    /// literal colours - every value comes from <see cref="ThemeManager"/>,
    /// <see cref="UiTokens"/> or the widget's own colour.
    /// </summary>
    public sealed class NoteRenderStyle
    {
        /// <summary>Body ink: paragraphs, list text, table cells.</summary>
        public Brush Text;

        /// <summary>Secondary ink: quote content.</summary>
        public Brush Muted;

        /// <summary>The widget colour: task boxes, their check and links.</summary>
        public Brush Accent;

        /// <summary>The band behind inline code spans and fenced code blocks.</summary>
        public Brush CodeBackground;

        /// <summary>Rules: the quote bar, the table header underline, a thematic break.</summary>
        public Brush Divider;

        /// <summary>True while the system high-contrast preference is on: every
        /// brush falls back to its system role (design §5.1).</summary>
        public bool HighContrast;

        /// <summary>The body font family (CJK friendly).</summary>
        public FontFamily BodyFont;

        /// <summary>The body size; the heading ladder scales from it.</summary>
        public double BaseFontSize;
    }

    /// <summary>
    /// The renderer's two outbound actions - the view wires them to the data
    /// (T13/T14). A null instance, or a null member, leaves the click inert.
    /// </summary>
    public sealed class NoteRenderCallbacks
    {
        /// <summary>A task box was clicked: the absolute source line to toggle.</summary>
        public Action<int> ToggleTaskLine;

        /// <summary>A link was clicked: the http/https target to open.</summary>
        public Action<string> OpenLink;
    }

    /// <summary>
    /// Turns the markdown AST (<see cref="MarkdownParser"/>) into a
    /// <see cref="FlowDocument"/>. Pure painting: it invents no text and keeps
    /// no state, so a re-render after every edit is cheap and idempotent. The
    /// closed subset maps one-to-one onto FlowDocument blocks (design §4):
    /// headings to a size ladder, paragraphs to soft-broken lines, lists to
    /// List/ListItem, tasks to a clickable hand-drawn box tagged with its
    /// source line, quotes to a left rule, code to a monospace band, tables to
    /// an aligned Table and rules to a thin line. Colours arrive through
    /// <see cref="NoteRenderStyle"/> and the high contrast flag swaps the whole
    /// palette for the system roles. Every painted block is also tagged with
    /// the absolute source line it stands for through
    /// <see cref="SourceLineProperty"/>, so a host can map a click back to the
    /// line it edits.
    /// </summary>
    public static class NoteMarkdownRenderer
    {
        /// <summary>The H1..H6 multipliers of the body size (design §4).</summary>
        private static readonly double[] HeadingScale = { 1.6, 1.4, 1.25, 1.12, 1.05, 1.0 };

        /// <summary>
        /// The absolute source line a painted element stands for (default -1:
        /// "no line"). T4/T6 read it back to map a click in the document to the
        /// line it edits. Registered on this static class on purpose - WPF
        /// allows it, exactly like the BCL's <see cref="TextOptions"/>.
        /// </summary>
        public static readonly DependencyProperty SourceLineProperty =
            DependencyProperty.RegisterAttached("SourceLine", typeof(int), typeof(NoteMarkdownRenderer),
                new FrameworkPropertyMetadata(-1));

        /// <summary>Tags <paramref name="element"/> with its source line.</summary>
        public static void SetSourceLine(DependencyObject element, int line)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            element.SetValue(SourceLineProperty, line);
        }

        /// <summary>The element's source line, or -1 when it was never tagged.</summary>
        public static int GetSourceLine(DependencyObject element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            return (int)element.GetValue(SourceLineProperty);
        }

        /// <summary>
        /// The heading size for a 1..6 level: the body size times the ladder
        /// multiplier, levels clamped to the ends so an out-of-range level can
        /// never size a box off the ladder.
        /// </summary>
        public static double HeadingFontSize(int level, double baseSize)
        {
            int clamped = Math.Min(Math.Max(level, 1), HeadingScale.Length);
            return baseSize * HeadingScale[clamped - 1];
        }

        /// <summary>The code stack: Consolas first, then the usual fallbacks.</summary>
        private static readonly FontFamily MonospaceFont =
            new FontFamily("Consolas, Courier New, Courier, monospace");

        /// <summary>The task box's side, edge and the hand-drawn check's path.</summary>
        private const double BoxSize = 14;
        private const double BoxStroke = 1.6;
        private const string CheckPathData = "M 3,7.6 L 5.9,10.5 L 11.2,4.1";

        /// <summary>
        /// Paints <paramref name="blocks"/> into a fresh document. <paramref name="callbacks"/>
        /// may be null: the document then renders read-only (task boxes and
        /// links stay inert but still visible).
        /// </summary>
        public static FlowDocument Build(List<MdBlock> blocks, NoteRenderStyle style,
            NoteRenderCallbacks callbacks = null)
        {
            if (style == null) throw new ArgumentNullException(nameof(style));

            var ctx = new RenderContext
            {
                Palette = Palette.From(style),
                BaseFontSize = style.BaseFontSize,
                Callbacks = callbacks
            };

            var doc = new FlowDocument
            {
                FontFamily = style.BodyFont,
                FontSize = style.BaseFontSize,
                Foreground = ctx.Palette.Text,
                Background = Brushes.Transparent,
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
                // One column: a note reads top to bottom, never in the
                // newspaper columns a wide FlowDocument would default to.
                ColumnWidth = double.PositiveInfinity
            };

            if (blocks != null)
            {
                foreach (var block in blocks)
                {
                    if (block != null) AppendBlock(doc.Blocks, block, ctx, ctx.Palette.Text);
                }
            }
            return doc;
        }

        /// <summary>The resolved brushes for one render.</summary>
        private sealed class Palette
        {
            public Brush Text;
            public Brush Muted;
            public Brush Accent;
            public Brush CodeBackground;
            public Brush Divider;

            /// <summary>The style's own brushes, or the system roles while high
            /// contrast is on (design §5.1: a full fallback, not a tint).</summary>
            public static Palette From(NoteRenderStyle style)
            {
                if (!style.HighContrast)
                {
                    return new Palette
                    {
                        Text = style.Text,
                        Muted = style.Muted,
                        Accent = style.Accent,
                        CodeBackground = style.CodeBackground,
                        Divider = style.Divider
                    };
                }

                return new Palette
                {
                    Text = SystemColors.WindowTextBrush,
                    Muted = SystemColors.GrayTextBrush,
                    Accent = SystemColors.HighlightBrush,
                    CodeBackground = SystemColors.ControlBrush,
                    Divider = SystemColors.ControlDarkBrush
                };
            }
        }

        /// <summary>Everything one Build call paints with, threaded down the
        /// recursion instead of a long parameter list.</summary>
        private sealed class RenderContext
        {
            public Palette Palette;
            public double BaseFontSize;
            public NoteRenderCallbacks Callbacks;
        }

        /// <summary>The inline state a Run is painted with. Nullable fields mean
        /// "inherit from the block", so a heading's weight survives a plain run.</summary>
        private sealed class InlineFormat
        {
            public FontWeight? Weight;
            public FontStyle? Slant;
            public TextDecorationCollection Decorations;
            public FontFamily Font;
            public Brush Background;
            public Brush Foreground;

            public InlineFormat Copy()
            {
                return new InlineFormat
                {
                    Weight = Weight,
                    Slant = Slant,
                    Decorations = Decorations,
                    Font = Font,
                    Background = Background,
                    Foreground = Foreground
                };
            }
        }

        private static void AppendBlock(BlockCollection target, MdBlock block, RenderContext ctx, Brush foreground)
        {
            var heading = block as MdHeading;
            if (heading != null) { target.Add(BuildHeading(heading, ctx, foreground)); return; }

            var paragraph = block as MdParagraph;
            if (paragraph != null) { target.Add(BuildParagraph(paragraph, ctx, foreground)); return; }

            var list = block as MdListBlock;
            if (list != null) { target.Add(BuildList(list, ctx, foreground)); return; }

            var task = block as MdTaskBlock;
            if (task != null) { target.Add(BuildTask(task, ctx, foreground)); return; }

            var quote = block as MdQuote;
            if (quote != null) { target.Add(BuildQuote(quote, ctx)); return; }

            var code = block as MdCodeBlock;
            if (code != null) { target.Add(BuildCode(code, ctx, foreground)); return; }

            var table = block as MdTable;
            if (table != null) { target.Add(BuildTable(table, ctx, foreground)); return; }

            if (block is MdThematicBreak) target.Add(BuildRule(block.Line, ctx));
        }

        private static Paragraph BuildHeading(MdHeading heading, RenderContext ctx, Brush foreground)
        {
            var paragraph = NewParagraph(foreground);
            paragraph.FontSize = HeadingFontSize(heading.Level, ctx.BaseFontSize);
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.Margin = new Thickness(0, 6, 0, 4);
            SetSourceLine(paragraph, heading.Line);
            AppendInlines(paragraph.Inlines, heading.Inlines, ctx, new InlineFormat(), foreground);
            return paragraph;
        }

        private static Paragraph BuildParagraph(MdParagraph block, RenderContext ctx, Brush foreground)
        {
            var paragraph = NewParagraph(foreground);
            SetSourceLine(paragraph, block.Line);
            var lines = block.Lines;
            if (lines != null)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    // Soft breaks stay breaks: the parser hands one inline run
                    // per source line (the Chinese-note deviation, design §4).
                    // Each line group is a Span tagged with its own source line,
                    // so a click inside it maps back to the line it edits.
                    if (i > 0) paragraph.Inlines.Add(new LineBreak());
                    var span = new Span();
                    SetSourceLine(span, block.Line + i);
                    AppendInlines(span.Inlines, lines[i], ctx, new InlineFormat(), foreground);
                    paragraph.Inlines.Add(span);
                }
            }
            return paragraph;
        }

        private static List BuildList(MdListBlock block, RenderContext ctx, Brush foreground)
        {
            var list = new List
            {
                MarkerStyle = block.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                MarkerOffset = 8,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(20, 0, 0, 0)
            };
            if (block.Ordered) list.StartIndex = block.Start;

            if (block.Items != null)
            {
                foreach (var item in block.Items)
                {
                    if (item == null) continue;
                    var paragraph = NewParagraph(foreground);
                    paragraph.Margin = new Thickness(0);
                    SetSourceLine(paragraph, item.Line);
                    AppendInlines(paragraph.Inlines, item.Inlines, ctx, new InlineFormat(), foreground);
                    list.ListItems.Add(new ListItem(paragraph));
                }
            }
            return list;
        }

        private static Paragraph BuildTask(MdTaskBlock block, RenderContext ctx, Brush foreground)
        {
            var paragraph = NewParagraph(foreground);
            SetSourceLine(paragraph, block.Line);
            paragraph.Inlines.Add(TaskGlyph(block.Line, block.Checked, ctx));
            AppendInlines(paragraph.Inlines, block.Inlines, ctx, new InlineFormat(), foreground);
            return paragraph;
        }

        /// <summary>
        /// The clickable task box: a hand-drawn-feel square in the accent colour
        /// (the system highlight while high contrast is on), tagged with the
        /// source line it must write back to. Completed boxes carry the check;
        /// open ones do not.
        /// </summary>
        private static InlineUIContainer TaskGlyph(int line, bool completed, RenderContext ctx)
        {
            var box = new Border
            {
                Width = BoxSize,
                Height = BoxSize,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(BoxStroke),
                BorderBrush = ctx.Palette.Accent,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 6, 0),
                Tag = line
            };

            if (completed)
            {
                box.Child = new Path
                {
                    Data = Geometry.Parse(CheckPathData),
                    Stroke = ctx.Palette.Accent,
                    StrokeThickness = BoxStroke,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stretch = Stretch.None
                };
            }

            int sourceLine = line;
            var callbacks = ctx.Callbacks;
            box.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                callbacks?.ToggleTaskLine?.Invoke(sourceLine);
            };

            return new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Center };
        }

        /// <summary>
        /// A quote: the design asks for a left rule plus an indent, and a
        /// <see cref="Section"/> is the only block-level FlowDocument element
        /// that carries a border, so the rule lives there. Content keeps the
        /// muted ink.
        /// </summary>
        private static Section BuildQuote(MdQuote block, RenderContext ctx)
        {
            var section = new Section
            {
                BorderBrush = ctx.Palette.Divider,
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(12, 0, 0, 0),
                Margin = new Thickness(0, 2, 0, 8),
                Foreground = ctx.Palette.Muted
            };
            SetSourceLine(section, block.Line);

            if (block.Blocks != null)
            {
                foreach (var inner in block.Blocks)
                {
                    if (inner != null) AppendBlock(section.Blocks, inner, ctx, ctx.Palette.Muted);
                }
            }
            return section;
        }

        /// <summary>A fenced block: monospace on the code band. The text wraps
        /// like any other content (no horizontal scrolling, design §4).</summary>
        private static Paragraph BuildCode(MdCodeBlock block, RenderContext ctx, Brush foreground)
        {
            var paragraph = new Paragraph
            {
                FontFamily = MonospaceFont,
                FontSize = ctx.BaseFontSize,
                Foreground = foreground,
                Background = ctx.Palette.CodeBackground,
                Margin = new Thickness(0, 2, 0, 8)
            };
            SetSourceLine(paragraph, block.Line);
            paragraph.Inlines.Add(new Run(block.Text ?? string.Empty));
            return paragraph;
        }

        private static Table BuildTable(MdTable block, RenderContext ctx, Brush foreground)
        {
            MdAlign[] align = block.Align ?? new MdAlign[0];
            var table = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 2, 0, 8)
            };
            SetSourceLine(table, block.Line);

            for (int c = 0; c < align.Length; c++)
            {
                table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            }

            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            var header = new TableRow();
            for (int c = 0; c < align.Length; c++)
            {
                header.Cells.Add(BuildCell(CellAt(block.Header, c), align[c], ctx, foreground, true));
            }
            group.Rows.Add(header);

            if (block.Rows != null)
            {
                for (int r = 0; r < block.Rows.Count; r++)
                {
                    var row = new TableRow();
                    for (int c = 0; c < align.Length; c++)
                    {
                        row.Cells.Add(BuildCell(CellAt(block.Rows[r], c), align[c], ctx, foreground, false));
                    }
                    group.Rows.Add(row);
                }
            }
            return table;
        }

        /// <summary>The cell's inlines. The parser pads rows to the alignment
        /// count, but a hand-built AST may not, so a missing cell is empty.</summary>
        private static List<MdInline> CellAt(List<List<MdInline>> row, int index)
        {
            if (row == null || index >= row.Count) return null;
            return row[index];
        }

        private static TableCell BuildCell(List<MdInline> inlines, MdAlign align, RenderContext ctx,
            Brush foreground, bool header)
        {
            var paragraph = NewParagraph(foreground);
            paragraph.Margin = new Thickness(0);
            if (header) paragraph.FontWeight = FontWeights.Bold;
            AppendInlines(paragraph.Inlines, inlines, ctx, new InlineFormat(), foreground);

            var cell = new TableCell(paragraph)
            {
                Padding = new Thickness(6, 4, 6, 4),
                TextAlignment = TextAlignmentOf(align)
            };
            if (header)
            {
                cell.BorderThickness = new Thickness(0, 0, 0, 1);
                cell.BorderBrush = ctx.Palette.Divider;
            }
            return cell;
        }

        private static TextAlignment TextAlignmentOf(MdAlign align)
        {
            switch (align)
            {
                case MdAlign.Center: return TextAlignment.Center;
                case MdAlign.Right: return TextAlignment.Right;
                // None is the GFM default and reads left, like an explicit :---.
                default: return TextAlignment.Left;
            }
        }

        private static Block BuildRule(int line, RenderContext ctx)
        {
            var container = new BlockUIContainer(new Border
            {
                Height = 1,
                Background = ctx.Palette.Divider,
                Margin = new Thickness(0, 8, 0, 8)
            });
            SetSourceLine(container, line);
            return container;
        }

        /// <summary>
        /// The inline scan: literal text and code spans become Runs, the
        /// emphasis family narrows the run state (so nesting composes), and a
        /// link becomes a <see cref="Hyperlink"/> whose click hands the target
        /// to the callbacks. Runs are flattened - the AST's nesting shows up as
        /// run properties, matching the design's Strong→FontWeight mapping.
        /// </summary>
        private static void AppendInlines(InlineCollection target, List<MdInline> inlines, RenderContext ctx,
            InlineFormat format, Brush foreground)
        {
            if (inlines == null) return;

            foreach (var inline in inlines)
            {
                if (inline == null) continue;

                var text = inline as MdText;
                if (text != null) { target.Add(NewRun(text.Text, format, foreground)); continue; }

                var code = inline as MdCodeSpan;
                if (code != null)
                {
                    var codeFormat = format.Copy();
                    codeFormat.Font = MonospaceFont;
                    codeFormat.Background = ctx.Palette.CodeBackground;
                    target.Add(NewRun(code.Text, codeFormat, foreground));
                    continue;
                }

                var link = inline as MdLink;
                if (link != null) { target.Add(BuildHyperlink(link, ctx, format, foreground)); continue; }

                var strong = inline as MdStrong;
                if (strong != null)
                {
                    var nested = format.Copy();
                    nested.Weight = FontWeights.Bold;
                    AppendInlines(target, strong.Inlines, ctx, nested, foreground);
                    continue;
                }

                var em = inline as MdEm;
                if (em != null)
                {
                    var nested = format.Copy();
                    nested.Slant = FontStyles.Italic;
                    AppendInlines(target, em.Inlines, ctx, nested, foreground);
                    continue;
                }

                var strike = inline as MdStrike;
                if (strike != null)
                {
                    var nested = format.Copy();
                    nested.Decorations = TextDecorations.Strikethrough;
                    AppendInlines(target, strike.Inlines, ctx, nested, foreground);
                    continue;
                }
            }
        }

        private static Run NewRun(string text, InlineFormat format, Brush foreground)
        {
            var run = new Run(text ?? string.Empty) { Foreground = format.Foreground ?? foreground };
            if (format.Weight.HasValue) run.FontWeight = format.Weight.Value;
            if (format.Slant.HasValue) run.FontStyle = format.Slant.Value;
            if (format.Font != null) run.FontFamily = format.Font;
            if (format.Background != null) run.Background = format.Background;
            if (format.Decorations != null) run.TextDecorations = format.Decorations;
            return run;
        }

        /// <summary>
        /// A web link: the label wears the accent, the target rides
        /// <see cref="Hyperlink.NavigateUri"/> (the reading view's own pin reads
        /// it) and the click routes through the callbacks. The renderer never
        /// navigates itself - it marks the click handled so WPF's default
        /// navigation stays out of the way and the host owns what happens next.
        /// </summary>
        private static Hyperlink BuildHyperlink(MdLink link, RenderContext ctx, InlineFormat format, Brush foreground)
        {
            string url = link.Url;
            var hyperlink = new Hyperlink
            {
                Foreground = ctx.Palette.Accent,
                ToolTip = url
            };

            if (!string.IsNullOrEmpty(url))
            {
                try { hyperlink.NavigateUri = new Uri(url, UriKind.Absolute); }
                catch (UriFormatException) { /* the parser keeps non-web targets literal */ }
            }

            var callbacks = ctx.Callbacks;
            hyperlink.Click += (s, e) =>
            {
                e.Handled = true;
                callbacks?.OpenLink?.Invoke(url);
            };
            // The Click handler marks its own event handled, but a click on a
            // Hyperlink also raises RequestNavigate; the host owns navigation,
            // so swallow this too (the renderer never opens a browser).
            hyperlink.RequestNavigate += (s, e) => e.Handled = true;

            var label = format.Copy();
            label.Foreground = ctx.Palette.Accent;
            hyperlink.Inlines.Add(NewRun(link.Text, label, foreground));
            return hyperlink;
        }

        private static Paragraph NewParagraph(Brush foreground)
        {
            return new Paragraph { Foreground = foreground, Margin = new Thickness(0, 0, 0, 8) };
        }
    }
}
