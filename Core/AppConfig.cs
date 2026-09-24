using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Kobold.Core
{
    /// <summary>
    /// Application configuration including all folder widgets
    /// </summary>
    public class AppConfig
    {
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
        public string IconStyle { get; set; }  // classic, modern, minimal, rounded
        public double ItemScale { get; set; }  // Item size scale factor (1.0 = base, 1.3 = Windows standard)
        public List<FolderData> Folders { get; set; }

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
                loaded.Folders.Add(FolderData.Create(Localization.Get("UI_DefaultFolderName"),
                    UiTokens.DefaultFolderColor, 100, 100, loaded.DefaultGridColumns));
                defaulted = true;
            }

            // A file that existed but could not be read gets a timestamped copy
            // before anything can overwrite it - whether or not we recovered.
            if (mainExisted && mainUnreadable) PreserveFailedFile(configPath);
            if (backupExisted && backupUnreadable) PreserveFailedFile(backupPath);

            loaded._configPath = configPath;
            loaded._backupPath = backupPath;
            loaded._skipBackupRefreshOnce =
                mainUnreadable || loadedFromBackup || (defaulted && (mainExisted || backupExisted));

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

                var config = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(path));
                if (config == null)
                {
                    unreadable = true;
                    return null;
                }

                RepairLegacyItems(config);
                return config;
            }
            catch (Exception)
            {
                unreadable = true;
                return null;
            }
        }

        /// <summary>Fills in Names missing from configs written before they existed.</summary>
        private static void RepairLegacyItems(AppConfig config)
        {
            foreach (var folder in config.Folders)
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

                    Utils.EnsureDirectoryExists(Path.GetDirectoryName(configPath));

                    // Create backup of the existing config - except while the
                    // current main file is not trusted (it failed to load, or the
                    // backup is the recovery source we just came from).
                    if (File.Exists(configPath) && !_skipBackupRefreshOnce)
                    {
                        File.Copy(configPath, backupPath, true);
                    }

                    string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                    AtomicFile.WriteAllText(configPath, json);
                    _skipBackupRefreshOnce = false;
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
        /// Adds a new folder widget
        /// </summary>
        public FolderData AddFolder(string name, string color, int posX, int posY, int gridColumns = 3)
        {
            var folder = FolderData.Create(name, color, posX, posY, gridColumns);
            lock (_saveLock)
            {
                Folders.Add(folder);
            }
            SaveDebounced();
            return folder;
        }

        /// <summary>
        /// Removes a folder widget by ID
        /// </summary>
        public void RemoveFolder(string id)
        {
            lock (_saveLock)
            {
                Folders.RemoveAll(f => f.Id == id);
            }
            SaveDebounced();
        }

        /// <summary>
        /// Gets a folder by ID
        /// </summary>
        public FolderData GetFolder(string id)
        {
            return Folders.Find(f => f.Id == id);
        }
    }
}
