using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Kobold.Core
{
    /// <summary>
    /// Append-only diagnostics for the app itself, at
    /// <c>%LocalAppData%\Kobold\logs\app.log</c>. The control server and the
    /// request router write here so "the pipe did nothing" leaves evidence.
    ///
    /// Diagnostics must never break the app: every filesystem error is swallowed,
    /// and the file rotates to <c>.1</c> once an append would take it to
    /// <see cref="MaxBytes"/> or beyond.
    /// </summary>
    public static class AppDiagnostics
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
                    RotateIfNeeded(path, Encoding.UTF8.GetByteCount(line));
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Diagnostics must never surface as an app failure.
            }
        }

        /// <summary>
        /// Rotates before the append when the incoming line would take the live
        /// log to <see cref="MaxBytes"/> or beyond. Projecting the append size
        /// (rather than only looking at the current size) means a single
        /// oversized line still archives the previous content in <c>.1</c> and
        /// leaves the new line in a fresh live log.
        /// </summary>
        private static void RotateIfNeeded(string path, int incomingBytes)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) return;
                if (info.Length + incomingBytes < _maxBytes) return;

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
            return Path.Combine(root, "Kobold", "logs", "app.log");
        }
    }
}
