using System;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// What the store knows about the published index. The store never keeps the
    /// "current settings": the service compares <see cref="SettingsFingerprint"/>
    /// with <c>FileSearchSettings.ComputeFingerprint</c> to decide "needs rebuild".
    /// </summary>
    public sealed class IndexStatus
    {
        public bool HasIndex { get; set; }
        public DateTime? LastScanUtc { get; set; }
        public DateTime? LastReconcileUtc { get; set; }
        public long SkippedDirectories { get; set; }
        public string LastError { get; set; }
        public string SettingsFingerprint { get; set; }
        public int SchemaVersion { get; set; }

        public IndexStatus()
        {
            LastError = string.Empty;
            SettingsFingerprint = string.Empty;
        }
    }
}
