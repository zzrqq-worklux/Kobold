using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Kobold.Core;
using Kobold.Core.Control;
using Kobold.Core.FileSearch;
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
            Check(!json.Contains("\"Error\""), "protocol: null request fields are omitted");
            string emptyResponseJson = JsonConvert.SerializeObject(new ControlResponse
            {
                V = 1,
                Id = "req-1",
                Ok = true
            }, ControlJson.Settings);
            Check(!emptyResponseJson.Contains("\"Result\"")
                  && !emptyResponseJson.Contains("\"Error\"")
                  && !emptyResponseJson.Contains("\"Revision\""),
                "protocol: null response fields are omitted");

            var afterWriteResponse = new ControlResponse
            {
                V = 1,
                Id = "req-after-write",
                Ok = true,
                AfterWrite = () => { }
            };
            Check(!JsonConvert.SerializeObject(afterWriteResponse, ControlJson.Settings)
                      .Contains("AfterWrite"),
                "protocol: the after-write action is never serialized");

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

            AppDiagnosticsRotates();
            ControlServerRoundTrips();
            ConfigValidationAndRevision();
            ConfigControlServicePins();
            ConfigControlRoutingPins();
            ConfigReplaceAndRestorePins();
            DesktopGuardAndServicePins();

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
            int afterWriteCount = 0;
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
                    Result = new JObject { ["pong"] = true },
                    AfterWrite = () => System.Threading.Interlocked.Increment(ref afterWriteCount)
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
                    // leaveOpen: the reader is disposed first; it must not close
                    // the shared pipe before the writer flushes.
                    using (var reader = new System.IO.StreamReader(
                        client, Encoding.UTF8, true, 4096, true))
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

                        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                        while (System.Threading.Volatile.Read(ref afterWriteCount) == 0
                               && DateTime.UtcNow < deadline)
                        {
                            System.Threading.Thread.Sleep(10);
                        }
                        Check(afterWriteCount == 1,
                            "control server: the after-write action runs after the response is written");
                    }
                }
            }
            finally
            {
                server.Stop();
            }
        }

        private static void ConfigValidationAndRevision()
        {
            string valid = @"{
              ""Language"":""en"", ""Theme"":""dark"",
              ""Widgets"":[
                {""Kind"":""note"",""Id"":""w1"",""Name"":""A"",""GroupId"":""g1""},
                {""Kind"":""todo"",""Id"":""w2"",""Name"":""B""}
              ],
              ""Groups"":[{""Id"":""g1"",""ActiveWidgetId"":""w1""}]
            }";
            var warnings = new List<string>();
            var config = ConfigDocument.Parse(valid, out warnings);
            Check(config != null && config.Widgets.Count == 2,
                "config document: parses widgets");
            Check(config != null && config.Groups == null
                  && config.Widgets[0].GroupId == null,
                "config document: repairs a one-member group away");
            Check(ConfigDocument.Validate(valid, out warnings),
                "config document: valid document validates");
            Check(!ConfigDocument.Validate("{ \"Widgets\": 5 }", out warnings),
                "config document: a wrong-typed block is rejected");

            warnings = new List<string>();
            Check(!ConfigDocument.Validate(@"{""Widgets"":null}", out warnings),
                "config document: explicit null widget list is invalid");
            var nullWidgets = ConfigDocument.Parse(@"{""Widgets"":null}", out warnings);
            Check(nullWidgets == null && warnings.Count > 0,
                "config document: rejected document returns null with a warning");
            Check(!ConfigDocument.Validate(
                      @"{""Widgets"":[{""Kind"":""folder"",""Id"":""f1"",""Items"":null}]}",
                      out warnings),
                "config document: explicit null folder items is rejected");
            Check(!ConfigDocument.Validate(
                      @"{""Widgets"":[{""Kind"":""todo"",""Id"":""t1"",""Items"":null}]}",
                      out warnings),
                "config document: explicit null todo items is rejected");
            Check(!ConfigDocument.Validate(
                      @"{""Widgets"":[{""Kind"":""note"",""Id"":""n1"",""Notes"":null}]}",
                      out warnings),
                "config document: explicit null note notes is rejected");
            Check(!ConfigDocument.Validate(@"{""Widgets"":[null]}", out warnings),
                "config document: a null widget entry is rejected");

            string paired = @"{
              ""Widgets"":[
                {""Kind"":""note"",""Id"":""w1"",""GroupId"":""g1""},
                {""Kind"":""todo"",""Id"":""w2"",""GroupId"":""g1""}
              ],
              ""Groups"":[{""Id"":""g1"",""ActiveWidgetId"":""w2""}]
            }";
            var pairedConfig = ConfigDocument.Parse(paired, out warnings);
            Check(pairedConfig != null && pairedConfig.Groups != null
                  && pairedConfig.Groups.Count == 1
                  && pairedConfig.Groups[0].ActiveWidgetId == "w2",
                "config document: a two-member group survives repair");

            var describe = ConfigDescribe.Build();
            Check(describe["topLevel"] is JArray top
                  && top.Any(x => (string)x["name"] == "Theme"),
                "config describe: includes Theme");
            Check(describe["widgetKinds"] is JArray kinds
                  && kinds.Any(x => (string)x["id"] == "note"),
                "config describe: includes the note kind");

            Check((int)describe["schemaVersion"] == AppConfig.CurrentSchemaVersion,
                "config describe: reports the schema version");
            Check(AllowedMatches(describe, "Theme",
                      "dark", "light", "high contrast"),
                "config describe: Theme allowed values");
            Check(AllowedMatches(describe, "IconStyle", "classic", "handdrawn"),
                "config describe: IconStyle allowed values");
            Check(AllowedMatches(describe, "Language", "en", "zh", "ja"),
                "config describe: Language allowed values");
            var opacityRange = RangeOf(describe, "PanelOpacity");
            Check(opacityRange != null && opacityRange.Count == 2
                  && (double)opacityRange[0] == 0.2
                  && (double)opacityRange[1] == 1.0,
                "config describe: PanelOpacity range");

            var topLevel = (JArray)describe["topLevel"];
            foreach (var property in typeof(AppConfig).GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;
                Check(topLevel.Any(x => (string)x["name"] == property.Name),
                    "config describe: covers AppConfig." + property.Name);
            }

            var describedKinds = (JArray)describe["widgetKinds"];
            foreach (var kind in WidgetKindRegistry.All)
            {
                Check(describedKinds.Any(x => (string)x["id"] == kind.Id),
                    "config describe: covers widget kind " + kind.Id);
            }

            foreach (var property in typeof(WidgetGroup).GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                Check(((JArray)describe["groups"]).Any(x => (string)x == property.Name),
                    "config describe: covers WidgetGroup." + property.Name);
            }

            foreach (var property in typeof(FileSearchSettings).GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                Check(((JArray)describe["fileSearch"]).Any(x => (string)x == property.Name),
                    "config describe: covers FileSearchSettings." + property.Name);
            }

            string r1 = ConfigRevision.Compute(config);
            string r2 = ConfigRevision.Compute(config);
            Check(r1 == r2 && r1.StartsWith("sha256:", StringComparison.Ordinal),
                "config revision: stable and prefixed");
            Check(r1 == r1.ToLowerInvariant() && r1.Length == "sha256:".Length + 64,
                "config revision: lowercase hex digest");

            var summary = (JObject)JToken.Parse(ConfigDocument.ToJson(config, full: false));
            Check(summary["HideIconsOriginal"] == null
                  && summary["FrequentFiles"] == null,
                "config document: summary omits internal and frequent-file blocks");
            var summaryWidgets = (JArray)summary["Widgets"];
            Check(summaryWidgets != null && summaryWidgets.Count == 2
                  && summaryWidgets[0]["Notes"] == null
                  && summaryWidgets[1]["Items"] == null,
                "config document: summary omits widget payload lists");
            Check(((JObject)JToken.Parse(ConfigDocument.ToJson(config, full: true)))
                      ["Widgets"] is JArray,
                "config document: full output keeps the widget array");

            string legacy = @"{ ""Folders"":[{ ""Name"":""Legacy"",""Items"":[] }] }";
            var legacyConfig = ConfigDocument.Parse(legacy, out warnings);
            Check(legacyConfig != null && legacyConfig.Folders != null
                  && legacyConfig.Folders.Count == 1,
                "config document: legacy Folders document parses");
            var legacySummary = (JObject)JToken.Parse(
                ConfigDocument.ToJson(legacyConfig, full: false));
            Check(legacySummary["Folders"] == null,
                "config document: summary omits the legacy Folders block");
            Check(((JObject)JToken.Parse(ConfigDocument.ToJson(legacyConfig, full: true)))
                      ["Folders"] is JArray,
                "config document: full output keeps the legacy Folders block");
        }

        private static void ConfigControlServicePins()
        {
            string root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "kobold-control-config-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                var note = new NoteData { Id = "w1", Name = "Notes" };
                note.Notes.Add(new NoteItem { Id = "n1", Text = "private body" });
                AppConfig live = new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion };
                live.Widgets.Add(note);

                int applyCount = 0;
                string configPath = System.IO.Path.Combine(root, "config.json");
                string backupDirectory = System.IO.Path.Combine(root, "backups");
                var service = new ConfigControlService(
                    () => live,
                    next =>
                    {
                        applyCount++;
                        live = next;
                        return new JObject();
                    },
                    configPath,
                    backupDirectory);

                // --- config.get ---
                JObject summary = service.Get(new JObject());
                Check((string)summary["view"] == "summary",
                    "config service: get defaults to the summary view");
                Check((string)summary["path"] == configPath,
                    "config service: get returns the config path");
                Check((int)summary["schemaVersion"] == AppConfig.CurrentSchemaVersion,
                    "config service: get returns the schema version");
                Check(((string)summary["revision"] ?? string.Empty)
                          .StartsWith("sha256:", StringComparison.Ordinal)
                      && (string)summary["revision"] == service.CurrentRevision,
                    "config service: get returns the current revision");
                var summaryWidgets = summary["config"]?["Widgets"] as JArray;
                Check(summaryWidgets != null && summaryWidgets.Count == 1
                      && summaryWidgets[0]["Notes"] == null,
                    "config service: get summary omits note bodies");

                JObject full = service.Get(new JObject { ["view"] = "full" });
                Check((string)full["view"] == "full"
                      && full["config"]?["Widgets"] is JArray fullWidgets
                      && fullWidgets[0]["Notes"] is JArray,
                    "config service: get full keeps note bodies");

                // --- no-op patch ---
                string revision = service.CurrentRevision;
                JObject noop = service.Patch(new JObject
                {
                    ["patch"] = JObject.Parse(@"{""Theme"":""dark""}")
                }, revision);
                Check(noop["wouldChange"] != null && !(bool)noop["wouldChange"],
                    "config patch: a no-op reports wouldChange false");
                Check(((JArray)noop["changes"]).Count == 0,
                    "config patch: a no-op reports no changes");
                Check((string)noop["revision"] == revision && applyCount == 0,
                    "config patch: a no-op does not apply or save");
                Check(service.CurrentRevision == revision,
                    "config patch: a no-op leaves the revision unchanged");

                // --- theme patch ---
                JObject applied = service.Patch(new JObject
                {
                    ["patch"] = JObject.Parse(@"{""Theme"":""light""}")
                }, revision);
                var appliedChanges = (JArray)applied["changes"];
                Check((bool)applied["wouldChange"]
                      && appliedChanges.Select(x => (string)x)
                          .SequenceEqual(new[] { "Theme" }),
                    "config patch: Theme is the only reported change");
                Check(live.Theme == "light" && applyCount == 1,
                    "config patch: applies the merged config");
                Check((string)applied["revision"] == service.CurrentRevision
                      && service.CurrentRevision != revision,
                    "config patch: returns the new revision");
                Check(applied["restartRequired"] != null && !(bool)applied["restartRequired"],
                    "config patch: a hot-field change needs no restart");
                Check(System.IO.Directory.GetFiles(backupDirectory, "config-*.json").Length == 1,
                    "config patch: backs up the live config before applying");

                // --- revision guard ---
                bool conflicted = false;
                try
                {
                    service.Patch(new JObject
                    {
                        ["patch"] = JObject.Parse(@"{""Theme"":""dark""}")
                    }, revision);
                }
                catch (ControlException ex)
                {
                    conflicted = ex.Code == ControlErrorCodes.Conflict
                        && (string)ex.Details["currentRevision"] == service.CurrentRevision;
                }
                Check(conflicted,
                    "config patch: a stale baseRevision conflicts with the current revision");
                Check(live.Theme == "light" && applyCount == 1,
                    "config patch: a conflicting patch changes nothing");

                bool revisionRequired = false;
                try
                {
                    service.Patch(new JObject { ["patch"] = new JObject() }, null);
                }
                catch (ControlException ex)
                {
                    revisionRequired = ex.Code == ControlErrorCodes.RevisionRequired;
                }
                Check(revisionRequired, "config patch: a missing baseRevision is rejected");

                // --- structural fields are forbidden ---
                foreach (string forbidden in new[]
                         {
                             "Widgets", "Groups", "Folders", "FrequentFiles", "SchemaVersion"
                         })
                {
                    bool rejected = false;
                    try
                    {
                        service.Patch(new JObject
                        {
                            ["patch"] = new JObject { [forbidden] = new JArray() }
                        }, service.CurrentRevision);
                    }
                    catch (ControlException ex)
                    {
                        rejected = ex.Code == ControlErrorCodes.Forbidden;
                    }
                    Check(rejected, "config patch: " + forbidden + " is forbidden");
                }
                Check(live.Widgets.Count == 1 && applyCount == 1,
                    "config patch: forbidden patches change nothing");

                // --- dryRun ---
                string beforeDryRun = service.CurrentRevision;
                int backupsBeforeDryRun = System.IO.Directory
                    .GetFiles(backupDirectory, "config-*.json").Length;
                JObject dryRun = service.Patch(new JObject
                {
                    ["patch"] = JObject.Parse(@"{""Theme"":""dark""}"),
                    ["dryRun"] = true
                }, beforeDryRun);
                Check((bool)dryRun["wouldChange"]
                      && ((JArray)dryRun["changes"]).Any(x => (string)x == "Theme"),
                    "config patch: dryRun reports the changes");
                Check(live.Theme == "light" && applyCount == 1,
                    "config patch: dryRun does not apply");
                Check((string)dryRun["revision"] == beforeDryRun
                      && System.IO.Directory.GetFiles(backupDirectory, "config-*.json").Length
                          == backupsBeforeDryRun,
                    "config patch: dryRun neither saves nor backs up");

                // --- RFC 7386 null removal ---
                live.IslandX = 640.0;
                JObject islandRemoved = service.Patch(new JObject
                {
                    ["patch"] = JObject.Parse(@"{""IslandX"":null}")
                }, service.CurrentRevision);
                Check(live.IslandX == null
                      && ((JArray)islandRemoved["changes"]).Any(x => (string)x == "IslandX"),
                    "config patch: null removes IslandX so the island centers");
                JObject islandNoop = service.Patch(new JObject
                {
                    ["patch"] = JObject.Parse(@"{""IslandX"":null}")
                }, service.CurrentRevision);
                Check(islandNoop["wouldChange"] != null && !(bool)islandNoop["wouldChange"],
                    "config patch: removing an absent IslandX is a no-op");

                live.FileSearch = FileSearchSettings.CreateDefault();
                JObject searchCleared = service.Patch(new JObject
                {
                    ["patch"] = JObject.Parse(@"{""FileSearch"":null}")
                }, service.CurrentRevision);
                Check(live.FileSearch == null
                      && ((JArray)searchCleared["changes"]).Any(x => (string)x == "FileSearch"),
                    "config patch: null clears the FileSearch block");

                live.FileSearch = FileSearchSettings.CreateDefault();
                string beforeNested = service.CurrentRevision;
                JObject nested = service.Patch(new JObject
                {
                    ["patch"] = JObject.Parse(@"{""FileSearch"":{""Enabled"":false}}")
                }, beforeNested);
                var nestedChanges = (JArray)nested["changes"];
                Check(nestedChanges.Count == 1 && (string)nestedChanges[0] == "FileSearch",
                    "config patch: a nested FileSearch edit is one top-level change");
                Check(live.FileSearch != null && !live.FileSearch.Enabled
                      && live.FileSearch.Types != null
                      && live.FileSearch.Types.Length == FileSearchSettings.DefaultTypes.Length,
                    "config patch: merge patch keeps the other FileSearch fields");
                JObject nestedNoop = service.Patch(new JObject
                {
                    ["patch"] = JObject.Parse(@"{""FileSearch"":{""Enabled"":false}}")
                }, service.CurrentRevision);
                Check(nestedNoop["wouldChange"] != null && !(bool)nestedNoop["wouldChange"],
                    "config patch: an unchanged nested FileSearch edit is a no-op");

                // --- config.validate ---
                string beforeValidate = service.CurrentRevision;
                JObject validDocument = service.Validate(new JObject
                {
                    ["document"] = JObject.Parse(@"{""Theme"":""light"",""Widgets"":[]}")
                });
                Check((bool)validDocument["ok"]
                      && validDocument["normalizedSummary"] is JObject validSummary
                      && (string)validSummary["Theme"] == "light",
                    "config validate: a document returns ok and a normalized summary");
                JObject invalidDocument = service.Validate(new JObject
                {
                    ["document"] = @"{""Widgets"":null}"
                });
                Check(invalidDocument["ok"] != null && !(bool)invalidDocument["ok"]
                      && ((JArray)invalidDocument["warnings"]).Count > 0,
                    "config validate: an invalid document reports ok false with warnings");
                JObject patchedDocument = service.Validate(new JObject
                {
                    ["patch"] = JObject.Parse(@"{""PanelOpacity"":0.5}")
                });
                Check((bool)patchedDocument["ok"]
                      && (double)patchedDocument["normalizedSummary"]["PanelOpacity"] == 0.5,
                    "config validate: a patch validates against the current config");
                Check(service.CurrentRevision == beforeValidate && live.PanelOpacity != 0.5,
                    "config validate: never applies to the live config");

                // --- config.backup ---
                int applyBeforeBackup = applyCount;
                string backupPath = service.Backup("manual pin");
                var backupFile = new System.IO.FileInfo(backupPath);
                Check(backupFile.Exists
                      && string.Equals(backupFile.DirectoryName, backupDirectory,
                          StringComparison.OrdinalIgnoreCase),
                    "config backup: writes under the backup directory");
                Check(backupFile.Name.StartsWith("config-", StringComparison.Ordinal)
                      && backupFile.Name.EndsWith(".json", StringComparison.Ordinal)
                      && backupFile.Name.Contains("-manual-pin"),
                    "config backup: names config-<stamp>-<label>.json");
                var backupJson = (JObject)JToken.Parse(System.IO.File.ReadAllText(backupPath));
                Check(backupJson["Widgets"] is JArray backupWidgets
                      && backupWidgets[0]["Notes"] is JArray,
                    "config backup: serializes the live config in full");
                Check(applyCount == applyBeforeBackup,
                    "config backup: does not touch the live config");

                string sanitized = service.Backup("before/agent: x");
                string sanitizedLabel = System.IO.Path.GetFileName(sanitized);
                Check(sanitizedLabel.Contains("-before-agent-x")
                      && sanitizedLabel.IndexOf('/') < 0
                      && sanitizedLabel.IndexOf(':') < 0,
                    "config backup: sanitizes the label");

                // --- retention ---
                string retentionDirectory = System.IO.Path.Combine(root, "retention");
                var retention = new ConfigControlService(
                    () => live,
                    next => new JObject(),
                    configPath,
                    retentionDirectory);
                var backupNames = new List<string>();
                for (int i = 0; i < 21; i++) backupNames.Add(retention.Backup("keep"));
                Check(backupNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 21,
                    "config backup: 21 backups all get unique names");
                Check(System.IO.Directory.GetFiles(retentionDirectory, "config-*.json").Length == 20,
                    "config backup: keeps only the newest 20");
                Check(System.IO.File.Exists(backupNames[20]),
                    "config backup: the newest backup survives retention");
            }
            finally
            {
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
        }

        private static void ConfigControlRoutingPins()
        {
            string root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "kobold-control-router-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                AppConfig live = new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion };
                string configPath = System.IO.Path.Combine(root, "config.json");
                string backupDirectory = System.IO.Path.Combine(root, "backups");
                var service = new ConfigControlService(
                    () => live,
                    next =>
                    {
                        live = next;
                        return new JObject();
                    },
                    configPath,
                    backupDirectory);
                var router = new ControlRequestRouter(service);

                ControlResponse get = router.Handle(new ControlRequest
                {
                    V = 1, Id = "get", Method = "config.get"
                });
                Check(get.Ok && get.Result["revision"] != null
                      && get.Result["config"] is JObject,
                    "router: config.get routes to the service");

                ControlResponse describe = router.Handle(new ControlRequest
                {
                    V = 1, Id = "describe", Method = "config.describe"
                });
                Check(describe.Ok && describe.Result["topLevel"] is JArray,
                    "router: config.describe routes to the schema");

                ControlResponse validate = router.Handle(new ControlRequest
                {
                    V = 1,
                    Id = "validate",
                    Method = "config.validate",
                    Params = new JObject
                    {
                        ["patch"] = JObject.Parse(@"{""Theme"":""light""}")
                    }
                });
                Check(validate.Ok && (bool)validate.Result["ok"],
                    "router: config.validate routes to the service");

                ControlResponse patch = router.Handle(new ControlRequest
                {
                    V = 1,
                    Id = "patch",
                    Method = "config.patch",
                    Params = new JObject
                    {
                        ["patch"] = JObject.Parse(@"{""Theme"":""light""}"),
                        ["baseRevision"] = "ignored"
                    },
                    BaseRevision = service.CurrentRevision
                });
                Check(patch.Ok && live.Theme == "light",
                    "router: config.patch uses request.BaseRevision");

                live.Theme = "dark";
                ControlResponse missing = router.Handle(new ControlRequest
                {
                    V = 1,
                    Id = "missing",
                    Method = "config.patch",
                    Params = new JObject
                    {
                        ["patch"] = JObject.Parse(@"{""Theme"":""light""}"),
                        ["baseRevision"] = service.CurrentRevision
                    }
                });
                Check(!missing.Ok && missing.Error.Code == ControlErrorCodes.RevisionRequired,
                    "router: config.patch does not read baseRevision from params");
                Check(live.Theme == "dark",
                    "router: a rejected config.patch changes nothing");

                ControlResponse backup = router.Handle(new ControlRequest
                {
                    V = 1,
                    Id = "backup",
                    Method = "config.backup",
                    Params = new JObject { ["label"] = "routed" }
                });
                Check(backup.Ok && (string)backup.Result["path"] != null
                      && System.IO.File.Exists((string)backup.Result["path"]),
                    "router: config.backup writes a snapshot");
            }
            finally
            {
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
        }

        private static void ConfigReplaceAndRestorePins()
        {
            int parsedPid;
            string parsedExe;
            Check(RestartHelper.TryParse(
                      new[] { "--restart-helper", "123", @"C:\Apps\Kobold.exe" },
                      out parsedPid, out parsedExe)
                  && parsedPid == 123
                  && parsedExe == @"C:\Apps\Kobold.exe",
                "restart helper: parses the helper arguments");
            Check(!RestartHelper.TryParse(new[] { "--restart-helper", "123" },
                      out parsedPid, out parsedExe)
                  && parsedPid == 0 && parsedExe == null,
                "restart helper: requires exactly the pid and the exe path");
            Check(!RestartHelper.TryParse(new[] { "--other", "123", @"C:\Kobold.exe" },
                      out parsedPid, out parsedExe),
                "restart helper: rejects another mode");
            Check(!RestartHelper.TryParse(new[] { "--restart-helper", "abc", @"C:\Kobold.exe" },
                      out parsedPid, out parsedExe),
                "restart helper: rejects a non-numeric pid");
            Check(!RestartHelper.TryParse(new[] { "--restart-helper", "0", @"C:\Kobold.exe" },
                      out parsedPid, out parsedExe),
                "restart helper: rejects a non-positive pid");
            Check(!RestartHelper.TryParse(new[] { "--restart-helper", "7", "  " },
                      out parsedPid, out parsedExe),
                "restart helper: rejects a blank exe path");
            Check(!RestartHelper.TryParse(null, out parsedPid, out parsedExe),
                "restart helper: rejects a missing argument list");

            var legacy = JsonConvert.DeserializeObject<AppConfig>(
                @"{""Folders"":[{""Name"":""Legacy"",""Items"":[]}]}");
            legacy.MigrateLegacyForControl();
            Check(legacy.Folders == null && legacy.Widgets.Count == 1
                  && legacy.Widgets[0] is FolderData
                  && legacy.Widgets[0].Name == "Legacy",
                "config replace: a legacy Folders document migrates into Widgets before a write");

            string root = Path.Combine(
                Path.GetTempPath(), "kobold-control-replace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var live = new AppConfig
                {
                    SchemaVersion = AppConfig.CurrentSchemaVersion,
                    Folders = null
                };
                live.Widgets.Add(new NoteData { Id = "w1", Name = "Current" });

                string configPath = Path.Combine(root, "config.json");
                string backupDirectory = Path.Combine(root, "backups");
                Directory.CreateDirectory(backupDirectory);
                int applyDocumentCount = 0;
                int hotApplyCount = 0;
                int beforeRestartCount = 0;
                int launchCount = 0;
                int launchedPid = 0;
                string launchedExe = null;
                bool launchResult = true;
                AppConfig appliedDocument = null;
                var order = new List<string>();

                var service = new ConfigControlService(
                    () => live,
                    next =>
                    {
                        hotApplyCount++;
                        live = next;
                        return new JObject();
                    },
                    configPath,
                    backupDirectory,
                    next =>
                    {
                        applyDocumentCount++;
                        appliedDocument = next;
                        live = next;
                        order.Add("apply");
                        return new JObject();
                    },
                    (pid, exe) =>
                    {
                        launchCount++;
                        launchedPid = pid;
                        launchedExe = exe;
                        order.Add("launch");
                        return launchResult;
                    },
                    () =>
                    {
                        beforeRestartCount++;
                        order.Add("beforeRestart");
                    });

                string structuralJson =
                    @"{""Widgets"":[{""Kind"":""note"",""Id"":""w2"",""Name"":""Next"",""Notes"":[]}]}";
                string revision = service.CurrentRevision;

                JObject guarded = service.Replace(new JObject
                {
                    ["document"] = structuralJson
                }, revision);
                Check((bool)guarded["wouldChange"]
                      && ((JArray)guarded["changes"]).Select(x => (string)x).Contains("Widgets")
                      && (bool)guarded["restartRequired"]
                      && !(bool)guarded["wrote"]
                      && !(bool)guarded["restarting"]
                      && (string)guarded["revision"] == revision,
                    "config replace: a structural change reports restartRequired and writes nothing");
                Check(!((JArray)guarded["changes"]).Select(x => (string)x)
                          .Contains("SchemaVersion"),
                    "config replace: a document without SchemaVersion reports no phantom schema change");
                Check(applyDocumentCount == 0 && hotApplyCount == 0
                      && Directory.GetFiles(backupDirectory, "config-*.json").Length == 0,
                    "config replace: a structural no-restart request neither applies nor backs up");

                JObject dry = service.Replace(new JObject
                {
                    ["document"] = structuralJson,
                    ["dryRun"] = true,
                    ["restart"] = true
                }, revision);
                Check((bool)dry["wouldChange"] && (bool)dry["restartRequired"]
                      && !(bool)dry["wrote"] && !(bool)dry["restarting"]
                      && (string)dry["revision"] == revision
                      && applyDocumentCount == 0 && launchCount == 0
                      && Directory.GetFiles(backupDirectory, "config-*.json").Length == 0,
                    "config replace: dryRun reports the structural change without writing or restarting");

                bool stale = false;
                try
                {
                    service.Replace(new JObject { ["document"] = structuralJson }, "sha256:stale");
                }
                catch (ControlException ex)
                {
                    stale = ex.Code == ControlErrorCodes.Conflict
                        && (string)ex.Details["currentRevision"] == service.CurrentRevision;
                }
                Check(stale && applyDocumentCount == 0 && launchCount == 0,
                    "config replace: a stale baseRevision conflicts before any write");

                JObject restarted = service.Replace(new JObject
                {
                    ["document"] = structuralJson,
                    ["restart"] = true
                }, service.CurrentRevision);
                Check((bool)restarted["wouldChange"] && (bool)restarted["restartRequired"]
                      && (bool)restarted["wrote"] && (bool)restarted["restarting"]
                      && restarted["restartFailed"] == null
                      && (string)restarted["revision"] == service.CurrentRevision,
                    "config replace: restart:true applies the document and reports restarting");
                Check(applyDocumentCount == 1 && beforeRestartCount == 1 && launchCount == 1
                      && order.SequenceEqual(new[] { "beforeRestart", "apply", "launch" }),
                    "config replace: the desktop guard runs before the write and the launcher");
                Check(appliedDocument != null && appliedDocument.Widgets.Count == 1
                      && appliedDocument.Widgets[0].Id == "w2"
                      && appliedDocument.Folders == null,
                    "config replace: the whole parsed document is applied");
                string[] backups = Directory.GetFiles(backupDirectory, "config-*.json");
                Check(backups.Length == 1
                      && Path.GetFileName(backups[0]).Contains("-before-replace"),
                    "config replace: the current config is backed up as before-replace");
                Check(live.Widgets.Count == 1 && live.Widgets[0].Id == "w2" && hotApplyCount == 0,
                    "config replace: a structural replace never uses the hot apply path");
                Check(launchedPid > 0 && !string.IsNullOrEmpty(launchedExe),
                    "config replace: the launcher receives the current process id and exe path");

                launchResult = false;
                string failingJson =
                    @"{""Widgets"":[{""Kind"":""note"",""Id"":""w3"",""Name"":""Third"",""Notes"":[]}]}";
                JObject failed = service.Replace(new JObject
                {
                    ["document"] = failingJson,
                    ["restart"] = true
                }, service.CurrentRevision);
                Check((bool)failed["wrote"] && !(bool)failed["restarting"]
                      && (bool)failed["restartFailed"] && (bool)failed["restartRequired"],
                    "config replace: a failed launcher reports restartFailed after writing");
                Check(applyDocumentCount == 2 && beforeRestartCount == 2 && launchCount == 2
                      && live.Widgets[0].Id == "w3",
                    "config replace: a failed restart keeps the written document");

                JObject hotDocument = JObject.Parse(ConfigDocument.ToJson(live, full: true));
                hotDocument["Theme"] = "light";
                int backupsBeforeHot = Directory.GetFiles(backupDirectory, "config-*.json").Length;
                JObject hot = service.Replace(new JObject
                {
                    ["document"] = hotDocument
                }, service.CurrentRevision);
                Check((bool)hot["wouldChange"] && !(bool)hot["restartRequired"]
                      && (bool)hot["wrote"] && !(bool)hot["restarting"]
                      && hotApplyCount == 1 && applyDocumentCount == 2 && launchCount == 2
                      && live.Theme == "light"
                      && Directory.GetFiles(backupDirectory, "config-*.json").Length
                          == backupsBeforeHot + 1,
                    "config replace: a hot-only change uses the hot apply path with a backup");

                JObject noopDocument = JObject.Parse(ConfigDocument.ToJson(live, full: true));
                int backupsBeforeNoop = Directory.GetFiles(backupDirectory, "config-*.json").Length;
                JObject noop = service.Replace(new JObject
                {
                    ["document"] = noopDocument
                }, service.CurrentRevision);
                Check(noop["wouldChange"] != null && !(bool)noop["wouldChange"]
                      && !(bool)noop["wrote"] && !(bool)noop["restartRequired"]
                      && !(bool)noop["restarting"]
                      && hotApplyCount == 1 && applyDocumentCount == 2
                      && Directory.GetFiles(backupDirectory, "config-*.json").Length
                          == backupsBeforeNoop,
                    "config replace: an unchanged document writes nothing");

                // --- restart ordering: the desktop guard must see the old
                //     HideDesktopIcons value, not the replacement's value.
                string guardRoot = Path.Combine(root, "guard-order");
                Directory.CreateDirectory(guardRoot);
                var guardLive = new AppConfig
                {
                    SchemaVersion = AppConfig.CurrentSchemaVersion,
                    Folders = null,
                    HideDesktopIcons = true
                };
                guardLive.Widgets.Add(new NoteData { Id = "g1", Name = "Guarded" });
                string guardBackupDirectory = Path.Combine(guardRoot, "backups");
                Directory.CreateDirectory(guardBackupDirectory);

                bool oldPreferenceSeen = false;
                var guardOrder = new List<string>();
                var guardService = new ConfigControlService(
                    () => guardLive,
                    next =>
                    {
                        guardLive = next;
                        return new JObject();
                    },
                    Path.Combine(guardRoot, "config.json"),
                    guardBackupDirectory,
                    next =>
                    {
                        guardOrder.Add("apply");
                        guardLive = next;
                        return new JObject();
                    },
                    (pid, exe) =>
                    {
                        guardOrder.Add("launch");
                        return true;
                    },
                    () =>
                    {
                        guardOrder.Add("beforeRestart");
                        oldPreferenceSeen = guardLive.HideDesktopIcons;
                    });

                JObject guardRestart = guardService.Replace(new JObject
                {
                    ["document"] = @"{""HideDesktopIcons"":false,""Widgets"":[{""Kind"":""note"",""Id"":""g2"",""Name"":""GuardNext"",""Notes"":[]}]}",
                    ["restart"] = true
                }, guardService.CurrentRevision);
                Check((bool)guardRestart["restarting"] && oldPreferenceSeen,
                    "config replace: the desktop guard sees the old HideDesktopIcons value");
                Check(guardOrder.SequenceEqual(new[] { "beforeRestart", "apply", "launch" }),
                    "config replace: the desktop guard runs before the document is applied");

                JObject newerSchemaDocument =
                    JObject.Parse(ConfigDocument.ToJson(guardLive, full: true));
                newerSchemaDocument["SchemaVersion"] = 99;
                JObject newerSchema = guardService.Replace(new JObject
                {
                    ["document"] = newerSchemaDocument
                }, guardService.CurrentRevision);
                Check((bool)newerSchema["wouldChange"]
                      && ((JArray)newerSchema["changes"]).Select(x => (string)x)
                          .Contains("SchemaVersion")
                      && guardLive.SchemaVersion == 99,
                    "config replace: an explicit newer SchemaVersion is left untouched");

                bool outsideForbidden = false;
                try
                {
                    service.Restore(new JObject { ["backup"] = configPath }, service.CurrentRevision);
                }
                catch (ControlException ex)
                {
                    outsideForbidden = ex.Code == ControlErrorCodes.Forbidden;
                }
                Check(outsideForbidden,
                    "config restore: a path outside the backup directory is forbidden");

                bool traversalForbidden = false;
                try
                {
                    service.Restore(new JObject
                    {
                        ["backup"] = Path.Combine(backupDirectory, "..", "config.json")
                    }, service.CurrentRevision);
                }
                catch (ControlException ex)
                {
                    traversalForbidden = ex.Code == ControlErrorCodes.Forbidden;
                }
                Check(traversalForbidden,
                    "config restore: parent traversal is forbidden");

                bool missingBackup = false;
                try
                {
                    service.Restore(new JObject(), service.CurrentRevision);
                }
                catch (ControlException ex)
                {
                    missingBackup = ex.Code == ControlErrorCodes.ValidationFailed;
                }
                Check(missingBackup, "config restore: the backup path is required");

                launchResult = true;
                string restoreJson =
                    @"{""Widgets"":[{""Kind"":""note"",""Id"":""w9"",""Name"":""Restored"",""Notes"":[]}]}";
                string restorePath = Path.Combine(backupDirectory, "config-restore-pin.json");
                File.WriteAllText(restorePath, restoreJson);
                int backupsBeforeRestore =
                    Directory.GetFiles(backupDirectory, "config-*.json").Length;

                JObject restored = service.Restore(new JObject
                {
                    ["backup"] = restorePath,
                    ["restart"] = true
                }, service.CurrentRevision);
                Check((bool)restored["wrote"] && (bool)restored["restarting"]
                      && applyDocumentCount == 3 && launchCount == 3
                      && live.Widgets.Count == 1 && live.Widgets[0].Id == "w9",
                    "config restore: applies the backup document through the replace path");
                Check(Directory.GetFiles(backupDirectory, "config-*.json").Length
                          == backupsBeforeRestore + 1
                      && Directory.GetFiles(backupDirectory, "config-*.json")
                          .Any(path => Path.GetFileName(path).Contains("-before-replace")),
                    "config restore: backs up the current config before applying");
                Check(File.Exists(restorePath) && File.ReadAllText(restorePath) == restoreJson,
                    "config restore: the source backup is left intact");

                var router = new ControlRequestRouter(service);
                string routedJson =
                    @"{""Widgets"":[{""Kind"":""note"",""Id"":""w5"",""Name"":""Routed"",""Notes"":[]}]}";
                ControlResponse preview = router.Handle(new ControlRequest
                {
                    V = 1,
                    Id = "replace-preview",
                    Method = "config.replace",
                    Params = new JObject { ["document"] = routedJson },
                    BaseRevision = service.CurrentRevision
                });
                Check(preview.Ok && (bool)preview.Result["restartRequired"]
                      && preview.AfterWrite == null,
                    "router: config.replace without restart attaches no after-write action");

                ControlResponse routed = router.Handle(new ControlRequest
                {
                    V = 1,
                    Id = "replace-restart",
                    Method = "config.replace",
                    Params = new JObject
                    {
                        ["document"] = routedJson,
                        ["restart"] = true
                    },
                    BaseRevision = service.CurrentRevision
                });
                Check(routed.Ok && (bool)routed.Result["restarting"]
                      && routed.AfterWrite != null,
                    "router: config.replace attaches the after-write restart");

                ControlResponse routedRestore = router.Handle(new ControlRequest
                {
                    V = 1,
                    Id = "restore-restart",
                    Method = "config.restore",
                    Params = new JObject
                    {
                        ["backup"] = restorePath,
                        ["restart"] = true
                    },
                    BaseRevision = service.CurrentRevision
                });
                Check(routedRestore.Ok && (bool)routedRestore.Result["restarting"]
                      && routedRestore.AfterWrite != null,
                    "router: config.restore passes the revision and attaches the restart");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void DesktopGuardAndServicePins()
        {
            Check(DesktopIconGuard.Policy(originalHidden: true, currentHidden: true, hideDesktopIcons: true)
                  == DesktopIconAction.Show,
                "desktop guard: a hidden run shows icons on exit");
            Check(DesktopIconGuard.Policy(originalHidden: true, currentHidden: true, hideDesktopIcons: false)
                  == DesktopIconAction.None,
                "desktop guard: an unhidden run does nothing");
            Check(DesktopIconGuard.Policy(originalHidden: false, currentHidden: true, hideDesktopIcons: true)
                  == DesktopIconAction.Show,
                "desktop guard: a run that started with visible icons still shows them");
            Check(DesktopIconGuard.Policy(originalHidden: true, currentHidden: false, hideDesktopIcons: true)
                  == DesktopIconAction.None,
                "desktop guard: already-visible icons need no action");

            DesktopIconAction first =
                DesktopIconGuard.Policy(originalHidden: true, currentHidden: true, hideDesktopIcons: true);
            DesktopIconAction second =
                DesktopIconGuard.Policy(originalHidden: true, currentHidden: true, hideDesktopIcons: true);
            Check(first == second && first == DesktopIconAction.Show,
                "desktop guard: the policy is idempotent");

            Func<bool, bool> originalStartup = StartupRegistryService.Applier;
            Func<bool, bool> originalQuickAccess = QuickAccessService.Applier;
            try
            {
                bool? startupArgument = null;
                StartupRegistryService.Applier = value =>
                {
                    startupArgument = value;
                    return true;
                };
                Check(StartupRegistryService.Apply(true) && startupArgument == true,
                    "startup registry: a successful fake applies and reports true");
                StartupRegistryService.Applier = value => false;
                Check(!StartupRegistryService.Apply(false),
                    "startup registry: a failing fake reports false");

                bool? quickAccessArgument = null;
                QuickAccessService.Applier = value =>
                {
                    quickAccessArgument = value;
                    return true;
                };
                Check(QuickAccessService.Apply(false) && quickAccessArgument == false,
                    "quick access: a successful fake applies and reports true");
                QuickAccessService.Applier = value => false;
                Check(!QuickAccessService.Apply(true),
                    "quick access: a failing fake reports false");
            }
            finally
            {
                StartupRegistryService.Applier = originalStartup;
                QuickAccessService.Applier = originalQuickAccess;
            }

            JObject allApplied = ConfigApplyService.CollectWarnings(true, true, true);
            Check(allApplied.Count == 0,
                "config apply: successful appliers produce no warnings");

            JObject startupFailed = ConfigApplyService.CollectWarnings(false, true, true);
            Check(startupFailed["StartWithWindows"] != null && startupFailed.Count == 1,
                "config apply: a failed startup applier warns on StartWithWindows");

            JObject quickAccessFailed = ConfigApplyService.CollectWarnings(true, false, true);
            Check(quickAccessFailed["PinStorageToQuickAccess"] != null
                  && quickAccessFailed.Count == 1,
                "config apply: a failed Quick Access applier warns on PinStorageToQuickAccess");

            JObject desktopFailed = ConfigApplyService.CollectWarnings(true, true, false);
            Check(desktopFailed["HideDesktopIcons"] != null && desktopFailed.Count == 1,
                "config apply: a failed desktop-icon applier warns on HideDesktopIcons");

            JObject twoFailed = ConfigApplyService.CollectWarnings(false, false, true);
            Check(twoFailed.Count == 2 && twoFailed["StartWithWindows"] != null
                  && twoFailed["PinStorageToQuickAccess"] != null,
                "config apply: every failed applier contributes its own warning");
        }

        private static JArray AllowedOf(JObject describe, string name)
        {
            var entry = ((JArray)describe["topLevel"]).OfType<JObject>()
                .FirstOrDefault(x => (string)x["name"] == name);
            return entry?["allowed"] as JArray;
        }

        private static bool AllowedMatches(JObject describe, string name, params string[] expected)
        {
            var allowed = AllowedOf(describe, name);
            return allowed != null && allowed.Select(x => (string)x).SequenceEqual(expected);
        }

        private static JArray RangeOf(JObject describe, string name)
        {
            var entry = ((JArray)describe["topLevel"]).OfType<JObject>()
                .FirstOrDefault(x => (string)x["name"] == name);
            return entry?["range"] as JArray;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }
    }
}
