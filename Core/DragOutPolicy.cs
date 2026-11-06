using System;
using System.Collections.Generic;

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
    /// dragged items. The shell's receipt alone is not trusted: the source path's
    /// actual existence after the drag decides whether the file really left.
    /// Pure so tests/DragOutCheck can cover the combinations.
    /// </summary>
    public static class DragOutPolicy
    {
        /// <summary>
        /// The shell's effect and the source check decide: a source that is still
        /// on disk never gets deleted or ejected on a move/copy receipt, and a
        /// source that is gone is removed instead of ejected.
        /// </summary>
        public static DragOutAction Resolve(ExternalDropEffect effect, bool handledInsideApp,
            bool releasedOutsidePanel, bool locked, bool sourceStillExists)
        {
            // A locked widget never gives its items up.
            if (locked) return DragOutAction.KeepItems;

            // Reordering and cross-widget merges are ours to handle.
            if (handledInsideApp) return DragOutAction.KeepItems;

            // The file already left the widget (or never existed): drop the item,
            // but never "restore" a path that is not there.
            if (!sourceStillExists) return DragOutAction.RemoveItems;

            switch (effect)
            {
                case ExternalDropEffect.Move:
                    // The shell claimed a move but the source is still here. Trust
                    // the disk, not the receipt: a wrong or partial Move would lose
                    // the file, while keeping it can at worst leave a duplicate the
                    // user can clean up.
                    return DragOutAction.KeepItems;
                case ExternalDropEffect.Copy:
                    return DragOutAction.KeepItems;   // the originals are still managed here
                default:
                    return releasedOutsidePanel ? DragOutAction.EjectAndRemove : DragOutAction.KeepItems;
            }
        }

        /// <summary>
        /// Evaluates every dragged path on its own so a mixed selection can
        /// remove, eject or keep each item independently. The caller supplies the
        /// disk check, keeping this policy pure.
        /// </summary>
        public static List<DragOutAction> ResolveSelection(ExternalDropEffect effect, bool handledInsideApp,
            bool releasedOutsidePanel, bool locked, IEnumerable<string> paths,
            Func<string, bool> sourceExists)
        {
            var actions = new List<DragOutAction>();
            if (paths == null) return actions;
            if (sourceExists == null) throw new ArgumentNullException(nameof(sourceExists));

            foreach (string path in paths)
            {
                actions.Add(Resolve(effect, handledInsideApp, releasedOutsidePanel, locked,
                    sourceExists(path)));
            }
            return actions;
        }

        /// <summary>
        /// The un-hidden paths that may be hidden again after the drag: only the
        /// ones whose item stayed managed. Removed or ejected files must stay
        /// visible - their path is gone or was restored elsewhere.
        /// </summary>
        public static List<string> PathsToRehide(IEnumerable<string> unhiddenPaths,
            IEnumerable<string> keptPaths)
        {
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (keptPaths != null)
            {
                foreach (string path in keptPaths)
                {
                    if (!string.IsNullOrEmpty(path)) kept.Add(path);
                }
            }

            var rehide = new List<string>();
            if (unhiddenPaths == null) return rehide;

            foreach (string path in unhiddenPaths)
            {
                if (!string.IsNullOrEmpty(path) && kept.Contains(path)) rehide.Add(path);
            }
            return rehide;
        }
    }
}
