namespace Kobold.Controls
{
    /// <summary>
    /// Shared state for the single in-flight drag operation. A drop target uses
    /// it to tell the drag source whether another widget will take the items, so
    /// the source must not eject them back to the desktop. WPF drag-drop is
    /// single-threaded - all access happens on the UI thread.
    /// </summary>
    internal sealed class DragDropSession
    {
        public static DragDropSession Current { get; private set; }

        public string SourceFolderId { get; }
        public bool SourceIsLocked { get; }

        /// <summary>True while the cursor is over a widget that can take the items.</summary>
        public bool PendingMove { get; private set; }

        /// <summary>True while the cursor is over a widget that refused the drop.</summary>
        public bool TargetRefused { get; private set; }

        /// <summary>True once another widget has merged the items.</summary>
        public bool AcceptedByOtherWidget { get; private set; }

        private DragDropSession(string sourceFolderId, bool sourceIsLocked)
        {
            SourceFolderId = sourceFolderId;
            SourceIsLocked = sourceIsLocked;
        }

        public static DragDropSession Begin(string sourceFolderId, bool sourceIsLocked)
        {
            Current = new DragDropSession(sourceFolderId, sourceIsLocked);
            return Current;
        }

        public static void End()
        {
            Current = null;
        }

        public void MarkPendingMove()
        {
            PendingMove = true;
            TargetRefused = false;
        }

        public void MarkTargetRefused()
        {
            PendingMove = false;
            TargetRefused = true;
        }

        /// <summary>The cursor left a target window - forget its last state.</summary>
        public void ClearTargetState()
        {
            PendingMove = false;
            TargetRefused = false;
        }

        /// <summary>Called by the target widget after it merged the items.</summary>
        public void MarkAcceptedByOtherWidget()
        {
            AcceptedByOtherWidget = true;
        }
    }
}
