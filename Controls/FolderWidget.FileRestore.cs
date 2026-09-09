using System;
using FoldRa.Core;

namespace FoldRa.Controls
{
    /// <summary>
    /// FolderWidget - File restore operations (restore to desktop, copy directory)
    /// </summary>
    public partial class FolderWidget
    {
        #region File Restore
        
        /// <summary>
        /// Gets a unique file path on desktop (handles duplicates)
        /// </summary>
        private string GetUniqueDesktopPath(string fileName)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string destPath = System.IO.Path.Combine(desktopPath, fileName);
            
            int counter = 1;
            string nameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(fileName);
            string ext = System.IO.Path.GetExtension(fileName);
            
            while (System.IO.File.Exists(destPath) || System.IO.Directory.Exists(destPath))
            {
                destPath = System.IO.Path.Combine(desktopPath, $"{nameWithoutExt} ({counter}){ext}");
                counter++;
            }
            return destPath;
        }
        
        /// <summary>
        /// Restores a file/folder from FoldRa storage back to desktop
        /// </summary>
        private bool RestoreToDesktop(string filePath)
        {
            string storagePath = Utils.GetStoragePath();
            
            // Only restore files that are in our storage folder
            if (!filePath.StartsWith(storagePath, StringComparison.OrdinalIgnoreCase))
                return false;
            
            string fileName = System.IO.Path.GetFileName(filePath);
            string destPath = GetUniqueDesktopPath(fileName);
            
            if (System.IO.Directory.Exists(filePath))
            {
                System.IO.Directory.Move(filePath, destPath);
                return true;
            }
            else if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Move(filePath, destPath);
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Restores all items from storage back to desktop when widget is deleted
        /// </summary>
        private void RestoreItemsToDesktop()
        {
            foreach (var item in _data.Items)
            {
                try
                {
                    RestoreToDesktop(item.Path);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FoldRa] Restore failed: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Restores a single item from storage back to desktop
        /// </summary>
        private void RestoreSingleItemToDesktop(string filePath)
        {
            try
            {
                RestoreToDesktop(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FoldRa] Restore single item failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Recursively copies a directory (for cross-drive moves)
        /// </summary>
        private void CopyDirectory(string sourceDir, string destDir)
        {
            var dir = new System.IO.DirectoryInfo(sourceDir);
            System.IO.Directory.CreateDirectory(destDir);
            
            foreach (var file in dir.GetFiles())
            {
                file.CopyTo(System.IO.Path.Combine(destDir, file.Name), false);
            }
            
            foreach (var subDir in dir.GetDirectories())
            {
                CopyDirectory(subDir.FullName, System.IO.Path.Combine(destDir, subDir.Name));
            }
        }
        
        #endregion
    }
}


