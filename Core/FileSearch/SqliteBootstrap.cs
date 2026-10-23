using System;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// The one place where the SQLite provider is installed. Microsoft.Data.Sqlite
    /// ships without a provider; the app references the e_sqlite3 provider and the
    /// native library directly (rather than the bundle meta package) so the
    /// installer's file list stays known. <see cref="EnsureInitialized"/> must run
    /// before the first connection and is safe to call from any thread.
    /// </summary>
    public static class SqliteBootstrap
    {
        private static readonly object Gate = new object();
        private static volatile bool _initialized;

        public static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (Gate)
            {
                if (_initialized) return;

                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                SQLitePCL.raw.FreezeProvider();
                _initialized = true;
            }
        }
    }
}
