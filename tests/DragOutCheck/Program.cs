using System;
using System.Text;
using Kobold.Core;

namespace Kobold.DragOutCheck
{
    /// <summary>
    /// Checks for DragOutPolicy: what happens to the dragged items once a drag
    /// that started in a panel ends. The shell reports what it actually did
    /// (move / copy / nothing), and the policy reconciles that receipt with the
    /// source path's actual existence before turning it into an action.
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
            MixedSelectionIsDecidedPerItem();
            OnlyKeptItemsAreHiddenAgain();
            ExternalDropPolicyPins();

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
            bool releasedOutsidePanel, bool locked = false, bool sourceStillExists = true)
        {
            return DragOutPolicy.Resolve(effect, handledInsideApp, releasedOutsidePanel, locked,
                sourceStillExists);
        }

        private static void TheShellTookTheFiles()
        {
            Check(Resolve(ExternalDropEffect.Move, false, true, sourceStillExists: true) == DragOutAction.KeepItems,
                "move + source still here: a wrong/partial move receipt keeps the file (worst case: a duplicate)");
            Check(Resolve(ExternalDropEffect.Move, false, true, sourceStillExists: false) == DragOutAction.RemoveItems,
                "move + source gone: the shell relocated the file, so the item leaves the widget");
        }

        private static void TheShellOnlyCopied()
        {
            Check(Resolve(ExternalDropEffect.Copy, false, true, sourceStillExists: true) == DragOutAction.KeepItems,
                "copy + source still here: the originals are still managed, so the items stay");
            Check(Resolve(ExternalDropEffect.Copy, false, false, sourceStillExists: true) == DragOutAction.KeepItems,
                "copy + inside: a copy inside the app keeps the items too");
            Check(Resolve(ExternalDropEffect.Copy, false, true, sourceStillExists: false) == DragOutAction.RemoveItems,
                "copy + source gone: the file left the widget, so remove the item");
        }

        private static void TheShellIgnoredTheDrop()
        {
            Check(Resolve(ExternalDropEffect.None, false, true, sourceStillExists: true) == DragOutAction.EjectAndRemove,
                "none + outside + source still here: the intentional drag-out-to-unmanaged restore behaviour");
            Check(Resolve(ExternalDropEffect.None, false, true, sourceStillExists: false) == DragOutAction.RemoveItems,
                "none + outside + source gone: do not eject a missing file");
            Check(Resolve(ExternalDropEffect.None, false, false, sourceStillExists: true) == DragOutAction.KeepItems,
                "none + inside: releasing inside the panel never ejects");
            Check(Resolve(ExternalDropEffect.None, false, false, sourceStillExists: false) == DragOutAction.RemoveItems,
                "none + inside + source gone: the file is gone, so the item leaves the widget");
        }

        private static void InternalDropsKeepTheItems()
        {
            Check(Resolve(ExternalDropEffect.Move, true, true) == DragOutAction.KeepItems,
                "internal move: a reorder or cross-widget merge keeps the items");
            Check(Resolve(ExternalDropEffect.None, true, false) == DragOutAction.KeepItems,
                "internal none: our own panel handled the drop, nothing is ejected");
            Check(Resolve(ExternalDropEffect.Move, true, true, sourceStillExists: false) == DragOutAction.KeepItems,
                "internal move + source gone: the in-app drop outranks the existence check");
            Check(Resolve(ExternalDropEffect.Copy, true, false, sourceStillExists: false) == DragOutAction.KeepItems,
                "internal copy + source gone: the in-app drop outranks the existence check");
            Check(Resolve(ExternalDropEffect.None, true, true, sourceStillExists: false) == DragOutAction.KeepItems,
                "internal none + source gone: the in-app drop outranks the existence check");
        }

        private static void LockedWidgetsNeverGiveItemsUp()
        {
            Check(Resolve(ExternalDropEffect.Move, false, true, locked: true) == DragOutAction.KeepItems,
                "locked: a locked widget keeps its items even when the shell moved files");
            Check(Resolve(ExternalDropEffect.None, false, true, locked: true) == DragOutAction.KeepItems,
                "locked: a locked widget never ejects on an ignored drop");
            Check(Resolve(ExternalDropEffect.Move, false, true, locked: true, sourceStillExists: false) == DragOutAction.KeepItems,
                "locked + source gone: the lock still keeps every item");
            Check(Resolve(ExternalDropEffect.Copy, false, true, locked: true, sourceStillExists: false) == DragOutAction.KeepItems,
                "locked copy + source gone: the lock still keeps every item");
            Check(Resolve(ExternalDropEffect.None, false, true, locked: true, sourceStillExists: false) == DragOutAction.KeepItems,
                "locked none + source gone: the lock still keeps every item");
        }

        private static void MixedSelectionIsDecidedPerItem()
        {
            const string movedPath = @"C:\drag\moved.txt";
            const string skippedPath = @"C:\drag\skipped.txt";

            var moveActions = DragOutPolicy.ResolveSelection(ExternalDropEffect.Move, false, true, false,
                new[] { movedPath, skippedPath }, p => p == skippedPath);
            Check(moveActions.Count == 2 &&
                  moveActions[0] == DragOutAction.RemoveItems &&
                  moveActions[1] == DragOutAction.KeepItems,
                "mixed move: a missing item leaves while a still-present item stays");

            var copyActions = DragOutPolicy.ResolveSelection(ExternalDropEffect.Copy, false, true, false,
                new[] { movedPath, skippedPath }, p => p == skippedPath);
            Check(copyActions.Count == 2 &&
                  copyActions[0] == DragOutAction.RemoveItems &&
                  copyActions[1] == DragOutAction.KeepItems,
                "mixed copy: a missing item leaves while a still-present item stays");

            var noneActions = DragOutPolicy.ResolveSelection(ExternalDropEffect.None, false, true, false,
                new[] { movedPath, skippedPath }, p => p == skippedPath);
            Check(noneActions.Count == 2 &&
                  noneActions[0] == DragOutAction.RemoveItems &&
                  noneActions[1] == DragOutAction.EjectAndRemove,
                "mixed none + outside: a missing item is removed while a present item is ejected");

            var lockedActions = DragOutPolicy.ResolveSelection(ExternalDropEffect.None, false, true, true,
                new[] { movedPath, skippedPath }, p => false);
            Check(lockedActions.Count == 2 &&
                  lockedActions[0] == DragOutAction.KeepItems &&
                  lockedActions[1] == DragOutAction.KeepItems,
                "mixed locked: every item stays whatever happened to its source");

            var emptyActions = DragOutPolicy.ResolveSelection(ExternalDropEffect.Move, false, true, false,
                null, p => true);
            Check(emptyActions.Count == 0, "mixed: no dragged paths means no decisions");
        }

        private static void OnlyKeptItemsAreHiddenAgain()
        {
            const string keptPath = @"C:\drag\kept.txt";
            const string removedPath = @"C:\drag\removed.txt";
            const string ejectedPath = @"C:\drag\ejected.txt";

            var rehide = DragOutPolicy.PathsToRehide(
                new[] { keptPath, removedPath, ejectedPath }, new[] { keptPath });
            Check(rehide.Count == 1 && rehide[0] == keptPath,
                "hidden state: only a kept item may be hidden again; removed/ejected files stay visible");

            var caseMatched = DragOutPolicy.PathsToRehide(
                new[] { @"C:\DRAG\KEPT.TXT" }, new[] { keptPath });
            Check(caseMatched.Count == 1, "hidden state: path matching ignores case");

            Check(DragOutPolicy.PathsToRehide(new[] { keptPath }, null).Count == 0,
                "hidden state: nothing is re-hidden when no item stayed");
            Check(DragOutPolicy.PathsToRehide(null, new[] { keptPath }).Count == 0,
                "hidden state: no un-hidden path is a no-op");
        }

        private static void ExternalDropPolicyPins()
        {
            Check(ExternalDropPolicy.Accepts(true, false, true, false),
                "external drop: the browse background accepts a file drop");
            Check(ExternalDropPolicy.Accepts(true, false, true, false) &&
                  ExternalDropPolicy.TargetDirectory(true, @"C:\a\sub", @"C:\a") == @"C:\a\sub",
                "external drop: a folder row accepts and targets the subfolder");
            Check(!ExternalDropPolicy.Accepts(true, false, true, true),
                "external drop: a file row refuses");
            Check(!ExternalDropPolicy.Accepts(true, true, true, false),
                "external drop: a locked widget refuses");
            Check(!ExternalDropPolicy.Accepts(true, false, false, false),
                "external drop: a non-file payload refuses");
            Check(!ExternalDropPolicy.Accepts(false, false, true, false),
                "external drop: root mode never uses the browse policy");
            Check(ExternalDropPolicy.TargetDirectory(true, null, @"C:\a") == @"C:\a" &&
                  ExternalDropPolicy.TargetDirectory(false, @"C:\a\sub", @"C:\a") == @"C:\a",
                "external drop: a blank folder row falls back to the current folder");
            Check(ExternalDropPolicy.IsExternalFileDrop(true, InternalDragFormats.None),
                "external drop: a plain FileDrop is external");
            Check(!ExternalDropPolicy.IsExternalFileDrop(true, InternalDragFormats.KoboldItem),
                "external drop: a root-widget drag is not external");
            Check(!ExternalDropPolicy.IsExternalFileDrop(true,
                    InternalDragFormats.KoboldItem | InternalDragFormats.KoboldSourceFolderId),
                "external drop: a cross-widget drag is not external");
            Check(!ExternalDropPolicy.IsExternalFileDrop(true, InternalDragFormats.BrowsePaths),
                "external drop: a browse-panel drag is not external");
            Check(!ExternalDropPolicy.IsExternalFileDrop(false, InternalDragFormats.None),
                "external drop: a payload without FileDrop is not external");
            Check(ExternalDropPolicy.FromFormats(new[] { ExternalDropPolicy.KoboldItemFormat }) ==
                  InternalDragFormats.KoboldItem,
                "external drop: the format name maps to the item marker");
            Check(ExternalDropPolicy.FromFormats(new[]
                  {
                      ExternalDropPolicy.KoboldItemFormat,
                      ExternalDropPolicy.KoboldSourceFolderFormat,
                      ExternalDropPolicy.BrowsePathsFormat
                  }) == (InternalDragFormats.KoboldItem |
                          InternalDragFormats.KoboldSourceFolderId |
                          InternalDragFormats.BrowsePaths),
                "external drop: combined format names map to combined flags");
            Check(ExternalDropPolicy.FromFormats(new[] { "FileDrop", "Text" }) == InternalDragFormats.None,
                "external drop: unknown formats add no marker");
            Check(!ExternalDropPolicy.IsExternalFileDrop(true,
                    ExternalDropPolicy.FromFormats(new[]
                    {
                        "FileDrop",
                        ExternalDropPolicy.KoboldItemFormat
                    })),
                "external drop: a root-widget format list is not external");
        }
    }
}
