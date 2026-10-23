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

        private enum WatcherEventKind
        {
            Created,
            Changed,
            Deleted,
            Renamed
        }

        private sealed class WatcherEvent
        {
            public WatcherEvent(WatcherEventKind kind, string path, string oldPath, string root)
            {
                Kind = kind;
                Path = path;
                OldPath = oldPath;
                Root = root;
            }

            public WatcherEventKind Kind { get; private set; }
            public string Path { get; private set; }
            public string OldPath { get; private set; }
            public string Root { get; private set; }
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
        private volatile bool _incrementalAvailable;

        /// <param name="progress">
        /// Optional progress sink for the subtree scan that repairs a directory
        /// rename. The index service passes null: subtree counters are local and
        /// must not replace the status counters of the last full scan.
        /// </param>
        public FileSystemWatcherHost(ISearchIndexStore store, FileSearchSettings settings,
            Action<ScanProgress> progress = null)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (settings == null) throw new ArgumentNullException("settings");

            _store = store;
            _settings = settings;
            _progress = progress;

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
                    var queue = new BlockingCollection<WatcherEvent>(QueueCapacity);
                    var cts = new CancellationTokenSource();
                    CancellationToken token = cts.Token;
                    _queue = queue;
                    _cts = cts;

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
                for (int i = 0; i < batch.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        ApplyEvent(batch[i], token);
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

            // Created/Changed. Paths outside the configured scope are ignored before
            // any store call: writing a delete for every excluded file (the index
            // database itself, for one) would make the watcher feed on its own writes.
            if (!InScope(path) || !IsIncludedKind(path)) return;

            if (!TryUpsert(item.Root, path))
            {
                // The item disappeared between the event and the stat: drop its row.
                _store.Delete(path);
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

            // A directory rename is reported only for the directory itself; its
            // children are not re-enumerated by the OS watcher, so the new subtree
            // has to be read live (no staging table involved).
            bool isDirectory;
            try { isDirectory = Directory.Exists(SearchPath.ToExtended(path)); }
            catch (Exception) { isDirectory = false; }

            if (isDirectory)
            {
                if (!_settings.IndexFolders) return;
                new DirectoryScanner(_store, _settings, _progress).ScanSubtree(item.Root, path, token);
                return;
            }

            if (!TryUpsert(item.Root, path)) _store.Delete(path);
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
        /// Cheap pre-filter so an excluded type never reaches the store. Directory
        /// events still need the stat below because the name alone cannot tell the
        /// two apart.
        /// </summary>
        private bool IsIncludedKind(string path)
        {
            bool isDirectory;
            try { isDirectory = Directory.Exists(SearchPath.ToExtended(path)); }
            catch (Exception) { isDirectory = false; }

            if (isDirectory) return _settings.IndexFolders;
            return SearchTypeFilter.IsFileIncluded(path, _settings);
        }

        private bool TryUpsert(string root, string path)
        {
            FileSystemInfo info;
            try
            {
                string extended = SearchPath.ToExtended(path);
                info = Directory.Exists(extended)
                    ? (FileSystemInfo)new DirectoryInfo(extended)
                    : new FileInfo(extended);
            }
            catch (Exception)
            {
                return false;
            }

            SearchEntry entry;
            if (!SearchEntry.TryCreate(root, info, out entry) || entry == null) return false;

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

        private void Enqueue(RootSlot slot, WatcherEventKind kind, string path, string oldPath)
        {
            BlockingCollection<WatcherEvent> queue = _queue;
            if (queue == null) return;

            var item = new WatcherEvent(kind, path, oldPath, slot.Root);
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
                    Enqueue(slot, WatcherEventKind.Created, args.FullPath, null);
                };
                watcher.Changed += delegate(object sender, FileSystemEventArgs args)
                {
                    Enqueue(slot, WatcherEventKind.Changed, args.FullPath, null);
                };
                watcher.Deleted += delegate(object sender, FileSystemEventArgs args)
                {
                    Enqueue(slot, WatcherEventKind.Deleted, args.FullPath, null);
                };
                watcher.Renamed += delegate(object sender, RenamedEventArgs args)
                {
                    Enqueue(slot, WatcherEventKind.Renamed, args.FullPath, args.OldFullPath);
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
