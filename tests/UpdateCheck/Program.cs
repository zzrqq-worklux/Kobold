using System;
using System.Collections.Generic;
using System.Text;
using Kobold.Services.Update;

namespace Kobold.UpdateCheck
{
    /// <summary>
    /// Behaviour checks for the updater's pure logic: manifest parsing and
    /// validation, version ordering, and the offer/check policies. No network,
    /// no registry, no files. Exit code 0 = all green; 1 = failures.
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
    }
}
