using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kobold.Controls;
using Kobold.Core;
using Localization = Kobold.Core.Localization;

namespace Kobold.GroupUiSpike
{
    /// <summary>
    /// T0 spike harness: proves the real TodoCalendarView + NotePanelView can be
    /// re-hosted in one group window with bare top tabs, keep their state across
    /// tab switches, follow a group resize, and that the group window can
    /// re-dispatch the window-level behaviour the widgets used to own.
    ///
    /// Modes:
    ///   --check            automated acceptance A1..A11, exit 0 = green
    ///   --ui               interactive group window + control window
    ///   --screenshot DIR   renders DIR\spike-todo.png and DIR\spike-note.png
    /// </summary>
    internal static class Program
    {
        private const string TodoColor = "#6A9BCC";
        private const string NoteColor = "#788C5D";

        private static string _sandbox;
        private static string _realConfigPath;
        private static string _realBackupPath;
        private static string _realConfigHashBefore;
        private static string _realBackupHashBefore;

        [STAThread]
        private static int Main(string[] args)
        {
            // WinExe has no console of its own: when `dotnet run` redirects the
            // streams, attach a UTF-8 writer to them so the PASS/FAIL lines
            // survive; ignore the no-console case.
            try
            {
                var utf8 = new UTF8Encoding(false);
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            }
            catch (Exception)
            {
                // no usable standard handles (double-clicked): output is lost
            }

            string mode = args.Length > 0 ? args[0] : "--check";
            string shotDir = args.Length > 1 ? args[1] : null;

            // A10 baseline: the worktree state before the spike touches anything.
            string[] gitBaseline = GitStatus();

            // A1: sandbox the singleton and snapshot the real config bytes
            // before anything can load or create the manager.
            _sandbox = Path.Combine(Path.GetTempPath(), "kobold-groupuispike");
            if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true);
            Directory.CreateDirectory(_sandbox);
            _realConfigPath = Utils.GetConfigPath();
            _realBackupPath = _realConfigPath + ".backup";
            _realConfigHashBefore = HashOrAbsent(_realConfigPath);
            _realBackupHashBefore = HashOrAbsent(_realBackupPath);
            WidgetManager.OverrideConfigPathsForChecks(
                Path.Combine(_sandbox, "config.json"),
                Path.Combine(_sandbox, "config.json.backup"));

            try
            {
                // The production App cannot be used here: its OnStartup runs on
                // the first dispatcher pump and, with a live Kobold instance
                // holding the single-instance mutex, signals that app and shuts
                // this process down. A plain Application carries the same
                // resource tree (Themes/Controls.xaml) without that lifecycle.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Kobold;component/Themes/Controls.xaml",
                        UriKind.Absolute)
                });
                WidgetManager.Instance.ToString();  // theme tokens + language from the sandbox
                WidgetManager.Instance.Config.Language = "zh";
                Localization.SetLanguage("zh");

                var input = BuildInput();

                if (mode == "--ui") return RunUi(input);
                if (mode == "--screenshot") return RunScreenshot(input, shotDir);
                return RunChecks(input, gitBaseline);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 1;
            }
        }

        #region Bootstrap data

        private static SpikeInput BuildInput()
        {
            DateTime today = DateTime.Today;
            DateTime now = DateTime.Now;

            var todo = new TodoData
            {
                Name = "待办",
                Color = TodoColor,
                SelectedDate = today
            };
            todo.Items.Add(new TodoItem
            {
                Id = "todo-1",
                Text = "写周报：整理本周数据",
                Date = today,
                Children =
                {
                    new SubTodoItem { Id = "todo-1-1", Text = "导出报表", Done = false }
                }
            });
            todo.Items.Add(new TodoItem
            {
                Id = "todo-2",
                Text = "回复邮件",
                Date = today,
                Done = true,
                CompletedDate = today
            });

            var note = new NoteData
            {
                Name = "灵感与草稿",
                Color = NoteColor
            };
            note.Notes.Add(new NoteItem
            {
                Id = "note-1",
                Text = "# 周会要点\n\n- 确认裸标签方案\n- [x] 记录 View 依赖\n- 组 resize 不跳窗\n",
                CreatedAt = now.AddDays(-1),
                ModifiedAt = now
            });
            note.Notes.Add(new NoteItem
            {
                Id = "note-2",
                Text = "## 随手记\n\n`ChromeTitle` 是 note 的上下文标题来源。\n",
                CreatedAt = now.AddDays(-3),
                ModifiedAt = now.AddDays(-2)
            });

            return new SpikeInput
            {
                Todo = todo,
                Note = note,
                ContentWidth = 758,
                ContentHeight = 466
            };
        }

        /// <summary>
        /// Discovers the group window by name so the harness compiles in the RED
        /// state (the window does not exist yet) and fails at runtime instead.
        /// </summary>
        private static ISpikeGroupWindow CreateGroup(SpikeInput input)
        {
            Type type = typeof(Program).Assembly.GetType("Kobold.GroupUiSpike.GroupSpikeWindow", false);
            if (type == null)
                throw new InvalidOperationException(
                    "group window type 'Kobold.GroupUiSpike.GroupSpikeWindow' not found (T0 RED state: the group window is not implemented)");
            MethodInfo factory = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            if (factory == null)
                throw new InvalidOperationException("GroupSpikeWindow.Create(SpikeInput) not found");
            ISpikeGroupWindow group = null;
            try
            {
                group = factory.Invoke(null, new object[] { input }) as ISpikeGroupWindow;
            }
            catch (TargetInvocationException ex)
            {
                // Surface the window's own failure, not the reflection wrapper.
                throw ex.InnerException ?? ex;
            }
            if (group == null)
                throw new InvalidOperationException("GroupSpikeWindow.Create returned no ISpikeGroupWindow");
            return group;
        }

        #endregion

        #region --check

        private static int RunChecks(SpikeInput input, string[] gitBaseline)
        {
            var checks = new Checks();
            Console.WriteLine("mode: --check");
            Console.WriteLine("sandbox: " + _sandbox);

            ISpikeGroupWindow group = null;
            string groupError = null;
            try
            {
                group = CreateGroup(input);
            }
            catch (Exception ex)
            {
                groupError = ex.Message;
            }
            Console.WriteLine("group window: " + (group == null ? "MISSING (" + groupError + ")" : "created"));
            Console.WriteLine("app resources: current=" + (Application.Current != null) +
                              ", Kobold.Style.LauncherItem=" +
                              (Application.Current != null &&
                               Application.Current.TryFindResource("Kobold.Style.LauncherItem") != null ? "found" : "MISSING"));

            try
            {
                if (group != null)
                {
                    ShowForLayout(group);
                    Pump();
                }

                RunA2(checks, group, groupError);
                RunA3(checks, group, groupError);
                RunA4(checks, group, groupError);
                RunA5(checks, group, groupError);
                RunA6(checks, group, groupError);
                RunA7(checks, group, groupError);
                RunA8(checks, group, groupError);
                RunA9(checks, group, groupError);
                RunA11(checks, group, groupError);
                RunA12(checks, group, groupError);
                RunA13(checks, group, groupError);
                RunA14(checks, group, groupError);
                RunA15(checks, group, groupError);
            }
            finally
            {
                if (group != null)
                {
                    try { group.Window.Close(); } catch { /* spike teardown */ }
                    Pump();
                    try { group.TodoHost.Close(); } catch { }
                    try { group.NoteHost.Close(); } catch { }
                    Pump();
                }
            }

            RunA1(checks);
            RunA10(checks, gitBaseline);

            Console.WriteLine(checks.Failures == 0
                ? "RESULT: GREEN (" + checks.Passed + " checks passed)"
                : "RESULT: RED (" + checks.Failures + " failed)");
            return checks.Failures == 0 ? 0 : 1;
        }

        private static void RequireGroup(ISpikeGroupWindow group, string error)
        {
            if (group == null)
                throw new InvalidOperationException("group window missing: " + error);
        }

        /// <summary>A1: the sandbox is real and the user's config bytes are intact.</summary>
        private static void RunA1(Checks checks)
        {
            checks.Run("A1", "bootstrap: sandbox config, real config untouched", () =>
            {
                string sandboxConfig = Path.Combine(_sandbox, "config.json");
                Checks.Require(!string.Equals(sandboxConfig, _realConfigPath, StringComparison.OrdinalIgnoreCase),
                    "the sandbox and real config paths are the same");

                // Prove the redirect: a forced synchronous save must land in the
                // sandbox (the same pin XamlLoadCheck uses).
                WidgetManager.Instance.Config.Language = "zh";
                WidgetManager.Instance.SaveConfigNow();
                Checks.Require(File.Exists(sandboxConfig), "the forced save did not land in the sandbox config");

                string configNow = HashOrAbsent(_realConfigPath);
                string backupNow = HashOrAbsent(_realBackupPath);
                Checks.Require(configNow == _realConfigHashBefore,
                    "the real config changed: " + _realConfigHashBefore + " -> " + configNow);
                Checks.Require(backupNow == _realBackupHashBefore,
                    "the real config backup changed: " + _realBackupHashBefore + " -> " + backupNow);
                Checks.Note("real config " + _realConfigPath + " = " + configNow);
                Checks.Note("real backup = " + backupNow);
            });
        }

        /// <summary>A2: window + rows + two tabs + the two real views in two slots.</summary>
        private static void RunA2(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A2", "structure: 32px tabs, 40px title, two real views in two slots", () =>
            {
                RequireGroup(group, error);
                Window window = group.Window;
                Checks.Require(window != null, "no window");
                Checks.Require(Math.Abs(window.Width - 760) < 0.5 && Math.Abs(window.Height - 540) < 0.5,
                    "default size is " + window.Width + "x" + window.Height + ", wanted 760x540");
                Checks.Require(Math.Abs(group.TabRow.ActualHeight - 32) <= 1,
                    "tab row height " + group.TabRow.ActualHeight + ", wanted 32");
                Checks.Require(Math.Abs(group.TitleRow.ActualHeight - 40) <= 1,
                    "title row height " + group.TitleRow.ActualHeight + ", wanted 40");

                var content = group.ContentArea as Panel;
                Checks.Require(content != null, "the content area is not a Panel");
                Checks.Require(content.Children.Count == 2, "content area holds " + content.Children.Count + " slots");

                Checks.Require(group.TodoTab != null && group.NoteTab != null, "two tabs were not both built");
                Checks.Require(group.TodoView is TodoCalendarView, "the todo slot does not host a TodoCalendarView");
                Checks.Require(group.NoteView is NotePanelView, "the note slot does not host a NotePanelView");
                Checks.Require(ReferenceEquals(group.TodoSlot.Content, group.TodoView),
                    "the todo slot hosts something else");
                Checks.Require(ReferenceEquals(group.NoteSlot.Content, group.NoteView),
                    "the note slot hosts something else");

                // The hidden hosts must have given the views up.
                var todoChrome = group.TodoHost.FindName("TodoChrome") as WidgetChrome;
                var noteChrome = group.NoteHost.FindName("NoteChrome") as WidgetChrome;
                Checks.Require(todoChrome != null, "the todo host chrome was not found");
                Checks.Require(noteChrome != null, "the note host chrome was not found");
                Checks.Require(todoChrome.ContentHost.Content == null,
                    "the todo host still owns its view");
                Checks.Require(noteChrome.ContentHost.Content == null,
                    "the note host still owns its view");
                Checks.Require(!group.TodoHost.IsVisible && !group.NoteHost.IsVisible,
                    "a hidden host became visible");

                Checks.Note("tab row " + group.TabRow.ActualHeight + ", title row " + group.TitleRow.ActualHeight);
                Checks.Note("slots: todo -> TodoCalendarView, note -> NotePanelView (same instances as the hosts created)");
            });
        }

        /// <summary>A3: bare tabs - no pill, no marker, exactly one 2px member-coloured underline.</summary>
        private static void RunA3(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A3", "bare tabs: transparent roots, one 2px member-coloured underline", () =>
            {
                RequireGroup(group, error);
                group.SetActive(0);
                Pump();

                foreach (FrameworkElement tab in new FrameworkElement[] { group.TodoTab, group.NoteTab })
                {
                    Brush rootFill = BackgroundOf(tab);
                    Checks.Require(IsTransparent(rootFill),
                        "a tab root carries a visible fill (" + tab.Name + ")");

                    foreach (DependencyObject node in Descendants(tab))
                    {
                        if (node is System.Windows.Shapes.Path)
                            throw new InvalidOperationException("a tab contains a Path/triangle marker");
                        var border = node as Border;
                        if (border == null) continue;
                        if (ReferenceEquals(border, group.TodoUnderline) || ReferenceEquals(border, group.NoteUnderline))
                            continue; // the underline is the one allowed painted element
                        if (border.Visibility == Visibility.Visible && !IsTransparent(border.Background))
                            throw new InvalidOperationException("a tab carries a visible pill/fill");
                    }
                }

                var underlines = Descendants(group.TabRow)
                    .OfType<Border>()
                    .Where(b => Math.Abs(b.Height - 2) < 0.01)
                    .ToList();
                Checks.Require(underlines.Count == 2,
                    "expected two underline borders (one per tab), found " + underlines.Count);

                var visible = underlines.Where(b => b.Visibility == Visibility.Visible).ToList();
                Checks.Require(visible.Count == 1, "visible underlines: " + visible.Count + ", wanted exactly 1");
                Border active = visible[0];
                Checks.Require(ReferenceEquals(active, group.TodoUnderline),
                    "the active underline is not the todo tab's while todo is active");
                Checks.Require(Math.Abs(active.Height - 2) < 0.01, "the underline height is " + active.Height);
                Checks.Require(active.IsVisible, "the active underline is not visible on screen");

                var brush = active.Background as SolidColorBrush;
                Checks.Require(brush != null, "the active underline has no solid brush");
                Checks.Require(brush.Color == Utils.HexToColor(TodoColor),
                    "underline colour " + brush.Color + ", wanted " + Utils.HexToColor(TodoColor));

                // 2px underline sits at the bottom edge and spans the tab width.
                Point origin = active.TranslatePoint(new Point(0, 0), group.TabRow);
                Checks.Require(Math.Abs((origin.Y + active.ActualHeight) - group.TabRow.ActualHeight) <= 1.5,
                    "the underline is not flush with the tab row bottom (bottom at " +
                    (origin.Y + active.ActualHeight) + " of " + group.TabRow.ActualHeight + ")");
                Checks.Require(Math.Abs(active.ActualWidth - group.TodoTab.ActualWidth) <= 1.0,
                    "the underline width " + active.ActualWidth + " does not match the tab " + group.TodoTab.ActualWidth);

                TextBlock todoLabel = FindTextBlock(group.TodoTab, Localization.Get("UI_WidgetKindTodo"));
                TextBlock noteLabel = FindTextBlock(group.NoteTab, Localization.Get("UI_WidgetKindNote"));
                Checks.Require(todoLabel != null && noteLabel != null, "the tab labels were not found");
                Checks.Require(todoLabel.FontWeight == FontWeights.SemiBold, "the active label is not semi-bold");
                Checks.Require(noteLabel.FontWeight != FontWeights.SemiBold, "the inactive label is semi-bold");
                Checks.Note("underline: 2px, tab width, " + brush.Color + ", bottom-aligned; labels 待办/笔记");

                // The note tab's underline appears (and the todo one hides) when
                // the note tab becomes active - still exactly one visible.
                group.SetActive(1);
                Pump();
                var noteVisible = Descendants(group.TabRow).OfType<Border>()
                    .Where(b => Math.Abs(b.Height - 2) < 0.01 && b.Visibility == Visibility.Visible)
                    .ToList();
                Checks.Require(noteVisible.Count == 1 && ReferenceEquals(noteVisible[0], group.NoteUnderline),
                    "switching to the note tab did not move the single underline");
                var noteBrush = group.NoteUnderline.Background as SolidColorBrush;
                Checks.Require(noteBrush != null && noteBrush.Color == Utils.HexToColor(NoteColor),
                    "the note underline colour is not the note member colour");
                group.SetActive(0);
                Pump();
            });
        }

        /// <summary>A4: clicking tabs swaps the visible view without rebuilding or resizing.</summary>
        private static void RunA4(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A4", "switching: clicks swap slots, 10 switches keep the same instances and size", () =>
            {
                RequireGroup(group, error);
                TodoCalendarView todo = group.TodoView;
                NotePanelView note = group.NoteView;
                double width = group.Window.Width;
                double height = group.Window.Height;

                group.SetActive(0);
                Pump();
                Checks.Require(group.TodoSlot.Visibility == Visibility.Visible &&
                               group.NoteSlot.Visibility == Visibility.Collapsed,
                    "todo active: slot visibility is wrong");

                ClickTab(group.NoteTab);
                Pump();
                Checks.Require(!group.IsActiveTodo, "the note tab click did not activate the note view");
                Checks.Require(group.NoteSlot.Visibility == Visibility.Visible &&
                               group.TodoSlot.Visibility == Visibility.Collapsed,
                    "note active: slot visibility is wrong");

                ClickTab(group.TodoTab);
                Pump();
                Checks.Require(group.IsActiveTodo, "the todo tab click did not activate the todo view");
                Checks.Require(group.TodoSlot.Visibility == Visibility.Visible &&
                               group.NoteSlot.Visibility == Visibility.Collapsed,
                    "todo active again: slot visibility is wrong");

                for (int i = 0; i < 5; i++)
                {
                    ClickTab(group.NoteTab);
                    Pump();
                    ClickTab(group.TodoTab);
                    Pump();
                }

                Checks.Require(ReferenceEquals(group.TodoView, todo), "the todo view instance was rebuilt");
                Checks.Require(ReferenceEquals(group.NoteView, note), "the note view instance was rebuilt");
                Checks.Require(ReferenceEquals(group.TodoSlot.Content, todo), "the todo slot changed its content");
                Checks.Require(ReferenceEquals(group.NoteSlot.Content, note), "the note slot changed its content");
                Checks.Require(Math.Abs(group.Window.Width - width) < 0.5 && Math.Abs(group.Window.Height - height) < 0.5,
                    "tab switching changed the group size");
                Checks.Note("10 switches, same view instances, size " + width + "x" + height);
            });
        }

        /// <summary>A5: resize re-fits both views; tab switches keep the group size.</summary>
        private static void RunA5(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A5", "resize: todo recomputes at 700x500 / 820x600, note re-lays out", () =>
            {
                RequireGroup(group, error);
                var sizes = new[] { new Size(700, 500), new Size(820, 600) };
                foreach (Size size in sizes)
                {
                    group.SetActive(0);
                    Pump();
                    Checks.Require(group.TryResize(size.Width, size.Height),
                        "TryResize(" + size.Width + "x" + size.Height + ") returned false while unlocked");
                    Pump();
                    group.ApplyContentSize();
                    Pump();
                    Checks.Require(Math.Abs(group.Window.Width - size.Width) < 0.5 &&
                                   Math.Abs(group.Window.Height - size.Height) < 0.5,
                        "window did not reach " + size.Width + "x" + size.Height);
                    Checks.Require(group.TodoView.ActualWidth > 0, "the todo view has no width at " + size.Width);
                    Checks.Require(group.TodoView.DesiredHeight > 0,
                        "the todo view DesiredHeight is not positive at " + size.Width);
                    Checks.Note("todo at " + size.Width + "x" + size.Height +
                                ": DesiredHeight=" + group.TodoView.DesiredHeight +
                                ", actual=" + group.TodoView.ActualWidth + "x" + group.TodoView.ActualHeight);

                    group.SetActive(1);
                    Pump();
                    Checks.Require(group.NoteView.ActualWidth > 0 && group.NoteView.ActualHeight > 0,
                        "the note view did not lay out at " + size.Width + "x" + size.Height +
                        " (actual " + group.NoteView.ActualWidth + "x" + group.NoteView.ActualHeight + ")");
                    Checks.Note("note at " + size.Width + "x" + size.Height + ": actual=" +
                                group.NoteView.ActualWidth + "x" + group.NoteView.ActualHeight);

                    double width = group.Window.Width;
                    double height = group.Window.Height;
                    group.SetActive(0);
                    Pump();
                    group.SetActive(1);
                    Pump();
                    Checks.Require(Math.Abs(group.Window.Width - width) < 0.5 &&
                                   Math.Abs(group.Window.Height - height) < 0.5,
                        "tab switching changed the group size after resizing");
                }

                group.SetActive(0);
                group.TryResize(760, 540);
                Pump();
                group.ApplyContentSize();
                Pump();
            });
        }

        /// <summary>A6: note title flows to the group title row, flush is safe, undo restores a delete.</summary>
        private static void RunA6(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A6", "note plumbing: ChromeTitle in the title row, flush safe, delete+undo", () =>
            {
                RequireGroup(group, error);
                group.SetActive(1);
                Pump();
                group.RefreshTitle();
                Pump();

                string tabLabel = Localization.Get("UI_WidgetKindNote");
                string title = group.TitleText.Text;
                Checks.Require(!string.IsNullOrWhiteSpace(title), "the note title row is empty");
                Checks.Require(title == group.NoteView.ChromeTitle,
                    "title row '" + title + "' != NotePanelView.ChromeTitle '" + group.NoteView.ChromeTitle + "'");
                Checks.Require(title != tabLabel, "the title duplicates the note tab label");
                Checks.Note("note home title: " + title);

                // Detail: the title follows the note's own title.
                group.NoteView.OpenNote("note-1");
                Pump();
                group.RefreshTitle();
                Pump();
                string detailTitle = group.TitleText.Text;
                Checks.Require(detailTitle != title, "the title did not follow the opened note");
                Checks.Require(detailTitle == group.NoteView.ChromeTitle,
                    "detail title '" + detailTitle + "' != ChromeTitle '" + group.NoteView.ChromeTitle + "'");
                Checks.Note("note detail title: " + detailTitle);
                InvokePrivate(group.NoteView, "GoHome");
                Pump();

                // Flush is callable and must not throw.
                group.NoteView.FlushPendingEdit();
                Checks.Note("FlushPendingEdit() returned without throwing");

                // Add -> delete through the view's real deletion path -> undo.
                var data = group.NoteData;
                Checks.Require(data != null, "the group window exposes no NoteData");
                int before = data.Notes.Count;
                bool notified = false;
                group.NoteHost.OnDataChanged += () => { notified = true; };

                var extra = new NoteItem
                {
                    Id = "note-spike",
                    Text = "### 撤销验证\n\n- 这一条先被删除\n",
                    CreatedAt = DateTime.Now,
                    ModifiedAt = DateTime.Now
                };
                data.Notes.Insert(0, extra);
                group.NoteView.Refresh();
                Checks.Require(data.Notes.Count == before + 1, "the note was not added to the data");

                MethodInfo delete = typeof(NotePanelView).GetMethod("InvokeDelete",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Checks.Require(delete != null, "NotePanelView.InvokeDelete was not found");
                delete.Invoke(group.NoteView, new object[] { extra });
                Checks.Require(data.Notes.Count == before, "the note delete did not reach the data");
                Checks.Require(notified, "the real host's NotifyDataChanged did not fire on delete");

                Checks.Require(group.NoteView.TryUndo(), "NotePanelView.TryUndo() returned false");
                Checks.Require(data.Notes.Count == before + 1 && data.Notes.Contains(extra),
                    "undo did not restore the deleted note");
                Checks.Note("delete + TryUndo restored the note and drove host.NotifyDataChanged");

                group.SetActive(0);
                Pump();
            });
        }

        /// <summary>A7: text, caret and focus survive a tab round trip.</summary>
        private static void RunA7(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A7", "todo input: text + caret + focus survive a tab round trip", () =>
            {
                RequireGroup(group, error);
                group.SetActive(0);
                Pump();
                ActivateWindow(group.Window);
                Pump();

                InvokePrivate(group.TodoView, "RevealAddBox");
                Pump();

                var box = group.TodoView.FindName("AddBox") as TextBox;
                Checks.Require(box != null, "the todo add TextBox was not found");
                box.Text = "验证输入 keep me";
                box.CaretIndex = 5;
                box.Focus();
                Pump();
                Checks.Require(box.IsKeyboardFocused, "the add TextBox could not take keyboard focus");
                Checks.Require(box.CaretIndex == 5, "the caret did not land at 5 (it is " + box.CaretIndex + ")");

                ClickTab(group.NoteTab);
                Pump();
                Checks.Require(!group.IsActiveTodo, "the note tab did not activate");

                ClickTab(group.TodoTab);
                Pump();
                Checks.Require(group.IsActiveTodo, "the todo tab did not come back");
                Pump();

                Checks.Require(box.Text == "验证输入 keep me", "the text was lost: '" + box.Text + "'");
                Checks.Require(box.CaretIndex == 5, "the caret moved to " + box.CaretIndex);
                Checks.Require(box.IsKeyboardFocused, "the focus was not restored to the add TextBox");
                Checks.Note("text, caret (5) and keyboard focus all survived the round trip");
            });
        }

        /// <summary>A8: window-level keys/clicks re-dispatched to the active view.</summary>
        private static void RunA8(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A8", "keyboard routing: Ctrl+Z reaches TryUndo, Esc closes the note title menu", () =>
            {
                RequireGroup(group, error);

                // --- Esc closes an open note title menu -----------------------
                group.SetActive(1);
                Pump();
                ActivateWindow(group.Window);
                Pump();
                group.NoteView.ToggleTitleMenu(group.TitleText);
                Pump();
                Checks.Require(group.NoteView.IsTitleMenuOpen, "the note title menu did not open");

                var escArgs = RaisePreviewKey(group.Window, Key.Escape);
                Pump();
                Checks.Require(!group.NoteView.IsTitleMenuOpen, "Esc did not close the note title menu");
                Checks.Require(escArgs.Handled, "the group window did not mark Esc handled");
                Checks.Note("Esc closed the title menu through the group window's key router");

                // --- mouse preview is wired ------------------------------------
                int mouseBefore = group.MouseEventsSeen;
                RaisePreviewMouseDown(group.Window);
                Pump();
                Checks.Require(group.MouseEventsSeen > mouseBefore,
                    "the group window's PreviewMouseLeftButtonDown did not fire");
                Checks.Note("PreviewMouseLeftButtonDown delivered to the group router");

                // --- Ctrl+Z reaches TodoCalendarView.TryUndo -------------------
                group.SetActive(0);
                Pump();
                var todoData = group.TodoData;
                Checks.Require(todoData != null, "the group window exposes no TodoData");

                TodoItem added = TodoListOps.AddItem(todoData, "Ctrl+Z 验证项", DateTime.Today);
                group.TodoView.Refresh();
                Pump();
                int count = todoData.Items.Count;
                Checks.Require(count > 0, "the todo was not added");

                MethodInfo remove = typeof(TodoCalendarView).GetMethod("RemoveItem",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Checks.Require(remove != null, "TodoCalendarView.RemoveItem was not found");
                remove.Invoke(group.TodoView, new object[] { added });
                Checks.Require(todoData.Items.Count == count - 1, "the todo delete did not reach the data");

                Keyboard.ClearFocus();
                Pump();
                Checks.Require(!(Keyboard.FocusedElement is TextBox),
                    "the harness could not move focus off a TextBox (" +
                    (Keyboard.FocusedElement == null ? "null" : Keyboard.FocusedElement.GetType().Name) + ")");

                bool undone = false;
                string how;
                bool modifierSeen = WithCtrl(() =>
                {
                    KeyEventArgs args = RaisePreviewKey(group.Window, Key.Z);
                    undone = args.Handled && todoData.Items.Contains(added);
                }, out how);
                Pump();

                if (!modifierSeen)
                {
                    checks.Pending("A8",
                        "Ctrl+Z: the harness could not put Control into the keyboard state, so a synchronous " +
                        "Ctrl+Z could not be delivered. Manual: focus the group window, add then delete a todo, " +
                        "press Ctrl+Z -> the item returns.");
                }
                else
                {
                    Checks.Require(undone,
                        "Ctrl+Z was delivered (" + how + ") but the item was not restored");
                    Checks.Note("Ctrl+Z (" + how + ") restored the deleted todo through TryUndo");
                }

                group.SetActive(0);
                Pump();
            });
        }

        /// <summary>A9: show/hide, topmost, pin (deactivation), lock (blocks resize).</summary>
        private static void RunA9(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A9", "window controls: show/hide, topmost, pin, lock", () =>
            {
                RequireGroup(group, error);
                Window window = group.Window;
                Checks.Require(window.IsVisible, "the window was not visible before the show/hide test");

                group.ToggleTopmost();
                Pump();
                bool topmostOn = window.Topmost;
                group.ToggleTopmost();
                Pump();
                Checks.Require(topmostOn, "ToggleTopmost did not turn Topmost on");
                Checks.Require(!window.Topmost, "ToggleTopmost did not turn Topmost back off");

                // Unpinned deactivation hides the group (independent widget behaviour).
                Checks.Require(!group.IsPinned, "the group started pinned");
                InvokeOnDeactivated(window);
                Pump();
                Checks.Require(!window.IsVisible, "deactivation did not hide an unpinned group");
                window.Show();
                Pump();
                Checks.Require(window.IsVisible, "Show() did not bring the group back");

                // Pinned deactivation keeps it open.
                group.TogglePin();
                Pump();
                Checks.Require(group.IsPinned, "TogglePin did not pin the group");
                InvokeOnDeactivated(window);
                Pump();
                Checks.Require(window.IsVisible, "deactivation hid a pinned group");
                group.TogglePin();
                Pump();
                Checks.Require(!group.IsPinned, "TogglePin did not unpin the group");

                // Lock blocks the resize path.
                double width = window.Width;
                double height = window.Height;
                group.ToggleLock();
                Pump();
                Checks.Require(group.IsLocked, "ToggleLock did not lock the group");
                Checks.Require(!group.TryResize(700, 500), "a locked group accepted a resize");
                Checks.Require(Math.Abs(window.Width - width) < 0.5 && Math.Abs(window.Height - height) < 0.5,
                    "a locked group changed size");
                group.ToggleLock();
                Pump();
                Checks.Require(!group.IsLocked, "ToggleLock did not unlock the group");
                Checks.Require(group.TryResize(760, 540), "an unlocked group refused a resize");
                Pump();
                group.ApplyContentSize();
                Pump();
                Checks.Note("topmost/pin/deactivation/lock all behaved; locked resize refused");
            });
        }

        /// <summary>
        /// A10: the spike changed nothing outside tests/GroupUiSpike during the
        /// run (before/after `git status --porcelain` equality) and ran no git
        /// writes. The equality is the real guarantee: any line that appears
        /// only after the run must belong to the spike folder.
        /// </summary>
        private static void RunA10(Checks checks, string[] baseline)
        {
            checks.Run("A10", "isolation: git worktree unchanged outside tests/GroupUiSpike", () =>
            {
                string[] after = GitStatus();
                Checks.Require(after.Length == baseline.Length,
                    "git status length changed: " + baseline.Length + " -> " + after.Length +
                    " [" + string.Join(" | ", after.Except(baseline)) + "]");
                for (int i = 0; i < baseline.Length; i++)
                {
                    Checks.Require(baseline[i] == after[i],
                        "git status line changed: '" + baseline[i] + "' -> '" + after[i] + "'");
                }

                // Defensive: with the equality above this can only be empty, but
                // any new line is still only legal inside the spike folder.
                foreach (string line in after.Except(baseline, StringComparer.Ordinal))
                {
                    Checks.Require(IsSpikePath(line),
                        "unexpected change from this spike: " + line);
                }
                Checks.Note("git status --porcelain is byte-identical before/after (" + after.Length + " baseline lines)");
            });
        }

        /// <summary>True when a porcelain line points inside tests/GroupUiSpike.</summary>
        private static bool IsSpikePath(string line)
        {
            string path = line.Length > 3 ? line.Substring(3).Trim() : string.Empty;
            return path.StartsWith("tests/GroupUiSpike/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("tests\\GroupUiSpike\\", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path, "tests/GroupUiSpike", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path, "tests\\GroupUiSpike", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A11: --screenshot renders both tabs to PNG (and the two differ).</summary>
        private static void RunA11(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A11", "screenshot: spike-todo.png and spike-note.png render", () =>
            {
                RequireGroup(group, error);
                string dir = Path.Combine(_sandbox, "checkshots");
                var paths = RenderShots(group, dir);
                var info = new FileInfo(paths.Item1);
                var info2 = new FileInfo(paths.Item2);
                Checks.Require(info.Exists && info2.Exists, "a screenshot file was not written");
                Checks.Require(info.Length > 1000 && info2.Length > 1000,
                    "a screenshot is suspiciously small (" + info.Length + " / " + info2.Length + " bytes)");
                Checks.Require(HashFile(paths.Item1) != HashFile(paths.Item2),
                    "the todo and note screenshots are identical (the tab did not paint)");
                Checks.Note(paths.Item1 + " (" + info.Length + " bytes)");
                Checks.Note(paths.Item2 + " (" + info2.Length + " bytes)");
            });
        }

        /// <summary>A12: the note surface edits for real and the group flush commits it.</summary>
        private static void RunA12(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A12", "note editing: live surface edit reaches NoteData and re-renders", () =>
            {
                RequireGroup(group, error);
                ActivateWindow(group.Window);
                Pump();
                group.SetActive(1);
                Pump();
                group.NoteView.OpenNote("note-1");
                Pump();

                NoteItem note = group.NoteData.Notes.FirstOrDefault(n => n != null && n.Id == "note-1");
                Checks.Require(note != null, "note-1 is not in NoteData");
                string before = note.Text ?? string.Empty;
                Checks.Require(before.Contains("周会要点"), "note-1 does not start from the expected sample text");

                NoteSurfaceView surface = Descendants(group.NoteView).OfType<NoteSurfaceView>().FirstOrDefault();
                Checks.Require(surface != null, "the note detail hosts no NoteSurfaceView");
                Checks.Require(!surface.IsEditing, "the surface is already editing before the check");

                var box = surface.FindName("EditBox") as TextBox;
                Checks.Require(box != null, "the surface edit TextBox (EditBox) was not found");

                bool notified = false;
                group.NoteHost.OnDataChanged += () => { notified = true; };

                surface.BeginEditWhole();
                Pump();
                Checks.Require(surface.IsEditing, "BeginEditWhole did not open an edit session");
                surface.FocusEditBox();
                Pump();
                Checks.Require(box.IsVisible, "the note edit box is not visible");
                Checks.Require(box.IsKeyboardFocused, "the note edit box could not take keyboard focus");

                box.AppendText("\n\n追加：外部编辑验证 A12");
                Pump();
                Checks.Require(box.Text.EndsWith("A12"), "the typed text did not land in the edit box");

                // Switching away is the group's flush path (FlushPendingEdit).
                ClickTab(group.TodoTab);
                Pump();
                Checks.Require(!surface.IsEditing, "switching away did not settle the note edit");
                Checks.Require(note.Text != null && note.Text.Contains("A12"),
                    "the typed text did not reach NoteData (text ends '" + Tail(note.Text, 40) + "')");
                Checks.Require(note.Text.Contains("周会要点"), "the original note text was lost");
                Checks.Require(notified, "the real host's NotifyDataChanged did not fire on the note edit");
                Checks.Note("committed to NoteData (" + note.Text.Length + " chars), host.NotifyDataChanged fired");

                // Back on the note tab the detail must re-render the committed text.
                ClickTab(group.NoteTab);
                Pump();
                group.RefreshTitle();
                Pump();
                NoteSurfaceView surface2 = Descendants(group.NoteView).OfType<NoteSurfaceView>().FirstOrDefault();
                Checks.Require(surface2 != null, "the note detail lost its surface after switching back");
                var reading = Descendants(surface2).OfType<RichTextBox>()
                    .FirstOrDefault(r => r.Visibility == Visibility.Visible);
                Checks.Require(reading != null, "the rendered reading box was not found");
                string rendered = new TextRange(reading.Document.ContentStart, reading.Document.ContentEnd).Text;
                Checks.Require(rendered.Contains("A12"), "the re-rendered note does not show the committed text");
                Checks.Require(group.TitleText.Text == group.NoteView.ChromeTitle,
                    "the title row is not the note title after the edit");
                Checks.Note("re-render shows the appended text; title row = " + group.TitleText.Text);
            });
        }

        /// <summary>A13: a real title-row affordance opens the note title menu; Esc closes it.</summary>
        private static void RunA13(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A13", "title-row affordance: click opens the note title menu, Esc closes it", () =>
            {
                RequireGroup(group, error);
                group.SetActive(1);
                Pump();

                var button = group.Window.FindName("TitleMenuButton") as FrameworkElement;
                Checks.Require(button != null,
                    "the title-row menu affordance 'TitleMenuButton' is not registered on the group window");
                Checks.Require(button.Visibility == Visibility.Visible,
                    "the title-row menu affordance is not visible on the note tab");
                Checks.Require(button.IsVisible, "the title-row menu affordance has no visible layout");

                ClickElement(button);
                Pump();
                Checks.Require(group.NoteView.IsTitleMenuOpen,
                    "clicking the title affordance did not open the note title menu");

                var esc = RaisePreviewKey(group.Window, Key.Escape);
                Pump();
                Checks.Require(!group.NoteView.IsTitleMenuOpen,
                    "Esc did not close the menu opened from the title affordance");
                Checks.Require(esc.Handled, "the group window did not mark that Esc handled");

                group.SetActive(0);
                Pump();
                Checks.Require(button.Visibility != Visibility.Visible,
                    "the note title affordance stays visible on the todo tab");

                Checks.Note("click -> IsTitleMenuOpen; Esc -> closed; hidden on the todo tab");
            });
        }

        /// <summary>A14: the todo day-area and row right-click menus exist with items.</summary>
        private static void RunA14(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A14", "todo right-click menus: day-area and row menus exist with items", () =>
            {
                RequireGroup(group, error);
                group.SetActive(0);
                Pump();

                var root = group.TodoView.FindName("Root") as FrameworkElement;
                Checks.Require(root != null, "the todo view Root was not found");

                // Raising ContextMenuOpening must run the real handler that
                // assigns the day-area menu.
                root.ContextMenu = null;
                RaiseContextMenuOpening(root);
                Pump();

                ContextMenu dayMenu = root.ContextMenu;
                Checks.Require(dayMenu != null, "ContextMenuOpening did not assign the day-area menu");
                Checks.Require(dayMenu.Items.Count >= 1, "the day-area menu has no items");
                Checks.Note("day-area menu: " + MenuHeaders(dayMenu));

                var itemsPanel = group.TodoView.FindName("ItemsPanel") as Panel;
                Checks.Require(itemsPanel != null, "the todo ItemsPanel was not found");
                ContextMenu rowMenu = null;
                foreach (DependencyObject node in Descendants(itemsPanel))
                {
                    var element = node as FrameworkElement;
                    if (element != null && element.ContextMenu != null)
                    {
                        rowMenu = element.ContextMenu;
                        break;
                    }
                }
                Checks.Require(rowMenu != null, "no todo row carries a context menu");
                Checks.Require(rowMenu.Items.Count >= 2,
                    "the row menu has " + rowMenu.Items.Count + " items");
                Checks.Note("row menu: " + MenuHeaders(rowMenu));

                // Real WPF menus: try to open the day-area one (soft evidence).
                try
                {
                    dayMenu.PlacementTarget = root;
                    dayMenu.IsOpen = true;
                    Pump();
                    Checks.Note("day-area menu IsOpen after opening: " + dayMenu.IsOpen);
                    dayMenu.IsOpen = false;
                    Pump();
                }
                catch (Exception ex)
                {
                    Checks.Note("day-area popup could not open in this harness: " + ex.Message);
                }
            });
        }

        /// <summary>A15: a left click outside the inline editor commits the item text.</summary>
        private static void RunA15(Checks checks, ISpikeGroupWindow group, string error)
        {
            checks.Run("A15", "outside click commits the todo item editor text", () =>
            {
                RequireGroup(group, error);
                ActivateWindow(group.Window);
                Pump();
                group.SetActive(0);
                Pump();

                TodoItem item = TodoListOps.AddItem(group.TodoData, "外部点击提交原文本", DateTime.Today);
                group.TodoView.Refresh();
                Pump();
                Checks.Require(group.TodoData.Items.Contains(item), "the todo item was not added");

                MethodInfo start = typeof(TodoCalendarView).GetMethod("StartItemEdit",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Checks.Require(start != null, "TodoCalendarView.StartItemEdit was not found");
                start.Invoke(group.TodoView, new object[] { item.Id, new Point(200, 200) });
                Pump();

                var itemsPanel = group.TodoView.FindName("ItemsPanel") as Panel;
                Checks.Require(itemsPanel != null, "the todo ItemsPanel was not found");
                TextBox editor = Descendants(itemsPanel).OfType<TextBox>()
                    .FirstOrDefault(t => t.IsVisible);
                Checks.Require(editor != null, "the inline item editor was not built");
                Checks.Require(editor.Text == "外部点击提交原文本",
                    "the editor did not open on the item text ('" + editor.Text + "')");

                editor.Focus();
                Pump();
                Checks.Require(editor.IsKeyboardFocused, "the item editor could not take keyboard focus");

                editor.Text = "外部点击提交改后文本";
                editor.CaretIndex = editor.Text.Length;
                Pump();

                // A left click outside the editor through the group window's real
                // PreviewMouseLeftButtonDown handler (the title text is outside).
                RaisePreviewMouseDown(group.Window, group.TitleText);
                Pump();

                Checks.Require(item.Text == "外部点击提交改后文本",
                    "the outside click did not commit the editor (item text '" + item.Text + "')");
                Checks.Require(!editor.IsKeyboardFocused,
                    "the editor kept the keyboard focus after the outside click");
                Checks.Note("outside click committed '" + item.Text + "' to TodoData");
            });
        }

        #endregion

        #region --screenshot / --ui

        private static int RunScreenshot(SpikeInput input, string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                Console.WriteLine("usage: --screenshot <dir>");
                return 2;
            }

            ISpikeGroupWindow group = CreateGroup(input);
            try
            {
                ShowForLayout(group);
                Pump();
                var paths = RenderShots(group, dir);
                Console.WriteLine("wrote " + paths.Item1);
                Console.WriteLine("wrote " + paths.Item2);
                return 0;
            }
            finally
            {
                try { group.Window.Close(); } catch { }
                Pump();
            }
        }

        private static int RunUi(SpikeInput input)
        {
            ISpikeGroupWindow group = CreateGroup(input);
            group.Window.WindowStartupLocation = WindowStartupLocation.Manual;
            group.Window.Left = 220;
            group.Window.Top = 140;
            group.Window.Show();
            group.Window.Activate();

            var control = new ControlWindow(group);
            control.WindowStartupLocation = WindowStartupLocation.Manual;
            control.Left = group.Window.Left + group.Window.Width + 16;
            control.Top = group.Window.Top;
            control.Show();

            bool shuttingDown = false;
            Action shutdown = () =>
            {
                if (shuttingDown) return;
                shuttingDown = true;
                try { group.Window.Close(); } catch { }
                try { control.Close(); } catch { }
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            };
            group.Window.Closed += (s, e) => shutdown();
            control.Closed += (s, e) => shutdown();

            Console.WriteLine("--ui: group window + control window shown; close the control window to exit.");
            Console.WriteLine("--ui manual items: IME candidate placement, flicker, right-click menus, " +
                              "Ctrl+Z / Esc / outside-click commit.");
            Dispatcher.Run();
            return 0;
        }

        /// <summary>Shows the group off-screen and lays it out for the checks.</summary>
        private static void ShowForLayout(ISpikeGroupWindow group)
        {
            Window window = group.Window;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -3000;
            window.Top = -3000;
            window.ShowActivated = true;
            window.Show();
            window.Activate();
            window.UpdateLayout();
            Pump();
        }

        private static Tuple<string, string> RenderShots(ISpikeGroupWindow group, string dir)
        {
            Directory.CreateDirectory(dir);
            if (!group.Window.IsVisible)
            {
                group.Window.Show();
                Pump();
            }
            group.Window.UpdateLayout();
            Pump();

            group.SetActive(0);
            group.RefreshTitle();
            Pump();
            string todoPath = Path.Combine(dir, "spike-todo.png");
            SaveWindowPng(group.Window, todoPath);

            group.SetActive(1);
            group.RefreshTitle();
            Pump();
            string notePath = Path.Combine(dir, "spike-note.png");
            SaveWindowPng(group.Window, notePath);

            return Tuple.Create(todoPath, notePath);
        }

        private static void SaveWindowPng(Window window, string path)
        {
            window.UpdateLayout();
            int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
            int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }

        #endregion

        #region Harness helpers

        private sealed class Checks
        {
            public int Passed { get; private set; }
            public int Failures { get; private set; }

            public void Run(string id, string title, Action body)
            {
                try
                {
                    body();
                    Passed++;
                    Console.WriteLine("PASS " + id + ": " + title);
                }
                catch (Exception ex)
                {
                    Failures++;
                    Console.WriteLine("FAIL " + id + ": " + ex.Message);
                }
            }

            public void Pending(string id, string detail)
            {
                Console.WriteLine("PENDING HUMAN " + id + ": " + detail);
            }

            public static void Note(string detail)
            {
                Console.WriteLine("    · " + detail);
            }

            public static void Require(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
            }
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }

        private static void ActivateWindow(Window window)
        {
            try
            {
                window.Activate();
                window.Focus();
                Pump();
            }
            catch (Exception)
            {
                // focus is best effort in a headless check
            }
        }

        private static void ClickTab(FrameworkElement tab)
        {
            ClickElement(tab);
        }

        private static void ClickElement(FrameworkElement element)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = element
            };
            element.RaiseEvent(args);
        }

        /// <summary>Runs the real ContextMenuOpening handler on an element.</summary>
        private static void RaiseContextMenuOpening(FrameworkElement target)
        {
            ConstructorInfo ctor = typeof(ContextMenuEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new Type[] { typeof(object), typeof(bool) }, null);
            if (ctor == null)
                throw new InvalidOperationException("ContextMenuEventArgs(object,bool) was not found");

            var args = (ContextMenuEventArgs)ctor.Invoke(new object[] { target, true });
            args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            target.RaiseEvent(args);
        }

        /// <summary>The item headers of a menu, for the report.</summary>
        private static string MenuHeaders(ContextMenu menu)
        {
            var headers = new List<string>();
            foreach (object item in menu.Items)
            {
                var menuItem = item as MenuItem;
                headers.Add(menuItem == null ? StringOf(item) : StringOf(menuItem.Header));
            }
            return string.Join(" | ", headers);
        }

        private static string StringOf(object value)
        {
            if (value == null) return "(null)";
            var block = value as TextBlock;
            if (block != null) return block.Text;
            return value.ToString();
        }

        private static string Tail(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? string.Empty;
            return "…" + text.Substring(text.Length - max);
        }

        private static KeyEventArgs RaisePreviewKey(Window window, Key key)
        {
            PresentationSource source = PresentationSource.FromVisual(window);
            if (source == null)
            {
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                source = System.Windows.Interop.HwndSource.FromHwnd(
                    new System.Windows.Interop.WindowInteropHelper(window).Handle);
            }
            if (source == null) throw new InvalidOperationException("no presentation source for key input");

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent
            };
            ((UIElement)window).RaiseEvent(args);
            return args;
        }

        private static void RaisePreviewMouseDown(Window window, UIElement source = null)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = source ?? window
            };
            ((UIElement)window).RaiseEvent(args);
        }

        private static void InvokeOnDeactivated(Window window)
        {
            MethodInfo method = typeof(Window).GetMethod("OnDeactivated",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Window.OnDeactivated was not found");
            method.Invoke(window, new object[] { EventArgs.Empty });
        }

        private static void InvokePrivate(object target, string method)
        {
            MethodInfo info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (info == null) throw new InvalidOperationException(method + " was not found on " + target.GetType().Name);
            info.Invoke(target, null);
        }

        private static Brush BackgroundOf(FrameworkElement element)
        {
            var control = element as Control;
            if (control != null) return control.Background;
            var panel = element as Panel;
            if (panel != null) return panel.Background;
            var border = element as Border;
            if (border != null) return border.Background;
            return null;
        }

        private static bool IsTransparent(Brush brush)
        {
            if (brush == null) return true;
            var solid = brush as SolidColorBrush;
            return solid != null && solid.Color.A == 0;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (DependencyObject nested in Descendants(child)) yield return nested;
            }
        }

        private static TextBlock FindTextBlock(DependencyObject root, string text)
        {
            if (root is TextBlock && ((TextBlock)root).Text == text) return (TextBlock)root;
            foreach (DependencyObject node in Descendants(root))
            {
                var block = node as TextBlock;
                if (block != null && block.Text == text) return block;
            }
            return null;
        }

        #region Ctrl modifier synthesis

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] keyState);

        [DllImport("user32.dll")]
        private static extern bool SetKeyboardState(byte[] keyState);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        private const byte VkControl = 0x11;
        private const byte VkLeftControl = 0xA2;
        private const byte VkRightControl = 0xA3;
        private const uint KeyEventKeyUp = 0x2;

        /// <summary>
        /// Puts Control into the keyboard state, runs the action and clears it
        /// again. Thread-local SetKeyboardState is tried first (no global side
        /// effects); a transient global keybd_event is the fallback. Returns
        /// false when WPF still does not see the modifier.
        /// </summary>
        private static bool WithCtrl(Action action, out string how)
        {
            how = "none";
            byte[] state = new byte[256];
            if (GetKeyboardState(state))
            {
                byte[] pressed = (byte[])state.Clone();
                pressed[VkControl] = 0x80;
                pressed[VkLeftControl] = 0x80;
                pressed[VkRightControl] = 0x80;
                if (SetKeyboardState(pressed))
                {
                    try
                    {
                        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                        {
                            how = "SetKeyboardState";
                            action();
                            return true;
                        }
                    }
                    finally
                    {
                        SetKeyboardState(state);
                    }
                }
            }

            try
            {
                keybd_event(VkControl, 0, 0, UIntPtr.Zero);
                Pump();
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    how = "keybd_event";
                    action();
                    return true;
                }
            }
            finally
            {
                keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);
            }
            return false;
        }

        #endregion

        #region Config + git pins

        private static string HashOrAbsent(string path)
        {
            return File.Exists(path) ? HashFile(path) : "absent";
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string[] GitStatus()
        {
            string repo = FindRepoRoot();
            var start = new ProcessStartInfo("git", "status --porcelain")
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(30000);
                return output.Replace("\r\n", "\n").Split('\n')
                    .Select(line => line.TrimEnd())
                    .Where(line => line.Length > 0)
                    .OrderBy(line => line, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
                dir = dir.Parent;
            }
            return Directory.GetCurrentDirectory();
        }

        #endregion

        #endregion
    }
}
