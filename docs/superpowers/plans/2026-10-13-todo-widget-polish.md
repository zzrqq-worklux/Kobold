# Todo 与 Widget 小项优化（光标 / 退格阶梯 + 撤回 / 清理已完成 / 滚动条 / 标题栏 + 方向键 / 右键菜单）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **状态**：**已获用户批准（2026-10-13，对话逐条定案 + SVG 草图选定样式 A）**。本计划按"决策记录"口径执行。
> 执行方式：subagent-driven-development（每任务 = 实现子代理 TDD → 暂存不提交 → 控制者快照 + 审查包 →
> 修复循环 → 复审通过后由实现子代理按计划提交信息提交；冒烟由用户操作）。
> worktree：`.worktrees/todo-widget-polish`；分支：`todo-widget-polish`（基线 `95f37e5`）。
> 设计草图：`docs/superpowers/plans/2026-10-13-todo-widget-polish-styles.svg`（菜单三选一样式 A + 滚动条状态图）。

**Goal：** 六项体验优化：①Todo 编辑器光标改组件色（醒目）；②Todo 条目退格阶梯删除（未完成→打勾、已完成→删除，父项子项一致）+ 删除类单步 Ctrl+Z + 当日「清理已完成」（列表空白右键）；③全局滚动条改细胶囊（悬停加粗）；④文件夹条目选中时标题栏显示全名（多选「N 项已选」）+ 方向键网格式移动选择；⑤全部右键菜单统一换"圆润留白"样式 A。

**Architecture：** 纯逻辑进 `Core/`（`TodoListOps` 的删除快照/恢复、新 `GridNavigation`），WPF 接线留在既有 partial/视图层
（`TodoCalendarView`、`TodoWidget`、`FolderWidget`）；样式改动只碰 `Themes/Controls.xaml`（滚动条）与 `App.xaml`（菜单模板），
组件色淡染机制（`Overlay*` 覆盖键）与现有渲染管线不动。所有新行为以既有自检工程的离屏 pin 覆盖（反射调用 + 合成键盘，不驱动真实鼠标）。

**Tech Stack：** WPF / .NET Framework 4.8 / C# 7.3；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵，31 行不变）。

**Spec：** 无独立 spec；需求来源 = 2026-10-13 对话（9 问逐条定案 + 草图样式 A）与下方"决策记录"。

## Global Constraints

- C# 7.3 / net48，不引入新依赖；构建 0 警告。
- TDD：每任务先写失败自检（编译失败或运行时失败均可）→ 最小实现 → 相关自检与 LangCheck 全绿。
- 三语 key 同步（zh/en/ja 同一提交内）；`LangCheck` 保持绿；新增 key 必须同任务使用（死键规则）。
- 不新增自检工程；新 pin 进现有 check；`tools/Run-Checks.ps1` 无需改动。自检内**禁止读写系统剪贴板**、
  **禁止驱动真实鼠标**（离屏 pin + 反射 + 合成键盘，参照 `RaiseKey`/`InvokeViewEdit` 既有助手）。
- hex 字面量只许在 `Core/UiTokens.cs`（`UiTokensCheck` 会抓）；本轮不新增颜色 token（沿用 `ScrollThumb*`/`Overlay*`）。
- 键盘焦点在任意 `TextBox` 内时不得被新逻辑拦截（编辑器/重命名输入各自保留文字行为）。
- 固定日期的测试夹具含未完成项会随"carry 到今天"失效——UI pin 一律用 `DateTime.Today` 造"今天"的数据；
  纯逻辑 pin 显式传 `today` 参数（无日期炸弹）。
- 每个提交独立可编译、可回退；**先任务审查后由实现子代理按 pathspec 提交**。推送/合并必须先问用户。
- 已知环境性红：`MenuRenderCheck`（观察项，会话重启后曾转绿）；矩阵以其为主，若非本分支回归且复发，如实记录。

## 决策记录（2026-10-13 用户确认）

1. **光标**：改组件色（现状 `TextPrimary` 过淡/太细）；高对比度回退系统文本色。WPF 原生光标 1px 不可加粗——
   先颜色修复；冒烟后若仍嫌细，另立"自绘 2px 光标"小任务（风险另披露）。
2. **退格阶梯**：编辑器内光标位于**文本最前端（CaretIndex==0 且无选区）**按退格——未完成→打勾
   （正常完成逻辑：盖完成日期、父项级联子项、行移到底部已完成区、编辑器跟随条目）；已完成→删除（父项含子树）。
   父项与子项一致；子项添加框/底部添加框不适用；非位置 0 或有选区时是普通文字编辑。
3. **清理已完成**：Todo 面板空白处（列表/日历的非条目区域）右键出新菜单，仅一项「清理已完成」；
   行上右键仍出行菜单；当天无已完成项时置灰。**范围 = 当前选中日**：当天列表显示的已完成行都清
   （已完成父项整条含子树；未完成父项下的已完成子项单独删）。
4. **Ctrl+Z**：删除类**单步**——覆盖单条删除/退格删除/清理已完成；只记最近一次；恢复原位与状态（含子树、完成日期）；
   编辑器聚焦时交给文本框自带撤回；无记录时无操作。
5. **滚动条**：全局细胶囊——点击区 8px 不变、轨道透明、无箭头、胶囊内缩 2px；悬停/拖动 4→6px 并加深；
   全应用统一（设置窗/弹窗列表一并）；高对比度回退系统。
6. **标题栏**：文件夹条目——单选显示条目全名（tooltip=完整路径）；多选显示「{0} 项已选」（新三语 key `UI_SelectedCountFormat`）；
   无选回落组件名/浏览路径；浏览模式同规则（选中优先于路径）。
7. **方向键**：网格式——左右=同行相邻、行首/尾跨行；上下=同列跳行、到边界停；替换式单选、自动滚入可见；
   焦点在文本框时不拦截；Shift/Ctrl 组合键不处理；Enter/双击行为不变。
8. **菜单**：全部右键菜单统一换新模板（`App.xaml`），保留组件色淡染与选中态；**样式 A = 圆角 12 · 内边距 8 ·
   行高约 34 · 悬停内缩胶囊（圆角 8）· 分割线内缩 30 · 柔和阴影 · 细人字子菜单箭头**；高对比度自动跟随系统色。

---

## Task T1: Todo 编辑器光标跟随组件色

**Files：**
- Modify: `Controls/TodoCalendarView.xaml`（`AddBox` 移除写死的 `CaretBrush`）
- Modify: `Controls/TodoCalendarView.xaml.cs`（`EditorCaretBrush()` + `BuildEditor` + `UpdateAddRowVisuals`）
- Test: `tests/XamlLoadCheck/Program.cs`（光标 pins）

**Interfaces：**
- Produces: `TodoCalendarView.EditorCaretBrush() -> Brush`（私有；`ThemeManager.IsHighContrast ? ThemeManager.TextBrush : _accentBrush`）
- 既有依赖：`_accentBrush`（字段，`Refresh()` 第 135 行重建）、`ThemeManager.IsHighContrast`、`ThemeManager.TextBrush`

- [ ] **Step 1（红）：** `tests/XamlLoadCheck/Program.cs` 在 todo pin 区（"todo: parent editor Enter opens the child adder" 一带）加：
  - `todo: the item editor caret follows the widget colour`：建一个带条目的 todo 视图（同既有 pin 的造法），
    `InvokeViewEdit(view, "StartItemEdit", item.Id)` 打开编辑器；取 `EditorBoxInList(view)`，
    断言 `((SolidColorBrush)box.CaretBrush).Color == Utils.HexToColor(data.Color)`。
  - `todo: the add box caret follows the widget colour`：断言
    `((SolidColorBrush)((TextBox)view.FindName("AddBox")).CaretBrush).Color == Utils.HexToColor(data.Color)`。
  运行 `dotnet run --project tests\XamlLoadCheck\XamlLoadCheck.csproj -c Debug --nologo` → 两 pin 红（现为 `TextPrimary`）。留档。

- [ ] **Step 2（实现）：**
  - `TodoCalendarView.xaml`：删除 `AddBox` 上的 `CaretBrush="{DynamicResource Kobold.Brush.TextPrimary}"` 一行（其余属性不动）。
  - `TodoCalendarView.xaml.cs`：在 `BuildEditor` 上方新增：
    ```csharp
    /// <summary>The caret ink of every inline editor: the widget colour, so the
    /// insertion point reads at a glance (high contrast keeps the system ink).</summary>
    private Brush EditorCaretBrush()
    {
        return ThemeManager.IsHighContrast ? ThemeManager.TextBrush : _accentBrush;
    }
    ```
  - `BuildEditor` 的 `new TextBox { ... }` 初始化器加一行：`CaretBrush = EditorCaretBrush(),`（放在 `Foreground` 之后）。
  - `UpdateAddRowVisuals`（`AddPillGlyph.Foreground = ReadableWidgetAccentBrush();` 之后）加：
    `AddBox.CaretBrush = EditorCaretBrush(); // 每次重绘重建，主题/组件色切换都跟得上`

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿；`dotnet build Kobold.csproj -c Debug --nologo` 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `fix(todo): make the editor caret follow the widget colour` →
  `git add Controls/TodoCalendarView.xaml Controls/TodoCalendarView.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## Task T2: 删除快照内核（TodoRemoval + 当日清理/恢复纯逻辑）

**Files：**
- Modify: `Core/TodoListOps.cs`（新增 `TodoRemoval`/`TodoParentRemoval`/`TodoChildRemoval` + 5 个静态函数）
- Test: `tests/TodoCalendarCheck/Program.cs`（纯逻辑 pins）

**Interfaces：**
- Produces:
  - `class TodoParentRemoval { TodoItem Item; int Index; }`
  - `class TodoChildRemoval { TodoItem Parent; SubTodoItem Child; int Index; }`
  - `class TodoRemoval { List<TodoParentRemoval> Parents; List<TodoChildRemoval> Children; bool IsEmpty; }`
  - `TodoListOps.RemoveParent(IList<TodoItem> items, TodoItem item) -> TodoRemoval`（找不到 → 空快照）
  - `TodoListOps.RemoveChildItem(TodoItem parent, SubTodoItem child) -> TodoRemoval`
  - `TodoListOps.RemoveCompletedOn(IList<TodoItem> items, DateTime day, DateTime today) -> TodoRemoval`
  - `TodoListOps.HasCompletedOn(IList<TodoItem> items, DateTime day, DateTime today) -> bool`
  - `TodoListOps.RestoreRemoval(TodoRemoval removal, IList<TodoItem> items) -> void`
- Consumes: 既有 `ForDay`/`OrderForDisplay`/`TodoRollover.DisplayDate`/`Toggle`

- [ ] **Step 1（红）：** `tests/TodoCalendarCheck/Program.cs` 新增 pin 组（用显式日期，勿用 `DateTime.Today`）：
  - 单条删除：`RemoveParent` 后列表少一项、`Parents[0].Index` 正确；`RestoreRemoval` 后顺序完全还原。
  - 子项删除：`RemoveChildItem` 记下 `Parent/Child/Index`；恢复后原位。
  - 当日清理：`today = 2026-10-12`、`day = 2026-10-12` 的夹具：①当日完成的父项（含子项）整条删除；
    ②昨日完成的父项保留；③当日未完成父项下的已完成子项删除；④非当日父项下的已完成子项保留；
    ⑤未来项/无日期项不受影响。断言快照内容与清理后的列表。
  - 恢复整批：`RestoreRemoval` 后列表与删除前逐项相等（文本/顺序/Done）。
  - 门控：`HasCompletedOn` 与清理计划一致（true/false 两组）；空日 → `IsEmpty` 且 `HasCompletedOn == false`。
  运行 `dotnet run --project tests\TodoCalendarCheck\TodoCalendarCheck.csproj -c Debug --nologo` → 编译失败（新 API 不存在）即红。留档。

- [ ] **Step 2（实现）：** `Core/TodoListOps.cs` 追加（`using System.Linq;` 已有）：
  ```csharp
  /// <summary>One removed top-level entry and where it sat.</summary>
  public sealed class TodoParentRemoval
  {
      public TodoItem Item;
      public int Index;
  }

  /// <summary>One removed sub-todo and where it sat under its parent.</summary>
  public sealed class TodoChildRemoval
  {
      public TodoItem Parent;
      public SubTodoItem Child;
      public int Index;
  }

  /// <summary>A deletion (one entry, one sub-todo, or a day's clear) kept in
  /// the shape <see cref="TodoListOps.RestoreRemoval"/> can undo.</summary>
  public sealed class TodoRemoval
  {
      public readonly List<TodoParentRemoval> Parents = new List<TodoParentRemoval>();
      public readonly List<TodoChildRemoval> Children = new List<TodoChildRemoval>();
      public bool IsEmpty => Parents.Count == 0 && Children.Count == 0;
  }
  ```
  以及（类内）：
  ```csharp
  /// <summary>Removes a top-level entry and records where it sat; an item that
  /// is not in the list yields an empty removal and changes nothing.</summary>
  public static TodoRemoval RemoveParent(IList<TodoItem> items, TodoItem item)
  {
      var removal = new TodoRemoval();
      if (items == null || item == null) return removal;
      int index = items.IndexOf(item);
      if (index < 0) return removal;
      items.RemoveAt(index);
      removal.Parents.Add(new TodoParentRemoval { Item = item, Index = index });
      return removal;
  }

  /// <summary>Removes one sub-todo and records its position under the parent.</summary>
  public static TodoRemoval RemoveChildItem(TodoItem parent, SubTodoItem child)
  {
      var removal = new TodoRemoval();
      if (parent == null || parent.Children == null || child == null) return removal;
      int index = parent.Children.IndexOf(child);
      if (index < 0) return removal;
      parent.Children.RemoveAt(index);
      removal.Children.Add(new TodoChildRemoval { Parent = parent, Child = child, Index = index });
      return removal;
  }

  /// <summary>The day-scoped clear: every completed row on the day list. A
  /// completed top-level entry goes whole (sub-todos ride along); under the
  /// day's open entries, completed sub-todos go individually.</summary>
  public static TodoRemoval RemoveCompletedOn(IList<TodoItem> items, DateTime day, DateTime today)
  {
      var removal = new TodoRemoval();
      if (items == null) return removal;

      var dayList = ForDay(items, day, today);
      var parents = new List<TodoItem>();
      var children = new List<KeyValuePair<TodoItem, SubTodoItem>>();
      foreach (var item in dayList)
      {
          if (item.Done) { parents.Add(item); continue; }
          if (item.Children == null) continue;
          foreach (var child in item.Children)
              if (child != null && child.Done) children.Add(new KeyValuePair<TodoItem, SubTodoItem>(item, child));
      }

      foreach (var item in parents)
      {
          int index = items.IndexOf(item);
          if (index >= 0) removal.Parents.Add(new TodoParentRemoval { Item = item, Index = index });
      }
      for (int i = removal.Parents.Count - 1; i >= 0; i--) items.RemoveAt(removal.Parents[i].Index);

      foreach (var pair in children)
      {
          int index = pair.Key.Children.IndexOf(pair.Value);
          if (index >= 0) removal.Children.Add(new TodoChildRemoval { Parent = pair.Key, Child = pair.Value, Index = index });
      }
      foreach (var entry in removal.Children.OrderByDescending(e => e.Index))
          entry.Parent.Children.RemoveAt(entry.Index);

      return removal;
  }

  /// <summary>True when the day's clear would remove something (the menu gate).</summary>
  public static bool HasCompletedOn(IList<TodoItem> items, DateTime day, DateTime today)
  {
      if (items == null) return false;
      foreach (var item in ForDay(items, day, today))
      {
          if (item.Done) return true;
          if (item.Children == null) continue;
          foreach (var child in item.Children)
              if (child != null && child.Done) return true;
      }
      return false;
  }

  /// <summary>Puts a removal back: ascending inserts at the recorded indices
  /// reproduce the original order; missing slots are clamped to the ends.</summary>
  public static void RestoreRemoval(TodoRemoval removal, IList<TodoItem> items)
  {
      if (removal == null || items == null) return;
      foreach (var entry in removal.Parents.OrderBy(e => e.Index))
          items.Insert(Math.Min(entry.Index, items.Count), entry.Item);
      foreach (var entry in removal.Children.OrderBy(e => e.Index))
      {
          if (entry.Parent == null) continue;
          if (entry.Parent.Children == null) entry.Parent.Children = new List<SubTodoItem>();
          entry.Parent.Children.Insert(Math.Min(entry.Index, entry.Parent.Children.Count), entry.Child);
      }
  }
  ```

- [ ] **Step 3（绿）：** TodoCalendarCheck 全绿；`TodoRolloverCheck` 与 build 无回归（pins 不动，仅确认）。

- [ ] **Step 4（提交，1 笔）：**
  `feat(todo): add the removal snapshot and the day clear core` →
  `git add Core/TodoListOps.cs tests/TodoCalendarCheck/Program.cs`

---

## Task T3: 退格阶梯 + 单步撤回接线

**Files：**
- Modify: `Controls/TodoCalendarView.xaml.cs`（`_lastRemoval`、`RemoveItem`/`RemoveChild`、两个 Commit 空支、`BuildEditor`、两个 Build*Row、`BackspaceLadder`×2、`UndoLastRemoval`）
- Modify: `Controls/TodoWidget.xaml`（根元素加 `PreviewKeyDown`）
- Modify: `Controls/TodoWidget.xaml.cs`（`Window_PreviewKeyDown` + `using System.Windows.Controls;`）
- Test: `tests/XamlLoadCheck/Program.cs`（阶梯/撤回 pins）

**Interfaces：**
- Consumes: T2 的 `TodoRemoval`/`RemoveParent`/`RemoveChildItem`/`RestoreRemoval`
- Produces: `TodoCalendarView.UndoLastRemoval() -> bool`（public）；`BackspaceLadder(TodoItem)` / `BackspaceLadder(TodoItem, SubTodoItem)`（私有）
- `BuildEditor` 新可选参数：`Action backspaceAtStart = null`（末位；添加框调用处传 `() => BackspaceLadder(...)`，adder 不传）

- [ ] **Step 1（红）：** XamlLoadCheck 加：
  - `todo: backspace at the text start completes an open item`：`InvokeViewEdit(view, "StartItemEdit", item.Id)` →
    `var box = EditorBoxInList(view); box.CaretIndex = 0; box.SelectionLength = 0;` → `RaiseKey(window, view, box, Key.Back)`
    → 断言 `item.Done == true` 且编辑器仍在（`EditorBoxInList(view)` 不抛）。
  - `todo: backspace again deletes the completed item` → 再 `RaiseKey(..., Key.Back)` → 断言该 item 不在 `_data.Items`。
  - `todo: undo restores the last deleted item` → 反射调 `UndoLastRemoval()` → 断言 item 回到原位（同 index），
    且编辑器数据一致（文本/位置）。
  - `todo: the child ladder completes then deletes`：`StartChildAdd`/打开子项编辑器（参照 "todo: adder blur commits once and closes" 一带）
    → 位置 0 `Key.Back` → 子项 `Done`；再按 → 移除；`UndoLastRemoval` → 子项回位。
  - `todo: backspace mid-text stays plain editing`：`CaretIndex = 2` 时 `Key.Back` → 条目的 `Done` 不变、未被移除。
  运行 → 运行时红（无阶梯行为/无 `UndoLastRemoval`）。留档。

- [ ] **Step 2（实现）：**
  - 视图字段（`_editingItemId` 一带）：`private TodoRemoval _lastRemoval;`
  - `RemoveItem` 改为（保留既有编辑态清理）：
    ```csharp
    private void RemoveItem(TodoItem item)
    {
        if (_editingItemId == item.Id) { _editingItemId = null; _editorCaretAt = null; }
        if (_addingChildToId == item.Id) { _addingChildToId = null; _editorCaretAt = null; }
        var removal = TodoListOps.RemoveParent(_data.Items, item);
        if (removal.IsEmpty) return;
        _lastRemoval = removal;
        AfterItemEdit();
    }
    ```
  - `RemoveChild` 同理：编辑态清理后 `var removal = TodoListOps.RemoveChildItem(parent, child); if (removal.IsEmpty) return; _lastRemoval = removal; AfterItemEdit();`
  - `CommitItemEdit` 的空文本分支改为 `RemoveItem(item); return;`（删掉原两行 + 末尾 `AfterItemEdit()` 在返回路径上不再重复）；
    `CommitChildEdit` 的空文本分支改为 `RemoveChild(parent, child); return;`，非空时 `child.Text = value.Trim();` 后 `AfterItemEdit();`。
  - `BuildEditor` 签名加末位参数 `Action backspaceAtStart = null`，`KeyDown` 里 `Key.Escape` 分支之后加：
    ```csharp
    else if (e.Key == Key.Back && backspaceAtStart != null && box.CaretIndex == 0 && box.SelectionLength == 0)
    {
        e.Handled = true;   // 位置 0 的退格属于条目动作，不是文字编辑
        backspaceAtStart();
    }
    ```
  - `BuildParentRow` 的编辑器调用（5 参：commit/cancel/commitOnBlur）第 6 参追加 `() => BackspaceLadder(item)`；
    `BuildChildRow` 的调用现为 4 参，改为
    `BuildEditor(child.Text, CHILD_FONT, <commit>, <cancel>, null, () => BackspaceLadder(parent, child))`；
    `BuildChildAdder` 不动（不传，默认 null）。
  - 新增：
    ```csharp
    /// <summary>Two-step safety on Backspace at the text start: an open entry
    /// completes first (the normal tick semantics); a completed entry deletes.
    /// The deletion lands in the one-step undo slot like every other delete.</summary>
    private void BackspaceLadder(TodoItem item)
    {
        if (!item.Done) { TodoRollover.Toggle(item, true, DateTime.Today); AfterItemEdit(); }
        else RemoveItem(item);
    }

    private void BackspaceLadder(TodoItem parent, SubTodoItem child)
    {
        if (!child.Done) { TodoRollover.ToggleChild(child, true); AfterItemEdit(); }
        else RemoveChild(parent, child);
    }

    /// <summary>The delete-undo: restores the last removal (entry, sub-todo or
    /// the day's clear) at its original place. A focused text box never gets
    /// here - the widget routes Ctrl+Z only for the list context.</summary>
    public bool UndoLastRemoval()
    {
        var removal = _lastRemoval;
        if (removal == null || removal.IsEmpty) return false;
        _lastRemoval = null;
        TodoListOps.RestoreRemoval(removal, _data.Items);
        AfterItemEdit();
        return true;
    }
    ```
  - `TodoWidget.xaml` 根元素属性加 `PreviewKeyDown="Window_PreviewKeyDown"`（与既有 `PreviewMouseLeftButtonDown` 并列）。
  - `TodoWidget.xaml.cs`：加 `using System.Windows.Controls;` 与方法：
    ```csharp
    /// <summary>Ctrl+Z in the list context restores the last delete; a focused
    /// text box keeps its own text undo.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (Keyboard.FocusedElement is TextBox) return;
        if (_view != null && _view.UndoLastRemoval()) e.Handled = true;
    }
    ```

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿；build 0 警告。（Ctrl+Z 键路由本身由冒烟覆盖——离屏 pin 无法伪造
  `Keyboard.Modifiers`，撤回语义已由 `UndoLastRemoval` pins 覆盖。）

- [ ] **Step 4（提交，1 笔）：**
  `feat(todo): ladder the backspace deletes and add one-step undo` →
  `git add Controls/TodoCalendarView.xaml.cs Controls/TodoWidget.xaml Controls/TodoWidget.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## Task T4: 「清理已完成」当日菜单

**Files：**
- Modify: `Controls/TodoCalendarView.xaml.cs`（`BuildDayAreaMenu` + `Attach` 接线 + `ClearCompletedForDay`）
- Modify: `Core/Localization.cs`（三语 `Menu_ClearCompleted`）
- Test: `tests/XamlLoadCheck/Program.cs`（菜单 pins）

**Interfaces：**
- Consumes: T2 的 `HasCompletedOn`/`RemoveCompletedOn`；T3 的 `_lastRemoval`（同文件先序）
- Produces: `BuildDayAreaMenu() -> ContextMenu`（私有；行菜单仍优先）；`ClearCompletedForDay()`（私有）

- [ ] **Step 1（红）：** XamlLoadCheck 加：
  - `todo: the day-area menu holds the clear entry`：反射调 `BuildDayAreaMenu`，断言 `Items.Count == 1` 且
    `HeaderOf(Items[0]) == Localization.Get("Menu_ClearCompleted")`。
  - `todo: the clear entry enables only with completed rows on the day`：无已完成 → `IsEnabled false`；
    造一个"今天完成"的条目（`TodoRollover.Toggle(item, true, DateTime.Today)`）→ `IsEnabled true`。
  - `todo: clicking clear removes the day's completed rows`：夹具 = ①今天完成的父项（带子项）②今天未完成父项（带一个已完成子项）
    ③今天未完成父项（无子项）；`ClickMenuItem`-风格地对菜单项 `RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent))` →
    断言①②的已完成部分删除、③保留；再 `UndoLastRemoval()` → 全部还原。
  运行 → 运行时红（`Menu_ClearCompleted` 不存在、方法不存在）。留档。

- [ ] **Step 2（实现）：**
  - `Localization.cs` 三个语言字典各加一条（`Menu_DeleteItem` 行之后）：
    `["Menu_ClearCompleted"] = "🧹 Clear completed"` / `"🧹 清理已完成"` / `"🧹 完了済みを整理"`。
  - `TodoCalendarView.xaml.cs`：
    ```csharp
    /// <summary>The day-area menu (empty list/calendar space): the day-scoped
    /// clear. Rebuilt per right-click so the enabled state is always current;
    /// rows carry their own menus and take precedence.</summary>
    private ContextMenu BuildDayAreaMenu()
    {
        return new MenuBuilder(_data.Color)
            .AddItem("Menu_ClearCompleted", () => ClearCompletedForDay(),
                TodoListOps.HasCompletedOn(_data.Items, _selected, DateTime.Today))
            .Build();
    }

    /// <summary>Clears the selected day's completed rows (whole completed
    /// parents, plus completed sub-todos of the day's open parents) as one
    /// undoable deletion.</summary>
    private void ClearCompletedForDay()
    {
        var removal = TodoListOps.RemoveCompletedOn(_data.Items, _selected, DateTime.Today);
        if (removal.IsEmpty) return;
        _lastRemoval = removal;
        AfterItemEdit();
    }
    ```
  - `Attach(...)` 内（`Root` 可用后）接线：
    ```csharp
    // 行菜单在各自行上是最近的 ContextMenu，优先命中；空白处命中这里。
    Root.ContextMenuOpening += (s, e) => { Root.ContextMenu = BuildDayAreaMenu(); };
    ```

- [ ] **Step 3（绿）：** XamlLoadCheck + LangCheck（三语键同步）全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(todo): clear the day's completed items from the list menu` →
  `git add Controls/TodoCalendarView.xaml.cs Core/Localization.cs tests/XamlLoadCheck/Program.cs`

---

## Task T5: 全局滚动条改细胶囊（悬停加粗）

**Files：**
- Modify: `Themes/Controls.xaml`（`Style TargetType="{x:Type ScrollBar}"` 整块替换）
- Test: `tests/XamlLoadCheck/Program.cs`（滚动条 pin）

**Interfaces：**
- 无新公共 API；沿用 `Kobold.Brush.ScrollThumb` / `ScrollThumbHover`；模板内保留 `PART_Track` 与 `Th` 命名。

- [ ] **Step 1（红）：** XamlLoadCheck 加（利用已初始化的 App 资源，隐式样式自动生效）：
  - `scrollbar: the pill rests slim and centred`：`var bar = new ScrollBar { Orientation = Orientation.Vertical }; bar.ApplyTemplate();`
    → `var track = (Track)bar.Template.FindName("PART_Track", bar); track.Thumb.ApplyTemplate();`
    → `var th = (Border)track.Thumb.Template.FindName("Th", track.Thumb);` 断言 `th.Width == 4`、
    `th.CornerRadius.TopLeft == 2`、`th.HorizontalAlignment == Center`。
  - `scrollbar: the horizontal pill mirrors the rest state`：同上、`Orientation.Horizontal` → `th.Height == 4`、
    `th.VerticalAlignment == Center`。
  运行 → 红（现 `Th` 为拉伸 + `Margin=1`，`Width` 为 NaN）。留档。

- [ ] **Step 2（实现）：** `Themes/Controls.xaml` 的 ScrollBar 样式替换为：
  ```xml
  <Style TargetType="{x:Type ScrollBar}">
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="Width" Value="8"/>
      <Setter Property="MinWidth" Value="8"/>
      <Setter Property="SnapsToDevicePixels" Value="True"/>
      <Setter Property="Template">
          <Setter.Value>
              <ControlTemplate TargetType="{x:Type ScrollBar}">
                  <Grid Background="Transparent">
                      <Track x:Name="PART_Track" IsDirectionReversed="True">
                          <Track.DecreaseRepeatButton>
                              <RepeatButton Command="ScrollBar.PageUpCommand" Focusable="False">
                                  <RepeatButton.Template>
                                      <ControlTemplate TargetType="{x:Type RepeatButton}">
                                          <Border Background="Transparent"/>
                                      </ControlTemplate>
                                  </RepeatButton.Template>
                              </RepeatButton>
                          </Track.DecreaseRepeatButton>
                          <Track.IncreaseRepeatButton>
                              <RepeatButton Command="ScrollBar.PageDownCommand" Focusable="False">
                                  <RepeatButton.Template>
                                      <ControlTemplate TargetType="{x:Type RepeatButton}">
                                          <Border Background="Transparent"/>
                                      </ControlTemplate>
                                  </RepeatButton.Template>
                              </RepeatButton>
                          </Track.IncreaseRepeatButton>
                          <Track.Thumb>
                              <Thumb>
                                  <Thumb.Template>
                                      <ControlTemplate TargetType="{x:Type Thumb}">
                                          <Border x:Name="Th" Width="4" CornerRadius="2"
                                                  HorizontalAlignment="Center" Margin="0,2,0,2"
                                                  Background="{DynamicResource Kobold.Brush.ScrollThumb}"/>
                                          <ControlTemplate.Triggers>
                                              <Trigger Property="IsMouseOver" Value="True">
                                                  <Setter TargetName="Th" Property="Width" Value="6"/>
                                                  <Setter TargetName="Th" Property="CornerRadius" Value="3"/>
                                                  <Setter TargetName="Th" Property="Background" Value="{DynamicResource Kobold.Brush.ScrollThumbHover}"/>
                                              </Trigger>
                                              <Trigger Property="IsDragging" Value="True">
                                                  <Setter TargetName="Th" Property="Width" Value="6"/>
                                                  <Setter TargetName="Th" Property="CornerRadius" Value="3"/>
                                                  <Setter TargetName="Th" Property="Background" Value="{DynamicResource Kobold.Brush.ScrollThumbHover}"/>
                                              </Trigger>
                                          </ControlTemplate.Triggers>
                                      </ControlTemplate>
                                  </Thumb.Template>
                              </Thumb>
                          </Track.Thumb>
                      </Track>
                  </Grid>
              </ControlTemplate>
          </Setter.Value>
      </Setter>
      <Style.Triggers>
          <Trigger Property="Orientation" Value="Horizontal">
              <Setter Property="Width" Value="Auto"/>
              <Setter Property="MinWidth" Value="0"/>
              <Setter Property="Height" Value="8"/>
              <Setter Property="MinHeight" Value="8"/>
              <Setter Property="Template">
                  <Setter.Value>
                      <ControlTemplate TargetType="{x:Type ScrollBar}">
                          <Grid Background="Transparent">
                              <Track x:Name="PART_Track" IsDirectionReversed="False">
                                  <Track.DecreaseRepeatButton>
                                      <RepeatButton Command="ScrollBar.PageUpCommand" Focusable="False">
                                          <RepeatButton.Template>
                                              <ControlTemplate TargetType="{x:Type RepeatButton}">
                                                  <Border Background="Transparent"/>
                                              </ControlTemplate>
                                          </RepeatButton.Template>
                                      </RepeatButton>
                                  </Track.DecreaseRepeatButton>
                                  <Track.IncreaseRepeatButton>
                                      <RepeatButton Command="ScrollBar.PageDownCommand" Focusable="False">
                                          <RepeatButton.Template>
                                              <ControlTemplate TargetType="{x:Type RepeatButton}">
                                                  <Border Background="Transparent"/>
                                              </ControlTemplate>
                                          </RepeatButton.Template>
                                      </RepeatButton>
                                  </Track.IncreaseRepeatButton>
                                  <Track.Thumb>
                                      <Thumb>
                                          <Thumb.Template>
                                              <ControlTemplate TargetType="{x:Type Thumb}">
                                                  <Border x:Name="Th" Height="4" CornerRadius="2"
                                                          VerticalAlignment="Center" Margin="2,0,2,0"
                                                          Background="{DynamicResource Kobold.Brush.ScrollThumb}"/>
                                                  <ControlTemplate.Triggers>
                                                      <Trigger Property="IsMouseOver" Value="True">
                                                          <Setter TargetName="Th" Property="Height" Value="6"/>
                                                          <Setter TargetName="Th" Property="CornerRadius" Value="3"/>
                                                          <Setter TargetName="Th" Property="Background" Value="{DynamicResource Kobold.Brush.ScrollThumbHover}"/>
                                                      </Trigger>
                                                      <Trigger Property="IsDragging" Value="True">
                                                          <Setter TargetName="Th" Property="Height" Value="6"/>
                                                          <Setter TargetName="Th" Property="CornerRadius" Value="3"/>
                                                          <Setter TargetName="Th" Property="Background" Value="{DynamicResource Kobold.Brush.ScrollThumbHover}"/>
                                                      </Trigger>
                                                  </ControlTemplate.Triggers>
                                              </ControlTemplate>
                                          </Thumb.Template>
                                      </Thumb>
                                  </Track.Thumb>
                              </Track>
                          </Grid>
                      </ControlTemplate>
                  </Setter.Value>
              </Setter>
          </Trigger>
      </Style.Triggers>
  </Style>
  ```

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿（含两条新 pin）；`dotnet build` 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `style(scrollbar): restyle the scrollbars as slim hover-growing pills` →
  `git add Themes/Controls.xaml tests/XamlLoadCheck/Program.cs`

---

## Task T6: 文件夹标题栏显示全名 /「N 项已选」

**Files：**
- Modify: `Controls/FolderWidget.xaml.cs`（`RefreshChromeTitle()`；`UpdateUI` 第 165-166 行替换）
- Modify: `Controls/FolderWidget.Selection.cs`（`ClearAllSelections`、lasso 收尾）
- Modify: `Controls/FolderWidget.MenuActions.cs`（右键归一化处）
- Modify: `Controls/FolderWidget.Watch.cs`（watch 重选处）
- Modify: `Controls/FolderWidget.DragDrop.cs`（`Item_MouseDown` 选择块收尾）
- Modify: `Core/Localization.cs`（三语 `UI_SelectedCountFormat`）
- Test: `tests/XamlLoadCheck/Program.cs`（标题 pins）

**Interfaces：**
- Produces: `FolderWidget.RefreshChromeTitle()`（私有；T7 的方向键也会调用）
- Consumes: 既有 `GetSelectedItems()`、`GetDisplayName(path)`、`Chrome.Title`/`Chrome.TitleText.ToolTip`、`BrowsePath.Label`、`Localization.Format`

- [ ] **Step 1（红）：** XamlLoadCheck 加（文件夹 Fixture 造法同 "folder: item selection follows the widget colour" 一带）：
  - `folder: a single selection shows the full name in the title`：给一个长名条目 `IsSelected = true`（在 `UpdateUI` 重建出的
    `ItemsContainer.ItemsSource` 列表上设置）→ 反射调 `RefreshChromeTitle` → 断言
    `Chrome.Title == System.IO.Path.GetFileName(item.Path)`（普通无 `.lnk` 长文件名）且 `Chrome.TitleText.ToolTip == item.Path`。
  - `folder: multiple selections show the count`：两个 `IsSelected = true` →
    `Chrome.Title == Localization.Format("UI_SelectedCountFormat", 2)` 且 tooltip 为 null。
  - `folder: an empty selection keeps the widget name`：全清 → `Chrome.Title == data.Name`。
  运行 → 红（`RefreshChromeTitle` 不存在）。留档。

- [ ] **Step 2（实现）：**
  - `Localization.cs` 三语各加：`["UI_SelectedCountFormat"] = "{0} selected"` / `"{0} 项已选"` / `"{0} 件選択中"`
    （`UI_NewWidgetFormat` 行之后）。
  - `FolderWidget.xaml.cs`：`UpdateUI` 中改 `Chrome.Title` 两行为 `RefreshChromeTitle();`，并新增：
    ```csharp
    /// <summary>The chrome title follows the selection: one entry shows its
    /// full name (tooltip: the whole path), several show the count, and an
    /// empty selection falls back to the widget name / browse path.</summary>
    private void RefreshChromeTitle()
    {
        var selected = GetSelectedItems();
        string title;
        string tooltip = null;
        if (selected.Count == 1)
        {
            title = GetDisplayName(selected[0].Path);
            tooltip = selected[0].Path;
        }
        else if (selected.Count > 1)
        {
            title = Localization.Format("UI_SelectedCountFormat", selected.Count);
        }
        else
        {
            title = IsBrowsing ? BrowsePath.Label(CurrentBrowsePath) : _data.Name;
            tooltip = IsBrowsing ? CurrentBrowsePath : null;
        }
        Chrome.Title = title;
        Chrome.TitleText.ToolTip = tooltip;
    }
    ```
  - 调用点（每处 +1 行）：
    - `FolderWidget.Selection.cs`：`ClearAllSelections()` 末尾（隐藏选择矩形之后）；`ItemsContainer_MouseLeftButtonUp` 的
      `Mouse.Capture(null);` 之后（lasso 松手才刷新，拖动中不抖动）。
    - `FolderWidget.MenuActions.cs`：`ShowBrowseItemMenu` 的右键归一化块（`item.IsSelected = true;` 之后）。
    - `FolderWidget.Watch.cs`：重选循环（`foreach` 块）之后。
    - `FolderWidget.DragDrop.cs`：`Item_MouseDown` 的选择处理块之后（`Mouse.Capture(b);` 之前）。

- [ ] **Step 3（绿）：** XamlLoadCheck + LangCheck 全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(folder): show the selection in the chrome title` →
  `git add Controls/FolderWidget.xaml.cs Controls/FolderWidget.Selection.cs Controls/FolderWidget.MenuActions.cs Controls/FolderWidget.Watch.cs Controls/FolderWidget.DragDrop.cs Core/Localization.cs tests/XamlLoadCheck/Program.cs`

---

## Task T7: 文件夹方向键网格导航

**Files：**
- Create: `Core/GridNavigation.cs`
- Modify: `Controls/FolderWidget.Interactions.cs`（`Window_PreviewKeyDown` 重构 + `TryMoveSelectionWithKey` + `MoveItemSelection`）
- Test: `tests/AdaptiveLayoutCheck/Program.cs`（矩阵 pins）、`tests/XamlLoadCheck/Program.cs`（接线 pin）

**Interfaces：**
- Consumes: T6 的 `RefreshChromeTitle()`；既有 `GetItemsPanel`（`FolderWidget.Selection.cs`）
- Produces: `enum GridDirection { Left, Right, Up, Down }`、`GridNavigation.Move(int index, int count, int columns, GridDirection direction) -> int`

- [ ] **Step 1（红）：** `tests/AdaptiveLayoutCheck/Program.cs` 加矩阵 pins（纯函数，无 UI）：
  - 左右=相邻：`(1,10,4,Left)=0`、`(1,10,4,Right)=2`、`(0,...,Left)=0`、`(9,...,Right)=9`。
  - 跨行：`(3,10,4,Right)=4`（行尾→下一行首）、`(4,10,4,Left)=3`（行首→上一行尾）。
  - 上下同列：`(2,10,4,Down)=6`、`(6,10,4,Up)=2`；`(1,10,4,Up)=1`（顶行停）、`(9,10,4,Down)=9`（末行停）。
  - 末行不满：`(7,10,4,Down)=9`（列不存在→末行最后一项）；`(5,10,4,Down)=9`。
  - 单列=线性：`(1,5,1,Down)=2`、`(3,5,1,Up)=2`。
  - 退化输入：`count<=0` / `columns<1` / `index` 越界 → 原样返回。
  运行 `dotnet run --project tests\AdaptiveLayoutCheck\...` → 编译失败（`GridNavigation` 不存在）即红。留档。

- [ ] **Step 2（实现）：**
  - 新建 `Core/GridNavigation.cs`：
    ```csharp
    using System;

    namespace Kobold.Core
    {
        /// <summary>The four arrow directions over the folder item grid.</summary>
        public enum GridDirection { Left, Right, Up, Down }

        /// <summary>
        /// The arrow-key motion over the item grid: rows fill left to right, so
        /// Left/Right are the neighbouring cell (the two row ends meet), while
        /// Up/Down jump a full row of <c>columns</c>; a partial last row takes
        /// Down from above on its last entry. A move that would leave the grid
        /// keeps the current cell.
        /// </summary>
        public static class GridNavigation
        {
            public static int Move(int index, int count, int columns, GridDirection direction)
            {
                if (count <= 0 || index < 0 || index >= count) return index;
                if (columns < 1) return index;

                switch (direction)
                {
                    case GridDirection.Left:
                        return index > 0 ? index - 1 : index;
                    case GridDirection.Right:
                        return index < count - 1 ? index + 1 : index;
                    case GridDirection.Up:
                        return index - columns >= 0 ? index - columns : index;
                    case GridDirection.Down:
                        {
                            int target = index + columns;
                            if (target < count) return target;
                            return index / columns == (count - 1) / columns ? index : count - 1;
                        }
                    default:
                        return index;
                }
            }
        }
    }
    ```
  - `FolderWidget.Interactions.cs`：`Window_PreviewKeyDown` 开头插入 `if (TryMoveSelectionWithKey(e)) return;`，新增 usings
    （`System.Windows.Controls.Primitives` 用于 `UniformGrid`）与：
    ```csharp
    /// <summary>Arrow keys walk the item grid; a focused text box (the rename
    /// editor) keeps the arrows for caret movement, and modifier combinations
    /// stay untouched. A boundary press falls through to the scroller.</summary>
    private bool TryMoveSelectionWithKey(KeyEventArgs e)
    {
        GridDirection direction;
        switch (e.Key)
        {
            case Key.Left: direction = GridDirection.Left; break;
            case Key.Right: direction = GridDirection.Right; break;
            case Key.Up: direction = GridDirection.Up; break;
            case Key.Down: direction = GridDirection.Down; break;
            default: return false;
        }
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None) return false;
        if (Keyboard.FocusedElement is TextBox) return false;
        if (!MoveItemSelection(direction)) return false;
        e.Handled = true;
        return true;
    }

    /// <summary>Moves the single selection across the grid; nothing selected
    /// means the first arrow picks the first entry. Returns true when the
    /// selection changed (or was created).</summary>
    private bool MoveItemSelection(GridDirection direction)
    {
        var items = ItemsContainer.ItemsSource as List<DisplayItem>;
        if (items == null || items.Count == 0) return false;

        int columns = 1;
        var panel = GetItemsPanel(ItemsContainer) as UniformGrid;
        if (panel != null && panel.Columns > 0) columns = panel.Columns;

        int current = items.FindIndex(i => i.IsSelected);
        int target;
        if (current < 0)
        {
            target = 0;
        }
        else
        {
            target = GridNavigation.Move(current, items.Count, columns, direction);
            if (target == current) return false; // grid boundary: leave the key alone
        }

        ClearAllSelections();
        items[target].IsSelected = true;
        RefreshChromeTitle();

        var container = ItemsContainer.ItemContainerGenerator.ContainerFromIndex(target) as FrameworkElement;
        if (container != null) container.BringIntoView();
        return true;
    }
    ```
  - XamlLoadCheck 接线 pin：`folder: the arrow keys move the selection across the grid`——建 root 模式文件夹 + 条目，
    选中第 0 项；用 `RaiseKey` 的模式（`EnsureRealDpi` + `HwndSource`）在窗口上 raise `Keyboard.PreviewKeyDownEvent`
    的 `Key.Right`；断言第 1 项 `IsSelected`、第 0 项未选、标题变为该条目全名；最后一项再 `Right` → 选择不动。
  运行 → 红（无处理）。留档。

- [ ] **Step 3（绿）：** AdaptiveLayoutCheck + XamlLoadCheck 全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(folder): move the selection with the arrow keys` →
  `git add Core/GridNavigation.cs Controls/FolderWidget.Interactions.cs tests/AdaptiveLayoutCheck/Program.cs tests/XamlLoadCheck/Program.cs`

---

## Task T8: 右键菜单统一样式 A（圆润留白）

**Files：**
- Modify: `App.xaml`（`ContextMenu` / `MenuItem` / `Separator` 三个模板）
- Test: `tests/XamlLoadCheck/Program.cs`（模板 pins）

**Interfaces：**
- 模板命名：`ContextMenu` 模板根内 Border `x:Name="Surface"`；`MenuItem` 内胶囊 Border `x:Name="Pill"`（缩 Thumb 风格的 `Bg`→`Pill`）；
  保留 `Overlay*` 动态键（组件色淡染继续生效）；`Arrow` 改细人字（保留 x:Name）。

- [ ] **Step 1（红）：** XamlLoadCheck 加：
  - `menu: the surface wears the rounded style`：`var menu = new MenuBuilder(null).AddItem("Menu_Delete", () => {}).Build(); menu.ApplyTemplate();`
    → `var surface = (Border)menu.Template.FindName("Surface", menu);` 断言 `CornerRadius.TopLeft == 12`、`Padding == new Thickness(8)`。
  - `menu: the item pill is inset and rounded`：`var item = new MenuItem { Header = "x" }; item.ApplyTemplate();`
    → `(Border)item.Template.FindName("Pill", item)` 断言 `Margin == new Thickness(4,1,4,1)`、`CornerRadius.TopLeft == 8`。
  - `menu: a disabled entry dims its pill`：`item.IsEnabled = false;` 后断言 `Pill.Opacity == 0.5`。
  - `menu: the separator fits the rounded rows`：`var sep = new Separator(); sep.ApplyTemplate();` → 其模板 Border
    `Margin == new Thickness(30,5,30,5)`。
  运行 → 红（命名/数值不存在）。留档。

- [ ] **Step 2（实现）：** `App.xaml` 三块替换：
  - `ContextMenu` 模板：
    ```xml
    <ControlTemplate TargetType="{x:Type ContextMenu}">
        <Grid Background="Transparent" Margin="10">
            <Border x:Name="Surface" Background="{DynamicResource Kobold.Brush.OverlayBackground}"
                    BorderBrush="{DynamicResource Kobold.Brush.OverlayBorder}" BorderThickness="1"
                    CornerRadius="12" Padding="8">
                <Border.Effect>
                    <DropShadowEffect BlurRadius="12" ShadowDepth="5" Opacity="0.40" Color="Black" RenderingBias="Quality"/>
                </Border.Effect>
                <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
            </Border>
        </Grid>
    </ControlTemplate>
    ```
    （外层 `Margin="10"` 是阴影留白；`ForceRightSidePlacement` 不动——它只在系统镜像设置开启时生效，
    阴影留白造成的 10px 视觉偏移记入冒烟观察项。）
  - `MenuItem` 模板：外层 `Border x:Name="Bg"` 改 `Background="Transparent" Padding="0"`，内层插入
    `<Border x:Name="Pill" Margin="4,1" CornerRadius="8" Padding="12,8" Background="Transparent">` 包住原 `Grid`；
    触发器中 `IsHighlighted` → `Pill` 背景 `OverlayHover`、`IsChecked` → `OverlayChecked`，并新增
    `<Trigger Property="IsEnabled" Value="False"><Setter TargetName="Pill" Property="Opacity" Value="0.5"/></Trigger>`；
    样式层加 `<Setter Property="FontSize" Value="13"/>`；`Arrow` 改为
    `<Path x:Name="Arrow" Grid.Column="1" Data="M0,0 L4.5,5.5 L0,11" Stroke="{DynamicResource Kobold.Brush.OverlayMuted}"
    StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" Fill="{x:Null}"
    Margin="12,0,0,0" VerticalAlignment="Center" Visibility="Collapsed"/>`；子菜单 `PART_Popup` 的
    `HorizontalOffset="-2" VerticalOffset="-4"` 改为 `-12` / `-14`（= 原值 − 10px 阴影留白，视觉位置不变），
    其 Border 同样包一层 `<Grid Margin="10">` + `CornerRadius="12" Padding="8"` + 同一阴影效果。
  - `Separator` 模板：`<Border Height="1" Margin="30,5,30,5" Background="{DynamicResource Kobold.Brush.OverlayDivider}"/>`。

- [ ] **Step 3（绿）：** XamlLoadCheck + MenuRenderCheck（图解 swatch 采样不回归）全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `style(menu): give the context menus the rounded surface` →
  `git add App.xaml tests/XamlLoadCheck/Program.cs`

---

## Task T9: 文档（README 英中 + CHANGELOG）

**Files：**
- Modify: `README.md`（按既有英/中双段结构，与上一轮"文件操作"同位置追加）
- Modify: `CHANGELOG.md`（「Unreleased」段落）

- [ ] **Step 1：** README 英中同步追加：退格阶梯 + Ctrl+Z 单步撤回（删除类）；列表/日历空白右键「清理已完成」（当日范围）；
  编辑器光标组件色；滚动条细胶囊；文件夹标题栏全名/N 项已选 + 方向键网格导航；右键菜单新样式。
- [ ] **Step 2：** CHANGELOG「Unreleased」四条：todo backspace ladder & one-step undo、day clear-completed、
  folder keyboard navigation & title selection、slim pill scrollbars & rounded menus。
- [ ] **Step 3（提交，1 笔）：** `docs: document the polish round` → `git add README.md CHANGELOG.md`

---

## 收尾（控制者执行，非任务）

- [ ] `tools\Run-Checks.ps1 -Group all`（31 行）全绿；`MenuRenderCheck` 若复发按观察项裁定；config-pin 误报先重跑。
- [ ] 计划文件追加"执行记录"，提交 `docs: record the polish round execution`。
- [ ] `tools\deploy.ps1` → **用户手动冒烟**：
  ①光标：各编辑器/添加框、深浅主题、高对比度；
  ②退格阶梯：父/子、位置 0、打勾后的行重排与编辑器跟随、二次删除、Ctrl+Z 找回（编辑器内 Ctrl+Z 仍是文字撤回）；
  ③清理已完成：空白右键、置灰逻辑、当日范围、整批 Ctrl+Z 还原、行菜单不受影响；
  ④滚动条：悬停/拖动加粗、各面板与设置窗；
  ⑤标题栏：单选全名与 tooltip、多选计数、方向键移动/跨行/边界/滚入可见、重命名时方向键不拦截；
  ⑥菜单样式 A：深浅/高对比、组件色淡染、子菜单、置灰项、选中项；
  ⑦回归：Enter 链、剪贴板粘贴、拖放/拖入子文件夹、拖回上级。
- [ ] 用户确认后：合并 main + 推送（需用户拍板）；worktree/SDD 工作区按收尾规范清理。
