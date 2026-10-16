using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// The pure line-range helpers behind block-level note editing: which
    /// source lines make up each top-level block, which block a line belongs
    /// to, and how to read or replace a run of lines. Everything here is text
    /// in, text out.
    ///
    /// A range is [Start, End) over the source lines in natural order and runs
    /// from the block's own start line to the next block's start line. A block
    /// therefore owns the blank run between it and the next block, and the last
    /// range ends at the line count, so the trailing newline belongs to the
    /// last block. Leading blank lines belong to no range. The ranges are
    /// derived from the top-level blocks of <see cref="MarkdownParser.Parse"/>,
    /// so they line up with the rendered blocks one to one.
    /// </summary>
    public static class NoteBlockRanges
    {
        /// <summary>
        /// The line range of every top-level block, in order. Each End is the
        /// next block's start line - the line count for the last block - so the
        /// ranges tile the text from the first block to its end. A text with no
        /// block yields no ranges.
        /// </summary>
        public static List<(int Start, int End)> RangesOf(string text)
        {
            var ranges = new List<(int Start, int End)>();
            var blocks = MarkdownParser.Parse(text);
            if (blocks.Count == 0) return ranges;

            int count = LineCount(text);
            for (int i = 0; i < blocks.Count; i++)
            {
                int start = blocks[i].Line;
                int end = i + 1 < blocks.Count ? blocks[i + 1].Line : count;
                ranges.Add((start, end));
            }
            return ranges;
        }

        /// <summary>
        /// The index of the range the line belongs to: the last range whose
        /// start line is at or above it, so a line in the blank run between two
        /// blocks reads as the block above it and a line past the last block as
        /// that block. A line before the first block, or a text with no range
        /// at all, reads as -1.
        /// </summary>
        public static int RangeIndexAtLine(string text, int line)
        {
            var ranges = RangesOf(text);
            int index = -1;
            for (int i = 0; i < ranges.Count && ranges[i].Start <= line; i++) index = i;
            return index;
        }

        /// <summary>
        /// The lines [start, end) joined with LF, every line's own trailing CR
        /// stripped (one at most), so a CRLF source reads back without its
        /// carriage returns. Bounds clamp to the text; an empty range reads as
        /// empty.
        /// </summary>
        public static string ExtractLines(string text, int start, int end)
        {
            var lines = SplitRaw(text);
            Clamp(lines.Length, ref start, ref end);

            var slice = new List<string>(end - start);
            for (int i = start; i < end; i++) slice.Add(StripCarriageReturn(lines[i]));
            return string.Join("\n", slice);
        }

        /// <summary>
        /// Replaces the lines [start, end) with <paramref name="replacement"/>
        /// and returns the whole text: the lines outside the region are kept
        /// verbatim (their CRLF terminators untouched) and the result joins
        /// with LF. Bounds clamp to the text, an end before the start reading
        /// as an empty region at the start line; an empty replacement removes
        /// the region rather than writing an empty line.
        /// </summary>
        public static string ReplaceLines(string text, int start, int end, string replacement)
        {
            var lines = SplitRaw(text);
            Clamp(lines.Length, ref start, ref end);

            var result = new List<string>(lines.Length);
            for (int i = 0; i < start; i++) result.Add(lines[i]);
            if (!string.IsNullOrEmpty(replacement))
            {
                foreach (var raw in replacement.Split('\n'))
                    result.Add(StripCarriageReturn(raw));
            }
            for (int i = end; i < lines.Length; i++) result.Add(lines[i]);
            return string.Join("\n", result);
        }

        /// <summary>
        /// The source line count: 0 for a null or empty text, otherwise one per
        /// LF plus the last line, so "a\n" counts its trailing empty line.
        /// </summary>
        public static int LineCount(string text)
        {
            return SplitRaw(text).Length;
        }

        /// <summary>The raw source lines: LF separated, each keeping a trailing
        /// CR when the source wrote CRLF. An empty (or null) text has no line
        /// at all, which is what makes the empty text count as zero.</summary>
        private static string[] SplitRaw(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            return text.Split('\n');
        }

        /// <summary>Pulls a line range into the text: a start before the text
        /// moves to the top, a start past it to the end, an end before the
        /// start collapses to it and an end past the text stops at the end.
        /// </summary>
        private static void Clamp(int count, ref int start, ref int end)
        {
            if (start < 0) start = 0;
            if (start > count) start = count;
            if (end < start) end = start;
            if (end > count) end = count;
        }

        /// <summary>Drops one trailing carriage return, the CR half of a CRLF
        /// terminator; a line with no CR comes back as it is.</summary>
        private static string StripCarriageReturn(string line)
        {
            if (line.Length > 0 && line[line.Length - 1] == '\r')
                return line.Substring(0, line.Length - 1);
            return line;
        }
    }
}
