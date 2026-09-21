using System;
using System.Diagnostics;
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
    }
}
