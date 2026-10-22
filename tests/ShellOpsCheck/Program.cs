using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Kobold.Core;
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
            NewFileNameNormalizesExtensions();
            NewFileNameComposesNames();
            OfficeFileFormats();
            RecyclePrecheckAcceptsFixedDriveOnly();
            DeleteFlagsKeepTheRecycleBinSafe();
            MoveFlagsKeepCollisionsWithTheShell();
            DoubleNullTerminatedListing();
            CreateAndRenameInTemp();
            MoveIntoFolderInTemp();
            CopyFlagsKeepCollisionsAndUndo();
            CopyIntoFolderInTemp();

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

        // ---------- new file names ----------

        private static void NewFileNameNormalizesExtensions()
        {
            Check(NewFileName.NormalizeExtension(null) == "",
                "new-name: a null extension becomes empty");
            Check(NewFileName.NormalizeExtension("") == "",
                "new-name: an empty extension stays empty");
            Check(NewFileName.NormalizeExtension("   ") == "",
                "new-name: a blank extension becomes empty");
            Check(NewFileName.NormalizeExtension("...") == "",
                "new-name: a dots-only extension becomes empty");
            Check(NewFileName.NormalizeExtension("md") == ".md",
                "new-name: a bare body gains one leading dot");
            Check(NewFileName.NormalizeExtension(".MD") == ".md",
                "new-name: the extension is lower-cased");
            Check(NewFileName.NormalizeExtension(" .tx-t ") == ".tx-t",
                "new-name: trimming keeps internal dashes");
            Check(NewFileName.NormalizeExtension("..json") == ".json",
                "new-name: extra leading dots collapse to one");
            Check(NewFileName.NormalizeExtension("a b") == null,
                "new-name: a space is rejected");
            Check(NewFileName.NormalizeExtension("a/b") == null,
                "new-name: a slash is rejected");
            Check(NewFileName.NormalizeExtension("a*b") == null,
                "new-name: a star is rejected");
        }

        private static void NewFileNameComposesNames()
        {
            Check(NewFileName.Compose("报告", ".md") == "报告.md",
                "new-name: compose appends a missing extension");
            Check(NewFileName.Compose("报告.md", ".md") == "报告.md",
                "new-name: an identical extension is not doubled");
            Check(NewFileName.Compose("报告.MD", ".md") == "报告.MD",
                "new-name: the dedupe compares case-insensitively and keeps the tail");
            Check(NewFileName.Compose("报告.md", ".txt") == "报告.md.txt",
                "new-name: a different extension is appended");
            Check(NewFileName.Compose("报告", "") == "报告",
                "new-name: an empty extension appends nothing");
            Check(NewFileName.Compose("报告", null) == "报告",
                "new-name: a null extension appends nothing");
        }

        // ---------- office formats ----------

        private static void OfficeFileFormats()
        {
            var exts = NewFileName.CommonExtensions;
            Check(exts[exts.Length - 3] == ".docx" && exts[exts.Length - 2] == ".xlsx" && exts[exts.Length - 1] == ".pptx",
                "new file: the extension list ends with docx/xlsx/pptx");

            Check(HasPart(FileTemplates.BuildOfficePackage(".docx"), "[Content_Types].xml") &&
                  HasPart(FileTemplates.BuildOfficePackage(".docx"), "_rels/.rels") &&
                  HasPart(FileTemplates.BuildOfficePackage(".docx"), "word/document.xml"),
                "new file: docx carries its minimal parts");
            Check(HasPart(FileTemplates.BuildOfficePackage(".xlsx"), "xl/workbook.xml") &&
                  HasPart(FileTemplates.BuildOfficePackage(".xlsx"), "xl/worksheets/sheet1.xml"),
                "new file: xlsx carries its minimal parts");
            Check(HasPart(FileTemplates.BuildOfficePackage(".pptx"), "ppt/presentation.xml") &&
                  HasPart(FileTemplates.BuildOfficePackage(".pptx"), "ppt/slides/slide1.xml"),
                "new file: pptx carries its minimal parts");

            var docx = FileTemplates.BuildOfficePackage(".docx");
            var xlsx = FileTemplates.BuildOfficePackage(".xlsx");
            var pptx = FileTemplates.BuildOfficePackage(".pptx");
            Check(NewFileName.DefaultExtension == ".txt",
                "new file: the default extension stays txt");
            Check(IsXml(PartText(docx, "[Content_Types].xml")) &&
                  IsXml(PartText(docx, "_rels/.rels")) &&
                  IsXml(PartText(docx, "word/document.xml")),
                "new file: docx parts are parseable XML");
            Check(IsXml(PartText(xlsx, "[Content_Types].xml")) &&
                  IsXml(PartText(xlsx, "_rels/.rels")) &&
                  IsXml(PartText(xlsx, "xl/workbook.xml")) &&
                  IsXml(PartText(xlsx, "xl/_rels/workbook.xml.rels")) &&
                  IsXml(PartText(xlsx, "xl/worksheets/sheet1.xml")),
                "new file: xlsx parts are parseable XML");
            Check(IsXml(PartText(pptx, "ppt/presentation.xml")) &&
                  IsXml(PartText(pptx, "ppt/slides/slide1.xml")),
                "new file: pptx parts are parseable XML");
            Check(ContainsPart(docx, "[Content_Types].xml", "/word/document.xml"),
                "new file: docx content types name the document part");
            Check(ContainsPart(xlsx, "[Content_Types].xml", "/xl/workbook.xml") &&
                  ContainsPart(xlsx, "[Content_Types].xml", "/xl/worksheets/sheet1.xml"),
                "new file: xlsx content types name the workbook and sheet");

            string dir = Path.Combine(Path.GetTempPath(), "kobold-office-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string error;
                Check(FileTemplates.TryCreate(Path.Combine(dir, "a.docx"), out error) &&
                      new FileInfo(Path.Combine(dir, "a.docx")).Length > 0,
                    "new file: TryCreate writes a docx");
                Check(FileTemplates.TryCreate(Path.Combine(dir, "b.txt"), out error) &&
                      new FileInfo(Path.Combine(dir, "b.txt")).Length == 0,
                    "new file: a text file stays empty");
                Check(!FileTemplates.TryCreate(Path.Combine(dir, "a.docx"), out error),
                    "new file: TryCreate never overwrites");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static bool HasPart(byte[] package, string name)
        {
            return PartText(package, name) != null;
        }

        private static string PartText(byte[] package, string name)
        {
            if (package == null) return null;
            using (var stream = new MemoryStream(package))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry(name);
                if (entry == null) return null;
                using (var reader = new StreamReader(entry.Open()))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static bool IsXml(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                XDocument.Parse(text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ContainsPart(byte[] package, string part, string needle)
        {
            string text = PartText(package, part);
            return text != null && text.IndexOf(needle, StringComparison.Ordinal) >= 0;
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

        private static void CopyFlagsKeepCollisionsAndUndo()
        {
            const ushort FOF_ALLOWUNDO = 0x0040;
            const ushort FOF_RENAMEONCOLLISION = 0x0008;

            Check(ShellFileOperations.CopyFlags == (ushort)(FOF_ALLOWUNDO | FOF_RENAMEONCOLLISION),
                "copy flags: exactly FOF_ALLOWUNDO | FOF_RENAMEONCOLLISION (0x48) - undoable, collisions rename");
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

        private static void CopyIntoFolderInTemp()
        {
            string root = Path.Combine(Path.GetTempPath(), "kobold-shellops-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(target);
            try
            {
                string file = Path.Combine(root, "src.txt");
                File.WriteAllText(file, "new");

                Check(ShellFileOperations.Copy(new[] { file }, target, IntPtr.Zero) &&
                      File.Exists(Path.Combine(target, "src.txt")) &&
                      File.Exists(file),
                    "copy: a real file lands in the target folder and the source stays");

                Check(ShellFileOperations.Copy(new string[0], target, IntPtr.Zero),
                    "copy: an empty list is a no-op that reports success");
                Check(!ShellFileOperations.Copy(new[] { file }, "   ", IntPtr.Zero),
                    "copy: a blank target folder is refused");

                // Collision: the shell renames the newcomer, both copies stay.
                File.WriteAllText(Path.Combine(target, "src.txt"), "old");
                bool copied = ShellFileOperations.Copy(new[] { file }, target, IntPtr.Zero);
                bool keptOriginal = File.ReadAllText(Path.Combine(target, "src.txt")) == "old";
                bool renamedCopy = false;
                foreach (string extra in Directory.GetFiles(target))
                {
                    if (Path.GetFileName(extra) != "src.txt" && File.ReadAllText(extra) == "new")
                    {
                        renamedCopy = true;
                    }
                }
                Check(copied && keptOriginal && renamedCopy,
                    "copy: an existing name keeps both copies (the newcomer is renamed)");
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
