# 笔记组件交互升级（就地工作台 · 块级行内编辑）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **状态：已获用户批准（2026-10-16：spec 审阅通过，含第二轮「块级行内编辑」改选）**。按 spec 口径执行。
> 执行方式：subagent-driven-development（每任务 = 实现子代理 TDD → 暂存不提交 → 控制者快照 + 审查包 → 修复循环 →
> 复审通过后由实现子代理按计划提交信息提交；冒烟由用户操作）。
> worktree：`.worktrees/note-interaction`；分支：`note-interaction`（基线 = 本计划 `docs(plans)` 提交在 main 的 SHA）。
> Spec：`docs/superpowers/specs/2026-10-16-note-widget-interaction-design.md`（本计划不再复述决策，只引 §号）。
> 子代理模型：**统一 `opencode-go/deepseek-v4.1-flash`**（实现与审查）；派发被中断时沿用「保留半成品 + 续用/RED 重建」预案。

**Goal：** 把笔记组件从「列表 → 阅读 → 编辑」三态整体重做为**就地工作台 + 块级行内编辑**：点卡片原地展开渲染全文；点某个块原地变源码编辑框、其余内容保持渲染、离开即渲染回去；全自动保存、零提交仪式；＋ 在列表顶部直接起笔。

**Architecture：** 新增 `Core/NoteBlockRanges.cs`（块→源码行范围的纯函数：范围 / 提取 / 拼接）；渲染器加行号挂载（附加属性）——
两者都为「点哪写哪」提供定位与回填底座。新的 `Controls/NoteSurfaceView`（面组件：渲染态单 RTB ⇄ 块编辑态
「前段/编辑框/后段」三段合成、节流拼接、插入菜单）取代旧的三态方法；`NotePanelView` 重写为列表唯一容器
（卡片两态、单展开、空稿判定、`FlushPendingEdit` 钩子）。数据格式与 Core 既有纯逻辑零改动。

**Tech Stack：** WPF / .NET Framework 4.8 / C# 7.3；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵 32 行不变）。

## Global Constraints

- C# 7.3 / net48，不引入新依赖；构建 0 警告。
- TDD：每任务先写失败 pin（编译失败或运行时失败均可）→ 最小实现 → 相关自检与 LangCheck 全绿。
- 三语 key 同步（zh/en/ja 同一提交内）；`LangCheck` 保持绿。本计划只**删除** 2 个退役 key
  （`UI_NoteSourceTab` / `UI_NotePreviewTab`），不新增。
- hex 字面量只许在 `Core/UiTokens.cs`（`UiTokensCheck` 会抓）；本改动不新增颜色值，一律走
  `UiTokens` / `ThemeManager` / 组件色。
- 自检内**禁止读写系统剪贴板**、**禁止驱动真实鼠标**（离屏 pin + 反射 + 合成事件，参照
  `RaiseNotePreviewKey` / `ClickNoteCard` 既有助手）；合成键盘只对离屏目标 `RaiseEvent`。
- 时间戳夹具一律显式传入；断言不得依赖 `DateTime.Now`（仅允许 `ModifiedAt >= 测试开始时刻` 这类比较）。
- 每个提交独立可编译、可回退；**先任务审查后由实现子代理按 pathspec 提交**。推送/合并必须先问用户。
- 离屏视角无法验证的项（真实焦点、滚动保持、排版接缝）如实标注为冒烟覆盖，不设假 pin。
- 每个实现子代理在红测步骤**留下失败证据**（编译错误原文或运行时失败输出摘要）在执行记录里。
- 既有环境事项：`MenuRenderCheck` 曾为观察项，v1.4.0 轮后稳定绿；若复发且非本分支回归，如实记录。

---

## Task T1: NoteBlockRanges（块范围纯函数）

**Files：**
- Create: `Core/NoteBlockRanges.cs`
- Modify: `tests/NoteMarkdownCheck/Program.cs`（`RunAll` 加 `BlockRanges();` + 新方法）

**Interfaces：**
- Produces（T4/T6 消费这些确切名字）：
  ```csharp
  public static class NoteBlockRanges
  {
      // 顶层块的行范围 [Start, End)（0 基，跨行自然序）：End = 下一块 Start（末块 = 总行数）。
      public static List<(int Start, int End)> RangesOf(string text);
      // 行号 → 范围下标（夹取最近的下方范围）；无块时 -1。
      public static int RangeIndexAtLine(string text, int line);
      // 提取行区间文本：按 '\n' 切、每行剥一个行尾 '\r'、'\n' 连接。
      public static string ExtractLines(string text, int start, int end);
      // 用 replacement 替换行区间：其余行原样保留（CRLF 不动）；越界钳位。
      public static string ReplaceLines(string text, int start, int end, string replacement);
      // 行数："" → 0；"a" → 1；"a\nb" → 2；"a\n" → 2（尾空行）。
      public static int LineCount(string text);
  }
  ```
- 语义要点（写进类注释）：范围归属含块间空行（`[Start, nextStart)`），首部空行不属于任何范围；
  `RangesOf` 由 `MarkdownParser.Parse` 的顶层块 `Line` 推导（与渲染 1:1 对齐）。

- [ ] **Step 1（红）：** `NoteMarkdownCheck` 加 `BlockRanges()` 方法与 pins：
  - `ranges: no blocks yield no ranges`（`""`、`"   \n\n"`）；
  - `ranges: a block spans to the next block`（`"# a\n\n- x\n- y"` → 2 条：`(0,2)`、`(2,4)`）；
  - `ranges: leading blank lines stay outside`（`"\n\n# a"` → `(2,3)`）；
  - `ranges: a multi-line paragraph is one range`（`"a\nb\nc"` → `(0,3)`）；
  - `ranges: a quote owns its blank tail`（`"> a\n> b\n\nx"` → `(0,3)`、`(3,4)`）；
  - `ranges: the trailing newline belongs to the last range`（`"# a\n"` → `(0,2)`）；
  - `ranges: the index clamps downwards`（上夹具：0→0、1→0、2→1、99→1；`"\n\n# a"`：0→-1、2→0；`""`→-1）；
  - `extract: lines are LF-joined with the CR stripped`（`"a\r\nb\r\nc"`：`[1,2)`→`"b"`、`[0,2)`→`"a\nb"`、`[1,1)`→`""`）；
  - `replace: a region splices in place`（`"a\nb\nc"`：`[1,2)` + `"x"`→`"a\nx\nc"`；`[1,2)` + `"x\ny"`→`"a\nx\ny\nc"`）；
  - `replace: an empty replacement removes the region`（`[1,2)`+`""`→`"a\nc"`；`[0,1)`+`""`→`"b\nc"`；全区间清空 → `""`）；
  - `replace: inserting into empty text`（`""`、`[0,0)`+`"a"`→`"a"`；+`"a\nb"`→`"a\nb"`）；
  - `replace: untouched lines keep their CRLF`（`"a\r\nb\r\nc"`、`[0,1)`+`"a"`→`"a\nb\r\nc"`）；
  - `replace: out-of-range clamps`（`"a\nb"`、`[-5,99)`+`"z"`→`"z"`；`[3,1)`+`"z"`→`"a\nb\nz"`）；
  - `replace: repeating a splice is idempotent`（同参应用两次，第二次原文不变）；
  - `line count: counts the empty text as zero`（`""`→0、`"a"`→1、`"a\nb"`→2、`"a\n"`→2）。
  运行 `dotnet run --project tests\NoteMarkdownCheck\NoteMarkdownCheck.csproj -c Debug --nologo` → 编译失败（`NoteBlockRanges` 不存在）即红。留档。

- [ ] **Step 2（实现）：** `Core/NoteBlockRanges.cs`（`using System; using System.Collections.Generic; using System.Text; using Kobold.Core;` 同程序集可省 using）：
  - 内部 `SplitRaw(text)` → `text.Split('\n')`（`""` 特判为空数组语义：以实现口径让 `LineCount("")==0` 且 `ExtractLines`/`ReplaceLines` 在空文本上行为与 pin 一致）；
  - `RangesOf`：`MarkdownParser.Parse(text)` → 顶层块按序取 `block.Line`，`End` = 下一块 `Line`（末块 = `LineCount`）；块间空行自然落进前一个范围；
  - `RangeIndexAtLine`：遍历范围，`Start <= line` 的最后一个；无 → -1；
  - `ExtractLines`：raw 行 `[start,end)` → 每行 `TrimEnd('\r')`（仅剥一个）→ `string.Join("\n", …)`；
  - `ReplaceLines`：raw 切分 → 钳位（`start<0→0`、`end<start→start`、`>count→count`）→ replacement 空串 = 零行（删除区间），否则按同规则切分剥 `\r` → 拼接组行 → `string.Join("\n", …)`；
  - `LineCount`：`text == null/""` → 0，否则 `SplitRaw(text).Length`。

- [ ] **Step 3（绿）：** NoteMarkdownCheck 全绿（既有 pins 不回归）；`dotnet build Kobold.csproj -c Debug --nologo` 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the note block range helpers`
  → `git add Core/NoteBlockRanges.cs tests/NoteMarkdownCheck/Program.cs`

---

## Task T2: 渲染器行号挂载（附加属性）+ 标题字号助手

**Files：**
- Modify: `Controls/NoteMarkdownRenderer.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`（渲染器 pins 组；`RunWithText` 助手适配 Span 递归）

**Interfaces：**
- Produces（T3/T4/T6 消费）：
  ```csharp
  // 挂到渲染元素上的行号（附加属性，默认 -1）；块级容器与段落行组都可用。
  public static readonly DependencyProperty SourceLineProperty;
  public static void SetSourceLine(DependencyObject element, int line);
  public static int GetSourceLine(DependencyObject element);
  // 标题字号阶梯（1..6 钳位；= baseSize * 1.6/1.4/1.25/1.12/1.05/1.0），供编辑框按块类型匹配字号。
  public static double HeadingFontSize(int level, double baseSize);
  ```
- 挂载点（逐块）：
  - `BuildHeading` → `Paragraph.SetSourceLine(heading.Line)`；
  - `BuildParagraph` → **每一行组包一层 `Span`**（`paragraph.Inlines.Add(span)`；组间照旧 `LineBreak`），
    `span.SetSourceLine(block.Line + i)`；
  - `BuildList` → 每个 item 的 `Paragraph` 挂 `item.Line`；
  - `BuildTask` → `Paragraph` 挂 `block.Line`；
  - `BuildQuote` → `Section` 挂 `block.Line`（内部块经递归各自挂绝对行号）；
  - `BuildCode` → `Paragraph` 挂 `block.Line`；
  - `BuildTable` → `Table` 挂 `block.Line`；
  - `BuildRule` → `BlockUIContainer` 挂 `block.Line`。
- 注意：`SourceLineProperty` 注册在静态类上（WPF 允许静态 owner，参照 BCL 的 `TextOptions`）。

- [ ] **Step 1（红）：** XamlLoadCheck 加：
  - `renderer: blocks carry their source lines`：夹具逐行编号
    `"one\ntwo\n\n# h\n\n- [ ] t\n\n> q\n\n```\nc\n```\n\n---\n\n| a | b |\n| --- | --- |\n| 1 | 2 |"`，
    断言：首段落的两个 `Span` 分别挂 0/1；标题段落挂 3；任务段落挂 5；引用 `Section` 挂 7 且其内段落挂 7；
    代码段落挂 9；分割线容器挂 13；表格挂 15（`GetSourceLine` 读取；`-1` 视为不合格）。
  - `renderer: the heading size helper follows the ladder`：`HeadingFontSize(1..6, 16)` 依次等于
    16×{1.6,1.4,1.25,1.12,1.05,1.0}；`HeadingFontSize(0, 16)` == `HeadingFontSize(1, 16)`；
    `HeadingFontSize(7, 16)` == `HeadingFontSize(6, 16)`。
  - 运行 → 编译失败（`SourceLineProperty` / `HeadingFontSize` 不存在）即红。留档。

- [ ] **Step 2（实现）：** 按 Interfaces 实现；`BuildHeading` 改用 `HeadingFontSize(level, ctx.BaseFontSize)`
  （阶梯数组成单一真源）；把 `RunWithText` 助手改为递归 Span 查找（仅测试侧改动，签名不变）。

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿（既有渲染器 pins 不回归——`InlineText`/`FindHyperlink` 已递归 Span）；
  build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(note): tag rendered blocks with their source lines`
  → `git add Controls/NoteMarkdownRenderer.cs tests/XamlLoadCheck/Program.cs`

---

## Task T3: NoteSurfaceView（面组件 · 渲染态）

**Files：**
- Create: `Controls/NoteSurfaceView.xaml`、`Controls/NoteSurfaceView.xaml.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`（新 surface pins 组 + 助手）

**Interfaces：**
- Produces（T4/T5/T6 消费这些确切名字）：
  ```csharp
  public partial class NoteSurfaceView : UserControl
  {
      public Action CollapseRequested;   // 浮动 ‹ 的点击
      public Action DataChanged;         // 数据写回后的宿主通知
      public void Attach(NoteData data, NoteItem note);  // 绑定并渲染（重置为渲染态）
      public void Repaint();             // 主题/语言刷新（编辑安全，T4 起生效）
      public void FocusReading();        // 渲染态的焦点目标（Backspace 路由）
  }
  ```
- XAML 骨架（命名元素是 pin 面，全用 `x:Name`）：根 `Grid SurfaceRoot`；`ScrollViewer SurfaceScroller`
  （Vertical Auto / Horizontal Disabled / PanningMode VerticalOnly / `Padding="0,24,0,0"`）内
  `StackPanel SurfaceStack`，依次：`RichTextBox ReadingBox`、`RichTextBox BeforeBox`（Collapsed）、
  `TextBox EditBox`（Collapsed，`AcceptsReturn` + `TextWrapping=Wrap`）、`RichTextBox AfterBox`（Collapsed）；
  三个 RTB 与旧 ReadingBox 同配（`IsReadOnly + IsDocumentEnabled`、内部滚动条 Disabled、透明、零边距）；
  浮动 `Border CollapseButton`（×22、圆角 6、左上，`MouseLeftButtonDown` → `CollapseRequested`）。
- 行为（本任务只做渲染态）：
  - `Attach`：存 `_data`/`_note`，切渲染态（ReadingBox 显示、三段隐藏），`RenderReading()`；
  - `RenderReading()`：`NoteMarkdownRenderer.Build(MarkdownParser.Parse(note.Text), BuildRenderStyle(),
    new NoteRenderCallbacks { ToggleTaskLine = ToggleTask, OpenLink = OpenLink })`；
  - `ToggleTask(line)`：`note.Text = NoteOps.ToggleTaskAt(...)` → `ModifiedAt = DateTime.Now` →
    `DataChanged?.Invoke()` → 记 `SurfaceScroller.VerticalOffset` → 重渲染 → `Dispatcher.BeginInvoke`
    恢复 offset（沿用旧阅读态写法）；
  - `OpenLink`：旧 `NotePanelView.OpenLink` 静态迁移（http/https → ShellExecute）；
  - `BuildRenderStyle()`：旧 `NotePanelView.BuildRenderStyle` 迁移（ThemeManager 令牌 + 组件色 +
    `ThemeManager.IsHighContrast`；字体 Segoe UI + `UiTokens.FontBody`）。
- 说明：`NotePanelView` 本轮**不动**（旧三态继续工作）；新组件与旧代码暂存重复，T6 统一退役。

- [ ] **Step 1（红）：** XamlLoadCheck 加（新助手：`SurfaceNamed` / `SurfaceReadingBox` / `NewSurface(data, note)`）：
  - `surface: attaching renders the document`（夹具 `"# H\n\npara\n\n- [ ] t"`；ReadingBox 可见、
    `Document.Blocks.Count == 3`、Before/Edit/After 均 Collapsed）；
  - `surface: a task click rewrites the source line and re-renders`（点 glyph Border `RaiseEvent(MouseLeftButtonUp)`
    → `note.Text` 含 `[x]`、`ModifiedAt >= 起点`、计数 `DataChanged` == 1、新文档 glyph 已翻转）；
  - `surface: a link carries its url`（`FirstHyperlink(ReadingBox.Document).NavigateUri` == 夹具 url）；
  - `surface: repaint re-renders the reading document`（改主题后 `Repaint()` → 文档非空、不抛）。
  运行 → 编译失败（类型不存在）即红。留档。

- [ ] **Step 2（实现）：** 按 Interfaces 新建两个文件（XAML 属性照抄旧 `ReadingBox` 的写法与
  `TextOptions.TextFormattingMode="Display"`；类注释注明「面组件：渲染态 ⇄ 块编辑态（T4）、插入菜单（T5）」）。

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿（含既有笔记 pins——面板未动）；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the note surface view`
  → `git add Controls/NoteSurfaceView.xaml Controls/NoteSurfaceView.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## Task T4: 块编辑态（点哪写哪 / 节流拼接 / 离开渲染）

**Files：**
- Modify: `Controls/NoteSurfaceView.xaml.cs`（XAML 微调：EditBox 事件挂接）
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces（T6 消费）：
  ```csharp
  public bool IsEditing { get; }             // 块编辑会话（私有 _editing 支撑）
  public bool TryBeginEditAt(int line);      // 渲染态点块：进入编辑（行→块范围→三段合成）；无块 false
  public void BeginEditWhole();              // 新笔记：整文为编辑区域
  public void LeaveEdit();                   // 提交 + 渲染回去（幂等）
  public void Flush();                       // 立即拼接节流中未落盘的区域（不改编辑状态；幂等）
  public void FocusEditBox();
  ```
- 私有：`_editStart`、`_editEnd`（区域行范围）、`_beforeBlocks` / `_afterBlocks`（入场时的前后段 AST，
  Repaint 复用）、`_throttle`（`DispatcherTimer`，400ms，tick = `CommitRegion()`）、`_editEntryText`
  （入场时 EditBox 文本，**提交守卫**：框文本未变则零接管）。
- 核心逻辑：
  - **点块进入（`ReadingBox.MouseLeftButtonUp` 挂接）**：`e.Handled` 为真 → 跳过（任务 glyph 已自处理）；
    从 `e.OriginalSource` 上溯：命中 `Hyperlink` → 跳过；读到 `NoteMarkdownRenderer.SourceLineProperty`
    （或任务 glyph 的 `Tag`）→ 该行 `TryBeginEditAt(line)`；**选择非空（拖选后松手）→ 跳过**；
    无命中 → 不动作（块级兜底由 `RangeIndexAtLine` 的夹取实现）；
  - `TryBeginEditAt(line)`：`RangesOf` + `RangeIndexAtLine` 取范围（<0 → false）→ 记 `_editStart/_editEnd`
    → `EditBox.Text = ExtractLines(note.Text, start, end)`、`_editEntryText = EditBox.Text`
    → `MarkdownParser.Parse(note.Text)` 切块：`BeforeBox.Document = Build(blocks[0..i), …, null)`、
    `AfterBox.Document = Build(blocks[(i+1)..], …, null)`（**静态渲染：回调为空**）
    → `ApplyEditTypography(blocks[i])` → 可见性切换（ReadingBox 收，三段开）→ `FocusEditBox()`
    → 光标 = `EditBox.GetCharacterIndexFromLine(line - _editStart)`（-1 钳到 0/文末）；
  - `BeginEditWhole()`：`_editStart=0; _editEnd=LineCount(text)`；框文本 = 全文；前/后段空文档；含空文本；
  - `CommitRegion()`：若 `EditBox.Text == _editEntryText` → 直接返回（守卫）；否则
    `var updated = NoteBlockRanges.ReplaceLines(note.Text, _editStart, _editEnd, EditBox.Text)`；
    `updated != note.Text` → 写回 + `ModifiedAt = Now` + `DataChanged()`；
    `_editEnd = _editStart + NoteBlockRanges.LineCount(EditBox.Text)`；`_editEntryText = EditBox.Text`
    （重复提交幂等）；
  - `LeaveEdit()`：`Flush()` → `_editing=false` → 渲染态（ReadingBox 重渲染 + 三段隐藏；offset
    捕获/恢复同 T3 先例）；`Attach` 时重置全部编辑状态；
  - `Repaint()`：编辑中 → 重建 style、前后段文档（用 `_beforeBlocks/_afterBlocks`）、EditBox 字色/字号
    重新应用，**不碰 EditBox.Text 与编辑状态**；渲染态 → `RenderReading()`；
  - `ApplyEditTypography(MdBlock block)`：标题 → `FontSize = HeadingFontSize(level, UiTokens.FontBody)` +
    SemiBold；代码块 → Consolas 族（与渲染器同族字符串）；引用 → `Foreground = Muted` + 左缩进 12；
    其余 → 正文大小/字体。
- 挂接：`EditBox.TextChanged="EditBox_TextChanged"`（重启 `_throttle`）；tick 在构造函数里接 `CommitRegion`。

- [ ] **Step 1（红）：** XamlLoadCheck 加（助手：`SurfaceEditBox/SurfaceBeforeBox/SurfaceAfterBox`、
  `InvokeNoteSurface(surface, name, args)` 反射调用。**区域含块间空行（spec §5.4）**：段落夹具
  `"# h\n\none\ntwo\n\n- x"` 的段落范围为 `[2,5)`，框文本带尾空行 `"one\ntwo\n"`，属预期）：
  - `surface: entering edit at a line swaps in the three panes`（夹具同上；`TryBeginEditAt(3)` → `IsEditing`；
    `EditBox.Text == "one\ntwo\n"`；Before 1 块、After 1 块；ReadingBox 收）；
  - `surface: the caret lands on the clicked line`（`TryBeginEditAt(3)` → `EditBox.GetLineIndexFromCharacterIndex(EditBox.CaretIndex) == 1`）；
  - `surface: a block-typed edit box matches the rendering`（标题块 → FontSize == `HeadingFontSize(1, UiTokens.FontBody)` 且 SemiBold；
    代码块 → Consolas；正文 → 基准大小）；
  - `surface: typing schedules the throttle`（改 `EditBox.Text` → `_throttle.IsEnabled == true`；数据**尚未**变）；
  - `surface: the throttle tick commits the region`（反射 `Throttle_Tick(null, EventArgs.Empty)` →
    `note.Text` 拼接生效、`ModifiedAt >= 起点`、`DataChanged` 计 1）；
  - `surface: flush commits without leaving`（改文本 → `Flush()` → 数据已写、`IsEditing` 仍真）；
  - `surface: leaving the edit splices and re-renders`（改文本 → `LeaveEdit()` → `IsEditing` 假、
    ReadingBox 显示、文档含新文本）；
  - `surface: leaving without changes keeps text and time`（不改 → `LeaveEdit()` → 文本/时间不变、`DataChanged` 计 0）；
  - `surface: entering edit on an empty note fails`（`""` 文本 → `TryBeginEditAt(0) == false`）；
  - `surface: clicking a tagged line enters its edit`（`"one\ntwo"`；对第二行 `Span` `RaiseEvent(MouseLeftButtonUp)`
    → 编辑态、框文本整段、光标行 1；**红测若揭示内容元素事件不达 RTB，改用对 `ReadingBox` 的等价触发路径并在执行记录注明**）；
  - `surface: clicking another block switches the edit`（编辑中 → 对另一块的 `Span` 触发 → 旧区域已提交、
    框文本为新区域、`IsEditing` 仍真）；
  - `surface: an already-handled click does not enter the edit`（任务 glyph 点击 → `IsEditing` 假、`[x]` 已落）；
  - `surface: begin whole-text edit covers the full text`（`"one\ntwo"` → `BeginEditWhole()` →
    `EditBox.Text == "one\ntwo"`、Before/After 0 块）；
  - `surface: the before and after panes are inert`（Before 里的 glyph 点击 → 数据不变、仍编辑态）；
  - `surface: repaint during edit keeps the box text`（改文本（未 tick）→ `Repaint()` → 框文本不变、仍编辑态、三段文档在）；
  - `surface: two splices converge`（同文本 `Flush()` 两次 → 数据/时间不变、`DataChanged` 计 1）。
  运行 → 编译失败（方法不存在）即红。留档。

- [ ] **Step 2（实现）：** 按 Interfaces 实现（拼装顺序注意：先设 `EditBox.Text` 再算光标；`EditBox.Focus()`
  放在布局之后不保证，pin 不依赖焦点）。

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(note): edit note blocks in place`
  → `git add Controls/NoteSurfaceView.xaml Controls/NoteSurfaceView.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## Task T5: 插入菜单迁移到面组件

**Files：**
- Modify: `Controls/NoteSurfaceView.xaml.cs`（把旧 `NotePanelView` 的菜单方法整段搬来并适配）
- Modify: `tests/XamlLoadCheck/Program.cs`（T15 的 8 条 pin 迁移为 surface 版本；旧面板版暂留待 T6 删）

**Interfaces：**
- Produces（私有，pin 走行为黑盒）：`FillEditorMenu(ContextMenu)` / `BuildEditorMenu()` /
  `CommandEntry(string, ICommand)` / `InsertEntry(string, Func<string,int,int,(string,int,int)>)` /
  `ApplyEditorOp(...)`（`BeginChange/EndChange` 单撤回单元 + 全选替换 + 光标回填 + `Focus()`）。
- 挂接：入场（`TryBeginEditAt` / `BeginEditWhole`）时 `EditBox.ContextMenu = BuildEditorMenu()`；
  `EditBox.ContextMenuOpening` → 编辑态重建、非编辑态置 null（旧 `EditingBox_ContextMenuOpening` 语义平移）。
- 菜单结构与三语 key **零变化**（标准四命令 + 标题子菜单 1–6 + 格式子菜单 + 平铺七项；`MenuBuilder.Prepare(menu, _data.Color)`）。

- [ ] **Step 1（红）：** XamlLoadCheck 加 8 条 surface 版 pins（沿用旧断言语义，入口换成
  「`NewSurface` + `BeginEditWhole`（或 `TryBeginEditAt`）后取 `EditBox.ContextMenu`」）：
  1. `surface menu: the standard four are commands`（前四项 Command 为 Cut/Copy/Paste/SelectAll，仅断言不触发）；
  2. `surface menu: the heading submenu writes a level`（第 3 项 click → `"### x"`）；
  3. `surface menu: the same list twice removes it`；
  4. `surface menu: the table entry lays out the skeleton`（夹具 `"x"`、光标行尾 → 逐字符等于
     `"x\n\n| 列 1 | 列 2 |\n| --- | --- |\n|  |  |"`、光标在首个体格）；
  5. `surface menu: wrapping a selection bolds it`（选 `"x"` → `"**x**"` 且选区罩 `x`）；
  6. `surface menu: an empty selection inserts the placeholder`；
  7. `surface menu: an insert is undoable with the box's own undo`（`CanUndo` / `Undo()` 回原文本）；
  8. `surface menu: the menu follows the widget colour`（MenuBuilder 淡染一处，沿用旧 pin 语式）。
  运行 → 编译失败（无 `ContextMenu` 行为入口即运行时失败亦可）即红。留档。

- [ ] **Step 2（实现）：** 从 `Controls/NotePanelView.xaml.cs` 的 `#region Editor quick-insert menu (T15)`
  整段搬移到 `NoteSurfaceView.xaml.cs`（改名 `EditBox_ContextMenuOpening`），`_editingBox` → `EditBox`、
  `_data.Color` 不变；旧面板代码**暂不删除**（避免半途编译断链，T6 统一退役）。

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿（旧面板菜单 pins 与新 surface pins 并行通过）；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(note): move the insert menu onto the surface`
  → `git add Controls/NoteSurfaceView.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## Task T6: NotePanelView 重写（就地工作台编排）+ 旧三态退役 + 三语键清理

**Files：**
- Modify: `Controls/NotePanelView.xaml`（重写）、`Controls/NotePanelView.xaml.cs`（重写）
- Modify: `Controls/NoteWidget.xaml.cs`（`CommitPendingEdit` → `FlushPendingEdit`）
- Modify: `Core/Localization.cs`（删 `UI_NoteSourceTab` / `UI_NotePreviewTab` 各三语）
- Modify: `tests/XamlLoadCheck/Program.cs`（删旧 T13/T14/T15 面板 pins 与死助手；加编排 pins）

**Interfaces：**
- `NotePanelView` 保留/产生（pin 面）：私有 `_expandedId`（string）、`_surface`（NoteSurfaceView，
  单实例建一次、随卡承载）；`Attach(NoteData, NoteWidget)`、`Refresh()`（编辑中走 `Repaint`、否则重建列表）、
  `public bool TryUndo()`（不变）、`public void FlushPendingEdit()`（= `_surface.LeaveEdit()` + 空稿判定 +
  需要时通知；幂等）、私有 `Expand(NoteItem)` / `CollapseExpanded()` / `StartNewNote()` / `InvokeDelete(NoteItem)`（不变）/
  `BuildBlankMenu()`（不变）/ `RebuildList()` / `Root_PreviewKeyDown` / `Root_MouseLeftButtonDown`。
- XAML：根 `Grid Root` + `PreviewKeyDown`；`Grid ListHost`（`Border AddPill` + `ScrollViewer ItemsScroller`
  > `StackPanel ItemsPanel`）。ReadingHost/EditingHost/段控整体删除。
- 编排行为：
  - `RebuildList()`：`ItemsPanel.Children.Clear()` → `NoteOps.SortForDisplay` 逐条：`_expandedId` 命中 →
    加**常驻宿主** `Border _surfaceHost`（Child 恒为 `_surface`，一次设置、不再换父；Margin/Padding 沿用卡面、
    圆角 `UiTokens.RadiusControl`；若 `!_surface.IsEditing` → `_surface.Attach(_data, note)` 刷新渲染）；
    其余 → 卡面（现有 `BuildCard` 结构原样保留：标题/时间/摘要/进度/悬浮/行菜单 MouseLeftButtonUp → `Expand`）；
    `_expandedId` 指向的笔记已不存在 → 清空该字段；
  - `Expand(note)`：已有展开先 `CollapseExpanded()` → `_expandedId = note.Id` → `RebuildList()`（宿主挂上、
    Attach 在其中）→ `_surface.FocusReading()`；
  - `CollapseExpanded()`：`_surface.LeaveEdit()`（冲刷 + 渲染回去，编辑态清空）→ 空稿判定
    （`IsNullOrWhiteSpace(note.Text)` → 从 `Notes` 移除 + `_host.NotifyDataChanged()`）→ `_expandedId = null` →
    `RebuildList()`；
  - `StartNewNote()`：`Notes.Insert(0, new NoteItem{Guid, "", Now, Now})` → `_expandedId = note.Id` →
    `RebuildList()`（Attach 在渲染态）→ `_surface.BeginEditWhole()` → `_surface.FocusEditBox()`（不 notify，
    沿用 T14 口径：首次提交/离场才落通知）；
  - `Root_PreviewKeyDown`：`e.Handled` 优先；编辑中 + Esc → `_surface.LeaveEdit()` + Handled；
    非编辑 + 已展开 + (Esc|Backspace) → `CollapseExpanded()` + Handled；
  - `Root_MouseLeftButtonDown`：`e.Handled` 跳过；从 `e.OriginalSource` 上溯找卡宿主/卡面 `Border`
    （或 AddPill 祖先）→ 命中即跳过；未命中且 `_expandedId != null` → `CollapseExpanded()`；
  - `Root.ContextMenuOpening`：`_expandedId == null` 时赋 `BuildBlankMenu()`，否则置 null（沿用旧写法，
    把 `_state == List` 判定换成展开判定）；
  - `Refresh()`：`_surface.IsEditing` → `_surface.Repaint()` 后返回；否则 `RebuildList()`（展开态由
    RebuildList 内部的 Attach 保持渲染刷新）；
  - `_surface.CollapseRequested` → `CollapseExpanded()`；`_surface.DataChanged` → `_host?.NotifyDataChanged()`
    （不重建列表——收起时才重建）；
  - `InvokeDelete`：开头防御 `if (note.Id == _expandedId) CollapseExpanded();`。
- `NoteWidget`：`Closing` 与 `OnPanelHidden` 两处 `CommitPendingEdit()` 改名 `FlushPendingEdit()`。

- [ ] **Step 1（红）：** XamlLoadCheck 同步删旧、加新（旧 T14 组 13 条、T15 组 8 条、T13 组 6 条面板版删除；
  死助手 `NoteState` / `NoteReadingHost` / `NoteBackButton` / `NoteEditingHost` / `NoteSourceBox` /
  `NotePreviewBox` / `NotePreviewToggle` / `NoteSegmentLabel` / `ClickNoteSegment` / `ClickNoteEditButton` /
  `ClickNoteHeaderButton` / `InvokeNoteEdit` / `NoteListHost` 删除；保留 `ClickNoteCard` /
  `ClickNoteAdd` / `RaiseNotePreviewKey` / `NoteNamed` / 卡片系列 / `InvokeNoteTryUndo` / `InvokeNoteDelete` /
  `InvokeNoteBlankMenu` / `NewNote`）。新助手：`NoteSurface(view)`（字段 `_surface`）、`NoteExpandedId(view)`
  （字段 `_expandedId`）、`ClickNoteCollapse(surface)`（`FindName("CollapseButton")` 触发）、
  `SurfaceReadingBox/EditBox/BeforeBox/AfterBox`、`NoteHostAt(view, index)`（ItemsPanel 子项 Border）。
  新 pins（夹具全部显式时间戳）：
  1. `note: a card click expands the surface in place`（两笔记；点第 0 卡 → `NoteExpandedId` 对、第 0 项宿主
     Border 的 Child 是 `_surface`、ReadingBox 文档非空、第 1 项 Child 仍是卡面 StackPanel）；
  2. `note: expanding another card collapses the first with its edit flushed`（展开 A → `TryBeginEditAt(0)` →
     改框文本 → 点 B 卡 → A 文本已落、`NoteExpandedId == B`、A 卡面标题已更新）；
  3. `note: the collapse button returns to the face`（`ClickNoteCollapse` → `_expandedId == null`、全部卡面、
     列表顺序仍按 ModifiedAt）；
  4. `note: Backspace in the rendered surface collapses`（`RaiseNotePreviewKey(widget, SurfaceReadingBox, Key.Back)`
     → 收起 + `Handled`）；
  5. `note: Esc in the rendered surface collapses`（同上 Key.Escape）；
  6. `note: Esc leaves a block edit without collapsing`（展开 → 编辑 → `RaiseNotePreviewKey(widget, SurfaceEditBox, Key.Escape)`
     → `IsEditing` 假、仍展开、文本已提交）；
  7. `note: clicking a block enters its edit at that line`（夹具 `"one\ntwo"`；对第二行 `Span` `RaiseEvent(MouseLeftButtonUp)`
     → 编辑态、框文本整段、光标行 1；**红测若揭示内容元素事件不达 RTB，改用对 `ReadingBox` 事件的等价触发路径并在执行记录注明**）；
  8. `note: a task glyph click toggles without entering the edit`（点 glyph → `[x]` 落数据、`IsEditing` 假）；
  9. `note: the add pill starts a whole-text edit`（空数据 → `ClickNoteAdd` → 1 条在索引 0、展开即新条、编辑态、
     空框、前/后段 0 块）；
  10. `note: the blank-area menu starts the same edit`（`InvokeNoteBlankMenu` → click → 同上核心断言）；
  11. `note: an empty new note is discarded on collapse`（`ClickNoteAdd` → `ClickNoteCollapse` → 0 条、
      `widget.OnDataChanged` 计数 ≥1）；
  12. `note: emptying an existing note discards it on collapse`（展开 → 编辑 → 框文本 `"   "` → 收起 →
      该条消失 + 通知）；
  13. `note: hiding the panel commits and leaves the edit`（编辑 → 改文本 → `widget.HidePanel()` →
      文本已落、`IsEditing` 假）；
  14. `note: a refresh during an edit keeps the text`（编辑 → 改文本 → `view.Refresh()` → 框文本不变、仍编辑态）；
  15. `note: the order follows the latest edit after collapse`（编辑较旧条 → 收起 → 其卡面在最上、标题已新）；
  16. `note: toggling a task rewrites the source line and re-renders`（展开渲染态 → 点 glyph → `[x]`、
      `ModifiedAt >= 起点`、新文档翻转）；
  17. `note: a rendered link carries its url`（`FirstHyperlink(SurfaceReadingBox(...).Document)` == url）。
  运行 → 编译失败 / 大面积旧 pin 失败即红（面板重写与 pin 重写同任务完成，红证据 = 旧实现的失败输出）。
  留档。

- [ ] **Step 2（实现）：** 重写 `Controls/NotePanelView.xaml(.cs)`（删除三态代码、菜单代码、段控；
  按 Interfaces 编排）；改 `NoteWidget.xaml.cs` 两处钩子名；删 `Core/Localization.cs` 六行
  （zh 32-33 / en 230-231 / ja 428-429 一带，逐语言确认后删）。

- [ ] **Step 3（绿）：** XamlLoadCheck + LangCheck 全绿；`WidgetKindsCheck` / `MenuRenderCheck` 不回归；
  build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(note): rebuild the note panel as an in-place workbench`
  → `git add Controls/NotePanelView.xaml Controls/NotePanelView.xaml.cs Controls/NoteWidget.xaml.cs Core/Localization.cs tests/XamlLoadCheck/Program.cs`

---

## Task T7: 文档（README 英/中 + CHANGELOG）

**Files：**
- Modify: `README.md`（英文功能段 + 中文功能段 + 两张用法表）
- Modify: `CHANGELOG.md`（「Unreleased」与「计划 / 待办」）

- [ ] **Step 1：** README：把笔记组件条目重述为**就地工作台 + 块级行内编辑**——点卡片原地展开渲染全文；
  点某个块原地编辑（其余内容保持渲染）、离开即渲染回去；自动保存无提交按钮；＋ 直接起笔；任务框/链接
  在渲染态可点；删除 + Ctrl+Z。用法表对应行重写（中英镜像）：
  `Create a note | Island ＋ → Note; the panel's ＋ row`、
  `Read a note | Click a card; click the blank area / ‹ (Esc, Backspace) to close`、
  `Edit a note | Click any block of the note; Esc renders it back`、
  `Tick a task in a note | Click the box in the rendered note`、
  `Insert markdown fast | Right-click the source box while editing a block`。
  「数据」小节兼容句不变（数据格式未动）。
- [ ] **Step 2：** CHANGELOG：`Unreleased / 新增` 的笔记组件段落按新交互改写（未发布特性直接重述，
  不写「变化」）；「计划 / 待办」删除交互升级一条（本轮交付），保留 `config.json.failed-*` 一条。
- [ ] **Step 3（提交，1 笔）：** `docs: document the note interaction upgrade`
  → `git add README.md CHANGELOG.md`

---

## 收尾（控制者执行，非任务）

- [ ] `tools\Run-Checks.ps1 -Group all`（32 行）全绿；`MenuRenderCheck` 若复发按观察项裁定；config-pin 误报先重跑。
- [ ] 计划文件追加"执行记录"，提交 `docs: record the note interaction upgrade execution`。
- [ ] `tools\deploy.ps1` → **用户手动冒烟**（spec §8.2 十二条）：①展开/收起；②点哪写哪（光标行）；③块编辑连续流；
  ④接缝排版与高度变化；⑤任务框/链接（含编辑中 inert）；⑥自动保存全路径；⑦空稿；⑧快速插入菜单；⑨删除撤回；
  ⑩三主题三语高对比度；⑪文件夹/待办回归抽查；⑫全量自检。
- [ ] 用户确认后：合并 main + 推送（发布提交或 HEAD 带 `[ci]` 视需要）；worktree 按收尾规范清理。

---

## Self-Review

- **Spec 覆盖**：§3 结构 = T6；§4 面两态 + 点哪写哪 + 手势表 = T3/T4/T6；§5 保存/冲刷 = T4（节流与幂等）
  + T6（FlushPendingEdit / 空稿）；§5.4 `NoteBlockRanges` = T1；§6 新建流 = T6；§7 退役/不变 = T6（删除面
  三态与两 key）/ T4（渲染器只加挂载）；§8 测试 = 各任务 pins + T7 文档；§9 风险 = 各任务红线与收尾冒烟。
- **占位符扫描**：无 TBD；所有 pin 均有夹具与断言口径；反射入口全部给了字段/方法名。
- **类型一致性**：`NoteBlockRanges` 五函数在 T1 定义、T4/T6 消费同名；`SourceLineProperty` / `HeadingFontSize`
  在 T2 定义、T4 消费；`NoteSurfaceView` 公开面（`IsEditing` / `TryBeginEditAt` / `BeginEditWhole` /
  `LeaveEdit` / `Flush` / `Repaint` / `FocusReading` / `FocusEditBox` / `CollapseRequested` / `DataChanged`）
  在 T3/T4 定义、T5/T6 消费；`_expandedId` / `_surface` / `FlushPendingEdit` 贯穿 T6 与 pin 助手。
- **风险**：① Span 包装改变段落内联结构——`InlineText`/`FindHyperlink` 已递归，`RunWithText` 在 T2 适配；
  ② 合成事件自 Span 到 RTB 的路由若不通——T4 的点击 pin 已给替代路径（面板集成 pin 同步观察）；
  ③ T6 为最大任务（重写 + 退役 + pins），执行记录里按「红证据 → 实现 → 全绿」留档，审查重点列在任务内；
  ④ 离屏无法验证的焦点/滚动/接缝全部移交冒烟（收尾清单）。
