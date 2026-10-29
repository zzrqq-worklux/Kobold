using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Walks directories and streams records into the index store. The scanner never
    /// starts or finishes a scan: the caller wraps it in
    /// <see cref="ISearchIndexStore.BeginFullScan"/>/<see cref="ISearchIndexStore.CompleteFullScan"/>
    /// (or the reconcile pair), so a cancelled walk is thrown away without touching
    /// the published index.
    ///
    /// Traversal is iterative and one directory at a time: no directory is ever
    /// materialized as an array, and every directory and entry is guarded because a
    /// real drive contains junctions, denied folders and paths longer than MAX_PATH.
    /// </summary>
    public sealed class DirectoryScanner
    {
        /// <summary>Rows per store write; short transactions keep a concurrent query responsive.</summary>
        public const int BatchSize = 800;

        /// <summary>
        /// Test hook: replaces the directory listing for a path, so a check can fail an
        /// enumeration after the first rows (a directory that vanishes or is revoked
        /// mid-listing) - a state a real drive reaches, but not on demand. Production
        /// never sets it and then enumerates the real directory.
        /// </summary>
        internal Func<string, IEnumerable<FileSystemInfo>> EnumerateForChecks { get; set; }

        private const int MaxPathLength = 32000;
        private const int ProgressIntervalMs = 500;

        private readonly ISearchIndexStore _store;
        private readonly FileSearchSettings _settings;
        private readonly Action<ScanProgress> _progress;
        private readonly Func<string, bool> _isForbiddenDirectory;
        private readonly Func<string, bool> _isReparsePoint;
        private readonly Func<string, bool> _isUnreadableDirectory;
        private readonly IReconcileSession _reconcileSession;

        /// <param name="isForbiddenDirectory">
        /// Per-directory veto on top of the configured exclusions (the service passes
        /// a "pause/stop here" check).
        /// </param>
        /// <param name="isReparsePoint">
        /// Optional override for reparse detection, so callers and tests can treat a
        /// directory as a junction without creating one.
        /// </param>
        /// <param name="isUnreadableDirectory">
        /// Optional test hook that simulates a denied directory open without changing
        /// real ACLs. Production never sets it; a true result is counted and reported
        /// exactly like a real <see cref="UnauthorizedAccessException"/>.
        /// </param>
        /// <param name="reconcileSession">
        /// Reconcile diff sink. When set, a <see cref="ScanMode.Reconcile"/> walk reads
        /// each directory's indexed rows, writes only the rows that differ and prunes
        /// the ones that disappeared, instead of upserting everything and marking a
        /// seen batch. Left null (checks, other modes) the walk keeps the batched
        /// behaviour.
        /// </param>
        public DirectoryScanner(ISearchIndexStore store, FileSearchSettings settings,
            Action<ScanProgress> progress = null,
            Func<string, bool> isForbiddenDirectory = null,
            Func<string, bool> isReparsePoint = null,
            Func<string, bool> isUnreadableDirectory = null,
            IReconcileSession reconcileSession = null)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (settings == null) throw new ArgumentNullException("settings");

            _store = store;
            _settings = settings;
            _progress = progress;
            _isForbiddenDirectory = isForbiddenDirectory;
            _isReparsePoint = isReparsePoint;
            _isUnreadableDirectory = isUnreadableDirectory;
            _reconcileSession = reconcileSession;
        }

        /// <summary>
        /// Walks every root and writes the records it finds. A full scan stages rows
        /// for the caller's swap; a reconcile with a session writes only the rows whose
        /// directory diff shows a change and prunes the ones that disappeared; without
        /// a session a reconcile writes live rows and marks the same batch as seen.
        /// Throws <see cref="OperationCanceledException"/> when the token fires, after
        /// which the caller aborts its scan.
        /// </summary>
        public ScanResult Scan(IReadOnlyList<string> roots, ScanMode mode, CancellationToken token)
        {
            if (roots == null) throw new ArgumentNullException("roots");

            // Full rows are staged in the shadow table and published by the caller's
            // CompleteFullScan. Without BeginFullScan every batch would be written
            // straight into the live index and a cancel could no longer discard them.
            if (mode == ScanMode.Full && !_store.IsFullScanActive)
            {
                throw new InvalidOperationException(
                    "ScanMode.Full requires BeginFullScan on the store before scanning.");
            }

            var starts = new List<DirectoryNode>(roots.Count);
            for (int i = 0; i < roots.Count; i++)
            {
                string root = SearchPath.Normalize(roots[i]);
                if (root.Length == 0) continue;
                starts.Add(new DirectoryNode(root, null, root, false));
            }

            return Walk(starts, mode, false, token);
        }

        /// <summary>
        /// Re-reads one directory subtree and writes its rows straight to the live
        /// table (directory rename/repair in the watcher). No staging table is
        /// touched, so the published index stays searchable throughout, and no
        /// BeginFullScan is required. The start directory's own row is written too:
        /// a rename deletes the old prefix, so without it the renamed folder would
        /// stay unindexed until the next reconcile.
        /// </summary>
        public ScanResult ScanSubtree(string root, string directory, CancellationToken token)
        {
            string normalizedDirectory = SearchPath.Normalize(directory);
            if (normalizedDirectory.Length == 0) return new ScanResult();

            string normalizedRoot = SearchPath.Normalize(root);
            if (normalizedRoot.Length == 0) normalizedRoot = normalizedDirectory;

            // A junction/mount point as the renamed directory itself must not be
            // followed: its target can sit outside the scanned tree (or loop).
            if (IsReparsePoint(normalizedDirectory))
            {
                return new ScanResult { SkippedReparsePoints = 1 };
            }

            var starts = new List<DirectoryNode>(1)
            {
                new DirectoryNode(normalizedDirectory, null, normalizedRoot, true)
            };
            return Walk(starts, ScanMode.Full, true, token);
        }

        private ScanResult Walk(List<DirectoryNode> starts, ScanMode mode, bool live, CancellationToken token)
        {
            // A scan must not compete with the foreground UI for CPU.
            ThreadPriority previous = Thread.CurrentThread.Priority;
            TrySetPriority(ThreadPriority.BelowNormal);
            try
            {
                return WalkCore(starts, mode, live, token);
            }
            finally
            {
                TrySetPriority(previous);
            }
        }

        private ScanResult WalkCore(List<DirectoryNode> starts, ScanMode mode, bool live, CancellationToken token)
        {
            var state = new ScanState(BatchSize);
            var stack = new Stack<DirectoryNode>();
            // Push in reverse so the first configured root is visited first.
            for (int i = starts.Count - 1; i >= 0; i--) stack.Push(starts[i]);

            token.ThrowIfCancellationRequested();

            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                DirectoryNode node = stack.Pop();
                state.CurrentDirectory = node.Path;

                DirectoryVerdict verdict = CheckDirectory(node, state);
                if (verdict != DirectoryVerdict.Walk)
                {
                    // A settings exclusion is a decision, not a failure: the rows a
                    // previous pass indexed under it are stale and go with it, exactly
                    // like the legacy root-wide anti-join. A veto (pause/stop here) is
                    // transient and keeps its rows.
                    if (verdict == DirectoryVerdict.Excluded) PruneExcluded(node.Path, mode, live);

                    // The veto callback may have cancelled; report it now instead of
                    // waiting for the next directory.
                    token.ThrowIfCancellationRequested();
                    continue;
                }

                state.Directories++;
                if (live && node.IsStart) AddDirectoryRow(node, state);
                Enumerate(node, stack, state, mode, live, token);
                MaybeReportProgress(state);

                token.ThrowIfCancellationRequested();
            }

            Flush(state, mode, live, token);
            token.ThrowIfCancellationRequested();

            if (_progress != null) _progress(state.ToProgress());
            return state.ToResult();
        }

        /// <summary>
        /// Invokes the progress callback at most once per interval, so a slow drive
        /// still reports while one huge directory is being enumerated.
        /// </summary>
        private void MaybeReportProgress(ScanState state)
        {
            if (_progress == null) return;

            long now = state.Clock.ElapsedMilliseconds;
            if (now - state.LastProgressMs < ProgressIntervalMs) return;

            state.LastProgressMs = now;
            _progress(state.ToProgress());
        }

        /// <summary>
        /// Decides whether this directory may be walked. Skipped directories are
        /// counted but never fatal: one denied folder must not end a drive scan.
        /// A nested excluded directory is already filtered out by its parent (so its
        /// row is never written); the settings check here covers a start node, which
        /// has no parent in the walk.
        /// </summary>
        private DirectoryVerdict CheckDirectory(DirectoryNode node, ScanState state)
        {
            if (node.Path.Length > MaxPathLength)
            {
                state.SkippedLongPaths++;
                return DirectoryVerdict.TooLong;
            }

            if (_isForbiddenDirectory != null && _isForbiddenDirectory(node.Path))
            {
                state.SkippedExcluded++;
                return DirectoryVerdict.Vetoed;
            }

            if (SearchDirectoryFilter.IsExcluded(node.Path, node.Name, _settings))
            {
                state.SkippedExcluded++;
                return DirectoryVerdict.Excluded;
            }

            return DirectoryVerdict.Walk;
        }

        /// <summary>
        /// Drops what the index still holds under a directory the settings exclude.
        /// It is out of scope now, so its old rows would otherwise stay searchable
        /// until a full rebuild. A diff pass only: a full scan publishes a fresh table
        /// and a live subtree write has no session to prune through.
        /// </summary>
        private void PruneExcluded(string path, ScanMode mode, bool live)
        {
            if (_reconcileSession == null || mode != ScanMode.Reconcile || live) return;

            _reconcileSession.DeletePathAndPrefix(path);
        }

        private void Enumerate(DirectoryNode node, Stack<DirectoryNode> stack, ScanState state,
            ScanMode mode, bool live, CancellationToken token)
        {
            if (_isUnreadableDirectory != null && _isUnreadableDirectory(node.Path))
            {
                MarkUnreadable(node.Path, state);
                state.SkippedDirectories++;
                return;
            }

            // Reconcile: the rows this directory already has. The walk writes only the
            // entries that differ and prunes the leftovers once the listing succeeded.
            Dictionary<string, SearchEntry> existing = ReadExistingChildren(node.Path, mode, live, token);

            IEnumerator<FileSystemInfo> items;
            try
            {
                IEnumerable<FileSystemInfo> source = null;
                if (EnumerateForChecks != null) source = EnumerateForChecks(node.Path);
                if (source == null) source = new DirectoryInfo(SearchPath.ToExtended(node.Path)).EnumerateFileSystemInfos();
                items = source.GetEnumerator();
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Gone, denied or unreadable since it was pushed: skip, keep walking.
                // Its subtree stays untouched by a reconcile (see MarkUnreadable);
                // only a real access/IO failure qualifies, not a malformed path.
                if (IsUnreadableError(ex)) MarkUnreadable(node.Path, state);
                state.SkippedDirectories++;
                return;
            }

            bool listedCompletely = true;
            try
            {
                bool listedAny = false;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    MaybeReportProgress(state);

                    FileSystemInfo info;
                    try
                    {
                        if (!items.MoveNext()) break;
                        info = items.Current;
                        listedAny = true;
                    }
                    catch (Exception ex) when (IsFileSystemError(ex))
                    {
                        // Nothing could be listed: the directory itself is gone or
                        // unreadable. Once part of it was listed, only the unread
                        // remainder counts as skipped entries. Either way its
                        // unread rows must survive a reconcile.
                        if (IsUnreadableError(ex)) MarkUnreadable(node.Path, state);
                        if (listedAny) state.SkippedEntries++; else state.SkippedDirectories++;
                        listedCompletely = false;
                        break;
                    }

                    if (info == null)
                    {
                        state.SkippedEntries++;
                        continue;
                    }

                    string fullPath;
                    FileAttributes attributes;
                    try
                    {
                        fullPath = info.FullName;
                        attributes = info.Attributes;
                    }
                    catch (Exception ex) when (IsFileSystemError(ex))
                    {
                        state.SkippedEntries++;
                        continue;
                    }

                    // (FileAttributes)(-1): the item disappeared between enumeration
                    // and this stat.
                    if ((int)attributes == -1)
                    {
                        state.SkippedEntries++;
                        continue;
                    }

                    string path = SearchPath.FromExtended(fullPath);
                    if (path.Length > MaxPathLength)
                    {
                        state.SkippedLongPaths++;
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0
                        || (_isReparsePoint != null && _isReparsePoint(path)))
                    {
                        // Following a junction can loop or leave the scanned tree.
                        state.SkippedReparsePoints++;
                        continue;
                    }

                    bool isDirectory = (attributes & FileAttributes.Directory) != 0 || info is DirectoryInfo;
                    if (!isDirectory && !SearchTypeFilter.IsFileIncluded(path, _settings)) continue;

                    SearchEntry entry;
                    if (!SearchEntry.TryCreate(node.Root, info, out entry) || entry == null)
                    {
                        state.SkippedEntries++;
                        continue;
                    }

                    if (isDirectory)
                    {
                        // An excluded directory is out of scope, so its own row is not
                        // written either (the parent used to add it and nothing could
                        // ever reach it). Whatever an older pass indexed under it is
                        // stale and goes now, like the legacy root-wide anti-join did.
                        // A veto is different: it only says "not now" and keeps rows.
                        if (SearchDirectoryFilter.IsExcluded(path, info.Name, _settings))
                        {
                            state.SkippedExcluded++;
                            if (existing != null) existing.Remove(path);
                            PruneExcluded(path, mode, live);
                            continue;
                        }

                        if (_settings.IndexFolders)
                        {
                            Add(entry, state, mode, live, token, existing);
                        }
                        else if (existing != null)
                        {
                            // Folder rows are off, so this directory writes no row of its
                            // own. A row left in the map by an older pass would make the
                            // parent's prune prefix-delete this whole subtree, and the
                            // walk of the child would then rewrite every file in it.
                            // Drop only the child's own row and let its own walk diff.
                            if (existing.Remove(entry.Path)) _reconcileSession.Delete(entry.Path);
                        }
                        stack.Push(new DirectoryNode(path, info.Name, node.Root, false));
                        continue;
                    }

                    state.Files++;
                    Add(entry, state, mode, live, token, existing);
                }
            }
            finally
            {
                items.Dispose();
            }

            // A partial listing (the directory vanished or went unreadable mid-way) has
            // not seen its whole contents, so nothing may be pruned for it.
            if (existing != null && listedCompletely) PruneMissingChildren(existing);
        }

        /// <summary>
        /// Writes the row for a live subtree's start directory. It is not yielded by
        /// its parent's enumeration (that parent is outside the walk), so a rename
        /// repair has to add it explicitly.
        /// </summary>
        private void AddDirectoryRow(DirectoryNode node, ScanState state)
        {
            if (!_settings.IndexFolders) return;

            SearchEntry entry;
            try
            {
                if (!SearchEntry.TryCreate(node.Root,
                        new DirectoryInfo(SearchPath.ToExtended(node.Path)), out entry)
                    || entry == null)
                {
                    state.SkippedEntries++;
                    return;
                }
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                state.SkippedEntries++;
                return;
            }

            _store.Upsert(entry);
        }

        private void Add(SearchEntry entry, ScanState state, ScanMode mode, bool live, CancellationToken token,
            Dictionary<string, SearchEntry> existing)
        {
            if (live)
            {
                // ScanSubtree: the watcher needs the rows, but not one commit per file.
                // BatchSize rows per transaction is the difference between ~80 rows/s
                // and thousands; the caller flushes the remainder when the walk ends.
                state.Batch.Add(entry);
                if (state.Batch.Count >= BatchSize) Flush(state, mode, live, token);
                return;
            }

            if (existing != null)
            {
                // Reconcile session: the session owns the one transaction of the pass,
                // so only a row that actually differs is written. Removing it from the
                // map marks it seen; whatever is left disappeared from this directory.
                SearchEntry current;
                if (!existing.TryGetValue(entry.Path, out current) || Differs(current, entry))
                    _reconcileSession.Upsert(entry);

                existing.Remove(entry.Path);
                return;
            }

            state.Batch.Add(entry);
            if (mode == ScanMode.Reconcile) state.Seen.Add(entry.Path);

            if (state.Batch.Count >= BatchSize) Flush(state, mode, live, token);
        }

        /// <summary>
        /// The indexed rows of one directory, or null when this walk does not diff (a
        /// full scan, a live subtree, or a caller that passed no session). Keyed
        /// case-insensitively: the index compares paths that way.
        /// </summary>
        private Dictionary<string, SearchEntry> ReadExistingChildren(string directory, ScanMode mode, bool live,
            CancellationToken token)
        {
            if (_reconcileSession == null || mode != ScanMode.Reconcile || live) return null;

            IReadOnlyList<SearchEntry> rows = _reconcileSession.ReadChildren(directory, token);
            var existing = new Dictionary<string, SearchEntry>(StringComparer.OrdinalIgnoreCase);
            if (rows == null) return existing;

            for (int i = 0; i < rows.Count; i++)
            {
                SearchEntry row = rows[i];
                if (row == null || string.IsNullOrEmpty(row.Path)) continue;
                existing[row.Path] = row;
            }
            return existing;
        }

        /// <summary>
        /// The row values a reconcile compares; everything else derives from them. Root
        /// is included because the upsert writes it: a row indexed under a previous root
        /// (a path that now belongs to a different configured root) must be repaired.
        /// </summary>
        private static bool Differs(SearchEntry current, SearchEntry entry)
        {
            return !string.Equals(current.Name, entry.Name, StringComparison.Ordinal)
                || current.IsDirectory != entry.IsDirectory
                || current.SizeBytes != entry.SizeBytes
                || current.ModifiedUtc != entry.ModifiedUtc
                || !string.Equals(current.Root ?? string.Empty, entry.Root ?? string.Empty,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Deletes the indexed rows of one directory that its current listing did not
        /// produce. A gone directory takes its whole indexed subtree with it.
        /// </summary>
        private void PruneMissingChildren(Dictionary<string, SearchEntry> existing)
        {
            foreach (SearchEntry row in existing.Values)
            {
                if (row.IsDirectory) _reconcileSession.DeletePathAndPrefix(row.Path);
                else _reconcileSession.Delete(row.Path);
            }
        }

        private void Flush(ScanState state, ScanMode mode, bool live, CancellationToken token)
        {
            if (state.Batch.Count == 0) return;

            // Never write a batch the caller may have to throw away anyway.
            token.ThrowIfCancellationRequested();

            _store.UpsertScanBatch(state.Batch);
            if (!live && mode == ScanMode.Reconcile) _store.MarkSeenBatch(state.Seen);

            state.Batch.Clear();
            state.Seen.Clear();
        }

        private static void MarkUnreadable(string path, ScanState state)
        {
            if (!state.UnreadableDirectories.Contains(path))
                state.UnreadableDirectories.Add(path);
        }

        private bool IsReparsePoint(string path)
        {
            if (_isReparsePoint != null && _isReparsePoint(path)) return true;

            try
            {
                var attributes = File.GetAttributes(SearchPath.ToExtended(path));
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                return false;
            }
        }

        private static bool IsFileSystemError(Exception ex)
        {
            return ex is UnauthorizedAccessException
                || ex is IOException
                || ex is ArgumentException
                || ex is NotSupportedException;
        }

        private static bool IsUnreadableError(Exception ex)
        {
            // DirectoryNotFoundException derives from IOException.
            return ex is UnauthorizedAccessException || ex is IOException;
        }

        private static void TrySetPriority(ThreadPriority priority)
        {
            try
            {
                Thread.CurrentThread.Priority = priority;
            }
            catch (ThreadStateException)
            {
            }
            catch (SecurityException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        /// <summary>Why a directory is or is not walked; only <see cref="Excluded"/> prunes.</summary>
        private enum DirectoryVerdict
        {
            Walk,
            TooLong,
            Vetoed,
            Excluded
        }

        private struct DirectoryNode
        {
            public DirectoryNode(string path, string name, string root, bool isStart)
            {
                Path = path;
                Name = name;
                Root = root;
                IsStart = isStart;
            }

            /// <summary>Plain, normalized directory path.</summary>
            public string Path { get; private set; }

            /// <summary>Leaf name, or null for a configured root (the filter derives it).</summary>
            public string Name { get; private set; }

            /// <summary>Plain scan root this subtree belongs to.</summary>
            public string Root { get; private set; }

            /// <summary>
            /// True for a starting node of the walk. Only a live subtree writes a row
            /// for it; every other directory row comes from its parent's enumeration.
            /// </summary>
            public bool IsStart { get; private set; }
        }

        private sealed class ScanState
        {
            public ScanState(int capacity)
            {
                Batch = new List<SearchEntry>(capacity);
                Seen = new List<string>(capacity);
            }

            public long Directories;
            public long Files;
            public long SkippedDirectories;
            public long SkippedExcluded;
            public long SkippedReparsePoints;
            public long SkippedLongPaths;
            public long SkippedEntries;
            public string CurrentDirectory;
            public readonly List<SearchEntry> Batch;
            public readonly List<string> Seen;
            public readonly List<string> UnreadableDirectories = new List<string>();

            /// <summary>Time since the walk started, for the progress throttle.</summary>
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public long LastProgressMs;

            public ScanProgress ToProgress()
            {
                return new ScanProgress
                {
                    Directories = Directories,
                    Files = Files,
                    SkippedDirectories = SkippedDirectories,
                    SkippedExcluded = SkippedExcluded,
                    SkippedReparsePoints = SkippedReparsePoints,
                    SkippedLongPaths = SkippedLongPaths,
                    SkippedEntries = SkippedEntries,
                    CurrentDirectory = CurrentDirectory
                };
            }

            public ScanResult ToResult()
            {
                return new ScanResult
                {
                    Directories = Directories,
                    Files = Files,
                    SkippedDirectories = SkippedDirectories,
                    SkippedExcluded = SkippedExcluded,
                    SkippedReparsePoints = SkippedReparsePoints,
                    SkippedLongPaths = SkippedLongPaths,
                    SkippedEntries = SkippedEntries,
                    Cancelled = false,
                    UnreadableDirectories = new List<string>(UnreadableDirectories)
                };
            }
        }
    }
}
