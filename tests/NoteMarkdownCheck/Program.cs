using System;
using System.Collections.Generic;
using System.Linq;
using Kobold.Core;

namespace Kobold.NoteMarkdownCheck
{
    /// <summary>
    /// Behaviour checks for the note markdown layer: the NoteOps helpers (the
    /// derived title, the plain-text excerpt, the task-box rewrite, the task
    /// progress and the stable display order) and the MarkdownParser block and
    /// inline parse. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/NoteMarkdownCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            RunAll();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL NOTE-MARKDOWN CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        private static void RunAll()
        {
            Titles();
            Excerpts();
            Toggles();
            Sorts();
            BlockBasics();
            Headings();
            Paragraphs();
            Lists();
            Tasks();
            Quotes();
            Fences();
            Rules();
            Tables();
            Inlines();
            Progress();
        }

        private static void Titles()
        {
            Check(NoteOps.TitleOf(null) == "", "title: null yields an empty title");
            Check(NoteOps.TitleOf("") == "", "title: empty text yields an empty title");
            Check(NoteOps.TitleOf("   \n\t  \n") == "",
                "title: whitespace-only text yields an empty title");
            Check(NoteOps.TitleOf("# 标题\n正文") == "标题", "title: an h1 heading is the title");
            Check(NoteOps.TitleOf("## 二级") == "二级", "title: an h2 heading is the title");
            Check(NoteOps.TitleOf("\n   \n标题行\n正文") == "标题行",
                "title: leading blank lines are skipped");
            Check(NoteOps.TitleOf("**粗** 与 [链](https://x)") == "粗 与 链",
                "title: inline markers are stripped");
        }

        private static void Excerpts()
        {
            Check(NoteOps.ExcerptOf("abc", 0) == "",
                "excerpt: a zero budget yields an empty excerpt");
            Check(NoteOps.ExcerptOf("abc", -5) == "",
                "excerpt: a negative budget yields an empty excerpt");
            Check(NoteOps.ExcerptOf("第一行\n第二行", 80) == "第一行 第二行",
                "excerpt: source lines join with single spaces");
            Check(NoteOps.ExcerptOf("# 标题\n- 项目\n1. 数字\n> 引用", 80) == "标题 项目 数字 引用",
                "excerpt: heading, list, numbered and quote prefixes come off");
            Check(NoteOps.ExcerptOf("前\n```\ncode\n```\n后", 80) == "前 code 后",
                "excerpt: fence lines do not appear");
            Check(NoteOps.ExcerptOf("**粗** [链](u)", 80) == "粗 链",
                "excerpt: inline markers are stripped");
            Check(NoteOps.ExcerptOf("一二三四五六七八", 5) == "一二三四五…",
                "excerpt: an over-budget excerpt is clipped and given an ellipsis");
            Check(NoteOps.ExcerptOf("一二三四五", 5) == "一二三四五",
                "excerpt: an excerpt exactly at the budget keeps no ellipsis");
            Check(NoteOps.ExcerptOf("ab cd", 3) == "ab…",
                "excerpt: clipping trims the boundary whitespace before the ellipsis");
        }

        private static void Toggles()
        {
            Check(NoteOps.ToggleTaskAt("- [ ] a", 0) == "- [x] a", "toggle: an open task checks");
            Check(NoteOps.ToggleTaskAt("- [x] b", 0) == "- [ ] b", "toggle: a done task opens");
            Check(NoteOps.ToggleTaskAt("- [X] c", 0) == "- [ ] c",
                "toggle: a capital X opens and normalizes");
            Check(NoteOps.ToggleTaskAt("- [ ]", 0) == "- [x]",
                "toggle: a bare task box with no text checks");

            var once = NoteOps.ToggleTaskAt("- [ ] z", 0);
            var twice = NoteOps.ToggleTaskAt(once, 0);
            Check(once == "- [x] z" && twice == "- [ ] z",
                "toggle: toggling twice returns the original line");

            var doneOnce = NoteOps.ToggleTaskAt("- [x] z", 0);
            var doneTwice = NoteOps.ToggleTaskAt(doneOnce, 0);
            Check(doneOnce == "- [ ] z" && doneTwice == "- [x] z",
                "toggle: a done task round-trips too");

            Check(NoteOps.ToggleTaskAt("普通", 0) == "普通", "toggle: a plain line is left alone");
            Check(NoteOps.ToggleTaskAt("  - [ ] 缩进", 0) == "  - [ ] 缩进",
                "toggle: an indented task is left alone");
            Check(NoteOps.ToggleTaskAt("- [x]nospace", 0) == "- [x]nospace",
                "toggle: a box without the required space is left alone");
            Check(NoteOps.ToggleTaskAt("- [ ] a", 5) == "- [ ] a",
                "toggle: a line index past the end changes nothing");
            Check(NoteOps.ToggleTaskAt("- [ ] a", -1) == "- [ ] a",
                "toggle: a negative line index changes nothing");

            var multi = "- [ ] a\n- [ ] b\n- [ ] c";
            Check(NoteOps.ToggleTaskAt(multi, 1) == "- [ ] a\n- [x] b\n- [ ] c",
                "toggle: only the target line flips");

            var crlf = "- [ ] a\r\n- [ ] b\r\n";
            Check(NoteOps.ToggleTaskAt(crlf, 1) == "- [ ] a\r\n- [x] b\r\n",
                "toggle: CRLF line endings survive the rewrite");
            Check(NoteOps.ToggleTaskAt(NoteOps.ToggleTaskAt(crlf, 1), 1) == crlf,
                "toggle: a CRLF note round-trips character for character");
        }

        private static void Sorts()
        {
            var early = Note("a", "early", new DateTime(2026, 10, 15, 9, 0, 0));
            var late = Note("b", "late", new DateTime(2026, 10, 15, 11, 0, 0));
            var mid = Note("c", "mid", new DateTime(2026, 10, 15, 10, 0, 0));
            var input = new List<NoteItem> { early, late, mid };

            Check(NoteOps.SortForDisplay(input).Select(n => n.Id).SequenceEqual(new[] { "b", "c", "a" }),
                "sort: the newest modification comes first");

            var tied = new List<NoteItem>
            {
                Note("t1", "one", new DateTime(2026, 10, 15, 9, 0, 0)),
                Note("t2", "two", new DateTime(2026, 10, 15, 9, 0, 0)),
                Note("t3", "three", new DateTime(2026, 10, 15, 9, 0, 0))
            };
            Check(NoteOps.SortForDisplay(tied).Select(n => n.Id).SequenceEqual(new[] { "t1", "t2", "t3" }),
                "sort: notes modified at the same instant keep their order");

            var mixed = new List<NoteItem>
            {
                Note("p", "p", new DateTime(2026, 10, 15, 9, 0, 0)),
                Note("q", "q", new DateTime(2026, 10, 15, 11, 0, 0)),
                Note("r", "r", new DateTime(2026, 10, 15, 9, 0, 0))
            };
            Check(NoteOps.SortForDisplay(mixed).Select(n => n.Id).SequenceEqual(new[] { "q", "p", "r" }),
                "sort: descending order keeps ties in their original order");

            Check(input.Select(n => n.Id).SequenceEqual(new[] { "a", "b", "c" }),
                "sort: the source list is not disturbed");
        }

        // ---- MarkdownParser block pins (T7) ------------------------------

        private static void BlockBasics()
        {
            Check(MarkdownParser.Parse("").Count == 0, "block: a blank text parses to nothing");
            Check(MarkdownParser.Parse("   \n\t\n\n").Count == 0,
                "block: a blank text parses to nothing (whitespace-only lines)");

            var one = MarkdownParser.Parse("hello");
            var para = one.Count == 1 ? one[0] as MdParagraph : null;
            Check(para != null && para.Line == 0 && para.Lines.Count == 1
                  && para.Lines[0].Count == 1 && InlineText(para.Lines[0]) == "hello",
                "block: a single line parses as one paragraph");

            var mixed = MarkdownParser.ParseInlines("**x** `y`");
            Check(mixed.Count == 3 && mixed[0] is MdStrong
                  && InlineText(((MdStrong)mixed[0]).Inlines) == "x"
                  && mixed[1] is MdText && ((MdText)mixed[1]).Text == " "
                  && mixed[2] is MdCodeSpan && ((MdCodeSpan)mixed[2]).Text == "y",
                "block: inline parsing is no longer a literal stub");
        }

        private static void Headings()
        {
            var blocks = MarkdownParser.Parse(
                "# 一级\n## 二级\n### 三级\n#### 四级\n##### 五级\n###### 六级\n## 带尾井号 ##\n#x");
            var texts = new[] { "一级", "二级", "三级", "四级", "五级", "六级" };

            bool levels = blocks.Count == 8;
            for (int i = 0; i < 6 && levels; i++)
            {
                var h = blocks[i] as MdHeading;
                levels = h != null && h.Level == i + 1 && h.Line == i && InlineText(h.Inlines) == texts[i];
            }
            Check(levels, "block: headings carry their level");

            var trailing = blocks.Count == 8 ? blocks[6] as MdHeading : null;
            Check(trailing != null && trailing.Level == 2 && trailing.Line == 6
                  && InlineText(trailing.Inlines) == "带尾井号",
                "block: headings carry their level (a trailing hash run is stripped)");

            var plain = blocks.Count == 8 ? blocks[7] as MdParagraph : null;
            Check(plain != null && plain.Line == 7 && plain.Lines.Count == 1
                  && InlineText(plain.Lines[0]) == "#x",
                "block: headings carry their level (a hash with no space is a paragraph)");

            var seven = MarkdownParser.Parse("####### 七级");
            var sevenPara = seven.Count == 1 ? seven[0] as MdParagraph : null;
            Check(sevenPara != null && sevenPara.Line == 0 && sevenPara.Lines.Count == 1
                  && InlineText(sevenPara.Lines[0]) == "####### 七级",
                "block: headings carry their level (seven hashes is a paragraph)");
        }

        private static void Paragraphs()
        {
            var three = MarkdownParser.Parse("a\nb\nc");
            var para = three.Count == 1 ? three[0] as MdParagraph : null;
            Check(para != null && para.Line == 0 && para.Lines.Count == 3
                  && InlineText(para.Lines[0]) == "a" && InlineText(para.Lines[1]) == "b"
                  && InlineText(para.Lines[2]) == "c",
                "block: a paragraph keeps its source lines");

            var split = MarkdownParser.Parse("a\n\nb\n\nc");
            var paras = split.OfType<MdParagraph>().ToList();
            Check(split.Count == 3 && paras.Count == 3 && paras[0].Lines.Count == 1
                  && paras[0].Line == 0 && paras[1].Line == 2 && paras[2].Line == 4,
                "block: a paragraph keeps its source lines (a blank line splits it)");

            var headed = MarkdownParser.Parse("a\n# b");
            var hp = headed.Count == 2 ? headed[0] as MdParagraph : null;
            var hh = headed.Count == 2 ? headed[1] as MdHeading : null;
            Check(hp != null && hp.Line == 0 && hp.Lines.Count == 1 && hh != null && hh.Line == 1
                  && InlineText(hh.Inlines) == "b",
                "block: a paragraph keeps its source lines (a heading ends it)");
        }

        private static void Lists()
        {
            var bullets = MarkdownParser.Parse("- a\n- b\n- c");
            var list = bullets.Count == 1 ? bullets[0] as MdListBlock : null;
            Check(list != null && !list.Ordered && list.Start == 1 && list.Line == 0
                  && list.Items.Count == 3 && list.Items[0].Line == 0
                  && list.Items[1].Line == 1 && list.Items[2].Line == 2
                  && InlineText(list.Items[0].Inlines) == "a" && InlineText(list.Items[2].Inlines) == "c",
                "block: bullets group into one list");

            var stars = MarkdownParser.Parse("* a\n* b");
            var starList = stars.Count == 1 ? stars[0] as MdListBlock : null;
            Check(starList != null && !starList.Ordered && starList.Items.Count == 2
                  && starList.Items[1].Line == 1 && InlineText(starList.Items[0].Inlines) == "a",
                "block: bullets group into one list (asterisks work too)");

            var mixed = MarkdownParser.Parse("- a\n1. b");
            Check(mixed.Count == 2 && mixed[0] is MdListBlock && !((MdListBlock)mixed[0]).Ordered
                  && mixed[1] is MdListBlock && ((MdListBlock)mixed[1]).Ordered,
                "block: bullets group into one list (a numbered item starts a new list)");

            var numbered = MarkdownParser.Parse("3. a\n4. b");
            var num = numbered.Count == 1 ? numbered[0] as MdListBlock : null;
            Check(num != null && num.Ordered && num.Start == 3 && num.Line == 0 && num.Items.Count == 2
                  && num.Items[0].Line == 0 && num.Items[1].Line == 1
                  && InlineText(num.Items[1].Inlines) == "b",
                "block: numbered items keep the start number");

            var nested = MarkdownParser.Parse("  - x\n  - y");
            var nestedPara = nested.Count == 1 ? nested[0] as MdParagraph : null;
            Check(nestedPara != null && nestedPara.Line == 0 && nestedPara.Lines.Count == 2
                  && InlineText(nestedPara.Lines[0]) == "  - x",
                "block: a nested-looking item stays a paragraph");
        }

        private static void Tasks()
        {
            var blocks = MarkdownParser.Parse("- [ ] open\n- [x] done\n- [X] caps\n- [x]nospace");
            var open = blocks.Count == 4 ? blocks[0] as MdTaskBlock : null;
            var done = blocks.Count == 4 ? blocks[1] as MdTaskBlock : null;
            var caps = blocks.Count == 4 ? blocks[2] as MdTaskBlock : null;
            Check(open != null && !open.Checked && open.Line == 0 && InlineText(open.Inlines) == "open"
                  && done != null && done.Checked && done.Line == 1 && InlineText(done.Inlines) == "done"
                  && caps != null && caps.Checked && caps.Line == 2 && InlineText(caps.Inlines) == "caps",
                "block: tasks parse with their state and line");

            var spurious = blocks.Count == 4 && !(blocks[3] is MdTaskBlock)
                ? blocks[3] as MdListBlock : null;
            Check(spurious != null && spurious.Items.Count == 1
                  && InlineText(spurious.Items[0].Inlines) == "[x]nospace",
                "block: tasks parse with their state and line (a box with no space is not a task)");

            var bare = MarkdownParser.Parse("- [ ]");
            var bareTask = bare.Count == 1 ? bare[0] as MdTaskBlock : null;
            Check(bareTask != null && !bareTask.Checked && bareTask.Line == 0
                  && InlineText(bareTask.Inlines) == "",
                "block: tasks parse with their state and line (a bare box)");
        }

        private static void Quotes()
        {
            var blocks = MarkdownParser.Parse("> a\n> b\n> - [ ] t");
            var quote = blocks.Count == 1 ? blocks[0] as MdQuote : null;
            var inner = quote != null && quote.Blocks.Count == 2 ? quote.Blocks[0] as MdParagraph : null;
            var task = quote != null && quote.Blocks.Count == 2 ? quote.Blocks[1] as MdTaskBlock : null;
            Check(quote != null && quote.Line == 0 && quote.Blocks.Count == 2
                  && inner != null && inner.Line == 0 && inner.Lines.Count == 2
                  && InlineText(inner.Lines[1]) == "b"
                  && task != null && task.Line == 2 && !task.Checked,
                "block: a quote groups its lines");

            var shifted = MarkdownParser.Parse("intro\n> q1\n> q2");
            var sq = shifted.Count == 2 ? shifted[1] as MdQuote : null;
            var sp = sq != null && sq.Blocks.Count == 1 ? sq.Blocks[0] as MdParagraph : null;
            Check(sq != null && sq.Line == 1 && sp != null && sp.Line == 1 && sp.Lines.Count == 2,
                "block: a quote groups its lines (inner lines are absolute)");

            // Quotes are single level: ">>" is not a quote marker (spec §4).
            var doubleGt = MarkdownParser.Parse(">> a\n");
            var dg = doubleGt.Count == 1 ? doubleGt[0] as MdParagraph : null;
            Check(dg != null && dg.Line == 0 && dg.Lines.Count == 1
                  && InlineText(dg.Lines[0]) == ">> a",
                "block: a double-gt line stays literal text");

            var spaced = MarkdownParser.Parse("> > a\n");
            var spacedQuote = spaced.Count == 1 ? spaced[0] as MdQuote : null;
            var spacedInner = spacedQuote != null && spacedQuote.Blocks.Count == 1
                ? spacedQuote.Blocks[0] as MdParagraph : null;
            Check(spacedQuote != null && spacedQuote.Line == 0 && spacedQuote.Blocks.Count == 1
                  && spacedInner != null && spacedInner.Line == 0
                  && spacedInner.Lines.Count == 1 && InlineText(spacedInner.Lines[0]) == "> a",
                "block: quotes never nest");
        }

        private static void Fences()
        {
            var closed = MarkdownParser.Parse("```\ncode\nmore\n```");
            var cb = closed.Count == 1 ? closed[0] as MdCodeBlock : null;
            Check(cb != null && cb.Line == 0 && cb.Info == "" && cb.Text == "code\nmore",
                "block: a closed fence collects its lines");

            var info = MarkdownParser.Parse("```csharp\nx = 1;\n```");
            var ib = info.Count == 1 ? info[0] as MdCodeBlock : null;
            Check(ib != null && ib.Info == "csharp" && ib.Text == "x = 1;",
                "block: the info string is kept");

            var open = MarkdownParser.Parse("```\ncode\nafter");
            var ob = open.Count == 1 ? open[0] as MdCodeBlock : null;
            Check(ob != null && ob.Text == "code\nafter",
                "block: an unclosed fence runs to the end");

            var trailing = MarkdownParser.Parse("```\ncode\n```\nafter");
            Check(trailing.Count == 2 && trailing[0] is MdCodeBlock && trailing[1] is MdParagraph
                  && ((MdParagraph)trailing[1]).Line == 3,
                "block: a closed fence ends where it says");
        }

        private static void Rules()
        {
            var single = MarkdownParser.Parse("---");
            var tb = single.Count == 1 ? single[0] as MdThematicBreak : null;
            Check(tb != null && tb.Line == 0 && !(single[0] is MdTable),
                "block: a standalone rule is a break, not a table");

            var others = MarkdownParser.Parse("***\n\n___");
            Check(others.Count == 2 && others[0] is MdThematicBreak && others[1] is MdThematicBreak
                  && others[1].Line == 2,
                "block: a standalone rule is a break, not a table (stars and underscores)");

            var after = MarkdownParser.Parse("para\n---\nafter");
            Check(after.Count == 3 && after[0] is MdParagraph && after[1] is MdThematicBreak
                  && after[1].Line == 1 && after[2] is MdParagraph
                  && ((MdParagraph)after[2]).Line == 2,
                "block: the line after a paragraph rule");
        }

        private static void Tables()
        {
            var blocks = MarkdownParser.Parse(
                "| A | B | C |\n|:--|:-:|--:|\n| 1 | 2 | 3 |\n| 4 |\n| 5 | 6 | 7 | 8 |");
            var table = blocks.Count == 1 ? blocks[0] as MdTable : null;
            bool align = table != null && table.Align != null && table.Align.Length == 3
                && table.Align[0] == MdAlign.Left && table.Align[1] == MdAlign.Center
                && table.Align[2] == MdAlign.Right;
            Check(table != null && table.Line == 0 && align && table.Header.Count == 3
                  && InlineText(table.Header[0]) == "A" && InlineText(table.Header[2]) == "C"
                  && table.Rows.Count == 3 && InlineText(table.Rows[0][1]) == "2",
                "block: a table reads the header, the separator and the rows");

            bool padded = table != null && table.Rows.Count == 3;
            Check(padded && table.Rows[1].Count == 3 && InlineText(table.Rows[1][0]) == "4"
                  && InlineText(table.Rows[1][1]) == "" && table.Rows[2].Count == 3
                  && InlineText(table.Rows[2][2]) == "7",
                "block: the table pads short rows and drops extra cells");

            var pipeless = MarkdownParser.Parse("a | b\nc | d");
            var pp = pipeless.Count == 1 ? pipeless[0] as MdParagraph : null;
            Check(pp != null && pp.Lines.Count == 2 && InlineText(pp.Lines[1]) == "c | d",
                "block: a pipe line without a separator stays a paragraph");

            var escaped = MarkdownParser.Parse("| a \\| b | c |\n|---|---|");
            var et = escaped.Count == 1 ? escaped[0] as MdTable : null;
            Check(et != null && et.Header.Count == 2 && InlineText(et.Header[0]) == "a | b"
                  && InlineText(et.Header[1]) == "c",
                "block: an escaped pipe does not split a cell");

            var bare = MarkdownParser.Parse("A | B\n--- | ---\n1 | 2");
            var bt = bare.Count == 1 ? bare[0] as MdTable : null;
            Check(bt != null && bt.Header.Count == 2 && bt.Rows.Count == 1
                  && InlineText(bt.Rows[0][1]) == "2",
                "block: a table needs no outer pipes");
        }

        // ---- MarkdownParser inline pins (T8) -----------------------------

        private static void Inlines()
        {
            // Code spans win over emphasis: the backticks' content is verbatim.
            var code = MarkdownParser.ParseInlines("`a **b** c`");
            Check(code.Count == 1 && code[0] is MdCodeSpan
                  && ((MdCodeSpan)code[0]).Text == "a **b** c",
                "inline: code spans win over emphasis");

            var openTick = MarkdownParser.ParseInlines("`a");
            Check(openTick.Count == 1 && openTick[0] is MdText
                  && ((MdText)openTick[0]).Text == "`a",
                "inline: an unclosed backtick stays literal");

            var strong = MarkdownParser.ParseInlines("**a**");
            var strongUnder = MarkdownParser.ParseInlines("__a__");
            var em = MarkdownParser.ParseInlines("*b*");
            var emUnder = MarkdownParser.ParseInlines("_b_");
            Check(strong.Count == 1 && strong[0] is MdStrong
                  && InlineText(((MdStrong)strong[0]).Inlines) == "a"
                  && strongUnder.Count == 1 && strongUnder[0] is MdStrong
                  && InlineText(((MdStrong)strongUnder[0]).Inlines) == "a"
                  && em.Count == 1 && em[0] is MdEm && InlineText(((MdEm)em[0]).Inlines) == "b"
                  && emUnder.Count == 1 && emUnder[0] is MdEm
                  && InlineText(((MdEm)emUnder[0]).Inlines) == "b",
                "inline: strong and em pair simply");

            var nested = MarkdownParser.ParseInlines("**a *b* c**");
            var nestedStrong = nested.Count == 1 ? nested[0] as MdStrong : null;
            Check(nestedStrong != null && nestedStrong.Inlines.Count == 3
                  && InlineText(new List<MdInline> { nestedStrong.Inlines[0] }) == "a "
                  && nestedStrong.Inlines[1] is MdEm
                  && InlineText(((MdEm)nestedStrong.Inlines[1]).Inlines) == "b"
                  && nestedStrong.Inlines[2] is MdText
                  && ((MdText)nestedStrong.Inlines[2]).Text == " c",
                "inline: strong and em pair simply (em nests inside strong)");

            var openStrong = MarkdownParser.ParseInlines("**a");
            Check(openStrong.Count == 1 && openStrong[0] is MdText
                  && ((MdText)openStrong[0]).Text == "**a",
                "inline: an unclosed strong marker stays literal");

            var strike = MarkdownParser.ParseInlines("~~x~~");
            Check(strike.Count == 1 && strike[0] is MdStrike
                  && InlineText(((MdStrike)strike[0]).Inlines) == "x",
                "inline: strike uses the double tilde");

            var singleTilde = MarkdownParser.ParseInlines("~x~");
            Check(singleTilde.Count == 1 && singleTilde[0] is MdText
                  && ((MdText)singleTilde[0]).Text == "~x~",
                "inline: a single tilde stays literal");

            var link = MarkdownParser.ParseInlines("[t](https://x)");
            Check(link.Count == 1 && link[0] is MdLink && ((MdLink)link[0]).Text == "t"
                  && ((MdLink)link[0]).Url == "https://x",
                "inline: links carry text and url");

            var httpLink = MarkdownParser.ParseInlines("[t](http://x)");
            Check(httpLink.Count == 1 && httpLink[0] is MdLink
                  && ((MdLink)httpLink[0]).Url == "http://x",
                "inline: http links carry text and url");

            var ftpLink = MarkdownParser.ParseInlines("[t](ftp://x)");
            Check(ftpLink.Count == 1 && ftpLink[0] is MdText
                  && ((MdText)ftpLink[0]).Text == "[t](ftp://x)",
                "inline: a non-web link stays literal");

            var openLink = MarkdownParser.ParseInlines("[t](");
            Check(openLink.Count == 1 && openLink[0] is MdText
                  && ((MdText)openLink[0]).Text == "[t](",
                "inline: an unclosed link stays literal");

            var escaped = MarkdownParser.ParseInlines("\\*x\\*");
            Check(escaped.Count == 1 && escaped[0] is MdText
                  && ((MdText)escaped[0]).Text == "*x*",
                "inline: backslash escapes the markers");

            var escapedBackslash = MarkdownParser.ParseInlines("\\\\");
            Check(escapedBackslash.Count == 1 && escapedBackslash[0] is MdText
                  && ((MdText)escapedBackslash[0]).Text == "\\",
                "inline: a backslash escapes itself");

            var escapedPipe = MarkdownParser.ParseInlines("\\|");
            Check(escapedPipe.Count == 1 && escapedPipe[0] is MdText
                  && ((MdText)escapedPipe[0]).Text == "|",
                "inline: a backslash escapes a pipe");

            var table = MarkdownParser.Parse("| **a** | b |\n|---|---|");
            var tt = table.Count == 1 ? table[0] as MdTable : null;
            Check(tt != null && tt.Header.Count == 2 && tt.Header[0].Count == 1
                  && tt.Header[0][0] is MdStrong
                  && InlineText(((MdStrong)tt.Header[0][0]).Inlines) == "a"
                  && InlineText(tt.Header[1]) == "b",
                "inline: table cells parse their inlines");

            var quote = MarkdownParser.Parse("> **a**");
            var qt = quote.Count == 1 ? quote[0] as MdQuote : null;
            var qp = qt != null && qt.Blocks.Count == 1 ? qt.Blocks[0] as MdParagraph : null;
            Check(qp != null && qp.Lines.Count == 1 && qp.Lines[0].Count == 1
                  && qp.Lines[0][0] is MdStrong
                  && InlineText(((MdStrong)qp.Lines[0][0]).Inlines) == "a",
                "inline: quotes re-parse their content");
        }

        // ---- NoteOps.TaskProgressOf pins (T8) ----------------------------

        private static void Progress()
        {
            Check(ProgressIs(NoteOps.TaskProgressOf("- [x] a\n- [ ] b\n- [ ] c"), 1, 3),
                "progress: counts open and done tasks");

            Check(NoteOps.TaskProgressOf("just a plain line\nand another") == null,
                "progress: a day with no tasks reports null");

            Check(NoteOps.TaskProgressOf("```\n- [ ] x\n```") == null,
                "progress: code-block task lines are not counted");

            Check(ProgressIs(NoteOps.TaskProgressOf("> - [x] a\n> - [ ] b"), 1, 2),
                "progress: quoted tasks are counted");
        }

        private static bool ProgressIs((int done, int total)? progress, int done, int total)
        {
            return progress.HasValue && progress.Value.done == done
                && progress.Value.total == total;
        }

        /// <summary>The literal text of an inline run (non-text inlines read as
        /// "?", so callers assert their type separately).</summary>
        private static string InlineText(List<MdInline> inlines)
        {
            if (inlines == null) return null;
            return string.Concat(inlines.Select(i => i is MdText ? ((MdText)i).Text : "?"));
        }

        private static NoteItem Note(string id, string text, DateTime modified)
        {
            return new NoteItem
            {
                Id = id,
                Text = text,
                CreatedAt = modified.AddHours(-1),
                ModifiedAt = modified
            };
        }
    }
}
