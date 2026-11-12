# Kobold Agent Control Plane + `kobold-config` Skill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a local, current-user-only control plane to Kobold so an agent can safely inspect/patch config, add/update/delete widgets and groups, control the file-search service, and read diagnostics through a portable `koboldctl.ps1` + `kobold-config` skill.

**Architecture:** `Kobold.exe` hosts a named-pipe JSON server (`\\.\pipe\Kobold.Control.v1.<sid>.<session>`). A PowerShell CLI sends one request per connection; all mutations run on the WPF dispatcher, are revision-checked, backed up, and audited to `%LocalAppData%\Kobold\logs\app.log`. A portable `skill/kobold-config/` teaches any agent how to use the CLI; no agent-specific install paths.

**Tech Stack:** .NET Framework 4.8, C# 7.3, WPF, Newtonsoft.Json 13, PowerShell 5.1, existing `tests/*Check` console projects + `tools/Run-Checks.ps1`.

**Spec:** `docs/superpowers/specs/2026-11-10-kobold-agent-control-design.md`

## Global Constraints

- Do **not** commit git unless the user explicitly asks. Checkpoints are build/check output, not commits.
- Do not reset/stash/delete the existing uncommitted P2 tree; HEAD is `aa1efbc`.
- Do not start or stop the user's running Kobold unless the task explicitly says deploy/manual restart and the user approved it.
- Keep the existing checks green: `Run-Checks -Group ci`, `Run-Checks -Group checks` (isolated `APPDATA`), `GroupUiSpike -- --check`.
- Run UI/config checks with a fresh isolated `APPDATA`; never touch the real `%APPDATA%\Kobold\config.json`.
- Do not edit `Core/FolderColor.cs`, `Helpers/FolderIconSource.cs`, `tests/FolderColorCheck/Program.cs`.
- .NET Framework 4.8 / C# 7.3 only; no new NuGet dependencies.
- Control pipe is local-only, current-user ACL, method allowlist; no HTTP, no arbitrary shell, no arbitrary path writes.
- All config writes require `baseRevision`; all destructive commands require `confirm`; backups before replace/restore.
- Spec decisions: 2C (no `app.showSettings`), 3B (app-internal restart helper), 4B (immediate startup/Quick Access), 5A (all folder states), desktop icon guard on exit.
- Skill installation is generic: `skill/kobold-config/README.md` tells the user/agent to copy the folder into its own skills directory. No agent-specific paths or installer.

---

### Task 1: Control protocol, pipe name, and `ControlCheck`

**Files:**
- Create: `Core/Control/ControlProtocol.cs`
- Create: `tests/ControlCheck/ControlCheck.csproj`
- Create: `tests/ControlCheck/Program.cs`
- Modify: `AssemblyInfo.cs` (`InternalsVisibleTo("ControlCheck")` if needed)
- Modify: `tools/Run-Checks.ps1` (add `ControlCheck` with `Ci = $true`)

**Interfaces:**
- Produces:
  - `ControlProtocol.Version = 1`
  - `ControlRequest { int V; string Id; string Method; JObject Params; string BaseRevision; }`
  - `ControlResponse { int V; string Id; bool Ok; JToken Result; ControlError Error; string Revision; }`
  - `ControlError { string Code; string Message; JObject Details; }`
  - `ControlErrorCodes` constants: `InvalidRequest`, `UnsupportedProtocol`, `UnknownMethod`, `ValidationFailed`, `Conflict`, `RevisionRequired`, `RestartRequired`, `AppNotRunning`, `ControlUnavailable`, `Forbidden`, `Internal`, `RestartFailed`.
  - `ControlPipeName.ForCurrentUser()` and `ControlPipeName.For(string userSid, int sessionId)`.
  - `ControlJson.Settings` (Newtonsoft settings; `NullValueHandling.Ignore`, `Formatting.None`, invariant culture).
- Produces for later tasks: request/response shape and pipe naming used by `ControlServer` and `koboldctl.ps1`.

- [ ] **Step 1: Write the failing protocol tests**

In `tests/ControlCheck/Program.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using Kobold.Core.Control;
using Kobold.Services.Control;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kobold.ControlCheck
{
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Check(ControlProtocol.Version == 1, "protocol: version is 1");
            Check(ControlPipeName.For("S-1-5-21-1", 3)
                  == @"Kobold.Control.v1.S-1-5-21-1.3",
                "protocol: pipe name carries sid and session");
            string currentPipe = ControlPipeName.ForCurrentUser();
            Check(currentPipe.StartsWith(@"Kobold.Control.v1.S-1-", StringComparison.Ordinal)
                  && currentPipe.Split('.').Length >= 4,
                "protocol: current-user pipe name carries a SID and session");

            var request = new ControlRequest
            {
                V = 1,
                Id = "req-1",
                Method = "config.patch",
                Params = JObject.Parse(@"{""Theme"":""light""}"),
                BaseRevision = "sha256:abc"
            };
            string json = JsonConvert.SerializeObject(request, ControlJson.Settings);
            var round = JsonConvert.DeserializeObject<ControlRequest>(json, ControlJson.Settings);
            Check(round != null && round.Method == "config.patch"
                  && (string)round.Params["Theme"] == "light"
                  && round.BaseRevision == "sha256:abc",
                "protocol: request round-trips");
            Check(!json.Contains("\"Error\""), "protocol: null response fields are omitted");

            var response = new ControlResponse
            {
                V = 1,
                Id = "req-1",
                Ok = false,
                Error = new ControlError
                {
                    Code = ControlErrorCodes.Conflict,
                    Message = "revision mismatch",
                    Details = JObject.Parse(@"{""currentRevision"":""sha256:def""}")
                }
            };
            string responseJson = JsonConvert.SerializeObject(response, ControlJson.Settings);
            var responseRound = JsonConvert.DeserializeObject<ControlResponse>(
                responseJson, ControlJson.Settings);
            Check(responseRound != null && !responseRound.Ok
                  && responseRound.Error.Code == ControlErrorCodes.Conflict,
                "protocol: error response round-trips");

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL CONTROL CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (string error in Errors) Console.WriteLine("  - " + error);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }
    }
}
```

`tests/ControlCheck/ControlCheck.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Kobold.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Run it and verify red**

Run:
```powershell
$env:APPDATA = Join-Path $env:TEMP ("kobold-control-red-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $env:APPDATA | Out-Null
dotnet run --project tests\ControlCheck
```
Expected: compile error `ControlProtocol` / `ControlPipeName` / `ControlJson` not found.

- [ ] **Step 3: Implement `Core/Control/ControlProtocol.cs`**

```csharp
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
```

- [ ] **Step 4: Register `ControlCheck` in `tools/Run-Checks.ps1`**

Add to the `$checks` array after `ConfigSafetyCheck`:

```powershell
@{ Name = 'ControlCheck';          Ci = $true  }
```

- [ ] **Step 5: Run green**

```powershell
$env:APPDATA = Join-Path $env:TEMP ("kobold-control-green-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $env:APPDATA | Out-Null
dotnet build Kobold.csproj -c Debug
dotnet run --project tests\ControlCheck
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group ci
```
Expected: ControlCheck `ALL CONTROL CHECKS PASSED`; CI `28/28` (27 existing + ControlCheck).

---

### Task 2: `AppDiagnostics`, `ControlServer`, and basic `diag.snapshot`

**Files:**
- Create: `Core/AppDiagnostics.cs`
- Create: `Services/Control/ControlServer.cs`
- Create: `Services/Control/ControlRequestRouter.cs`
- Create: `Core/Control/DiagnosticsService.cs`
- Modify: `App.xaml.cs` (start/stop server)
- Modify: `tests/ControlCheck/Program.cs` (AppDiagnostics rotation + pipe server round-trip)

**Interfaces:**
- Produces:
  - `AppDiagnostics.LogPath` (internal test seam), `AppDiagnostics.Write(string)`, rotation at 1 MB to `.1`.
  - `ControlServer(string pipeName, Action<Action> dispatch, Func<ControlRequest, ControlResponse> handler)` with `Start()` / `Stop()`.
  - `DiagnosticsService.Snapshot()` returns `JObject` with `app`, `config`, `search`, `logs`.
- Consumes: `ControlProtocol`, `ControlPipeName`, `WidgetManager.Instance`, `FileSearchIndexService.Instance`.

- [ ] **Step 1: Write failing `AppDiagnostics` and pipe tests**

Add to `tests/ControlCheck/Program.cs`:

```csharp
private static void AppDiagnosticsRotates()
{
    string dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "kobold-appdiag-" + Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(dir);
    string path = System.IO.Path.Combine(dir, "app.log");
    string previous = AppDiagnostics.LogPath;
    try
    {
        AppDiagnostics.LogPath = path;
        AppDiagnostics.MaxBytes = 256;
        AppDiagnostics.Write("first line");
        AppDiagnostics.Write(new string('x', 300));
        Check(System.IO.File.Exists(path), "app diagnostics: main log exists");
        Check(System.IO.File.Exists(path + ".1"), "app diagnostics: oversized log rotates");
    }
    finally
    {
        AppDiagnostics.LogPath = previous;
        AppDiagnostics.MaxBytes = 1024 * 1024;
        try { System.IO.Directory.Delete(dir, true); } catch { }
    }
}

private static void ControlServerRoundTrips()
{
    string pipe = "Kobold.ControlCheck." + Guid.NewGuid().ToString("N");
    var server = new ControlServer(pipe, action => action(), request =>
    {
        if (request.Method != "diag.ping")
            return new ControlResponse
            {
                V = 1, Id = request.Id, Ok = false,
                Error = new ControlError { Code = ControlErrorCodes.UnknownMethod }
            };
        return new ControlResponse
        {
            V = 1, Id = request.Id, Ok = true,
            Result = new JObject { ["pong"] = true }
        };
    });
    try
    {
        server.Start();
        using (var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut))
        {
            client.Connect(3000);
            using (var writer = new System.IO.StreamWriter(client) { AutoFlush = true })
            using (var reader = new System.IO.StreamReader(client))
            {
                writer.WriteLine(JsonConvert.SerializeObject(new ControlRequest
                {
                    V = 1, Id = "p1", Method = "diag.ping"
                }, ControlJson.Settings));
                var response = JsonConvert.DeserializeObject<ControlResponse>(
                    reader.ReadLine(), ControlJson.Settings);
                Check(response != null && response.Ok
                      && (bool)response.Result["pong"],
                    "control server: request/response round-trip");
            }
        }
    }
    finally
    {
        server.Stop();
    }
}
```

Register both in `Main`.

- [ ] **Step 2: Run red**

`dotnet run --project tests\ControlCheck` → compile errors for `AppDiagnostics` / `ControlServer`.

- [ ] **Step 3: Implement `Core/AppDiagnostics.cs`**

Copy the `SearchDiagnostics` pattern, path `%LocalAppData%\Kobold\logs\app.log`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Kobold.Core
{
    public static class AppDiagnostics
    {
        private const long DefaultMaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();
        private static string _path;
        private static long _maxBytes = DefaultMaxBytes;

        internal static string LogPath
        {
            get { lock (Gate) return _path ?? DefaultLogPath(); }
            set { lock (Gate) _path = value; }
        }

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
            catch { }
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
            catch { }
        }

        private static string DefaultLogPath()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
            return Path.Combine(root, "Kobold", "logs", "app.log");
        }
    }
}
```

- [ ] **Step 4: Implement `Services/Control/ControlServer.cs`**

```csharp
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Kobold.Core;
using Kobold.Core.Control;
using Newtonsoft.Json;

namespace Kobold.Services.Control
{
    public sealed class ControlServer : IDisposable
    {
        private const int MaxMessageBytes = 1024 * 1024;
        private readonly string _pipeName;
        private readonly Action<Action> _dispatch;
        private readonly Func<ControlRequest, ControlResponse> _handler;
        private Thread _thread;
        private volatile bool _running;
        private volatile NamedPipeServerStream _current;

        public ControlServer(string pipeName, Action<Action> dispatch,
            Func<ControlRequest, ControlResponse> handler)
        {
            _pipeName = pipeName;
            _dispatch = dispatch ?? (action => action());
            _handler = handler;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Kobold.Control" };
            _thread.Start();
            AppDiagnostics.Write("control server started pipe=" + _pipeName);
        }

        public void Stop()
        {
            _running = false;
            try { _current?.Dispose(); } catch { }
            AppDiagnostics.Write("control server stopped");
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    var security = new PipeSecurity();
                    var user = WindowsIdentity.GetCurrent().User;
                    security.AddAccessRule(new PipeAccessRule(
                        user, PipeAccessRights.FullControl, AccessControlType.Allow));
                    security.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        PipeAccessRights.FullControl, AccessControlType.Allow));

                    using (var server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None,
                        0, 0, security))
                    {
                        _current = server;
                        server.WaitForConnection();
                        Handle(server);
                    }
                }
                catch (Exception ex)
                {
                    if (_running) AppDiagnostics.Write("control pipe error: " + ex.Message);
                }
                finally
                {
                    _current = null;
                }
            }
        }

        private void Handle(NamedPipeServerStream server)
        {
            string line;
            using (var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true))
                line = reader.ReadLine();
            if (line == null || line.Length > MaxMessageBytes) return;

            ControlRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<ControlRequest>(
                    line, ControlJson.Settings);
            }
            catch (Exception)
            {
                Write(server, Error(null, ControlErrorCodes.InvalidRequest, "Invalid JSON."));
                return;
            }

            ControlResponse response;
            try
            {
                // The real app dispatches on the UI thread and blocks this
                // connection until the dispatcher finishes.
                ControlResponse captured = null;
                _dispatch(() => captured = _handler(request));
                response = captured;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("control handler error: " + ex.Message);
                response = Error(request, ControlErrorCodes.Internal, ex.Message);
            }

            if (response == null) response = Error(request, ControlErrorCodes.Internal, "No response.");
            Write(server, response);
        }

        private static ControlResponse Error(ControlRequest request, string code, string message)
        {
            return new ControlResponse
            {
                V = ControlProtocol.Version,
                Id = request == null ? null : request.Id,
                Ok = false,
                Error = new ControlError { Code = code, Message = message }
            };
        }

        private static void Write(NamedPipeServerStream server, ControlResponse response)
        {
            string json = JsonConvert.SerializeObject(response, ControlJson.Settings);
            byte[] bytes = Encoding.UTF8.GetBytes(json + Environment.NewLine);
            server.Write(bytes, 0, bytes.Length);
            server.Flush();
        }

        public void Dispose() { Stop(); }
    }
}
```

- [ ] **Step 5: Implement `Core/Control/DiagnosticsService.cs` (basic)**

```csharp
using System;
using Kobold.Core.FileSearch;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    public sealed class DiagnosticsService
    {
        public JObject Snapshot()
        {
            var config = WidgetManager.Instance.Config;
            var search = FileSearchIndexService.Instance.Status;
            var index = FileSearchIndexService.Instance.ReadStatus();
            return new JObject
            {
                ["app"] = new JObject
                {
                    ["version"] = typeof(DiagnosticsService).Assembly.GetName().Version.ToString(),
                    ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                    ["startedUtc"] = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
                        .ToString("o")
                },
                ["config"] = new JObject
                {
                    ["path"] = Utils.GetConfigPath(),
                    ["backupPath"] = Utils.GetConfigPath() + ".backup",
                    ["schemaVersion"] = config.SchemaVersion,
                    ["widgetCount"] = config.Widgets == null ? 0 : config.Widgets.Count,
                    ["groupCount"] = config.Groups == null ? 0 : config.Groups.Count
                },
                ["search"] = new JObject
                {
                    ["state"] = search.State.ToString(),
                    ["message"] = search.Message,
                    ["filesIndexed"] = search.FilesIndexed,
                    ["needsRebuild"] = search.NeedsRebuild,
                    ["databasePath"] = search.DatabasePath,
                    ["index"] = new JObject
                    {
                        ["hasIndex"] = index.HasIndex,
                        ["lastScanUtc"] = index.LastScanUtc?.ToString("o"),
                        ["lastReconcileUtc"] = index.LastReconcileUtc?.ToString("o")
                    }
                },
                ["logs"] = new JObject
                {
                    ["app"] = AppDiagnostics.LogPath,
                    ["search"] = SearchDiagnostics.LogPath
                }
            };
        }
    }
}
```

`SearchDiagnostics.LogPath` is internal; `DiagnosticsService` is same assembly, okay. `WidgetManager.Instance` may initialize; in tests use XamlLoadCheck, not ControlCheck.

- [ ] **Step 6: Start/stop in `App.xaml.cs`**

Add fields and startup after `WidgetManager.Instance.Initialize()`:

```csharp
private ControlServer _controlServer;

_controlServer = new ControlServer(
    ControlPipeName.ForCurrentUser(),
    action => Dispatcher.Invoke(action),
    request => new ControlRequestRouter().Handle(request));
_controlServer.Start();
```

For this task, `ControlRequestRouter` can be a minimal class in `Services/Control/ControlRequestRouter.cs`:

```csharp
public sealed class ControlRequestRouter
{
    private readonly DiagnosticsService _diagnostics = new DiagnosticsService();
    public ControlResponse Handle(ControlRequest request)
    {
        try
        {
            if (request == null || request.V != ControlProtocol.Version)
                return Error(request, ControlErrorCodes.UnsupportedProtocol, "Protocol mismatch.");
            switch (request.Method)
            {
                case "diag.ping":
                    return Ok(request, new JObject { ["pong"] = true });
                case "diag.snapshot":
                    return Ok(request, _diagnostics.Snapshot());
                default:
                    return Error(request, ControlErrorCodes.UnknownMethod, "Unknown method.");
            }
        }
        catch (ControlException ex)
        {
            return Error(request, ex.Code, ex.Message, ex.Details);
        }
    }
    // Ok/Error helpers set V/Id/Ok/Error/Details.
}
```

Stop in `OnExit` before existing cleanup:

```csharp
try { _controlServer?.Stop(); } catch { }
```

- [ ] **Step 7: Run green**

```powershell
$env:APPDATA = Join-Path $env:TEMP ("kobold-control-task2-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $env:APPDATA | Out-Null
dotnet build Kobold.csproj -c Debug
dotnet run --project tests\ControlCheck
dotnet run --project tests\XamlLoadCheck
```
Expected: ControlCheck all pass; XamlLoadCheck unchanged (snapshot not yet pinned there).

---

### Task 3: Config document, validation, describe, revision

**Files:**
- Create: `Core/Control/ConfigDocument.cs`
- Create: `Core/Control/ConfigDescribe.cs`
- Create: `Core/Control/ConfigRevision.cs`
- Modify: `Core/AppConfig.cs` (expose `RepairForControl`)
- Modify: `tests/ControlCheck/Program.cs`

**Interfaces:**
- Produces:
  - `ConfigDocument.Parse(string json, out List<string> warnings)` returns `AppConfig` or null.
  - `ConfigDocument.Validate(string json, out List<string> warnings)` returns `bool`.
  - `ConfigDocument.ToJson(AppConfig config, bool full)`.
  - `ConfigDescribe.Build()` returns `JObject`.
  - `ConfigRevision.Compute(AppConfig config)` returns `"sha256:<hex>"`.
- Consumes: `AppConfig`, `WidgetDataConverter`, `FileSearchSettings.Normalize`, `WidgetKindRegistry`.

- [ ] **Step 1: Write failing tests**

```csharp
private static void ConfigValidationAndRevision()
{
    string valid = @"{
      ""Language"":""en"", ""Theme"":""dark"",
      ""Widgets"":[
        {""Kind"":""note"",""Id"":""w1"",""Name"":""A""},
        {""Kind"":""todo"",""Id"":""w2"",""Name"":""B""}
      ],
      ""Groups"":[{""Id"":""g1"",""ActiveWidgetId"":""w1""}]
    }";
    var warnings = new List<string>();
    var config = ConfigDocument.Parse(valid, out warnings);
    Check(config != null && config.Widgets.Count == 2,
        "config document: parses widgets");
    Check(config.Groups != null && config.Groups.Count == 1,
        "config document: repairs a one-member group away");
    Check(ConfigDocument.Validate(valid, out warnings),
        "config document: valid document validates");
    Check(!ConfigDocument.Validate("{ \"Widgets\": 5 }", out warnings),
        "config document: a wrong-typed block is rejected");

    var describe = ConfigDescribe.Build();
    Check(describe["topLevel"] is JArray top
          && top.Any(x => (string)x["name"] == "Theme"),
        "config describe: includes Theme");
    Check(describe["widgetKinds"] is JArray kinds
          && kinds.Any(x => (string)x["id"] == "note"),
        "config describe: includes the note kind");

    string r1 = ConfigRevision.Compute(config);
    string r2 = ConfigRevision.Compute(config);
    Check(r1 == r2 && r1.StartsWith("sha256:", StringComparison.Ordinal),
        "config revision: stable and prefixed");
}
```

- [ ] **Step 2: Run red**

`dotnet run --project tests\ControlCheck` → missing `ConfigDocument` / `ConfigDescribe` / `ConfigRevision`.

- [ ] **Step 3: Implement `AppConfig.RepairForControl`**

In `Core/AppConfig.cs`:

```csharp
/// <summary>
/// Runs the same repair/normalization as Load, without touching disk.
/// Used by the control plane to validate a proposed document.
/// </summary>
internal void RepairForControl()
{
    RepairGroups();
    if (FileSearch != null) FileSearch.Normalize();
}
```

`RepairGroups` is private in the same class; callable here.

- [ ] **Step 4: Implement `Core/Control/ConfigDocument.cs`**

```csharp
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    public static class ConfigDocument
    {
        public static AppConfig Parse(string json, out List<string> warnings)
        {
            warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var config = JsonConvert.DeserializeObject<AppConfig>(json);
                if (config == null) return null;
                config.RepairForControl();
                return config;
            }
            catch (Exception ex)
            {
                warnings.Add(ex.Message);
                return null;
            }
        }

        public static bool Validate(string json, out List<string> warnings)
        {
            return Parse(json, out warnings) != null;
        }

        public static string ToJson(AppConfig config, bool full)
        {
            if (config == null) return "{}";
            if (full)
                return JsonConvert.SerializeObject(config, Formatting.Indented);

            var root = JObject.FromObject(config);
            if (root["Widgets"] is JArray widgets)
            {
                foreach (var widget in widgets.OfType<JObject>())
                {
                    widget.Remove("Items");
                    widget.Remove("Notes");
                    widget.Remove("BrowseStack");
                }
            }
            root.Remove("FrequentFiles");
            return root.ToString(Formatting.Indented);
        }
    }
}
```

- [ ] **Step 5: Implement `ConfigDescribe` and `ConfigRevision`**

`ConfigDescribe` reflects `AppConfig` public properties, `WidgetGroup`, `FileSearchSettings`, and `WidgetKindRegistry.All` fields. Use `JObject` with `name`, `type`, `allowed` for known enums. `ConfigRevision` serializes `config` with `Formatting.None`, computes SHA-256 over UTF-8, returns `"sha256:" + hex`.

- [ ] **Step 6: Run green**

`dotnet run --project tests\ControlCheck`; expected all pass.

---

### Task 4: `config.get`, `config.patch`, backup, revision guard

**Files:**
- Create: `Core/Control/ConfigControlService.cs`
- Modify: `Core/Control/ControlRequestRouter.cs` (route `config.*`)
- Modify: `tests/ControlCheck/Program.cs`

**Interfaces:**
- Produces:
  - `ConfigControlService.Get(JObject p)` → `JObject` with `config`, `revision`, `schemaVersion`, `path`.
  - `ConfigControlService.Patch(JObject p, string baseRevision)` → `JObject` with `wouldChange`, `changes`, `warnings`, `restartRequired`, `revision`.
  - `ConfigControlService.Backup(string label)` → path.
  - `ConfigControlService.CurrentRevision` property.
- Consumes: `ConfigDocument`, `ConfigRevision`, `WidgetManager.Instance.Config`, `WidgetManager.Instance.SaveConfigNow`.

- [ ] **Step 1: Write failing tests for patch/revision/backup**

Use a temp config sandbox? `ControlCheck` should not touch `WidgetManager.Instance`; instead make `ConfigControlService` accept a `Func<AppConfig>` and `Func<AppConfig, JObject>` + paths. Signature:

```csharp
public ConfigControlService(
    Func<AppConfig> getConfig,
    Func<AppConfig, JObject> applyConfig,
    string configPath,
    string backupDirectory)
```

`applyConfig` returns a `JObject` of warnings (empty object when clean).

Tests create a fresh `AppConfig` and assert:
- `Get` returns revision and summary without `Notes`.
- `Patch` with `{"Theme":"light"}` changes config, returns `changes` and new revision.
- stale `baseRevision` → `conflict` and no change.
- `Patch` with `{"Widgets":[]}` → `forbidden`.
- `dryRun` doesn't change.
- `Backup` writes a file under the backup dir; 21 backups keep 20.

- [ ] **Step 2: Run red**

`dotnet run --project tests\ControlCheck` → missing service.

- [ ] **Step 3: Implement `ConfigControlService`**

Key patch logic:

```csharp
private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
{
    "Language", "Theme", "PanelOpacity", "IslandX", "IslandCollapseDelay",
    "DefaultGridColumns", "IconStyle", "ItemScale", "StartWithWindows",
    "HideDesktopSourceOnStore", "HideDesktopIcons", "PinStorageToQuickAccess",
    "FileSearch"
};

public JObject Patch(JObject p, string baseRevision)
{
    if (string.IsNullOrEmpty(baseRevision)) throw ControlException.RevisionRequired();
    string current = CurrentRevision;
    if (!string.Equals(baseRevision, current, StringComparison.Ordinal))
        throw ControlException.Conflict(current);

    var patch = p["patch"] as JObject ?? new JObject();
    foreach (var property in patch.Properties())
        if (!Allowed.Contains(property.Name))
            throw ControlException.Forbidden("Use widget/group commands for " + property.Name + ".");

    var currentConfig = _getConfig();
    var currentRoot = JObject.FromObject(currentConfig);
    var next = (JObject)currentRoot.DeepClone();
    next.Merge(patch, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
    var parsed = ConfigDocument.Parse(next.ToString(), out var warnings);
    if (parsed == null) throw ControlException.Validation(warnings);

    var changes = new List<string>();
    foreach (var property in patch.Properties())
        if (!JToken.DeepEquals(currentRoot[property.Name], property.Value))
            changes.Add(property.Name);

    bool dryRun = p["dryRun"] != null && (bool)p["dryRun"];
    JObject applyWarnings = new JObject();
    if (!dryRun)
    {
        Backup("before-patch");
        applyWarnings = _applyConfig(parsed) ?? new JObject();
    }
    return new JObject
    {
        ["wouldChange"] = changes.Count > 0,
        ["changes"] = new JArray(changes),
        ["warnings"] = applyWarnings,
        ["restartRequired"] = false,
        ["revision"] = dryRun ? current : CurrentRevision
    };
}
```

`_applyConfig` is supplied by `ConfigApplyService` in Task 5; in Task 4 tests pass a fake that replaces the config. `CurrentRevision` = `ConfigRevision.Compute(_getConfig())`.

- [ ] **Step 4: Route `config.get` / `config.patch` / `config.backup`**

In `ControlRequestRouter.Handle`, add cases. The router owns one `ConfigControlService` instance wired to `WidgetManager.Instance.Config` and `WidgetManager.Instance.SaveConfigNow`. Pass `request.BaseRevision` into `Patch`; do not read it from `params`. For Task 4, `applyConfig` can be a temporary delegate that replaces `_config` fields and returns an empty `JObject` (Task 5 replaces it with `ConfigApplyService`).

- [ ] **Step 5: Run green**

`dotnet run --project tests\ControlCheck`; expected all pass.

---

### Task 5: Config apply, startup/Quick Access, desktop icon guard

**Files:**
- Create: `Core/Control/ConfigApplyService.cs`
- Create: `Core/StartupRegistryService.cs`
- Create: `Core/QuickAccessService.cs`
- Create: `Core/DesktopIconGuard.cs`
- Modify: `Core/WidgetManager.cs` (apply helpers)
- Modify: `App.xaml.cs` (use services; desktop guard on exit/session end)
- Modify: `tests/ControlCheck/Program.cs` (fakes/policy)
- Modify: `tests/XamlLoadCheck/Program.cs` (hot apply pins)

**Interfaces:**
- `ConfigApplyService.Apply(AppConfig next)` returns `JObject warnings`; applies hot fields.
- `StartupRegistryService.Apply(bool startWithWindows)` returns `bool`; test seam interface.
- `QuickAccessService.Apply(bool pinned)` returns `bool`; test seam interface.
- `DesktopIconGuard.RestoreOnExit()`; `DesktopIconGuard.ApplyOnStartup(AppConfig)`; test seam `IDesktopIconState`.
- Produces: hot apply for theme/language/island/icon/item scale/file search/HideDesktopIcons/StartWithWindows/PinStorageToQuickAccess.

- [ ] **Step 1: Write failing tests**

`ControlCheck`:
```csharp
Check(DesktopIconGuard.Policy(originalHidden: true, currentHidden: true, hideDesktopIcons: true)
      == DesktopIconAction.Show,
    "desktop guard: a hidden run shows icons on exit");
Check(DesktopIconGuard.Policy(originalHidden: true, currentHidden: true, hideDesktopIcons: false)
      == DesktopIconAction.None,
    "desktop guard: an unhidden run does nothing");
```
Also fake `StartupRegistryService` / `QuickAccessService` interfaces and assert warning on false.

`XamlLoadCheck`:
- `config.patch {"Theme":"light"}` updates `ThemeManager.CurrentTheme`/chrome brush.
- `{"Language":"zh"}` changes `Localization.CurrentLanguage` and a widget title.
- `{"IslandCollapseDelay":2.0}` updates island timer.
- `{"IconStyle":"handdrawn"}` rebuilds island tiles.
- `{"HideDesktopIcons":true}` calls the guard; use a test seam to avoid touching the real desktop.
- `{"StartWithWindows":true}` uses fake registry service and returns warning path.

- [ ] **Step 2: Run red**

ControlCheck/XamlLoadCheck compile errors.

- [ ] **Step 3: Implement services**

`StartupRegistryService` extracts `App.SyncStartupRegistry`:

```csharp
public static class StartupRegistryService
{
    public static bool Apply(bool startWithWindows)
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) return false;
                if (startWithWindows)
                {
                    string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    key.SetValue("Kobold", "\"" + exe + "\"");
                }
                else key.DeleteValue("Kobold", false);
                return true;
            }
        }
        catch { return false; }
    }
}
```

`QuickAccessService` wraps `QuickAccess.Pin/Unpin/TryIsPinned` and the existing `WaitForPinState` polling (move that helper out of `SettingsWindow` or duplicate a small 10×100ms loop).

`DesktopIconGuard`:

```csharp
public enum DesktopIconAction { None, Show }

public static class DesktopIconGuard
{
    public static DesktopIconAction Policy(bool originalHidden, bool currentHidden, bool hideDesktopIcons)
    {
        if (!hideDesktopIcons || !currentHidden) return DesktopIconAction.None;
        return DesktopIconAction.Show; // always show on exit; next launch re-hides
    }

    public static void RestoreOnExit()
    {
        try
        {
            var config = WidgetManager.Instance.Config;
            if (config == null || !config.HideDesktopIcons) return;
            DesktopIcons.SetHidden(false);
            AppDiagnostics.Write("desktop guard: restored icons on exit");
        }
        catch (Exception ex) { AppDiagnostics.Write("desktop guard failed: " + ex.Message); }
    }
}
```

`ConfigApplyService` maps fields; for `HideDesktopIcons` call `WidgetManager.Instance.SetHideDesktopIcons`; for `StartWithWindows`/QuickAccess call services and collect warnings; for theme/language/icon/island/file search mirror `SettingsWindow.SaveButton_Click`/`ThemeCombo_SelectionChanged` behavior.

- [ ] **Step 4: Wire exit/session end**

In `App.OnExit` replace `RestoreDesktopIconsOnExit` with `DesktopIconGuard.RestoreOnExit`; add same call in `SessionEnding` before save. Keep `WidgetManager.RestoreDesktopIconsOnExit` for compatibility or remove after tests.

- [ ] **Step 5: Run green**

```powershell
dotnet build Kobold.csproj -c Debug
dotnet run --project tests\ControlCheck
dotnet run --project tests\XamlLoadCheck
dotnet run --project tests\DesktopIconsCheck
```

---

### Task 6: Widget / Group control service

**Files:**
- Create: `Core/Control/WidgetGroupControlService.cs`
- Modify: `Core/WidgetManager.cs` (`CreateWidgetForControl`, update helpers)
- Modify: `Core/Control/ControlRequestRouter.cs` (route `widget.*`/`group.*`)
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces:**
- Produces methods:
  - `JObject Add(JObject p)`, `JObject Update(JObject p)`, `JObject Delete(JObject p)`
  - `JObject Merge(JObject p)`, `JObject Detach(JObject p)`, `JObject Reorder(JObject p)`, `JObject Dissolve(JObject p)`
- Consumes: `WidgetManager.FindHostForWidget`, `CreateWidget`, `RemoveWidget`, `MergeWidgets`, `DetachWidget`, `ReorderGroupMember`, `GroupHostPolicy`.

- [ ] **Step 1: Write failing XamlLoadCheck pins**

Add checks:
- `widget.add` note: returns id, `IsPanelOpen == false`, `IsPanelPinned == false`, config count +1.
- `widget.update` name/color/pinned on a grouped member: data changes, tab label updates, group pin derives.
- `widget.delete` folder member: folder files ejected (reuse fake folder view or real temp storage); group dissolves.
- `group.merge`: two standalone widgets become a group; live views preserved.
- `group.detach`: member becomes standalone; group survives if >=2.
- `group.reorder`: tab order changes; active follows.
- `group.dissolve`: all members become standalone; group config removed.

- [ ] **Step 2: Run red**

`dotnet run --project tests\XamlLoadCheck` → missing service.

- [ ] **Step 3: Implement `WidgetManager.CreateWidgetForControl`**

Add:

Refactor `CreateWidget` into a shared core so the UI and control paths cannot drift:

```csharp
public IWidgetWindow CreateWidget(string kindId)
{
    return CreateWidgetCore(kindId, open: true, pinned: true);
}

public IWidgetWindow CreateWidgetForControl(string kindId)
{
    return CreateWidgetCore(kindId, open: false, pinned: false);
}

private IWidgetWindow CreateWidgetCore(string kindId, bool open, bool pinned)
{
    var kind = WidgetKindRegistry.Find(kindId);
    if (kind == null) return null;
    var data = kind.CreateDefault();
    int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
    int screenHeight = (int)SystemParameters.PrimaryScreenHeight;
    int count = _widgets.Count;
    data.PosX = screenWidth / 2 - 50 + (count % 5) * 30;
    data.PosY = screenHeight / 2 - 50 + (count % 5) * 30;
    data.Color = CreationColors[count % CreationColors.Length];
    data.Name = Localization.Get(kind.NameKey);
    data.IsPanelPinned = pinned;
    if (data is FolderData folder) folder.GridColumns = _config.DefaultGridColumns;
    PanelSizeMigration.FillMissingSizes(data, _config.ItemScale, _config.Language, DateTime.Today);
    _config.AddWidget(data);
    var widget = CreateWidgetInternal(data);
    RefreshIsland();
    if (open) OpenPanel(widget);
    SaveConfig();
    return widget;
}
```

- [ ] **Step 4: Implement the service methods**

Use this shape; `Ok`/`Error` helpers are the same ones the router uses.

```csharp
public JObject Add(JObject p)
{
    string kind = (string)p["kind"];
    var widget = _manager.CreateWidgetForControl(kind);
    if (widget == null) throw ControlException.Validation("Unknown widget kind: " + kind);

    var data = widget.Data;
    if (p["name"] != null) data.Name = ((string)p["name"]).Trim();
    if (p["color"] != null) data.Color = (string)p["color"];
    if (p["posX"] != null) data.PosX = (int)p["posX"];
    if (p["posY"] != null) data.PosY = (int)p["posY"];
    widget.UpdateUI();
    _manager.SaveConfig();
    return new JObject
    {
        ["id"] = data.Id,
        ["kind"] = data.Kind,
        ["revision"] = ConfigRevision.Compute(_manager.Config)
    };
}

public JObject Update(JObject p)
{
    string id = (string)p["id"];
    var data = FindData(id);
    if (data == null) throw ControlException.Validation("Unknown widget: " + id);

    if (p["name"] != null) data.Name = ((string)p["name"]).Trim();
    if (p["color"] != null) data.Color = (string)p["color"];
    if (p["locked"] != null) data.IsLocked = (bool)p["locked"];
    if (p["pinned"] != null) data.IsPanelPinned = (bool)p["pinned"];
    if (p["posX"] != null) data.PosX = (int)p["posX"];
    if (p["posY"] != null) data.PosY = (int)p["posY"];
    if (p["panelWidth"] != null) data.PanelWidth = (double)p["panelWidth"];
    if (p["panelHeight"] != null) data.PanelHeight = (double)p["panelHeight"];
    if (data is FolderData folder && p["folder"]?["gridColumns"] != null)
        folder.GridColumns = (int)p["folder"]["gridColumns"];
    if (data is TodoData todo && p["todo"]?["holidayRegion"] != null)
        todo.HolidayRegion = (string)p["todo"]["holidayRegion"];
    if (data is NoteData note && p["note"]?["fontScale"] != null)
        note.FontScale = (double)p["note"]["fontScale"];

    var host = _manager.FindHostForWidget(id);
    var group = host as WidgetGroupWindow;
    if (group != null) group.Group.IsPanelPinned =
        GroupHostPolicy.AnyMemberPinned(GroupHostPolicy.MembersOf(group.Group, _manager.Config.Widgets));
    host?.UpdateUI();
    _manager.SaveConfig();
    _manager.RefreshIsland();
    return new JObject { ["changed"] = new JArray(), ["revision"] = ConfigRevision.Compute(_manager.Config) };
}

public JObject Delete(JObject p)
{
    if (p["confirm"] == null || !(bool)p["confirm"])
        throw ControlException.Forbidden("widget.delete requires confirm:true.");
    string id = (string)p["id"];
    if (_manager.FindHostForWidget(id) == null)
        throw ControlException.Validation("Unknown widget: " + id);
    _manager.RemoveWidget(id);
    return new JObject { ["removed"] = id, ["revision"] = ConfigRevision.Compute(_manager.Config) };
}

public JObject Merge(JObject p)
{
    string targetId = (string)p["targetId"];
    var members = p["memberIds"] as JArray ?? new JArray();
    var target = _manager.FindHostForWidget(targetId);
    if (target == null) throw ControlException.Validation("Unknown target: " + targetId);
    foreach (var token in members)
    {
        string sourceId = (string)token;
        var source = _manager.FindHostForWidget(sourceId);
        if (source == null || source is WidgetGroupWindow)
            throw ControlException.Validation("Source must be a standalone widget: " + sourceId);
        if (!_manager.MergeWidgets(source, target))
            throw ControlException.Validation("Merge refused for " + sourceId);
        target = _manager.FindHostForWidget(targetId);
    }
    return new JObject
    {
        ["groupId"] = (target as WidgetGroupWindow)?.WidgetId,
        ["revision"] = ConfigRevision.Compute(_manager.Config)
    };
}
```

`Detach`, `Reorder`, `Dissolve` follow the same pattern:

```csharp
public JObject Detach(JObject p)
{
    string groupId = (string)p["groupId"];
    string widgetId = (string)p["widgetId"];
    if (!_manager.DetachWidget(groupId, widgetId))
        throw ControlException.Validation("Detach refused.");
    return new JObject
    {
        ["standaloneId"] = widgetId,
        ["revision"] = ConfigRevision.Compute(_manager.Config)
    };
}

public JObject Reorder(JObject p)
{
    string groupId = (string)p["groupId"];
    int from = (int)p["from"];
    int to = (int)p["to"];
    if (!_manager.ReorderGroupMember(groupId, from, to))
        throw ControlException.Validation("Reorder refused.");
    return new JObject { ["revision"] = ConfigRevision.Compute(_manager.Config) };
}

public JObject Dissolve(JObject p)
{
    if (p["confirm"] == null || !(bool)p["confirm"])
        throw ControlException.Forbidden("group.dissolve requires confirm:true.");
    string groupId = (string)p["groupId"];
    var host = _manager.FindHostForWidget(groupId) as WidgetGroupWindow;
    if (host == null) throw ControlException.Validation("Unknown group: " + groupId);
    var ids = new JArray();
    while (host.Members.Count >= 2)
    {
        string memberId = host.Members[0].Id;
        if (!_manager.DetachWidget(groupId, memberId))
            throw ControlException.Validation("Dissolve refused for " + memberId);
        ids.Add(memberId);
        host = _manager.FindHostForWidget(groupId) as WidgetGroupWindow;
        if (host == null) break;
    }
    return new JObject { ["standaloneIds"] = ids, ["revision"] = ConfigRevision.Compute(_manager.Config) };
}
```

- [ ] **Step 5: Run green**

`dotnet run --project tests\XamlLoadCheck`; expected all pass.

---

### Task 7: Diagnostics search/folder/memory + log tail

**Files:**
- Modify: `Core/Control/DiagnosticsService.cs`
- Modify: `Services/FullscreenWatcher.cs` (`IsAvoiding`)
- Modify: `Core/WidgetManager.cs` (last trim state, fullscreen reference)
- Modify: `Controls/FolderPanelView.xaml.cs` (`FolderDiagnosticsSnapshot`)
- Modify: `Core/Control/ControlRequestRouter.cs` (route `diag.*`, `search.*`, `app.showIsland`)
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces:**
- `DiagnosticsService.Snapshot()` full fields from spec 7.1.
- `DiagnosticsService.TailLog(string name, int lines)`.
- `DiagnosticsService.SearchStatus()`, `SearchQuery(string text, int limit)`.
- `DiagnosticsService.Folders()`.
- `search.rebuild/cancel/pause/resume/reconcile`.

- [ ] **Step 1: Write failing pins**

XamlLoadCheck:
- `diag.snapshot` has `app`, `config`, `island`, `search`, `memory`, `fullscreen`, `folders`, `logs`.
- `diag.folders` includes a folder widget with `active`/`watcherActive`.
- `diag.search.status` matches `FileSearchIndexService.Status`.
- `diag.search.query` returns a known seeded entry.
- `diag.logs.tail --name app` returns lines after `AppDiagnostics.Write`.
- `search.rebuild` calls `RequestRebuild` (fake service seam or state change).

- [ ] **Step 2: Run red**

- [ ] **Step 3: Implement**

Add `FullscreenWatcher.IsAvoiding => _lastAvoid`; store `_fullscreenWatcher` reference and `LastTrimUtc` in `WidgetManager`; add `FolderPanelView.FolderDiagnosticsSnapshot()` returning `JObject`; iterate `WidgetManager.Widgets` and `WidgetGroupWindow.Views` on UI thread.

Search commands call `FileSearchIndexService.Instance.RequestRebuild/CancelScan/Pause/Resume/RequestReconcile`; `search.query` calls `Search(text, limit, CancellationToken.None)`.

Log tail uses `File.ReadLines` with `FileShare.ReadWrite` and takes last N; only `app`/`search` names.

- [ ] **Step 4: Run green**

`dotnet run --project tests\XamlLoadCheck`; expected all pass.

---

### Task 8: `config.replace`, `config.restore`, restart helper

**Files:**
- Create: `Core/Control/RestartHelper.cs`
- Modify: `Core/Control/ConfigControlService.cs` (`Replace`, `Restore`)
- Modify: `Core/Control/ControlRequestRouter.cs`
- Modify: `App.xaml.cs` (`--restart-helper` mode, desktop guard)
- Modify: `tests/ControlCheck/Program.cs` (helper args/policy)
- Modify: `tests/ConfigSafetyCheck/Program.cs` (restore)

**Interfaces:**
- `ConfigControlService.Replace(JObject p, string baseRevision)`; `Restore(JObject p, string baseRevision)`.
- `RestartHelper.Launch(int parentPid, string exePath)` starts the helper process; `RestartHelper.TryRun(string[] args)` blocks in helper mode; `RestartHelper.Run(int parentPid, string exePath)` waits for the parent and starts the new instance.
- App startup detects `--restart-helper <pid> <exe>` before normal startup.

- [ ] **Step 1: Write failing tests**

ControlCheck:
- `RestartHelper.ParseArgs(new[]{"--restart-helper","123","C:\\Kobold.exe"})` returns pid/exe.
- `DesktopIconGuard` shows icons before helper starts.
ConfigSafety:
- replace with structural document returns `restartRequired`; `restart:true` writes and returns `restarting:true`.
- restore from a backup applies the document and backs up current first.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Implement**

`Core/Control/RestartHelper.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Threading;

namespace Kobold.Core.Control
{
    public static class RestartHelper
    {
        public static void Launch(int parentPid, string exePath)
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = "--restart-helper " + parentPid + " \"" + exePath + "\"",
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exePath)
            });
        }

        public static bool TryRun(string[] args)
        {
            int pid;
            string exe;
            if (!TryParse(args, out pid, out exe)) return false;
            Run(pid, exe); // blocks in the helper process; no UI is created
            return true;
        }

        public static bool TryParse(string[] args, out int parentPid, out string exePath)
        {
            parentPid = 0;
            exePath = null;
            if (args == null || args.Length != 3
                || !string.Equals(args[0], "--restart-helper", StringComparison.Ordinal))
                return false;
            if (!int.TryParse(args[1], out parentPid) || parentPid <= 0) return false;
            exePath = args[2];
            return !string.IsNullOrWhiteSpace(exePath);
        }

        public static void Run(int parentPid, string exePath)
        {
            try
            {
                var parent = Process.GetProcessById(parentPid);
                parent.WaitForExit(15000);
            }
            catch { }
            try
            {
                Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exePath)
                });
            }
            catch (Exception ex)
            {
                Kobold.Core.AppDiagnostics.Write("restart helper failed: " + ex.Message);
            }
        }
    }
}
```

In `App.OnStartup`, if `e.Args` starts with `--restart-helper`, call `RestartHelper.TryRun(e.Args)` and `Shutdown()` without creating the manager/island.

`ConfigControlService.Replace`: parse, validate, detect structural changes by comparing `Widgets/Groups/Folders/FrequentFiles` JSON. If structural and `restart` is false, return `restartRequired` without writing. If `restart:true`, call `DesktopIconGuard.RestoreOnExit()`, backup current, write new, call `RestartHelper.Launch(Process.GetCurrentProcess().Id, exePath)`, and schedule shutdown with `Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Application.Current.Shutdown()))` **after** returning the response. Never call `Shutdown` before the response is written.

`Restore`: path must be under `backups/`; read JSON; same path as Replace.

- [ ] **Step 4: Run green**

`dotnet run --project tests\ControlCheck`, `tests\ConfigSafetyCheck`, manual restart later.

---

### Task 9: `koboldctl.ps1` + installer

**Files:**
- Create: `tools/koboldctl.ps1`
- Modify: `tools/installer-common.ps1` (`KoboldToolFiles = @('koboldctl.ps1')`)
- Create: `tests/ControlScriptCheck/ControlScriptCheck.csproj`
- Create: `tests/ControlScriptCheck/Program.cs`
- Modify: `tools/Run-Checks.ps1` (add `ControlScriptCheck` with `Ci = $false`; it spawns PowerShell + a fake pipe, so keep it in the local `checks` group only)

**Interfaces:**
- CLI commands exactly as spec 9.2.
- `Invoke-Control` sends one JSON line over `NamedPipeClientStream`, reads one line, returns object.
- Exit codes: 0 success, 1 business/validation, 2 connection/protocol, 3 restart required.

- [ ] **Step 1: Write failing `ControlScriptCheck`**

Start a fake named-pipe server in the console app, then run:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\koboldctl.ps1 status --pipe <fake> --json
```
Assert exit 0 and JSON `ok:true`. Also test `--dry-run` sends `dryRun:true`, `--yes` sends `confirm:true`, and connection failure exits 2.

- [ ] **Step 2: Run red**

`dotnet run --project tests\ControlScriptCheck` → script missing.

- [ ] **Step 3: Implement the script**

Core helper:

```powershell
function Invoke-Control {
    param([string]$PipeName, [string]$Method, [hashtable]$Params = @{}, [string]$BaseRevision = $null)
    $request = @{
        v = 1
        id = [guid]::NewGuid().ToString('N')
        method = $Method
        params = $Params
    }
    if ($BaseRevision) { $request.baseRevision = $BaseRevision }
    $json = $request | ConvertTo-Json -Depth 20 -Compress
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect(3000)
        $writer = New-Object System.IO.StreamWriter($client)
        $writer.AutoFlush = $true
        $writer.WriteLine($json)
        $reader = New-Object System.IO.StreamReader($client)
        $line = $reader.ReadLine()
        return $line | ConvertFrom-Json
    } finally {
        $client.Dispose()
    }
}
```

Command parsing maps `config get/patch/replace/backup/restore`, `widget add/update/delete`, `group merge/detach/reorder/dissolve`, `search ...`, `diag ...`, `status`.
`--start` starts `Kobold.exe`, polls `status` for up to 10s.
`--restart` sends `restart:true`, then polls `status` until new pipe; exit 3 on timeout.
`--json` prints the response object; human mode prints a summary.

- [ ] **Step 4: Installer copy**

Add to `installer-common.ps1`:

```powershell
$KoboldToolFiles = @('koboldctl.ps1')
```

Validate/copy it in `Install-KoboldFiles` alongside runtime files.

- [ ] **Step 5: Run green**

`dotnet run --project tests\ControlScriptCheck`; `Run-Checks -Group ci` expected 28/28; `Run-Checks -Group checks` expected 36/36 (34 existing + ControlCheck + ControlScriptCheck).

---

### Task 10: `kobold-config` skill + docs

**Files:**
- Create: `skill/kobold-config/SKILL.md`
- Create: `skill/kobold-config/README.md`
- Create: `skill/kobold-config/references/config-schema.md`
- Create: `skill/kobold-config/references/troubleshooting.md`
- Create: `skill/kobold-config/scripts/find-koboldctl.ps1`
- Modify: `README.md`
- Modify: `CHANGELOG.md`

**Interfaces:**
- `SKILL.md` frontmatter:
  ```yaml
  ---
  name: kobold-config
  description: Configure Kobold, add/remove/group widgets, inspect config, and debug the file-search service through koboldctl.
  ---
  ```
- `README.md` tells the user/agent to copy the folder into its own skills directory; no agent-specific path.
- References match `config.describe`/CLI commands.

- [ ] **Step 1: Write the skill**

`SKILL.md` sections:
1. When to use.
2. Locate `koboldctl.ps1` (install dir or repo `tools/`).
3. Workflow: status → describe/get/snapshot → diagnose → dry-run → confirm → apply → verify.
4. Safety rules (no direct live config edit, baseRevision, confirm destructive, summary by default, no arbitrary shell).
5. Command examples for theme/language, add note, merge group, debug search, backup/restore.
6. Error handling (`conflict`, `restart_required`, `app_not_running`, `restart_failed`).

- [ ] **Step 2: Write references**

`config-schema.md`: top-level fields, widget kinds, group fields, FileSearch fields, examples.
`troubleshooting.md`: search stuck, no results, config validation, widget/group issues, restart.

- [ ] **Step 3: README/CHANGELOG**

Add a short “Local agent control / kobold-config skill” section to README and CHANGELOG. Mention `koboldctl.ps1` in the install dir and the portable skill folder.

- [ ] **Step 4: Verify docs**

```powershell
dotnet build Kobold.csproj -c Debug
Select-String -Path skill\kobold-config\SKILL.md -Pattern 'TODO|TBD|OpenCode|Claude'
```
Expected: build 0/0; no placeholders/agent-specific paths.

---

### Task 11: Full regression and manual smoke

**Files:** none (verification only).

- [ ] **Step 1: Full matrix**

```powershell
dotnet build Kobold.csproj -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group ci
$env:APPDATA = Join-Path $env:TEMP ("kobold-final-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $env:APPDATA | Out-Null
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group checks
dotnet run --project tests\GroupUiSpike -- --check
```
Expected: build 0/0; CI 28/28; checks 36/36 (34 existing + ControlCheck + ControlScriptCheck); GroupUiSpike 15/15; config-pin True.

- [ ] **Step 2: Focused checks**

```powershell
dotnet run --project tests\ControlCheck
dotnet run --project tests\ControlScriptCheck
dotnet run --project tests\ConfigSafetyCheck
dotnet run --project tests\DesktopIconsCheck
dotnet run --project tests\XamlLoadCheck
```

- [ ] **Step 3: Manual smoke (human)**

- Install/deploy to a test copy (or run from `bin\Debug`) and start `koboldctl.ps1 status`.
- `config get --summary` → revision; `config patch` theme/language; verify UI.
- `widget add --kind note` → no focus steal; island updates.
- `group merge` two widgets → tabs; `group detach`/`dissolve`; live views preserved.
- `search status/query/rebuild`; `diag logs --name search`.
- `config replace` structural with `--restart` → helper restarts; desktop icons show during restart, hide again after startup.
- Kill Kobold normally while desktop icons hidden → icons show; next launch hides again.
- Pipe ACL: a second Windows user cannot connect (manual).
- Skill: copy `skill/kobold-config` into an agent skills dir; ask it to change theme/add note/debug search.

- [ ] **Step 4: Record results**

Write `PASS/FAIL` for every command, note any flake, and do not commit.

---

## Self-Review Notes

- Spec coverage: protocol, config, widget/group, diagnostics/search, skill, security, packaging, tests are each mapped to a task.
- Decision coverage: 2C (no showSettings), 3B (restart helper), 4B (immediate startup/QuickAccess), 5A (all folder states), desktop icon guard are in Tasks 5/8/10.
- No agent-specific paths: Task 10 uses generic `README.md`; no `install-skill.ps1`.
- No commit steps: every checkpoint says build/check, not git commit, per workspace rule.
- The plan uses exact file paths, signatures, and test commands; no TBD placeholders.
