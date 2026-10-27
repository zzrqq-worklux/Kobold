using System;
using System.Collections.Generic;
using System.Threading;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// One reconcile pass against the live index: reads the rows a directory
    /// currently has, writes only real differences and prunes what disappeared.
    /// The whole pass is one connection and one transaction, so cancelling or
    /// failing rolls it back instead of leaving half-applied batches behind.
    /// </summary>
    public interface IReconcileSession : IDisposable
    {
        /// <summary>
        /// The rows that are direct children of <paramref name="directory"/> (never
        /// its own row and never descendants). One indexed lookup by the stored parent
        /// key, which is case-insensitive like the paths it is derived from.
        /// Throws <see cref="OperationCanceledException"/> when the token fires; a
        /// directory with many children is marshalled row by row, so the check runs
        /// periodically.
        /// </summary>
        IReadOnlyList<SearchEntry> ReadChildren(string directory, CancellationToken token);

        void Upsert(SearchEntry entry);
        void Delete(string path);
        void DeletePathAndPrefix(string path);

        /// <summary>Publishes every write of the pass and closes the session.</summary>
        void Commit();
    }

    /// <summary>
    /// The file-search index. Writes are serialized inside the store (single writer);
    /// queries run on their own read-only connection and may race a scan. A full
    /// scan stages rows in a shadow table and only swaps it in on completion, so a
    /// cancelled scan never damages the index that is currently being searched.
    /// </summary>
    public interface ISearchIndexStore : IDisposable
    {
        string DatabasePath { get; }

        /// <summary>
        /// True between <see cref="BeginFullScan"/> and its complete/abort. A full
        /// scan written without it would land in the live table, so the scanner
        /// refuses to start in that state.
        /// </summary>
        bool IsFullScanActive { get; }

        void EnsureSchema();

        void BeginFullScan();
        void UpsertScanBatch(IReadOnlyList<SearchEntry> entries);
        void CompleteFullScan(long skippedDirectories, long skippedReparsePoints, long skippedLongPaths, string fingerprint);
        void AbortFullScan();

        /// <summary>
        /// Starts a legacy reconcile: creates the <c>reconcile_seen</c> table that
        /// <see cref="CompleteReconcile"/> anti-joins at the end. Kept for the batched
        /// scanner path and its pins; the service uses <see cref="OpenReconcileSession"/>.
        /// </summary>
        void BeginReconcile();
        void MarkSeenBatch(IReadOnlyList<string> paths);

        /// <summary>
        /// Marks every existing row at or below each prefix as seen. Used when a
        /// directory could not be read: its subtree is incomplete, so a reconcile
        /// must not prune the rows it could not visit. Legacy path only; it requires an
        /// active <see cref="BeginReconcile"/>.
        /// </summary>
        void MarkSeenPathsUnder(IReadOnlyList<string> prefixes);

        /// <summary>
        /// Completes a <see cref="BeginReconcile"/> pass: drops the rows of each root
        /// that were never marked seen. This is the legacy reconcile commit; new
        /// callers use <see cref="OpenReconcileSession"/> instead.
        /// </summary>
        void CompleteReconcile(IReadOnlyList<string> roots);

        /// <summary>
        /// Drops the legacy seen table if there is one. Safe when no reconcile is
        /// active, so the service's quiet abort helper can always call it.
        /// </summary>
        void AbortReconcile();

        /// <summary>
        /// Opens a reconcile session: one write connection and one transaction for a
        /// whole pass, with per-directory diff reads and writes. Only one session can
        /// be open at a time; the session holds the store's single-writer gate until
        /// it is committed or disposed, and either call may run on a different thread.
        /// <see cref="Dispose"/> without <see cref="IReconcileSession.Commit"/> rolls
        /// the pass back.
        /// </summary>
        IReconcileSession OpenReconcileSession();

        void Upsert(SearchEntry entry);
        void Delete(string path);
        void DeletePathAndPrefix(string path);

        SearchQueryResult Query(string text, int limit, CancellationToken token);
        IndexStatus ReadStatus();
        long GetDatabaseSizeBytes();
        void Checkpoint();
        void Vacuum();
    }
}
