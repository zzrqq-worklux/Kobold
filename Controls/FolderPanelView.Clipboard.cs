using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Kobold.Core;
using Kobold.Helpers;
using Kobold.Services;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderPanelView - the file clipboard wiring. The payload itself
    /// (FileDrop + Preferred DropEffect) lives in Helpers/FileClipboard;
    /// this file owns the keyboard/menu actions: Ctrl+C/X/V copy or cut the
    /// browse selection and paste Explorer-style into the folder on screen,
    /// while the root mode only ever adds references. Locked widgets refuse
    /// every clipboard action.
    /// </summary>
    public partial class FolderPanelView
    {
        private void CopySelection(List<DisplayItem> selected, bool cut)
        {
            if (selected == null || selected.Count == 0 || IsLocked) return;
            var paths = selected.Where(i => i != null && !string.IsNullOrEmpty(i.Path)).Select(i => i.Path);
            try { Clipboard.SetDataObject(FileClipboard.CreatePayload(paths, cut), true); }
            catch (Exception ex) { Debug.WriteLine("[Kobold] Clipboard failed: " + ex.Message); }
        }

        private static IDataObject TryGetClipboardData()
        {
            try { return Clipboard.GetDataObject(); }
            catch (Exception ex) { Debug.WriteLine("[Kobold] Clipboard read failed: " + ex.Message); return null; }
        }

        /// <summary>Browse mode: paste into the folder on screen (a cut payload moves).</summary>
        private void PasteHere()
        {
            if (IsLocked || !IsBrowsing) return;
            var data = TryGetClipboardData();
            var paths = FileClipboard.PathsFrom(data);
            if (paths.Count == 0) return;
            IntPtr handle = PanelHwnd;
            if (FileClipboard.IsMoveEffect(data))
            {
                var movable = BrowseMove.MovableInto(paths, CurrentBrowsePath,
                    p => File.Exists(p) || Directory.Exists(p));
                if (movable.Count == 0) return;
                if (ShellFileOperations.Move(movable, CurrentBrowsePath, handle))
                {
                    try { Clipboard.Clear(); }
                    catch (Exception ex) { Debug.WriteLine("[Kobold] Clipboard clear failed: " + ex.Message); }
                }
            }
            else ShellFileOperations.Copy(paths, CurrentBrowsePath, handle);
            Dispatcher.BeginInvoke(new Action(UpdateUI));
        }

        /// <summary>Root mode: pasted files become references, never stored.</summary>
        private void PasteIntoWidget()
        {
            if (IsLocked || IsBrowsing) return;
            var paths = FileClipboard.PathsFrom(TryGetClipboardData());
            if (paths.Count == 0) return;
            AddExternalPaths(paths.ToArray());
        }

        private static bool ClipboardHasFiles()
        {
            try { return Clipboard.ContainsFileDropList(); }
            catch (Exception ex) { Debug.WriteLine("[Kobold] Clipboard probe failed: " + ex.Message); return false; }
        }
    }
}
