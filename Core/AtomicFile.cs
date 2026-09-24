using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Kobold.Core
{
    /// <summary>
    /// All-or-nothing text writes: the target is only ever replaced after the
    /// new contents are fully written and flushed to disk, so a crash, a full
    /// disk or a lock mid-write leaves the previous file exactly as it was.
    /// </summary>
    public static class AtomicFile
    {
        private const int ReplaceAttempts = 5;
        private const int ReplaceRetryDelayMs = 100;

        public static void WriteAllText(string path, string contents)
        {
            Utils.EnsureDirectoryExists(Path.GetDirectoryName(path));

            string temp = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                    FileShare.None, 16 * 1024, FileOptions.SequentialScan))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true); // push to disk before the old file is replaced
                }

                ReplaceWithRetry(temp, path);
            }
            catch
            {
                TryDelete(temp); // never leave a half-written or stale temp behind
                throw;
            }
        }

        /// <summary>
        /// Swaps the temp file into place, retrying only the transient
        /// "another process is holding it" failures - the last attempt throws.
        /// </summary>
        private static void ReplaceWithRetry(string temp, string target)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Replace(temp, target, null);
                    else File.Move(temp, target);
                    return;
                }
                catch (IOException) when (attempt < ReplaceAttempts) { }
                catch (UnauthorizedAccessException) when (attempt < ReplaceAttempts) { }
                Thread.Sleep(ReplaceRetryDelayMs);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
