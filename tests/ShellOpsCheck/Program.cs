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
            MoveFlagsKeepCollisionsWithTheShell();
            DoubleNullTerminatedListing();
            CreateAndRenameInTemp();
            MoveIntoFolderInTemp();

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

        private static void MoveFlagsKeepCollisionsWithTheShell()
        {
            const ushort FOF_NOCONFIRMATION = 0x0010;
            const ushort FOF_NOERRORUI = 0x0400;

            Check((ShellFileOperations.MoveFlags & FOF_NOCONFIRMATION) == 0,
                "move flags: the shell still asks before replacing an existing file");
            Check((ShellFileOperations.MoveFlags & FOF_NOERRORUI) == 0,
                "move flags: a failed move stays visible instead of failing silently");
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

        private static void MoveIntoFolderInTemp()
        {
            string root = Path.Combine(Path.GetTempPath(), "kobold-shellops-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(target);
            try
            {
                string file = Path.Combine(root, "move-me.txt");
                File.WriteAllText(file, "x");

                Check(ShellFileOperations.Move(new[] { file }, target, IntPtr.Zero) &&
                      File.Exists(Path.Combine(target, "move-me.txt")) &&
                      !File.Exists(file),
                    "move: a real file lands in the target folder and leaves the source");

                Check(ShellFileOperations.Move(new string[0], target, IntPtr.Zero),
                    "move: an empty list is a no-op that reports success");
                Check(!ShellFileOperations.Move(new[] { file }, "   ", IntPtr.Zero),
                    "move: a blank target folder is refused");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

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
    }
}
