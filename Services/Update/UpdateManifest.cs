using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kobold.Services.Update
{
    /// <summary>
    /// One release advertised by the update manifest. Parsing is deliberately
    /// strict: the download URL must stay on the fixed HTTPS host and the
    /// checksum must be a full SHA-256 string, so a malformed or redirected
    /// manifest can never reach the downloader. Pure logic; no network or disk.
    /// </summary>
    public sealed class UpdateManifest
    {
        /// <summary>The only manifest address the updater ever requests.</summary>
        public const string DefaultManifestUrl = "https://api.merrickzq.work/downloads/kobold/latest.json";

        /// <summary>The only host a manifest or a download URL may use.</summary>
        public const string DefaultHost = "api.merrickzq.work";

        private const long MaxSize = 200000000;
        private const int Sha256Length = 64;

        /// <summary>Release version as published, e.g. "2.0.2".</summary>
        public string Version { get; set; }

        /// <summary>Absolute HTTPS URL of the release zip on <see cref="DefaultHost"/>.</summary>
        public string Url { get; set; }

        /// <summary>Exact package size in bytes.</summary>
        public long Size { get; set; }

        /// <summary>Lower-case hex SHA-256 of the package.</summary>
        public string Sha256 { get; set; }

        /// <summary>Optional HTTPS release-notes page; null when the manifest has none.</summary>
        public string NotesUrl { get; set; }

        /// <summary>
        /// Parses and validates one manifest document. Returns false with an
        /// English error when the JSON is malformed, a required field is
        /// missing or has the wrong type, or any value breaks the fixed rules.
        /// </summary>
        public static bool TryParse(string json, out UpdateManifest manifest, out string error)
        {
            manifest = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "manifest is empty";
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException)
            {
                error = "manifest is not valid JSON";
                return false;
            }

            string version = ReadString(root, "version", out error);
            if (error != null) return false;
            if (CountVersionParts(version) < 3 || !UpdateVersion.TryParse(version, out _))
            {
                error = "version must have at least three parts (for example 2.0.1)";
                return false;
            }

            string url = ReadString(root, "url", out error);
            if (error != null) return false;
            Uri downloadUri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps)
            {
                error = "url must be an absolute https URL";
                return false;
            }
            if (!string.Equals(downloadUri.Host, DefaultHost, StringComparison.OrdinalIgnoreCase))
            {
                error = "url host must be " + DefaultHost;
                return false;
            }

            long size;
            if (!TryReadSize(root, out size, out error)) return false;

            string sha256 = ReadString(root, "sha256", out error);
            if (error != null) return false;
            if (!IsSha256(sha256))
            {
                error = "sha256 must be 64 hexadecimal characters";
                return false;
            }

            string notesUrl = null;
            JToken notesToken = root["notes_url"];
            if (notesToken != null && notesToken.Type != JTokenType.Null)
            {
                if (notesToken.Type != JTokenType.String)
                {
                    error = "notes_url must be a string";
                    return false;
                }
                string notesText = ((string)notesToken).Trim();
                if (notesText.Length > 0)
                {
                    Uri notesUri;
                    if (!Uri.TryCreate(notesText, UriKind.Absolute, out notesUri) ||
                        notesUri.Scheme != Uri.UriSchemeHttps)
                    {
                        error = "notes_url must be an https URL";
                        return false;
                    }
                    notesUrl = notesText;
                }
            }

            manifest = new UpdateManifest
            {
                Version = version,
                Url = url,
                Size = size,
                Sha256 = sha256.ToLowerInvariant(),
                NotesUrl = notesUrl
            };
            return true;
        }

        private static string ReadString(JObject root, string name, out string error)
        {
            error = null;
            JToken token = root[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = name + " is missing";
                return null;
            }
            if (token.Type != JTokenType.String)
            {
                error = name + " must be a string";
                return null;
            }
            string text = ((string)token).Trim();
            if (text.Length == 0)
            {
                error = name + " is empty";
                return null;
            }
            return text;
        }

        private static bool TryReadSize(JObject root, out long size, out string error)
        {
            size = 0;
            error = null;
            JToken token = root["size"];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = "size is missing";
                return false;
            }
            // Integral JSON numbers arrive as JTokenType.Integer; a quoted or
            // fractional value is a type error, not a value error.
            if (token.Type != JTokenType.Integer)
            {
                error = "size must be an integer";
                return false;
            }
            if (!long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) ||
                size < 1 || size > MaxSize)
            {
                error = "size must be between 1 and " + MaxSize + " bytes";
                return false;
            }
            return true;
        }

        private static bool IsSha256(string text)
        {
            if (text == null || text.Length != Sha256Length) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        private static int CountVersionParts(string version)
        {
            int parts = 1;
            for (int i = 0; i < version.Length; i++)
            {
                if (version[i] == '.') parts++;
            }
            return parts;
        }
    }
}
