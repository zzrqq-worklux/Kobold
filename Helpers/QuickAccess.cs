using System;
using System.Diagnostics;
using System.IO;

namespace Kobold.Helpers
{
    /// <summary>
    /// Pins a folder to Explorer's Quick Access through the canonical Shell
    /// verbs ("pintohome"/"unpinfromhome"), so %AppData%\Kobold\Storage shows
    /// up in the file picker's sidebar without a shell extension or a mapped
    /// drive. Pin/Unpin are only invoked from the Settings window; automated
    /// checks may use the pure matcher and the read-only probe, never the
    /// actions (they would change the real Quick Access).
    /// </summary>
    public static class QuickAccess
    {
        private const string QuickAccessNamespace = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
        private const string PinVerb = "pintohome";
        private const string UnpinVerb = "unpinfromhome";

        /// <summary>
        /// Pins a folder to Quick Access. Returns false when the path does not
        /// exist, the Shell COM object is unavailable or the verb failed.
        /// </summary>
        public static bool Pin(string path)
        {
            return InvokeVerb(path, PinVerb);
        }

        /// <summary>Unpins a folder from Quick Access; same contract as Pin.</summary>
        public static bool Unpin(string path)
        {
            return InvokeVerb(path, UnpinVerb);
        }

        /// <summary>
        /// Best-effort reconciliation for the desired state true: pins the
        /// folder only when it is not already pinned. Never unpins - a manual
        /// pin must survive a desired state of false. Silent except Debug.
        /// </summary>
        public static void EnsurePinned(string path)
        {
            try
            {
                if (TryIsPinned(path) == true) return;
                if (!Pin(path))
                {
                    Debug.WriteLine($"[Kobold] Quick Access auto-pin failed for {path}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Quick Access auto-pin failed for {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// Read-only: true when the folder is a Quick Access entry, false when
        /// it is not, null when the namespace could not be enumerated.
        /// </summary>
        public static bool? TryIsPinned(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                dynamic shell = CreateShell();
                if (shell == null) return null;

                dynamic quickAccess = shell.NameSpace(QuickAccessNamespace);
                if (quickAccess == null) return null;

                dynamic items = quickAccess.Items();
                if (items == null) return null;

                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic item = items.Item(i);
                        string entryPath = item == null ? null : item.Path as string;
                        if (MatchesPinnedEntry(entryPath, path)) return true;
                    }
                    catch (Exception)
                    {
                        // An entry without a filesystem path (a non-filesystem
                        // pin) is skipped, not fatal.
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Quick Access probe failed for {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pure path match: full path, no trailing separator, case-insensitive.
        /// Used by TryIsPinned and pinned by tests/XamlLoadCheck.
        /// </summary>
        public static bool MatchesPinnedEntry(string entryPath, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(entryPath) || string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            try
            {
                return string.Equals(Normalize(entryPath), Normalize(targetPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool InvokeVerb(string path, string verb)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

            try
            {
                dynamic shell = CreateShell();
                if (shell == null) return false;

                dynamic folder = shell.NameSpace(path);
                if (folder == null) return false;

                dynamic self = folder.Self;
                if (self == null) return false;

                self.InvokeVerb(verb);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Quick Access {verb} failed for {path}: {ex.Message}");
                return false;
            }
        }

        private static dynamic CreateShell()
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            return shellType == null ? null : Activator.CreateInstance(shellType);
        }
    }
}
