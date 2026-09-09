using System;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - Eject operations. Ejecting an item means "stop managing
    /// this file": references are un-hidden (the file never moved), stored items
    /// are moved back to their original path (desktop as legacy fallback).
    /// </summary>
    public partial class FolderWidget
    {
        #region Eject

        /// <summary>
        /// Ejects one item from the widget without removing it from the list:
        /// - Reference item: file never moved - unhide its desktop source (if any)
        /// - Stored item with OriginalPath: move back to the original location
        /// - Stored legacy item (no OriginalPath): fall back to the desktop
        /// </summary>
        private void EjectItem(WidgetItem item)
        {
            try
            {
                if (item.IsReference)
                {
                    // Reference: file stays where it is - just make it visible again
                    StorageOps.SetHidden(item.Path, false);
                    return;
                }

                string restoredTo = null;
                if (!string.IsNullOrEmpty(item.OriginalPath) &&
                    StorageOps.TryRestoreToOriginal(item.Path, item.OriginalPath))
                {
                    restoredTo = item.OriginalPath;
                }
                else
                {
                    // Legacy item (no recorded original) or original occupied/missing
                    restoredTo = RestoreToDesktopFallback(item.Path);
                }

                // Hidden attribute travels with the file - clear it on the restored copy
                if (restoredTo != null)
                {
                    StorageOps.SetHidden(restoredTo, false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Kobold] Eject failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ejects every item (used when the widget is deleted)
        /// </summary>
        private void EjectAllItems()
        {
            foreach (var item in _data.Items)
            {
                EjectItem(item);
            }
        }

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
        /// Fallback for stored files that cannot go back to their original path:
        /// moves them from Kobold storage to the desktop. Returns the new path,
        /// or null when the file is not in storage / could not be moved.
        /// </summary>
        private string RestoreToDesktopFallback(string filePath)
        {
            string storagePath = Utils.GetStoragePath();

            // Only handle files that are in our storage folder
            if (!StorageOps.IsUnder(filePath, storagePath))
                return null;

            string fileName = System.IO.Path.GetFileName(filePath);
            string destPath = GetUniqueDesktopPath(fileName);
            
            if (System.IO.Directory.Exists(filePath))
            {
                System.IO.Directory.Move(filePath, destPath);
                return destPath;
            }
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Move(filePath, destPath);
                return destPath;
            }
            return null;
        }

        #endregion
    }
}
