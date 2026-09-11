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

                RestoreStoredFile(item);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Kobold] Eject failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Moves a stored file back to its original location (desktop fallback
        /// for legacy items or occupied originals). The hidden attribute travels
        /// with the file, so it is cleared on the restored copy. Returns the new
        /// path, or null when the file could not be moved.
        /// </summary>
        private string RestoreStoredFile(WidgetItem item)
        {
            string restoredTo = !string.IsNullOrEmpty(item.OriginalPath) &&
                                StorageOps.TryRestoreToOriginal(item.Path, item.OriginalPath)
                ? item.OriginalPath
                : RestoreToDesktopFallback(item.Path);

            if (restoredTo != null) StorageOps.SetHidden(restoredTo, false);
            return restoredTo;
        }

        /// <summary>
        /// Moves a stored file back to where it came from while keeping the item
        /// in the widget as a reference (the inverse of "store").
        /// </summary>
        private void UnstoreItem(WidgetItem item)
        {
            try
            {
                string restoredTo = RestoreStoredFile(item);
                if (restoredTo == null) return;

                item.Path = restoredTo;
                item.Name = System.IO.Path.GetFileName(restoredTo);
                item.IsReference = true;
                item.OriginalPath = null;

                UpdateUI();
                OnDataChanged?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Kobold] Unstore failed: {ex.Message}");
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
