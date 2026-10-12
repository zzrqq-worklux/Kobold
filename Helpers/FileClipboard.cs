using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Kobold.Helpers
{
    public enum ClipboardAction { None, Copy, Cut, Paste }

    /// <summary>
    /// The Windows file-clipboard payload shared with Explorer: a FileDrop
    /// list plus the four-byte "Preferred DropEffect" stream (Copy=1 /
    /// Move=2), and the Ctrl+C/X/V resolution for the folder widget. The
    /// payload functions are pure - callers own the real system clipboard.
    /// </summary>
    public static class FileClipboard
    {
        public const string PreferredDropEffectFormat = "Preferred DropEffect";
        public const int CopyEffect = 1;
        public const int MoveEffect = 2;

        /// <summary>
        /// Builds the clipboard payload: the FileDrop list (blank paths
        /// skipped) plus the drop-effect stream a paste can read back.
        /// </summary>
        public static DataObject CreatePayload(IEnumerable<string> paths, bool move)
        {
            var files = new StringCollection();
            if (paths != null)
            {
                foreach (var path in paths)
                {
                    if (!string.IsNullOrWhiteSpace(path)) files.Add(path);
                }
            }

            var data = new DataObject();
            data.SetFileDropList(files);
            data.SetData(PreferredDropEffectFormat,
                new MemoryStream(BitConverter.GetBytes(move ? MoveEffect : CopyEffect)));
            return data;
        }

        /// <summary>
        /// File list from a payload (string[] or StringCollection form);
        /// blank entries are dropped and anything unreadable reads as empty.
        /// </summary>
        public static List<string> PathsFrom(IDataObject data)
        {
            var paths = new List<string>();
            if (data == null) return paths;

            try
            {
                object payload = data.GetData(DataFormats.FileDrop);
                var files = payload as string[];
                if (files != null)
                {
                    foreach (var file in files) AddIfPresent(paths, file);
                    return paths;
                }

                var collection = payload as StringCollection;
                if (collection != null)
                {
                    foreach (var file in collection) AddIfPresent(paths, file);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Clipboard file list unreadable: " + ex.Message);
            }
            return paths;
        }

        /// <summary>
        /// True when the payload asks for a move (Ctrl+X); a missing or
        /// malformed effect stream reads as copy, never as a destructive move.
        /// </summary>
        public static bool IsMoveEffect(IDataObject data)
        {
            if (data == null) return false;

            try
            {
                var stream = data.GetData(PreferredDropEffectFormat) as MemoryStream;
                if (stream == null || stream.Length < 4) return false;
                int effect = BitConverter.ToInt32(stream.ToArray(), 0);
                return (effect & MoveEffect) != 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Clipboard drop effect unreadable: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Resolves a key chord: Ctrl+C copies anywhere, Ctrl+X cuts only in
        /// the browse view, Ctrl+V pastes; Shift/Alt, a locked panel or a
        /// missing Ctrl reject the chord.
        /// </summary>
        public static ClipboardAction Resolve(bool ctrl, bool shift, bool alt, bool browsing, bool locked, Key key)
        {
            if (!ctrl || shift || alt || locked) return ClipboardAction.None;

            switch (key)
            {
                case Key.C: return ClipboardAction.Copy;
                case Key.X: return browsing ? ClipboardAction.Cut : ClipboardAction.None;
                case Key.V: return ClipboardAction.Paste;
                default: return ClipboardAction.None;
            }
        }

        private static void AddIfPresent(List<string> paths, string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        }
    }
}
