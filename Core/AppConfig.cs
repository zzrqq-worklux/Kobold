using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Kobold.Core.FileSearch;

namespace Kobold.Core
{
    /// <summary>
    /// Application configuration including all folder widgets
    /// </summary>
    public class AppConfig
    {
        /// <summary>
        /// The schema this build writes. A config without the field is legacy
        /// (version 0) and is upgraded on its next save; a config with a newer
        /// version is read for display but never overwritten.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; }

        public string Language { get; set; }
        public string Theme { get; set; }
        public bool StartWithWindows { get; set; }
        public bool HideDesktopSourceOnStore { get; set; }
        public double PanelOpacity { get; set; } // Expanded panel opacity (0.2 - 1.0)
        public double? IslandX { get; set; } // Remembered island center X (DIP); null = centered
        public double IslandCollapseDelay { get; set; } // Seconds after the mouse leaves the island before it collapses
        public bool HideDesktopIcons { get; set; } // Hide the desktop icons while the app runs
        public int? HideIconsOriginal { get; set; } // Original "HideIcons" value, to restore on exit (null = value absent)
        public int DefaultGridColumns { get; set; }
        public string IconStyle { get; set; }  // classic, handdrawn
        public double ItemScale { get; set; }  // Item size scale factor (1.0 = base, 1.3 = Windows standard)
        public bool PinStorageToQuickAccess { get; set; } // Desired Quick Access state for the storage folder
        public List<WidgetData> Widgets { get; set; }

        /// <summary>
        /// Usage counters for the files opened from the widgets ("frequent
        /// files"); null until something is opened, which keeps an untouched
        /// config free of the block.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<FrequentFile> FrequentFiles { get; set; }

        /// <summary>
        /// Read-only migration entrance for configs written before widget
        /// kinds existed. Never written back: <see cref="Load"/> moves its
        /// entries into <see cref="Widgets"/> and clears it.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<FolderData> Folders { get; set; }

        /// <summary>
        /// Tab groups; null until the user merges widgets, which keeps an
        /// untouched config free of the block. Membership is stored on the
        /// widgets (<see cref="WidgetData.GroupId"/>). A damaged block is read
        /// tolerantly (bad entries skipped) so it can never cost the widgets.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [JsonConverter(typeof(WidgetGroupListConverter))]
        public List<WidgetGroup> Groups { get; set; }

        /// <summary>
        /// Optional file-search settings; null until the user configures the
        /// feature, in which case nothing is written for it.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public FileSearchSettings FileSearch { get; set; }

        public AppConfig()
        {
            Language = "en";
            Theme = "dark";
            StartWithWindows = true;
            HideDesktopSourceOnStore = true;
            PanelOpacity = 1.0;
            DefaultGridColumns = 3;
            IconStyle = "classic";
            ItemScale = 1.1; // Default to slightly larger icons
            IslandX = null; // centered by default
            IslandCollapseDelay = 1.0; // seconds
            HideDesktopIcons = false;
            HideIconsOriginal = null;
            PinStorageToQuickAccess = true; // the storage folder is pinned unless the user turns it off
            Widgets = new List<WidgetData>();
            Folders = new List<FolderData>();
        }

        // Where this instance was loaded from, so Save writes back to the same
        // place. Instances created directly (not via Load) fall back to the
        // shared %AppData% location.
        private string _configPath;
        private string _backupPath;

        // True until the first successful save after a load that could not use
        // the main file: the backup is the recovery source then, and the first
        // save must not overwrite it with a main file we could not trust.
        private bool _skipBackupRefreshOnce;

        // The version found in the loaded file, and whether it was newer than
        // this build understands. Saving a newer config would silently drop
        // fields written by a newer build, so it is refused until a newer
        // build loads the file.
        private int _loadedSchemaVersion;
        private bool _schemaSaveBlocked;

        // Set by BeginClosing: the session is ending, so writes must happen now
        // and no new debounced writes may be scheduled.
        private bool _closing;

        /// <summary>
        /// Loads configuration from the shared location, or creates a default
        /// one on a fresh install.
        /// </summary>
        public static AppConfig Load()
        {
            return Load(Utils.GetConfigPath(), Utils.GetConfigPath() + ".backup");
        }

        /// <summary>
        /// Loads configuration from explicit paths. When the main file cannot be
        /// read the backup is used; when neither can be read the unreadable
        /// files are first kept as '*.failed-&lt;timestamp&gt;' evidence copies
        /// and only then is a default config created - never a silent overwrite.
        /// </summary>
        public static AppConfig Load(string configPath, string backupPath)
        {
            bool mainExisted = File.Exists(configPath);
            bool backupExisted = File.Exists(backupPath);

            bool mainUnreadable;
            AppConfig loaded = TryReadConfig(configPath, out mainUnreadable);

            bool loadedFromBackup = false;
            bool backupUnreadable = false;
            if (loaded == null)
            {
                loaded = TryReadConfig(backupPath, out backupUnreadable);
                loadedFromBackup = loaded != null;
            }

            bool defaulted = false;
            if (loaded == null)
            {
                loaded = new AppConfig();
                loaded.Widgets.Add(FolderData.Create(Localization.Get("UI_DefaultFolderName"),
                    UiTokens.DefaultFolderColor, 100, 100, loaded.DefaultGridColumns));
                defaulted = true;
            }

            // A file that existed but could not be read gets a timestamped copy
            // before anything can overwrite it - whether or not we recovered.
            if (mainExisted && mainUnreadable) PreserveFailedFile(configPath);
            if (backupExisted && backupUnreadable) PreserveFailedFile(backupPath);

            // Legacy Folders move into Widgets before anything reads the list
            // or writes it back. New-format configs are already migrated.
            loaded.MigrateLegacyWidgets();

            // Group membership is validated once the widget list is final. A
            // broken block only clears membership fields, never widget data.
            loaded.RepairGroups();

            // Optional file-search settings are repaired only when present; a
            // missing block stays null (unconfigured) and is never written.
            if (loaded.FileSearch != null) loaded.FileSearch.Normalize();

            // Configs written before the size fields existed (and the migrated
            // legacy folders) get their panel size filled once in memory; the
            // existing values of newer configs are never touched. No save is
            // forced - a defaulted load writes the sizes with its usual save.
            foreach (var widget in loaded.Widgets)
            {
                PanelSizeMigration.FillMissingSizes(widget, loaded.ItemScale, loaded.Language, DateTime.Today);
            }

            loaded._configPath = configPath;
            loaded._backupPath = backupPath;
            loaded._skipBackupRefreshOnce =
                mainUnreadable || loadedFromBackup || (defaulted && (mainExisted || backupExisted));
            loaded._loadedSchemaVersion = loaded.SchemaVersion;
            loaded._schemaSaveBlocked = loaded.SchemaVersion > CurrentSchemaVersion;

            if (defaulted) loaded.Save();
            return loaded;
        }

        /// <summary>
        /// Reads one config file; 'unreadable' tells the caller that a file was
        /// there but could not be used (as opposed to simply not existing).
        /// </summary>
        private static AppConfig TryReadConfig(string path, out bool unreadable)
        {
            unreadable = false;
            try
            {
                if (!File.Exists(path)) return null;

                string json = File.ReadAllText(path);
                var config = JsonConvert.DeserializeObject<AppConfig>(json);
                if (config == null)
                {
                    unreadable = true;
                    return null;
                }

                // A structurally corrupt widget list is treated exactly like
                // unparseable JSON: evidence copy, backup recovery, default.
                EnsureStructurallySound(config, json);
                RepairLegacyItems(config);
                return config;
            }
            catch (Exception)
            {
                unreadable = true;
                return null;
            }
        }

        /// <summary>
        /// Moves a config written before widget kinds existed (only "Folders")
        /// into "Widgets", keeping order and fields. When both lists are
        /// present Widgets wins; the legacy list is always dropped so a save
        /// never writes it out again.
        /// </summary>
        private void MigrateLegacyWidgets()
        {
            if (Widgets.Count == 0 && Folders.Count > 0)
            {
                Widgets.AddRange(Folders.Cast<WidgetData>());
            }

            Folders = null;
        }

        /// <summary>
        /// Rejects a config whose widget lists are explicitly null or hold null
        /// entries, or whose item lists are explicitly null. Missing properties
        /// are fine - the constructors initialize every list. Throwing here lets
        /// the caller treat such a file as unreadable, so a null list can never
        /// reach the UI and recovery follows the usual evidence/backup path.
        /// </summary>
        private static void EnsureStructurallySound(AppConfig config, string json)
        {
            // An explicit "Folders": null never reaches the property: its
            // NullValueHandling.Ignore keeps the constructor's list. A file
            // with no Widgets array at all is a legacy-shaped config whose
            // list is null and stays rejected; our own output pairs the null
            // with a Widgets array (Save writes it on purpose), which is fine.
            var root = JObject.Parse(json);
            if (root["Folders"]?.Type == JTokenType.Null
                && root["Widgets"]?.Type != JTokenType.Array)
                throw new InvalidDataException("config Folders is null");

            if (config.Folders == null) throw new InvalidDataException("config Folders is null");
            if (config.Widgets == null) throw new InvalidDataException("config Widgets is null");

            foreach (var folder in config.Folders)
            {
                if (folder == null) throw new InvalidDataException("config Folders holds a null entry");
                if (folder.Items == null) throw new InvalidDataException("config folder Items is null");
            }

            foreach (var widget in config.Widgets)
            {
                if (widget == null) throw new InvalidDataException("config Widgets holds a null entry");
                if (widget is FolderData folder && folder.Items == null)
                    throw new InvalidDataException("config folder Items is null");
                if (widget is TodoData todo && todo.Items == null)
                    throw new InvalidDataException("config todo Items is null");
                if (widget is NoteData note && note.Notes == null)
                    throw new InvalidDataException("config note Notes is null");
            }
        }

        /// <summary>Fills in fields missing from configs written before they existed.</summary>
        private static void RepairLegacyItems(AppConfig config)
        {
            // Legacy entries are repaired before the migration moves them.
            foreach (var folder in config.Folders) RepairFolder(folder);

            foreach (var widget in config.Widgets)
            {
                if (widget is FolderData folder) RepairFolder(folder);
                else if (widget is TodoData todo) RepairTodo(todo);
                else if (widget is NoteData note) RepairNote(note);
            }

            // Colours written by the pre-Claude-Warm picker keep their old
            // Tailwind hue; map them onto the current palette while the config
            // is being repaired (both the main file and a backup load).
            WidgetColorMigration.Apply(config);
        }

        private static void RepairFolder(FolderData folder)
        {
            // A missing MaxPanelRows already defaults in the FolderData
            // constructor; only hand-edited out-of-range values need repair.
            if (folder.MaxPanelRows < PanelRows.MinAllowedRows || folder.MaxPanelRows > PanelRows.MaxAllowedRows)
            {
                folder.MaxPanelRows = PanelRows.DefaultMaxRows;
            }

            foreach (var item in folder.Items)
            {
                if (string.IsNullOrEmpty(item.Name) && !string.IsNullOrEmpty(item.Path))
                {
                    item.Name = Path.GetFileName(item.Path);
                }
            }
        }

        private static void RepairTodo(TodoData todo)
        {
            if (todo.SelectedDate.HasValue) todo.SelectedDate = todo.SelectedDate.Value.Date;

            foreach (var item in todo.Items)
            {
                if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString();
                if (item.Text == null) item.Text = string.Empty;
                if (item.Date.HasValue) item.Date = item.Date.Value.Date;
                if (item.CompletedDate.HasValue) item.CompletedDate = item.CompletedDate.Value.Date;

                // Children are optional per item; a null list is filled in so
                // the UI can add the first sub-todo without a special case.
                if (item.Children == null) item.Children = new List<SubTodoItem>();
                foreach (var child in item.Children)
                {
                    if (string.IsNullOrEmpty(child.Id)) child.Id = Guid.NewGuid().ToString();
                    if (child.Text == null) child.Text = string.Empty;
                }
            }
        }

        private static void RepairNote(NoteData note)
        {
            // A hand-edited or absent scale normalizes the same way the
            // property's setter does, so the UI never reads a broken value.
            note.FontScale = NoteFontScale.Normalize(note.FontScale);

            // Timestamps are deliberately left alone: note times are not
            // date-normalized the way todo dates are.
            foreach (var item in note.Notes)
            {
                if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString();
                if (item.Text == null) item.Text = string.Empty;
            }
        }

        /// <summary>
        /// Repairs the loaded group block. Only well-formed groups with at
        /// least two members survive; every other membership field is cleared,
        /// so a hand-edited or stale block can never strand widget data or
        /// make the whole config unreadable.
        /// </summary>
        private void RepairGroups()
        {
            // No groups at all: every membership field is an orphan.
            if (Groups == null || Groups.Count == 0)
            {
                Groups = null;
                ClearMembership(Widgets);
                return;
            }

            // Drop null/blank ids and duplicate ids (first one wins), then the
            // groups that could not even show two tabs.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var surviving = new List<WidgetGroup>();
            foreach (var group in Groups)
            {
                if (group == null || string.IsNullOrWhiteSpace(group.Id)) continue;
                if (!seen.Add(group.Id)) continue;
                if (CountMembers(group.Id) < 2) continue;
                surviving.Add(group);
            }

            var survivingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in surviving) survivingIds.Add(group.Id);

            // Anything pointing at a dropped/unknown group loses both fields.
            foreach (var widget in Widgets)
            {
                if (widget == null) continue;
                if (widget.GroupId == null || !survivingIds.Contains(widget.GroupId))
                {
                    widget.GroupId = null;
                    widget.GroupOrder = null;
                }
            }

            foreach (var group in surviving)
            {
                var members = OrderedMembers(group.Id);
                for (int i = 0; i < members.Count; i++) members[i].GroupOrder = i;

                // A missing/unknown active id falls back to the first tab.
                bool activeIsMember = false;
                if (!string.IsNullOrEmpty(group.ActiveWidgetId))
                {
                    foreach (var member in members)
                    {
                        if (string.Equals(member.Id, group.ActiveWidgetId, StringComparison.Ordinal))
                        {
                            activeIsMember = true;
                            break;
                        }
                    }
                }
                if (!activeIsMember) group.ActiveWidgetId = members[0].Id;

                // Legacy groups could carry a group-level pin with no member
                // pin. Keep that behavior by pinning the active tab; the group
                // field is always re-derived from the members.
                int pinned = GroupHostPolicy.PinnedCount(members);
                if (group.IsPanelPinned && pinned == 0)
                {
                    foreach (var member in members)
                    {
                        if (string.Equals(member.Id, group.ActiveWidgetId, StringComparison.Ordinal))
                        {
                            member.IsPanelPinned = true;
                            break;
                        }
                    }
                }
                group.IsPanelPinned = GroupHostPolicy.AnyMemberPinned(members);
            }

            if (surviving.Count == 0) Groups = null;
            else Groups = surviving;
        }

        /// <summary>
        /// Runs the same repair/normalization as Load, without touching disk.
        /// Used by the control plane to validate a proposed document.
        /// </summary>
        internal void RepairForControl()
        {
            RepairGroups();
            if (FileSearch != null) FileSearch.Normalize();
        }

        /// <summary>
        /// The load-time legacy migration for a document the control plane is
        /// about to write: an old non-empty <c>Folders</c> list moves into an
        /// empty <c>Widgets</c> list, and the retired block is cleared in every
        /// case, so a legacy document can never be written back with
        /// <c>Widgets=[]</c> and the folder data dropped.
        /// </summary>
        internal void MigrateLegacyForControl()
        {
            if (Folders != null && Folders.Count > 0
                && (Widgets == null || Widgets.Count == 0))
            {
                if (Widgets == null) Widgets = new List<WidgetData>();
                Widgets.AddRange(Folders.Cast<WidgetData>());
            }

            Folders = null;
            RepairGroups();
        }

        /// <summary>
        /// The members of a group in normalized tab order: by the stored
        /// order, with the config index breaking ties (and standing in for a
        /// missing order).
        /// </summary>
        private List<WidgetData> OrderedMembers(string groupId)
        {
            var indexed = new List<KeyValuePair<int, WidgetData>>();
            for (int i = 0; i < Widgets.Count; i++)
            {
                if (IsMember(Widgets[i], groupId))
                    indexed.Add(new KeyValuePair<int, WidgetData>(i, Widgets[i]));
            }

            indexed.Sort((a, b) =>
            {
                int left = a.Value.GroupOrder ?? int.MaxValue;
                int right = b.Value.GroupOrder ?? int.MaxValue;
                return left != right ? left.CompareTo(right) : a.Key.CompareTo(b.Key);
            });

            var ordered = new List<WidgetData>(indexed.Count);
            foreach (var entry in indexed) ordered.Add(entry.Value);
            return ordered;
        }

        private static bool IsMember(WidgetData widget, string groupId)
        {
            return widget != null && string.Equals(widget.GroupId, groupId, StringComparison.Ordinal);
        }

        private int CountMembers(string groupId)
        {
            int count = 0;
            foreach (var widget in Widgets)
            {
                if (IsMember(widget, groupId)) count++;
            }
            return count;
        }

        private static void ClearMembership(IEnumerable<WidgetData> widgets)
        {
            if (widgets == null) return;
            foreach (var widget in widgets)
            {
                if (widget == null) continue;
                widget.GroupId = null;
                widget.GroupOrder = null;
            }
        }

        /// <summary>
        /// Keeps a copy of a file that could not be read, so a later save can
        /// never destroy the user's real data without a trace. Best effort: if
        /// the copy itself fails, startup still continues.
        /// </summary>
        private static void PreserveFailedFile(string path)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string candidate = path + ".failed-" + stamp;
                int suffix = 2;
                while (File.Exists(candidate))
                {
                    candidate = path + ".failed-" + stamp + "-" + suffix++;
                }
                File.Copy(path, candidate, false);
            }
            catch (Exception)
            {
                // Evidence is best effort - never block startup over it.
            }
        }

        private System.Timers.Timer _saveTimer;
        private System.Timers.Timer _forceSaveTimer;
        private readonly object _saveLock = new object();
        private readonly object _timerLock = new object();

        /// <summary>
        /// Raised when a save fails (disk full, permissions, lock). The tray
        /// subscribes to show the user a one-time notice instead of failing
        /// silently.
        /// </summary>
        public static event Action<string> SaveFailed;

        /// <summary>
        /// Upper bound on how long a pending save may be pushed back by rapid
        /// changes (every debounce reset would otherwise postpone it again), so
        /// edits always reach the disk eventually.
        /// </summary>
        [JsonIgnore]
        public int ForceSaveIntervalMs { get; set; } = 10000;

        /// <summary>
        /// Saves current configuration to JSON file with backup
        /// </summary>
        public void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    string configPath = _configPath ?? Utils.GetConfigPath();
                    string backupPath = _backupPath ?? configPath + ".backup";

                    // A config written by a newer build may hold fields this
                    // build does not understand. Loading it is safe (unknown
                    // fields are ignored); writing it back would drop them, so
                    // refuse before either file is touched.
                    if (_schemaSaveBlocked)
                    {
                        string message = "Config schema " + _loadedSchemaVersion
                            + " is newer than this build's " + CurrentSchemaVersion
                            + "; refusing to overwrite " + configPath + ".";
                        System.Diagnostics.Debug.WriteLine("[Kobold] " + message);
                        StopSaveTimers(); // a pending debounce must not report this again
                        SaveFailed?.Invoke(message);
                        return;
                    }

                    Utils.EnsureDirectoryExists(Path.GetDirectoryName(configPath));

                    SchemaVersion = CurrentSchemaVersion;
                    string json = JsonConvert.SerializeObject(this, Formatting.Indented);

                    // Releases ≤1.3.0 read the legacy Folders list. An absent
                    // key silently yields an empty widget list there, while an
                    // explicit null drives them to keep the file as
                    // '*.failed-*' evidence and fall back to defaults - the
                    // downgrade promise the docs make.
                    var root = JObject.Parse(json);
                    root["Folders"] = JValue.CreateNull();

                    // An unconfigured or empty group list is not written at
                    // all: an old config keeps its exact shape, and a dissolved
                    // last group leaves no empty block behind.
                    if (Groups == null || Groups.Count == 0) root.Remove("Groups");

                    json = root.ToString(Formatting.Indented);

                    // The replaced file becomes the backup in the same atomic
                    // step. While the main file is untrusted, write without a
                    // backup so the recovery source survives.
                    AtomicFile.WriteAllText(configPath, json,
                        _skipBackupRefreshOnce ? null : backupPath);
                    _skipBackupRefreshOnce = false;
                    _schemaSaveBlocked = false;
                    _loadedSchemaVersion = CurrentSchemaVersion;
                    StopSaveTimers();
                }
                catch (Exception ex)
                {
                    // The backup is the safety net; the user still deserves to
                    // know that this save did not land.
                    System.Diagnostics.Debug.WriteLine("[Kobold] config save failed: " + ex.Message);
                    SaveFailed?.Invoke(ex.Message);
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> under the same gate <see cref="Save"/>
        /// holds while it serializes the config. A caller that mutates a list
        /// (the frequent-files block) uses this so a debounced save can never
        /// observe Add/Sort/RemoveAt and throw "Collection was modified".
        /// The action must not call back into <see cref="Save"/> or
        /// <see cref="SaveDebounced"/> - that would nest the gate; schedule the
        /// write after it returns.
        /// </summary>
        internal void WithSaveLock(Action action)
        {
            if (action == null) return;
            lock (_saveLock)
            {
                action();
            }
        }

        /// <summary>
        /// Schedules a save operation (Debounce)
        /// Prevents disk spamming during rapid changes (dragging, etc.), and is
        /// paired with a force-save cap so a long stream of changes cannot keep
        /// postponing the write forever.
        /// </summary>
        public void SaveDebounced(int delayMs = 1000)
        {
            if (_closing) return;

            int forceMs = ForceSaveIntervalMs > 0 ? ForceSaveIntervalMs : 10000;
            lock (_timerLock)
            {
                if (_saveTimer == null)
                {
                    _saveTimer = new System.Timers.Timer(delayMs);
                    _saveTimer.AutoReset = false;
                    _saveTimer.Elapsed += (s, e) => Save();
                }
                if (_forceSaveTimer == null)
                {
                    _forceSaveTimer = new System.Timers.Timer(forceMs);
                    _forceSaveTimer.AutoReset = false;
                    _forceSaveTimer.Elapsed += (s, e) => Save();
                }

                _saveTimer.Stop();
                _saveTimer.Interval = delayMs;
                _saveTimer.Start();

                _forceSaveTimer.Stop();
                _forceSaveTimer.Interval = forceMs;
                _forceSaveTimer.Start();
            }
        }

        /// <summary>
        /// Final synchronous save for a Windows session end (logoff/shutdown):
        /// writes now - there may be no time for the debounce timer - and stops
        /// accepting new debounced writes.
        /// </summary>
        public void BeginClosing()
        {
            _closing = true;
            Save();
        }

        /// <summary>Stops both pending-save timers once a save has landed.</summary>
        private void StopSaveTimers()
        {
            lock (_timerLock)
            {
                _saveTimer?.Stop();
                _forceSaveTimer?.Stop();
            }
        }

        /// <summary>
        /// Adds a widget of any kind
        /// </summary>
        public void AddWidget(WidgetData widget)
        {
            lock (_saveLock)
            {
                Widgets.Add(widget);
            }
            SaveDebounced();
        }

        /// <summary>
        /// Removes a widget of any kind by ID. A group the widget belonged to
        /// is cleaned up with it: below two members the group dissolves and
        /// the last member becomes a plain panel, otherwise the active tab
        /// falls back to the first survivor.
        /// </summary>
        public void RemoveWidget(string id)
        {
            lock (_saveLock)
            {
                var affectedGroups = new HashSet<string>(StringComparer.Ordinal);
                foreach (var widget in Widgets)
                {
                    if (widget != null && widget.Id == id && widget.GroupId != null)
                        affectedGroups.Add(widget.GroupId);
                }

                Widgets.RemoveAll(w => w.Id == id);

                if (Groups != null)
                {
                    foreach (var groupId in affectedGroups)
                    {
                        WidgetGroup group = null;
                        foreach (var candidate in Groups)
                        {
                            if (candidate != null
                                && string.Equals(candidate.Id, groupId, StringComparison.Ordinal))
                            {
                                group = candidate;
                                break;
                            }
                        }
                        if (group == null) continue;

                        var members = OrderedMembers(groupId);
                        if (members.Count < 2)
                        {
                            // A single surviving widget is no group: dissolve
                            // it and let that widget stand on its own.
                            Groups.Remove(group);
                            foreach (var member in members)
                            {
                                member.GroupId = null;
                                member.GroupOrder = null;
                            }
                            continue;
                        }

                        for (int i = 0; i < members.Count; i++) members[i].GroupOrder = i;

                        bool activeIsMember = false;
                        foreach (var member in members)
                        {
                            if (string.Equals(member.Id, group.ActiveWidgetId, StringComparison.Ordinal))
                            {
                                activeIsMember = true;
                                break;
                            }
                        }
                        if (!activeIsMember) group.ActiveWidgetId = members[0].Id;
                    }

                    if (Groups.Count == 0) Groups = null;
                }
            }
            SaveDebounced();
        }

        /// <summary>
        /// Gets a folder by ID
        /// </summary>
        public FolderData GetFolder(string id)
        {
            return Widgets.Find(w => w is FolderData && w.Id == id) as FolderData;
        }
    }
}
