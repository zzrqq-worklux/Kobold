using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kobold.Services.Update
{
    /// <summary>
    /// A download, verification or staging failure. The message is a short
    /// English phrase; the UI layer owns the user-facing translation.
    /// </summary>
    public class UpdateException : Exception
    {
        public UpdateException(string message)
            : base(message)
        {
        }

        public UpdateException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Downloads a release package to a temporary file while hashing it, then
    /// unpacks it into a staging directory and checks the executable's file
    /// version. Nothing is published until the size and SHA-256 both match, and
    /// a failed extraction never leaves a half-unpacked stage behind.
    /// </summary>
    public static class UpdateDownloader
    {
        private const int BufferSize = 81920;
        private const string PackageExeName = "Kobold.exe";

        /// <summary>
        /// Downloads <paramref name="manifest"/> to <paramref name="destinationPath"/>.
        /// The bytes go to "&lt;destination&gt;.part" first; the file is only
        /// moved into place once its length equals <see cref="UpdateManifest.Size"/>
        /// and its SHA-256 equals <see cref="UpdateManifest.Sha256"/>.
        /// <paramref name="progress"/> receives the cumulative byte count and
        /// may be null. Any failure removes the .part file; cancellation is
        /// rethrown after that cleanup.
        /// </summary>
        public static async Task DownloadAsync(UpdateManifest manifest, string destinationPath,
            IProgress<long> progress, CancellationToken token)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException("destination path is required", nameof(destinationPath));
            }

            string destination = Path.GetFullPath(destinationPath);
            string partPath = destination + ".part";
            string directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            try
            {
                byte[] hash = await DownloadToPartAsync(manifest, partPath, progress, token)
                    .ConfigureAwait(false);

                if (!string.Equals(ToHex(hash), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException("checksum mismatch");
                }

                if (File.Exists(destination)) File.Replace(partPath, destination, null);
                else File.Move(partPath, destination);
            }
            finally
            {
                // Success moves the .part file away; every other exit path
                // (including cancellation) must not leave it on disk.
                TryDeleteFile(partPath);
            }
        }

        /// <summary>
        /// Unpacks <paramref name="zipPath"/> into <paramref name="stageDir"/>
        /// and verifies that the package contains a Kobold.exe whose FileVersion
        /// matches the first three parts of <paramref name="expectedVersion"/>.
        /// Every entry path is normalized and must stay inside the stage
        /// directory. On any failure the stage directory is deleted and the
        /// exception is rethrown; on success the absolute stage path is returned.
        /// </summary>
        public static string Extract(string zipPath, string stageDir, string expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(zipPath))
            {
                throw new ArgumentException("zip path is required", nameof(zipPath));
            }
            if (string.IsNullOrWhiteSpace(stageDir))
            {
                throw new ArgumentException("stage directory is required", nameof(stageDir));
            }
            if (string.IsNullOrWhiteSpace(expectedVersion))
            {
                throw new ArgumentException("expected version is required", nameof(expectedVersion));
            }

            string stage = Path.GetFullPath(stageDir);
            string stagePrefix = stage.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? stage
                : stage + Path.DirectorySeparatorChar;

            try
            {
                Directory.CreateDirectory(stage);
                using (var archive = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string target = SafeTarget(stage, stagePrefix, entry.FullName);
                        if (entry.Name.Length == 0)
                        {
                            // Directory entry ("folder/"): create it and move on.
                            Directory.CreateDirectory(target);
                            continue;
                        }
                        string parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        entry.ExtractToFile(target, true);
                    }
                }

                string exePath = Path.Combine(stage, PackageExeName);
                string installedVersion;
                if (!File.Exists(exePath) ||
                    !TryGetInstalledVersion(exePath, out installedVersion) ||
                    !SameThreePartVersion(expectedVersion, installedVersion))
                {
                    throw new UpdateException("version mismatch");
                }
            }
            catch
            {
                TryDeleteDirectory(stage);
                throw;
            }

            return stage;
        }

        /// <summary>
        /// Reads the FileVersion of <paramref name="exePath"/> and returns its
        /// first three parts (for example "2.0.1"). Returns false when the file
        /// is missing or has no parseable FileVersion.
        /// </summary>
        public static bool TryGetInstalledVersion(string exePath, out string version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;

            string fileVersion;
            try
            {
                fileVersion = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            Version parsed;
            if (!UpdateVersion.TryParse(fileVersion, out parsed)) return false;
            version = ThreeParts(parsed);
            return true;
        }

        private static async Task<byte[]> DownloadToPartAsync(UpdateManifest manifest, string partPath,
            IProgress<long> progress, CancellationToken token)
        {
            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Get, manifest.Url))
            {
                client.Timeout = TimeSpan.FromSeconds(120);
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "Kobold/" + CurrentVersion);

                using (var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    long total = 0;
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var sink = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, BufferSize, true))
                    using (var sha = SHA256.Create())
                    {
                        byte[] buffer = new byte[BufferSize];
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, token)
                            .ConfigureAwait(false)) > 0)
                        {
                            await sink.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                            sha.TransformBlock(buffer, 0, read, null, 0);
                            total += read;
                            if (progress != null) progress.Report(total);
                        }

                        if (total != manifest.Size)
                        {
                            // Size comes before the hash: a truncated download is
                            // a size failure even though its hash also differs.
                            throw new UpdateException("size mismatch");
                        }

                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        return sha.Hash;
                    }
                }
            }
        }

        private static string SafeTarget(string stage, string stagePrefix, string entryName)
        {
            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(stage, entryName));
            }
            catch (ArgumentException)
            {
                throw new UpdateException("unsafe entry");
            }
            catch (NotSupportedException)
            {
                throw new UpdateException("unsafe entry");
            }
            catch (PathTooLongException)
            {
                throw new UpdateException("unsafe entry");
            }

            if (!target.StartsWith(stagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("unsafe entry");
            }
            return target;
        }

        private static bool SameThreePartVersion(string expectedVersion, string installedVersion)
        {
            Version expected;
            if (!UpdateVersion.TryParse(expectedVersion, out expected)) return false;
            return string.Equals(ThreeParts(expected), installedVersion, StringComparison.Ordinal);
        }

        private static string ThreeParts(Version version)
        {
            int build = version.Build < 0 ? 0 : version.Build;
            return version.Major + "." + version.Minor + "." + build;
        }

        /// <summary>The running app's three-part version, as shown in Settings.</summary>
        private static string CurrentVersion
        {
            get
            {
                Version version = typeof(UpdateDownloader).Assembly.GetName().Version;
                return version == null ? "0.0.0" : ThreeParts(version);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2"));
            return builder.ToString();
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
