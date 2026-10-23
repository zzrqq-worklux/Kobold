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

        private const int MaxPathLength = 32000;
        private const int ProgressIntervalMs = 500;

        private readonly ISearchIndexStore _store;
        private readonly FileSearchSettings _settings;
        private readonly Action<ScanProgress> _progress;
        private readonly Func<string, bool> _isForbiddenDirectory;
        private readonly Func<string, bool> _isReparsePoint;

        /// <param name="isForbiddenDirectory">
        /// Per-directory veto on top of the configured exclusions (the service passes
        /// a "pause/stop here" check).
        /// </param>
        /// <param name="isReparsePoint">
        /// Optional override for reparse detection, so callers and tests can treat a
        /// directory as a junction without creating one.
        /// </param>
        public DirectoryScanner(ISearchIndexStore store, FileSearchSettings settings,
            Action<ScanProgress> progress = null,
            Func<string, bool> isForbiddenDirectory = null,
            Func<string, bool> isReparsePoint = null)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (settings == null) throw new ArgumentNullException("settings");

            _store = store;
            _settings = settings;
            _progress = progress;
            _isForbiddenDirectory = isForbiddenDirectory;
            _isReparsePoint = isReparsePoint;
        }

        /// <summary>
        /// Walks every root and writes the records it finds. A full scan stages rows
        /// for the caller's swap; a reconcile writes live rows and marks the same
        /// batch as seen. Throws <see cref="OperationCanceledException"/> when the
        /// token fires, after which the caller aborts its scan.
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

                if (!EnterDirectory(node, state))
                {
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

            Flush(state, mode, token);
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
        /// </summary>
        private bool EnterDirectory(DirectoryNode node, ScanState state)
        {
            if (node.Path.Length > MaxPathLength)
            {
                state.SkippedLongPaths++;
                return false;
            }

            if (_isForbiddenDirectory != null && _isForbiddenDirectory(node.Path))
            {
                state.SkippedDirectories++;
                return false;
            }

            if (SearchDirectoryFilter.IsExcluded(node.Path, node.Name, _settings))
            {
                state.SkippedDirectories++;
                return false;
            }

            return true;
        }

        private void Enumerate(DirectoryNode node, Stack<DirectoryNode> stack, ScanState state,
            ScanMode mode, bool live, CancellationToken token)
        {
            IEnumerator<FileSystemInfo> items;
            try
            {
                var directory = new DirectoryInfo(SearchPath.ToExtended(node.Path));
                items = directory.EnumerateFileSystemInfos().GetEnumerator();
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Gone, denied or unreadable since it was pushed: skip, keep walking.
                state.SkippedDirectories++;
                return;
            }

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
                        // remainder counts as skipped entries.
                        if (listedAny) state.SkippedEntries++; else state.SkippedDirectories++;
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
                        if (_settings.IndexFolders) Add(entry, state, mode, live, token);
                        stack.Push(new DirectoryNode(path, info.Name, node.Root, false));
                        continue;
                    }

                    state.Files++;
                    Add(entry, state, mode, live, token);
                }
            }
            finally
            {
                items.Dispose();
            }
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

        private void Add(SearchEntry entry, ScanState state, ScanMode mode, bool live, CancellationToken token)
        {
            if (live)
            {
                // ScanSubtree: the watcher needs the row immediately, and no staging
                // table may be involved.
                _store.Upsert(entry);
                return;
            }

            state.Batch.Add(entry);
            if (mode == ScanMode.Reconcile) state.Seen.Add(entry.Path);

            if (state.Batch.Count >= BatchSize) Flush(state, mode, token);
        }

        private void Flush(ScanState state, ScanMode mode, CancellationToken token)
        {
            if (state.Batch.Count == 0) return;

            // Never write a batch the caller may have to throw away anyway.
            token.ThrowIfCancellationRequested();

            _store.UpsertScanBatch(state.Batch);
            if (mode == ScanMode.Reconcile) _store.MarkSeenBatch(state.Seen);

            state.Batch.Clear();
            state.Seen.Clear();
        }

        private static bool IsFileSystemError(Exception ex)
        {
            return ex is UnauthorizedAccessException
                || ex is IOException
                || ex is ArgumentException
                || ex is NotSupportedException;
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
            public long SkippedReparsePoints;
            public long SkippedLongPaths;
            public long SkippedEntries;
            public string CurrentDirectory;
            public readonly List<SearchEntry> Batch;
            public readonly List<string> Seen;

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
                    SkippedReparsePoints = SkippedReparsePoints,
                    SkippedLongPaths = SkippedLongPaths,
                    SkippedEntries = SkippedEntries,
                    Cancelled = false
                };
            }
        }
    }
}
