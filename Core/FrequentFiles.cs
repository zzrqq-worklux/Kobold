using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Kobold.Core.FileSearch;

namespace Kobold.Core
{
    /// <summary>
    /// One remembered file open: the file, how often it was opened from a
    /// widget and when it was last opened (UTC).
    /// </summary>
    public sealed class FrequentFile
    {
        public string Path { get; set; }
        public int Count { get; set; }
        public DateTime LastOpenedUtc { get; set; }
    }

    /// <summary>
    /// Usage counters for the files opened from the widgets ("frequent files").
    /// Only a real file counts, only the most-used entries are kept, and the
    /// write goes through the manager's debounced save - this class never
    /// holds a config or a timer of its own. Kept free of UI and storage
    /// dependencies: the list lives in <see cref="AppConfig"/>, which
    /// Newtonsoft already serializes.
    ///
    /// Eviction leaves one probation slot: the entry recorded by a call is
    /// never the victim, so a brand-new file (count 1) survives its first open
    /// on a full list and the second-lowest entry is evicted instead. Without
    /// that slot a one-off file would be trimmed on the spot and could never
    /// accumulate opens.
    ///
    /// Both public entry points read <c>WidgetManager.Instance</c>, whose getter
    /// lazily constructs the manager and loads the config: call them on the UI
    /// thread after startup (the open/preview funnels), never from a worker
    /// before the manager exists.
    /// </summary>
    public static class FrequentFiles
    {
        /// <summary>The config keeps at most this many entries; the read view shows the top 10.</summary>
        public const int Keep = 20;

        /// <summary>
        /// Counts one open of an existing file in the singleton's config. A
        /// missing config, a null/empty path, a directory or a file that is
        /// gone by now is ignored, and the write is only scheduled (debounced).
        /// Bookkeeping never fails an open: every failure in here is swallowed.
        /// </summary>
        public static void Record(string path)
        {
            // The call sites sit inside the open/key funnels; a bookkeeping
            // failure must never surface as a failed open or escape a key
            // handler. This also covers the lazy manager/config load.
            try
            {
                RecordCore(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] frequent-file record failed: " + ex.Message);
            }
        }

        private static void RecordCore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // File.Exists is false for a directory, a missing file and an
            // unreadable path alike.
            if (!File.Exists(path)) return;

            WidgetManager manager = WidgetManager.Instance;
            AppConfig config = manager?.Config;
            if (config == null) return;

            // The same gate Save holds while it serializes the config: a
            // concurrent Add/Sort/RemoveAt would otherwise throw
            // "Collection was modified" inside the debounced write and drop
            // that save. No callback re-enters the lock from here.
            config.WithSaveLock(() =>
            {
                if (config.FrequentFiles == null) config.FrequentFiles = new List<FrequentFile>();
                PruneMissing(config.FrequentFiles);
                Record(config.FrequentFiles, path, DateTime.UtcNow, Keep);
            });

            // The debounced path, never SaveConfigNow: an open must not run a
            // backup copy + full serialize + File.Replace on the UI thread.
            manager.SaveConfig();
        }

        /// <summary>
        /// The most-used files, highest count first, copied out of the config so
        /// no caller can mutate the live list. Deleted files are filtered out of
        /// the view (the stored list is pruned on the next record). A legacy
        /// config (no block) and a non-positive count yield an empty view.
        /// </summary>
        public static IReadOnlyList<FrequentFile> Top(int count)
        {
            if (count <= 0) return new List<FrequentFile>().AsReadOnly();

            AppConfig config = WidgetManager.Instance?.Config;
            if (config == null) return new List<FrequentFile>().AsReadOnly();

            List<FrequentFile> snapshot = null;
            config.WithSaveLock(() =>
            {
                List<FrequentFile> stored = config.FrequentFiles;
                if (stored == null) return;

                snapshot = new List<FrequentFile>(stored.Count);
                foreach (FrequentFile file in stored)
                {
                    if (file == null || string.IsNullOrEmpty(file.Path)) continue;
                    if (!Exists(file.Path)) continue;
                    snapshot.Add(new FrequentFile
                    {
                        Path = file.Path,
                        Count = file.Count,
                        LastOpenedUtc = file.LastOpenedUtc
                    });
                }
            });

            if (snapshot == null) return new List<FrequentFile>().AsReadOnly();
            snapshot.Sort(Compare);
            if (snapshot.Count > count) snapshot.RemoveRange(count, snapshot.Count - count);
            return snapshot.AsReadOnly();
        }

        /// <summary>
        /// The pure update: increments the entry the file already has (the
        /// comparison ignores case, so two spellings of one file stay together),
        /// refreshes its timestamp, orders the list and trims it to
        /// <paramref name="keep"/>. Stat-free on purpose - existence checks and
        /// pruning belong to the public paths, so the pins can drive this with
        /// fake paths.
        /// </summary>
        internal static void Record(List<FrequentFile> list, string path, DateTime nowUtc, int keep)
        {
            if (list == null) return;

            // A hand-edited config may carry empty entries; drop them before
            // the list is ordered and trimmed.
            list.RemoveAll(file => file == null || string.IsNullOrEmpty(file.Path));

            string normalized = SearchPath.Normalize(path);
            if (normalized.Length == 0) return;

            FrequentFile existing = null;
            foreach (FrequentFile file in list)
            {
                if (!string.Equals(file.Path, normalized, StringComparison.OrdinalIgnoreCase)) continue;
                existing = file;
                break;
            }

            if (existing != null)
            {
                existing.Count = existing.Count + 1;
                existing.LastOpenedUtc = nowUtc;
            }
            else
            {
                existing = new FrequentFile { Path = normalized, Count = 1, LastOpenedUtc = nowUtc };
                list.Add(existing);
            }

            list.Sort(Compare);

            int limit = keep < 0 ? 0 : keep;
            // The entry just recorded is never the one trimmed away - with a
            // full list of repeats its first open would otherwise be undone on
            // the spot and the file could never accumulate opens. Everything
            // else goes lowest-count first.
            while (list.Count > limit)
            {
                int victim = list.Count - 1;
                if (ReferenceEquals(list[victim], existing) && list.Count > 1) victim--;
                list.RemoveAt(victim);
            }
        }

        /// <summary>Drops entries whose file is gone or whose path is unusable.</summary>
        private static void PruneMissing(List<FrequentFile> list)
        {
            list.RemoveAll(file => file == null
                || string.IsNullOrEmpty(file.Path)
                || !Exists(file.Path));
        }

        private static bool Exists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                // An unreadable path is no usage evidence.
                return false;
            }
        }

        /// <summary>Count descending, then last-opened descending, then path ordinal.</summary>
        private static int Compare(FrequentFile left, FrequentFile right)
        {
            int byCount = right.Count.CompareTo(left.Count);
            if (byCount != 0) return byCount;

            int byTime = right.LastOpenedUtc.CompareTo(left.LastOpenedUtc);
            if (byTime != 0) return byTime;

            return string.CompareOrdinal(left.Path ?? string.Empty, right.Path ?? string.Empty);
        }
    }
}
