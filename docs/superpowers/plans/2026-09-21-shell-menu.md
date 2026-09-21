# Kobold 面板内 Shell 菜单 + 文件管理动作（Shell Menu & File Actions）实现计划

> **状态（2026-09-21）**：待执行。设计见 `docs/superpowers/specs/2026-09-21-shell-menu-design.md`。
> **提交约定**：每个 Task 末尾给出 commit 命令，但**执行时必须先获得用户明确许可**才可提交。
> **执行方式**：executing-plans（inline，当前会话逐任务执行）；每个 Task 完成后独立复核（code-review-checklist）并报告。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (or superpowers:subagent-driven-development) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 面板浏览态下可就地新建/重命名/删除到回收站/在终端打开/复制路径，并能调出本机原生 shell 右键菜单（Git Bash Here、7-Zip、属性、发送到）；根态只新增"在终端中打开"。

**Architecture:** 三个新服务承担纯逻辑与 OS 调用（`TerminalLauncher` 终端解析与启动、`ShellFileOperations` 文件写操作与回收站、`ShellContextMenuService` 原生 IContextMenu 互操作）；`Controls/FolderWidget.MenuActions.cs` 新 partial 负责菜单装配、动作入口、多选规则、键盘通道与模态护栏；`Browse.cs`/`Interactions.cs` 只做分流接线。浏览动作只写磁盘，不改 `_data.Items`。

**Tech Stack:** WPF (net48)、C# 7.3、`SHFileOperation`（回收站）、`IShellItem`/`IShellFolder`/`IContextMenu(2/3)` 互操作、现有 `Localization` / `ThemeManager` / `MenuBuilder` / `DialogFactory`、控制台自检（`tests/ShellOpsCheck`）。

**Spec:** `docs/superpowers/specs/2026-09-21-shell-menu-design.md`

## Global Constraints

- 目标框架 net48，`LangVersion 7.3`（不用 C# 8+ 语法：无 switch 表达式、无 `using var`、无 `??=`、无范围/索引）。
- 所有 UI 文案走 `Localization`；新增 key 必须 en/zh/ja 三语齐全（`tests/LangCheck` 守护 key 集合一致、无空值、`{0}` 占位符在每种语言都能 `Format`）。
- 新增 `.cs` 不得出现十六进制颜色字面量（`tests/UiTokensCheck` 扫描 `.xaml`/`.cs`，只允许 `Core/UiTokens.cs` 里有）；一律用 `ThemeManager` 画刷。
- 浏览态写操作**只作用于磁盘**，不改 `_data.Items`、不触发 `OnDataChanged`；删除只走回收站，**绝不静默永久删除**。
- 原生菜单**先验证后接线**（Task 5 Step 2 通过后才接背景入口）；互操作失败只写 `Debug.WriteLine`，不弹异常。
- 每完成一个 Task 必须：`dotnet build Kobold.csproj -c Debug` 0 错误、0 新增警告；相关 `tests/*Check` 全绿。
- 手动验证运行 `bin\Debug\net48\Kobold.exe`。
- 提交信息用 conventional commits（`feat(shell-menu): ...`）。**提交前必须获得用户许可。**

---

## 文件结构

| 文件 | 责任 | 动作 |
|---|---|---|
| `Services/TerminalLauncher.cs` | 终端解析（wt → powershell → cmd）与启动；解析与三种转义均为纯函数 | 新建 |
| `Services/ShellFileOperations.cs` | 新建（文件/目录）、重命名、回收站删除；名称校验、删除预检、参数构造为纯函数 | 新建 |
| `Services/ShellContextMenuService.cs` | `IContextMenu` 互操作（条目 + 目录背景）、菜单消息转发、命令调用 | 新建 |
| `Controls/FolderWidget.MenuActions.cs` | 菜单装配、动作入口、多选规则、键盘通道、模态护栏 | 新建 partial |
| `Controls/FolderWidget.Browse.cs` | 删除旧的只读条目菜单；空白右键改走新入口 | 修改 |
| `Controls/FolderWidget.Interactions.cs` | 根态条目菜单抽成方法 + 终端项；`Deactivated` 护栏；`Window_KeyDown` 扩展；既有对话框改走模态包装 | 修改 |
| `Controls/FolderWidget.Dialogs.cs` | 组件重命名输入改走模态包装 | 修改 |
| `Controls/FolderWidget.xaml.cs` | `SourceInitialized` 时挂原生菜单消息 hook | 修改 |
| `Helpers/ContextMenuBuilder.cs` | `MenuBuilder.AddItem` 增加 `isEnabled` 可选参数 | 修改 |
| `Helpers/DialogFactory.cs` | `ShowInput` 增加可选校验回调（错误显示在对话框内，不关闭） | 修改 |
| `Core/Localization.cs` | 新增 18 个 key × 三语 | 修改 |
| `tests/ShellOpsCheck/`（csproj + Program.cs） | 终端解析/转义、名称校验、删除预检与参数、写操作临时目录实测 | 新建 |
| `README.md` | 功能表 + 用法表 + 自检清单（中英两节） | 修改 |

不改动：`FolderWidget.xaml`、`DisplayItem`、`FolderListing`、`Core/WidgetConstants.cs`。

---

### Task 1: 纯逻辑 + `tests/ShellOpsCheck`

**Files:**
- Create: `Services/TerminalLauncher.cs`（本 Task 只含解析/转义，`TryLaunch` 在 Task 4 加）
- Create: `Services/ShellFileOperations.cs`（本 Task 只含名称校验/删除预检/参数构造）
- Create: `tests/ShellOpsCheck/ShellOpsCheck.csproj`
- Create: `tests/ShellOpsCheck/Program.cs`

**Interfaces:**
- Produces:
  - `Kobold.Services.TerminalKind { WindowsTerminal, PowerShell, Cmd }`
  - `Kobold.Services.TerminalCommand { Kind; FileName; Arguments; }`
  - `Kobold.Services.TerminalLauncher.ExecutableFor(TerminalKind) -> string`
  - `Kobold.Services.TerminalLauncher.Resolve(string, Func<TerminalKind,bool>) -> TerminalCommand`
  - `Kobold.Services.TerminalLauncher.BuildArguments(TerminalKind, string) -> string`
  - `Kobold.Services.TerminalLauncher.QuoteForWindowsTerminal/QuoteForPowerShell/QuoteForCmd(string) -> string`
  - `Kobold.Services.NameCheck { Ok, Invalid, Exists }`
  - `Kobold.Services.ShellFileOperations.ValidateName(string, string, Func<string,bool>, string) -> NameCheck`
  - `Kobold.Services.ShellFileOperations.CanRecycle(string, Func<string,DriveType>, Func<string,bool>) -> bool`
  - `Kobold.Services.ShellFileOperations.ToDoubleNullTerminated(IEnumerable<string>) -> string`
  - `Kobold.Services.ShellFileOperations.DeleteFlags : ushort`

- [ ] **Step 1: 建测试工程**

`tests/ShellOpsCheck/ShellOpsCheck.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <AssemblyName>Kobold.ShellOpsCheck</AssemblyName>
    <RootNamespace>Kobold.ShellOpsCheck</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Kobold.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 写失败的自检**

`tests/ShellOpsCheck/Program.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kobold.Services;

namespace Kobold.ShellOpsCheck
{
    /// <summary>
    /// Checks for the shell-menu pure logic: terminal resolution/quoting,
    /// file-name validation, recycle-bin prechecks and delete parameters.
    /// No UI, no writes outside temp. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/ShellOpsCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            TerminalResolutionPrefersWindowsTerminal();
            TerminalResolutionFallsBack();
            TerminalResolutionReturnsNullWhenNothingIsAvailable();
            QuotingCoversSpacesUnicodeAndQuotes();
            QuotingHandlesTrailingBackslash();
            NameValidationRejectsBadInput();
            NameValidationChecksCollisions();
            NameValidationAllowsCaseOnlyRename();
            RecyclePrecheckAcceptsFixedDriveOnly();
            DeleteFlagsKeepTheRecycleBinSafe();
            DoubleNullTerminatedListing();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL SHELL-OPS CHECKS PASSED");
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

        // ---------- terminal ----------

        private static void TerminalResolutionPrefersWindowsTerminal()
        {
            var command = TerminalLauncher.Resolve(@"C:\work", k => true);

            Check(command != null && command.Kind == TerminalKind.WindowsTerminal,
                "resolve: wt.exe wins when available");
            Check(command != null && command.FileName.EndsWith("wt.exe", StringComparison.OrdinalIgnoreCase),
                "resolve: the wt candidate points at wt.exe");
            Check(command != null && command.Arguments == "-d \"C:\\work\"",
                "resolve: wt gets -d with a quoted path (got " + (command == null ? "<null>" : command.Arguments) + ")");
        }

        private static void TerminalResolutionFallsBack()
        {
            var ps = TerminalLauncher.Resolve(@"C:\work", k => k != TerminalKind.WindowsTerminal);
            Check(ps != null && ps.Kind == TerminalKind.PowerShell &&
                  ps.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase),
                "resolve: wt missing -> powershell.exe");

            var cmd = TerminalLauncher.Resolve(@"C:\work", k => k == TerminalKind.Cmd);
            Check(cmd != null && cmd.Kind == TerminalKind.Cmd &&
                  cmd.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase),
                "resolve: only cmd available -> cmd.exe");

            Check(TerminalLauncher.Resolve(null, k => true) == null &&
                  TerminalLauncher.Resolve("   ", k => true) == null,
                "resolve: null/blank directory -> null");
        }

        private static void TerminalResolutionReturnsNullWhenNothingIsAvailable()
        {
            Check(TerminalLauncher.Resolve(@"C:\work", k => false) == null,
                "resolve: no candidate available -> null");
        }

        private static void QuotingCoversSpacesUnicodeAndQuotes()
        {
            Check(TerminalLauncher.QuoteForWindowsTerminal(@"C:\my dir\中文") == "\"C:\\my dir\\中文\"",
                "quoting: wt quotes spaces and unicode");
            Check(TerminalLauncher.QuoteForCmd(@"C:\my dir\中文") == "\"C:\\my dir\\中文\"",
                "quoting: cmd quotes spaces and unicode");
            Check(TerminalLauncher.QuoteForPowerShell(@"C:\it's here") == "'C:\\it''s here'",
                "quoting: powershell doubles embedded single quotes");

            string psArgs = TerminalLauncher.BuildArguments(TerminalKind.PowerShell, @"C:\a b");
            Check(psArgs == "-NoExit -Command \"Set-Location -LiteralPath 'C:\\a b'\"",
                "quoting: powershell arguments wrap the literal path (got " + psArgs + ")");
        }

        private static void QuotingHandlesTrailingBackslash()
        {
            // CommandLineToArgvW would treat backslash-quote as an escaped quote.
            Check(TerminalLauncher.QuoteForWindowsTerminal(@"C:\") == "\"C:\\\\\"",
                "quoting: wt doubles a trailing backslash (got " + TerminalLauncher.QuoteForWindowsTerminal(@"C:\") + ")");
            Check(TerminalLauncher.QuoteForPowerShell(@"C:\") == "'C:\\'",
                "quoting: powershell needs no trailing-backslash fix");
        }

        // ---------- names ----------

        private static void NameValidationRejectsBadInput()
        {
            Func<string, bool> nothing = p => false;

            Check(ShellFileOperations.ValidateName(null, @"C:\d", nothing) == NameCheck.Invalid,
                "names: null -> Invalid");
            Check(ShellFileOperations.ValidateName("   ", @"C:\d", nothing) == NameCheck.Invalid,
                "names: blank -> Invalid");
            Check(ShellFileOperations.ValidateName("a/b", @"C:\d", nothing) == NameCheck.Invalid,
                "names: path separator -> Invalid");
            Check(ShellFileOperations.ValidateName("a:b", @"C:\d", nothing) == NameCheck.Invalid,
                "names: colon -> Invalid");
            Check(ShellFileOperations.ValidateName("name.", @"C:\d", nothing) == NameCheck.Invalid,
                "names: trailing dot -> Invalid");
            Check(ShellFileOperations.ValidateName("name ", @"C:\d", nothing) == NameCheck.Invalid,
                "names: trailing space -> Invalid");
            Check(ShellFileOperations.ValidateName("CON", @"C:\d", nothing) == NameCheck.Invalid,
                "names: reserved device name -> Invalid");
            Check(ShellFileOperations.ValidateName("nul.txt", @"C:\d", nothing) == NameCheck.Invalid,
                "names: reserved device name with extension -> Invalid");
        }

        private static void NameValidationChecksCollisions()
        {
            Check(ShellFileOperations.ValidateName("notes.txt", @"C:\d", p => p.EndsWith("notes.txt")) == NameCheck.Exists,
                "names: existing target -> Exists");
            Check(ShellFileOperations.ValidateName("other.txt", @"C:\d", p => false) == NameCheck.Ok,
                "names: fresh name -> Ok");
        }

        private static void NameValidationAllowsCaseOnlyRename()
        {
            string self = Path.Combine(@"C:\d", "notes.txt");
            Check(ShellFileOperations.ValidateName("NOTES.TXT", @"C:\d", p => true, self) == NameCheck.Ok,
                "names: case-only rename of the same item stays Ok");
            Check(ShellFileOperations.ValidateName("other.txt", @"C:\d", p => true, self) == NameCheck.Exists,
                "names: selfPath must not excuse collisions with a different target");
        }

        // ---------- recycle ----------

        private static void RecyclePrecheckAcceptsFixedDriveOnly()
        {
            string file = Path.Combine(Path.GetTempPath(), "kobold-shellops-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(file, "");
            try
            {
                Check(ShellFileOperations.CanRecycle(file, r => DriveType.Fixed, r => false),
                    "recycle: a real file on a fixed drive passes");
                Check(!ShellFileOperations.CanRecycle(file, r => DriveType.Removable, r => false),
                    "recycle: removable drive is refused");
                Check(!ShellFileOperations.CanRecycle(file, r => DriveType.Network, r => false),
                    "recycle: network drive is refused");
                Check(!ShellFileOperations.CanRecycle(file, r => DriveType.Fixed, r => true),
                    "recycle: subst drive is refused");
                Check(!ShellFileOperations.CanRecycle(file + ".missing", r => DriveType.Fixed, r => false),
                    "recycle: a path that does not exist is refused");
                Check(!ShellFileOperations.CanRecycle(null, r => DriveType.Fixed, r => false),
                    "recycle: null path is refused");
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        private static void DeleteFlagsKeepTheRecycleBinSafe()
        {
            const ushort FOF_ALLOWUNDO = 0x0040;
            const ushort FOF_NOCONFIRMATION = 0x0010;
            const ushort FOF_WANTNUKEWARNING = 0x4000;

            Check((ShellFileOperations.DeleteFlags & FOF_ALLOWUNDO) != 0,
                "flags: FOF_ALLOWUNDO is set (delete goes to the recycle bin)");
            Check((ShellFileOperations.DeleteFlags & FOF_WANTNUKEWARNING) != 0,
                "flags: FOF_WANTNUKEWARNING is set (a permanent delete still warns)");
            Check((ShellFileOperations.DeleteFlags & FOF_NOCONFIRMATION) != 0,
                "flags: FOF_NOCONFIRMATION is set (we confirm ourselves)");
        }

        private static void DoubleNullTerminatedListing()
        {
            string one = ShellFileOperations.ToDoubleNullTerminated(new[] { @"C:\a\b.txt" });
            Check(one == "C:\\a\\b.txt\0\0",
                "listing: a single path ends with a double NUL");

            string two = ShellFileOperations.ToDoubleNullTerminated(new[] { @"C:\a", @"D:\b" });
            Check(two == "C:\\a\0D:\\b\0\0",
                "listing: multiple paths are concatenated with single NULs and a double NUL tail");
        }
    }
}
```

- [ ] **Step 3: 写最小桩实现，让自检能编译并失败**

`Services/TerminalLauncher.cs`（桩）：

```csharp
using System;

namespace Kobold.Services
{
    public enum TerminalKind { WindowsTerminal, PowerShell, Cmd }

    public sealed class TerminalCommand
    {
        public TerminalKind Kind { get; set; }
        public string FileName { get; set; }
        public string Arguments { get; set; }
    }

    /// <summary>
    /// Resolves and starts a terminal at a directory. Resolution and quoting
    /// are pure so tests/ShellOpsCheck can cover them.
    /// </summary>
    public static class TerminalLauncher
    {
        public static string ExecutableFor(TerminalKind kind) => string.Empty;
        public static TerminalCommand Resolve(string directory, Func<TerminalKind, bool> isAvailable) => null;
        public static string BuildArguments(TerminalKind kind, string directory) => string.Empty;
        public static string QuoteForWindowsTerminal(string path) => string.Empty;
        public static string QuoteForPowerShell(string path) => string.Empty;
        public static string QuoteForCmd(string path) => string.Empty;
    }
}
```

`Services/ShellFileOperations.cs`（桩）：

```csharp
using System;
using System.Collections.Generic;
using System.IO;

namespace Kobold.Services
{
    public enum NameCheck { Ok, Invalid, Exists }

    public static class ShellFileOperations
    {
        public const ushort DeleteFlags = 0;

        public static NameCheck ValidateName(string name, string directory, Func<string, bool> exists, string selfPath = null)
            => NameCheck.Invalid;

        public static bool CanRecycle(string path, Func<string, DriveType> driveType, Func<string, bool> isSubst)
            => false;

        public static string ToDoubleNullTerminated(IEnumerable<string> paths) => string.Empty;
    }
}
```

- [ ] **Step 4: 运行自检，确认失败**

Run: `dotnet run --project tests/ShellOpsCheck`
Expected: 退出码 1，`FAILURES (n):` 列表 n ≥ 12，无未捕获异常。

- [ ] **Step 5: 实现真实逻辑**

`Services/TerminalLauncher.cs`（替换整个文件）：

```csharp
using System;
using System.IO;

namespace Kobold.Services
{
    public enum TerminalKind { WindowsTerminal, PowerShell, Cmd }

    public sealed class TerminalCommand
    {
        public TerminalKind Kind { get; set; }
        public string FileName { get; set; }
        public string Arguments { get; set; }
    }

    /// <summary>
    /// Resolves and starts a terminal at a directory. Resolution and quoting
    /// are pure so tests/ShellOpsCheck can cover them.
    /// </summary>
    public static class TerminalLauncher
    {
        private static readonly TerminalKind[] Order =
        {
            TerminalKind.WindowsTerminal, TerminalKind.PowerShell, TerminalKind.Cmd
        };

        public static string ExecutableFor(TerminalKind kind)
        {
            switch (kind)
            {
                case TerminalKind.WindowsTerminal:
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\WindowsApps\wt.exe");
                case TerminalKind.PowerShell:
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe");
                default:
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            }
        }

        /// <summary>First available candidate in fallback order; null when none is available.</summary>
        public static TerminalCommand Resolve(string directory, Func<TerminalKind, bool> isAvailable)
        {
            if (string.IsNullOrWhiteSpace(directory)) return null;

            foreach (var kind in Order)
            {
                if (isAvailable != null && !isAvailable(kind)) continue;
                return new TerminalCommand
                {
                    Kind = kind,
                    FileName = ExecutableFor(kind),
                    Arguments = BuildArguments(kind, directory)
                };
            }
            return null;
        }

        public static string BuildArguments(TerminalKind kind, string directory)
        {
            switch (kind)
            {
                case TerminalKind.WindowsTerminal:
                    return "-d " + QuoteForWindowsTerminal(directory);
                case TerminalKind.PowerShell:
                    return "-NoExit -Command \"Set-Location -LiteralPath " + QuoteForPowerShell(directory) + "\"";
                default:
                    return "/K cd /d " + QuoteForCmd(directory);
            }
        }

        public static string QuoteForWindowsTerminal(string path)
        {
            string value = path ?? string.Empty;
            // CommandLineToArgvW treats a backslash before the closing quote as an
            // escape, so a drive root ("C:\") needs one more backslash.
            if (value.EndsWith("\\", StringComparison.Ordinal)) value += "\\";
            return "\"" + value + "\"";
        }

        public static string QuoteForPowerShell(string path)
        {
            return "'" + (path ?? string.Empty).Replace("'", "''") + "'";
        }

        public static string QuoteForCmd(string path)
        {
            return "\"" + (path ?? string.Empty) + "\"";
        }
    }
}
```

`Services/ShellFileOperations.cs`（替换整个文件；本 Task 只含纯逻辑，写操作在 Task 2 追加）：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Kobold.Services
{
    public enum NameCheck { Ok, Invalid, Exists }

    /// <summary>
    /// File actions for the in-panel browser (create, rename, recycle).
    /// Validation, prechecks and parameter building are pure so
    /// tests/ShellOpsCheck can cover them; the OS calls are thin wrappers.
    /// </summary>
    public static class ShellFileOperations
    {
        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>Name rules shared by New and Rename. selfPath allows a case-only rename.</summary>
        public static NameCheck ValidateName(string name, string directory, Func<string, bool> exists, string selfPath = null)
        {
            string trimmed = name == null ? null : name.Trim();
            if (string.IsNullOrEmpty(trimmed)) return NameCheck.Invalid;

            // Leading/trailing whitespace is rejected instead of silently trimmed:
            // Windows strips it, so the created name would differ from the typed one.
            if (!string.Equals(name, trimmed, StringComparison.Ordinal)) return NameCheck.Invalid;
            if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return NameCheck.Invalid;
            if (trimmed.EndsWith(".", StringComparison.Ordinal)) return NameCheck.Invalid;
            if (IsReservedDeviceName(trimmed)) return NameCheck.Invalid;

            string target = Path.Combine(directory ?? string.Empty, trimmed);
            if (selfPath != null && SamePath(target, selfPath)) return NameCheck.Ok;
            return exists != null && exists(target) ? NameCheck.Exists : NameCheck.Ok;
        }

        /// <summary>True when the path can be expected to land in a recycle bin.</summary>
        public static bool CanRecycle(string path, Func<string, DriveType> driveType, Func<string, bool> isSubst)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!File.Exists(path) && !Directory.Exists(path)) return false;

            string root;
            try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
            catch (Exception) { return false; }
            if (string.IsNullOrEmpty(root)) return false;

            if (isSubst != null && isSubst(root)) return false;
            return driveType == null || driveType(root) == DriveType.Fixed;
        }

        /// <summary>Builds the double-NUL terminated list SHFileOperation expects.</summary>
        public static string ToDoubleNullTerminated(IEnumerable<string> paths)
        {
            var builder = new StringBuilder();
            if (paths != null)
            {
                foreach (var path in paths)
                {
                    if (!string.IsNullOrEmpty(path)) builder.Append(path).Append('\0');
                }
            }
            return builder.Append('\0').ToString();
        }

        private static bool IsReservedDeviceName(string name)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            foreach (var reserved in ReservedDeviceNames)
            {
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Normalize(string path)
        {
            return Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal const ushort FOF_NOCONFIRMATION = 0x0010;
        internal const ushort FOF_ALLOWUNDO = 0x0040;
        internal const ushort FOF_NOERRORUI = 0x0400;
        internal const ushort FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>
        /// Recycle to the bin; Kobold already confirmed, but a file that cannot
        /// be recycled must still make the shell warn before it nukes it.
        /// </summary>
        public const ushort DeleteFlags =
            FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI;
    }
}
```

- [ ] **Step 6: 构建 + 运行自检，确认全绿**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 errors
Run: `dotnet run --project tests/ShellOpsCheck` → Expected: `ALL SHELL-OPS CHECKS PASSED`，退出码 0

- [ ] **Step 7: 提交（需用户许可）**

```bash
git add Services/TerminalLauncher.cs Services/ShellFileOperations.cs tests/ShellOpsCheck docs/superpowers/specs/2026-09-21-shell-menu-design.md docs/superpowers/plans/2026-09-21-shell-menu.md
git commit -m "feat(shell-menu): add terminal resolution and file-name/recycle pure logic"
```

（`.gitignore` 的 `.worktrees/` 规则在主 checkout 工作区，合并时随分支一起收尾，不放进本 Task 的提交。）

---

### Task 2: 写操作（新建/重命名/回收站删除）

**Files:**
- Modify: `Services/ShellFileOperations.cs`
- Modify: `tests/ShellOpsCheck/Program.cs`

**Interfaces:**
- Consumes: `NameCheck` / `ValidateName` / `CanRecycle` / `ToDoubleNullTerminated`（Task 1）
- Produces:
  - `ShellFileOperations.CreateFolder(string, string, out string) -> bool`
  - `ShellFileOperations.CreateTextFile(string, string, out string) -> bool`
  - `ShellFileOperations.Rename(string, string, out string) -> bool`
  - `ShellFileOperations.GetDriveType(string) -> DriveType`
  - `ShellFileOperations.IsSubstDrive(string) -> bool`
  - `ShellFileOperations.Recycle(IList<string>, IntPtr) -> int`（返回失败项数）
  - `ShellFileOperations.DeleteFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI`

- [ ] **Step 1: 扩展自检（先失败）**

`tests/ShellOpsCheck/Program.cs`：在 `Main` 里 `DoubleNullTerminatedListing();` 之后加一行 `CreateAndRenameInTemp();`；在 `DoubleNullTerminatedListing` 方法之后新增：

```csharp
        private static void CreateAndRenameInTemp()
        {
            string root = Path.Combine(Path.GetTempPath(), "kobold-shellops-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string error;
                Check(ShellFileOperations.CreateFolder(root, "sub", out error) &&
                      Directory.Exists(Path.Combine(root, "sub")),
                    "create: a folder appears in the target directory");

                Check(ShellFileOperations.CreateTextFile(root, "note.txt", out error) &&
                      File.Exists(Path.Combine(root, "note.txt")),
                    "create: a text file appears in the target directory");

                File.WriteAllText(Path.Combine(root, "note.txt"), "keep");
                Check(!ShellFileOperations.CreateTextFile(root, "note.txt", out error) &&
                      File.ReadAllText(Path.Combine(root, "note.txt")) == "keep",
                    "create: an existing file is never overwritten");

                Check(ShellFileOperations.Rename(Path.Combine(root, "note.txt"), "renamed.txt", out error) &&
                      File.Exists(Path.Combine(root, "renamed.txt")) &&
                      !File.Exists(Path.Combine(root, "note.txt")),
                    "rename: a file is moved to the new name");

                Check(ShellFileOperations.Rename(Path.Combine(root, "renamed.txt"), "RENAMED.TXT", out error) &&
                      File.Exists(Path.Combine(root, "renamed.txt")),
                    "rename: a case-only rename succeeds");

                File.WriteAllText(Path.Combine(root, "other.txt"), "other");
                Check(!ShellFileOperations.Rename(Path.Combine(root, "renamed.txt"), "other.txt", out error) &&
                      File.Exists(Path.Combine(root, "renamed.txt")) &&
                      File.ReadAllText(Path.Combine(root, "other.txt")) == "other",
                    "rename: an existing target is refused and both files stay intact");

                Check(!ShellFileOperations.Rename(Path.Combine(root, "missing.txt"), "x.txt", out error),
                    "rename: a missing source reports failure");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
```

- [ ] **Step 2: 运行自检，确认新用例失败（编译失败也算符合预期）**

Run: `dotnet run --project tests/ShellOpsCheck`
Expected: 退出码非 0（`CreateFolder`/`CreateTextFile`/`Rename` 尚不存在，编译失败即测试先于实现）。

- [ ] **Step 3: 实现写操作与回收站**

`Services/ShellFileOperations.cs`：

1. 顶部 using 增加 `using System.Diagnostics;` 与 `using System.Runtime.InteropServices;`。
2. 在 `Normalize` 之后、`DeleteFlags` 之前插入：

```csharp
        /// <summary>Creates a folder. Fails (with a message) instead of throwing.</summary>
        public static bool CreateFolder(string directory, string name, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Path.Combine(directory, name.Trim()));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Creates an empty text file. CreateNew refuses to overwrite.</summary>
        public static bool CreateTextFile(string directory, string name, out string error)
        {
            error = null;
            try
            {
                using (new FileStream(Path.Combine(directory, name.Trim()), FileMode.CreateNew)) { }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Renames a file or folder. Fails without touching anything when the target exists.</summary>
        public static bool Rename(string path, string newName, out string error)
        {
            error = null;
            try
            {
                string target = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, newName.Trim());
                if (string.Equals(target, path, StringComparison.Ordinal)) return true;

                if (Directory.Exists(path)) Directory.Move(path, target);
                else if (File.Exists(path)) File.Move(path, target);
                else
                {
                    error = "path not found";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Drive type of a path root; Unknown when it cannot be read.</summary>
        public static DriveType GetDriveType(string root)
        {
            try { return new DriveInfo(root).DriveType; }
            catch (Exception) { return DriveType.Unknown; }
        }

        /// <summary>True when the drive letter is a subst alias (its recycle bin behaviour is unreliable).</summary>
        public static bool IsSubstDrive(string root)
        {
            try
            {
                string device = (root ?? string.Empty).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (device.Length == 0) return false;

                var buffer = new StringBuilder(1024);
                if (QueryDosDevice(device, buffer, (uint)buffer.Capacity) == 0) return false;
                return buffer.ToString().StartsWith(@"\??\", StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Sends each path to the recycle bin through the shell. Returns the
        /// number of items that could not be removed. Never permanently
        /// deletes silently: when the shell cannot recycle, FOF_WANTNUKEWARNING
        /// makes it ask before nuking.
        /// </summary>
        public static int Recycle(IList<string> paths, IntPtr ownerHwnd)
        {
            if (paths == null) return 0;

            int failed = 0;
            foreach (var path in paths)
            {
                if (!RecycleOne(path, ownerHwnd)) failed++;
            }
            return failed;
        }

        private static bool RecycleOne(string path, IntPtr ownerHwnd)
        {
            var operation = new SHFILEOPSTRUCT
            {
                hwnd = ownerHwnd,
                wFunc = FO_DELETE,
                pFrom = ToDoubleNullTerminated(new[] { path }),
                fFlags = DeleteFlags
            };

            try
            {
                int result = SHFileOperation(ref operation);
                if (result != 0 || operation.fAnyOperationsAborted) return false;

                // Some locations report success without removing anything;
                // "still there" is a failure.
                return !File.Exists(path) && !Directory.Exists(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Recycle failed: " + ex.Message);
                return false;
            }
        }

        private const uint FO_DELETE = 0x0003;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
```

3. `DeleteFlags` 与四个 `FOF_*` 常量已在 Task 1 落地，本 Task 不再改动（自检里的 flags 用例已在 Task 1 通过）。

- [ ] **Step 4: 构建 + 运行自检**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 errors
Run: `dotnet run --project tests/ShellOpsCheck` → Expected: 全绿

- [ ] **Step 5: 提交（需用户许可）**

```bash
git add Services/ShellFileOperations.cs tests/ShellOpsCheck/Program.cs
git commit -m "feat(shell-menu): add create/rename/recycle file operations"
```

---

### Task 3: 基础设施（对话框校验回调、菜单可用态、模态护栏、三语文案）

**Files:**
- Modify: `Helpers/DialogFactory.cs`
- Modify: `Helpers/ContextMenuBuilder.cs`
- Create: `Controls/FolderWidget.MenuActions.cs`（本 Task 只放护栏与包装方法）
- Modify: `Controls/FolderWidget.Interactions.cs`
- Modify: `Controls/FolderWidget.Dialogs.cs`
- Modify: `Core/Localization.cs`

**Interfaces:**
- Produces:
  - `DialogFactory.ShowInput(Window, string, string, string, Func<string,string>)`（第 5 参数可选；返回非 null 时显示错误、不关闭）
  - `MenuBuilder.AddItem(string, Action, bool isEnabled = true)`
  - `FolderWidget.RunModal(Action)` / `ShowInputModal(...)` / `ShowConfirmModal(string)` / `ShowMessage(string)` / `_modalDepth`

- [ ] **Step 1: `DialogFactory.ShowInput` 增加校验回调**

`Helpers/DialogFactory.cs`：

1. 方法签名改为：

```csharp
        public static string ShowInput(Window owner, string title, string prompt, string initialValue,
            Func<string, string> validate = null)
        {
```

2. 在 `textBox` 创建之后、`stock` 结果变量声明处按下列结构改写（`result` 保持原声明，新增 `errorText` 与 `accept`）：

```csharp
            string result = null;

            var errorText = new TextBlock
            {
                Foreground = ThemeManager.DangerBrush,
                FontSize = UiTokens.FontBody,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, UiTokens.Space2, 0, 0)
            };

            Action accept = () =>
            {
                string value = textBox.Text;
                string error = validate == null ? null : validate(value);
                if (error != null)
                {
                    errorText.Text = error;
                    errorText.Visibility = Visibility.Visible;
                    textBox.Focus();
                    return;
                }
                result = value;
                dialog.Close();
            };
```

3. OK 按钮改为 `okButton.Click += (s, e) => accept();`；文本框回车处理改为：

```csharp
            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    accept();
                    e.Handled = true;
                }
            };
```

4. 在 `content.Children.Add(textBox);` 之后插入 `content.Children.Add(errorText);`。

- [ ] **Step 2: `MenuBuilder.AddItem` 支持灰显**

`Helpers/ContextMenuBuilder.cs`：把 `AddItem` 替换为：

```csharp
        /// <summary>
        /// Add a simple menu item with click handler. isEnabled=false renders it
        /// greyed out (used when a browse selection makes the action ambiguous).
        /// </summary>
        public MenuBuilder AddItem(string localizationKey, Action onClick, bool isEnabled = true)
        {
            var item = new MenuItem { Header = Localization.Get(localizationKey), IsEnabled = isEnabled };
            item.Click += (s, e) => onClick?.Invoke();
            _menu.Items.Add(item);
            return this;
        }
```

- [ ] **Step 3: 新建 `MenuActions` partial（护栏与包装方法）**

`Controls/FolderWidget.MenuActions.cs`：

```csharp
using System;
using System.Windows;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - menus and file actions. Browsing may write to disk through
    /// explicit actions, but it never touches _data.Items.
    /// </summary>
    public partial class FolderWidget
    {
        // >0 while one of our own modal dialogs is up: Deactivated must not
        // collapse the panel, or a New/Rename/Delete prompt would reset browse.
        private int _modalDepth;

        /// <summary>Shows a modal dialog while shielding the panel from focus-loss collapse.</summary>
        private void RunModal(Action show)
        {
            if (show == null) return;

            _modalDepth++;
            try
            {
                show();
            }
            finally
            {
                _modalDepth--;
                if (_modalDepth == 0 && _isExpanded && !_data.IsPanelPinned && !IsActive)
                {
                    HidePanel();
                }
            }
        }

        private string ShowInputModal(string title, string prompt, string initialValue, Func<string, string> validate)
        {
            string result = null;
            RunModal(() => { result = DialogFactory.ShowInput(this, title, prompt, initialValue, validate); });
            return result;
        }

        private bool ShowConfirmModal(string message)
        {
            var answer = MessageBoxResult.No;
            RunModal(() =>
            {
                answer = MessageBox.Show(this, message, Localization.Get("Dialog_Confirm"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
            });
            return answer == MessageBoxResult.Yes;
        }

        private void ShowMessage(string message)
        {
            RunModal(() => MessageBox.Show(this, message, "Kobold", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }
}
```

- [ ] **Step 4: 护栏接入既有流程**

`Controls/FolderWidget.Interactions.cs`：

1. `Window_Deactivated` 改为：

```csharp
        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Always clear selection when window loses focus
            ClearAllSelections();

            // Our own modal dialog is up: keep the panel alive underneath it
            // (HidePanel would also reset the browse stack).
            if (_modalDepth > 0) return;

            // Don't close panel if pinned
            if (_isExpanded && !_data.IsPanelPinned)
            {
                HidePanel();
            }
        }
```

2. `CreateNewFile` 的输入调用改为：

```csharp
            string fileName = ShowInputModal(
                Localization.Get("Dialog_NewFile_Title"),
                Localization.Get("Dialog_NewFile_Prompt"),
                "NewFile.txt",
                null);
```

`CreateNewFolder` 的输入调用改为：

```csharp
            string folderName = ShowInputModal(
                Localization.Get("Dialog_NewFolder_Title"),
                Localization.Get("Dialog_NewFolder_Prompt"),
                "NewFolder",
                null);
```

3. 这两个方法 catch 里的 `MessageBox.Show($"Error creating ...")` 改为 `ShowMessage($"Error creating ...")`（文案保持原样）。

4. `RenameItem` 的输入与 catch 改为：

```csharp
            string newName = ShowInputModal(
                Localization.Get("Dialog_RenameItem_Title"),
                Localization.Get("Dialog_RenameItem_Prompt"),
                currentName,
                null);
```

```csharp
            catch (Exception ex)
            {
                ShowMessage($"Error renaming: {ex.Message}");
            }
```

5. `CreateDeleteMenuItem` 的确认框改为：

```csharp
            item.Click += (s, a) =>
            {
                if (ShowConfirmModal(Localization.Format("Dialog_DeleteWidget", _data.Name)))
                {
                    EjectAllItems();
                    OnDeleted?.Invoke(this);
                    Close();
                }
            };
```

6. `StoreItemIntoWidget` 的失败提示改为 `ShowMessage(Localization.Get("Dialog_StoreFailed"));`

`Controls/FolderWidget.Dialogs.cs`：`ShowRenameDialog` 的输入改为：

```csharp
            string newName = ShowInputModal(
                Localization.Get("Dialog_Rename"),
                Localization.Get("Dialog_EnterName"),
                _data.Name,
                null);
```

- [ ] **Step 5: 三语文案（spec §9 全表，18 个 key）**

`Core/Localization.cs`：在**每个**语言块的最后一项 `["Menu_CopyPath"] = ...` 之前插入对应语言的 18 行（新行都带逗号，`Menu_CopyPath` 仍为最后一项不带逗号）：

en：

```
                ["Menu_OpenTerminal"] = "💻 Open in Terminal",
                ["Menu_OpenInExplorer"] = "📁 Open in Explorer",
                ["Menu_ShowMoreOptions"] = "Show more options",
                ["Menu_Refresh"] = "🔄 Refresh",
                ["Menu_NewTextFile"] = "📄 New Text Document",
                ["Menu_DeleteItem"] = "🗑️ Delete",
                ["Dialog_NewTextFile_Title"] = "New Text Document",
                ["Dialog_RecycleItem"] = "Move '{0}' to the Recycle Bin?",
                ["Dialog_RecycleItems"] = "Move {0} items to the Recycle Bin?",
                ["Dialog_RecycleFailed"] = "{0} item(s) could not be moved to the Recycle Bin.",
                ["Dialog_RecycleNotLocal"] = "Only items on local fixed drives can be moved to the Recycle Bin.",
                ["Dialog_NameInvalid"] = "That name can't be used.",
                ["Dialog_NameExists"] = "An item with that name already exists.",
                ["Dialog_CreateFailed"] = "Couldn't create the item: {0}",
                ["Dialog_RenameFailed"] = "Couldn't rename the item: {0}",
                ["Dialog_TerminalNotFound"] = "No terminal was found (Windows Terminal, PowerShell or cmd).",
                ["UI_NewFolderDefault"] = "New Folder",
                ["UI_NewTextFileDefault"] = "New Text Document.txt",
```

zh：

```
                ["Menu_OpenTerminal"] = "💻 在终端中打开",
                ["Menu_OpenInExplorer"] = "📁 在资源管理器中打开",
                ["Menu_ShowMoreOptions"] = "显示更多选项",
                ["Menu_Refresh"] = "🔄 刷新",
                ["Menu_NewTextFile"] = "📄 新建文本文档",
                ["Menu_DeleteItem"] = "🗑️ 删除",
                ["Dialog_NewTextFile_Title"] = "新建文本文档",
                ["Dialog_RecycleItem"] = "将“{0}”移入回收站？",
                ["Dialog_RecycleItems"] = "将 {0} 个项目移入回收站？",
                ["Dialog_RecycleFailed"] = "有 {0} 项未能移入回收站。",
                ["Dialog_RecycleNotLocal"] = "只有本机固定磁盘上的项目才能移入回收站。",
                ["Dialog_NameInvalid"] = "该名称不可用。",
                ["Dialog_NameExists"] = "已存在同名的项目。",
                ["Dialog_CreateFailed"] = "无法创建：{0}",
                ["Dialog_RenameFailed"] = "无法重命名：{0}",
                ["Dialog_TerminalNotFound"] = "未找到可用的终端（Windows Terminal / PowerShell / cmd）。",
                ["UI_NewFolderDefault"] = "新建文件夹",
                ["UI_NewTextFileDefault"] = "新建文本文档.txt",
```

ja：

```
                ["Menu_OpenTerminal"] = "💻 ターミナルで開く",
                ["Menu_OpenInExplorer"] = "📁 エクスプローラーで開く",
                ["Menu_ShowMoreOptions"] = "その他のオプションを表示",
                ["Menu_Refresh"] = "🔄 更新",
                ["Menu_NewTextFile"] = "📄 新しいテキスト ドキュメント",
                ["Menu_DeleteItem"] = "🗑️ 削除",
                ["Dialog_NewTextFile_Title"] = "新しいテキスト ドキュメント",
                ["Dialog_RecycleItem"] = "「{0}」をゴミ箱に移動しますか？",
                ["Dialog_RecycleItems"] = "{0} 個の項目をゴミ箱に移動しますか？",
                ["Dialog_RecycleFailed"] = "{0} 個の項目をゴミ箱に移動できませんでした。",
                ["Dialog_RecycleNotLocal"] = "ローカル固定ドライブ上の項目のみゴミ箱に移動できます。",
                ["Dialog_NameInvalid"] = "その名前は使用できません。",
                ["Dialog_NameExists"] = "同じ名前の項目が既に存在します。",
                ["Dialog_CreateFailed"] = "作成できませんでした: {0}",
                ["Dialog_RenameFailed"] = "名前を変更できませんでした: {0}",
                ["Dialog_TerminalNotFound"] = "使用できるターミナルが見つかりません（Windows Terminal / PowerShell / cmd）。",
                ["UI_NewFolderDefault"] = "新しいフォルダー",
                ["UI_NewTextFileDefault"] = "新しいテキスト ドキュメント.txt",
```

- [ ] **Step 6: 构建 + 相关自检**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 errors
Run: `dotnet run --project tests/LangCheck`、`tests/UiTokensCheck`、`tests/XamlLoadCheck` → Expected: 全绿

- [ ] **Step 7: 手动验证对话框不回归**

Run: `bin\Debug\net48\Kobold.exe`
1. 未固定面板：条目右键 → 重命名 → 输入非法字符（如 `a/b`）→ 对话框内红色错误且不关闭；改回合法名可保存。
2. 根态"新建文件/文件夹"、"删除组件"确认、"收纳失败"提示照常；未固定面板在对话框期间不收起。

- [ ] **Step 8: 提交（需用户许可）**

```bash
git add Helpers/DialogFactory.cs Helpers/ContextMenuBuilder.cs Controls/FolderWidget.MenuActions.cs Controls/FolderWidget.Interactions.cs Controls/FolderWidget.Dialogs.cs Core/Localization.cs
git commit -m "feat(shell-menu): add validated dialogs, disabled menu items and a modal guard"
```

---

### Task 4: 自建菜单与动作接入

**Files:**
- Modify: `Services/TerminalLauncher.cs`（追加 `TryLaunch`）
- Modify: `Controls/FolderWidget.MenuActions.cs`（追加动作与菜单装配；原生菜单相关方法留到 Task 5）
- Modify: `Controls/FolderWidget.Browse.cs`（删除旧只读菜单）
- Modify: `Controls/FolderWidget.Interactions.cs`（根态菜单抽方法 + 终端项；空白右键改入口；键盘扩展）

**Interfaces:**
- Consumes: Task 1-3 的全部产出
- Produces（`MenuActions` 内 private 方法）：
  - `ShowBrowseItemMenu(DisplayItem, FrameworkElement anchor = null)`
  - `ShowBrowseBackgroundMenu(FrameworkElement anchor = null)`
  - `ShowRootItemMenu(DisplayItem, FrameworkElement anchor = null)`
  - `HandleMenuActionKey(KeyEventArgs) -> bool`
  - `ShowMenu(ContextMenu, FrameworkElement)` / `GetItemContainer(DisplayItem)`

- [ ] **Step 1: `TerminalLauncher.TryLaunch`**

`Services/TerminalLauncher.cs`：顶部 using 增加 `using System.Diagnostics;`；在 `QuoteForCmd` 之后追加：

```csharp
        /// <summary>
        /// Starts the first terminal that is installed; on a launch failure it
        /// falls through to the next candidate. Returns false when none works.
        /// </summary>
        public static bool TryLaunch(string directory, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                error = "directory not found";
                return false;
            }

            foreach (var kind in Order)
            {
                string executable = ExecutableFor(kind);
                if (!File.Exists(executable)) continue;

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = BuildArguments(kind, directory),
                        UseShellExecute = true
                    });
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Kobold] Terminal launch failed (" + kind + "): " + ex.Message);
                }
            }

            error = "no terminal could be started";
            return false;
        }
```

- [ ] **Step 2: `MenuActions` 追加动作方法**

`Controls/FolderWidget.MenuActions.cs`：using 区补齐：

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kobold.Core;
using Kobold.Helpers;
using Kobold.Services;
using Localization = Kobold.Core.Localization;
```

在 `ShowMessage` 之后追加：

```csharp
        private IntPtr PanelHwnd
        {
            get
            {
                try { return new System.Windows.Interop.WindowInteropHelper(this).Handle; }
                catch (Exception) { return IntPtr.Zero; }
            }
        }

        private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

        private string NameErrorText(NameCheck check)
        {
            if (check == NameCheck.Exists) return Localization.Get("Dialog_NameExists");
            if (check == NameCheck.Invalid) return Localization.Get("Dialog_NameInvalid");
            return null;
        }

        private void RefreshPanel()
        {
            ClearAllSelections();
            UpdateUI();
        }

        // ---------- open / terminal / explorer / copy ----------

        private void OpenItem(DisplayItem item)
        {
            if (item == null) return;
            if (item.IsDirectory) EnterFolder(item.Path);
            else OpenWithShell(item.Path);
        }

        private void OpenTerminalFor(string path, bool isDirectory)
        {
            string directory = isDirectory ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return;

            string error;
            if (!TerminalLauncher.TryLaunch(directory, out error))
            {
                ShowMessage(Localization.Get("Dialog_TerminalNotFound"));
            }
        }

        private void OpenInExplorer(string path, bool isDirectory)
        {
            try
            {
                if (isDirectory) Process.Start("explorer.exe", "\"" + path + "\"");
                else OpenContainingFolder(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Open in Explorer failed: " + ex.Message);
            }
        }

        private void CopySelectedPaths(List<DisplayItem> selected)
        {
            if (selected == null || selected.Count == 0) return;

            string text = selected.Count == 1
                ? selected[0].Path
                : string.Join(Environment.NewLine, selected.Select(i => i.Path));
            CopyPathToClipboard(text);
        }

        // ---------- create / rename / delete ----------

        private void CreateBrowseFolder(string directory)
        {
            string name = ShowInputModal(
                Localization.Get("Dialog_NewFolder_Title"),
                Localization.Get("Dialog_NewFolder_Prompt"),
                Localization.Get("UI_NewFolderDefault"),
                n => NameErrorText(ShellFileOperations.ValidateName(n, directory, PathExists)));
            if (name == null) return;

            string error;
            if (!ShellFileOperations.CreateFolder(directory, name, out error))
            {
                ShowMessage(Localization.Format("Dialog_CreateFailed", error));
                return;
            }
            RefreshPanel();
        }

        private void CreateBrowseTextFile(string directory)
        {
            string name = ShowInputModal(
                Localization.Get("Dialog_NewTextFile_Title"),
                Localization.Get("Dialog_NewFile_Prompt"),
                Localization.Get("UI_NewTextFileDefault"),
                n => NameErrorText(ShellFileOperations.ValidateName(n, directory, PathExists)));
            if (name == null) return;

            string error;
            if (!ShellFileOperations.CreateTextFile(directory, name, out error))
            {
                ShowMessage(Localization.Format("Dialog_CreateFailed", error));
                return;
            }
            RefreshPanel();
        }

        private void RenameBrowseItem(DisplayItem item)
        {
            if (item == null) return;

            string directory = Path.GetDirectoryName(item.Path);
            string currentName = Path.GetFileName(item.Path);

            string newName = ShowInputModal(
                Localization.Get("Dialog_RenameItem_Title"),
                Localization.Get("Dialog_RenameItem_Prompt"),
                currentName,
                n => NameErrorText(ShellFileOperations.ValidateName(n, directory, PathExists, item.Path)));
            if (string.IsNullOrEmpty(newName)) return;

            string error;
            if (!ShellFileOperations.Rename(item.Path, newName, out error))
            {
                ShowMessage(Localization.Format("Dialog_RenameFailed", error));
                return;
            }
            RefreshPanel();
        }

        private void DeleteToRecycleBin(List<DisplayItem> selected)
        {
            if (selected == null || selected.Count == 0) return;

            var paths = selected.Select(i => i.Path).ToList();
            foreach (var path in paths)
            {
                if (!ShellFileOperations.CanRecycle(path,
                        ShellFileOperations.GetDriveType, ShellFileOperations.IsSubstDrive))
                {
                    ShowMessage(Localization.Get("Dialog_RecycleNotLocal"));
                    return;
                }
            }

            string message = paths.Count == 1
                ? Localization.Format("Dialog_RecycleItem", Path.GetFileName(paths[0]))
                : Localization.Format("Dialog_RecycleItems", paths.Count);
            if (!ShowConfirmModal(message)) return;

            int failed = ShellFileOperations.Recycle(paths, PanelHwnd);
            RefreshPanel();
            if (failed > 0)
            {
                ShowMessage(Localization.Format("Dialog_RecycleFailed", failed));
            }
        }
```

- [ ] **Step 3: `MenuActions` 追加菜单装配与键盘通道**

`Controls/FolderWidget.MenuActions.cs` 追加：

```csharp
        // ---------- menus ----------

        private void ShowBrowseItemMenu(DisplayItem item, FrameworkElement anchor = null)
        {
            if (item == null) return;

            // Explorer semantics: right-clicking an unselected item selects it first.
            if (!item.IsSelected)
            {
                ClearAllSelections();
                item.IsSelected = true;
            }

            var selected = GetSelectedItems();
            bool single = selected.Count == 1;

            var menu = new MenuBuilder(_data.Color)
                .AddItem("Menu_Open", () => OpenItem(item), single)
                .AddItem("Menu_OpenTerminal", () => OpenTerminalFor(item.Path, item.IsDirectory), single)
                .AddItem("Menu_OpenInExplorer", () => OpenInExplorer(item.Path, item.IsDirectory), single)
                .AddItem("Menu_CopyPath", () => CopySelectedPaths(selected))
                .AddSeparator()
                .AddItem("Menu_RenameItem", () => RenameBrowseItem(item), single)
                .AddItem("Menu_DeleteItem", () => DeleteToRecycleBin(selected))
                .Build();

            ShowMenu(menu, anchor);
        }

        private void ShowBrowseBackgroundMenu(FrameworkElement anchor = null)
        {
            string directory = CurrentBrowsePath;
            if (directory == null) return;

            var menu = new MenuBuilder(_data.Color)
                .AddItem("Menu_NewFolder", () => CreateBrowseFolder(directory))
                .AddItem("Menu_NewTextFile", () => CreateBrowseTextFile(directory))
                .AddSeparator()
                .AddItem("Menu_OpenTerminal", () => OpenTerminalFor(directory, true))
                .AddItem("Menu_OpenInExplorer", () => OpenInExplorer(directory, true))
                .AddItem("Menu_CopyPath", () => CopyPathToClipboard(directory))
                .AddItem("Menu_Refresh", RefreshPanel)
                .Build();

            ShowMenu(menu, anchor);
        }

        /// <summary>Root (widget items) menu: the previous behaviour plus Open in Terminal.</summary>
        private void ShowRootItemMenu(DisplayItem item, FrameworkElement anchor = null)
        {
            var menu = new ContextMenu();

            var openItem = new MenuItem { Header = Localization.Get("Menu_Open") };
            openItem.Click += (s, a) => OpenWithShell(item.Path);

            var locItem = new MenuItem { Header = Localization.Get("Menu_OpenLocation") };
            locItem.Click += (s, a) => OpenContainingFolder(item.Path);

            var terminalItem = new MenuItem
            {
                Header = Localization.Get("Menu_OpenTerminal"),
                IsEnabled = item.IsDirectory && !item.IsMissing
            };
            terminalItem.Click += (s, a) => OpenTerminalFor(item.Path, true);

            // Store physically into Kobold storage (explicit action - by
            // default dropped files are references and stay in place).
            // Locked widgets forbid moving content in or out.
            var dataItem = _data.Items.FirstOrDefault(i => i.Path == item.Path);
            if (dataItem != null && dataItem.IsReference && !item.IsMissing && !_data.IsLocked)
            {
                var storeItem = new MenuItem { Header = Localization.Get("Menu_StoreItem") };
                storeItem.Click += (s, a) => StoreItemIntoWidget(dataItem);
                menu.Items.Add(storeItem);
            }

            // Stored item: move the file back while keeping the entry in the widget
            if (dataItem != null && !dataItem.IsReference && !_data.IsLocked)
            {
                var unstoreItem = new MenuItem { Header = Localization.Get("Menu_UnstoreItem") };
                unstoreItem.Click += (s, a) => UnstoreItem(dataItem);
                menu.Items.Add(unstoreItem);
            }

            // Eject (remove from widget; stored files go back to their original location)
            var remItem = new MenuItem
            {
                Header = Localization.Get("Menu_RemoveItem"),
                IsEnabled = !_data.IsLocked // locked widgets forbid moving content out
            };
            remItem.Click += (s, a) =>
            {
                var itemToRemove = _data.Items.FirstOrDefault(i => i.Path == item.Path);
                if (itemToRemove != null)
                {
                    EjectItem(itemToRemove);
                    _data.Items.Remove(itemToRemove);
                    UpdateUI();
                    OnDataChanged?.Invoke();
                }
            };

            var copyPathItem = new MenuItem { Header = Localization.Get("Menu_CopyPath") };
            copyPathItem.Click += (s, a) => CopyPathToClipboard(item.Path);

            var renameItem = new MenuItem { Header = Localization.Get("Menu_RenameItem") };
            renameItem.Click += (s, a) => RenameItem(item);

            menu.Items.Add(openItem);
            menu.Items.Add(locItem);
            menu.Items.Add(terminalItem);
            menu.Items.Add(renameItem);
            menu.Items.Add(copyPathItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(remItem);
            MenuBuilder.Prepare(menu, _data.Color);
            ShowMenu(menu, anchor);
        }

        /// <summary>Opens at the cursor, or anchored below the element for keyboard use.</summary>
        private static void ShowMenu(ContextMenu menu, FrameworkElement anchor)
        {
            if (anchor != null)
            {
                menu.PlacementTarget = anchor;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.HorizontalOffset = 0;
                menu.VerticalOffset = 0;
            }
            menu.IsOpen = true;
        }

        // ---------- keyboard ----------

        /// <summary>F5 / Enter / Shift+F10 / Apps. Returns true when the key was consumed.</summary>
        private bool HandleMenuActionKey(KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                RefreshPanel();
                return true;
            }

            if (e.Key == Key.Enter)
            {
                var selected = GetSelectedItems();
                if (selected.Count == 1) OpenItem(selected[0]);
                return true;
            }

            bool menuKey = e.Key == Key.Apps ||
                (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
            if (!menuKey) return false;

            var items = GetSelectedItems();
            if (items.Count >= 1)
            {
                var item = items[0];
                if (IsBrowsing) ShowBrowseItemMenu(item, GetItemContainer(item));
                else ShowRootItemMenu(item, GetItemContainer(item));
            }
            else if (IsBrowsing)
            {
                ShowBrowseBackgroundMenu(ItemsScroller);
            }
            else
            {
                ShowPanelContextMenu();
            }
            return true;
        }

        private FrameworkElement GetItemContainer(DisplayItem item)
        {
            return ItemsContainer.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        }
```

- [ ] **Step 4: 接入浏览态与根态**

`Controls/FolderWidget.Browse.cs`：删除整个 `#region Read-Only Item Menu`（旧 `ShowBrowseItemMenu`）；新的同名方法在 `MenuActions`。

`Controls/FolderWidget.Interactions.cs`：

1. `Item_RightClick` 整体替换为：

```csharp
        private void Item_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.DataContext is DisplayItem item)
            {
                if (IsBrowsing) ShowBrowseItemMenu(item);
                else ShowRootItemMenu(item);
                e.Handled = true;
            }
        }
```

2. 原 `Item_RightClick` 里的根态菜单构造整段删除（已搬进 `MenuActions.ShowRootItemMenu`，顺序保持：收纳/移出 → 打开 → 打开文件位置 → **在终端中打开** → 重命名 → 复制路径 → 分隔线 → 移除）。

3. `ExpandedPanel_MouseRightButtonDown` 的浏览态闸门替换为：

```csharp
            if (IsBrowsing)
            {
                ShowBrowseBackgroundMenu();
                e.Handled = true;
                return;
            }
```

4. `Window_KeyDown` 开头加入：

```csharp
            if (HandleMenuActionKey(e))
            {
                e.Handled = true;
                return;
            }
```

- [ ] **Step 5: 构建 + 全部自检**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 errors
Run: `dotnet run --project tests/ShellOpsCheck`、`tests/LangCheck`、`tests/XamlLoadCheck`、`tests/UiTokensCheck`、`tests/FolderListingCheck` → Expected: 全绿

- [ ] **Step 6: 手动验证**

Run: `bin\Debug\net48\Kobold.exe`
1. 浏览态空白右键 → 新建文件夹/文本文档 → 出现在当前目录；重名/非法字符在对话框内被拒；取消无副作用。
2. 条目右键 → 重命名、删除（回收站可还原）、多选删除（数量文案）、复制路径（多选多行）、终端（文件夹/文件所在目录）、在资源管理器中打开。
3. 多选时打开/终端/资源管理器/重命名灰显；右键未选中条目会先单选它。
4. F5 / Enter / Shift+F10 / 菜单键行为正确；Esc 仍收面板；Backspace 仍返回。
5. 根态条目菜单出现"在终端中打开"（文件夹可用、文件灰显）；其余旧项不变。
6. 未固定面板：新建/重命名/删除对话框中面板不收起。

- [ ] **Step 7: 提交（需用户许可）**

```bash
git add Services/TerminalLauncher.cs Controls/FolderWidget.MenuActions.cs Controls/FolderWidget.Browse.cs Controls/FolderWidget.Interactions.cs
git commit -m "feat(shell-menu): wire in-panel menus, keyboard shortcuts and file actions"
```

---

### Task 5: 原生菜单（ShellContextMenuService，先验证后接线）

**Files:**
- Create: `Services/ShellContextMenuService.cs`
- Modify: `Controls/FolderWidget.MenuActions.cs`（hook + 两个原生入口 + 两条 `Menu_ShowMoreOptions`）
- Modify: `Controls/FolderWidget.xaml.cs`（`SourceInitialized` 挂 hook）

**Interfaces:**
- Produces:
  - `ShellContextMenuService.ShowForItem(IntPtr, string, int, int, bool extendedVerbs = false) -> bool`
  - `ShellContextMenuService.ShowForFolderBackground(IntPtr, string, int, int, bool extendedVerbs = false) -> bool`
  - `ShellContextMenuService.HandleMenuMessage(int, IntPtr, IntPtr, out IntPtr) -> bool`
  - `FolderWidget.EnsureMenuHook()` / `MenuMessageHook(...)` / `ShowNativeItemMenu(DisplayItem)` / `ShowNativeBackgroundMenu(string)`

- [ ] **Step 1: 实现服务**

`Services/ShellContextMenuService.cs`（完整文件；GUID 取自 Windows SDK 头文件，Task 5 Step 2 的手动验证是它们的验收）：

```csharp
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kobold.Services
{
    /// <summary>
    /// Hosts the real shell context menu (item and folder background) in-process.
    /// Every failure is silent by design: the fallback is simply "no menu".
    /// </summary>
    public static class ShellContextMenuService
    {
        private const int WM_DRAWITEM = 0x002B;
        private const int WM_MEASUREITEM = 0x002C;
        private const int WM_INITMENUPOPUP = 0x0117;

        private const uint CMF_NORMAL = 0x00000000;
        private const uint CMF_EXTENDEDVERBS = 0x00000100;
        private const uint TPM_RETURNCMD = 0x0100;

        private const uint CMIC_MASK_UNICODE = 0x00004000;
        private const uint CMIC_MASK_PTINVOKE = 0x20000000;
        private const int SW_SHOWNORMAL = 1;

        private const uint ID_FIRST = 1;
        private const uint ID_LAST = 0x7FFF;

        private static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        private static readonly Guid IID_IShellFolder = new Guid("000214e6-0000-0000-c000-000000000046");
        private static readonly Guid IID_IContextMenu = new Guid("000214e4-0000-0000-c000-000000000046");
        private static readonly Guid BHID_SFObject = new Guid("3981e224-f559-11d3-8e3a-00c04f6837d5");
        private static readonly Guid BHID_SFUIObject = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5");

        private static IContextMenu3 _activeMenu3;
        private static IContextMenu2 _activeMenu2;
        private static bool _sessionActive;

        public static bool ShowForItem(IntPtr ownerHwnd, string path, int xPx, int yPx, bool extendedVerbs = false)
        {
            try
            {
                object item = CreateShellItem(path);
                if (item == null) return false;

                var menu = BindToHandler(item, BHID_SFUIObject, IID_IContextMenu) as IContextMenu;
                if (menu == null)
                {
                    Debug.WriteLine("[Kobold] Shell menu: no IContextMenu for " + path);
                    return false;
                }
                return Show(menu, ownerHwnd, xPx, yPx, extendedVerbs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Shell item menu failed: " + ex.Message);
                return false;
            }
        }

        public static bool ShowForFolderBackground(IntPtr ownerHwnd, string folder, int xPx, int yPx, bool extendedVerbs = false)
        {
            try
            {
                object item = CreateShellItem(folder);
                if (item == null) return false;

                var shellFolder = BindToHandler(item, BHID_SFObject, IID_IShellFolder) as IShellFolder;
                if (shellFolder == null)
                {
                    Debug.WriteLine("[Kobold] Shell menu: no IShellFolder for " + folder);
                    return false;
                }

                // CreateViewObject on the folder itself is the documented way to
                // get Explorer's background menu (Directory\Background verbs).
                IntPtr ptr;
                Guid iid = IID_IContextMenu;
                int hr = shellFolder.CreateViewObject(ownerHwnd, ref iid, out ptr);
                if (hr != 0 || ptr == IntPtr.Zero)
                {
                    Debug.WriteLine("[Kobold] Shell menu: CreateViewObject failed 0x" + hr.ToString("X8"));
                    return false;
                }

                object menuObject;
                try { menuObject = Marshal.GetObjectForIUnknown(ptr); }
                finally { Marshal.Release(ptr); }

                var menu = menuObject as IContextMenu;
                if (menu == null) return false;
                return Show(menu, ownerHwnd, xPx, yPx, extendedVerbs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Shell background menu failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Forwards menu messages to the active IContextMenu2/3 session.</summary>
        public static bool HandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (!_sessionActive) return false;
            if (msg != WM_DRAWITEM && msg != WM_MEASUREITEM && msg != WM_INITMENUPOPUP) return false;

            try
            {
                if (_activeMenu3 != null)
                {
                    IntPtr lr;
                    if (_activeMenu3.HandleMenuMsg2(msg, wParam, lParam, out lr) == 0)
                    {
                        result = lr;
                        return true;
                    }
                }
                else if (_activeMenu2 != null && _activeMenu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Shell menu message failed: " + ex.Message);
            }
            return false;
        }

        private static bool Show(IContextMenu menu, IntPtr ownerHwnd, int xPx, int yPx, bool extendedVerbs)
        {
            IntPtr hMenu = IntPtr.Zero;
            try
            {
                hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return false;

                uint flags = CMF_NORMAL | (extendedVerbs ? CMF_EXTENDEDVERBS : 0);
                int hr = menu.QueryContextMenu(hMenu, 0, ID_FIRST, ID_LAST, flags);
                if (hr < 0)
                {
                    Debug.WriteLine("[Kobold] Shell menu: QueryContextMenu failed 0x" + hr.ToString("X8"));
                    return false;
                }
                if (GetMenuItemCount(hMenu) <= 0) return false;

                _activeMenu3 = menu as IContextMenu3;
                _activeMenu2 = _activeMenu3 == null ? menu as IContextMenu2 : null;
                _sessionActive = true;
                try
                {
                    SetForegroundWindow(ownerHwnd);
                    uint command = TrackPopupMenuEx(hMenu, TPM_RETURNCMD, xPx, yPx, ownerHwnd, IntPtr.Zero);
                    if (command == 0) return false;

                    Invoke(menu, command - ID_FIRST, xPx, yPx, ownerHwnd);
                    return true;
                }
                finally
                {
                    _sessionActive = false;
                    _activeMenu3 = null;
                    _activeMenu2 = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Shell menu failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            }
        }

        private static void Invoke(IContextMenu menu, uint commandOffset, int xPx, int yPx, IntPtr ownerHwnd)
        {
            int size = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = size,
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                    hwnd = ownerHwnd,
                    lpVerb = new IntPtr(commandOffset),
                    lpVerbW = new IntPtr(commandOffset),
                    nShow = SW_SHOWNORMAL,
                    ptInvoke = new POINT { x = xPx, y = yPx }
                };
                Marshal.StructureToPtr(info, buffer, false);
                menu.InvokeCommand(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static object CreateShellItem(string path)
        {
            object item;
            Guid iid = IID_IShellItem;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (hr != 0 || item == null)
            {
                Debug.WriteLine("[Kobold] Shell menu: cannot create item for " + path + " (0x" + hr.ToString("X8") + ")");
                return null;
            }
            return item;
        }

        private static object BindToHandler(object shellItem, Guid handler, Guid iid)
        {
            IntPtr ptr;
            int hr = ((IShellItem)shellItem).BindToHandler(IntPtr.Zero, ref handler, ref iid, out ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            try { return Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr lpTitle;
            public IntPtr lpVerbW;
            public IntPtr lpParametersW;
            public IntPtr lpDirectoryW;
            public IntPtr lpTitleW;
            public POINT ptInvoke;
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("000214e6-0000-0000-c000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName,
                out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
            [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid,
                IntPtr rgfReserved, out IntPtr ppv);
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
            [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        }

        [ComImport, Guid("000214f4-0000-0000-c000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
            [PreserveSig] int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
            [PreserveSig] int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(int uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern int GetMenuItemCount(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
```

- [ ] **Step 2: 接条目入口并手动验证（验证不通过就停）**

`Controls/FolderWidget.MenuActions.cs`：

1. using 增加 `using System.Windows.Interop;`。
2. 追加：

```csharp
        // ---------- native shell menu ----------

        private HwndSource _menuHookSource;

        private void EnsureMenuHook()
        {
            if (_menuHookSource != null) return;
            try
            {
                IntPtr handle = PanelHwnd;
                if (handle == IntPtr.Zero) return;

                var source = HwndSource.FromHwnd(handle);
                if (source == null) return;

                _menuHookSource = source;
                source.AddHook(MenuMessageHook);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Native-menu hook failed: " + ex.Message);
            }
        }

        private IntPtr MenuMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            IntPtr result;
            if (ShellContextMenuService.HandleMenuMessage(msg, wParam, lParam, out result))
            {
                handled = true;
                return result;
            }
            return IntPtr.Zero;
        }

        private static bool IsShiftDown()
        {
            return (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        }

        private void ShowNativeItemMenu(DisplayItem item)
        {
            var anchor = GetItemAnchorPhysical(item);
            if (ShellContextMenuService.ShowForItem(PanelHwnd, item.Path, anchor.X, anchor.Y, IsShiftDown()))
            {
                RefreshPanel();
            }
        }

        private System.Windows.Point GetItemAnchorPhysical(DisplayItem item)
        {
            var container = GetItemContainer(item);
            return container != null
                ? container.PointToScreen(new System.Windows.Point(0, container.ActualHeight))
                : ItemsScroller.PointToScreen(new System.Windows.Point(8, 8));
        }
```

3. `ShowBrowseItemMenu` 的 `new MenuBuilder(...)` 链末尾（`.AddItem("Menu_DeleteItem", ...)` 之后）加：

```csharp
                .AddSeparator()
                .AddItem("Menu_ShowMoreOptions", () => ShowNativeItemMenu(item), single)
```

（原先 `.AddItem("Menu_DeleteItem", ...)` 后直接 `.Build();`，现在改为上述两行 + `.Build();`。）

`Controls/FolderWidget.xaml.cs`：构造函数里 `ThemeManager.ThemeChanged += OnThemeChanged;` 之后加：

```csharp
            SourceInitialized += (s, e) => EnsureMenuHook();
```

构建并手动验证**条目菜单**：

Run: `dotnet build Kobold.csproj -c Debug`；`bin\Debug\net48\Kobold.exe`
1. 浏览态右键**文件** → 显示更多选项… → 原生菜单出现：打开/打开方式/发送到/复制/属性 + **7-Zip 子菜单（带图标）**。
2. 浏览态右键**文件夹** → 显示更多选项… → 出现 **Git Bash Here / Git GUI Here**、7-Zip、属性。
3. 点属性 → 系统属性页出现；7-Zip 子菜单能展开、图标正常（验证 `WM_DRAWITEM`/`WM_MEASUREITEM` 转发）。
4. 未固定面板打开原生菜单时面板不收起（`SetForegroundWindow` 落回面板窗口）。
5. 任一项不成立：停止接线，用 `systematic-debugging` 排查（GUID/HRESULT/hook），修好再继续。

- [ ] **Step 3: 接背景入口**

`Controls/FolderWidget.MenuActions.cs`：追加：

```csharp
        private void ShowNativeBackgroundMenu(string directory)
        {
            var anchor = ItemsScroller.PointToScreen(new System.Windows.Point(8, 8));
            if (ShellContextMenuService.ShowForFolderBackground(
                    PanelHwnd, directory, (int)anchor.X, (int)anchor.Y, IsShiftDown()))
            {
                RefreshPanel();
            }
        }
```

`ShowBrowseBackgroundMenu` 的 `new MenuBuilder(...)` 链末尾（`.AddItem("Menu_Refresh", RefreshPanel)` 之后）加：

```csharp
                .AddSeparator()
                .AddItem("Menu_ShowMoreOptions", () => ShowNativeBackgroundMenu(directory))
```

- [ ] **Step 4: 手动验证背景菜单**

Run: `bin\Debug\net48\Kobold.exe`
1. 浏览态空白右键 → 显示更多选项… → 菜单出现：**Git Bash Here**、新建/粘贴/属性等。
2. 点 Git Bash Here → Git Bash 打开的**当前目录**正确（`%v` 生效）。
3. 菜单中**不出现 7-Zip**（本机注册在条目 handler，与资源管理器一致）。
4. 点 新建 → 文件夹 → 菜单关闭后列表刷新出现新文件夹。
5. 不存在/无权限路径：不弹菜单、不崩溃。

- [ ] **Step 5: 构建 + 全部自检**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 errors
Run: 全部 `tests/*Check`（含 ShellOpsCheck）→ Expected: 全绿

- [ ] **Step 6: 提交（需用户许可）**

```bash
git add Services/ShellContextMenuService.cs Controls/FolderWidget.MenuActions.cs Controls/FolderWidget.xaml.cs
git commit -m "feat(shell-menu): host the native shell context menu for items and backgrounds"
```

---

### Task 6: README + 全量验收

**Files:**
- Modify: `README.md`
- Test: 全部 `tests/*Check` + 手动验收清单（spec §7.3）

- [ ] **Step 1: README**

英文功能列表在 "Browse folders in place" 一条后追加：

```markdown
- **File actions in panels** — while browsing a folder, create, rename and
  delete (to the Recycle Bin) in place, open a terminal at the current
  folder, or fall back to the full Windows context menu (Git Bash, 7-Zip,
  properties…) via **Show more options**.
```

中文功能列表在"面板内浏览文件夹"一条后追加：

```markdown
- **面板内文件操作** — 浏览目录时可就地新建、重命名、删除（进回收站），
  一键在当前目录打开终端；需要时用「显示更多选项」调出完整的系统右键菜单
  （Git Bash、7-Zip、属性等）。
```

英文用法表在 "Browse a folder inside a panel" 行后追加：

```markdown
| File actions in a panel | Right-click an item or the empty area (F5 refreshes, Enter opens, Shift+F10 opens the menu) |
| The full Windows context menu | Choose **Show more options** at the bottom of the panel menu |
```

中文用法表在"在面板内浏览文件夹"行后追加：

```markdown
| 面板内文件操作 | 右键条目或空白处（F5 刷新、Enter 打开、Shift+F10 弹菜单） |
| 完整系统右键菜单 | 面板菜单底部的「显示更多选项」 |
```

两处自检清单（中英）在 `FolderListingCheck` 之后都追加：

```markdown
dotnet run --project tests/ShellOpsCheck
```

- [ ] **Step 2: 全量自检**

Run（逐个，全部退出码 0）：
`tests/LangCheck`、`tests/WidgetItemsCheck`、`tests/ScreenGeometryCheck`、`tests/IslandLayoutCheck`、
`tests/StorageOpsCheck`、`tests/DesktopIconsCheck`、`tests/UiTokensCheck`、`tests/XamlLoadCheck`、
`tests/FolderListingCheck`、`tests/ShellOpsCheck`

- [ ] **Step 3: 手动验收（spec §7.3 的 11 条）**

Run: `bin\Debug\net48\Kobold.exe`，逐条走完并记录结果（新建/重命名/删除可还原/终端/原生菜单条目+空白/键盘/多选/未固定面板/缩放主题/回归/失败路径）。

- [ ] **Step 4: 提交（需用户许可）**

```bash
git add README.md
git commit -m "docs(shell-menu): document in-panel file actions and the shell menu"
```

---

## 自检（写完计划后核对）

**1. Spec 覆盖**

| Spec 要求 | 对应 Task |
|---|---|
| 终端解析顺序 + 三种转义（§6.1） | Task 1（纯函数）+ Task 4 Step 1（启动降级） |
| 名称校验（§6.2/§6.3） | Task 1 |
| 删除预检 + 参数构造（§6.4） | Task 1（预检/参数）+ Task 2（P/Invoke 与逐项调用） |
| 新建/重命名/删除动作（§6.2-6.4） | Task 2 + Task 4 |
| 复制路径/打开/定位（§6.5） | Task 4 |
| 浏览态条目菜单 + 多选矩阵（§3.1） | Task 4 |
| 浏览态空白菜单（§3.2） | Task 4 |
| 根态终端项（§3.3） | Task 4 Step 4 |
| 键盘通道 F5/Enter/Shift+F10/Apps（§3.4） | Task 4 Step 3/4 |
| 模态护栏（§6.6） | Task 3 |
| 原生菜单条目 + 背景 + 消息转发 + 失败回退（§5） | Task 5 |
| 调用后刷新（§6.7） | Task 5 Step 2/3 的 `RefreshPanel()` |
| 三语文案（§9） | Task 3 Step 5 |
| 可自检（§7.1） | Task 1 + Task 2 |
| README（§4 文件表） | Task 6 |

**2. 占位符扫描**：无 TBD/TODO；每个代码步骤都有可直接粘贴的完整片段或精确替换指令。

**3. 类型一致性**：`TerminalKind`/`TerminalCommand`/`Resolve`/`BuildArguments`/`TryLaunch`（Task 1 定义 → Task 4 消费）；`NameCheck`/`ValidateName`/`CanRecycle`/`ToDoubleNullTerminated`/`DeleteFlags`（Task 1 → Task 2/4）；`RunModal`/`ShowInputModal`/`ShowConfirmModal`/`ShowMessage`/`_modalDepth`（Task 3 → Task 4）；`ShowMenu`/`GetItemContainer`/`EnsureMenuHook`/`ShowNativeItemMenu`/`ShowNativeBackgroundMenu`/`GetItemAnchorPhysical`/`IsShiftDown`（Task 4 定义骨架 → Task 5 接线）；`ShowForItem`/`ShowForFolderBackground`/`HandleMenuMessage`（Task 5 定义与消费）。

**4. 已知执行风险**：原生菜单的 COM GUID 与消息转发是最脆的部分（Task 5 Step 2 先验证，失败即停）；`PointToScreen` 返回物理像素，`TrackPopupMenuEx` 需要物理坐标（200% 缩放下同一套代码）；`IContextMenu3` 不存在时依赖 `IContextMenu2` 的 `WM_DRAWITEM` 回退。

