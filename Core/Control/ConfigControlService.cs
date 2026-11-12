using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    /// <summary>
    /// Read, patch and backup operations for the live config, plus the revision
    /// guard every write goes through (spec 5.1, 5.4, 5.6). The live config and
    /// the paths are injected so the checks never touch the real %AppData%
    /// files or <see cref="WidgetManager.Instance"/>.
    /// </summary>
    public sealed class ConfigControlService
    {
        private const int MaxBackups = 20;
        private const int MaxLabelLength = 48;

        /// <summary>
        /// Top-level fields a patch may touch. Everything structural (widgets,
        /// groups, legacy folders, frequent files, the schema number) is
        /// reserved for the widget/group commands and config.replace.
        /// </summary>
        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Language", "Theme", "PanelOpacity", "IslandX", "IslandCollapseDelay",
            "DefaultGridColumns", "IconStyle", "ItemScale", "StartWithWindows",
            "HideDesktopSourceOnStore", "HideDesktopIcons", "PinStorageToQuickAccess",
            "FileSearch"
        };

        /// <summary>
        /// Top-level fields whose change makes the running widget tree stale
        /// and therefore needs a restart.
        /// </summary>
        private static readonly string[] StructuralFields =
        {
            "Widgets", "Groups", "Folders", "FrequentFiles"
        };

        private readonly Func<AppConfig> _getConfig;
        private readonly Func<AppConfig, JObject> _applyConfig;
        private readonly Func<AppConfig, JObject> _applyDocument;
        private readonly Func<int, string, bool> _restartLauncher;
        private readonly Action _beforeRestart;
        private readonly string _configPath;
        private readonly string _backupDirectory;

        /// <summary>
        /// Production shape: structural replaces copy the whole document into
        /// the live config through <see cref="DefaultApplyDocument"/> and use the
        /// app's real restart helper. The router uses the internal constructor
        /// so the copy and save go through <see cref="WidgetManager"/>.
        /// </summary>
        public ConfigControlService(
            Func<AppConfig> getConfig,
            Func<AppConfig, JObject> applyConfig,
            string configPath,
            string backupDirectory)
            : this(
                getConfig,
                applyConfig,
                configPath,
                backupDirectory,
                DefaultApplyDocument(getConfig, applyConfig),
                RestartHelper.Launch,
                DesktopIconGuard.RestoreOnExit)
        {
        }

        /// <summary>
        /// Check seam: all app-facing effects (whole-document apply, restart
        /// launch and the pre-restart desktop guard) are injected, so the
        /// checks never touch the real config or start a real restart.
        /// </summary>
        internal ConfigControlService(
            Func<AppConfig> getConfig,
            Func<AppConfig, JObject> applyConfig,
            string configPath,
            string backupDirectory,
            Func<AppConfig, JObject> applyDocument,
            Func<int, string, bool> restartLauncher,
            Action beforeRestart)
        {
            _getConfig = getConfig ?? throw new ArgumentNullException("getConfig");
            _applyConfig = applyConfig ?? throw new ArgumentNullException("applyConfig");
            _applyDocument = applyDocument ?? throw new ArgumentNullException("applyDocument");
            _restartLauncher = restartLauncher ?? throw new ArgumentNullException("restartLauncher");
            _beforeRestart = beforeRestart ?? (() => { });
            _configPath = configPath ?? throw new ArgumentNullException("configPath");
            _backupDirectory = backupDirectory ?? throw new ArgumentNullException("backupDirectory");
        }

        /// <summary>Content revision of the live config; writes must echo it.</summary>
        public string CurrentRevision
        {
            get { return ConfigRevision.Compute(_getConfig()); }
        }

        /// <summary>
        /// The config document plus its identity. The default view is the
        /// summary (no note/todo/folder payloads); <c>full</c> is only returned
        /// when the caller asks for it explicitly.
        /// </summary>
        public JObject Get(JObject p)
        {
            AppConfig config = _getConfig();
            string view = string.Equals((string)(p == null ? null : p["view"]), "full",
                StringComparison.Ordinal)
                ? "full"
                : "summary";

            return new JObject
            {
                ["config"] = JObject.Parse(ConfigDocument.ToJson(config, view == "full")),
                ["revision"] = ConfigRevision.Compute(config),
                ["schemaVersion"] = config == null ? 0 : config.SchemaVersion,
                ["path"] = _configPath,
                ["view"] = view
            };
        }

        /// <summary>
        /// Applies an RFC 7386 JSON Merge Patch to the allowed top-level
        /// fields. The revision is checked first; a stale caller gets
        /// <c>conflict</c> and neither config nor disk changes.
        /// </summary>
        public JObject Patch(JObject p, string baseRevision)
        {
            if (string.IsNullOrEmpty(baseRevision)) throw ControlException.RevisionRequired();

            string current = CurrentRevision;
            if (!string.Equals(baseRevision, current, StringComparison.Ordinal))
                throw ControlException.Conflict(current);

            JObject parameters = p ?? new JObject();
            var patch = parameters["patch"] as JObject ?? new JObject();
            foreach (var property in patch.Properties())
            {
                if (!Allowed.Contains(property.Name))
                {
                    throw ControlException.Forbidden(
                        "Use widget/group commands for " + property.Name + ".");
                }
            }

            AppConfig currentConfig = _getConfig();
            var currentRoot = JObject.FromObject(currentConfig);
            var next = (JObject)currentRoot.DeepClone();
            ApplyMergePatch(next, patch);

            AppConfig parsed = ConfigDocument.Parse(
                next.ToString(Formatting.None), out List<string> warnings);
            if (parsed == null) throw ControlException.Validation(warnings);

            // Compare the normalized rendered documents per patched top-level
            // property, so a no-op patch reports no change and a nested
            // FileSearch edit is still one top-level change.
            var nextRoot = JObject.FromObject(parsed);
            var changes = new List<string>();
            foreach (var property in patch.Properties())
            {
                if (!JToken.DeepEquals(currentRoot[property.Name], nextRoot[property.Name]))
                    changes.Add(property.Name);
            }

            bool dryRun = parameters["dryRun"] != null
                && parameters["dryRun"].Type == JTokenType.Boolean
                && (bool)parameters["dryRun"];

            JObject applyWarnings = new JObject();
            if (!dryRun && changes.Count > 0)
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
                ["revision"] = dryRun || changes.Count == 0 ? current : CurrentRevision
            };
        }

        /// <summary>
        /// Validates a complete document, or a patch applied to the current
        /// config, without touching the live config or disk. Returns
        /// <c>{ ok, warnings, normalizedSummary }</c>.
        /// </summary>
        public JObject Validate(JObject p)
        {
            JObject parameters = p ?? new JObject();
            List<string> warnings;
            AppConfig parsed;

            JToken document = parameters["document"];
            if (document != null)
            {
                string json = document.Type == JTokenType.String
                    ? (string)document
                    : document.ToString(Formatting.None);
                parsed = ConfigDocument.Parse(json, out warnings);
            }
            else if (parameters["patch"] is JObject patch)
            {
                var root = JObject.FromObject(_getConfig());
                ApplyMergePatch(root, patch);
                parsed = ConfigDocument.Parse(root.ToString(Formatting.None), out warnings);
            }
            else
            {
                throw ControlException.Validation("A document or patch is required.");
            }

            return new JObject
            {
                ["ok"] = parsed != null,
                ["warnings"] = new JArray(warnings),
                ["normalizedSummary"] = parsed == null
                    ? (JToken)JValue.CreateNull()
                    : JObject.Parse(ConfigDocument.ToJson(parsed, full: false))
            };
        }

        /// <summary>
        /// Applies a complete config document. A change to the widget tree
        /// (widgets, groups, legacy folders or frequent files) needs a restart
        /// and is only written when the caller passes <c>restart:true</c>;
        /// hot-only changes go through the same apply path as a patch. The
        /// revision is checked first, so a stale caller never changes anything.
        /// </summary>
        public JObject Replace(JObject p, string baseRevision)
        {
            if (string.IsNullOrEmpty(baseRevision)) throw ControlException.RevisionRequired();

            string current = CurrentRevision;
            if (!string.Equals(baseRevision, current, StringComparison.Ordinal))
                throw ControlException.Conflict(current);

            JObject parameters = p ?? new JObject();
            AppConfig parsed = ParseReplaceDocument(parameters);

            // The document may predate widget kinds. Migrate before the diff so
            // a legacy document is never written back with an empty widget list.
            parsed.MigrateLegacyForControl();

            AppConfig live = _getConfig();

            // A hand-written document that omits the schema field deserializes
            // as 0. That is not a downgrade request, so it adopts the live
            // schema before the diff; an explicit newer value is left alone.
            if (parsed.SchemaVersion == 0) parsed.SchemaVersion = live.SchemaVersion;

            JObject currentRoot = JObject.FromObject(live);
            JObject nextRoot = JObject.FromObject(parsed);
            RemoveEmptyLegacyFolders(currentRoot);
            RemoveEmptyLegacyFolders(nextRoot);

            bool structural;
            List<string> changes = CollectChanges(currentRoot, nextRoot, out structural);
            bool wouldChange = changes.Count > 0;
            bool dryRun = BoolParam(parameters, "dryRun");
            bool restart = BoolParam(parameters, "restart");

            if (!wouldChange)
            {
                return BuildResult(false, changes, new JObject(),
                    restartRequired: false, wrote: false, restarting: false,
                    restartFailed: false, revision: current);
            }

            if (dryRun)
            {
                return BuildResult(true, changes, new JObject(),
                    restartRequired: structural, wrote: false, restarting: false,
                    restartFailed: false, revision: current);
            }

            if (structural && !restart)
            {
                return BuildResult(true, changes, new JObject(),
                    restartRequired: true, wrote: false, restarting: false,
                    restartFailed: false, revision: current);
            }

            if (structural)
            {
                Backup("before-replace");

                // The desktop guard must see the OLD live preference: the new
                // document may turn HideDesktopIcons off, and RestoreOnExit
                // would then leave the desktop hidden through the restart.
                RunBeforeRestart();
                JObject warnings = _applyDocument(parsed) ?? new JObject();
                string revision = CurrentRevision;
                bool restarting = LaunchRestart();

                return BuildResult(true, changes, warnings,
                    restartRequired: true, wrote: true, restarting: restarting,
                    restartFailed: !restarting, revision: revision);
            }

            Backup("before-replace");
            JObject hotWarnings = _applyConfig(parsed) ?? new JObject();
            return BuildResult(true, changes, hotWarnings,
                restartRequired: false, wrote: true, restarting: false,
                restartFailed: false, revision: CurrentRevision);
        }

        /// <summary>
        /// Restores a snapshot written by <see cref="Backup"/>. Only files
        /// under the service's backup directory are accepted (canonical path,
        /// case-insensitive, separator boundary), and the document then takes
        /// the exact same structural/hot/backup path as <see cref="Replace"/>.
        /// </summary>
        public JObject Restore(JObject p, string baseRevision)
        {
            JObject parameters = p ?? new JObject();
            JToken backupToken = parameters["backup"];
            if (backupToken == null || backupToken.Type != JTokenType.String
                || string.IsNullOrWhiteSpace((string)backupToken))
            {
                throw ControlException.Validation("A backup path is required.");
            }

            string backupPath = ResolveBackupPath((string)backupToken);
            if (!File.Exists(backupPath))
                throw ControlException.Validation("The backup file was not found.");

            string json;
            try
            {
                json = File.ReadAllText(backupPath);
            }
            catch (Exception ex)
            {
                throw ControlException.Validation(
                    "The backup file could not be read: " + ex.Message);
            }

            var replaceParameters = (JObject)parameters.DeepClone();
            replaceParameters.Remove("backup");
            replaceParameters["document"] = json;
            return Replace(replaceParameters, baseRevision);
        }

        /// <summary>
        /// Writes the in-memory config (debounced changes included) to a new
        /// timestamped snapshot and prunes everything beyond the newest 20.
        /// </summary>
        public string Backup(string label)
        {
            AppConfig config = _getConfig();
            Directory.CreateDirectory(_backupDirectory);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string baseName = "config-" + stamp + "-" + SanitizeLabel(label);
            string path = Path.Combine(_backupDirectory, baseName + ".json");

            int suffix = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(_backupDirectory,
                    baseName + "-" + suffix.ToString(CultureInfo.InvariantCulture) + ".json");
                suffix++;
            }

            File.WriteAllText(path, ConfigDocument.ToJson(config, full: true));
            TrimBackups(path);
            return path;
        }

        private static AppConfig ParseReplaceDocument(JObject parameters)
        {
            JToken document = parameters["document"];
            if (document == null || document.Type == JTokenType.Null)
                throw ControlException.Validation("A document is required.");

            string json = document.Type == JTokenType.String
                ? (string)document
                : document.ToString(Formatting.None);

            List<string> warnings;
            AppConfig parsed = ConfigDocument.Parse(json, out warnings);
            if (parsed == null) throw ControlException.Validation(warnings);
            return parsed;
        }

        private static bool BoolParam(JObject parameters, string name)
        {
            JToken token = parameters == null ? null : parameters[name];
            return token != null && token.Type == JTokenType.Boolean && token.Value<bool>();
        }

        /// <summary>
        /// Compares every top-level property of the current and the parsed
        /// document. The four structural fields drive the restart decision;
        /// the rest are the hot path, exactly like a patch.
        /// </summary>
        private static List<string> CollectChanges(
            JObject currentRoot, JObject nextRoot, out bool structural)
        {
            var structuralFields = new HashSet<string>(StructuralFields, StringComparer.Ordinal);
            var changes = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            structural = false;

            foreach (var property in currentRoot.Properties())
            {
                seen.Add(property.Name);
                if (JToken.DeepEquals(property.Value, nextRoot[property.Name])) continue;

                changes.Add(property.Name);
                if (structuralFields.Contains(property.Name)) structural = true;
            }

            foreach (var property in nextRoot.Properties())
            {
                if (!seen.Add(property.Name)) continue;

                changes.Add(property.Name);
                if (structuralFields.Contains(property.Name)) structural = true;
            }

            return changes;
        }

        /// <summary>
        /// The retired legacy list is never written back. An absent block and
        /// an empty array are the same shape, so neither counts as a change.
        /// </summary>
        private static void RemoveEmptyLegacyFolders(JObject root)
        {
            JToken folders = root["Folders"];
            if (folders == null || folders.Type == JTokenType.Null
                || (folders is JArray array && array.Count == 0))
            {
                root.Remove("Folders");
            }
        }

        private string ResolveBackupPath(string requested)
        {
            string full;
            string root;
            try
            {
                full = Path.GetFullPath(requested);
                root = Path.GetFullPath(_backupDirectory);
            }
            catch (Exception)
            {
                throw ControlException.Forbidden(
                    "The backup must live under the backups directory.");
            }

            string prefix = root.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw ControlException.Forbidden(
                    "The backup must live under the backups directory.");
            }

            return full;
        }

        /// <summary>
        /// Runs the pre-restart action (the desktop guard) exactly once, before
        /// the new document is copied into the live config. A failure is logged
        /// but must not stop the restart.
        /// </summary>
        private void RunBeforeRestart()
        {
            try
            {
                _beforeRestart();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("config replace: desktop guard failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Starts the helper only after the document has been written and the
        /// pre-restart action has already run. A launcher failure is reported,
        /// never thrown.
        /// </summary>
        private bool LaunchRestart()
        {
            try
            {
                return _restartLauncher(CurrentProcessId(), CurrentExePath());
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("config replace: restart launcher failed: " + ex.Message);
                return false;
            }
        }

        private static int CurrentProcessId()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return process.Id;
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string CurrentExePath()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    string path = process.MainModule == null
                        ? null
                        : process.MainModule.FileName;
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
            }
            catch (Exception)
            {
                // Fall through to the layout-based fallback below.
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kobold.exe");
        }

        private static JObject BuildResult(
            bool wouldChange,
            List<string> changes,
            JObject warnings,
            bool restartRequired,
            bool wrote,
            bool restarting,
            bool restartFailed,
            string revision)
        {
            var result = new JObject
            {
                ["wouldChange"] = wouldChange,
                ["changes"] = new JArray(changes ?? new List<string>()),
                ["warnings"] = warnings ?? new JObject(),
                ["restartRequired"] = restartRequired,
                ["wrote"] = wrote,
                ["restarting"] = restarting,
                ["revision"] = revision ?? string.Empty
            };

            if (restartFailed) result["restartFailed"] = true;
            return result;
        }

        private static Func<AppConfig, JObject> DefaultApplyDocument(
            Func<AppConfig> getConfig, Func<AppConfig, JObject> applyConfig)
        {
            return next =>
            {
                AppConfig live = getConfig == null ? null : getConfig();
                if (live != null) CopyDocument(next, live);
                return applyConfig == null || live == null
                    ? new JObject()
                    : applyConfig(live) ?? new JObject();
            };
        }

        private static void CopyDocument(AppConfig source, AppConfig target)
        {
            if (source == null || target == null) return;

            foreach (PropertyInfo property in typeof(AppConfig).GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite) continue;
                if (property.GetIndexParameters().Length > 0) continue;

                property.SetValue(target, property.GetValue(source, null), null);
            }
        }

        /// <summary>
        /// RFC 7386: objects merge recursively, arrays and scalars replace, and
        /// a null value removes the key. Newtonsoft's <c>JObject.Merge</c>
        /// stores nulls, so the rule is implemented here.
        /// </summary>
        private static void ApplyMergePatch(JObject target, JObject patch)
        {
            if (target == null || patch == null) return;

            foreach (var property in patch.Properties())
            {
                if (property.Value == null || property.Value.Type == JTokenType.Null)
                {
                    target.Remove(property.Name);
                    continue;
                }

                if (property.Value is JObject patchObject)
                {
                    // RFC 7386: a non-object target is replaced by an empty
                    // object before the nested patch is applied.
                    var child = target[property.Name] as JObject;
                    if (child == null)
                    {
                        child = new JObject();
                        target[property.Name] = child;
                    }
                    ApplyMergePatch(child, patchObject);
                    continue;
                }

                target[property.Name] = property.Value.DeepClone();
            }
        }

        /// <summary>
        /// Keeps the newest <see cref="MaxBackups"/> snapshots by last-write
        /// time. The just-written snapshot is always protected, even when the
        /// file system gives several files the same timestamp.
        /// </summary>
        private void TrimBackups(string keepPath)
        {
            try
            {
                var files = Directory.GetFiles(_backupDirectory, "config-*.json")
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToList();

                int remove = files.Count - MaxBackups;
                for (int i = files.Count - 1; i >= 0 && remove > 0; i--)
                {
                    if (string.Equals(files[i].FullName, keepPath,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    try
                    {
                        files[i].Delete();
                        remove--;
                    }
                    catch (Exception)
                    {
                        // Retention is best effort; a locked snapshot stays.
                    }
                }
            }
            catch (Exception)
            {
                // Listing failures must never fail the backup itself.
            }
        }

        /// <summary>
        /// Keeps only ASCII letters, digits, '.', '-' and '_' so a label can
        /// never add path segments or characters Windows rejects.
        /// </summary>
        private static string SanitizeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "manual";

            var builder = new StringBuilder(label.Length);
            foreach (char c in label)
            {
                bool allowed = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || c == '.' || c == '-' || c == '_';
                if (allowed)
                {
                    builder.Append(c);
                }
                else if (builder.Length > 0 && builder[builder.Length - 1] != '-')
                {
                    builder.Append('-');
                }
            }

            string sanitized = builder.ToString().Trim('-', '.', '_');
            if (sanitized.Length == 0) return "manual";
            if (sanitized.Length <= MaxLabelLength) return sanitized;
            return sanitized.Substring(0, MaxLabelLength).TrimEnd('-', '.', '_');
        }
    }
}
