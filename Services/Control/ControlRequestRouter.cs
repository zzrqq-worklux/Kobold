using System;
using System.IO;
using System.Reflection;
using System.Windows;
using Kobold.Core;
using Kobold.Core.Control;
using Newtonsoft.Json.Linq;

namespace Kobold.Services.Control
{
    /// <summary>
    /// Maps one protocol request to its response. Later tasks add methods here;
    /// every handler either returns a result or throws <see cref="ControlException"/>
    /// with a protocol error code.
    /// </summary>
    public sealed class ControlRequestRouter
    {
        private readonly DiagnosticsService _diagnostics = new DiagnosticsService();

        // One service for the whole app; created on the first config command so
        // diag requests never force the config to load.
        private static readonly Lazy<ConfigControlService> SharedConfig =
            new Lazy<ConfigControlService>(CreateConfigService);

        // Widget/group commands only construct the manager once the app already
        // owns one; diag/config reads never force a widget tree into existence.
        private static readonly Lazy<WidgetGroupControlService> SharedWidgets =
            new Lazy<WidgetGroupControlService>(
                () => new WidgetGroupControlService(WidgetManager.Instance));

        private readonly ConfigControlService _config;

        public ControlRequestRouter()
        {
        }

        /// <summary>Test seam: the checks inject a sandboxed service.</summary>
        internal ControlRequestRouter(ConfigControlService config)
        {
            _config = config;
        }

        private ConfigControlService ConfigService
        {
            get { return _config ?? SharedConfig.Value; }
        }

        private static WidgetGroupControlService WidgetService
        {
            get { return SharedWidgets.Value; }
        }

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
                    case "diag.logs.tail":
                        return Ok(request, _diagnostics.TailLog(
                            StringParam(request, "name"),
                            IntParam(request, "lines", 200)));
                    case "diag.search.status":
                        return Ok(request, _diagnostics.SearchStatus());
                    case "diag.search.query":
                        return Ok(request, _diagnostics.SearchQuery(
                            StringParam(request, "text"),
                            IntParam(request, "limit", 50)));
                    case "diag.folders":
                        return Ok(request, _diagnostics.Folders());
                    case "search.rebuild":
                        RequireConfirm(request);
                        return Ok(request, _diagnostics.RebuildSearch());
                    case "search.cancel":
                        return Ok(request, _diagnostics.CancelSearchScan());
                    case "search.pause":
                        return Ok(request, _diagnostics.PauseSearch());
                    case "search.resume":
                        return Ok(request, _diagnostics.ResumeSearch());
                    case "search.reconcile":
                        return Ok(request, _diagnostics.ReconcileSearch());
                    case "app.showIsland":
                        WidgetManager.Instance.ShowIsland();
                        return Ok(request, new JObject { ["shown"] = true });
                    case "config.get":
                        return Ok(request, ConfigService.Get(request.Params ?? new JObject()));
                    case "config.describe":
                        return Ok(request, ConfigDescribe.Build());
                    case "config.validate":
                        return Ok(request, ConfigService.Validate(request.Params ?? new JObject()));
                    case "config.patch":
                        // The revision guard lives on the envelope, never in params.
                        return Ok(request, ConfigService.Patch(
                            request.Params ?? new JObject(), request.BaseRevision));
                    case "config.replace":
                        return ConfigWrite(request, ConfigService.Replace(
                            request.Params ?? new JObject(), request.BaseRevision));
                    case "config.restore":
                        return ConfigWrite(request, ConfigService.Restore(
                            request.Params ?? new JObject(), request.BaseRevision));
                    case "config.backup":
                        {
                            JToken labelToken = request.Params == null
                                ? null
                                : request.Params["label"];
                            string label = labelToken != null
                                && labelToken.Type == JTokenType.String
                                ? (string)labelToken
                                : null;
                            return Ok(request, new JObject
                            {
                                ["path"] = ConfigService.Backup(label)
                            });
                        }
                    case "widget.add":
                        return Ok(request, WidgetService.Add(request.Params ?? new JObject()));
                    case "widget.update":
                        return Ok(request, WidgetService.Update(request.Params ?? new JObject()));
                    case "widget.delete":
                        return Ok(request, WidgetService.Delete(request.Params ?? new JObject()));
                    case "group.merge":
                        return Ok(request, WidgetService.Merge(request.Params ?? new JObject()));
                    case "group.detach":
                        return Ok(request, WidgetService.Detach(request.Params ?? new JObject()));
                    case "group.reorder":
                        return Ok(request, WidgetService.Reorder(request.Params ?? new JObject()));
                    case "group.dissolve":
                        return Ok(request, WidgetService.Dissolve(request.Params ?? new JObject()));
                    default:
                        return Error(request, ControlErrorCodes.UnknownMethod, "Unknown method.");
                }
            }
            catch (ControlException ex)
            {
                return Error(request, ex.Code, ex.Message, ex.Details);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("control router error: " + ex.Message);
                return Error(request, ControlErrorCodes.Internal, ex.Message);
            }
        }

        /// <summary>
        /// Wires the app's one config service to the live manager. Task 5
        /// replaces <see cref="ApplyHotFields"/> with ConfigApplyService.
        /// </summary>
        private static ConfigControlService CreateConfigService()
        {
            return new ConfigControlService(
                () => WidgetManager.Instance.Config,
                ApplyHotFields,
                Utils.GetConfigPath(),
                Path.Combine(Utils.GetAppDataPath(), "backups"),
                ApplyDocument,
                RestartHelper.Launch,
                DesktopIconGuard.RestoreOnExit);
        }

        /// <summary>
        /// The single apply seam: ConfigApplyService copies the hot fields and
        /// pushes their side effects without saving; the router lands one
        /// synchronous save afterwards. The service is stateless, so building
        /// it per request is deliberate.
        /// </summary>
        private static JObject ApplyHotFields(AppConfig next)
        {
            WidgetManager manager = WidgetManager.Instance;
            JObject warnings = new ConfigApplyService(manager).Apply(next);
            manager.SaveConfigNow();
            return warnings;
        }

        /// <summary>
        /// Structural replace: copy every config property (widgets, groups,
        /// legacy folders, frequent files and file-search settings included)
        /// into the live instance and land one save. The app is about to
        /// restart, so no hot-field side effects are pushed here.
        /// </summary>
        private static JObject ApplyDocument(AppConfig next)
        {
            WidgetManager manager = WidgetManager.Instance;
            AppConfig live = manager.Config;
            if (live == null || next == null) return new JObject();

            foreach (PropertyInfo property in typeof(AppConfig).GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite) continue;
                if (property.GetIndexParameters().Length > 0) continue;

                property.SetValue(live, property.GetValue(next, null), null);
            }

            manager.SaveConfigNow();
            return new JObject();
        }

        /// <summary>
        /// Wraps a config write response. Only a response the service marked
        /// as restarting carries an after-write action: the server runs it once
        /// the reply line is on the wire, and it shuts down on the UI thread.
        /// </summary>
        private static ControlResponse ConfigWrite(ControlRequest request, JObject result)
        {
            ControlResponse response = Ok(request, result);

            JToken restarting = result == null ? null : result["restarting"];
            if (restarting == null || restarting.Type != JTokenType.Boolean
                || !restarting.Value<bool>())
            {
                return response;
            }

            response.AfterWrite = () =>
            {
                Application application = Application.Current;
                if (application == null) return;

                application.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        WidgetManager.Instance.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.Write("restart shutdown failed: " + ex.Message);
                    }
                    application.Shutdown();
                }));
            };

            return response;
        }

        private static string StringParam(ControlRequest request, string name)
        {
            JToken token = request.Params == null ? null : request.Params[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String)
                throw ControlException.Validation("'" + name + "' must be a string.");
            return (string)token;
        }

        private static int IntParam(ControlRequest request, string name, int fallback)
        {
            JToken token = request.Params == null ? null : request.Params[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type != JTokenType.Integer)
                throw ControlException.Validation("'" + name + "' must be an integer.");
            return token.Value<int>();
        }

        private static void RequireConfirm(ControlRequest request)
        {
            JToken token = request.Params == null ? null : request.Params["confirm"];
            if (token == null || token.Type != JTokenType.Boolean || !token.Value<bool>())
                throw ControlException.Forbidden("This command requires confirm=true.");
        }

        private static ControlResponse Ok(ControlRequest request, JToken result)
        {
            return new ControlResponse
            {
                V = ControlProtocol.Version,
                Id = request == null ? null : request.Id,
                Ok = true,
                Result = result
            };
        }

        private static ControlResponse Error(ControlRequest request, string code, string message,
            JObject details = null)
        {
            return new ControlResponse
            {
                V = ControlProtocol.Version,
                Id = request == null ? null : request.Id,
                Ok = false,
                Error = new ControlError { Code = code, Message = message, Details = details }
            };
        }
    }
}
