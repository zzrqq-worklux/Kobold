using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kobold.Services.Update;

namespace Kobold.UpdateCheck
{
    /// <summary>
    /// Behaviour checks for the updater: manifest parsing and validation,
    /// version ordering, the offer/check policies, and the downloader's
    /// verify/extract staging. The download checks talk only to a loopback
    /// HttpListener; nothing leaves the machine and no registry is touched.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/UpdateCheck
    /// </summary>
    internal static class Program
    {
        private const string GoodUrl = "https://api.merrickzq.work/downloads/kobold/Kobold-v2.0.2.zip";
        private const string GoodNotesUrl = "https://api.merrickzq.work/downloads/kobold/notes/2.0.2";
        private const string GoodSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string GoodSize = "7511751";

        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            ManifestAcceptsAValidRelease();
            ManifestRejectsBadInput();
            ManifestAcceptsUppercaseSha();
            VersionOrdering();
            OfferPolicy();
            CheckPolicy();
            ExtractChecks();
            DownloadChecks();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL UPDATE CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        // ---------- manifest ----------

        private static void ManifestAcceptsAValidRelease()
        {
            UpdateManifest manifest;
            string error;
            bool ok = UpdateManifest.TryParse(GoodJson(), out manifest, out error);

            Check(ok, "manifest: a valid release parses" + (ok ? "" : " (" + error + ")"));
            Check(ok && manifest.Version == "2.0.2", "manifest: version is kept as published (got " +
                (manifest == null ? "<null>" : manifest.Version) + ")");
            Check(ok && manifest.Url == GoodUrl, "manifest: download URL is kept");
            Check(ok && manifest.Size == 7511751L, "manifest: size is read as a number");
            Check(ok && manifest.Sha256 == GoodSha, "manifest: sha256 is kept");
            Check(ok && manifest.NotesUrl == GoodNotesUrl, "manifest: notes URL is kept");

            UpdateManifest noNotes;
            Check(UpdateManifest.TryParse(ManifestJson("2.0.2", GoodUrl, GoodSize, GoodSha, null),
                      out noNotes, out error),
                "manifest: notes_url is optional");
            Check(noNotes != null && noNotes.NotesUrl == null,
                "manifest: an absent notes_url stays null");

            UpdateManifest maxSize;
            Check(UpdateManifest.TryParse(ManifestJson("2.0.2", GoodUrl, "200000000", GoodSha, null),
                      out maxSize, out error) && maxSize.Size == 200000000L,
                "manifest: the 200 MB size limit itself is accepted");

            UpdateManifest mixedCaseHost;
            Check(UpdateManifest.TryParse(ManifestJson("2.0.2",
                      "https://API.MerrickZQ.work/downloads/kobold/Kobold-v2.0.2.zip", GoodSize, GoodSha, null),
                      out mixedCaseHost, out error),
                "manifest: the host match ignores case");

            Check(UpdateManifest.DefaultManifestUrl ==
                  "https://api.merrickzq.work/downloads/kobold/latest.json",
                "manifest: default manifest URL is the fixed HTTPS address");
            Check(UpdateManifest.DefaultHost == "api.merrickzq.work",
                "manifest: default host is the fixed host");
        }

        private static void ManifestRejectsBadInput()
        {
            Reject(null, "manifest: null JSON is rejected");
            Reject("", "manifest: empty JSON is rejected");
            Reject("{ not json", "manifest: malformed JSON is rejected");
            Reject("[]", "manifest: a non-object document is rejected");

            Reject(ManifestJson(null, GoodUrl, GoodSize, GoodSha, null),
                "manifest: a missing version is rejected");
            Reject(ManifestJson("2.0", GoodUrl, GoodSize, GoodSha, null),
                "manifest: a two-part version is rejected");
            Reject(ManifestJson("garbage", GoodUrl, GoodSize, GoodSha, null),
                "manifest: a non-numeric version is rejected");
            Reject(ManifestJson("2.0.2", GoodUrl, GoodSize,
                    GoodSha.Substring(0, 63), null),
                "manifest: a 63-character sha256 is rejected");
            Reject(ManifestJson("2.0.2", GoodUrl, GoodSize,
                    GoodSha + "0", null),
                "manifest: a 65-character sha256 is rejected");
            Reject(ManifestJson("2.0.2", GoodUrl, GoodSize,
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", null),
                "manifest: a non-hex sha256 is rejected");
            Reject(ManifestJson("2.0.2", GoodUrl, "0", GoodSha, null),
                "manifest: a zero size is rejected");
            Reject(ManifestJson("2.0.2", GoodUrl, "200000001", GoodSha, null),
                "manifest: a size over 200 MB is rejected");
            Reject(ManifestJson("2.0.2", "http://api.merrickzq.work/downloads/kobold/Kobold-v2.0.2.zip",
                    GoodSize, GoodSha, null),
                "manifest: an http download URL is rejected");
            Reject(ManifestJson("2.0.2", "https://evil.example.com/downloads/kobold/Kobold-v2.0.2.zip",
                    GoodSize, GoodSha, null),
                "manifest: a download URL on another host is rejected");
            Reject(ManifestJson("2.0.2", "/downloads/kobold/Kobold-v2.0.2.zip", GoodSize, GoodSha, null),
                "manifest: a relative download URL is rejected");
            Reject(ManifestJson("2.0.2", GoodUrl, "\"7511751\"", GoodSha, null),
                "manifest: a quoted size is rejected as the wrong type");
            Reject("{\"version\": 202, \"url\": \"" + GoodUrl + "\", \"size\": 7511751, " +
                   "\"sha256\": \"" + GoodSha + "\"}",
                "manifest: a numeric version is rejected as the wrong type");
            Reject(ManifestJson("2.0.2", GoodUrl, GoodSize, GoodSha,
                    "http://api.merrickzq.work/downloads/kobold/notes/2.0.2"),
                "manifest: an http notes_url is rejected");
        }

        private static void ManifestAcceptsUppercaseSha()
        {
            UpdateManifest manifest;
            string error;
            string upper = GoodSha.ToUpperInvariant();
            Check(UpdateManifest.TryParse(ManifestJson("2.0.2", GoodUrl, GoodSize, upper, null),
                      out manifest, out error) && manifest != null,
                "manifest: an upper-case sha256 is accepted" + (error == null ? "" : " (" + error + ")"));
            Check(manifest != null && manifest.Sha256 == GoodSha,
                "manifest: a sha256 is normalized to lower case");
        }

        // ---------- version ----------

        private static void VersionOrdering()
        {
            Check(UpdateVersion.IsNewer("2.0.9", "2.0.10"),
                "version: 2.0.10 is newer than 2.0.9 (numeric, not string, order)");
            Check(!UpdateVersion.IsNewer("2.0.10", "2.0.9"),
                "version: 2.0.9 is not newer than 2.0.10");
            Check(!UpdateVersion.IsNewer("2.0.1", "2.0.1.0"),
                "version: 2.0.1 is not newer than 2.0.1.0 (absent part is zero)");
            Check(!UpdateVersion.IsNewer("2.0.1.0", "2.0.1"),
                "version: 2.0.1.0 is not newer than 2.0.1");
            Check(UpdateVersion.IsNewer("2.0.1.0", "2.0.2"),
                "version: 2.0.2 is newer than 2.0.1.0");
            Check(!UpdateVersion.IsNewer("2.1.0", "2.0.9"),
                "version: an older remote version is not newer");
            Check(!UpdateVersion.IsNewer("garbage", "2.0.2"),
                "version: an invalid local version never looks older");
            Check(!UpdateVersion.IsNewer("2.0.1", "garbage"),
                "version: an invalid remote version never looks newer");

            Version parsed;
            Check(UpdateVersion.TryParse("2.0.1", out parsed) && parsed == new Version(2, 0, 1),
                "version: TryParse accepts three parts");
            Check(UpdateVersion.TryParse("2.0.1.0", out parsed) && parsed == new Version(2, 0, 1, 0),
                "version: TryParse accepts four parts");
            Check(UpdateVersion.TryParse("2.0.1 \r\n", out parsed) && parsed == new Version(2, 0, 1),
                "version: TryParse allows trailing whitespace");
            Check(!UpdateVersion.TryParse("2.0.1.0.0", out parsed),
                "version: TryParse rejects more than four parts");
            Check(!UpdateVersion.TryParse("", out parsed) && !UpdateVersion.TryParse(null, out parsed),
                "version: TryParse rejects empty and null text");
        }

        // ---------- policy ----------

        private static void OfferPolicy()
        {
            var manifest = new UpdateManifest { Version = "2.0.2" };

            Check(UpdatePolicy.ShouldOffer(manifest, "2.0.1", ""),
                "offer: a strictly newer release is offered");
            Check(!UpdatePolicy.ShouldOffer(manifest, "2.0.2", ""),
                "offer: the same release is not offered");
            Check(!UpdatePolicy.ShouldOffer(manifest, "2.0.3", ""),
                "offer: an older remote release is not offered");
            Check(!UpdatePolicy.ShouldOffer(manifest, "2.0.1", "2.0.2"),
                "offer: the skipped version is not offered again");
            Check(!UpdatePolicy.ShouldOffer(manifest, "2.0.1", "2.0.2.0"),
                "offer: skipping an equivalent version also hides it");
            Check(UpdatePolicy.ShouldOffer(manifest, "2.0.1", "2.0.1"),
                "offer: skipping another version does not hide the new one");
            Check(!UpdatePolicy.ShouldOffer(null, "2.0.1", ""),
                "offer: a missing manifest is not offered");
        }

        private static void CheckPolicy()
        {
            DateTime now = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
            TimeSpan interval = UpdatePolicy.DefaultCheckInterval;

            Check(interval == TimeSpan.FromHours(24),
                "check: the default interval is 24 hours");
            Check(!UpdatePolicy.ShouldCheck(false, null, now, interval),
                "check: a disabled update check never runs");
            Check(UpdatePolicy.ShouldCheck(true, null, now, interval),
                "check: a never-checked install is due immediately");
            Check(UpdatePolicy.ShouldCheck(true, "", now, interval),
                "check: an empty timestamp counts as never checked");
            Check(!UpdatePolicy.ShouldCheck(true, now.AddHours(-1).ToString("o"), now, interval),
                "check: a check one hour ago is too soon");
            Check(UpdatePolicy.ShouldCheck(true, now.AddHours(-25).ToString("o"), now, interval),
                "check: a check 25 hours ago is due");
            Check(UpdatePolicy.ShouldCheck(true, now.AddHours(-24).ToString("o"), now, interval),
                "check: a check exactly 24 hours ago is due");
            Check(UpdatePolicy.ShouldCheck(true, "not a timestamp", now, interval),
                "check: a garbage timestamp counts as never checked");
        }

        // ---------- helpers ----------

        /// <summary>
        /// Builds a manifest document from its raw JSON field values. A null
        /// field is left out entirely (so "missing" can be tested); the caller
        /// passes the exact JSON token for the rest, e.g. "0" for a number and
        /// "\"0\"" for a string.
        /// </summary>
        private static string ManifestJson(string version, string url, string size, string sha256, string notesUrl)
        {
            var fields = new List<string>();
            if (version != null) fields.Add("\"version\": \"" + version + "\"");
            if (url != null) fields.Add("\"url\": \"" + url + "\"");
            if (size != null) fields.Add("\"size\": " + size);
            if (sha256 != null) fields.Add("\"sha256\": \"" + sha256 + "\"");
            if (notesUrl != null) fields.Add("\"notes_url\": \"" + notesUrl + "\"");
            return "{ " + string.Join(", ", fields) + " }";
        }

        private static string GoodJson()
        {
            return ManifestJson("2.0.2", GoodUrl, GoodSize, GoodSha, GoodNotesUrl);
        }

        private static void Reject(string json, string what)
        {
            UpdateManifest manifest;
            string error;
            bool ok = UpdateManifest.TryParse(json, out manifest, out error);
            Check(!ok, what);
            Check(ok || !string.IsNullOrEmpty(error), what + " - with an English error");
        }

        // ---------- downloader: extract ----------

        private static void ExtractChecks()
        {
            string root = NewTempDirectory("extract");
            try
            {
                string entryAssembly = Assembly.GetEntryAssembly() == null
                    ? typeof(Program).Assembly.Location
                    : Assembly.GetEntryAssembly().Location;
                string sourceExe = Path.Combine(root, "source", "Kobold.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(sourceExe));
                File.Copy(entryAssembly, sourceExe, true);

                string installedVersion;
                bool haveInstalledVersion = UpdateDownloader.TryGetInstalledVersion(sourceExe, out installedVersion);
                Check(haveInstalledVersion,
                    "extract: the test process exe exposes a readable FileVersion" +
                    (haveInstalledVersion ? " (" + installedVersion + ")" : ""));
                if (!haveInstalledVersion)
                {
                    Check(false, "extract: the extraction checks need a readable test exe version");
                    return;
                }

                byte[] exeBytes = File.ReadAllBytes(sourceExe);
                byte[] textBytes = Encoding.UTF8.GetBytes("not a real package payload");

                string goodZip = Path.Combine(root, "good.zip");
                CreateZip(goodZip, ZipEntry("Kobold.exe", exeBytes), ZipEntry("lib/other.txt", textBytes));
                string goodStage = Path.Combine(root, "stage-good");
                string extracted = UpdateDownloader.Extract(goodZip, goodStage, installedVersion);
                Check(extracted == Path.GetFullPath(goodStage),
                    "extract: a normal package returns the absolute stage directory");
                Check(File.Exists(Path.Combine(extracted, "Kobold.exe")),
                    "extract: a normal package unpacks Kobold.exe");
                Check(File.Exists(Path.Combine(extracted, "lib", "other.txt")),
                    "extract: a normal package unpacks nested entries");

                string bombZip = Path.Combine(root, "bomb.zip");
                CreateZipDeclaringTooMuch(bombZip, 300000000);
                string bombStage = Path.Combine(root, "stage-bomb");
                ExpectUpdateException(
                    () => UpdateDownloader.Extract(bombZip, bombStage, installedVersion),
                    "size mismatch",
                    "extract: an archive declaring more than the 200 MB cap is rejected before unpacking");
                Check(!Directory.Exists(bombStage),
                    "extract: the stage directory is removed after an oversized package");

                string unsafeZip = Path.Combine(root, "unsafe.zip");
                CreateZip(unsafeZip, ZipEntry("..\\evil.txt", textBytes));
                string unsafeStage = Path.Combine(root, "stage-unsafe");
                ExpectUpdateException(
                    () => UpdateDownloader.Extract(unsafeZip, unsafeStage, installedVersion),
                    "unsafe entry",
                    "extract: a ..\\evil.txt entry is rejected as unsafe");
                Check(!Directory.Exists(unsafeStage),
                    "extract: the stage directory is removed after an unsafe entry");
                Check(!File.Exists(Path.Combine(root, "evil.txt")),
                    "extract: an unsafe entry writes nothing outside the stage directory");

                string missingExeZip = Path.Combine(root, "missing-exe.zip");
                CreateZip(missingExeZip, ZipEntry("readme.txt", textBytes));
                string missingExeStage = Path.Combine(root, "stage-missing-exe");
                ExpectUpdateException(
                    () => UpdateDownloader.Extract(missingExeZip, missingExeStage, installedVersion),
                    "version mismatch",
                    "extract: a package without Kobold.exe is rejected");
                Check(!Directory.Exists(missingExeStage),
                    "extract: the stage directory is removed after a missing exe");

                string wrongVersionZip = Path.Combine(root, "wrong-version.zip");
                CreateZip(wrongVersionZip, ZipEntry("Kobold.exe", exeBytes));
                string wrongVersionStage = Path.Combine(root, "stage-wrong-version");
                ExpectUpdateException(
                    () => UpdateDownloader.Extract(wrongVersionZip, wrongVersionStage, "9.9.9"),
                    "version mismatch",
                    "extract: an exe whose FileVersion differs from the manifest is rejected");
                Check(!Directory.Exists(wrongVersionStage),
                    "extract: the stage directory is removed after a version mismatch");

                string absentVersion;
                Check(!UpdateDownloader.TryGetInstalledVersion(Path.Combine(root, "missing.exe"), out absentVersion),
                    "installer: a missing exe has no readable version");
                string nullVersion;
                Check(!UpdateDownloader.TryGetInstalledVersion(null, out nullVersion),
                    "installer: a null exe path has no readable version");
            }
            finally
            {
                DeleteQuietly(root);
            }
        }

        // ---------- downloader: download ----------

        private static void DownloadChecks()
        {
            string root = NewTempDirectory("download");
            try
            {
                byte[] body = Encoding.UTF8.GetBytes("Kobold package payload - 0123456789 - abcdef");
                string sha = Sha256Hex(body);

                using (var server = new LocalHttpServer(body, body.Length))
                {
                    string destination = Path.Combine(root, "good", "Kobold-v2.0.2.zip");
                    var progress = new RecordingProgress();
                    UpdateDownloader.DownloadAsync(DownloadManifest(server.Url, body.Length, sha),
                        destination, progress, CancellationToken.None).GetAwaiter().GetResult();

                    Check(File.Exists(destination),
                        "download: a valid package lands at the destination");
                    Check(File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(body),
                        "download: the downloaded bytes match the served bytes");
                    Check(!File.Exists(destination + ".part"),
                        "download: the .part file is gone after a successful download");
                    Check(progress.Reports > 0,
                        "download: progress is reported while downloading");
                    Check(progress.Last == body.Length,
                        "download: progress reports the cumulative byte count (last " + progress.Last + ")");
                    Check(server.Requests == 1,
                        "download: the package is requested exactly once");
                    Check(server.UserAgent == "Kobold/" + TestedAssemblyVersion(),
                        "download: the request carries User-Agent Kobold/<current version> (got " +
                        (server.UserAgent ?? "<none>") + ")");
                }

                using (var server = new LocalHttpServer(body, body.Length))
                {
                    string destination = Path.Combine(root, "bad-sha", "Kobold-v2.0.2.zip");
                    ExpectUpdateException(
                        () => UpdateDownloader.DownloadAsync(
                            DownloadManifest(server.Url, body.Length, new string('0', 64)),
                            destination, null, CancellationToken.None).GetAwaiter().GetResult(),
                        "checksum mismatch",
                        "download: a wrong sha256 is rejected with checksum mismatch");
                    Check(!File.Exists(destination + ".part"),
                        "download: the .part file is removed after a checksum mismatch");
                    Check(!File.Exists(destination),
                        "download: nothing is published after a checksum mismatch");
                }

                using (var server = new LocalHttpServer(body, body.Length - 1))
                {
                    string destination = Path.Combine(root, "short", "Kobold-v2.0.2.zip");
                    ExpectUpdateException(
                        () => UpdateDownloader.DownloadAsync(
                            DownloadManifest(server.Url, body.Length, sha),
                            destination, null, CancellationToken.None).GetAwaiter().GetResult(),
                        "size mismatch",
                        "download: a body shorter than manifest.Size is rejected with size mismatch");
                    Check(!File.Exists(destination + ".part"),
                        "download: the .part file is removed after a short body");
                    Check(!File.Exists(destination),
                        "download: nothing is published after a short body");
                }

                using (var server = new LocalHttpServer(body, body.Length))
                {
                    string destination = Path.Combine(root, "long", "Kobold-v2.0.2.zip");
                    ExpectUpdateException(
                        () => UpdateDownloader.DownloadAsync(
                            DownloadManifest(server.Url, body.Length - 1, sha),
                            destination, null, CancellationToken.None).GetAwaiter().GetResult(),
                        "size mismatch",
                        "download: a body longer than manifest.Size is rejected with size mismatch");
                    Check(!File.Exists(destination + ".part"),
                        "download: the .part file is removed after an oversized body");
                    Check(!File.Exists(destination),
                        "download: nothing is published after an oversized body");
                }
            }
            finally
            {
                DeleteQuietly(root);
            }
        }

        // ---------- downloader helpers ----------

        private static UpdateManifest DownloadManifest(string url, long size, string sha256)
        {
            return new UpdateManifest
            {
                Version = "2.0.2",
                Url = url,
                Size = size,
                Sha256 = sha256
            };
        }

        /// <summary>The three-part version of the Kobold assembly under test.</summary>
        private static string TestedAssemblyVersion()
        {
            Version version = typeof(UpdateDownloader).Assembly.GetName().Version;
            return version.Major + "." + version.Minor + "." + Math.Max(version.Build, 0);
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
        }

        private static void CreateZip(string zipPath, params KeyValuePair<string, byte[]>[] entries)
        {
            using (var file = File.Create(zipPath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var entry in entries)
                {
                    ZipArchiveEntry zipEntry = archive.CreateEntry(entry.Key, CompressionLevel.Fastest);
                    using (var stream = zipEntry.Open())
                    {
                        stream.Write(entry.Value, 0, entry.Value.Length);
                    }
                }
            }
        }

        private static KeyValuePair<string, byte[]> ZipEntry(string name, byte[] bytes)
        {
            return new KeyValuePair<string, byte[]>(name, bytes);
        }

        /// <summary>
        /// Writes a structurally valid zip whose single stored entry declares
        /// <paramref name="declaredLength"/> uncompressed bytes but carries no
        /// data. The downloader must reject it from the declared size alone,
        /// before touching any entry; building a real 200 MB+ archive in the
        /// check suite would be slow and prove the same thing.
        /// </summary>
        private static void CreateZipDeclaringTooMuch(string zipPath, uint declaredLength)
        {
            byte[] name = Encoding.ASCII.GetBytes("bomb.txt");
            using (var stream = File.Create(zipPath))
            {
                // Local file header (30 bytes + name).
                WriteUInt32(stream, 0x04034b50);
                WriteUInt16(stream, 20);              // version needed
                WriteUInt16(stream, 0);               // flags
                WriteUInt16(stream, 0);               // method: stored
                WriteUInt16(stream, 0);               // modification time
                WriteUInt16(stream, 0);               // modification date
                WriteUInt32(stream, 0);               // crc32
                WriteUInt32(stream, 0);               // compressed size
                WriteUInt32(stream, declaredLength);  // uncompressed size
                WriteUInt16(stream, (ushort)name.Length);
                WriteUInt16(stream, 0);               // extra field length
                stream.Write(name, 0, name.Length);
                uint centralOffset = (uint)stream.Position;

                // Central directory header (46 bytes + name).
                WriteUInt32(stream, 0x02014b50);
                WriteUInt16(stream, 20);              // version made by
                WriteUInt16(stream, 20);              // version needed
                WriteUInt16(stream, 0);               // flags
                WriteUInt16(stream, 0);               // method: stored
                WriteUInt16(stream, 0);               // modification time
                WriteUInt16(stream, 0);               // modification date
                WriteUInt32(stream, 0);               // crc32
                WriteUInt32(stream, 0);               // compressed size
                WriteUInt32(stream, declaredLength);  // uncompressed size
                WriteUInt16(stream, (ushort)name.Length);
                WriteUInt16(stream, 0);               // extra field length
                WriteUInt16(stream, 0);               // comment length
                WriteUInt16(stream, 0);               // disk number
                WriteUInt16(stream, 0);               // internal attributes
                WriteUInt32(stream, 0);               // external attributes
                WriteUInt32(stream, 0);               // local header offset
                stream.Write(name, 0, name.Length);
                uint centralSize = (uint)stream.Position - centralOffset;

                // End of central directory record (22 bytes).
                WriteUInt32(stream, 0x06054b50);
                WriteUInt16(stream, 0);               // this disk
                WriteUInt16(stream, 0);               // central directory disk
                WriteUInt16(stream, 1);               // entries on this disk
                WriteUInt16(stream, 1);               // total entries
                WriteUInt32(stream, centralSize);
                WriteUInt32(stream, centralOffset);
                WriteUInt16(stream, 0);               // comment length
            }
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        private static string NewTempDirectory(string label)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "KoboldUpdateCheck-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteQuietly(string directory)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void ExpectUpdateException(Action action, string expectedMessage, string what)
        {
            try
            {
                action();
            }
            catch (UpdateException ex)
            {
                Check(ex.Message == expectedMessage,
                    what + (ex.Message == expectedMessage ? "" : " (message was \"" + ex.Message + "\")"));
                return;
            }
            catch (Exception ex)
            {
                Check(false, what + " (threw " + ex.GetType().Name + ": " + ex.Message + ")");
                return;
            }
            Check(false, what + " (no exception was thrown)");
        }

        private sealed class RecordingProgress : IProgress<long>
        {
            public long Last { get; private set; }

            public int Reports { get; private set; }

            public void Report(long value)
            {
                Last = value;
                Reports++;
            }
        }

        /// <summary>
        /// Serves one payload from http://127.0.0.1:&lt;free port&gt;/ on a
        /// background thread. The declared length may be shorter than the
        /// payload so the downloader's size check can be exercised. This is the
        /// only "network" the checks use.
        /// </summary>
        private sealed class LocalHttpServer : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly Thread _thread;
            private readonly byte[] _body;
            private readonly int _declaredLength;
            private volatile string _userAgent;
            private int _requests;

            public LocalHttpServer(byte[] body, int declaredLength)
            {
                _body = body;
                _declaredLength = declaredLength;

                int port = FreeTcpPort();
                Url = "http://127.0.0.1:" + port + "/package.zip";
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                _listener.Start();

                _thread = new Thread(Serve) { IsBackground = true, Name = "UpdateCheckHttp" };
                _thread.Start();
            }

            public string Url { get; private set; }

            public string UserAgent
            {
                get { return _userAgent; }
            }

            public int Requests
            {
                get { return Volatile.Read(ref _requests); }
            }

            public void Dispose()
            {
                try
                {
                    _listener.Stop();
                }
                catch (ObjectDisposedException)
                {
                }
                try
                {
                    _listener.Close();
                }
                catch (ObjectDisposedException)
                {
                }
                _thread.Join(2000);
            }

            private void Serve()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = _listener.GetContext();
                    }
                    catch (HttpListenerException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    _userAgent = context.Request.UserAgent;
                    Interlocked.Increment(ref _requests);
                    try
                    {
                        int length = Math.Min(_declaredLength, _body.Length);
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/octet-stream";
                        context.Response.ContentLength64 = length;
                        context.Response.OutputStream.Write(_body, 0, length);
                        context.Response.OutputStream.Flush();
                    }
                    finally
                    {
                        try
                        {
                            context.Response.Close();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                }
            }

            private static int FreeTcpPort()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                try
                {
                    return ((IPEndPoint)probe.LocalEndpoint).Port;
                }
                finally
                {
                    probe.Stop();
                }
            }
        }
    }
}
