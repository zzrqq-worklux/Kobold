using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kobold.Core
{
    /// <summary>
    /// The pure text helpers behind the note widget: the title and the plain
    /// excerpt shown on a card, the task-box rewrite used when a checkbox is
    /// clicked, and the stable display order. Rendering itself lives in the
    /// markdown parser; everything here is text in, text out.
    /// </summary>
    public static class NoteOps
    {
        private static readonly Regex HeadingPrefix = new Regex(@"^#{1,6}\s+", RegexOptions.Compiled);
        private static readonly Regex Link = new Regex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex ListPrefix = new Regex(@"^[-*]\s+", RegexOptions.Compiled);
        private static readonly Regex NumberPrefix = new Regex(@"^\d+\.\s+", RegexOptions.Compiled);
        private static readonly Regex QuotePrefix = new Regex(@"^>\s?", RegexOptions.Compiled);
        private static readonly Regex TaskPrefix = new Regex(@"^- \[(?: |x|X)\]\s?", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly char[] MarkerChars = { '*', '_', '~', '`' };

        /// <summary>
        /// The first non-blank line with its block marker (heading, task,
        /// bullet, number or quote), links and inline emphasis markers
        /// stripped. A blank note has an empty title.
        /// </summary>
        public static string TitleOf(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                line = StripLinePrefix(line);
                return StripInlineMarkers(line).Trim();
            }
            return string.Empty;
        }

        /// <summary>
        /// The plain text of the note with block markers, links and inline
        /// markers stripped: source lines join with single spaces, fence lines
        /// drop out, and anything past <paramref name="maxChars"/> is clipped
        /// with a trailing ellipsis. A non-positive budget is empty.
        /// </summary>
        public static string ExcerptOf(string text, int maxChars)
        {
            if (maxChars <= 0 || string.IsNullOrEmpty(text)) return string.Empty;

            var parts = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("```", StringComparison.Ordinal)) continue;

                line = StripLinePrefix(line);
                line = StripInlineMarkers(line).Trim();
                if (line.Length > 0) parts.Add(line);
            }

            string plain = Whitespace.Replace(string.Join(" ", parts), " ").Trim();
            if (plain.Length <= maxChars) return plain;
            return plain.Substring(0, maxChars).TrimEnd() + "…";
        }

        /// <summary>
        /// Flips the task box on the given source line - open to done, done
        /// (either spelling) to open - and returns the whole text. A line that
        /// is not a column-zero task, or a line index out of range, changes
        /// nothing; every other line and every source line ending is kept.
        /// </summary>
        public static string ToggleTaskAt(string text, int lineIndex)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var lines = text.Split('\n');
            if (lineIndex < 0 || lineIndex >= lines.Length) return text;

            var line = lines[lineIndex];
            if (!IsTaskLine(line)) return text;

            char next = line[3] == ' ' ? 'x' : ' ';
            lines[lineIndex] = line.Substring(0, 3) + next + line.Substring(4);
            return string.Join("\n", lines);
        }

        /// <summary>
        /// The display order: most recently modified first. LINQ's stable sort
        /// keeps notes modified at the same instant in their original order,
        /// and the source list is left alone.
        /// </summary>
        public static List<NoteItem> SortForDisplay(IList<NoteItem> notes)
        {
            if (notes == null) return new List<NoteItem>();
            return notes.OrderByDescending(n => n.ModifiedAt).ToList();
        }

        /// <summary>
        /// The task progress the card shows: how many boxes are done out of how
        /// many the note renders. The count follows the parser, so quoted tasks
        /// count and code-block task lines do not; a note with no task at all
        /// reports null.
        /// </summary>
        public static (int done, int total)? TaskProgressOf(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            int done = 0;
            int total = 0;
            CountTasks(MarkdownParser.Parse(text), ref done, ref total);
            return total == 0 ? ((int done, int total)?)null : (done, total);
        }

        /// <summary>Walks the block tree and tallies the task boxes, quoting
        /// down into every quote (code blocks hold no blocks, so their lines
        /// are never counted).</summary>
        private static void CountTasks(List<MdBlock> blocks, ref int done, ref int total)
        {
            foreach (var block in blocks)
            {
                var task = block as MdTaskBlock;
                if (task != null)
                {
                    total++;
                    if (task.Checked) done++;
                    continue;
                }

                var quote = block as MdQuote;
                if (quote != null) CountTasks(quote.Blocks, ref done, ref total);
            }
        }

        /// <summary>A column-zero task: "- [ ]", "- [x]" or "- [X]", with a
        /// space or the end of the line after the closing bracket.</summary>
        private static bool IsTaskLine(string line)
        {
            if (line.Length < 5) return false;
            if (line[0] != '-' || line[1] != ' ' || line[2] != '[' || line[4] != ']') return false;
            char state = line[3];
            if (state != ' ' && state != 'x' && state != 'X') return false;
            return line.Length == 5 || line[5] == ' ';
        }

        /// <summary>Strips the line-start block markers - a task box, a
        /// heading, a bullet, a number or a quote - from one already-trimmed
        /// line. The single step the title and the excerpt share so the two
        /// cannot drift: the task check runs before the bullet check so
        /// "- [ ] x" loses the whole task marker, not just "- ".</summary>
        private static string StripLinePrefix(string line)
        {
            line = TaskPrefix.Replace(line, string.Empty);
            line = HeadingPrefix.Replace(line, string.Empty);
            line = ListPrefix.Replace(line, string.Empty);
            line = NumberPrefix.Replace(line, string.Empty);
            line = QuotePrefix.Replace(line, string.Empty);
            return line;
        }

        /// <summary>Turns "[text](url)" into "text" and drops the characters
        /// that spell bold, italic, strike and inline code.</summary>
        private static string StripInlineMarkers(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;

            string text = Link.Replace(line, "$1");
            foreach (var marker in MarkerChars)
                text = text.Replace(marker.ToString(), string.Empty);
            return text;
        }
    }
}
