using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kobold.Core
{
    /// <summary>The column alignment a table's separator row asks for.</summary>
    public enum MdAlign { None, Left, Center, Right }

    /// <summary>A block of the note markdown subset. <see cref="Line"/> is the
    /// absolute 0-based line of the whole source text the block starts on.</summary>
    public abstract class MdBlock { public int Line; }

    /// <summary>A heading line: one to six hashes then the text.</summary>
    public sealed class MdHeading : MdBlock { public int Level; public List<MdInline> Inlines; }

    /// <summary>A paragraph: one inline run per source line (soft breaks are
    /// kept as separate lines, it is a Chinese-note-friendly deviation from
    /// CommonMark).</summary>
    public sealed class MdParagraph : MdBlock { public List<List<MdInline>> Lines; }

    /// <summary>A run of same-kind list items.</summary>
    public sealed class MdListBlock : MdBlock { public bool Ordered; public int Start; public List<MdListItem> Items; }

    /// <summary>One list item and the source line it came from.</summary>
    public sealed class MdListItem { public int Line; public List<MdInline> Inlines; }

    /// <summary>A task box line: its state and the source line to write back to.</summary>
    public sealed class MdTaskBlock : MdBlock { public bool Checked; public List<MdInline> Inlines; }

    /// <summary>A quote: the recursively parsed content of its stripped lines.</summary>
    public sealed class MdQuote : MdBlock { public List<MdBlock> Blocks; }

    /// <summary>A fenced code block. <see cref="Info"/> is the info string
    /// (collected, never highlighted); <see cref="Text"/> is the body verbatim.</summary>
    public sealed class MdCodeBlock : MdBlock { public string Info; public string Text; }

    /// <summary>A table: one alignment per column, the header cells and the
    /// body rows, both padded or truncated to the alignment count.</summary>
    public sealed class MdTable : MdBlock { public MdAlign[] Align; public List<List<MdInline>> Header; public List<List<List<MdInline>>> Rows; }

    /// <summary>A standalone rule line (`---` / `***` / `___`).</summary>
    public sealed class MdThematicBreak : MdBlock { }

    /// <summary>A piece of inline content.</summary>
    public abstract class MdInline { }

    /// <summary>Literal text.</summary>
    public sealed class MdText : MdInline { public string Text; }

    /// <summary>An inline code span.</summary>
    public sealed class MdCodeSpan : MdInline { public string Text; }

    /// <summary>A link with its label and target.</summary>
    public sealed class MdLink : MdInline { public string Text; public string Url; }

    /// <summary>Bold content.</summary>
    public sealed class MdStrong : MdInline { public List<MdInline> Inlines; }

    /// <summary>Italic content.</summary>
    public sealed class MdEm : MdInline { public List<MdInline> Inlines; }

    /// <summary>Struck-through content.</summary>
    public sealed class MdStrike : MdInline { public List<MdInline> Inlines; }

    /// <summary>
    /// The block-level markdown parser behind the note widget: source text in,
    /// a small AST out. It covers the closed subset of the design - headings,
    /// paragraphs, bullet / numbered lists, task boxes, quotes, fenced code,
    /// rules and GFM-style tables - and is pure logic with no WPF dependency.
    /// Inline syntax is not handled here yet: <see cref="ParseInlines"/> is a
    /// literal stub that the inline task replaces.
    /// </summary>
    public static class MarkdownParser
    {
        private static readonly Regex FenceOpen = new Regex("^\\s*```", RegexOptions.Compiled);
        private static readonly Regex FenceClose = new Regex("^\\s*```+\\s*$", RegexOptions.Compiled);
        private static readonly Regex Heading = new Regex("^(#{1,6})\\s+(.*?)(?:\\s+#+)?\\s*$", RegexOptions.Compiled);
        // A quote line is exactly one ">" then a space or the end of the line:
        // quotes are single level, so ">> x" is literal text (design §4).
        private static readonly Regex QuoteLine = new Regex("^>(?: |$)", RegexOptions.Compiled);
        private static readonly Regex QuotePrefix = new Regex("^> ?", RegexOptions.Compiled);
        private static readonly Regex Task = new Regex("^- \\[( |x|X)\\](?: (.*)|$)", RegexOptions.Compiled);
        private static readonly Regex Bullet = new Regex("^[-*](?: (?<c>.*)|$)", RegexOptions.Compiled);
        private static readonly Regex Numbered = new Regex("^(?<n>\\d+)\\.(?: (?<c>.*)|$)", RegexOptions.Compiled);
        private static readonly Regex Rule = new Regex("^\\s*(?:-{3,}|\\*{3,}|_{3,})\\s*$", RegexOptions.Compiled);
        private static readonly Regex Separator = new Regex(
            "^\\s*\\|?\\s*:?-{1,}:?\\s*(\\|\\s*:?-{1,}:?\\s*)+\\|?\\s*$", RegexOptions.Compiled);
        private static readonly Regex CellSplit = new Regex("(?<!\\\\)\\|", RegexOptions.Compiled);

        /// <summary>
        /// Parses the whole text into blocks. Lines end in LF, CRLF or CR and
        /// are all indexed absolutely from zero; a quote's inner blocks are
        /// shifted by the quote's own start line.
        /// </summary>
        public static List<MdBlock> Parse(string text)
        {
            return ParseBlocks(text, true);
        }

        /// <summary>
        /// The block scan. <paramref name="allowQuotes"/> is false while a
        /// quote's own content is parsed: quotes are single level, so a quote
        /// block never holds another quote block.
        /// </summary>
        private static List<MdBlock> ParseBlocks(string text, bool allowQuotes)
        {
            var blocks = new List<MdBlock>();
            if (string.IsNullOrEmpty(text)) return blocks;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                if (IsBlank(lines[i])) { i++; continue; }

                if (FenceOpen.IsMatch(lines[i])) { blocks.Add(ReadCodeBlock(lines, ref i)); continue; }
                if (Rule.IsMatch(lines[i])) { blocks.Add(new MdThematicBreak { Line = i }); i++; continue; }
                if (IsTableStart(lines, i)) { blocks.Add(ReadTable(lines, ref i)); continue; }

                var heading = Heading.Match(lines[i]);
                if (heading.Success)
                {
                    blocks.Add(new MdHeading
                    {
                        Line = i,
                        Level = heading.Groups[1].Value.Length,
                        Inlines = ParseInlines(heading.Groups[2].Value)
                    });
                    i++;
                    continue;
                }

                if (allowQuotes && QuoteLine.IsMatch(lines[i]))
                {
                    blocks.Add(ReadQuote(lines, ref i));
                    continue;
                }

                // A task box is tested before the bullet list: it reads like a
                // bullet but is its own block with its source line kept.
                var task = Task.Match(lines[i]);
                if (task.Success)
                {
                    blocks.Add(new MdTaskBlock
                    {
                        Line = i,
                        Checked = task.Groups[1].Value != " ",
                        Inlines = ParseInlines(Group(task, 2))
                    });
                    i++;
                    continue;
                }

                if (Bullet.IsMatch(lines[i])) { blocks.Add(ReadList(lines, ref i, false)); continue; }
                if (Numbered.IsMatch(lines[i])) { blocks.Add(ReadList(lines, ref i, true)); continue; }

                blocks.Add(ReadParagraph(lines, ref i, allowQuotes));
            }
            return blocks;
        }

        /// <summary>
        /// Placeholder inline pass: the whole string becomes one literal
        /// <see cref="MdText"/>. The inline task replaces this; nothing else in
        /// the block parser needs to change when it does.
        /// </summary>
        public static List<MdInline> ParseInlines(string text)
        {
            return new List<MdInline> { new MdText { Text = text ?? string.Empty } };
        }

        private static MdCodeBlock ReadCodeBlock(IList<string> lines, ref int i)
        {
            int start = i;
            var opening = lines[i].TrimStart();
            int ticks = 0;
            while (ticks < opening.Length && opening[ticks] == '`') ticks++;

            i++;
            var body = new List<string>();
            while (i < lines.Count && !FenceClose.IsMatch(lines[i]))
            {
                body.Add(lines[i]);
                i++;
            }
            if (i < lines.Count) i++; // the closing fence

            return new MdCodeBlock
            {
                Line = start,
                Info = opening.Substring(ticks).Trim(),
                Text = string.Join("\n", body)
            };
        }

        private static MdQuote ReadQuote(IList<string> lines, ref int i)
        {
            int start = i;
            var inner = new List<string>();
            while (i < lines.Count && QuoteLine.IsMatch(lines[i]))
            {
                inner.Add(QuotePrefix.Replace(lines[i], string.Empty));
                i++;
            }

            // Quotes never nest: the inner parse runs with quote detection off.
            var blocks = ParseBlocks(string.Join("\n", inner), false);
            ShiftLines(blocks, start);
            return new MdQuote { Line = start, Blocks = blocks };
        }

        private static MdListBlock ReadList(IList<string> lines, ref int i, bool ordered)
        {
            var block = new MdListBlock
            {
                Line = i,
                Ordered = ordered,
                Start = 1,
                Items = new List<MdListItem>()
            };

            while (i < lines.Count)
            {
                if (Task.IsMatch(lines[i])) break; // tasks are their own blocks

                var m = ordered ? Numbered.Match(lines[i]) : Bullet.Match(lines[i]);
                if (!m.Success) break;

                if (ordered && block.Items.Count == 0)
                {
                    int start;
                    if (int.TryParse(m.Groups["n"].Value, out start)) block.Start = start;
                }

                block.Items.Add(new MdListItem
                {
                    Line = i,
                    Inlines = ParseInlines(m.Groups["c"].Success ? m.Groups["c"].Value : string.Empty)
                });
                i++;
            }
            return block;
        }

        private static MdParagraph ReadParagraph(IList<string> lines, ref int i, bool allowQuotes)
        {
            var paragraph = new MdParagraph { Line = i, Lines = new List<List<MdInline>>() };
            while (i < lines.Count && !StartsBlock(lines, i, allowQuotes))
            {
                paragraph.Lines.Add(ParseInlines(lines[i]));
                i++;
            }

            if (paragraph.Lines.Count == 0)
            {
                // Defensive: the caller only reaches a paragraph for a line
                // that starts no other block, so this can never fire.
                paragraph.Lines.Add(ParseInlines(lines[i]));
                i++;
            }
            return paragraph;
        }

        private static MdTable ReadTable(IList<string> lines, ref int i)
        {
            int start = i;

            var separator = SplitCells(lines[i + 1]);
            var align = new MdAlign[separator.Count];
            for (int k = 0; k < separator.Count; k++) align[k] = AlignOf(separator[k]);

            var header = NormalizeRow(SplitCells(lines[i]), align.Length);

            var rows = new List<List<List<MdInline>>>();
            i += 2;
            while (i < lines.Count && lines[i].IndexOf('|') >= 0)
            {
                rows.Add(NormalizeRow(SplitCells(lines[i]), align.Length));
                i++;
            }

            return new MdTable { Line = start, Align = align, Header = header, Rows = rows };
        }

        /// <summary>True when the line starts a block (and therefore ends a
        /// paragraph).</summary>
        private static bool StartsBlock(IList<string> lines, int i, bool allowQuotes)
        {
            var line = lines[i];
            return IsBlank(line) || FenceOpen.IsMatch(line) || Rule.IsMatch(line)
                || Heading.IsMatch(line) || Task.IsMatch(line)
                || Bullet.IsMatch(line) || Numbered.IsMatch(line) || IsTableStart(lines, i)
                || (allowQuotes && QuoteLine.IsMatch(line));
        }

        /// <summary>True when the line holds a pipe and is followed by a
        /// separator row (the two together are a table).</summary>
        private static bool IsTableStart(IList<string> lines, int i)
        {
            if (i + 1 >= lines.Count) return false;
            if (lines[i].IndexOf('|') < 0) return false;
            return Separator.IsMatch(lines[i + 1]);
        }

        /// <summary>Splits a table line into trimmed cells on unescaped pipes,
        /// dropping one optional leading and trailing pipe.</summary>
        private static List<string> SplitCells(string line)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("|", StringComparison.Ordinal))
                trimmed = trimmed.Substring(1);
            if (trimmed.Length > 0 && trimmed[trimmed.Length - 1] == '|'
                && (trimmed.Length < 2 || trimmed[trimmed.Length - 2] != '\\'))
                trimmed = trimmed.Substring(0, trimmed.Length - 1);

            var cells = new List<string>();
            foreach (var cell in CellSplit.Split(trimmed)) cells.Add(cell.Trim());
            return cells;
        }

        /// <summary>Pads or truncates a row to the column count, every cell
        /// going through <see cref="ParseInlines"/>.</summary>
        private static List<List<MdInline>> NormalizeRow(List<string> cells, int count)
        {
            var row = new List<List<MdInline>>();
            for (int k = 0; k < count; k++)
                row.Add(ParseInlines(k < cells.Count ? cells[k] : string.Empty));
            return row;
        }

        private static MdAlign AlignOf(string cell)
        {
            bool left = cell.StartsWith(":", StringComparison.Ordinal);
            bool right = cell.EndsWith(":", StringComparison.Ordinal);
            if (left && right) return MdAlign.Center;
            if (left) return MdAlign.Left;
            if (right) return MdAlign.Right;
            return MdAlign.None;
        }

        /// <summary>Moves every line number in a freshly parsed sub-block set
        /// down by the enclosing quote's start line.</summary>
        private static void ShiftLines(List<MdBlock> blocks, int delta)
        {
            foreach (var block in blocks)
            {
                block.Line += delta;

                var quote = block as MdQuote;
                if (quote != null)
                {
                    ShiftLines(quote.Blocks, delta);
                    continue;
                }

                var list = block as MdListBlock;
                if (list != null)
                {
                    foreach (var item in list.Items) item.Line += delta;
                }
            }
        }

        private static bool IsBlank(string line)
        {
            return line.Trim().Length == 0;
        }

        private static string Group(Match match, int index)
        {
            return match.Groups[index].Success ? match.Groups[index].Value : string.Empty;
        }
    }
}
