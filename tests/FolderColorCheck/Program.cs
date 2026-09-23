using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.FolderColorCheck
{
    /// <summary>
    /// Checks FolderColor end to end on throwaway temp folders: coloring writes
    /// desktop.ini plus the System flag, restoring cleans up without touching a
    /// user's own desktop.ini settings, and system-folder icons are refused.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/FolderColorCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;
        private static string _iconPath;

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            _root = Path.Combine(Path.GetTempPath(), "kobold-foldercolor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _iconPath = FolderIconFactory.EnsureIcon("#EF4444", Path.Combine(_root, "icons"));
            if (_iconPath == null)
            {
                Console.WriteLine("TOP FAIL: the icon factory could not build a test icon");
                return 1;
            }

            try
            {
                ColoringWritesTheDesktopIni();
                RestoringCleansUp();
                UserSettingsSurviveRestore();
                SpecialFolderIconsAreRefused();
                RestoreIsIdempotent();
                BadInputFailsSafely();
            }
            finally
            {
                TryDelete(_root);
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL FOLDER-COLOR CHECKS PASSED");
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

        private static string NewFolder(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string IniPath(string folder)
        {
            return Path.Combine(folder, "desktop.ini");
        }

        private static void ColoringWritesTheDesktopIni()
        {
            string folder = NewFolder("case-color");

            var result = FolderColor.Apply(folder, _iconPath, 0);

            Check(result == FolderColorResult.Applied, "apply: reports Applied");

            bool exists = File.Exists(IniPath(folder));
            Check(exists, "apply: writes desktop.ini");
            if (!exists) return;

            var attributes = File.GetAttributes(IniPath(folder));
            Check((attributes & FileAttributes.Hidden) != 0 && (attributes & FileAttributes.System) != 0,
                "apply: the desktop.ini is hidden and system (Explorer ignores it otherwise)");
            var folderAttributes = File.GetAttributes(folder);
            Check((folderAttributes & FileAttributes.System) != 0,
                "apply: marks the folder as a system folder (Explorer needs it) - got " + folderAttributes);

            string resource = FolderColor.GetIconResource(folder);
            Check(resource != null && resource.StartsWith(_iconPath, StringComparison.OrdinalIgnoreCase),
                "apply: reads back our icon resource (" + resource + ")");
            Check(resource != null && resource.EndsWith(",0", StringComparison.Ordinal),
                "apply: the icon index travels with the resource");
        }

        private static void RestoringCleansUp()
        {
            string folder = NewFolder("case-restore");
            FolderColor.Apply(folder, _iconPath, 0);

            var result = FolderColor.Restore(folder);

            Check(result == FolderColorResult.Restored, "restore: reports Restored");
            Check(!File.Exists(IniPath(folder)), "restore: removes the desktop.ini it created");
            Check((File.GetAttributes(folder) & FileAttributes.System) == 0,
                "restore: clears the system flag");
            Check(FolderColor.GetIconResource(folder) == null, "restore: the icon is the default again");
        }

        private static void UserSettingsSurviveRestore()
        {
            string folder = NewFolder("case-keep-user-settings");
            File.WriteAllText(IniPath(folder),
                "[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\SHELL32.dll,3\r\n" +
                "InfoTip=keep me\r\n[ViewState]\r\nMode=\r\nVid=\r\nFolderType=Generic\r\n");
            File.SetAttributes(IniPath(folder), FileAttributes.Hidden | FileAttributes.System);

            FolderColor.Restore(folder);

            string text = File.ReadAllText(IniPath(folder));
            Check(File.Exists(IniPath(folder)), "user settings: the desktop.ini is kept");
            Check(text.IndexOf("InfoTip=keep me", StringComparison.Ordinal) >= 0 &&
                  text.IndexOf("[ViewState]", StringComparison.Ordinal) >= 0,
                "user settings: unrelated entries and sections survive");
            Check(text.IndexOf("IconResource", StringComparison.OrdinalIgnoreCase) < 0,
                "user settings: the icon entries are gone");
        }

        private static void SpecialFolderIconsAreRefused()
        {
            // A real special folder is a system folder, so mirror that shape here.
            string folder = NewFolder("case-special");
            File.WriteAllText(IniPath(folder),
                "[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\SHELL32.dll,3\r\n");
            File.SetAttributes(IniPath(folder), FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.System);

            var result = FolderColor.Apply(folder, _iconPath, 0);

            Check(result == FolderColorResult.RefusedSpecialFolder,
                "special folder: applying over a C:\\Windows icon is refused");

            string kept = FolderColor.GetIconResource(folder);
            Check(kept != null && kept.IndexOf("SHELL32", StringComparison.OrdinalIgnoreCase) >= 0,
                "special folder: the existing icon is untouched");
        }

        private static void RestoreIsIdempotent()
        {
            string folder = NewFolder("case-idempotent");

            Check(FolderColor.Restore(folder) == FolderColorResult.Restored,
                "idempotent: restoring a plain folder is a no-op that succeeds");
        }

        private static void BadInputFailsSafely()
        {
            Check(FolderColor.Apply(Path.Combine(_root, "missing") + "\\", _iconPath, 0) == FolderColorResult.Failed,
                "bad input: a missing folder fails");
            Check(FolderColor.Apply(null, _iconPath, 0) == FolderColorResult.Failed,
                "bad input: a null folder fails");
            Check(FolderColor.GetIconResource(Path.Combine(_root, "missing")) == null,
                "bad input: reading a missing folder returns null");
        }

        private static void TryDelete(string path)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(dir, FileAttributes.Directory); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
