namespace Kobold.Core
{
    /// <summary>What the shell actually did with a drop that left the panel.</summary>
    public enum ExternalDropEffect
    {
        None,
        Move,
        Copy
    }

    /// <summary>What the widget does with the dragged items once the drag has ended.</summary>
    public enum DragOutAction
    {
        /// <summary>The items stay in the widget.</summary>
        KeepItems,

        /// <summary>The shell relocated the files - the items leave the widget.</summary>
        RemoveItems,

        /// <summary>The shell ignored the drop - restore the files to where they came from and drop the items.</summary>
        EjectAndRemove
    }

    /// <summary>
    /// Turns the result of a drag that started in a panel into an action for the
    /// dragged items. Pure so tests/DragOutCheck can cover the combinations.
    /// </summary>
    public static class DragOutPolicy
    {
        /// <summary>
        /// The shell's effect decides: a move relocated the files, a copy left the
        /// originals alone, and an ignored drop falls back to the panel's own
        /// restore behaviour when the release happened outside the panel.
        /// </summary>
        public static DragOutAction Resolve(ExternalDropEffect effect, bool handledInsideApp,
            bool releasedOutsidePanel, bool locked)
        {
            // A locked widget never gives its items up.
            if (locked) return DragOutAction.KeepItems;

            // Reordering and cross-widget merges are ours to handle.
            if (handledInsideApp) return DragOutAction.KeepItems;

            switch (effect)
            {
                case ExternalDropEffect.Move:
                    return DragOutAction.RemoveItems; // the files are already at the target
                case ExternalDropEffect.Copy:
                    return DragOutAction.KeepItems;   // the originals are still managed here
                default:
                    return releasedOutsidePanel ? DragOutAction.EjectAndRemove : DragOutAction.KeepItems;
            }
        }
    }
}
