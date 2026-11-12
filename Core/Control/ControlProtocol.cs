using System;
using System.Globalization;
using System.Security.Principal;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    public static class ControlProtocol
    {
        public const int Version = 1;
    }

    public sealed class ControlRequest
    {
        public int V { get; set; }
        public string Id { get; set; }
        public string Method { get; set; }
        public JObject Params { get; set; }
        public string BaseRevision { get; set; }
    }

    public sealed class ControlResponse
    {
        public int V { get; set; }
        public string Id { get; set; }
        public bool Ok { get; set; }
        public JToken Result { get; set; }
        public ControlError Error { get; set; }
        public string Revision { get; set; }

        /// <summary>
        /// Work the server runs only after the response line has been written.
        /// A restart response uses it to shut the app down, so the CLI always
        /// receives its reply first. It is never part of the wire protocol.
        /// </summary>
        [JsonIgnore]
        public Action AfterWrite { get; set; }
    }

    public sealed class ControlError
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public JObject Details { get; set; }
    }

    public static class ControlErrorCodes
    {
        public const string InvalidRequest = "invalid_request";
        public const string UnsupportedProtocol = "unsupported_protocol";
        public const string UnknownMethod = "unknown_method";
        public const string ValidationFailed = "validation_failed";
        public const string Conflict = "conflict";
        public const string RevisionRequired = "revision_required";
        public const string RestartRequired = "restart_required";
        public const string AppNotRunning = "app_not_running";
        public const string ControlUnavailable = "control_unavailable";
        public const string Forbidden = "forbidden";
        public const string Internal = "internal";
        public const string RestartFailed = "restart_failed";
    }

    public sealed class ControlException : Exception
    {
        public string Code { get; private set; }
        public JObject Details { get; private set; }

        public ControlException(string code, string message, JObject details = null)
            : base(message)
        {
            Code = code;
            Details = details;
        }

        public static ControlException RevisionRequired()
        {
            return new ControlException(ControlErrorCodes.RevisionRequired,
                "A baseRevision is required for this write.");
        }

        public static ControlException Conflict(string currentRevision)
        {
            return new ControlException(ControlErrorCodes.Conflict,
                "Config changed since the supplied revision.",
                new JObject { ["currentRevision"] = currentRevision });
        }

        public static ControlException Forbidden(string message)
        {
            return new ControlException(ControlErrorCodes.Forbidden, message);
        }

        public static ControlException Validation(string message)
        {
            return new ControlException(ControlErrorCodes.ValidationFailed, message);
        }

        public static ControlException Validation(System.Collections.Generic.IEnumerable<string> errors)
        {
            return new ControlException(ControlErrorCodes.ValidationFailed,
                "Config validation failed.",
                new JObject { ["errors"] = new JArray(errors ?? new string[0]) });
        }
    }

    public static class ControlPipeName
    {
        public static string For(string userSid, int sessionId)
        {
            if (string.IsNullOrWhiteSpace(userSid))
                throw new ArgumentException("A user SID is required.", "userSid");
            return "Kobold.Control.v1." + userSid + "."
                + sessionId.ToString(CultureInfo.InvariantCulture);
        }

        public static string ForCurrentUser()
        {
            string sid = WindowsIdentity.GetCurrent().User.Value;
            int session = Process.GetCurrentProcess().SessionId;
            return For(sid, session);
        }
    }

    public static class ControlJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            Culture = CultureInfo.InvariantCulture,
            DateParseHandling = DateParseHandling.DateTime
        };
    }
}
