using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kobold.ControlScriptCheck
{
    /// <summary>
    /// Drives tools\koboldctl.ps1 against a throwaway named pipe. Nothing here
    /// touches the real app or its pipe: the server name is unique per run and
    /// every connection carries exactly one request and one response.
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            string root = FindRepoRoot();
            string script = Path.Combine(root, "tools", "koboldctl.ps1");
            string sandbox = Path.Combine(
                Path.GetTempPath(), "kobold-ctl-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            string patchFile = Path.Combine(sandbox, "patch.json");
            File.WriteAllText(patchFile, "{ \"Theme\": \"light\" }", new UTF8Encoding(false));

            string pipeName = "Kobold.ControlCheck." + Guid.NewGuid().ToString("N");
            string missingPipe = "Kobold.ControlCheck.Missing." + Guid.NewGuid().ToString("N");

            try
            {
                using (var server = new FakePipeServer(pipeName, 10, Respond))
                {
                    CliResult status = RunCli(root, script,
                        "status", "--json", "--pipe", pipeName);
                    Check(status.ExitCode == 0, "status --json exits 0");
                    JObject statusJson = ParseJson(status.Stdout);
                    Check(statusJson != null && Bool(statusJson["ok"]),
                        "status --json prints ok:true");

                    CliResult human = RunCli(root, script,
                        "status", "--pipe", pipeName);
                    Check(human.ExitCode == 0
                          && human.Stdout.Contains("path: C:\\fake\\config.json")
                          && human.Stdout.Contains("revision: rev-12345678"),
                        "status prints the human path and 12-char revision summary");

                    CliResult get = RunCli(root, script,
                        "config", "get", "--summary", "--json", "--pipe", pipeName);
                    Check(get.ExitCode == 0, "config get exits 0");

                    CliResult dryRun = RunCli(root, script,
                        "config", "patch", "--file", patchFile,
                        "--base-revision", "rev-abc123", "--dry-run", "--json",
                        "--pipe", pipeName);
                    Check(dryRun.ExitCode == 0, "config patch --dry-run exits 0");

                    CliResult merge = RunCli(root, script,
                        "group", "merge", "--target", "group-target",
                        "--member", "member-a", "--member", "member-b",
                        "--json", "--pipe", pipeName);
                    Check(merge.ExitCode == 0, "group merge exits 0");

                    CliResult delete = RunCli(root, script,
                        "widget", "delete", "--id", "x", "--yes", "--json",
                        "--pipe", pipeName);
                    Check(delete.ExitCode == 0, "widget delete --yes exits 0");

                    CliResult conflict = RunCli(root, script,
                        "config", "patch", "--file", patchFile,
                        "--base-revision", "rev-abc123", "--json",
                        "--pipe", pipeName);
                    Check(conflict.ExitCode == 1, "a conflict response exits 1");
                    JObject conflictJson = ParseJson(conflict.Stdout);
                    Check(conflictJson != null
                          && StringValue(conflictJson["error"], "code") == "conflict",
                        "a conflict response prints a JSON error object");

                    CliResult dryRunRestart = RunCli(root, script,
                        "config", "replace", "--file", patchFile,
                        "--base-revision", "rev-abc123", "--dry-run", "--json",
                        "--pipe", pipeName);
                    Check(dryRunRestart.ExitCode == 0,
                        "config replace --dry-run exits 0 even when restartRequired is reported");
                    JObject dryRunRestartJson = ParseJson(dryRunRestart.Stdout);
                    Check(dryRunRestartJson != null
                          && Bool(dryRunRestartJson["result"]?["restartRequired"]),
                        "the dry-run replace still prints restartRequired:true");

                    CliResult applyNeedsRestart = RunCli(root, script,
                        "config", "replace", "--file", patchFile,
                        "--base-revision", "rev-abc123", "--json",
                        "--pipe", pipeName);
                    Check(applyNeedsRestart.ExitCode == 3,
                        "config replace without --dry-run/--restart exits 3");
                    JObject applyNeedsRestartJson = ParseJson(applyNeedsRestart.Stdout);
                    Check(applyNeedsRestartJson != null
                          && Bool(applyNeedsRestartJson["result"]?["restartRequired"]),
                        "the restart-required apply response is still printed");

                    JObject[] requests = server.Requests;
                    Check(server.Error == null,
                        "the fake pipe server did not fail: " + Describe(server.Error));
                    Check(requests.Length == 9,
                        "the fake pipe saw exactly nine requests (saw "
                        + requests.Length.ToString() + ")");

                    JObject getRequest = requests.FirstOrDefault(r =>
                        StringValue(r, "method") == "config.get");
                    Check(getRequest != null
                          && (string)Param(getRequest, "view") == "summary",
                        "config get --summary sends view=summary");

                    JObject mergeRequest = requests.FirstOrDefault(r =>
                        StringValue(r, "method") == "group.merge");
                    var mergeMembers = mergeRequest == null
                        ? null
                        : Param(mergeRequest, "memberIds") as JArray;
                    Check(mergeRequest != null
                          && (string)Param(mergeRequest, "targetId") == "group-target"
                          && mergeMembers != null && mergeMembers.Count == 2
                          && (string)mergeMembers[0] == "member-a"
                          && (string)mergeMembers[1] == "member-b",
                        "group merge sends targetId and both repeated members");

                    JObject patchRequest = requests.FirstOrDefault(r =>
                        StringValue(r, "method") == "config.patch"
                        && Bool(Param(r, "dryRun")));
                    Check(patchRequest != null,
                        "config patch --dry-run sends params.dryRun=true");
                    if (patchRequest != null)
                    {
                        Check(StringValue(patchRequest, "baseRevision") == "rev-abc123",
                            "the base revision travels on the request envelope");
                        Check(Param(patchRequest, "baseRevision") == null,
                            "the base revision is not repeated in params");
                        JObject patch = Param(patchRequest, "patch") as JObject;
                        Check(patch != null && StringValue(patch, "Theme") == "light",
                            "config patch sends the file content as params.patch");
                    }

                    JObject deleteRequest = requests.FirstOrDefault(r =>
                        StringValue(r, "method") == "widget.delete");
                    JToken deleteId = deleteRequest == null ? null : Param(deleteRequest, "id");
                    Check(deleteRequest != null
                          && Bool(Param(deleteRequest, "confirm"))
                          && deleteId != null
                          && deleteId.Type == JTokenType.String
                          && (string)deleteId == "x",
                        "widget delete --yes sends confirm=true");
                }

                CliResult unavailable = RunCli(root, script,
                    "status", "--json", "--pipe", missingPipe);
                Check(unavailable.ExitCode == 2, "a missing pipe exits 2");
                JObject unavailableJson = ParseJson(unavailable.Stdout);
                Check(unavailableJson != null
                      && StringValue(unavailableJson["error"], "code") == "app_not_running",
                    "a missing pipe prints a JSON app_not_running error");
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL CONTROL SCRIPT CHECKS PASSED");
                return 0;
            }

            Console.WriteLine("FAILURES (" + Errors.Count.ToString() + "):");
            foreach (string error in Errors) Console.WriteLine("  - " + error);
            return 1;
        }

        private static JObject Respond(JObject request, int index)
        {
            string id = request == null ? null : StringValue(request, "id");
            var response = new JObject
            {
                ["v"] = 1,
                ["id"] = id,
                ["ok"] = true
            };

            string method = request == null ? null : StringValue(request, "method");
            switch (method)
            {
                case "diag.ping":
                    response["result"] = new JObject { ["pong"] = true };
                    break;
                case "diag.snapshot":
                    response["result"] = new JObject
                    {
                        ["app"] = new JObject
                        {
                            ["version"] = "1.0.0",
                            ["pid"] = 4242
                        },
                        ["config"] = new JObject
                        {
                            ["path"] = @"C:\fake\config.json",
                            ["revision"] = "rev-1234567890abcdef",
                            ["widgetCount"] = 1,
                            ["groupCount"] = 0
                        }
                    };
                    break;
                case "config.get":
                    response["result"] = new JObject
                    {
                        ["revision"] = "rev-abc123",
                        ["view"] = "summary"
                    };
                    break;
                case "config.patch":
                    if (Bool(Param(request, "dryRun")))
                    {
                        response["result"] = new JObject
                        {
                            ["wouldChange"] = true,
                            ["changes"] = new JArray("Theme"),
                            ["warnings"] = new JObject(),
                            ["restartRequired"] = false,
                            ["revision"] = "rev-abc123"
                        };
                    }
                    else
                    {
                        response["ok"] = false;
                        response["error"] = new JObject
                        {
                            ["code"] = "conflict",
                            ["message"] = "Config changed since the supplied revision.",
                            ["details"] = new JObject
                            {
                                ["currentRevision"] = "rev-new"
                            }
                        };
                    }
                    break;
                case "config.replace":
                    response["result"] = new JObject
                    {
                        ["wouldChange"] = true,
                        ["changes"] = new JArray("Widgets"),
                        ["warnings"] = new JObject(),
                        ["restartRequired"] = true,
                        ["wrote"] = false,
                        ["restarting"] = false,
                        ["revision"] = "rev-abc123"
                    };
                    break;
                case "widget.delete":
                    response["result"] = new JObject
                    {
                        ["removed"] = "x",
                        ["revision"] = "rev-next"
                    };
                    break;
                default:
                    response["result"] = new JObject();
                    break;
            }

            return response;
        }

        private static CliResult RunCli(string root, string script, params string[] arguments)
        {
            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershell)) powershell = "powershell.exe";

            var psi = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = string.Join(" ", new List<string>
                {
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    script
                }.Concat(arguments).Select(Quote)),
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(); } catch { }
                    process.WaitForExit(5000);
                    return new CliResult(-1, stdout.Result, stderr.Result
                        + Environment.NewLine + "CLI timed out.");
                }

                process.WaitForExit();
                return new CliResult(process.ExitCode, stdout.Result, stderr.Result);
            }
        }

        private static string Quote(string value)
        {
            if (value.Length > 0
                && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string FindRepoRoot()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                if (File.Exists(Path.Combine(directory, "Kobold.csproj"))
                    && Directory.Exists(Path.Combine(directory, "tools")))
                {
                    return directory;
                }

                string parent = Path.GetDirectoryName(
                    directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == directory) break;
                directory = parent;
            }

            throw new InvalidOperationException("Could not locate the Kobold repository root.");
        }

        private static JObject ParseJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                return JObject.Parse(text.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static JToken Param(JObject request, string name)
        {
            if (request == null) return null;
            var parameters = request["params"] as JObject;
            return parameters == null ? null : parameters[name];
        }

        private static bool Bool(JToken token)
        {
            return token != null
                && token.Type == JTokenType.Boolean
                && token.Value<bool>();
        }

        private static string StringValue(JToken token, string property)
        {
            if (token == null) return null;
            var obj = token as JObject;
            if (obj == null) return null;
            JToken value = obj[property];
            return value == null || value.Type != JTokenType.String
                ? null
                : (string)value;
        }

        private static string StringValue(JObject obj, string property)
        {
            return StringValue(obj as JToken, property);
        }

        private static string Describe(Exception exception)
        {
            return exception == null ? "none" : exception.GetType().Name + ": " + exception.Message;
        }

        private static void Check(bool condition, string name)
        {
            if (condition)
            {
                Console.WriteLine("  ok   " + name);
                return;
            }

            Errors.Add(name);
            Console.WriteLine("  FAIL " + name);
        }

        private sealed class CliResult
        {
            public CliResult(int exitCode, string stdout, string stderr)
            {
                ExitCode = exitCode;
                Stdout = stdout;
                Stderr = stderr;
            }

            public int ExitCode { get; private set; }
            public string Stdout { get; private set; }
            public string Stderr { get; private set; }
        }

        private sealed class FakePipeServer : IDisposable
        {
            private readonly string _name;
            private readonly int _maxConnections;
            private readonly Func<JObject, int, JObject> _responder;
            private readonly List<JObject> _requests = new List<JObject>();
            private readonly ManualResetEvent _ready = new ManualResetEvent(false);
            private readonly Thread _thread;
            private volatile bool _running = true;
            private volatile NamedPipeServerStream _current;
            private Exception _error;

            public FakePipeServer(string name, int maxConnections,
                Func<JObject, int, JObject> responder)
            {
                _name = name;
                _maxConnections = maxConnections;
                _responder = responder;
                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "ControlScriptCheck.FakePipe"
                };
                _thread.Start();
                if (!_ready.WaitOne(5000))
                    throw new InvalidOperationException("The fake pipe server did not start.");
            }

            public Exception Error
            {
                get { return _error; }
            }

            public JObject[] Requests
            {
                get
                {
                    lock (_requests) return _requests.ToArray();
                }
            }

            private void Loop()
            {
                int handled = 0;
                try
                {
                    while (_running && handled < _maxConnections)
                    {
                        using (var server = new NamedPipeServerStream(
                            _name, PipeDirection.InOut, 1,
                            PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            _current = server;
                            _ready.Set();
                            server.WaitForConnection();

                            string line;
                            using (var reader = new StreamReader(
                                server, Encoding.UTF8, false, 4096, true))
                            {
                                line = reader.ReadLine();
                            }

                            JObject request = null;
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(line)) request = JObject.Parse(line);
                            }
                            catch
                            {
                            }

                            if (request != null)
                            {
                                lock (_requests) _requests.Add(request);
                            }

                            JObject response = _responder(request, handled);
                            byte[] bytes = Encoding.UTF8.GetBytes(
                                response.ToString(Formatting.None) + "\n");
                            server.Write(bytes, 0, bytes.Length);
                            server.Flush();
                            handled++;
                            _current = null;
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (_running) _error = exception;
                }
                finally
                {
                    _ready.Set();
                }
            }

            public void Dispose()
            {
                _running = false;
                try
                {
                    NamedPipeServerStream current = _current;
                    if (current != null) current.Dispose();
                }
                catch
                {
                }

                if (_thread != null) _thread.Join(3000);
                _ready.Dispose();
            }
        }
    }
}
