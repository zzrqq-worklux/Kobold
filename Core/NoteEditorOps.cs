using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Kobold.Core
{
    /// <summary>
    /// The pure text transforms behind the note editor's right-click quick
    /// insert menu: every op takes the source box's text and selection and
    /// returns the rewritten text with the new caret / selection, so the UI
    /// only has to call and write back. The placeholder words (the
    /// no-selection wrap text and the two table column names) are parameters -
    /// this layer never reaches for Localization.
    ///
    /// The line-scope ops build an ascending <c>(pos, removeLen, insert)</c>
    /// edit table and apply it in one pass; the caret / selection endpoints are
    /// remapped through the same table (<c>p' = p + sum of deltas whose edit is
    /// at or before p</c>, a caret inside a removed span landing after the
    /// edit). The line prefix family is kept in step with the parser's own
    /// line grammar, but spacers are space or tab and never a line terminator
    /// (<c>^#{1,6}[ \t]+</c>, <c>^- \[( |x|X)\](?:[ \t]|$)</c>,
    /// <c>^[-*](?:[ \t]|$)</c>, <c>^\d+\.(?:[ \t]|$)</c>,
    /// <c>^>(?:[ \t]|$)</c>), so a prefix match can never eat a <c>\r</c>.
    /// </summary>
    public static class NoteEditorOps
    {
        /// <summary>One replacement of the line-scope edit table: drop
        /// <see cref="RemoveLen"/> characters at <see cref="Pos"/> and put
        /// <see cref="Insert"/> in their place.</summary>
        private struct Edit
        {
            public int Pos;
            public int RemoveLen;
            public string Insert;
        }

        // The line prefix family, kept in step with MarkdownParser (T7): the
        // spacer is a space or tab (never a \r), and the marker-only forms
        // ("-", "*", ">", "1.", "- [x]") are recognized when the line has
        // nothing after them.
        private static readonly Regex HeadingPrefix = new Regex(@"^#{1,6}[ \t]+", RegexOptions.Compiled);
        private static readonly Regex TaskPrefix = new Regex(@"^- \[( |x|X)\](?:[ \t]|$)", RegexOptions.Compiled);
        private static readonly Regex BulletPrefix = new Regex(@"^[-*](?:[ \t]|$)", RegexOptions.Compiled);
        private static readonly Regex NumberPrefix = new Regex(@"^\d+\.(?:[ \t]|$)", RegexOptions.Compiled);
        private static readonly Regex QuotePrefix = new Regex(@"^>(?:[ \t]|$)", RegexOptions.Compiled);

        // List-family kinds, used so a task line is never mistaken for a
        // bullet (its "- " opening would otherwise match one).
        private const int KindNone = 0;
        private const int KindBullet = 1;
        private const int KindNumbered = 2;
        private const int KindTask = 3;

        /// <summary>The heading markers for every line the selection touches,
        /// replacing any heading already on the line. The level is clamped to
        /// 1..6, and there is no toggle-off: a heading is always set.</summary>
        public static (string Text, int SelStart, int SelLength) InsertHeading(
            string text, int selStart, int selLength, int level)
        {
            text = text ?? string.Empty;
            Clamp(text, ref selStart, ref selLength);
            if (level < 1) level = 1;
            if (level > 6) level = 6;

            string prefix = new string('#', level) + " ";
            var edits = new List<Edit>();
            foreach (int ls in AffectedLines(text, selStart, selLength))
            {
                int have = PrefixLength(HeadingPrefix, text, ls);
                edits.Add(new Edit { Pos = ls, RemoveLen = have, Insert = prefix });
            }
            return Apply(text, edits, selStart, selLength);
        }

        /// <summary>An unordered list marker: inserted at the line start,
        /// replacing a sibling list prefix, or removing the prefix when the
        /// line is already an unordered item.</summary>
        public static (string Text, int SelStart, int SelLength) InsertBullet(string text, int selStart, int selLength)
        {
            return ListOp(text, selStart, selLength, KindBullet, "- ");
        }

        /// <summary>An ordered list marker, with the same replace / toggle
        /// rules as <see cref="InsertBullet"/>.</summary>
        public static (string Text, int SelStart, int SelLength) InsertNumbered(string text, int selStart, int selLength)
        {
            return ListOp(text, selStart, selLength, KindNumbered, "1. ");
        }

        /// <summary>A task box marker, with the same replace / toggle rules as
        /// <see cref="InsertBullet"/>.</summary>
        public static (string Text, int SelStart, int SelLength) InsertTask(string text, int selStart, int selLength)
        {
            return ListOp(text, selStart, selLength, KindTask, "- [ ] ");
        }

        /// <summary>A quote marker on every line the selection touches; a line
        /// that is already a quote loses its marker.</summary>
        public static (string Text, int SelStart, int SelLength) InsertQuote(string text, int selStart, int selLength)
        {
            text = text ?? string.Empty;
            Clamp(text, ref selStart, ref selLength);

            var edits = new List<Edit>();
            foreach (int ls in AffectedLines(text, selStart, selLength))
            {
                int have = PrefixLength(QuotePrefix, text, ls);
                edits.Add(new Edit
                {
                    Pos = ls,
                    RemoveLen = have,
                    Insert = have > 0 ? string.Empty : "> "
                });
            }
            return Apply(text, edits, selStart, selLength);
        }

        /// <summary>A fenced code skeleton: a blank line is replaced by the
        /// fences, any other line gets them a line below. Either way the caret
        /// lands on the empty line between the fences.</summary>
        public static (string Text, int SelStart, int SelLength) InsertCodeBlock(string text, int selStart, int selLength)
        {
            text = text ?? string.Empty;
            string nl = NewlineOf(text);
            string block = "```" + nl + nl + "```";
            return InsertBlock(text, selStart, selLength, block, 3 + nl.Length);
        }

        /// <summary>A three-line table skeleton (header, align row, one empty
        /// body row) built from the two column placeholder words; the caret
        /// lands in the first body cell.</summary>
        public static (string Text, int SelStart, int SelLength) InsertTable(
            string text, int selStart, int selLength, string col1, string col2)
        {
            text = text ?? string.Empty;
            string nl = NewlineOf(text);
            string head = "| " + (col1 ?? string.Empty) + " | " + (col2 ?? string.Empty) + " |";
            string sep = "| --- | --- |";
            string row = "|  |  |";
            string block = head + nl + sep + nl + row;
            int caret = head.Length + nl.Length + sep.Length + nl.Length + 2;
            return InsertBlock(text, selStart, selLength, block, caret);
        }

        /// <summary>A standalone rule: a blank line is replaced by it, any
        /// other line gets it on a line below.</summary>
        public static (string Text, int SelStart, int SelLength) InsertDivider(string text, int selStart, int selLength)
        {
            return InsertBlock(text, selStart, selLength, "---", 3);
        }

        /// <summary>Bold: wraps the selection, or inserts the placeholder and
        /// selects it when there is no selection.</summary>
        public static (string Text, int SelStart, int SelLength) WrapBold(string text, int selStart, int selLength, string placeholder)
        {
            return Wrap(text, selStart, selLength, placeholder, "**", "**");
        }

        /// <summary>Italic, like <see cref="WrapBold"/> with single stars.</summary>
        public static (string Text, int SelStart, int SelLength) WrapItalic(string text, int selStart, int selLength, string placeholder)
        {
            return Wrap(text, selStart, selLength, placeholder, "*", "*");
        }

        /// <summary>Strike, like <see cref="WrapBold"/> with double tildes.</summary>
        public static (string Text, int SelStart, int SelLength) WrapStrike(string text, int selStart, int selLength, string placeholder)
        {
            return Wrap(text, selStart, selLength, placeholder, "~~", "~~");
        }

        /// <summary>Inline code, like <see cref="WrapBold"/> with backticks.</summary>
        public static (string Text, int SelStart, int SelLength) WrapInlineCode(string text, int selStart, int selLength, string placeholder)
        {
            return Wrap(text, selStart, selLength, placeholder, "`", "`");
        }

        /// <summary>A link: wraps the selection as <c>[text]()</c> with the
        /// caret inside the empty parentheses, or inserts the placeholder as
        /// <c>[placeholder]()</c> and selects it when there is no selection.</summary>
        public static (string Text, int SelStart, int SelLength) WrapLink(string text, int selStart, int selLength, string placeholder)
        {
            text = text ?? string.Empty;
            Clamp(text, ref selStart, ref selLength);
            placeholder = placeholder ?? string.Empty;

            if (selLength == 0)
            {
                string insert = "[" + placeholder + "]()";
                string expanded = text.Substring(0, selStart) + insert + text.Substring(selStart);
                return (expanded, selStart + 1, placeholder.Length);
            }

            string linked = text.Substring(0, selStart) + "["
                + text.Substring(selStart, selLength) + "]()"
                + text.Substring(selStart + selLength);
            return (linked, selStart + selLength + 3, 0);
        }

        // ---- internals ---------------------------------------------------

        private static (string, int, int) ListOp(
            string text, int selStart, int selLength, int targetKind, string targetPrefix)
        {
            text = text ?? string.Empty;
            Clamp(text, ref selStart, ref selLength);

            var edits = new List<Edit>();
            foreach (int ls in AffectedLines(text, selStart, selLength))
            {
                int kind, have;
                ListPrefixOf(text, ls, out kind, out have);

                // The same construct again removes the prefix (toggle off);
                // any other list prefix is replaced in place.
                edits.Add(kind == targetKind
                    ? new Edit { Pos = ls, RemoveLen = have, Insert = string.Empty }
                    : new Edit { Pos = ls, RemoveLen = have, Insert = targetPrefix });
            }
            return Apply(text, edits, selStart, selLength);
        }

        private static (string, int, int) Wrap(
            string text, int selStart, int selLength, string placeholder, string open, string close)
        {
            text = text ?? string.Empty;
            Clamp(text, ref selStart, ref selLength);
            placeholder = placeholder ?? string.Empty;

            if (selLength == 0)
            {
                string insert = open + placeholder + close;
                string expanded = text.Substring(0, selStart) + insert + text.Substring(selStart);
                return (expanded, selStart + open.Length, placeholder.Length);
            }

            string wrapped = text.Substring(0, selStart) + open
                + text.Substring(selStart, selLength) + close
                + text.Substring(selStart + selLength);
            return (wrapped, selStart + open.Length, selLength);
        }

        /// <summary>Inserts a block at the current line: a blank line is
        /// replaced by it, a non-empty line gets a newline then the block. The
        /// anchor and the split use the line's content end, so the document's
        /// own <c>\r\n</c> terminators survive byte-for-byte and the block is
        /// the only producer of its internal endings. The caret is
        /// <paramref name="caretInBlock"/> characters into the block; the
        /// selection's start only picks the anchor line.</summary>
        private static (string, int, int) InsertBlock(
            string text, int selStart, int selLength, string block, int caretInBlock)
        {
            text = text ?? string.Empty;
            Clamp(text, ref selStart, ref selLength);

            int ls = LineStart(text, selStart);
            int le = LineEnd(text, selStart);
            bool blank = text.Substring(ls, le - ls).Trim().Length == 0;

            string updated;
            int caret;
            if (blank)
            {
                updated = text.Substring(0, ls) + block + text.Substring(le);
                caret = ls + caretInBlock;
            }
            else
            {
                string nl = NewlineOf(text);
                updated = text.Substring(0, le) + nl + block + text.Substring(le);
                caret = le + nl.Length + caretInBlock;
            }
            return (updated, caret, 0);
        }

        /// <summary>Applies an ascending edit table in one pass and remaps the
        /// selection through it. The edits never overlap by construction (one
        /// per line, inside its own line), so no overlap guard is needed.</summary>
        private static (string, int, int) Apply(string text, List<Edit> edits, int selStart, int selLength)
        {
            edits.Sort(delegate (Edit a, Edit b) { return a.Pos.CompareTo(b.Pos); });

            var sb = new StringBuilder(text.Length + 16);
            int cursor = 0;
            foreach (var e in edits)
            {
                sb.Append(text, cursor, e.Pos - cursor);
                sb.Append(e.Insert);
                cursor = e.Pos + e.RemoveLen;
            }
            sb.Append(text, cursor, text.Length - cursor);

            int start = Map(edits, selStart);
            int end = selLength == 0 ? start : Map(edits, selStart + selLength);
            if (end < start) end = start;
            return (sb.ToString(), start, end - start);
        }

        /// <summary>Moves a position through the edits: every edit at or before
        /// it shifts it, and a position inside a removed span lands just after
        /// that edit's inserted text (a repeated line prefix clamps to the line
        /// start, never before it).</summary>
        private static int Map(List<Edit> edits, int p)
        {
            int shift = 0;
            foreach (var e in edits)
            {
                if (p < e.Pos) break;
                if (p <= e.Pos + e.RemoveLen) return e.Pos + shift + e.Insert.Length;
                shift += e.Insert.Length - e.RemoveLen;
            }
            return p + shift;
        }

        /// <summary>The start offset of every line the selection touches. A
        /// zero-length selection is just the caret's line; a selection ending
        /// exactly at a line start does not reach the line after it.</summary>
        private static List<int> AffectedLines(string text, int selStart, int selLength)
        {
            int first = LineStart(text, selStart);
            int last = selLength == 0 ? selStart : Math.Max(selStart, selStart + selLength - 1);
            int lastStart = LineStart(text, last);

            var lines = new List<int>();
            int start = first;
            lines.Add(start);
            while (start < lastStart)
            {
                int next = LineBreakEnd(text, LineEnd(text, start));
                if (next <= start || next > lastStart) break;
                start = next;
                lines.Add(start);
            }
            return lines;
        }

        /// <summary>Brings a selection into the text: a negative start or
        /// length reads as zero and an end past the text clamps to it.</summary>
        private static void Clamp(string text, ref int selStart, ref int selLength)
        {
            if (selStart < 0) selStart = 0;
            if (selStart > text.Length) selStart = text.Length;
            if (selLength < 0) selLength = 0;
            if (selLength > text.Length - selStart) selLength = text.Length - selStart;
        }

        /// <summary>The first character of the line holding <paramref name="pos"/>.
        /// A position on a CRLF terminator still belongs to the line before it;
        /// a lone CR is a break of its own.</summary>
        private static int LineStart(string text, int pos)
        {
            int i = pos < 0 ? 0 : (pos > text.Length ? text.Length : pos);
            while (i > 0)
            {
                char c = text[i - 1];
                if (c == '\n') break;
                if (c == '\r' && (i >= text.Length || text[i] != '\n')) break;
                i--;
            }
            return i;
        }

        /// <summary>The end of the line's content: the first <c>\r</c> or
        /// <c>\n</c>, or the end of the text. It never includes a terminator.</summary>
        private static int LineEnd(string text, int pos)
        {
            int i = pos < 0 ? 0 : (pos > text.Length ? text.Length : pos);
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
            return i;
        }

        /// <summary>The first character after the line break that starts at
        /// <paramref name="contentEnd"/> (consuming <c>\r\n</c>, <c>\r</c> or
        /// <c>\n</c>).</summary>
        private static int LineBreakEnd(string text, int contentEnd)
        {
            int i = contentEnd;
            if (i < text.Length && text[i] == '\r') i++;
            if (i < text.Length && text[i] == '\n') i++;
            return i;
        }

        /// <summary>The length of <paramref name="re"/> when it matches the
        /// content of the line starting at <paramref name="pos"/>, else zero.
        /// The match runs against the line content alone, so an end-of-line
        /// anchor means this line's end and no terminator can be consumed.</summary>
        private static int PrefixLength(Regex re, string text, int pos)
        {
            int end = LineEnd(text, pos);
            if (end <= pos) return 0;
            var m = re.Match(text.Substring(pos, end - pos));
            return m.Success && m.Index == 0 ? m.Length : 0;
        }

        private static void ListPrefixOf(string text, int pos, out int kind, out int length)
        {
            int task = PrefixLength(TaskPrefix, text, pos);
            if (task > 0) { kind = KindTask; length = task; return; }

            int bullet = PrefixLength(BulletPrefix, text, pos);
            if (bullet > 0) { kind = KindBullet; length = bullet; return; }

            int number = PrefixLength(NumberPrefix, text, pos);
            if (number > 0) { kind = KindNumbered; length = number; return; }

            kind = KindNone;
            length = 0;
        }

        /// <summary>The newline the document already uses, so a block insert
        /// never mixes line endings inside a note.</summary>
        private static string NewlineOf(string text)
        {
            return text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        }
    }
}
