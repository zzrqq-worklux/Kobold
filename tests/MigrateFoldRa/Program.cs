using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Kobold.Core;

namespace Kobold.MigrateFoldRa
{
    /// <summary>
    /// One-time migration from the original FoldRa app to Kobold:
    /// 1. Reads %AppData%\FoldRa\config.json (UTF-8, never modifies it)
    /// 2. Copies non-empty widgets into %AppData%\Kobold\config.json (merged)
    /// 3. Physically moves stored files from the old FoldRa Storage into
    ///    Kobold Storage and rewrites item paths (legacy items keep
    ///    OriginalPath = null, so eject falls back to the desktop)
    /// 4. Takes app settings (theme/icon style/scale/language/startup) from
    ///    the source config, keeping Kobold-only fields on the target.
    /// The target config is backed up to config.json.pre-migrate first.
    /// Usage: MigrateFoldRa.exe [foldraAppDataDir]
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string srcDir = args.Length > 0 ? args[0] : Path.Combine(appData, "FoldRa");
            string dstDir = Path.Combine(appData, "Kobold");

            string srcCfg = Path.Combine(srcDir, "config.json");
            string dstCfg = Path.Combine(dstDir, "config.json");
            string srcStorage = Path.Combine(srcDir, "Storage");
            string dstStorage = Path.Combine(dstDir, "Storage");

            if (!File.Exists(srcCfg))
            {
                Console.WriteLine("Source config not found: " + srcCfg);
                return 2;
            }

            // Refuse to run while either app is alive (config/storage races)
            if (IsProcessRunning("Kobold") || IsProcessRunning("FoldRa"))
            {
                Console.WriteLine("Please exit Kobold and FoldRa first, then run again.");
                return 3;
            }

            // 1. Load source (legacy schema, UTF-8)
            AppConfig src;
            try
            {
                src = JsonConvert.DeserializeObject<AppConfig>(
                    File.ReadAllText(srcCfg, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to parse source config: " + ex.Message);
                return 4;
            }
            if (src == null || src.Folders == null)
            {
                Console.WriteLine("Source config is empty.");
                return 4;
            }

            // 2. Load target (or start fresh); back it up
            AppConfig dst = File.Exists(dstCfg)
                ? JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(dstCfg, Encoding.UTF8)) ?? new AppConfig()
                : new AppConfig();
            if (File.Exists(dstCfg))
            {
                File.Copy(dstCfg, dstCfg + ".pre-migrate", true);
                Console.WriteLine("Target backed up to config.json.pre-migrate");
            }

            // 3. Take app settings from the source (user's real preferences)
            dst.Language = src.Language ?? dst.Language;
            dst.Theme = src.Theme ?? dst.Theme;
            dst.StartWithWindows = src.StartWithWindows;
            dst.DefaultGridColumns = src.DefaultGridColumns > 0 ? src.DefaultGridColumns : dst.DefaultGridColumns;
            dst.IconStyle = src.IconStyle ?? dst.IconStyle;
            dst.ItemScale = src.ItemScale > 0 ? src.ItemScale : dst.ItemScale;

            // 4. Migrate widgets (skip empty shells)
            int widgets = 0, items = 0, movedFiles = 0;
            foreach (var folder in src.Folders)
            {
                if (folder.Items == null || folder.Items.Count == 0)
                {
                    Console.WriteLine("Skip empty widget: '" + folder.Name + "'");
                    continue;
                }
                if (dst.Folders.Exists(f => f.Id == folder.Id))
                {
                    Console.WriteLine("Skip (id already exists): '" + folder.Name + "'");
                    continue;
                }

                foreach (var item in folder.Items)
                {
                    if (string.IsNullOrEmpty(item.Path)) continue;

                    if (!item.IsReference && StorageOps.IsUnder(item.Path, srcStorage))
                    {
                        // Legacy stored file: physically move into Kobold storage
                        string newPath = StorageOps.MoveIntoStorage(item.Path, dstStorage);
                        if (newPath != null)
                        {
                            item.Path = newPath;
                            item.OriginalPath = null; // legacy: no origin recorded
                            movedFiles++;
                            Console.WriteLine("  moved to storage: " + Path.GetFileName(newPath));
                        }
                        else
                        {
                            Console.WriteLine("  WARN could not move (kept old path): " + item.Path);
                        }
                    }
                    items++;
                }

                dst.Folders.Add(folder);
                widgets++;
                Console.WriteLine("Migrated widget '" + folder.Name + "' (" + folder.Items.Count + " items)");
            }

            // 5. Save target (UTF-8, no BOM - same as the app writes)
            string json = JsonConvert.SerializeObject(dst, Formatting.Indented);
            Directory.CreateDirectory(dstDir);
            File.WriteAllText(dstCfg, json, new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine("Done: {0} widgets, {1} items, {2} files moved to Kobold storage.",
                widgets, items, movedFiles);
            Console.WriteLine("Source config was NOT modified.");
            return 0;
        }

        private static bool IsProcessRunning(string name)
        {
            return System.Diagnostics.Process.GetProcessesByName(name).Length > 0;
        }
    }
}
