using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// The pure click-to-caret map behind the note surface: a visual character
    /// offset inside a rendered line becomes the absolute source line and the
    /// 0-based source column the source box should put its caret on. Paragraph
    /// lines map through the parser's per-character source columns; a heading,
    /// bullet or task box adds the marker prefix the renderer hides; code maps
    /// into its body line; a table, quote or rule falls back to the line start.
    /// No WPF types are involved (design §4.3).
    /// </summary>
    public static class NoteCaretMap
    {
        /// <summary>
        /// Maps <paramref name="visualOffset"/> - the 0-based visible-character
        /// offset inside the clicked line - to the absolute source line and
        /// column. A missing block, an out-of-range line or a line that owns no
        /// inline run reads as the line start; a negative offset reads as the
        /// first visible character.
        /// </summary>
        public static (int Line, int Column) Map(string text, int line, int visualOffset)
        {
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');

            if (line < 0) return (0, 0);
            if (line >= lines.Length) return (line, 0);

            string lineText = lines[line];
            var blocks = MarkdownParser.Parse(normalized);
            foreach (var block in blocks)
            {
                var heading = block as MdHeading;
                if (heading != null && heading.Line == line)
                    return MapInlines(lineText, heading.Inlines, HeadingPrefix(lineText), line, visualOffset);

                var paragraph = block as MdParagraph;
                if (paragraph != null && paragraph.Lines != null)
                {
                    int index = line - paragraph.Line;
                    if (index >= 0 && index < paragraph.Lines.Count)
                        return MapInlines(lineText, paragraph.Lines[index], 0, line, visualOffset);
                }

                var list = block as MdListBlock;
                if (list != null && list.Items != null)
                {
                    foreach (var item in list.Items)
                    {
                        if (item != null && item.Line == line)
                            return MapInlines(lineText, item.Inlines, ListPrefix(lineText), line, visualOffset);
                    }
                }

                var task = block as MdTaskBlock;
                if (task != null && task.Line == line)
                    return MapInlines(lineText, task.Inlines, TaskPrefix(lineText), line, visualOffset);

                var code = block as MdCodeBlock;
                if (code != null && code.Line == line)
                    return MapCode(code, line, visualOffset);
            }

            // A blank line, a quote / table / rule, or a line with no inline
            // owner all land on the clicked line's first column.
            return (line, 0);
        }

        /// <summary>
        /// Maps an offset inside one line's inline run: the parser's source
        /// columns stay relative to the inline text, so the hidden marker
        /// prefix is added here. The offset one past the last visible character
        /// is the line end (one past its source column).
        /// </summary>
        private static (int Line, int Column) MapInlines(string lineText, List<MdInline> inlines,
            int prefix, int line, int visualOffset)
        {
            int total = VisibleLength(inlines);
            int offset = visualOffset < 0 ? 0 : (visualOffset > total ? total : visualOffset);

            int relative = 0;
            if (total > 0)
            {
                if (offset < total)
                {
                    if (!TryMapInlines(inlines, offset, out relative)) relative = 0;
                }
                else
                {
                    // The line end sits after the last visible character's
                    // source column (an escaped / nested run can leave gaps).
                    int last;
                    if (TryMapInlines(inlines, total - 1, out last)) relative = last + 1;
                }
            }

            int column = prefix + relative;
            if (column < 0) column = 0;
            if (column > lineText.Length) column = lineText.Length;
            return (line, column);
        }

        /// <summary>
        /// Maps an offset inside a code block: its body text counts the newlines
        /// before the offset, so the result is the body line's absolute source
        /// line and the column inside it. An offset past the body clamps to the
        /// body's end; an empty body falls back to the clicked line's start.
        /// </summary>
        private static (int Line, int Column) MapCode(MdCodeBlock block, int line, int visualOffset)
        {
            string body = block.Text ?? string.Empty;
            if (body.Length == 0) return (line, 0);

            int offset = visualOffset < 0 ? 0 : (visualOffset > body.Length ? body.Length : visualOffset);
            int codeLine = 0;
            int codeColumn = 0;
            for (int i = 0; i < offset; i++)
            {
                if (body[i] == '\n')
                {
                    codeLine++;
                    codeColumn = 0;
                }
                else
                {
                    codeColumn++;
                }
            }
            return (block.Line + 1 + codeLine, codeColumn);
        }

        /// <summary>
        /// The heading's hidden prefix: the leading `#` run and the whitespace
        /// after it. Scanned explicitly - trimming would also eat heading text
        /// that itself starts with a hash ("# #tag" starts its text at column 2).
        /// </summary>
        private static int HeadingPrefix(string lineText)
        {
            int i = 0;
            while (i < lineText.Length && lineText[i] == '#') i++;
            while (i < lineText.Length && (lineText[i] == ' ' || lineText[i] == '\t')) i++;
            return i;
        }

        /// <summary>The list item's hidden prefix, marker and its one space.</summary>
        private static int ListPrefix(string lineText)
        {
            int space = lineText.IndexOf(' ');
            return space < 0 ? 0 : space + 1;
        }

        /// <summary>The task box's hidden prefix: the bullet, the box and the
        /// space after it.</summary>
        private static int TaskPrefix(string lineText)
        {
            int close = lineText.IndexOf(']');
            return close < 0 ? 0 : close + 2;
        }

        /// <summary>The visible characters an inline contributes (markers and
        /// targets excluded).</summary>
        private static int VisibleLength(MdInline inline)
        {
            if (inline == null) return 0;

            var text = inline as MdText;
            if (text != null) return Length(text.Text);

            var code = inline as MdCodeSpan;
            if (code != null) return Length(code.Text);

            var link = inline as MdLink;
            if (link != null) return Length(link.Text);

            var strong = inline as MdStrong;
            if (strong != null) return VisibleLength(strong.Inlines);

            var em = inline as MdEm;
            if (em != null) return VisibleLength(em.Inlines);

            var strike = inline as MdStrike;
            if (strike != null) return VisibleLength(strike.Inlines);

            return 0;
        }

        private static int VisibleLength(List<MdInline> inlines)
        {
            if (inlines == null) return 0;
            int total = 0;
            foreach (var inline in inlines) total += VisibleLength(inline);
            return total;
        }

        private static int Length(string text)
        {
            return text == null ? 0 : text.Length;
        }

        /// <summary>Finds the inline owning <paramref name="offset"/> and maps
        /// the offset inside it to its source column.</summary>
        private static bool TryMapInlines(List<MdInline> inlines, int offset, out int column)
        {
            column = 0;
            if (inlines == null) return false;

            int remaining = offset;
            foreach (var inline in inlines)
            {
                int length = VisibleLength(inline);
                if (remaining < length) return TryMapInline(inline, remaining, out column);
                remaining -= length;
            }
            return false;
        }

        /// <summary>Maps an offset inside one inline; the emphasis family
        /// recurses into its own child run.</summary>
        private static bool TryMapInline(MdInline inline, int offset, out int column)
        {
            column = 0;

            var text = inline as MdText;
            if (text != null) return TryMapText(text.Text, text.SourcePositions, offset, out column);

            var code = inline as MdCodeSpan;
            if (code != null) return TryMapText(code.Text, code.SourcePositions, offset, out column);

            var link = inline as MdLink;
            if (link != null) return TryMapText(link.Text, link.SourcePositions, offset, out column);

            var strong = inline as MdStrong;
            if (strong != null) return TryMapInlines(strong.Inlines, offset, out column);

            var em = inline as MdEm;
            if (em != null) return TryMapInlines(em.Inlines, offset, out column);

            var strike = inline as MdStrike;
            if (strike != null) return TryMapInlines(strike.Inlines, offset, out column);

            return false;
        }

        /// <summary>One visible character's source column. A hand-built inline
        /// without the parser's positions has no usable column, so the caller
        /// parks at the line start instead of guessing.</summary>
        private static bool TryMapText(string visible, List<int> positions, int offset, out int column)
        {
            column = 0;
            if (visible == null || offset < 0 || offset >= visible.Length) return false;
            if (positions == null || offset >= positions.Count) return false;

            column = positions[offset];
            return true;
        }
    }
}
