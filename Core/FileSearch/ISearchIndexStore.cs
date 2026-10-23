using System;
using System.Collections.Generic;
using System.Threading;

namespace Kobold.Core.FileSearch
{
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

        void BeginReconcile();
        void MarkSeenBatch(IReadOnlyList<string> paths);

        /// <summary>
        /// Marks every existing row at or below each prefix as seen. Used when a
        /// directory could not be read: its subtree is incomplete, so a reconcile
        /// must not prune the rows it could not visit.
        /// </summary>
        void MarkSeenPathsUnder(IReadOnlyList<string> prefixes);

        void CompleteReconcile(IReadOnlyList<string> roots);
        void AbortReconcile();

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
