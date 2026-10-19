# 笔记组件导航与交互打磨实施计划（详情页 · 标题下拉 · 空白语义 · 字号）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **状态：待用户审阅后执行。**
> **Spec**：`docs/superpowers/specs/2026-10-19-note-navigation-and-polish-design.md`（本计划只引 §号，不复述决策）。
> **基线**：`main` @ `64b91ac`；worktree：`C:\Users\31177347\.local\share\opencode\worktree\c3852b\note-widget-polish`；分支 `note-widget-polish`。
> **执行方式**：subagent-driven-development。每任务：实现子代理 TDD → 按 pathspec `git add`（不提交）→ 控制者
> `git stash create` 快照 + 审查包 → 任务审查 → 修复 / 复审 → 复审通过后由实现子代理按计划信息提交。
> **子代理模型**：`opencode/deepseek-v4.1-flash-free`（opencode-go key 续期前）；实现与审查同一模型。
> **子代理命令**：一律绝对路径 / `git -C <worktree>`；**禁止 `Set-Location`**（实测会导致子代理会话假死）。
> **会话位置**：控制者会话驻留 `C:\Users\31177347\Desktop\Coding`（技能所在仓库）；所有工作树操作使用绝对路径。

**Goal：** 把笔记组件从「列表内手风琴」升级为「home 列表 ⇄ detail 详情」，用标题下拉承担新建 / 回 home / 快速切换；收紧空白点击语义；修复退出编辑后无法重进的 Direct 路由 bug；新增每组件独立的字号档位。

**Architecture：** `NotePanelView` 拆成 `HomeHost`（卡片列表 + 空态）与 `DetailHost`（常驻 `NoteSurfaceView`）；`WidgetChrome` 增加可选标题 `▾` 与 `TitleMenuClicked`；`NoteSurfaceView` 增加文末 AppendZone、字号缩放，并用 `InputHitTest` 从点击坐标解析 RTB 行号（真实鼠标的 `OriginalSource` 是 RTB）；`NoteData` 增加 `FontScale`，`WidgetMenuBuilder` 增加字号子菜单。

**Tech Stack：** WPF / .NET Framework 4.8 / C# 7.3；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵 32 行不变）。

---

## Global Constraints

- C# 7.3 / net48；零新依赖；构建 0 警告。
- TDD：每任务先写失败 pin（编译失败或运行时失败均可）→ 最小实现 → 相关自检全绿；红测证据写进执行记录。
- 三语 key 同步（zh/en/ja 同一提交）；`LangCheck` 保持绿。
- hex 字面量只许在 `Core/UiTokens.cs`；本轮不新增颜色值，一律走 `UiTokens` / `ThemeManager` / 组件色。
- 自检内**禁止读写系统剪贴板**、**禁止驱动真实鼠标**；离屏 pin + 反射 + 合成事件。
  §Task 1 的脚本驱动真实鼠标是**一次性人工诊断**，不是自检的一部分；脚本不入库。
- 时间戳夹具一律显式传入；断言不得依赖 `DateTime.Now`（仅允许 `ModifiedAt >= 测试开始时刻` 这类比较）。
- 每个提交独立可编译、可回退；先任务审查后提交。合并 / 推送 / 部署必须先问用户。
- 离屏无法验证的项（真实焦点、Popup 屏幕边界、滚动接缝、真鼠标）如实标注为冒烟覆盖，不设假 pin。
- 已有延迟项（`_expandedId` 悬空、编辑中删他条留旧行、测试助手重复等）可顺手清理，但不得喧宾夺主。
- 本轮不改 `Core/MarkdownParser.cs` / `Core/NoteBlockRanges.cs` / `Controls/NoteMarkdownRenderer.cs`
  的行为；字号只通过既有 `NoteRenderStyle.BaseFontSize` 传入。

---

## 任务依赖图

```
T1 bug 修复（独立，先做）
T1B 点击光标精确到字符（依赖 T1 的点解析）
T2 字号数据（独立）
T3 WidgetChrome 标题 ▾（独立）
T4 home/detail + 标题下拉 + NoteWidget 接线（依赖 T3）
T5 AppendZone + 空白语义（依赖 T4 的 detail 宿主）
T6 字号渲染 + 右键菜单（依赖 T2 / T4）
T7 文档 + 全量自检 + 执行记录
```

---

## Task 1: 真鼠标点击 bug（探针 → 证据 → 修复）

**Files：**
- Modify: `Controls/NoteSurfaceView.xaml.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`
- Temporary: `tools/note-click-probe.ps1`（诊断脚本，最终删除 / 不入库）

**Interfaces：**
- 诊断探针只读，不改变行为；最终代码**保留** root 的
  `UIElement.MouseLeftButtonUpEvent` + `handledEventsToo: true` 订阅。
- 新增 `private static object HitAtPoint(RichTextBox, Point)` 与
  `private static int LineAtPoint(RichTextBox, Point)`；真实鼠标的
  `OriginalSource` 是 RTB 本身，行号必须用 `InputHitTest` 从坐标解析。
- 处理器签名保持 `(object sender, MouseButtonEventArgs e)`。

### Step 1（探针，不提交）

在 `NoteSurfaceView` 构造函数现有 root 订阅旁加临时探针（**最终必须删除**）：

```csharp
// TEMP PROBE - remove before commit.
SurfaceRoot.AddHandler(UIElement.MouseLeftButtonUpEvent,
    new MouseButtonEventHandler((s, e) => ClickProbe("MouseLeftButtonUp", e)), true);
SurfaceRoot.AddHandler(Mouse.MouseUpEvent,
    new MouseButtonEventHandler((s, e) => ClickProbe("Mouse.MouseUp", e)), true);
```

```csharp
// TEMP PROBE - remove before commit.
private static void ClickProbe(string evt, MouseButtonEventArgs e)
{
    try
    {
        object hit = e.OriginalSource ?? e.Source;
        string captured = Mouse.Captured == null ? "<null>" : Mouse.Captured.GetType().Name;
        string line = DateTime.Now.ToString("O")
            + " | " + evt
            + " | handled=" + e.Handled
            + " | button=" + e.ChangedButton
            + " | source=" + Describe(hit)
            + " | captured=" + captured
            + " | editing=" + (_editing ? "1" : "0")
            + " | taggedLine=" + TaggedLine(hit)
            + " | control=" + IsControlHit(hit)
            + " | scroll=" + IsScrollChrome(hit)
            + " | selection=" + SelectionIsNotEmpty(hit);
        File.AppendAllText(
            Path.Combine(Path.GetTempPath(), "kobold-note-click-probe.log"),
            line + Environment.NewLine);
    }
    catch
    {
        // Probe must never break input.
    }
}

private static string Describe(object hit)
{
    var element = hit as FrameworkElement;
    if (element == null) return hit == null ? "<null>" : hit.GetType().Name;
    return element.GetType().Name + (string.IsNullOrEmpty(element.Name) ? "" : "#" + element.Name);
}
```

`using System.IO;` 仅探针期间需要；删除探针时一并删除。

### Step 2（控制者：部署探针 + 真鼠标取证）

- 构建：`dotnet build C:\Users\31177347\.local\share\opencode\worktree\c3852b\note-widget-polish\Kobold.csproj -c Debug --nologo`
- 征求用户同意后部署探针版；让用户打开一个**有文字**的笔记组件，点卡展开
  （当前基线仍是手风琴），点第一块进入编辑，按 Esc，再点同一块。
- 控制者用脚本驱动一次真实鼠标（用户已授权）。脚本内容：

```powershell
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ProbeMouse {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint DOWN = 0x0002, UP = 0x0004;
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(DOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(UP, 0, 0, 0, UIntPtr.Zero);
    }
}
"@
$h = [ProbeMouse]::FindWindow($null, "NoteWidget")
if ($h -eq [IntPtr]::Zero) { throw "NoteWidget window not found" }
[ProbeMouse]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 400
$r = New-Object ProbeMouse+RECT
[ProbeMouse]::GetWindowRect($h, [ref]$r) | Out-Null
$x = [int](($r.Left + $r.Right) / 2)
$y = [int]($r.Top + 96)
"window=$($r.Left),$($r.Top),$($r.Right),$($r.Bottom) click=$x,$y" |
    Out-File -Append -Encoding utf8 "$env:TEMP\kobold-note-click-probe.log"
[ProbeMouse]::Click($x, $y)       # enter edit
Start-Sleep -Milliseconds 500
[ProbeMouse]::Click($x, $y)       # click a block (no-op / edit)
Start-Sleep -Milliseconds 300
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Milliseconds 400
[ProbeMouse]::Click($x, $y)       # the failing re-entry click
Start-Sleep -Milliseconds 600
```

- **实际证据（2026-10-19，已写入 ledger）**：
  - `MouseLeftButtonUp` 与 `Mouse.MouseUp` **都到达 root**；`Handled=True`；
  - `OriginalSource = RichTextBox#ReadingBox`，不是 `Span` / `Paragraph`；
  - `Mouse.Captured = null`；普通单击 `selection=False`；`TaggedLine(OriginalSource) = -1`；
  - 同一击的 `ReadingBox.InputHitTest(point)` 返回 `Paragraph`，
    `NoteMarkdownRenderer.SourceLine = 0`；`GetPositionFromPoint(point, false)` 为 `null`。
- 结论：**Direct 路由假设被推翻**；root 订阅正常，问题是真实鼠标的
  `OriginalSource` 是 RTB，行号必须用点击坐标 `InputHitTest` 解析。
- 证据日志：`%TEMP%\kobold-note-click-probe.log`（`input=Paragraph inputLine=0`）。

### Step 3（删除探针）

删除 `ClickProbe` / `Describe` / 两个探针 `AddHandler` 与 `using System.IO;`；
`git diff` 确认生产代码只剩原订阅。

### Step 4（红：坐标解析 pin）

- **不迁移事件类型**；`ClickTaggedLine` 与既有 `MouseLeftButtonUp` 合成事件保持原样。
- 新增 `LineAtPoint` pin（通过反射调用私有静态方法；方法不存在时运行时失败）：

```csharp
private static void SurfaceLineAtPointResolvesAParagraph()
{
    ThemeManager.Initialize("dark");
    var note = NewNote("a", "one\ntwo", new DateTime(2026, 3, 5, 9, 0, 0));
    var surface = NewSurface(new NoteData(), note);
    LayoutSurface(surface);

    var box = SurfaceReadingBox(surface);
    var span = SpanWithLine(box.Document, 1);
    Rect rect = span.ContentStart.GetCharacterRect(LogicalDirection.Forward);
    var method = typeof(NoteSurfaceView).GetMethod("LineAtPoint",
        BindingFlags.Static | BindingFlags.NonPublic);
    if (method == null)
        throw new InvalidOperationException("NoteSurfaceView has no LineAtPoint");
    int line = (int)method.Invoke(null, new object[]
    {
        box, new Point(rect.X + Math.Min(2, Math.Max(1, rect.Width / 2)), rect.Y + 2)
    });

    if (line != 1)
        throw new InvalidOperationException("the point resolved to line " + line + ", wanted 1");
}
```

- 可选 pin `SurfaceLineAtPointReturnsMinusOneOnBlank`：点在 `ReadingBox` 末尾之外的
  空白点 → -1；若离屏布局无法稳定构造空白点，标注冒烟覆盖。
- 注册到 `RunAll` 的 XamlLoadCheck 笔记组。
- 运行 `dotnet run --project tests\XamlLoadCheck\XamlLoadCheck.csproj -c Debug --nologo`
  → 预期新 pin 失败（`LineAtPoint` 不存在）。留档失败输出。

### Step 5（绿：实现）

- **保留**构造函数里的 `UIElement.MouseLeftButtonUpEvent` + `handledEventsToo: true`
  订阅（探针已证明它能到达 root）；不改事件类型。
- 在 `SurfaceRoot_MouseLeftButtonUp` 附近新增：

```csharp
/// <summary>
/// Resolves the element under the click inside a RichTextBox. Real mouse input
/// reports the box itself as OriginalSource, so the tagged Paragraph/Span has
/// to be read back through InputHitTest (the probe: source=ReadingBox,
/// taggedLine=-1, input=Paragraph with SourceLine=0).
/// </summary>
private static object HitAtPoint(RichTextBox box, Point point)
{
    var input = box.InputHitTest(point) as DependencyObject;
    if (IsControlHit(input)) return input;

    var pos = box.GetPositionFromPoint(point, true);
    if (pos != null && IsControlHit(pos.Parent)) return pos.Parent;
    return input;
}

/// <summary>The source line under the point, or -1 when it is blank.</summary>
private static int LineAtPoint(RichTextBox box, Point point)
{
    return TaggedLine(HitAtPoint(box, point));
}
```

- 处理器开头改为：

```csharp
private void SurfaceRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
{
    object hit = e.OriginalSource ?? e.Source;
    object resolved = hit;
    var box = hit as RichTextBox;
    if (box != null)
    {
        resolved = HitAtPoint(box, e.GetPosition(box));
        if (resolved == null) resolved = hit;
    }

    if (IsInside(hit, EditBox)) return;
    // AppendZone / CollapseButton checks as they exist ...
    if (SelectionIsNotEmpty(hit)) return;

    if (IsControlHit(resolved))
    {
        if (_editing) LeaveEdit();
        return;
    }

    if (IsScrollChrome(hit)) return;

    int line = TaggedLine(resolved);
    if (line < 0 && !ReferenceEquals(resolved, hit)) line = TaggedLine(hit);
    if (line < 0)
    {
        if (_editing) LeaveEdit();
        return;
    }

    if (_editing) { TrySwitchPaneEdit(line); return; }
    TryBeginEditAt(line);
}
```

- 保留 `EditBox` / `AppendZone`（Task 5） / `IsControlHit` / `IsScrollChrome` /
  `SelectionIsNotEmpty` / `TaggedLine` / `TrySwitchPaneEdit` 逻辑；`LineAtPoint` 供
  root 与测试共用。
- 运行 XamlLoadCheck 全绿；`dotnet build Kobold.csproj -c Debug --nologo` 0 警告。

### Step 6（控制者：修复后真鼠标冒烟）

- 部署修复版（先问用户）。探针已删除，修复版不再写日志；用 `PrintWindow`
  截图 / 用户肉眼确认点击后出现 Markdown 源码编辑框。
- 把 `NoteWidget` 移到未被其它置顶组件遮住的区域（`SetWindowPos`），点击已知
  文本行（探针用的 `(200, 190)` 一带），按 Esc，再点同一块；再点另一块、点空白后
  点块。完成后把窗口位置恢复。
- 用户手动复现一次，确认「可以再次进入编辑」；顺带点任务框 / 链接确认不误进编辑。
- 证据（窗口坐标、点击坐标、截图路径 / 用户确认）写进 ledger；若失败，回到 Step 1
  重新探针，不得宣称修复。

### Step 7（暂存 + 提交）

- `git add Controls/NoteSurfaceView.xaml.cs tests/XamlLoadCheck/Program.cs`
- 提交：`fix(note): resolve the clicked block from the rich text box point`
- 注意：探针与脚本不在提交内；`git status` 确认干净。

---

## Task 1B: 点击光标精确到字符（视觉偏移 → 源码行列）

**Files：**
- Modify: `Core/MarkdownParser.cs`（`MdInline.SourcePositions`）
- Create: `Core/NoteCaretMap.cs`
- Modify: `Controls/NoteSurfaceView.xaml.cs`
- Modify: `tests/NoteMarkdownCheck/Program.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces：
  ```csharp
  // MdInline: one source column per visible character; nested inlines carry their own.
  public List<int> SourcePositions;

  public static class NoteCaretMap
  {
      // visualOffset = 0-based visible-character offset inside the clicked line.
      // Returns the absolute source line (0-based) and source column (0-based).
      public static (int Line, int Column) Map(string text, int line, int visualOffset);
  }

  // NoteSurfaceView
  public bool TryBeginEditAt(int line, int column = 0);
  private static bool CaretAtPoint(RichTextBox box, Point point,
      out int line, out int visualOffset);
  private void SetCaret(int relativeLine, int relativeColumn);
  ```
- 覆盖：段落 / 标题 / 列表 / 任务精确；代码块尽量；表格 / 引用 / 分割线回退 `(line, 0)`。

### Step 1（红：parser source map）

在 `NoteMarkdownCheck` 加 `ParserSourcePositions()` 与 pins：

1. `source: plain text maps every character`：`MarkdownParser.ParseInlines("abc")` →
   一个 `MdText`，`Text=="abc"`，`SourcePositions == [0,1,2]`。
2. `source: an escape skips the backslash`：`"a\\*b"` → `Text=="a*b"`，
   `SourcePositions == [0,2,3]`。
3. `source: strong carries its inner offsets`：`"**bold** x"` → 一个 `MdStrong`
   含 `MdText("bold")`，子 `SourcePositions == [2,3,4,5]`；尾部 `MdText(" x")`
   `SourcePositions == [8,9]`。
4. `source: a code span excludes its backticks`：``"`co`de"`` → `MdCodeSpan("co")`
   `[1,2]`；`MdText("de")` `[4,5]`。
5. `source: a link label excludes the brackets`：`"[label](https://x)"` →
   `MdLink` `Text=="label"`，`SourcePositions == [1,2,3,4,5]`。
6. `source: an unpaired marker stays literal`：`"a * b"` → 可见文本 `"a * b"`，
   positions `[0,1,2,3,4]`。
- 运行 `dotnet run --project tests\NoteMarkdownCheck\NoteMarkdownCheck.csproj -c Debug --nologo`
  → 编译失败（`SourcePositions` 不存在）即红。留档。

### Step 2（实现 parser）

- `MdInline` 加 `public List<int> SourcePositions;`（默认 null；不改变 AST 类型）。
- `ParseInlines(string text)` 改为 `ParseInlines(text, 0)`；新增私有重载
  `ParseInlines(string text, int sourceBase)`。
- `literal` 旁维护 `var literalPositions = new List<int>();`，`Flush` 改为：
  ```csharp
  private static void Flush(List<MdInline> inlines, StringBuilder literal, List<int> positions)
  {
      if (literal.Length == 0) return;
      inlines.Add(new MdText { Text = literal.ToString(), SourcePositions = new List<int>(positions) });
      literal.Clear();
      positions.Clear();
  }
  ```
- 逐分支写位置（`sourceBase` 为当前子串在原行中的起始列）：
  - 反斜杠转义：`literal.Append(text[i + 1]); literalPositions.Add(sourceBase + i + 1); i += 2;`
  - 行内代码：`new MdCodeSpan { Text = ..., SourcePositions = Range(sourceBase + i + 1, length) }`
    （`Range` 返回连续列 `start..start+length-1`）；`i = close + 1`。
  - 链接（web）：`MdLink.Text` 的每个字符列 = `sourceBase + i + 1 + k`
    （label 是 `[` 后的原始子串）；`i = end`。
  - 非 web 链接 / 未配对 marker：按原样字符逐个 append，位置 = `sourceBase + 当前索引`。
  - `**` / `_` strong：递归 `ParseInlines(text.Substring(i + 2, close - i - 2), sourceBase + i + 2)`；
    `i = close + 2`。
  - `~~` strike：递归起点 `sourceBase + i + 2`；`i = close + 2`。
  - `*` / `_` em：递归起点 `sourceBase + i + 1`；`i = close + 1`。
  - 普通字符：`literalPositions.Add(sourceBase + i);`
- 运行 NoteMarkdownCheck → 新 pins 全绿，既有 pins 不回归。

### Step 3（红：NoteCaretMap）

在 `NoteMarkdownCheck` 加 `CaretMap()` 与 pins（全部显式）：

1. `caret: a paragraph offset maps directly`：`"one **two** three"` line 0；
   visualOffset 6（"two" 的末字符）→ `(0,8)`；visualOffset 4（"t"）→ `(0,6)`。
2. `caret: a heading skips its marker`：`"# Head"` line 0；visualOffset 2 → `(0,4)`；
   visualOffset 0 → `(0,2)`。
3. `caret: a list skips its bullet`：`"- item"` line 0；visualOffset 2 → `(0,4)`。
4. `caret: a task skips the box`：`"- [ ] task"` line 0；visualOffset 2 → `(0,8)`。
5. `caret: the line end sits after the last visible character`：`"# Head"` visualOffset 4
   → `(0,6)`；`""` / blank line → `(line,0)`。
6. `caret: a heading after a blank line uses the absolute line`：
   `"# H\n\nbody"` line 2 visualOffset 1 → `(2,1)`。
7. `caret: code maps to the code body line`：``"```\none\ntwo\n```"``；
   line 0 visualOffset 5（"two" 的 'o'）→ `(2,1)`（opening fence line 0 + 1 + code line 1，
   column 1）。
8. `caret: a table falls back to the line start`：`"| a |\n| --- |"` line 0 offset 2 →
   `(0,0)`；quote `"> q"` line 0 offset 1 → `(0,0)`。
- 运行 → 编译失败（`NoteCaretMap` 不存在）即红。留档。

### Step 4（实现 NoteCaretMap）

- 归一化换行（同 parser）：`text.Replace("\r\n","\n").Replace('\r','\n')` 后按 `\n` 切行。
- `MarkdownParser.Parse(normalized)`；遍历顶层块，按块类型找 line 对应的 inline 列表：
  - `MdParagraph`：`line - block.Line` 在 `Lines` 范围 → `Lines[index]`，prefix 0。
  - `MdHeading`：`block.Line == line` → `Inlines`；prefix =
    `lineText.Length - lineText.TrimStart('#', ' ', '\t').Length`（`#` + 空白）；
  - `MdListBlock`：找 `item.Line == line` → `item.Inlines`；prefix =
    `lineText.IndexOf(' ') + 1`（`- ` / `1. `）；
  - `MdTaskBlock`：`block.Line == line` → `Inlines`；prefix =
    `lineText.IndexOf(']') + 2`（`- [ ] `）；
  - `MdCodeBlock`：`block.Line == line` → 在 `block.Text` 中按 `visualOffset` 计 `\n`，
    返回 `(block.Line + 1 + codeLine, codeColumn)`；越界回退 `(line,0)`；
  - `MdQuote` / `MdTable` / `MdThematicBreak`：`(line,0)`。
- 可见 inline 展开：
  ```csharp
  private static int VisibleLength(MdInline inline) { ... }
  private static bool TryMapInline(MdInline inline, int offset, out int column)
  ```
  按 `MdText` / `MdCodeSpan` / `MdLink` 的 `Text.Length` 推进；`MdStrong` / `MdEm` /
  `MdStrike` 递归；找到后取 `SourcePositions[local] + prefix`。`offset == total` 时
  取最后一个位置 + 1；无可见文本返回 `prefix`。
- 运行 NoteMarkdownCheck → 全绿。

### Step 5（红：surface caret）

在 `XamlLoadCheck` 加：

1. `surface: the caret lands on the clicked character`：
   note `"# H\n\none **two** three"`；`TryBeginEditAt(2, 8)` → `EditBox` 文本为该段源码，
   `EditBox.CaretIndex` 等于 `8`（段落源码从行首开始）。
2. `surface: a heading caret skips the marker`：note `"# Head\n\nbody"`；
   `TryBeginEditAt(0, 4)` → `EditBox.CaretIndex == 4`（源码 `# Head`，列 4 是 'a'）。
3. `surface: a task caret skips the box`：note `"- [ ] task"`；
   `TryBeginEditAt(0, 8)` → `EditBox.CaretIndex == 8`。
4. `surface: CaretAtPoint names the visual offset`：布局后取 `SpanWithLine(...,1)` 的字符矩形，
   反射调用 `CaretAtPoint` → `line == 1`、`visualOffset` 在合理范围内；若离屏
   `GetPositionFromPoint` 不稳定，改断言 `NoteCaretMap` 纯逻辑 + 冒烟覆盖，并在记录注明。
- 运行 XamlLoadCheck → 编译失败 / 失败（无 `column` 参数）即红。留档。

### Step 6（实现 surface）

- `TryBeginEditAt(int line, int column = 0)`：`BeginEditAtIndex(index, line - range.Start, column)`。
- `BeginEditAtIndex(int index, int relativeLine, int relativeColumn)`；
  `EnterEditingState(relativeLine, relativeColumn)`；`SetCaret` 改为：
  ```csharp
  private void SetCaret(int relativeLine, int relativeColumn)
  {
      string text = EditBox.Text ?? string.Empty;
      string[] lines = text.Split('\n');
      int caret = 0;
      for (int i = 0; i < relativeLine && i < lines.Length; i++)
          caret += lines[i].Length + 1;
      if (relativeColumn > 0 && relativeLine < lines.Length)
          caret += Math.Min(relativeColumn, lines[relativeLine].Length);
      if (caret > text.Length) caret = text.Length;
      EditBox.CaretIndex = caret;
  }
  ```
- `CaretAtPoint(RichTextBox box, Point point, out int line, out int visualOffset)`：
  - `pos = box.GetPositionFromPoint(point, true)`；从 `pos.Parent` 上溯找带
    `SourceLine` 的 `Span` / `Paragraph`；`visualOffset = element.ContentStart.GetOffsetToPosition(pos)`；
  - 找不到 → false。
- root 处理器：`LineAtPoint` 找到行后，再 `CaretAtPoint` 取 visualOffset；
  `var mapped = NoteCaretMap.Map(_note.Text, line, visualOffset);`
  `TryBeginEditAt(mapped.Line, mapped.Column)`；切换块同样带列；映射失败 → 列 0。
- 保留 `LineAtPoint` 的既有 pin；`TryBeginEditAt(line)` 默认列 0，既有调用不变。
- 运行 XamlLoadCheck / NoteMarkdownCheck / build 全绿。

### Step 7（暂存 + 提交）

- `git add Core/MarkdownParser.cs Core/NoteCaretMap.cs Controls/NoteSurfaceView.xaml.cs tests/NoteMarkdownCheck/Program.cs tests/XamlLoadCheck/Program.cs`
- 提交：`feat(note): place the caret at the clicked character`

---

## Task 2: 字号数据（NoteFontScale / NoteData / AppConfig）

**Files：**
- Create: `Core/NoteFontScale.cs`
- Modify: `Core/NoteData.cs`
- Modify: `Core/AppConfig.cs`（`RepairNote`）
- Modify: `tests/NoteMarkdownCheck/Program.cs`
- Modify: `tests/WidgetKindsCheck/Program.cs`
- Modify: `tests/ConfigSafetyCheck/Program.cs`（新增 note 字号原子保存 pin）

**Interfaces：**
- Produces（T6 消费）：
  ```csharp
  public static class NoteFontScale
  {
      public const double Small = 0.9;
      public const double Medium = 1.0;
      public const double Large = 1.2;
      public static double Normalize(double value);
  }
  ```
  `NoteData.FontScale`（double，默认 `NoteFontScale.Medium`，setter 归一化）。

### Step 1（红）

- `NoteMarkdownCheck` 加 `NoteFontScales()` 方法与 pins：
  - `font: the three steps normalize to themselves`（0.9/1.0/1.2 原样，容差 1e-9）；
  - `font: invalid values fall back to medium`（`double.NaN`、`double.PositiveInfinity`、0、-1 → 1.0）；
  - `font: out-of-range values clamp`（0.5 → 0.9，2.0 → 1.2）；
  - `font: a fresh note data starts at medium`（`new NoteData().FontScale == 1.0`）。
- `WidgetKindsCheck` 加 `NoteFontScaleSurvivesConfig()`：
  - 写 JSON `{ "Widgets": [ { "Kind": "note", "Id": "n1", "FontScale": 1.2, "Notes": [] } ] }`
    → `AppConfig.Load` → `NoteData.FontScale == 1.2`；
  - 写 JSON 缺 `FontScale` → 1.0；
  - 写 `"FontScale": 0` 与 `"FontScale": 99` → 1.0 / 1.2（setter / RepairNote）；
  - `cfg.Save()` 后读回仍是 1.2。
- `ConfigSafetyCheck` 加 `NoteFontScaleSurvivesAnAtomicSave()`：构造带 note
  `FontScale=1.2` 的配置 → `Save()` → 重新 `Load()` → 仍为 1.2，且 `config.json`
  不是半截文件（沿用该检查既有的原子保存断言口径）。
- 运行 `dotnet run --project tests\NoteMarkdownCheck\NoteMarkdownCheck.csproj -c Debug --nologo`
  → 编译失败（`NoteFontScale` 不存在）即红。留档。

### Step 2（实现）

```csharp
using System;

namespace Kobold.Core
{
    /// <summary>The note body's three size steps: a multiplier of UiTokens.FontBody.</summary>
    public static class NoteFontScale
    {
        public const double Small = 0.9;
        public const double Medium = 1.0;
        public const double Large = 1.2;

        public static double Normalize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return Medium;
            if (value < Small) return Small;
            if (value > Large) return Large;
            return value;
        }
    }
}
```

`Core/NoteData.cs`：

```csharp
private double _fontScale = NoteFontScale.Medium;

/// <summary>The body-size multiplier (0.9 / 1.0 / 1.2); invalid values normalize.</summary>
public double FontScale
{
    get { return _fontScale; }
    set { _fontScale = NoteFontScale.Normalize(value); }
}
```

`Core/AppConfig.cs` `RepairNote` 开头：

```csharp
note.FontScale = NoteFontScale.Normalize(note.FontScale);
```

### Step 3（绿）

- NoteMarkdownCheck / WidgetKindsCheck / ConfigSafetyCheck 全绿；
  `dotnet build Kobold.csproj -c Debug --nologo` 0 警告。

### Step 4（暂存 + 提交）

- `git add Core/NoteFontScale.cs Core/NoteData.cs Core/AppConfig.cs tests/NoteMarkdownCheck/Program.cs tests/WidgetKindsCheck/Program.cs tests/ConfigSafetyCheck/Program.cs`
- 提交：`feat(note): add the note font scale data`

---

## Task 3: WidgetChrome 标题下拉触发器

**Files：**
- Modify: `Controls/WidgetChrome.xaml`
- Modify: `Controls/WidgetChrome.xaml.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces（T4 消费）：
  ```csharp
  public bool TitleMenuEnabled { get; set; }      // default false
  public event Action TitleMenuClicked;
  public FrameworkElement TitleMenuAnchor { get; } // TitleGroup
  ```
- 命名元素：`TitleGroup`、`TitleText`（保留）、`TitleChevron`。

### Step 1（红）

`XamlLoadCheck` 加：

```csharp
private static void ChromeTitleMenuChevronFollowsTheFlag()
{
    var chrome = new WidgetChrome();
    var chevron = chrome.FindName("TitleChevron") as FrameworkElement;
    if (chevron == null) throw new InvalidOperationException("WidgetChrome has no TitleChevron");
    if (chevron.Visibility != Visibility.Collapsed)
        throw new InvalidOperationException("the title chevron is visible while disabled");

    int clicks = 0;
    chrome.TitleMenuClicked += () => clicks++;
    chrome.TitleMenuEnabled = true;
    if (chevron.Visibility != Visibility.Visible)
        throw new InvalidOperationException("the title chevron stayed hidden after enabling");

    chevron.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
    {
        RoutedEvent = UIElement.MouseLeftButtonDownEvent
    });
    chevron.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
    {
        RoutedEvent = UIElement.MouseLeftButtonUpEvent
    });
    if (clicks != 1)
        throw new InvalidOperationException("the title chevron raised " + clicks + " click(s), wanted 1");
}
```

- 另加 `ChromeTitleTextStillExistsForFolderPins()`：`TitleText` 非空，
  `TitleMenuEnabled=false` 时 `TitleChevron` 不占可见区域；文件夹既有 pins 不回归。
- 运行 XamlLoadCheck → 编译失败 / 失败（无 `TitleChevron` / 属性）即红。留档。

### Step 2（实现 XAML）

把 `WidgetChrome.xaml` 头部标题列：

```xml
<Grid x:Name="TitleGroup" Grid.Column="1" Background="Transparent">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    <TextBlock x:Name="TitleText" x:FieldModifier="public" Grid.Column="0"
               FontSize="13" FontWeight="SemiBold" VerticalAlignment="Center"
               TextTrimming="CharacterEllipsis"/>
    <TextBlock x:Name="TitleChevron" x:FieldModifier="public" Grid.Column="1"
               Text="▾" FontSize="12" Margin="6,0,0,0" Padding="2,0"
               VerticalAlignment="Center" Visibility="Collapsed" Cursor="Hand"
               Foreground="{DynamicResource Kobold.Brush.TextMuted}"
               MouseLeftButtonDown="TitleChevron_MouseLeftButtonDown"
               MouseLeftButtonUp="TitleChevron_MouseLeftButtonUp"/>
</Grid>
```

### Step 3（实现 code-behind）

```csharp
private bool _titleMenuEnabled;

/// <summary>Shows the title chevron; note widgets turn this on.</summary>
public bool TitleMenuEnabled
{
    get { return _titleMenuEnabled; }
    set
    {
        if (_titleMenuEnabled == value) return;
        _titleMenuEnabled = value;
        TitleChevron.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>The chevron was clicked; the note widget opens its title menu.</summary>
public event Action TitleMenuClicked;

/// <summary>The title area the note widget anchors its popup to.</summary>
public FrameworkElement TitleMenuAnchor => TitleGroup;

private void TitleChevron_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
{
    e.Handled = true; // never start a header drag from the chevron
}

private void TitleChevron_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
{
    e.Handled = true;
    TitleMenuClicked?.Invoke();
}
```

- `ApplyTheme()` 末尾加 `TitleChevron.Foreground = ThemeManager.SecondaryTextBrush;`（无需新 hex）。

### Step 4（绿）

- XamlLoadCheck 全绿（文件夹 / 待办标题 pin 不回归）；build 0 警告。

### Step 5（暂存 + 提交）

- `git add Controls/WidgetChrome.xaml Controls/WidgetChrome.xaml.cs tests/XamlLoadCheck/Program.cs`
- 提交：`feat(chrome): add the optional title menu chevron`

---

## Task 4: home / detail 导航 + 标题下拉 + NoteWidget 接线

**Files：**
- Modify: `Controls/NotePanelView.xaml`（重写）
- Modify: `Controls/NotePanelView.xaml.cs`（重写编排）
- Modify: `Controls/NoteSurfaceView.xaml`（删除浮动 `‹`；detail 滚动区）
- Modify: `Controls/NoteSurfaceView.xaml.cs`（删除 `CollapseRequested` / 折叠按钮处理器）
- Modify: `Controls/NoteWidget.xaml.cs`（标题菜单 / 标题刷新）
- Modify: `Core/Localization.cs`（`UI_NoteHome` / `UI_NoteUntitled` / `UI_NoteEmptyHome`）
- Modify: `tests/XamlLoadCheck/Program.cs`（笔记组 pins 重写）
- `tests/LangCheck/Program.cs` 不改：它自动校验三语 key 集一致

**Interfaces：**
- Consumes：Task 3 的 `WidgetChrome.TitleMenuEnabled` / `TitleMenuClicked` / `TitleMenuAnchor`。
- Produces（T5/T6 消费）：
  ```csharp
  public string ChromeTitle { get; }
  public bool IsHome { get; }
  public void ToggleTitleMenu(UIElement anchor);
  public void CloseTitleMenu();
  public void OpenNote(string id);
  public FrameworkElement BuildTitleMenuContent();
  ```
- XAML 命名：`HomeHost`、`DetailHost`、`EmptyState`、`ItemsScroller`、`ItemsPanel`。
- 私有：`Expand` / `GoHome` / `StartNewNote` / `MountDetail` / `RebuildHome` /
  `SettleExpandedEdit` / `BuildBlankMenu` / `BuildCard`。

### Step 1（红：先重写 pins）

- 删除旧 `AddPill` / `ListHost` / 手风琴相关 pins 与死助手（`ClickNoteAdd`、
  `ClickNoteCollapse`、`NoteListHost`、`NoteHostAt`、`AssertEveryRowIsACardFace` 等；
  保留 `ClickNoteCard` / `NoteCardAt` / `NoteCardLine` / `InvokeNoteDelete` /
  `InvokeNoteTryUndo` / `NewNote`）。
- 顺手修 `Attach` 重复订阅 `Root.ContextMenuOpening`：用 `_attached` guard，或把订阅
  移到构造函数（每次 Attach 只更新数据与列表）。
- 所有旧 pin 里的 `NoteHostAt` / `AssertEveryRowIsACardFace` 断言改为
  `IsHome` / `DetailHost.Visibility` / `_surfaceHost`（后者通过 `NotePrivate` 读取）；
  不再假设展开行是 `ItemsPanel` 的子项。
- 新助手：
  ```csharp
  private static FrameworkElement NoteNamed(NotePanelView view, string name); // 既有
  private static NoteSurfaceView NoteSurface(NotePanelView view);            // _surface
  private static string NoteExpandedId(NotePanelView view);                  // _expandedId
  private static FrameworkElement TitleMenuPart(FrameworkElement root, string tag);
  ```
- 新 pins（夹具时间戳显式）：
  1. `note: home shows cards and the empty state`：空数据 → `EmptyState.Visible`、
     `ItemsPanel.Children.Count == 0`、`FindName("AddPill") == null`；加两条 → Refresh →
     两张卡、`EmptyState.Collapsed`。
  2. `note: a card click opens the detail`：两条笔记；`ClickNoteCard(view, 0)` →
     `IsHome == false`、`HomeHost.Collapsed`、`DetailHost.Visible`、`ReadingBox` 文档非空、
     `NoteExpandedId` 正确、`ChromeTitle` = 笔记标题。
  3. `note: the title follows home and detail`：home = `data.Name`；detail = 笔记标题；
     空笔记 = `Localization.Get("UI_NoteUntitled")`；编辑提交后标题更新。
  4. `note: the title menu groups actions and notes`：`BuildTitleMenuContent()` →
     `Tag="NoteMenuNew"` 存在；home 态 `Tag="NoteMenuHome"` 不存在；detail 态存在；
     笔记条目按 ModifiedAt 降序；当前条 `Tag="NoteMenuCheck"` 可见；
     条目 `Tag` = note id。
  5. `note: the title menu switches notes`：A 编辑改文本 → `OpenNote("b")` →
     A 文本已落、B detail 渲染态、`IsEditing == false`。
  6. `note: the title menu starts a new note`：点 `NoteMenuNew` → 新条置顶、detail、
     整文编辑、空框；旧编辑已落。
  7. `note: the title menu returns home`：detail 编辑 A → 点 `NoteMenuHome` →
     A 落盘、`IsHome == true`、卡面按 ModifiedAt；A 为空 → 丢弃 + 通知。
  8. `note: opening the title menu keeps the edit`：编辑中 `BuildTitleMenuContent()` /
     `ToggleTitleMenu(anchor)` → `IsEditing` 仍真；`CloseTitleMenu()` 后
     （离屏 Popup 打不开时，pin 只断言 `BuildTitleMenuContent` 不改编辑状态，
     打开 / 焦点行为交冒烟，并在执行记录注明）。
  9. `note: rendered blank clicks no longer collapse`：detail 渲染态点 `Root` 空白 /
     `_surfaceHost` padding → `IsHome == false`。
  10. `note: blank clicks leave the edit but stay in the detail`：编辑中点 `_surfaceHost`
      padding / Root 边距 → `IsEditing == false`、`IsHome == false`、文本已落。
  11. `note: Esc and Backspace return home only in the rendered detail`：
      渲染态 Esc/Backspace → home；编辑态 Esc → 退出编辑但 detail；home → 无动作。
  12. `note: the blank menu follows the state`：`BuildBlankMenu()` 在 home 只有新建、
      detail 有新建 + `UI_NoteHome`；`Root.ContextMenuOpening` 对 surface 内部的抑制
      若离屏事件构造不稳定，则 pin 只覆盖 `BuildBlankMenu`，右键路由交冒烟并在记录注明。
- 运行 XamlLoadCheck → 大面积旧 pin 失败 / 编译失败（`IsHome` 等不存在）即红。留档。

### Step 2（实现 XAML）

`NotePanelView.xaml` 重写为：

```xml
<UserControl ... PreviewKeyDown="Root_PreviewKeyDown">
    <Grid x:Name="Root" Margin="12,8,12,10" Background="Transparent"
          MouseLeftButtonDown="Root_MouseLeftButtonDown">

        <Grid x:Name="HomeHost">
            <ScrollViewer x:Name="ItemsScroller" VerticalScrollBarVisibility="Auto"
                          HorizontalScrollBarVisibility="Disabled" PanningMode="VerticalOnly"
                          Background="Transparent">
                <StackPanel x:Name="ItemsPanel"/>
            </ScrollViewer>
            <TextBlock x:Name="EmptyState" Visibility="Collapsed"
                       HorizontalAlignment="Center" VerticalAlignment="Center"
                       TextAlignment="Center" TextWrapping="Wrap" Margin="24"
                       FontSize="{DynamicResource Kobold.Font.Body}"
                       Foreground="{DynamicResource Kobold.Brush.TextMuted}"/>
        </Grid>

        <Grid x:Name="DetailHost" Visibility="Collapsed"/>
    </Grid>
</UserControl>
```

- 删除 `ListHost` / `AddPill` / `AddPillGlyph`。
- `Attach` 与每次 `RebuildHome()` 都设置
  `EmptyState.Text = Localization.Get("UI_NoteEmptyHome");`（语言切换后也正确）。

### Step 3（实现 `NotePanelView` 编排）

- 构造函数：创建 `_surface` 与 `_surfaceHost`，`DetailHost.Children.Add(_surfaceHost)`；
  `_surface.DataChanged = () => { _host?.NotifyDataChanged(); _host?.RefreshChromeTitle(); };`
  不再订阅 `CollapseRequested`。
- `ChromeTitle` / `IsHome` 按 spec §3.2/§7.1。
- `RebuildHome()`：清空 `ItemsPanel`，按 `NoteOps.SortForDisplay` 加卡（`null` 条目跳过）；
  `EmptyState.Visibility = count == 0 ? Visible : Collapsed`；`HomeHost` 显示、`DetailHost` 收起；
  `_expandedId = null; _mountedNoteId = null;` 更新标题。
- `MountDetail()`：`HomeHost` 收起、`DetailHost` 显示；`FindExpanded()` 为 null → `GoHome()`；
  编辑中直接返回；否则 `_surface.Attach(_data, note)`；新挂载滚到顶部；
  `_mountedNoteId = note.Id`；更新标题。
- `Expand(note)`：先 `SettleExpandedEdit()`，`_expandedId = note.Id`，`MountDetail()`，
  `_surface.FocusReading()`，更新标题。
- `GoHome()`：`SettleExpandedEdit()`；`_expandedId = null`；`RebuildHome()`；
  恢复 home 滚动 offset（捕获/恢复失败回顶部）。
- `StartNewNote()`：先结算当前；新建 NoteItem 插到头部；`_expandedId`；`MountDetail()`；
  `_surface.BeginEditWhole()`；`_surface.FocusEditBox()`；更新标题。
- `SettleExpandedEdit()`：`_surface.LeaveEdit()`；空稿 → 从 `_data.Notes` 移除 +
  `_host.NotifyDataChanged()`；若移除的是当前条 → `_expandedId = null; _mountedNoteId = null;`。
- `OpenNote(string id)`：找 note；与当前条相同则返回；否则 `Expand(note)`。
- `Refresh()`：开头 `CloseTitleMenu()`（主题 / 语言 / 数据刷新时旧菜单不残留）；
  编辑中 → `_surface.Repaint()`；detail → `MountDetail()`；home → `RebuildHome()`；
  末尾更新标题。
- `Root_PreviewKeyDown` / `Root_MouseLeftButtonDown` 按 spec §3.3/§7.1。
- `Root.ContextMenuOpening`：仅当命中 `_surfaceHost` 之外时赋 `BuildBlankMenu()`；
  home 菜单只有新建，detail 菜单新建 + `UI_NoteHome`；surface 内部置 null。
- `BuildBlankMenu()` 用 `MenuBuilder`，条目：
  ```csharp
  var newItem = new MenuItem {
      Header = "＋ " + Localization.Format("UI_NewWidgetFormat", Localization.Get("UI_WidgetKindNote"))
  };
  newItem.Click += (s, e) => StartNewNote();
  menu.AddMenuItem(newItem);
  if (!IsHome) {
      var home = new MenuItem { Header = Localization.Get("UI_NoteHome") };
      home.Click += (s, e) => GoHome();
      menu.AddMenuItem(home);
  }
  ```

### Step 4（实现标题下拉）

- `BuildTitleMenuContent()` 返回 `Border`（`Tag="NoteTitleMenu"`），内含：
  - 动作 `StackPanel Orientation=Horizontal`：
    `Button Tag="NoteMenuNew"`（`＋` + 新建）与 `Button Tag="NoteMenuHome"`（仅 detail）；
  - 分隔线；
  - `ScrollViewer`（`MaxHeight = Math.Max(120, ActualHeight * 0.45)`）内
    `StackPanel` 笔记条目；
  - 每条 `Button Tag=note.Id`，内容 `Grid`：`TextBlock Tag="NoteMenuCheck"`（当前条 `✓`）、
    `TextBlock` 标题；当前条 `Background = ThemeManager.SurfaceHoverBrush`。
  - 按钮一律透明底、无边框、`Cursor=Hand`，hover 用 `ThemeManager.SurfaceHoverBrush`
    或 `ThemeManager.ControlBackgroundBrush`；圆角走 `UiTokens.RadiusControl`；
    **不得新增 hex**。
- 点击动作：`CloseTitleMenu()` 后 `StartNewNote()` / `GoHome()`；点击条目：
  `CloseTitleMenu()` 后 `OpenNote(id)`。
- 下拉内容的 `PreviewKeyDown`：Esc → `CloseTitleMenu()` + `Handled=true`
  （Popup 是独立焦点域，不能只依赖面板的 key router）。
- `ToggleTitleMenu(UIElement anchor)`：
  - 已开 → `CloseTitleMenu()`；
  - 否则创建 `Popup { PlacementTarget=anchor, Placement=Bottom, StaysOpen=false,
    AllowsTransparency=true, Child=BuildTitleMenuContent() }`，`Closed` 时清引用；
    若关闭时仍在编辑 → `_surface.FocusEditBox()`；`IsOpen = true`。
- `CloseTitleMenu()`：`IsOpen=false`、清引用、幂等。

### Step 5（`NoteSurfaceView` / `NoteWidget` 接线）

- `NoteSurfaceView.xaml` 删除 `CollapseButton`；`SurfaceScroller` 去掉 `Padding="0,24,0,0"`，
  改为 detail 的滚动区。
- `NoteSurfaceView.xaml.cs` 删除 `CollapseRequested` 与 `CollapseButton_MouseLeftButtonUp`。
- `NoteWidget`：
  - 构造：`NoteChrome.TitleMenuEnabled = true;`
    `NoteChrome.TitleMenuClicked += () => _view.ToggleTitleMenu(NoteChrome.TitleMenuAnchor);`
  - `UpdateUI`：`base.UpdateUI(); _view?.Refresh(); RefreshChromeTitle();`
  - `internal void RefreshChromeTitle()`：`if (Chrome == null) return;`
    `NoteChrome.Title = _view?.ChromeTitle ?? _data.Name;`
    detail 下 `NoteChrome.TitleText.ToolTip = _view?.ChromeTitle;`，home 清空。
  - `Closing` / `OnPanelHidden`：`_view?.CloseTitleMenu(); _view?.FlushPendingEdit();`
- `NotePanelView` 在 `Expand` / `GoHome` / `StartNewNote` / `SettleExpandedEdit` /
  `OpenNote` / `Refresh` 末尾调用 `_host?.RefreshChromeTitle()`（避免递归：
  `RefreshChromeTitle` 只改 chrome，不回调 view）。

### Step 6（三语 key）

在 `Core/Localization.cs` 三种语言表中加入：

```csharp
["UI_NoteHome"] = "‹ Notes",                 // zh: "‹ 笔记列表"; ja: "‹ ノート一覧"
["UI_NoteUntitled"] = "Untitled",           // zh: "未命名"; ja: "無題"
["UI_NoteEmptyHome"] = "No notes yet — open the title menu to create one.",
                                             // zh: "暂无笔记——点标题 ▾ 新建"
                                             // ja: "ノートはまだありません — タイトルの ▾ から作成"
```

（实际按下表逐语言写入，不要三种语言都用英文。）

### Step 7（绿）

- 运行：XamlLoadCheck、LangCheck、NoteMarkdownCheck、MenuRenderCheck、WidgetKindsCheck。
- `dotnet build Kobold.csproj -c Debug --nologo` 0 警告。
- 若 `NoteSurfaceView` 的 `CollapseButton` 被旧 pins 引用，删除对应旧 pin / 助手。

### Step 8（暂存 + 提交）

- `git add Controls/NotePanelView.xaml Controls/NotePanelView.xaml.cs Controls/NoteSurfaceView.xaml Controls/NoteSurfaceView.xaml.cs Controls/NoteWidget.xaml.cs Core/Localization.cs tests/XamlLoadCheck/Program.cs`
- 提交：`feat(note): navigate between the note list and a detail page`

---

## Task 5: AppendZone + 表面空白语义

**Files：**
- Modify: `Controls/NoteSurfaceView.xaml`
- Modify: `Controls/NoteSurfaceView.xaml.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces：`public bool TryBeginAppendEdit();`
- 命名元素：`AppendZone`（`Border`）、`PaneStack`（`StackPanel`）、`SurfaceStack`（`Grid`）。
- 私有字段：`_appendSession`、`_appendPrefix`。

### Step 1（红）

新增 pins：

1. `surface: the append zone is only visible while editing`：
   `Attach` 后 `AppendZone.Visibility == Collapsed`；`TryBeginEditAt(0)` 后 `Visible`；
   `LeaveEdit()` 后 `Collapsed`。
2. `surface: the append zone starts a new block`：
   note `"one\ntwo"`；`TryBeginEditAt(1)` → 编辑；点击 `AppendZone`
   （`RaiseLeftClick` 或直接 `AppendZone.RaiseEvent(MouseLeftButtonUp)`）→ `IsEditing` 真、
   `EditBox.Text == ""`、`BeforeBox.Document.Blocks.Count == 1`、`AfterBox` 0。
   设 `EditBox.Text = "new"` → `Flush()` → `note.Text == "one\ntwo\n\nnew"`；
   `ModifiedAt >= when`；`DataChanged` 计 1。
3. `surface: an empty append writes nothing`：打开追加 → `LeaveEdit()` →
   `note.Text == "one\ntwo"`、`ModifiedAt == when`、`DataChanged == 0`。
4. `surface: the append separator keeps one blank line`：
   - `"one"` + `"new"` → `"one\n\nnew"`；
   - `"one\n"` + `"new"` → `"one\n\nnew"`；
   - `"one\n\n"` + `"new"` → `"one\n\nnew"`；
   - `"one\r\n"` + `"new"` → `"one\r\n\r\nnew"`；
   - `"one\r\n\r\n"` + `"new"` → `"one\r\n\r\nnew"`；
   - `""` + `"new"` → `"new"`。
5. `surface: appending in a whole-text edit is a no-op`：`BeginEditWhole()` →
   点击 `AppendZone` → 框文本不变、仍编辑、无追加。
6. `surface: switching from an append commits it`：追加输入 `"new"` → 点击
   `BeforeBox` 的块（`ClickTaggedLine`）→ note.Text 已追加、编辑切到该块。
7. `surface: a block-gap click leaves the edit`：编辑中点 `SurfaceStack`（非 AppendZone、
   非行标签）→ `IsEditing == false`、仍 detail。
8. `surface: the append zone is a click-away no-op in the rendered state`：
   渲染态（AppendZone 隐藏）点击 `SurfaceStack` → 不动作、不崩溃。
- 运行 XamlLoadCheck → 编译失败（`TryBeginAppendEdit` / `AppendZone` 不存在）即红。留档。

### Step 2（XAML）

```xml
<ScrollViewer x:Name="SurfaceScroller" VerticalContentAlignment="Stretch"
              VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"
              PanningMode="VerticalOnly" Background="Transparent">
    <Grid x:Name="SurfaceStack">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        <StackPanel x:Name="PaneStack" Grid.Row="0">
            <!-- ReadingBox / BeforeBox / EditBox / AfterBox 原样放这里 -->
        </StackPanel>
        <Border x:Name="AppendZone" Grid.Row="1" MinHeight="48"
                Background="Transparent" Cursor="IBeam" Visibility="Collapsed"
                MouseLeftButtonUp="AppendZone_MouseLeftButtonUp"/>
    </Grid>
</ScrollViewer>
```

- `ShowRendered()`：`AppendZone.Visibility = Collapsed;`
- `ShowEditing()`：`AppendZone.Visibility = Visible;`
- `AppendZone_MouseLeftButtonUp`：`e.Handled = true;` 若 `_editing && _editBlock != null`
  且非追加会话 → `TryBeginAppendEdit()`。

### Step 3（实现追加会话）

- `TryBeginAppendEdit()`：
  ```csharp
  public bool TryBeginAppendEdit()
  {
      if (_note == null) return false;
      if (_editing && _editBlock == null) return false; // whole-text / append already
      if (_editing) CommitRegion();

      string text = _note.Text ?? string.Empty;
      _appendSession = true;
      _appendPrefix = text;
      _editStart = NoteBlockRanges.LineCount(text);
      _editEnd = _editStart;
      _editBlock = null;
      _beforeBlocks = MarkdownParser.Parse(text);
      _afterBlocks = new List<MdBlock>();
      _entryText = text;
      _entryIndex = _beforeBlocks.Count;
      EnterEditingState(0);
      Dispatcher.BeginInvoke(new Action(() => AppendZone.BringIntoView()));
      return true;
  }
  ```
- `CommitRegion()` 开头：
  ```csharp
  if (_appendSession) { CommitAppend(); return; }
  ```
- `CommitAppend()`：
  ```csharp
  private void CommitAppend()
  {
      if (_note == null) return;
      if (EditBox.Text == _editEntryText) return;

      string body = EditBox.Text ?? string.Empty;
      string updated = _appendPrefix ?? string.Empty;
      if (body.Length > 0)
      {
          string separator;
          if (updated.Length == 0) separator = string.Empty;
          else if (updated.EndsWith("\n\n", StringComparison.Ordinal)
                || updated.EndsWith("\r\n\r\n", StringComparison.Ordinal)) separator = string.Empty;
          else if (updated.EndsWith("\r\n", StringComparison.Ordinal)) separator = "\r\n";
          else if (updated.EndsWith("\n", StringComparison.Ordinal)) separator = "\n";
          else separator = "\n\n";
          updated = updated + separator + body;
      }

      if (!string.Equals(updated, _note.Text, StringComparison.Ordinal))
      {
          _note.Text = updated;
          _note.ModifiedAt = DateTime.Now;
          DataChanged?.Invoke();
      }
      _editEntryText = EditBox.Text;
  }
  ```
- `ResetEditState()` 清 `_appendSession=false; _appendPrefix=null;`
- `LeaveEdit()` 在 `Flush()` 之后清 `_appendSession=false; _appendPrefix=null;`
  （会话结束；否则下次进入会带着旧追加状态）。
- `TryBeginEditAt` / `BeginEditWhole` 入场前清 `_appendSession`（或 `EnterEditingState` 统一清，
  但在 `TryBeginAppendEdit` 设置后不能清；在 `BeginEditAtIndex` / `BeginEditWhole` 开头清）。
- `SurfaceRoot_MouseUp` 分类加：
  ```csharp
  if (IsInside(hit, AppendZone)) return; // the zone owns its click
  ```
  （在 `IsInside(hit, EditBox)` 之后。）

### Step 4（绿）

- XamlLoadCheck 全绿（既有 T4/T5/T6 点击 pins 保持 `MouseLeftButtonUp`；T1 的
  `LineAtPoint` pin 已覆盖坐标解析）。
- NoteMarkdownCheck / MenuRenderCheck 不回归；build 0 警告。

### Step 5（暂存 + 提交）

- `git add Controls/NoteSurfaceView.xaml Controls/NoteSurfaceView.xaml.cs tests/XamlLoadCheck/Program.cs`
- 提交：`feat(note): append a new block from the end-of-text blank`

---

## Task 6: 字号渲染 + 右键菜单

**Files：**
- Modify: `Controls/NoteSurfaceView.xaml.cs`（`BuildRenderStyle` / `ApplyEditTypography`）
- Modify: `Controls/NoteWidget.xaml.cs`（`RefreshChromeTitle` 已有；`UpdateUI` 已刷 view）
- Modify: `Helpers/WidgetMenuBuilder.cs`
- Modify: `Core/Localization.cs`（字号 4 个 key）
- Modify: `tests/XamlLoadCheck/Program.cs`
- `tests/LangCheck/Program.cs` 不改（自动 parity）

**Interfaces：**
- Consumes：Task 2 的 `NoteFontScale` / `NoteData.FontScale`；Task 4 的 detail view。
- Produces：`WidgetMenuBuilder` 的 note 字号子菜单。

### Step 1（红）

1. `surface: the font scale scales the document and the edit box`：
   `data.FontScale = NoteFontScale.Large`；note `"# H\n\nbody"`；
   `surface.Attach(data, note)` → `ReadingBox.Document.FontSize == 12 * 1.2`（容差 0.01）；
   标题 `Paragraph.FontSize == NoteMarkdownRenderer.HeadingFontSize(1, 12 * 1.2)`；
   `TryBeginEditAt(2)` → `EditBox.FontSize == 12 * 1.2`。
2. `surface: changing the font scale during an edit keeps the text and caret`：
   编辑中设 `data.FontScale = 0.9` → `surface.Repaint()` → 框文本不变、仍编辑、
   `EditBox.FontSize == 12 * 0.9`。
3. `note: the card text does not follow the font scale`：
   data.FontScale = 1.2；home 卡面标题仍 14、时间 11、摘要 12。
4. `menu: the note font submenu checks the current step`：
   `WidgetMenuBuilder.Build(widget, null)` → 找到 `Menu_NoteFontSize`；
   三项文本 = `Note_FontSmall/Medium/Large`；默认 `Note_FontMedium` 勾选；
   点击 `Note_FontLarge` → `data.FontScale == 1.2`、`widget.UpdateUI` 被调用
   （或 view 的 `ReadingBox` 字号变化）、`OnDataChanged` 计数 1。
5. `menu: the folder menu has no note font submenu`：文件夹菜单不含 `Menu_NoteFontSize`。
- 运行 XamlLoadCheck → 失败（字号未接入 / 菜单不存在）即红。留档。

### Step 2（实现渲染）

`NoteSurfaceView`：

```csharp
private double BaseFontSize()
{
    return UiTokens.FontBody * NoteFontScale.Normalize(_data == null ? 1.0 : _data.FontScale);
}
```

- `BuildRenderStyle()`：`BaseFontSize = BaseFontSize()`。
- `ApplyEditTypography`：`EditBox.FontSize = BaseFontSize();`
  标题：`NoteMarkdownRenderer.HeadingFontSize(heading.Level, BaseFontSize())`；
  代码 / 引用：字号用 `BaseFontSize()`（字体族 / 缩进不变）。
- 整文编辑 / 追加：正文 `BaseFontSize()`。

### Step 3（实现菜单）

`WidgetMenuBuilder.Build` 在 note 分支：

```csharp
if (host.Data is NoteData note) menu.AddMenuItem(CreateNoteFontMenu(host, note));
```

```csharp
private static MenuItem CreateNoteFontMenu(IWidgetWindow host, NoteData data)
{
    var root = new MenuItem { Header = Localization.Get("Menu_NoteFontSize") };
    AddFontEntry(root, host, data, "Note_FontSmall", NoteFontScale.Small);
    AddFontEntry(root, host, data, "Note_FontMedium", NoteFontScale.Medium);
    AddFontEntry(root, host, data, "Note_FontLarge", NoteFontScale.Large);
    return root;
}

private static void AddFontEntry(MenuItem root, IWidgetWindow host, NoteData data,
    string key, double scale)
{
    var item = new MenuItem
    {
        Header = Localization.Get(key),
        IsChecked = Math.Abs(NoteFontScale.Normalize(data.FontScale) - scale) < 0.001
    };
    item.Click += (s, e) =>
    {
        data.FontScale = scale;
        DataChanged(host);
    };
    root.Items.Add(item);
}
```

- 菜单顺序：在 folder / todo 附录之后、`AddSeparator()` 之前。
- `Core/Localization.cs` 加 `Menu_NoteFontSize` / `Note_FontSmall` / `Note_FontMedium` /
  `Note_FontLarge` 三语（见 spec §7.6）。
- `NoteWidget.UpdateUI` 已有 `_view.Refresh()`；确保编辑中 `Refresh()` 走
  `_surface.Repaint()`，不重建列表、不丢文本。

### Step 4（绿）

- XamlLoadCheck / LangCheck / NoteMarkdownCheck / WidgetKindsCheck / MenuRenderCheck 全绿；
  build 0 警告。

### Step 5（暂存 + 提交）

- `git add Controls/NoteSurfaceView.xaml.cs Controls/NoteWidget.xaml.cs Helpers/WidgetMenuBuilder.cs Core/Localization.cs tests/XamlLoadCheck/Program.cs`
- 提交：`feat(note): scale the note text and add the font menu`

---

## Task 7: 文档 + 全量自检 + 执行记录

**Files：**
- Modify: `README.md`（英/中功能段 + 用法表）
- Modify: `CHANGELOG.md`（Unreleased）
- Modify: 本计划文件（追加「执行记录」）

- [ ] **Step 1：README**

  英文 / 中文功能段改为：
  - 笔记以 home 卡片列表呈现；点卡进入详情；标题右侧 `▾` 下拉可新建、回列表、快速切换；
  - 详情内点块就地编辑，点空白退出编辑，点文末空白追加新块；Esc/Backspace 回列表；
  - 右键组件菜单可选字号小/中/大；
  - 任务框 / 链接 / 快速插入 / 删除撤回保持。
  用法表对应行（中英镜像）：
  - `Open a note | Click a card; the title ▾ lists every note for quick switching`
  - `Create a note | Title ▾ → New, or right-click the home blank area`
  - `Back to the list | Title ▾ → Notes, or Esc / Backspace in the rendered detail`
  - `Edit a note | Click any block; click the blank area / Esc to render it back`
  - `Append a block | While editing, click the blank space below the last block`
  - `Change note text size | Right-click the widget → Font size → Small / Medium / Large`

- [ ] **Step 2：CHANGELOG**

  `Unreleased / 新增` 的笔记组件段落按新导航与字号重述；「修复」补一条
  「退出编辑后再次点击块无法进入编辑（真鼠标 Direct 路由）」；「计划 / 待办」删除
  已交付项，保留 `config.json.failed-*` 兼容条。

- [ ] **Step 3：全量自检**

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all
  ```

  预期 32/32（30 checks + installer 60 例）。

- [ ] **Step 4：执行记录**

  在本计划末尾追加：
  - 每任务 BASE / 实现提交 / 审查结论 / 修复轮；
  - Task 1 探针日志摘要与真鼠标冒烟结果（时间、窗口坐标、事件证据）；
  - 延迟 minor 清单与裁定；
  - 全量自检结果与部署冒烟结论。

- [ ] **Step 5：提交**

  `git add README.md CHANGELOG.md docs/superpowers/plans/2026-10-19-note-navigation-and-polish.md`
  → `docs: document the note navigation and polish`

---

## 收尾（控制者执行，非任务）

- [ ] 整支终审（spec 覆盖 / 数据 / 事件路由 / 测试口径）。
- [ ] 单一修复波；复审通过。
- [ ] `tools\Run-Checks.ps1 -Group all` 32/32。
- [ ] 征求用户同意后 `tools\deploy.ps1`，用户按 spec §8.2 真鼠标冒烟（尤其 bug 1、AppendZone、标题下拉、字号）。
- [ ] 用户确认后合并 main + 推送（合并 / 推送必须先问）；worktree 按收尾规范清理。

## Self-Review

- **Spec 覆盖**：§3 信息架构 = T4；§3.2 下拉 = T4；§4 空白 + AppendZone = T5；§5 bug = T1；
  §6 字号数据 = T2、渲染/菜单 = T6；§7 接口 = T3/T4/T5/T6；§8 测试 = 各任务；§9 风险 = 各任务红线 +
  收尾冒烟；§10 文档 = T7。
- **占位符扫描**：无 TBD；测试 pin 均给夹具与断言；探针脚本给了完整代码。
- **类型一致性**：`NoteFontScale` 常量 / `Normalize` 在 T2 定义、T6 消费；`TitleMenuEnabled` /
  `TitleMenuClicked` / `TitleMenuAnchor` 在 T3 定义、T4 消费；`TryBeginAppendEdit` 在 T5 定义；
  `ChromeTitle` / `IsHome` / `OpenNote` / `BuildTitleMenuContent` 在 T4 定义。
- **风险**：T4 最大（导航 + 下拉 + 旧 pins 重写）；执行记录按「红证据 → 实现 → 全绿」留档，
  审查重点 = 切换时的提交 / 空稿丢弃 / 标题刷新 / Popup 关闭焦点。
