using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
