# Kobold 笔记组件导航与交互打磨设计（详情页 · 标题下拉 · 空白语义 · 字号）

> **状态**：**待用户审阅**（2026-10-19 逐条确认后落文；审阅通过后按 writing-plans 出实施计划）。
> **基线**：`main` @ `64b91ac`（上一轮「就地工作台 + 块级行内编辑」已合并、已推送）。
> **关系**：本 spec 是 2026-10-16 `note-widget-interaction-design.md` 的增量修订版；
> 未修订的条款继续有效。本 spec 明确覆盖旧 spec §3.1（列表唯一容器）、§3.2（展开编排）、
> §3.3（展开面 chrome）、§4.4（手势表）中与「详情页 / 标题下拉 / 空白语义」冲突的部分。
> **代码走查 2026-10-19**：`Controls/NotePanelView.xaml(.cs)`、`Controls/NoteSurfaceView.xaml(.cs)`、
> `Controls/NoteWidget.xaml(.cs)`、`Controls/WidgetChrome.xaml(.cs)`、`Helpers/WidgetMenuBuilder.cs`、
> `Helpers/ContextMenuBuilder.cs`、`Core/NoteData.cs`、`Core/NoteOps.cs`、`Core/AppConfig.cs`、
> `Core/Localization.cs`、`Controls/NoteMarkdownRenderer.cs`、`tests/*`。
> **本轮定性**：信息架构从「列表内手风琴」改为「home 列表 ⇄ detail 详情」；标题下拉承担
> 新建 / 回 home / 快速切换；空白点击语义收紧；修一个 Direct 路由导致的真鼠标 bug；
> 新增每组件独立的字号档位。数据格式只新增一个可选字段，Core 既有纯逻辑零改动。

## 0. 决策记录（2026-10-19 用户逐条确认）

1. **导航 = 详情页**：home 是卡片列表；点卡进入 detail，笔记占满内容区；home 列表在
   detail 下隐藏。标题下拉负责快速切换与回 home。
2. **顶部入口收进标题下拉**：移除列表顶部常驻的 `＋` 行与展开面左上角的浮动 `‹`。
   标题右侧新增 `▾`；**只有 `▾` 可点**，标题文字仍是拖拽区。
3. **标题显示**：home 显示组件名；detail 显示当前笔记名（空标题显示「未命名」）。
4. **下拉结构**：上下两块——上块动作组（`＋ 新建`、`‹ 笔记列表`；home 态隐藏后者），
   下块笔记列表（按最近修改排序、仅标题、当前条打勾 + 高亮、超高滚动）。
5. **切换语义**：点其它笔记 = 提交当前编辑 → 打开目标渲染态；点当前条只关下拉；
   `＋` = 提交当前 → 新建并整文编辑；`‹` = 提交当前 → 回 home（空稿丢弃）。
6. **编辑中打开下拉**：保持编辑会话；关闭下拉后焦点回编辑框；只有真正选择条目才提交。
7. **空白点击语义**（编辑态）：块间空白 / 卡片内边距 / 面板空白 = 提交 + 退出编辑、
   仍留 detail；渲染态点这些空白 = 不动作（不再收起）。
8. **新块编辑的触发**：只有编辑态把光标点到「文末空白」才追加新块并进入其编辑；
   渲染态点文末空白不动作。块间空白不插入新块。
9. **字号**：每组件独立；`0.9 / 1.0 / 1.2` 三档；默认 `1.0`；只影响渲染全文与行内编辑框，
   不影响卡片标题 / 时间 / 摘要；入口在组件右键菜单。
10. **Bug 修复纪律**：先临时探针 + 脚本驱动一次真实鼠标复现，拿到 `OriginalSource` /
    路由证据后再修；修复后必须再次真鼠标冒烟。测试本身仍禁止驱动真实鼠标。

---

## 1. 目标与成功标准

| # | 目标 | 验收口径 |
|---|---|---|
| 1 | 列表与详情分离 | home 只显示卡；点卡进入 detail；detail 只显示当前笔记；不会同时出现列表和内容。 |
| 2 | 快速切换 | 任意状态点标题 `▾`，两步内可切到任意笔记 / 新建 / 回 home；当前条有勾选高亮。 |
| 3 | 空白语义可预期 | 编辑态点空白只退出编辑（除文末空白外），不收起、不误建块；渲染态空白不动作。 |
| 4 | 真鼠标可重进编辑 | Esc / 点空白退出后，再点任意块都能重新进入编辑；真鼠标复现通过。 |
| 5 | 字号可调 | 每个笔记组件可在右键菜单选小/中/大；渲染与编辑框同步缩放；卡片摘要不变；重启后保持。 |
| 6 | 零回归 | 全量自检维持 32/32；文件夹 / 待办 / 菜单渲染 / 配置安全不回归；0 警告。 |

## 2. 非目标（本轮不做）

- 下拉的完整键盘导航（方向键 / Home / End / 输入筛选）；v1 只保证鼠标点击、Esc 关闭、
  点外部关闭。
- 在块与块之间插入新块；新块只支持文末追加。
- 笔记级字号（每条笔记不同）或全局字号默认；字号只跟组件实例走。
- 卡片标题 / 时间 / 摘要 / 空态提示跟随字号缩放。
- 列表搜索、排序切换、标签、导出、拖拽重排、多笔记并排。
- 详情页与 home 之间的动画过渡；保持即时切换。
- 数据格式迁移 / 旧版本兼容承诺：只新增一个可选字段，旧配置缺字段按中处理；
  旧版 Kobold 读到新字段会忽略，保存时可能丢弃该字段（不视为破坏）。
- 上一轮非目标维持：逐字符塌缩、块内逐键实时渲染、可拖分栏、失焦自动收起等。

---

## 3. 信息架构

### 3.1 home ⇄ detail

```
home                                   detail
┌ 标题 = 组件名  ▾ ┐                   ┌ 标题 = 当前笔记名  ▾ ┐
│  A 卡面           │                   │  笔记内容（占满）    │
│  B 卡面           │  ── 点卡 ──▶      │  渲染 ⇄ 块编辑      │
│  C 卡面           │                   │                     │
└──────────────────┘                   └─────────────────────┘
```

- 状态仍由 `_expandedId` 表达：`null` = home；非空 = detail。
- **home**（`Controls/NotePanelView` 的 `HomeHost`）：`ItemsScroller` > `ItemsPanel`，
  逐条 `NoteOps.SortForDisplay` 生成卡面。**不再有 `AddPill` 行**。
- **detail**（`DetailHost`）：唯一子元素是常驻宿主 `_surfaceHost`（Child 恒为同一个
  `NoteSurfaceView`，只设置一次、不换父）。笔记面铺满内容区，`SurfaceScroller` 成为
  唯一滚动区。
- 空列表：home 显示居中提示文字（`UI_NoteEmptyHome`，次要色、可换行）；
  有卡时隐藏。下拉里无笔记时只显示动作组，不额外放占位项。
- 进入 detail：`SurfaceScroller.ScrollToTop()`；从 detail 回 home 时尽量保持 home 列表
  之前的滚动位置（捕获/恢复失败时回到顶部即可，不设假 pin）。
- 详情页不显示卡片标题 / 时间 / 摘要 / 任务进度；这些只在卡面出现。

### 3.2 标题与标题下拉

**标题文本**

- `WidgetChrome.Title` 仍由宿主设置：home = `WidgetData.Name`；detail = `NoteOps.TitleOf(note.Text)`，
  空白时 = `Localization.Get("UI_NoteUntitled")`。
- 长标题继续 `TextTrimming=CharacterEllipsis`；detail 下 `TitleText.ToolTip` 显示完整笔记名，
  home 清空 tooltip（避免盖住组件的既有 tooltip 语义）。
- 编辑中标题随 400ms 拼接节拍更新；离开编辑 / 收起 / 切换时立即更新。
- 标题文字本身不响应点击，仍是 `Header` 的拖拽区。

**`▾` 触发器**

- `WidgetChrome` 新增可选的标题菜单能力：`TitleMenuEnabled`（默认 false）、
  `TitleMenuClicked` 事件、`TitleMenuAnchor`（标题区域的 `FrameworkElement`）。
- NoteWidget 把 `TitleMenuEnabled = true`；文件夹 / 待办保持 false，视觉与行为零变化。
- `▾` 是标题右侧的小按钮（`TitleChevron`），`Cursor=Hand`；按下时 `Handled=true`，
  阻止 `Header` 开始拖拽；抬起时触发 `TitleMenuClicked`。
- 打开 / 关闭为 toggle：下拉已开时再点 `▾` 关闭。

**下拉内容（每次打开重建）**

```
┌──────────────────────────┐
│  ＋ 新建       ‹ 笔记列表 │  ← 动作块（home 态隐藏 ‹）
├──────────────────────────┤
│  ✓ 当前笔记标题           │  ← 当前条：勾选 + 高亮
│    另一条笔记             │
│    …（超高滚动）          │
└──────────────────────────┘
```

- 上块：横向两枚动作按钮。`＋ 新建` 文案 = `"＋ " + Localization.Format("UI_NewWidgetFormat",
  Localization.Get("UI_WidgetKindNote"))`；`‹ 笔记列表` 文案 = `UI_NoteHome`。home 态隐藏后者。
- 下块：`NoteOps.SortForDisplay(_data.Notes)`，每条只显示标题（空标题 = `UI_NoteUntitled`）；
  当前 `_expandedId` 条目显示勾选并带高亮底色；超长标题省略。
- 下拉宽度 = `max(220, 面板 ActualWidth - 24)`，上限 360；列表区最大高度 ≈ 面板
  `ActualHeight * 0.45`（下限 120），超出时内部滚动。
- 放置：标题区域下方左对齐；由 WPF `Popup` 负责屏幕边界避让。
- 关闭：选择任一动作 / 笔记、点外部、Esc、面板隐藏 / 关闭、主题或语言刷新。
- **编辑中打开**：不提交、不退出；下拉关闭后若仍在编辑，焦点回 `EditBox`。
- 下拉打开时 `NoteSurfaceView` 的编辑会话与文本保持不变；只有选择条目才触发提交。

### 3.3 导航与键盘

| 当前状态 | 操作 | 结果 |
|---|---|---|
| home | 点卡面 | 进入 detail（渲染态） |
| home | `＋`（下拉 / 空白右键） | 新笔记 + detail + 整文编辑 |
| home | `▾` | 打开下拉；`‹ 笔记列表` 隐藏 |
| home | Esc / Backspace | 无动作 |
| detail 渲染 | 点块正文 | 该块进入编辑 |
| detail 渲染 | 点空白 / 卡片内边距 | 无动作 |
| detail 渲染 | Esc / Backspace | 回 home |
| detail 渲染 | 下拉选其它笔记 | 直接切到目标渲染态 |
| detail 渲染 | 下拉选当前条 | 关闭下拉，不动作 |
| detail 编辑 | Esc | 提交 + 退出编辑，仍 detail |
| detail 编辑 | 下拉选其它笔记 / `＋` / `‹` | 提交当前 → 切换 / 新建 / 回 home |
| detail 编辑 | 点空白 / 卡片内边距 | 提交 + 退出编辑，仍 detail |
| detail 编辑 | 点文末空白（AppendZone） | 提交当前 + 追加新块并进入编辑 |
| 任意 | 面板隐藏 / 关闭 / 关机 | 提交当前块；空稿丢弃；detail 状态保留到下次显示 |

- 删除入口仍只在 home 卡面行菜单；detail 不提供删除。
- **空白区域右键**在 home 与 detail 都弹出同一动作菜单：`＋ 新建` + `‹ 笔记列表`
  （home 态隐藏 home 项，因此实际只显示新建；detail 态两项都显示）。
  「空白」指 `_surfaceHost` 之外的 padding / 面板边距；笔记内容区内部右键不走该菜单
  （编辑框仍走快速插入菜单，渲染文本不弹动作菜单）。
- `TryUndo`（Ctrl+Z 列表态）保持现有撤回栈；焦点在编辑框内时仍走文本撤销。

---

## 4. 空白点击与 AppendZone

### 4.1 编辑态手势矩阵

| 点击位置 | 结果 |
|---|---|
| 编辑框内 | 保持编辑（输入 / 选择 / 右键菜单） |
| 块与块之间空白（前段与编辑框、编辑框与后段之间） | 提交 + 退出编辑，仍 detail |
| 卡片内边距（`_surfaceHost` 的 padding / 圆角边） | 提交 + 退出编辑，仍 detail |
| 面板空白（detail 内容区之外的边距） | 提交 + 退出编辑，仍 detail |
| 前 / 后段中的任务框、链接 | 控件保持惰性（不写、不导航）；提交 + 退出编辑 |
| 前 / 后段中另一块的行 | 提交当前 + 切换到该块编辑 |
| 滚动条 / 滑块 | 保持编辑（拖动滚动） |
| **AppendZone（文末空白）** | 提交当前 + 追加新块并进入编辑 |

- 所有「退出编辑」路径都必须先 `Flush()`（节流中未落盘的文本立即拼接）；幂等。
- 退出编辑后焦点回 `ReadingBox`（Esc / Backspace 路由需要）。
- 渲染态：点块进入编辑；点块间 / 文末 / 卡片内边距 / 面板空白都不动作；
  任务框与链接保持既有行为。

### 4.2 AppendZone（新块编辑入口）

- `NoteSurfaceView` 的滚动内容由 `SurfaceStack`（`Grid`）承载：
  - 第 0 行：`PaneStack`（ReadingBox / BeforeBox / EditBox / AfterBox）；
  - 第 1 行：`AppendZone`（透明 `Border`，`*` 行，`MinHeight=48`，`Cursor=IBeam`，
    无文字 / 图标）。
- `ScrollViewer.VerticalContentAlignment=Stretch`，使内容不足一屏时 AppendZone 占满
  最后一块剩余高度；内容超长时 AppendZone 位于最后一个 pane 之下（滚动到底可见）。
- 可见性：仅编辑态 `Visible`；渲染态 `Collapsed`。新笔记的整文编辑也可见，但此时
  点击它是 no-op（已经在整文编辑）。
- 点击 AppendZone：
  1. 若不在编辑态 → 不动作；
  2. 若当前是整文编辑 / 已是追加会话 → 不动作；
  3. 否则先 `CommitRegion()` 提交当前块，再进入追加会话，并把 AppendZone 滚入视口。
- **追加会话的数据语义**：
  - 会话保存 `_appendPrefix = note.Text`（进入时的全文）；Before 段渲染全部块，
    After 段为空，EditBox 为空、正文排版。
  - 提交时：若 EditBox 文本为空 → 数据保持 `_appendPrefix`（不产生空块）；
    否则 `note.Text = _appendPrefix + Separator + EditBox.Text`。
  - `Separator`：前缀为空 → `""`；前缀以 `\n\n` 或 `\r\n\r\n` 结尾 → `""`；
    前缀以 `\r\n` 结尾 → `"\r\n"`；前缀以 `\n` 结尾 → `"\n"`；否则 → `"\n\n"`。
    保证新块与上一个块之间有一个空行，CRLF 源不产生双空行或混用换行。
  - 每次提交幂等：同一 `_appendPrefix` + 同一文本重复提交结果不变；`ModifiedAt`
    只在文本真变时更新；`DataChanged` 只在真变时发一次。
  - 追加会话中切换到另一块 / 离开编辑 / 切笔记 / 面板隐藏：先按上述规则提交，
    空文本不写数据。
  - 追加会话中删除到空文本：数据回到 `_appendPrefix`；若前缀本身为空（空笔记），
    离场仍按空稿丢弃。
- **滚动**：进入追加会话后 `AppendZone.BringIntoView()`（或 `SurfaceScroller.ScrollToEnd()`），
  保证新编辑框在视口内；滚动失败不影响数据。

### 4.3 点击位置 → 源码光标（精确到字符）

- 目标：点击块内哪个字/字间，进入编辑后光标就落在源码的对应位置，而不是块首。
- 覆盖：
  - **段落 / 标题 / 列表项 / 任务行**：必须精确；
  - **代码块**：尽量精确（按代码正文的可见偏移换算到代码行/列）；
  - **表格 / 引用 / 分割线**：回退到点击行行首（不设假精度）。
- 数据底座：`Core/MarkdownParser` 的 `MdInline` 新增
  `public List<int> SourcePositions`（每个可见字符对应 0 基源码列；嵌套 inline 由子节点各自携带）。
  这是**新增元数据**，解析结果与既有 pins 不变。
- 新纯函数 `Core/NoteCaretMap.cs`：
  ```csharp
  public static class NoteCaretMap
  {
      // visualOffset = 点击行内可见字符的 0 基偏移（不含行首 marker）。
      // 返回 (sourceLine, sourceColumn)：sourceLine 为整篇源码的 0 基行号，
      // sourceColumn 为该行源码的 0 基列号。
      public static (int Line, int Column) Map(string text, int line, int visualOffset);
  }
  ```
  - 段落：直接用该行的 `SourcePositions[visualOffset]`；
  - 标题 / 列表 / 任务：在 `SourcePositions` 结果上加上行首 marker 前缀长度
    （`# ` / `- ` / `1. ` / `- [ ] ` 等）；
  - 行尾 / 空行：落到该行可见文本末尾之后（源码列 = 最后一个位置 + 1；无可见文本 =
    前缀长度）；
  - 代码块：`visualOffset` 在 `MdCodeBlock.Text` 内换算成代码行/列，返回
    `(block.Line + 1 + codeLine, column)`；无法换算时回退 `(line, 0)`；
  - 表格 / 引用 / 分割线：`(line, 0)`。
- `NoteSurfaceView`：
  - 点击时从 `TextPointer` 取**可见字符偏移**：找到带 `SourceLine` 的 `Span` / `Paragraph`，
    用 `ContentStart.GetOffsetToPosition(pos)`；任务 glyph 占一个内联位置，映射前要扣除
    或直接由 `SourcePositions` 的文本长度校正。
  - `TryBeginEditAt(int line, int column = 0)`；切换块时同样带列；
  - `SetCaret(relativeLine, relativeColumn)` 按「前面行长度 + 1 + 列」定位，
    越界钳位；
  - 映射失败 / 表格 / 引用：列 0，保持原来的行级行为。
- 既有 `LineAtPoint` 保留（行级回归）；新增 `CaretAtPoint` / `NoteCaretMap` 的纯逻辑 pin。
- 不改变：任务框 / 链接的控件优先分类；空白点击语义；编辑框文本与块范围。

### 4.4 块间空白的识别

- 编辑态下，`SurfaceRoot` 的点击分类按祖先链：
  1. `EditBox` / `AppendZone` → 各自拥有；
  2. 任务框（行号 `Tag`）/ `Hyperlink` → 控件命中；
  3. 滚动条 / `Thumb` → 滚动；
  4. 带 `NoteMarkdownRenderer.SourceLine` 的渲染元素 → 该行所属块（切换编辑）；
  5. 其余命中 → 块间空白 / chrome → 提交 + 退出编辑（不收起）。
- 不放宽上一轮的「拖选后松手不进编辑」保护：`SelectionIsNotEmpty` 仍优先跳过。
- 任务框 / 链接在编辑期间仍为静态渲染（回调为空），命中它们不得写数据或导航。

---

## 5. Bug：退出编辑后无法重新进入

### 5.1 真鼠标诊断结论（2026-10-19，已取证）

- 探针（root 同时观察 `MouseLeftButtonUp` 与 `Mouse.MouseUp`）在真实鼠标下记录到：
  - **两个事件都到达 root**；`MouseLeftButtonUp` 的 `Handled=True`，`Mouse.MouseUp` 也到；
  - `OriginalSource = RichTextBox#ReadingBox`（不是 `Span` / `Paragraph`）；
  - `Mouse.Captured = null`；`TaggedLine(OriginalSource) = -1`；`selection` 在普通单击时为
    `False`；
  - 同一击的 `ReadingBox.InputHitTest(point)` 返回 **`Paragraph`**，其
    `NoteMarkdownRenderer.SourceLine = 0`；`GetPositionFromPoint(point, false)` 在该点
    返回 `null`。
- 因此上一轮的 Direct 路由假设**被推翻**：root 订阅本身是好的，`handledEventsToo`
  也确实生效；真正的原因是 **RichTextBox 在真实鼠标事件里把 `OriginalSource` 报成自己**，
  祖先链上找不到行号标签。行号必须用点击坐标在 RTB 内做 `InputHitTest` 才能拿到。
- 该结论同样适用于 BeforeBox / AfterBox（它们也是 RichTextBox）以及任务框 / 链接的
  命中分类：不能只信 `OriginalSource`。

### 5.2 诊断方法（已执行，探针不入库）

- 临时探针记录：事件名、`Handled`、`ChangedButton`、`OriginalSource` 类型 / `Name`、
  `Mouse.Captured`、`_editing`、`TaggedLine(hit)`、`IsControlHit`、`IsScrollChrome`、
  `SelectionIsNotEmpty`、`e.GetPosition(box)`、`GetPositionFromPoint`、
  `InputHitTest` 与 `TaggedLine(InputHitTest)`。
- 真实鼠标证据：窗口 `NoteWidget` 被移到 `(50,50)`、点击 `(200,190)`；
  `%TEMP%\kobold-note-click-probe.log` 记录
  `source=RichTextBox#ReadingBox ... taggedLine=-1 ... input=Paragraph inputLine=0`。
- 探针与脚本在修复提交前删除 / 不入库。

### 5.3 修复

- **保留**现有 root 订阅 `UIElement.MouseLeftButtonUpEvent` + `handledEventsToo: true`
  （证据显示它能到达）；不改成 `Mouse.MouseUp`。
- 新增坐标解析：
  ```csharp
  // 在 RTB 内把点击点解析成带行号的元素（真实鼠标的 OriginalSource 是 RTB 本身）。
  private static object HitAtPoint(RichTextBox box, Point point)
  {
      var input = box.InputHitTest(point) as DependencyObject;
      if (IsControlHit(input)) return input;          // 任务框 / 链接
      var pos = box.GetPositionFromPoint(point, true); // 文本位置兜底
      if (pos != null && IsControlHit(pos.Parent)) return pos.Parent;
      return input;
  }
  ```
- root 处理器先算 `hit = e.OriginalSource ?? e.Source`；若 `hit is RichTextBox box`，
  用 `HitAtPoint(box, e.GetPosition(box))` 得到 `resolved`，后续
  `IsControlHit` / `TaggedLine` 都优先用 `resolved`，拿不到时再回退 `hit`。
  - `IsInside(hit, EditBox)`、`SelectionIsNotEmpty(hit)`、`IsScrollChrome(hit)` 仍用原始
    `hit` / 其祖先链，保持既有语义。
  - 任务框 / 链接：`InputHitTest` 先命中控件（或 `GetPositionFromPoint(...).Parent`
    命中 Hyperlink）时按控件处理，不进入编辑。
  - `private static int LineAtPoint(RichTextBox box, Point point)` =
    `TaggedLine(HitAtPoint(box, point))`，供 root 处理器与测试共用。
- 回归 pin：
  - `SurfaceLineAtPointResolvesAParagraph`：布局后取某行 `Span` 的字符矩形，调用
    `LineAtPoint`（内部静态，测试反射）→ 断言点击行号 = 该 Span 的行号；
  - `SurfaceLineAtPointReturnsMinusOneOnBlank`：点在文档内容之外的空白 → -1；
  - `SurfacePointHitKeepsTaskGlyphOutOfTheEdit`：若可离屏构造 glyph 的
    `InputHitTest` 命中，断言 `IsControlHit` 分类；否则标注冒烟覆盖。
  - 既有「handled 行点击」「handled glyph」「框内点击保持」「滚动条不退出」pins
    **不迁移事件类型**，保持 `MouseLeftButtonUp`；只补坐标解析 pin。
- 修复后部署，用脚本 + 用户手动各做一次真鼠标冒烟：Esc 后点同块、点其它块、
  点空白后再点块；顺带点任务框 / 链接确认不误进编辑。

---

## 6. 字号

### 6.1 数据模型

- `Core/NoteData.cs` 新增：
  ```csharp
  public double FontScale { get; set; } = NoteFontScale.Medium;
  ```
  （C# 7.3 属性初始化器；旧 JSON 缺字段时保留 1.0。）
- 新 `Core/NoteFontScale.cs`（纯逻辑，可逐条 pin）：
  ```csharp
  public static class NoteFontScale
  {
      public const double Small = 0.9;
      public const double Medium = 1.0;
      public const double Large = 1.2;
      public static double Normalize(double value); // NaN/∞/≤0 → 1.0；其余夹到 [0.9,1.2]
  }
  ```
- `NoteData.FontScale` 的 setter 走 `NoteFontScale.Normalize`；`AppConfig.RepairNote`
  也对读入值归一化（防手改配置）。
- 序列化：Newtonsoft 默认读写 `double`；不新增转换器、不改 `WidgetDataConverter`。
- 旧配置：缺字段 → 1.0；新配置被旧版本读取 → 字段被忽略（允许，不承诺回写保留）。

### 6.2 渲染与编辑

- `NoteSurfaceView.BuildRenderStyle()`：
  `BaseFontSize = UiTokens.FontBody * NoteFontScale.Normalize(_data.FontScale)`。
- 标题阶梯仍由 `NoteMarkdownRenderer.HeadingFontSize(level, baseSize)` 计算；
  正文、代码块、表格、引用全部随 `BaseFontSize` 缩放。
- `ApplyEditTypography`：正文 / 标题 / 代码 / 引用的字号全部用同一个缩放后的基准；
  进出编辑不跳字号。
- 卡片标题（14）、时间（11）、摘要（12）、进度（11）**不变**；空态提示不变。
- 编辑中改字号：`NoteWidget.UpdateUI()` → `_surface.Repaint()`；只重绘前后段与
  编辑框排版，**不重写 EditBox.Text、不丢光标 / 选区、不退出编辑**。
- 渲染态改字号：重渲染当前文档；尽量保持滚动偏移（失败回顶部不设假 pin）。

### 6.3 入口与持久化

- `Helpers/WidgetMenuBuilder.cs` 在 note 分支新增「字号」子菜单（类似文件夹的图标大小）：
  - 标题：`Menu_NoteFontSize`；
  - 三项：`Note_FontSmall` / `Note_FontMedium` / `Note_FontLarge`，对应 0.9 / 1.0 / 1.2；
  - `IsChecked` 跟随归一化后的当前值（容差 0.001）；
  - 点击：写 `NoteData.FontScale` → `DataChanged(host)`（`UpdateUI` + 通知 + 防抖保存）。
- 菜单位置：显示 / 隐藏、重命名、换色、锁定、置顶之后，删除之前；与文件夹的
  「图标大小」同层。
- 三语 key 同提交；`LangCheck` 保持绿。

---

## 7. 接口与文件

### 7.1 `Controls/NotePanelView`

- 保留：`Attach(NoteData, NoteWidget)`、`Refresh()`、`TryUndo()`、`FlushPendingEdit()`、
  `_expandedId`（pin 面）。
- 新增 / 改名：
  ```csharp
  public string ChromeTitle { get; }      // home = 组件名；detail = 笔记名 / 未命名
  public bool IsHome { get; }             // _expandedId == null
  public void ToggleTitleMenu(UIElement anchor); // 打开 / 关闭标题下拉
  public void CloseTitleMenu();
  public void OpenNote(string id);        // 提交当前 + 打开目标渲染态（下拉 / 外部入口）
  public FrameworkElement BuildTitleMenuContent(); // 每次打开重建；测试可读的结构入口
  ```
- 私有编排：`Expand(NoteItem)` / `GoHome()` / `StartNewNote()` / `MountDetail()` /
  `RebuildHome()` / `SettleExpandedEdit()` / `BuildBlankMenu()` / `BuildCard(NoteItem)`。
- XAML：`HomeHost`（`ItemsScroller` > `ItemsPanel` + `EmptyState`）与 `DetailHost`
  （常驻 `_surfaceHost`）；删除 `AddPill` 与 `ListHost` 旧结构。
- `Root_MouseLeftButtonDown`：不再收起；编辑中点 surface 之外（padding / 面板边距）
  → `LeaveEdit()`；其余无动作。
- `Root_PreviewKeyDown`：编辑 + Esc → `LeaveEdit()`；detail 渲染 + Esc/Backspace →
  `GoHome()`；home 无动作。

### 7.2 `Controls/NoteSurfaceView`

- 删除：`CollapseRequested`、`CollapseButton` 及浮动 `‹`。
- 新增：
  ```csharp
  public bool TryBeginEditAt(int line, int column = 0); // 行 + 源码列
  public bool TryBeginAppendEdit(); // 提交当前 + 文末追加空块编辑；整文/追加态返回 false
  ```
- 私有：`CaretAtPoint(RichTextBox, Point, out int line, out int visualOffset)`；
  `SetCaret(relativeLine, relativeColumn)`。
- XAML：`SurfaceStack` 改 `Grid`（`PaneStack` + `AppendZone`）；`SurfaceScroller`
  改为 detail 的唯一滚动区（`VerticalScrollBarVisibility=Auto`，去掉旧 `Padding=0,24`）。
- root 点击保持 `MouseLeftButtonUpEvent`（handledEventsToo）；新增 `HitAtPoint` /
  `LineAtPoint`，真实鼠标的 `OriginalSource` 是 RTB 时按点击坐标解析行号。
- 字号：`BuildRenderStyle` / `ApplyEditTypography` 读取 `_data.FontScale`。
- 追加会话字段：`_appendSession`、`_appendPrefix`；`ResetEditState` 清空。

### 7.3 `Controls/WidgetChrome`

- 新增：
  ```csharp
  public bool TitleMenuEnabled { get; set; } // 默认 false
  public event Action TitleMenuClicked;
  public FrameworkElement TitleMenuAnchor { get; } // 标题区域
  ```
- XAML：标题列改为 `TitleGroup`（`Grid`：`TitleText` + `TitleChevron`）；
  `TitleChevron` 默认 `Collapsed`，`TitleMenuEnabled=true` 时显示。
- 只有 `▾` 处理点击（按下 Handled，抬起触发事件）；`TitleText` 仍由 `Header` 拖拽。

### 7.4 `Controls/NoteWidget`

- 构造：`NoteChrome.TitleMenuEnabled = true;` 订阅 `TitleMenuClicked` →
  `_view.ToggleTitleMenu(NoteChrome.TitleMenuAnchor)`。
- 新增 `internal void RefreshChromeTitle()`：按 `_view.ChromeTitle` 设置
  `NoteChrome.Title` 与 detail 下的 tooltip。
- `UpdateUI`：base → `_view.Refresh()` → `RefreshChromeTitle()`。
- `Closing` / `OnPanelHidden`：`_view.CloseTitleMenu()` + `FlushPendingEdit()`。
- `NotePanelView` 在导航 / 提交 / 空稿丢弃后调用 `_host.RefreshChromeTitle()`。

### 7.5 `Helpers/WidgetMenuBuilder.cs`

- note 分支新增 `CreateNoteFontMenu(host, data)`；三档勾选、点击写 `FontScale` 并
  走既有 `DataChanged(host)`。
- 文件夹 / 待办菜单项顺序与行为零变化。

### 7.6 `Core/Localization.cs` 新增 key（zh / en / ja 同提交）

| key | en | zh | ja |
|---|---|---|---|
| `UI_NoteHome` | `‹ Notes` | `‹ 笔记列表` | `‹ ノート一覧` |
| `UI_NoteUntitled` | `Untitled` | `未命名` | `無題` |
| `UI_NoteEmptyHome` | `No notes yet — open the title menu to create one.` | `暂无笔记——点标题 ▾ 新建` | `ノートはまだありません — タイトルの ▾ から作成` |
| `Menu_NoteFontSize` | `Font size` | `字号` | `文字サイズ` |
| `Note_FontSmall` | `Small` | `小` | `小` |
| `Note_FontMedium` | `Medium` | `中` | `中` |
| `Note_FontLarge` | `Large` | `大` | `大` |

- `＋ 新建` 复用 `UI_NewWidgetFormat` + `UI_WidgetKindNote`，不新增 key。
- 下拉里无笔记时不放占位条目；空列表由 home 提示承担。

### 7.7 `Core/MarkdownParser.cs` / `Core/NoteCaretMap.cs`

- `MdInline` 新增 `public List<int> SourcePositions`（每个可见字符的源码列；
  嵌套 inline 由子节点携带）。`ParseInlines` 的可见文本与 AST 类型不变。
- 新 `NoteCaretMap.Map(string text, int line, int visualOffset)` 返回
  `(int Line, int Column)`；纯逻辑、无 WPF 依赖，逐构造 pin。

---

## 8. 测试与验收

### 8.1 自检矩阵（维持 32 行，不新增工程）

- **NoteMarkdownCheck**：
  - `NoteFontScale.Normalize`：0.9/1.0/1.2 原样；NaN/∞/0/负数 → 1.0；
    超出 [0.9,1.2] 夹取；`NoteData.FontScale` 默认 1.0。
  - 解析器 source map：转义 / 粗体 / 斜体 / 删除线 / 行内代码 / 链接的
    `SourcePositions` 逐字符等于源码列；可见文本与 AST 类型不变。
  - `NoteCaretMap.Map`：段落偏移、标题前缀、列表前缀、任务前缀、行尾、空行、
    粗体内部、行内代码、代码块行/列、表格/引用回退 (line,0)。
  - 既有 NoteOps / NoteBlockRanges pins 零回归。
- **WidgetKindsCheck**：
  - 新 `NoteData` 的 `FontScale == 1.0`；
  - 保存 / 读取后自定义 `FontScale` 保持；
  - 旧 JSON 缺字段 → 1.0；
  - 手改 0 / 99 → `RepairNote` 归一化为 1.0 / 1.2。
- **ConfigSafetyCheck**：note 配置带 `FontScale` 时原子保存 / 读回不炸；既有 pins 不回归。
- **LangCheck**：7 个新 key 三语齐全；旧 key 不缺失。
- **UiTokensCheck**：不新增 hex；字号常量走 `NoteFontScale` / `UiTokens`。
- **MenuRenderCheck**：不回归（字号子菜单沿用既有 MenuItem 模板）。
- **XamlLoadCheck（笔记组重写，核心）**：
  1. home：两张笔记 → 两张卡面；无 `AddPill`；空列表 → `EmptyState` 可见、文案 =
     `UI_NoteEmptyHome`；有卡 → 隐藏。
  2. 点卡 → detail：`IsHome == false`、`HomeHost` 收起、`DetailHost` 可见、
     `ReadingBox` 文档非空、`_expandedId` 正确、`ChromeTitle` = 笔记标题。
  3. 标题：home = 组件名；detail = 笔记标题；空笔记 = `UI_NoteUntitled`；
     编辑提交后标题跟着变。
  4. 标题下拉结构：`BuildTitleMenuContent()` → 动作块 2 项（home 态 1 项）、
     笔记列表按 ModifiedAt 降序、当前条 `IsChecked`/高亮、点击条目走
     `OpenNote(id)` 并打开对应 detail。
  5. 下拉切换：编辑 A → 点 B → A 文本落盘、`_expandedId == B`、B 渲染态、
     `IsEditing == false`；点当前条只关下拉。
  6. 下拉新建：编辑 A → `＋` → A 落盘、新条置顶、detail、整文编辑、空框。
  7. 下拉 home：编辑 A → `‹` → A 落盘、`IsHome == true`、卡面顺序按 ModifiedAt；
     A 为空 → 丢弃 + 通知。
  8. 编辑中开下拉：`ToggleTitleMenu` 后 `IsEditing` 仍真；关闭后焦点回 `EditBox`
     （离屏焦点 pin 若不稳定则如实标注冒烟）。
  9. 空白：编辑中点击 `SurfaceStack` / `_surfaceHost` padding / panel root →
     `IsEditing == false`、仍 detail、文本已落；渲染态点击同样位置 → 仍 detail。
  10. 渲染态 Esc / Backspace → home；编辑态 Esc → 退出编辑但 detail。
  11. 块点击：detail 渲染点带行号 `Span` → 该块编辑；任务框 / 链接不进入编辑；
      既有 handled 行点击 pin 保持 `MouseLeftButtonUp`，另补 `LineAtPoint` 坐标解析 pin
      （真实鼠标 `OriginalSource` 是 RTB 的回归）。
  12. AppendZone：编辑中点击 `AppendZone` → 追加会话（框空、Before 全块、After 空）；
      输入 `"new"` → 数据以空行分隔追加；空文本 → 数据不变；整文编辑 / 渲染态点击
      → no-op；追加后切块 / 离开 → 已提交。
  12b. 光标精确：`TryBeginEditAt(line, column)` → `EditBox.CaretIndex` 落在该行该列；
       段落 / 标题 / 列表 / 任务各一条；行内粗体 / 代码 span 的列映射正确；
       表格 / 引用回退列 0；`CaretAtPoint` 用字符矩形 pin 至少一条。
  13. 字号：`NoteData.FontScale = 0.9/1.0/1.2` → `ReadingBox.Document.FontSize` 与
      `EditBox.FontSize` 同步；标题字号 = `HeadingFontSize(level, 12*scale)`；
      卡片摘要字号仍 12；编辑中改字号 → 框文本 / 光标保持、仍编辑。
  14. 字号菜单：`WidgetMenuBuilder.Build` 的 note 菜单含 `Menu_NoteFontSize` 三项；
      点击 `Large` → `FontScale == 1.2`、`UpdateUI` / 通知 / 勾选状态正确；
      文件夹菜单不含该子菜单。
  15. 标题 chrome：`NoteWidget` 的 `TitleMenuEnabled == true`、`TitleChevron` 可见；
      文件夹 / 待办默认 false、不可见。
  16. 空白右键：home 菜单含新建、home 隐藏；detail 菜单含新建 + home；
      笔记内容区内部右键不弹该动作菜单。
  17. 删除 + 撤回栈不回归；任务框 / 链接 / 插入菜单 / 滚动 offset 既有 pins 不回归。
- **离屏限制**：真实焦点、真实鼠标、Popup 打开 / 屏幕边界、滚动接缝仍由冒烟覆盖；
  不设假 pin。自检禁止读写剪贴板、禁止驱动真实鼠标。

### 8.2 冒烟清单（用户手动）

1. home：空列表提示；点卡进 detail；标题 = 笔记名；返回 home 后列表顺序 / 滚动位置合理。
2. 标题下拉：`▾` 打开；动作块 + 笔记列表；当前条勾选；切 3 条笔记；点当前条只关下拉；
   home / detail 的空白右键都弹动作菜单（home 只显示新建，detail 显示新建 + 笔记列表）。
3. 新建：home / detail / 编辑中三种状态点 `＋` → 旧编辑落盘、新笔记起笔；空稿离场消失。
3b. 光标：点段落/标题/列表/任务里的某个字或字间 → 进入编辑后光标就在那个源码位置；
   粗体 / 行内代码里的点击也尽量精确；表格 / 引用落到行首。
4. 空白：编辑中点块间 / 卡片内边距 / 面板边距 → 渲染回去且仍 detail；渲染态点空白
   → 不收起；`‹ 笔记列表` / Esc / Backspace 才回 home。
5. AppendZone：编辑中点文末空白 → 新编辑框；输入后空行分隔；什么都不写离开 → 不留空块；
   切块 / 切笔记 / 隐藏面板不丢字。
6. Bug：进入编辑 → Esc → 点同块 / 其它块 → 都能再次进入；点空白退出后再点块也正常。
7. 字号：右键菜单小/中/大；渲染与编辑框同步；卡片摘要不变；编辑中切换不丢文本；
   重启后保持。
8. 任务框 / 链接：渲染态可点；编辑期间前后段惰性；点它们会退出编辑。
9. 删除 + Ctrl+Z：home 行菜单删除、多步找回。
10. 三主题 / 三语 / 高对比度：标题下拉、空态、字号菜单、AppendZone 光标正确。
11. 回归：文件夹 / 待办 / 标题拖拽 / 锁 / 钉 / 面板尺寸。
12. 全量自检 `Run-Checks.ps1 -Group all` 32/32；真鼠标 bug 冒烟通过。

---

## 9. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 真实鼠标 `OriginalSource` 是 RTB，行号解析不到 | 先探针真鼠标取证；`InputHitTest` 坐标解析 + 回归 pin + 真鼠标冒烟 |
| 标题下拉 Popup 在面板边缘 / 多屏 / 高 DPI 下越界 | WPF Popup 自带屏幕避让；宽度/高度按面板自适应；冒烟覆盖 |
| 标题下拉抢走 Header 拖拽 | 只有 `▾` 处理点击并 Handled；标题文字保持拖拽；文件夹默认不启用 |
| 详情页切换丢失半成品文本 | 所有切换 / 回 home / 隐藏 / 关闭路径先 `Flush()`；既有节流 + 冲刷点不变 |
| AppendZone 占满剩余高度后误触 / 抢滚动 | 透明、无文字、IBeam；仅编辑态可见；滚轮继续冒泡给 ScrollViewer |
| 追加分隔符产生多余空行 / CRLF 破坏 | 纯函数规则 + 逐构造 pin（空前缀 / 单换行 / 双换行 / CRLF） |
| 字号切换打断编辑 / 丢光标 | `Repaint` 只改排版，不碰 `EditBox.Text`；护栏 pin |
| 旧配置缺字段 / 手改非法值 | 默认 1.0 + `RepairNote` 归一化 + 数据 pin |
| XamlLoadCheck 笔记组重写量大 | 按 home/detail、下拉、空白、AppendZone、字号分块重写；旧面板死助手同删 |
| 真实鼠标脚本误点其它窗口 | 脚本先按窗口标题 / 进程定位 `NoteWidget`，置前后再点；坐标写入日志；运行前确认 |

## 10. 交付物

- `Core/NoteFontScale.cs`（新：三档常量 + 归一化）。
- `Core/NoteCaretMap.cs`（新：点击可见偏移 → 源码行列）。
- `Core/MarkdownParser.cs`（`MdInline.SourcePositions` 元数据；AST/可见文本不变）。
- `Core/NoteData.cs`（新增 `FontScale`）。
- `Core/AppConfig.cs`（`RepairNote` 归一化字号）。
- `Core/Localization.cs`（7 个三语 key）。
- `Controls/NotePanelView.xaml(.cs)`（home / detail 重构、标题下拉、空白语义）。
- `Controls/NoteSurfaceView.xaml(.cs)`（AppendZone、字号、`InputHitTest` 坐标解析修复、
  移除浮动 `‹`）。
- `Controls/NoteWidget.xaml.cs`（标题菜单接线、标题刷新、关闭时关下拉）。
- `Controls/WidgetChrome.xaml(.cs)`（可选标题菜单 / `▾`）。
- `Helpers/WidgetMenuBuilder.cs`（字号子菜单）。
- `tests/NoteMarkdownCheck/Program.cs`、`tests/WidgetKindsCheck/Program.cs`、
  `tests/ConfigSafetyCheck/Program.cs`、`tests/XamlLoadCheck/Program.cs`、
  `tests/LangCheck/Program.cs`（按 §8 增补 / 重写）。
- `README.md`（英/中）、`CHANGELOG.md`（Unreleased）。
- `docs/superpowers/plans/2026-10-19-note-navigation-and-polish.md`（writing-plans 产出）。
- 临时探针与真鼠标诊断脚本仅存在于诊断任务，最终提交前删除 / 不入库。

## 11. 与旧 spec 的关系

- 本 spec **取代**旧 spec 的：§3.1「列表成为唯一容器」、§3.2「展开 / 收起编排」、
  §3.3「展开面 chrome 与既有入口」、§4.4 手势表中与空白 / 切换 / home 相关的行。
- 旧 spec 继续有效：数据格式零变化的总体原则（本 spec 只加一个可选字段）、
  §4.1 渲染态任务框 / 链接、§4.2 点哪写哪、§4.3 块编辑态与节流拼接、
  §4.5 空稿丢弃、§5 保存与冲刷、§6 新建流（入口改为标题下拉 / home 右键）、
  §7 退役清单中与本轮不冲突的部分、§8 测试口径。
- 本 spec 对旧 spec §4.2「光标 = 点中的那一行」升级为「行 + 列」；对
  「`Core/MarkdownParser.cs` 零改动」的旧护栏做一次**增量修订**：只新增
  `MdInline.SourcePositions` 元数据与 `NoteCaretMap`，解析结果 / 可见文本 / 既有 pins 不变。
- 若两者冲突，以本 spec 为准。
