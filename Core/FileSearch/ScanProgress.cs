using System;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Which rows a scan writes. <see cref="Full"/> stages every row in the shadow
    /// table for a later swap; <see cref="Reconcile"/> writes live rows and records
    /// what it saw so vanished rows can be dropped.
    /// </summary>
    public enum ScanMode
    {
        Full,
        Reconcile
    }

    /// <summary>
    /// Throttled snapshot of a running scan (at most one every 500ms plus a final
    /// one). Counters are monotonic; <see cref="CurrentDirectory"/> is the directory
    /// being enumerated when the snapshot was taken.
    /// </summary>
    public sealed class ScanProgress
    {
        public long Directories { get; set; }
        public long Files { get; set; }
        public long SkippedDirectories { get; set; }
        public long SkippedReparsePoints { get; set; }
        public long SkippedLongPaths { get; set; }
        public long SkippedEntries { get; set; }
        public string CurrentDirectory { get; set; }
    }

    /// <summary>
    /// Final counters of one walk. Cancellation is reported as an
    /// <see cref="OperationCanceledException"/> instead of a result, so
    /// <see cref="Cancelled"/> is false on every result the scanner returns; the
    /// caller aborts its full scan/reconcile on the exception.
    /// </summary>
    public sealed class ScanResult
    {
        public long Directories { get; set; }
        public long Files { get; set; }
        public long SkippedDirectories { get; set; }
        public long SkippedReparsePoints { get; set; }
        public long SkippedLongPaths { get; set; }
        public long SkippedEntries { get; set; }
        public bool Cancelled { get; set; }
    }
}
