using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Append-only diagnostics for the file-search service, at
    /// <c>%LocalAppData%\Kobold\logs\search.log</c>. The service state and every
    /// query are written here so a transient failure ("no results until it
    /// recovered") leaves evidence instead of an empty in-memory status.
    ///
    /// Diagnostics must never break search: every filesystem error is swallowed,
    /// and the file rotates to <c>.1</c> before it grows past <see cref="MaxBytes"/>.
    /// </summary>
    public static class SearchDiagnostics
    {
        private const long DefaultMaxBytes = 1024 * 1024;

        private static readonly object Gate = new object();
        private static string _path;
        private static long _maxBytes = DefaultMaxBytes;

        /// <summary>Test seam; null means the per-user LocalAppData path.</summary>
        internal static string LogPath
        {
            get { lock (Gate) return _path ?? DefaultLogPath(); }
            set { lock (Gate) _path = value; }
        }

        /// <summary>Test seam for rotation; production keeps one megabyte.</summary>
        internal static long MaxBytes
        {
            get { lock (Gate) return _maxBytes; }
            set { lock (Gate) _maxBytes = value > 0 ? value : DefaultMaxBytes; }
        }

        public static void Write(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " [" + Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) + "] "
                    + message + Environment.NewLine;

                lock (Gate)
                {
                    string path = _path ?? DefaultLogPath();
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    RotateIfNeeded(path);
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Diagnostics must never surface as a search or scan failure.
            }
        }

        /// <summary>Quotes one value for a single log line, keeping the length bounded.</summary>
        public static string Quote(string value)
        {
            if (value == null) return "\"\"";
            string text = value.Length > 200 ? value.Substring(0, 200) + "..." : value;
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < _maxBytes) return;

                string archived = path + ".1";
                if (File.Exists(archived)) File.Delete(archived);
                File.Move(path, archived);
            }
            catch (Exception)
            {
                // A viewer holding the file open must not stop the next line.
            }
        }

        private static string DefaultLogPath()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
            return Path.Combine(root, "Kobold", "logs", "search.log");
        }
    }
}
