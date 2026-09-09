using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FoldRa.Core
{
    /// <summary>
    /// Application configuration including all folder widgets
    /// </summary>
    public class AppConfig
    {
        public string Language { get; set; }
        public string Theme { get; set; }
        public bool StartWithWindows { get; set; }
        public int DefaultGridColumns { get; set; }
        public string IconStyle { get; set; }  // classic, modern, minimal, rounded
        public double ItemScale { get; set; }  // Item size scale factor (1.0 = base, 1.3 = Windows standard)
        public List<FolderData> Folders { get; set; }

        public AppConfig()
        {
            Language = "en";
            Theme = "dark";
            StartWithWindows = true;
            DefaultGridColumns = 3;
            IconStyle = "classic";
            ItemScale = 1.1; // Default to slightly larger icons
            Folders = new List<FolderData>();
        }

        /// <summary>
        /// Loads configuration from JSON file, or creates default if not exists
        /// </summary>
        public static AppConfig Load()
        {
            string configPath = Utils.GetConfigPath();
            string backupPath = configPath + ".backup";
            
            // Try main config first, then backup
            foreach (string path in new[] { configPath, backupPath })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path);
                        var config = JsonConvert.DeserializeObject<AppConfig>(json);
                        
                        if (config != null)
                        {
                            // Fix missing Names in WidgetItems (for old configs)
                            foreach (var folder in config.Folders)
                            {
                                foreach (var item in folder.Items)
                                {
                                    if (string.IsNullOrEmpty(item.Name) && !string.IsNullOrEmpty(item.Path))
                                    {
                                        item.Name = System.IO.Path.GetFileName(item.Path);
                                    }
                                }
                            }
                            return config;
                        }
                    }
                }
                catch (Exception)
                {
                    // Try next file (backup)
                }
            }

            // Create and save default config with one folder
            var defaultConfig = new AppConfig();
            defaultConfig.Folders.Add(FolderData.Create("My Folder", "#3B82F6", 100, 100, defaultConfig.DefaultGridColumns));
            defaultConfig.Save();
            return defaultConfig;
        }

        private System.Timers.Timer _saveTimer;
        private readonly object _saveLock = new object();
        
        /// <summary>
        /// Saves current configuration to JSON file with backup
        /// </summary>
        public void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    string configPath = Utils.GetConfigPath();
                    string backupPath = configPath + ".backup";
                    
                    Utils.EnsureDirectoryExists(Path.GetDirectoryName(configPath));
                    
                    // Create backup of existing config
                    if (File.Exists(configPath))
                    {
                        File.Copy(configPath, backupPath, true);
                    }
                    
                    string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                    File.WriteAllText(configPath, json);
                }
                catch (Exception)
                {
                    // Silently fail - config backup exists if needed
                }
            }
        }

        /// <summary>
        /// Schedules a save operation (Debounce)
        /// Prevents disk spamming during rapid changes (dragging, etc.)
        /// </summary>
        public void SaveDebounced(int delayMs = 1000)
        {
            if (_saveTimer == null)
            {
                _saveTimer = new System.Timers.Timer(delayMs);
                _saveTimer.AutoReset = false;
                _saveTimer.Elapsed += (s, e) => Save();
            }
            
            _saveTimer.Stop();
            _saveTimer.Interval = delayMs;
            _saveTimer.Start();
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


