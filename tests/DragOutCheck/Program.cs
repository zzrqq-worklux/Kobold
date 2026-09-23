using System;
using System.Text;
using Kobold.Core;

namespace Kobold.DragOutCheck
{
    /// <summary>
    /// Checks for DragOutPolicy: what happens to the dragged items once a drag
    /// that started in a panel ends. The shell reports what it actually did
    /// (move / copy / nothing), and the policy turns that into an action.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/DragOutCheck
    /// </summary>
    internal static class Program
    {
        private static readonly System.Collections.Generic.List<string> Errors =
            new System.Collections.Generic.List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            TheShellTookTheFiles();
            TheShellOnlyCopied();
            TheShellIgnoredTheDrop();
            InternalDropsKeepTheItems();
            LockedWidgetsNeverGiveItemsUp();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL DRAG-OUT CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        private static DragOutAction Resolve(ExternalDropEffect effect, bool handledInsideApp,
            bool releasedOutsidePanel, bool locked = false)
        {
            return DragOutPolicy.Resolve(effect, handledInsideApp, releasedOutsidePanel, locked);
        }

        private static void TheShellTookTheFiles()
        {
            Check(Resolve(ExternalDropEffect.Move, false, true) == DragOutAction.RemoveItems,
                "move: the shell relocated the files, so the items leave the widget");
        }

        private static void TheShellOnlyCopied()
        {
            Check(Resolve(ExternalDropEffect.Copy, false, true) == DragOutAction.KeepItems,
                "copy: the originals are still managed, so the items stay");
            Check(Resolve(ExternalDropEffect.Copy, false, false) == DragOutAction.KeepItems,
                "copy: a copy inside the app keeps the items too");
        }

        private static void TheShellIgnoredTheDrop()
        {
            Check(Resolve(ExternalDropEffect.None, false, true) == DragOutAction.EjectAndRemove,
                "none + outside: an ignored drop keeps the old restore-to-where-it-came-from behaviour");
            Check(Resolve(ExternalDropEffect.None, false, false) == DragOutAction.KeepItems,
                "none + inside: releasing inside the panel never ejects");
        }

        private static void InternalDropsKeepTheItems()
        {
            Check(Resolve(ExternalDropEffect.Move, true, true) == DragOutAction.KeepItems,
                "internal move: a reorder or cross-widget merge keeps the items");
            Check(Resolve(ExternalDropEffect.None, true, false) == DragOutAction.KeepItems,
                "internal none: our own panel handled the drop, nothing is ejected");
        }

        private static void LockedWidgetsNeverGiveItemsUp()
        {
            Check(Resolve(ExternalDropEffect.Move, false, true, locked: true) == DragOutAction.KeepItems,
                "locked: a locked widget keeps its items even when the shell moved files");
            Check(Resolve(ExternalDropEffect.None, false, true, locked: true) == DragOutAction.KeepItems,
                "locked: a locked widget never ejects on an ignored drop");
        }
    }
}
