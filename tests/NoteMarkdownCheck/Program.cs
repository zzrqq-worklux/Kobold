using System;
using System.Collections.Generic;
using System.Linq;
using Kobold.Core;

namespace Kobold.NoteMarkdownCheck
{
    /// <summary>
    /// Behaviour checks for the note markdown helpers in NoteOps: the derived
    /// title, the plain-text excerpt, the task-box rewrite, and the stable
    /// display order. Exit code 0 = all green; 1 = failures.
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
