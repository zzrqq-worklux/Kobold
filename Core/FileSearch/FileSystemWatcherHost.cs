using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Watches every configured root and keeps the live index in step with the
    /// filesystem. One <see cref="FileSystemWatcher"/> per root only enqueues
    /// events; one background worker applies them serially to the store, so a
    /// burst of changes never runs SQLite on a watcher callback thread.
    ///
    /// The queue is bounded. A full queue is never waited on (that would stall
    /// the OS callback); the host drops the event, reports overflow and asks for
    /// a reconcile, which is the same repair path used when the OS buffer
    /// overflows and when a watcher cannot be started at all.
    ///
    /// <see cref="PauseProcessing"/> lets the owner freeze incremental writes
    /// while a full scan swaps tables or a reconcile collects its "seen" set.
    /// Events keep arriving in the queue; after <see cref="ResumeProcessing"/>
    /// the worker drains them against the new/published index.
    /// </summary>
    public sealed class FileSystemWatcherHost : IDisposable
    {
        /// <summary>Bounded so a disk-wide change storm cannot grow without limit.</summary>
        public const int QueueCapacity = 20000;

        /// <summary>Events applied in one go before the store is touched again.</summary>
        public const int MaxBatchSize = 500;

        /// <summary>Wall clock budget for collecting a batch after its first event.</summary>
        public const int BatchWindowMilliseconds = 200;

        internal enum WatcherEventKind
        {
            Created,
            Changed,
            Deleted,
            Renamed
        }

        internal sealed class WatcherEvent
        {
            public WatcherEvent(WatcherEventKind kind, string path, string oldPath)
            {
                Kind = kind;
                Path = path;
                OldPath = oldPath;
            }

            public WatcherEventKind Kind { get; private set; }
            public string Path { get; private set; }
            public string OldPath { get; private set; }
        }

        private sealed class RootSlot
        {
            public RootSlot(string root)
            {
                Root = root;
            }

            public string Root { get; private set; }
            public FileSystemWatcher Watcher { get; set; }
        }

        private readonly ISearchIndexStore _store;
        private readonly FileSearchSettings _settings;
        private readonly Action<ScanProgress> _progress;
        private readonly string _databasePath;
        private readonly string _databaseWalPath;
        private readonly string _databaseShmPath;

        private readonly object _lifecycleGate = new object();
        private readonly object _pauseGate = new object();
        private readonly List<RootSlot> _slots = new List<RootSlot>();

        private BlockingCollection<WatcherEvent> _queue;
        private CancellationTokenSource _cts;
        private Thread _worker;
        private bool _started;
        private bool _disposed;
        private bool _paused;
        private int _pauseDepth;
        private int _activeBatches;
        private int _reconcilePending;
        private readonly int _queueCapacity;
        private volatile bool _incrementalAvailable;

        /// <param name="progress">
        /// Optional progress sink for the subtree scan that repairs a directory
        /// rename. The index service passes null: subtree counters are local and
        /// must not replace the status counters of the last full scan.
        /// </param>
        /// <param name="queueCapacity">
        /// Bound of the event queue. The production default is
        /// <see cref="QueueCapacity"/>; tests shrink it to make an overflow
        /// deterministic.
        /// </param>
        public FileSystemWatcherHost(ISearchIndexStore store, FileSearchSettings settings,
            Action<ScanProgress> progress = null, int queueCapacity = QueueCapacity)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (settings == null) throw new ArgumentNullException("settings");
            if (queueCapacity < 1)
            {
                throw new ArgumentOutOfRangeException("queueCapacity", "The queue needs at least one slot.");
            }

            _store = store;
            _settings = settings;
            _progress = progress;
            _queueCapacity = queueCapacity;

            _databasePath = store.DatabasePath ?? string.Empty;
            _databaseWalPath = _databasePath.Length == 0 ? string.Empty : _databasePath + "-wal";
            _databaseShmPath = _databasePath.Length == 0 ? string.Empty : _databasePath + "-shm";
        }

        /// <summary>
        /// Raised when the queue overflowed, a watcher reported an OS error, or an
        /// event could not be applied. Handlers run on whatever thread noticed the
        /// problem (a watcher callback or the worker); they must only schedule a
        /// reconcile, never run one inline.
        /// </summary>
        public event EventHandler ReconcileRequested;

        /// <summary>True only while every requested root has a live watcher.</summary>
        public bool IncrementalAvailable
        {
            get { return _incrementalAvailable; }
        }

        /// <summary>Number of roots with a live watcher; 0 means "reconcile only".</summary>
        public int WatchedRoots
        {
            get
            {
                lock (_lifecycleGate)
                {
                    int count = 0;
                    for (int i = 0; i < _slots.Count; i++)
                    {
                        if (_slots[i].Watcher != null) count++;
                    }
                    return count;
                }
            }
        }

        /// <summary>
        /// Starts one watcher per root and the single worker. Roots that cannot be
        /// watched are reported through the return value and
        /// <see cref="IncrementalAvailable"/>; this method never throws for a bad
        /// root, because one unreadable drive must not disable file search.
        /// </summary>
        public bool Start(IReadOnlyList<string> roots)
        {
            lock (_lifecycleGate)
            {
                if (_disposed || _started) return false;

                _started = true;
                _slots.Clear();
                _incrementalAvailable = false;

                // The queue exists before any watcher starts raising events: a
                // callback that fires between EnableRaisingEvents and the worker
                // start must find a live queue, not a null field.
                var queue = new BlockingCollection<WatcherEvent>(_queueCapacity);
                var cts = new CancellationTokenSource();
                CancellationToken token = cts.Token;
                _queue = queue;
                _cts = cts;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (roots != null)
                {
                    for (int i = 0; i < roots.Count; i++)
                    {
                        string root = SearchPath.Normalize(roots[i]);
                        if (root.Length == 0 || !seen.Add(root)) continue;

                        // A slot is kept even when the watcher cannot be created:
                        // the root still counts as "requested but degraded", and a
                        // later error restart can retry the same slot.
                        var slot = new RootSlot(root);
                        _slots.Add(slot);
                        AttachLocked(slot);
                    }
                }

                _incrementalAvailable = AllWatchedLocked();

                // A live watcher needs a consumer. With no watchable root the host
                // still exists (and still reports reconcile requests), but it costs
                // no background thread.
                if (WatchedRootsLocked() > 0)
                {
                    // Capture locals, not the fields: Stop may null those before the
                    // new thread starts, and reading a null field inside the thread
                    // lambda would be an unhandled exception in a background thread.
                    var worker = new Thread(delegate()
                    {
                        try { WorkerLoop(queue, token); }
                        catch (Exception)
                        {
                            _incrementalAvailable = false;
                            RequestReconcile();
                        }
                    });
                    worker.IsBackground = true;
                    worker.Name = "Kobold file-search watcher";
                    try { worker.Priority = ThreadPriority.BelowNormal; }
                    catch (Exception) { }
                    _worker = worker;
                    worker.Start();
                }
                else
                {
                    // No watcher can raise an event: release the unused queue.
                    _queue = null;
                    _cts = null;
                    try { queue.Dispose(); }
                    catch (Exception) { }
                    try { cts.Dispose(); }
                    catch (Exception) { }
                }

                return _incrementalAvailable;
            }
        }

        /// <summary>
        /// Stops the watchers, cancels the worker, drops queued events and releases
        /// the timer-facing state. Safe to call twice and safe while paused (the
        /// pause is released so the worker can exit).
        /// </summary>
        public void Stop()
        {
            CancellationTokenSource cts;
            BlockingCollection<WatcherEvent> queue;
            Thread worker;
            var watchers = new List<FileSystemWatcher>();

            lock (_lifecycleGate)
            {
                if (!_started)
                {
                    _incrementalAvailable = false;
                    return;
                }

                _started = false;
                _incrementalAvailable = false;

                for (int i = 0; i < _slots.Count; i++)
                {
                    RootSlot slot = _slots[i];
                    if (slot.Watcher != null) watchers.Add(slot.Watcher);
                    slot.Watcher = null;
                }
                _slots.Clear();

                cts = _cts;
                queue = _queue;
                worker = _worker;
                _cts = null;
                _queue = null;
                _worker = null;
            }

            // Disposing the watchers first stops new events; cancelling wakes the
            // worker even if it is waiting for a resume that will never come.
            for (int i = 0; i < watchers.Count; i++)
            {
                DisposeWatcher(watchers[i]);
            }

            ResumeAllProcessing();

            if (cts != null)
            {
                try { cts.Cancel(); }
                catch (Exception) { }
            }

            if (queue != null)
            {
                try { queue.CompleteAdding(); }
                catch (Exception) { }
            }

            bool exited = worker == null;
            if (worker != null && worker != Thread.CurrentThread)
            {
                try { exited = worker.Join(2000); }
                catch (ThreadStateException) { exited = true; }
                catch (Exception) { }
            }

            // Disposing a queue the worker still polls would throw inside the
            // background thread. A worker that missed its 2s bound owns the queue
            // until it exits; the leak is one object, not a store.
            if (exited && queue != null)
            {
                try { queue.Dispose(); }
                catch (Exception) { }
            }
            if (cts != null)
            {
                try { cts.Dispose(); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Freezes store writes from the worker. Any batch already being applied
        /// finishes first, so the caller can safely start a full scan/reconcile as
        /// soon as this returns. Events keep queueing (and may overflow).
        /// </summary>
        public void PauseProcessing()
        {
            lock (_pauseGate)
            {
                if (_disposed) return;

                _pauseDepth++;
                if (_pauseDepth > 1) return;

                _paused = true;
                while (_activeBatches > 0)
                {
                    Monitor.Wait(_pauseGate, 100);
                }
            }
        }

        /// <summary>Releases one <see cref="PauseProcessing"/> level and drains the queue.</summary>
        public void ResumeProcessing()
        {
            lock (_pauseGate)
            {
                if (_pauseDepth == 0) return;

                _pauseDepth--;
                if (_pauseDepth > 0) return;

                _paused = false;
                Monitor.PulseAll(_pauseGate);
            }
        }

        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            Stop();
        }

        private void WorkerLoop(BlockingCollection<WatcherEvent> queue, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    WaitWhilePaused(token);
                    if (token.IsCancellationRequested) break;

                    WatcherEvent first;
                    if (!queue.TryTake(out first, BatchWindowMilliseconds, token))
                    {
                        if (queue.IsCompleted) break;
                        continue;
                    }

                    var batch = new List<WatcherEvent>(MaxBatchSize);
                    batch.Add(first);

                    var window = Stopwatch.StartNew();
                    while (batch.Count < MaxBatchSize && !token.IsCancellationRequested)
                    {
                        int remaining = BatchWindowMilliseconds - (int)window.ElapsedMilliseconds;
                        if (remaining <= 0) break;

                        WatcherEvent next;
                        if (!queue.TryTake(out next, remaining, token)) break;
                        batch.Add(next);
                    }

                    ApplyBatch(batch, token);

                    // Caught up completely: a later overflow may ask for a new
                    // reconcile instead of being coalesced into the old request.
                    if (queue.Count == 0)
                    {
                        Interlocked.Exchange(ref _reconcilePending, 0);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    // One bad event/batch must not kill incremental updates forever.
                    // Report the failure and let the next event retry.
                    _incrementalAvailable = false;
                    RequestReconcile();
                    try { Thread.Sleep(500); }
                    catch (ThreadInterruptedException) { }
                }
            }
        }

        private void WaitWhilePaused(CancellationToken token)
        {
            lock (_pauseGate)
            {
                while (_paused && !token.IsCancellationRequested)
                {
                    Monitor.Wait(_pauseGate, 100);
                }
            }
        }

        private void ApplyBatch(List<WatcherEvent> batch, CancellationToken token)
        {
            // The pause check and the in-flight counter must be changed under the
            // same lock: otherwise a Pause that starts right now could see zero
            // batches and return while this batch is still about to write.
            lock (_pauseGate)
            {
                while (_paused && !token.IsCancellationRequested)
                {
                    Monitor.Wait(_pauseGate, 100);
                }
                if (token.IsCancellationRequested) return;
                _activeBatches++;
            }

            try
            {
                List<WatcherEvent> collapsed = Coalesce(batch);
                for (int i = 0; i < collapsed.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        ApplyEvent(collapsed[i], token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (ObjectDisposedException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // A failed single row is repaired by the next reconcile.
                        RequestReconcile();
                    }
                }
            }
            finally
            {
                lock (_pauseGate)
                {
                    _activeBatches--;
                    if (_activeBatches == 0) Monitor.PulseAll(_pauseGate);
                }
            }
        }

        /// <summary>
        /// Collapses a batch to its last <c>Created/Changed/Deleted</c> event per path,
        /// so a burst on one file costs one stat and one upsert. A
        /// <see cref="WatcherEventKind.Renamed"/> is never collapsed away: it carries
        /// the delete of its old path and the subtree scan of the destination, so
        /// dropping it when a Created for the destination lands in the same batch would
        /// lose a whole subtree. The order between different paths is preserved.
        /// </summary>
        private static List<WatcherEvent> Coalesce(List<WatcherEvent> batch)
        {
            // The worker also hands over one-event batches; those already are their
            // own collapse and must not pay for a dictionary and its hashing.
            if (batch.Count < 2) return batch;

            var last = new Dictionary<string, int>(batch.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch[i].Kind == WatcherEventKind.Renamed) continue;
                last[batch[i].Path ?? string.Empty] = i;
            }

            var collapsed = new List<WatcherEvent>(batch.Count);
            for (int i = 0; i < batch.Count; i++)
            {
                WatcherEvent item = batch[i];
                if (item.Kind == WatcherEventKind.Renamed || last[item.Path ?? string.Empty] == i)
                {
                    collapsed.Add(item);
                }
            }
            return collapsed;
        }

        private void ApplyEvent(WatcherEvent item, CancellationToken token)
        {
            string path = SearchPath.Normalize(item.Path);
            if (path.Length == 0) return;

            if (item.Kind == WatcherEventKind.Deleted)
            {
                if (InScope(path)) _store.DeletePathAndPrefix(path);
                return;
            }

            if (item.Kind == WatcherEventKind.Renamed)
            {
                ApplyRename(item, path, token);
                return;
            }

            // Created/Changed. Paths outside every watched root (and excluded paths)
            // are ignored before any store call: writing a delete for every excluded
            // file (the index database itself, for one) would make the watcher feed
            // on its own writes.
            string root = FindContainingRoot(path);
            if (root == null || !InScope(path)) return;

            // The type filter is string-only, so the common case costs no stat. Only a
            // name it rejects can still be a directory (C:\dir\v1.2): one stat decides
            // that, and a directory then follows the folder gate.
            if (!SearchTypeFilter.IsFileIncluded(path, _settings) && !IsIncludedDirectory(path)) return;

            bool isDirectory;
            if (!TryUpsert(root, path, out isDirectory) && !isDirectory)
            {
                // The item disappeared between the event and the stat, so nothing tells
                // us whether it was a file or a directory: the prefix delete covers the
                // row and, for a directory, the children it took with it.
                _store.DeletePathAndPrefix(path);
            }
        }

        private void ApplyRename(WatcherEvent item, string path, CancellationToken token)
        {
            string oldPath = SearchPath.Normalize(item.OldPath ?? string.Empty);
            if (oldPath.Length > 0 && InScope(oldPath))
            {
                _store.DeletePathAndPrefix(oldPath);
            }

            if (!InScope(path)) return;

            // A rename can leave the watched tree (a same-volume move to a sibling
            // outside the root). The old row is gone above; the destination must not
            // be inserted under a root it does not belong to.
            string root = FindContainingRoot(path);
            if (root == null) return;

            // A directory rename is reported only for the directory itself; its
            // children are not re-enumerated by the OS watcher, so the new subtree
            // has to be read live (no staging table involved). ScanSubtree already
            // gates the directory row on IndexFolders and always writes the files:
            // skipping it here would erase a renamed folder's files when folder rows
            // are disabled.
            bool isDirectory;
            try { isDirectory = Directory.Exists(SearchPath.ToExtended(path)); }
            catch (Exception) { isDirectory = false; }

            if (isDirectory)
            {
                new DirectoryScanner(_store, _settings, _progress).ScanSubtree(root, path, token);
                return;
            }

            if (!TryUpsert(root, path, out _)) _store.Delete(path);
        }

        /// <summary>
        /// The watched root that contains <paramref name="path"/>, longest match
        /// first so a nested configured root owns its own subtree. Null means the
        /// path is outside every watched tree.
        /// </summary>
        private string FindContainingRoot(string path)
        {
            lock (_lifecycleGate)
            {
                string best = null;
                for (int i = 0; i < _slots.Count; i++)
                {
                    string root = _slots[i].Root;
                    if (!SearchPath.IsUnder(path, root)) continue;
                    if (best == null || root.Length > best.Length) best = root;
                }
                return best;
            }
        }

        /// <summary>False for the index database and its WAL sidecars, which live on a watched drive.</summary>
        private bool IsDatabaseFile(string path)
        {
            return Matches(path, _databasePath)
                || Matches(path, _databaseWalPath)
                || Matches(path, _databaseShmPath);
        }

        private static bool Matches(string path, string candidate)
        {
            return candidate.Length > 0
                && string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase);
        }

        private bool InScope(string path)
        {
            if (IsDatabaseFile(path)) return false;
            return !SearchDirectoryFilter.IsExcluded(path, null, _settings);
        }

        /// <summary>
        /// True when a path the string type check rejected is a directory the settings
        /// index. One stat, reached only when the name alone says no; with folder rows
        /// disabled the answer is already no, so no stat is paid at all.
        /// </summary>
        private bool IsIncludedDirectory(string path)
        {
            if (!_settings.IndexFolders) return false;

            try { return Directory.Exists(SearchPath.ToExtended(path)); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Writes one row from one stat: <see cref="SearchEntry.TryCreate"/> reads the
        /// attributes, size and timestamp together and reports <c>IsDirectory</c>, so a
        /// <see cref="FileInfo"/> covers a directory too. False means nothing was
        /// written - the item vanished or the settings keep it out; <paramref name="isDirectory"/>
        /// tells the two apart, so a folder event is never mistaken for a deleted file.
        /// </summary>
        private bool TryUpsert(string root, string path, out bool isDirectory)
        {
            isDirectory = false;

            FileSystemInfo info;
            try
            {
                info = new FileInfo(SearchPath.ToExtended(path));
            }
            catch (Exception)
            {
                return false;
            }

            SearchEntry entry;
            if (!SearchEntry.TryCreate(root, info, out entry) || entry == null) return false;

            isDirectory = entry.IsDirectory;
            if (entry.IsDirectory)
            {
                if (!_settings.IndexFolders) return false;
            }
            else if (!SearchTypeFilter.IsFileIncluded(path, _settings))
            {
                return false;
            }

            _store.Upsert(entry);
            return true;
        }

        private void Enqueue(WatcherEventKind kind, string path, string oldPath)
        {
            BlockingCollection<WatcherEvent> queue = _queue;
            if (queue == null) return;

            var item = new WatcherEvent(kind, path, oldPath);
            try
            {
                if (!queue.TryAdd(item))
                {
                    // Bounded queue full. Never block the OS callback: drop it and
                    // let the reconcile repair everything this batch missed.
                    RequestReconcile();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding during Stop: the event is covered by the scan.
            }
        }

        /// <summary>
        /// Test hook: applies an event batch on the calling thread through the same
        /// gates as the worker, so a check can assert the store writes without
        /// racing the OS watcher. Production never calls it.
        /// </summary>
        internal void ApplyBatchForChecks(IReadOnlyList<WatcherEvent> batch)
        {
            ApplyBatch(new List<WatcherEvent>(batch), CancellationToken.None);
        }

        private void RequestReconcile()
        {
            if (Interlocked.Exchange(ref _reconcilePending, 1) == 1) return;

            EventHandler handler = ReconcileRequested;
            if (handler == null) return;

            try { handler(this, EventArgs.Empty); }
            catch (Exception)
            {
                // A subscriber must not break the watcher callback.
            }
        }

        private void OnWatcherError(RootSlot slot)
        {
            _incrementalAvailable = false;
            RequestReconcile();

            // Restart off the callback thread: disposing the watcher that raised
            // this event inside that event is needlessly delicate.
            try
            {
                ThreadPool.QueueUserWorkItem(delegate { Restart(slot); });
            }
            catch (Exception)
            {
            }
        }

        private void Restart(RootSlot slot)
        {
            lock (_lifecycleGate)
            {
                if (!_started || _disposed) return;
                if (_slots.IndexOf(slot) < 0) return;

                DisposeWatcher(slot.Watcher);
                slot.Watcher = null;
                AttachLocked(slot);
                _incrementalAvailable = AllWatchedLocked();
            }
        }

        private void AttachLocked(RootSlot slot)
        {
            FileSystemWatcher watcher = null;
            try
            {
                watcher = new FileSystemWatcher(slot.Root);
                watcher.IncludeSubdirectories = true;
                watcher.InternalBufferSize = 64 * 1024;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                    | NotifyFilters.Size | NotifyFilters.LastWrite;

                watcher.Created += delegate(object sender, FileSystemEventArgs args)
                {
                    Enqueue(WatcherEventKind.Created, args.FullPath, null);
                };
                watcher.Changed += delegate(object sender, FileSystemEventArgs args)
                {
                    Enqueue(WatcherEventKind.Changed, args.FullPath, null);
                };
                watcher.Deleted += delegate(object sender, FileSystemEventArgs args)
                {
                    Enqueue(WatcherEventKind.Deleted, args.FullPath, null);
                };
                watcher.Renamed += delegate(object sender, RenamedEventArgs args)
                {
                    Enqueue(WatcherEventKind.Renamed, args.FullPath, args.OldFullPath);
                };
                watcher.Error += delegate { OnWatcherError(slot); };

                watcher.EnableRaisingEvents = true;
                slot.Watcher = watcher;
            }
            catch (Exception)
            {
                // A missing drive, a denied root, an invalid path or a too-large
                // buffer: this root degrades to periodic reconcile only.
                DisposeWatcher(watcher);
                slot.Watcher = null;
            }
        }

        private int WatchedRootsLocked()
        {
            int count = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Watcher != null) count++;
            }
            return count;
        }

        private bool AllWatchedLocked()
        {
            return _slots.Count > 0 && WatchedRootsLocked() == _slots.Count;
        }

        private void ResumeAllProcessing()
        {
            lock (_pauseGate)
            {
                _pauseDepth = 0;
                _paused = false;
                Monitor.PulseAll(_pauseGate);
            }
        }

        private static void DisposeWatcher(FileSystemWatcher watcher)
        {
            if (watcher == null) return;
            try { watcher.EnableRaisingEvents = false; }
            catch (Exception) { }
            try { watcher.Dispose(); }
            catch (Exception) { }
        }
    }
}
