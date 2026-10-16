using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using IO = System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Kobold.Controls;
using Kobold.Controls.IconRenderers;
using Kobold.Core;
using Kobold.Helpers;
using Kobold.Windows;
using Localization = Kobold.Core.Localization;

namespace Kobold.XamlLoadCheck
{
    /// <summary>
    /// Loads the XAML windows that consume the runtime-installed theme tokens
    /// (Kobold.Radius.*, Kobold.Font.*, Kobold.Brush.*) so resource/type
    /// mismatches fail here instead of crashing the app when a user opens a
    /// window. Exit code 0 = all loaded; 1 = failures. Run via:
    /// dotnet run --project tests/XamlLoadCheck
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// The add pill's top margin (fix round 18): the missing half-leading
        /// that puts the pill's top border at the list's inter-row pitch.
        /// </summary>
        private const double ADD_GAP = 6;

        // The pin's hash for a file that does not exist (start state = absent,
        // end state must be absent too).
        private const string AbsentHash = "absent";

        // The singleton's config sandbox and the real config's startup byte
        // state, so the tail pin can prove the checks left the real files
        // untouched (the day-switch test used to rewrite them via the debounce).
        private static string _checkSandbox;
        private static string _realConfigPath;
        private static string _realBackupPath;
        private static string _realConfigHashBefore;
        private static string _realBackupHashBefore;

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            // Redirect the WidgetManager singleton to a throwaway sandbox
            // before anything can touch it, and snapshot the real config's
            // bytes first so the tail pin can compare against them. The
            // sandbox starts empty, so the checks always run on defaults.
            _checkSandbox = IO.Path.Combine(IO.Path.GetTempPath(), "kobold-check-xamlload");
            if (IO.Directory.Exists(_checkSandbox)) IO.Directory.Delete(_checkSandbox, true);
            IO.Directory.CreateDirectory(_checkSandbox);
            _realConfigPath = Utils.GetConfigPath();
            _realBackupPath = _realConfigPath + ".backup";
            _realConfigHashBefore = HashOrAbsent(_realConfigPath);
            _realBackupHashBefore = HashOrAbsent(_realBackupPath);
            WidgetManager.OverrideConfigPathsForChecks(
                IO.Path.Combine(_checkSandbox, "config.json"),
                IO.Path.Combine(_checkSandbox, "config.json.backup"));

            try
            {
                Console.WriteLine("step: start");
                var app = new App();
                Console.WriteLine("step: app created");
                app.InitializeComponent(); // Application resources: App.xaml + Themes/Controls.xaml
                Console.WriteLine("step: app resources loaded");

                // Mirror App.OnStartup: WidgetManager initializes ThemeManager and
                // installs the token resources before any window is constructed.
                WidgetManager.Instance.ToString();
                Console.WriteLine("step: theme tokens installed");

                int failures = 0;
                failures += TryLoad("SettingsWindow", () => new SettingsWindow());
                failures += TryLoad("IslandWindow", () => new IslandWindow());
                failures += TryLoad("FolderWidget", () => new FolderWidget(new FolderData()));

                // The shared widget shell must construct and repaint for every
                // theme a host window can be in.
                failures += TryLoad("WidgetChrome [dark]", () => LoadChrome("dark"));
                failures += TryLoad("WidgetChrome [light]", () => LoadChrome("light"));
                failures += TryLoad("WidgetChrome [high contrast]", () => LoadChrome("high contrast"));

                // The first kind-specific host must load its chrome and render
                // its calendar + day list content under every theme too.
                failures += TryLoad("TodoWidget [dark]", () => LoadTodo("dark"));
                failures += TryLoad("TodoWidget [light]", () => LoadTodo("light"));
                failures += TryLoad("TodoWidget [high contrast]", () => LoadTodo("high contrast"));
                failures += TryLoad("TodoWidget with items [dark]", LoadTodoWithItems);

                // Task T10: the note host must load its chrome + the panel
                // view skeleton under every theme, with and without notes.
                failures += TryLoad("NoteWidget [dark]", () => LoadNote("dark"));
                failures += TryLoad("NoteWidget [light]", () => LoadNote("light"));
                failures += TryLoad("NoteWidget [high contrast]", () => LoadNote("high contrast"));
                failures += TryLoad("NoteWidget with notes [dark]", LoadNoteWithNotes);

                // Regression pins for the drag-anchor defect the manual smoke
                // found (see report): a forwarded move must never move the
                // window before its start event, and the chrome must raise the
                // start while its internal drag flag is still false.
                failures += TryCheck("drag: move without start is ignored", DragMoveWithoutStartIsIgnored);
                failures += TryCheck("drag: start then move moves by the DPI-scaled delta", DragStartThenMoveMoves);
                failures += TryCheck("chrome: start event precedes the drag flag", ChromeRaisesStartBeforeDragActive);

                // Task F1 pins: the file-clipboard payload (FileDrop +
                // "Preferred DropEffect") round-trips through plain DataObjects
                // and Resolve follows the Ctrl+C/X/V truth table (browsing
                // gates Cut; locked / Shift / Alt reject). The real system
                // clipboard is never touched.
                failures += TryCheck("files: the clipboard payload round-trips and Resolve follows the truth table",
                    FileClipboardPayloadAndResolve);

                // Task F4 pin: the shared ComboBox template grows an editable
                // text host - an editable combo shows PART_EditableTextBox,
                // while a plain combo keeps it collapsed and still renders
                // the selection presenter.
                failures += TryCheck("combo: the editable template hosts a text box", EditableComboBoxSupportsTextEntry);
                failures += TryCheck("combo: the editable text is not clipped", EditableComboBoxTextIsNotClipped);

                // Task A2.2 pins: the persisted panel size wins over the legacy
                // content maths (with the kind minimum applied); the resize
                // protocol mirrors the drag anchor rule (capture -> event ->
                // active, a move without a start is ignored) with per-edge
                // maths clamped to [kind minimum, work area]; a finished
                // resize persists size and position without an island rebuild,
                // while a click without movement writes nothing; the chrome's
                // resize bands/grip disappear when the panel is locked.
                failures += TryCheck("panel: persisted size wins", PanelPersistedSizeWins);
                failures += TryCheck("panel: persisted size below the minimum is clamped", PanelPersistedBelowMinimumIsClamped);
                failures += TryCheck("panel: missing sizes fall back to the legacy content size", PanelMissingSizesFallBackToTheLegacyContentSize);
                failures += TryCheck("resize: move without start is ignored", ResizeMoveWithoutStartIsIgnored);
                failures += TryCheck("resize: bottom-right start then move resizes by the DPI-scaled delta", ResizeBottomRightStartThenMoveScalesByDpi);
                failures += TryCheck("resize: left edge keeps the right edge fixed", ResizeLeftEdgeKeepsTheRightEdgeFixed);
                failures += TryCheck("resize: top edge keeps the bottom edge fixed", ResizeTopEdgeKeepsTheBottomEdgeFixed);
                failures += TryCheck("resize: shrinking clamps at the kind minimum", ResizeShrinkingClampsAtTheKindMinimum);
                failures += TryCheck("resize: growing clamps at the work area", ResizeGrowingClampsAtTheWorkArea);
                failures += TryCheck("resize: during-drag writes nothing", ResizeDuringDragWritesNothing);
                failures += TryCheck("resize: end persists size and position", ResizeEndPersistsSizeAndPosition);
                failures += TryCheck("resize: a band click without movement does not persist", ResizeBandClickWithoutMovementDoesNotPersist);
                failures += TryCheck("chrome: resize start event precedes the resize flag", ChromeResizeStartPrecedesTheResizeFlag);
                failures += TryCheck("chrome: locked panels expose no resize handles", ChromeLockedPanelsExposeNoResizeHandles);
                failures += TryCheck("chrome: resize cursors map to the edges", ChromeResizeCursorsMapToTheEdges);
                failures += TryCheck("resize: locked band press is ignored", ResizeLockedBandPressIsIgnored);

                // Fix round 1 pins: the shell backdrop follows the widget color
                // (and returns to the neutral surface), and a resize re-fits the
                // panel into the work area.
                failures += TryCheck("chrome: panel color tints the shell backdrop", ChromePanelColorTintsBackdrop);
                failures += TryCheck("chrome: cleared panel color returns the neutral surface", ChromePanelColorClearedReturnsNeutral);
                failures += TryCheck("resize: fit keeps the panel inside the work area", FitPanelKeepsPanelInWorkArea);

                // Fix round 2 pins: no todo text box may clip its line box -
                // the shared TextBox style's 10,6 padding used to eat the 28px
                // add row, and the offscreen layout makes the numbers testable.
                failures += TryCheck("todo: add box fits its line box", TodoAddBoxIsNotClipped);
                failures += TryCheck("todo: item editor fits its line box", TodoItemEditorIsNotClipped);
                failures += TryCheck("todo: child adder fits its line box", TodoChildAdderIsNotClipped);

                // Fix round 3 pins: the add row starts as a pill and reveals the
                // input on click; fix round 11 (R2): on an empty day the pill
                // stays and stacks below the input, while a non-empty day keeps
                // replacing it; the list type grew while the calendar shrank.
                failures += TryCheck("todo: add row is a pill until clicked", TodoAddRowIsPillUntilClicked);
                failures += TryCheck("todo: non-empty reveal replaces the pill", TodoNonEmptyRevealReplacesThePill);
                failures += TryCheck("todo: empty-day reveal keeps the pill", TodoEmptyRevealedInputKeepsThePill);
                failures += TryCheck("todo: day list uses the larger type scale", TodoListTextIsLarger);
                failures += TryCheck("todo: the calendar cell height follows the panel", TodoCalendarCellHeightTracksThePanel);

                // Fix round 4 pins: an open inline editor auto-commits when it
                // loses focus. The routed event is raised directly and the
                // deferred delegate run via reflection (a real dispatcher pump
                // would execute the dormant App.OnStartup op); the window-level
                // outside-click blur still needs the live smoke.
                failures += TryCheck("todo: editor commits on focus loss", TodoEditorCommitsOnFocusLoss);
                failures += TryCheck("todo: empty editor deletes on focus loss", TodoEmptyEditorDeletesOnFocusLoss);
                failures += TryCheck("todo: canceled editor does not commit on focus loss", TodoCanceledEditorDoesNotCommitOnFocusLoss);
                failures += TryCheck("todo: adder blur commits once and closes", TodoAdderBlurCommitsOnceAndCloses);
                failures += TryCheck("todo: adder Enter then blur adds once", TodoAdderEnterThenBlurAddsOnce);
                failures += TryCheck("todo: empty adder blur closes without adding", TodoAdderEmptyBlurClosesWithoutAdding);

                // Smoke fix C (new spec): Enter in a parent editor commits and
                // opens that item's child adder; Enter on an empty child adder
                // jumps to the next day item's adder (the last parent jumps to
                // the bottom add input); the add row's Enter hands the caret
                // to the new item's adder.
                failures += TryCheck("todo: parent editor Enter opens the child adder", TodoParentEditorEnterOpensChildAdder);
                failures += TryCheck("todo: empty adder Enter opens the next parent's editor", TodoAdderEmptyEnterOpensTheNextParentEditor);
                failures += TryCheck("todo: add-row Enter opens the new item's adder", TodoAddRowEnterOpensTheNewItemsAdder);

                // B2 (Enter chain, user 2026-10-13): a parent's Enter hands the
                // caret to its first child's editor (the adder only when there
                // is no child); a child's Enter walks to the next sibling's
                // editor (the adder after the last); every chain-opened editor
                // selects all and an emptied box still deletes without walking.
                failures += TryCheck("todo: parent Enter opens the first child's editor", TodoParentEnterOpensTheFirstChildsEditor);
                failures += TryCheck("todo: child Enter walks to the next sibling", TodoChildEnterWalksToTheNextSibling);
                failures += TryCheck("todo: child Enter at the last one opens the adder", TodoChildEnterAtTheLastOneOpensTheAdder);
                failures += TryCheck("todo: child Enter on an emptied box deletes without walking", TodoChildEnterOnAnEmptiedBoxDeletesWithoutWalking);
                failures += TryCheck("todo: chain-opened editors select all", TodoChainOpenedEditorsSelectAll);

                // Polish round (T1): the caret ink of the inline editors and
                // the bottom add box is the widget colour (it used to be the
                // pale theme text), so the insertion point reads at a glance.
                failures += TryCheck("todo: the item editor caret follows the widget colour", TodoItemEditorCaretFollowsTheWidgetColor);
                failures += TryCheck("todo: the add box caret follows the widget colour", TodoAddBoxCaretFollowsTheWidgetColor);

                // Polish round (T3): Backspace at the text start ladders an
                // entry - an open one completes (the tick semantics), a
                // completed one deletes - and every ladder delete lands in the
                // one-step undo slot; a Backspace away from the start stays a
                // plain text edit.
                failures += TryCheck("todo: backspace at the text start completes an open item", TodoBackspaceAtStartCompletesAnOpenItem);
                failures += TryCheck("todo: backspace again deletes the completed item", TodoBackspaceAgainDeletesTheCompletedItem);
                failures += TryCheck("todo: undo restores the last deleted item", TodoUndoRestoresTheLastDeletedItem);
                failures += TryCheck("todo: the child ladder completes then deletes", TodoChildLadderCompletesThenDeletes);
                failures += TryCheck("todo: backspace mid-text stays plain editing", TodoBackspaceMidTextStaysPlainEditing);
                failures += TryCheck("todo: the ladder commits the box text before completing", TodoLadderCommitsTheBoxTextBeforeCompleting);
                failures += TryCheck("todo: the ladder deletes when the box was emptied", TodoLadderDeletesWhenTheBoxWasEmptied);
                failures += TryCheck("todo: the ladder clears the click anchor before the rebuild", TodoLadderClearsTheClickAnchorBeforeTheRebuild);

                // Fix round 5 pins: the list viewport shrinks to its content so
                // the add pill sits directly under the last row (no 2-row
                // reservation), while an overflowing list keeps six rows.
                // Fix round 12 (R1): an empty day reserves no row at all and
                // renders no message - the collapsed pill sits right under the
                // day header.
                failures += TryCheck("todo: pill follows a single item", TodoPillFollowsASingleItem);
                failures += TryCheck("todo: empty day shows no message", TodoEmptyDayShowsNoMessage);
                failures += TryCheck("todo: overflowing list keeps six rows", TodoOverflowingListKeepsSixRows);

                // Post-smoke design: row actions moved from hover glyphs to a
                // right-click menu (add-child + delete on a parent, delete on a
                // child). Menus are built offline by the view.
                failures += TryCheck("todo: row menus hold the expected actions", TodoRowMenusHoldExpectedActions);
                failures += TryCheck("todo: row menus act on their row", TodoRowMenusActOnTheirRow);
                failures += TryCheck("todo: rows carry no hover buttons", TodoRowsCarryNoHoverButtons);

                // T4: the day-area menu (empty list/calendar space) clears the
                // selected day's completed rows as one undoable batch; it is
                // enabled only while the day actually has completed rows.
                failures += TryCheck("todo: the day-area menu holds the clear entry", TodoDayAreaMenuHoldsTheClearEntry);
                failures += TryCheck("todo: the clear entry enables only with completed rows on the day", TodoClearEntryEnablesOnlyWithCompletedRows);
                failures += TryCheck("todo: clicking clear removes the day's completed rows", TodoClickingClearRemovesTheDaysCompletedRows);

                // B3: the one-step delete undo grew into a multi-step stack -
                // deletes and ticks both land on it, the newest twenty survive,
                // and every press walks back exactly one edit.
                failures += TryCheck("todo: undo walks back through several edits", TodoUndoWalksBackThroughSeveralEdits);
                failures += TryCheck("todo: the undo stack keeps only the newest twenty", TodoUndoStackKeepsOnlyTheNewestTwenty);
                failures += TryCheck("todo: undo restores a ticked parent with its cascade", TodoUndoRestoresATickedParentWithItsCascade);
                failures += TryCheck("todo: undo restores a child tick", TodoUndoRestoresAChildTick);
                failures += TryCheck("todo: the ladder tick lands on the undo stack", TodoLadderTickLandsOnTheUndoStack);

                // Fix round 12 (R1): the empty-day message is gone; the reveal
                // keeps the input in the pill's collapsed slot (fix round 11's
                // stacking), and an empty blur restores the pill there.
                failures += TryCheck("todo: empty-day input sits in the pill slot", TodoEmptyDayInputSitsInThePillSlot);
                failures += TryCheck("todo: collapsing the empty-day input restores the pill", TodoCollapsingEmptyDayInputRestoresThePill);

                // B1 (reported crash): the empty add box's blur used to rebuild
                // the list synchronously inside the click that blurred it, and
                // the detached row's handler then crashed resolving its screen
                // point. The collapse is deferred now and the row handlers carry
                // a dead-visual guard.
                failures += TryCheck("todo: an empty add box blur no longer rebuilds the list under the click", TodoEmptyAddBoxBlurKeepsTheList);
                failures += TryCheck("todo: a click after the empty add box blur still opens the editor", TodoClickAfterEmptyAddBoxBlurOpensTheEditor);
                failures += TryCheck("todo: a stale row click never throws", TodoStaleRowClickNeverThrows);

                // Post-smoke design: the revealed input is a todo-like row -
                // unticked checkbox leading, no placeholder, no background
                // (fix round 11, R1: the control-background tint is gone).
                failures += TryCheck("todo: revealed input looks like a todo row", TodoRevealedInputLooksLikeARow);
                failures += TryCheck("todo: add input shows no placeholder", TodoAddInputHasNoPlaceholder);
                failures += TryCheck("todo: add row has no white background", TodoAddRowHasNoWhiteBackground);
                failures += TryCheck("todo: add checkbox follows the live theme", TodoAddCheckboxFollowsLiveTheme);

                // Fix round 13: the add pill's ＋ follows the widget's own
                // colour (not the fixed brand accent), with a readability
                // fallback for extreme colours under each theme's backdrop.
                failures += TryCheck("todo: add pill follows the widget colour", TodoAddPillFollowsTheWidgetColor);
                failures += TryCheck("todo: add pill stays readable for extreme colours", TodoAddPillStaysReadableForExtremeColors);

                // Fix round 14: the pill's border follows the same readable
                // widget colour as its ＋ (R1); the calendar's selected day
                // becomes the human's hand-drawn circle (R2).
                // Fix round 15: the circle grows (R1), today's marker becomes
                // a filled widget-colour disc instead of the rounded ring
                // (R2), and the folder widget's item selection follows the
                // widget colour through local resource overrides (R3).
                // Fix round 16: the pill's background/border become derived
                // tints of the raw widget colour (R1) and today always shows
                // the disc plus the hand-drawn circle (R2). Task A4.2: today
                // drops the disc for the hand-drawn ring + its corner check.
                failures += TryCheck("todo: selected day uses the hand-drawn circle", TodoSelectedDayUsesTheHandDrawnCircle);
                failures += TryCheck("todo: today wears the hand-drawn ring and its check", TodoTodayMarkerIsTheHandDrawnRingWithACheck);
                failures += TryCheck("folder: item selection follows the widget colour", FolderSelectionFollowsTheWidgetColor);
                failures += TryCheck("folder: the panel size fallback matches the engine", FolderPanelSizeFallbackMatchesTheEngine);
                failures += TryCheck("folder: the panel adapts columns and scale to its size", FolderAdaptsColumnsAndScaleToItsSize);
                failures += TryCheck("folder: the widget menu offers the item-size submenu", FolderMenuOffersTheItemSizeSubmenu);
                failures += TryCheck("folder: clipboard entries follow the lock and paste gates", FolderClipboardMenuEntries);
                failures += TryCheck("folder: the new-file menu entries wear the shared label", FolderNewFileMenuUsesTheFileLabel);
                failures += TryCheck("folder: the browse back button accepts drops", BrowseBackButtonAcceptsDrops);
                failures += TryCheck("folder: raising the item size grows an open panel", FolderItemSizeChangeGrowsAnOpenPanel);
                failures += TryCheck("folder: widening adds spacing at the set icon size", FolderWideningKeepsTheIconSizeAndAddsSpacing);
                failures += TryCheck("folder: the icon row pitch does not follow the panel height", FolderRowPitchIsIndependentOfThePanelHeight);
                failures += TryCheck("resize: folder re-adapts columns during the drag", FolderReadaptsDuringResize);

                // T6 (user spec 2026-10-13): the chrome title follows the
                // selection - one entry's full name (tooltip: its path),
                // several entries' count, and the widget name / browse path
                // when nothing is selected.
                failures += TryCheck("folder: a single selection shows the full name in the title",
                    FolderSingleSelectionShowsTheFullNameInTheTitle);
                failures += TryCheck("folder: multiple selections show the count",
                    FolderMultipleSelectionsShowTheCount);
                failures += TryCheck("folder: an empty selection keeps the widget name",
                    FolderEmptySelectionKeepsTheWidgetName);

                // T7 (user spec 2026-10-13): the arrow keys walk the item
                // grid; the chrome title follows the moved selection and a
                // boundary press stays unhandled for the scroller.
                failures += TryCheck("folder: the arrow keys move the selection across the grid",
                    FolderArrowKeysMoveTheSelectionAcrossTheGrid);

                // Restyle pins: the add input aligns with a list row exactly,
                // and inline editors are borderless with no background band
                // (fix round 11, R1: the caret is the only editing cue).
                failures += TryCheck("todo: add input aligns with a list row", TodoAddInputAlignsWithARow);
                failures += TryCheck("todo: inline editor is borderless and unbanded", TodoInlineEditorIsBorderlessAndUnbanded);

                // Fix round 12 (R2): the panel size must include the chrome
                // shell's 1px border, or the arranged content host loses 2 DIP
                // and WPF clips the view's bottom (the pill's lower border).
                failures += TryCheck("todo: panel fits its content", TodoPanelFitsItsContent);

                // Fix round 17 pins: a day switch must publish the new day's
                // height even on an unrealized tree (R1), and the row text
                // shows the I-beam cursor (R2) and opens the editor with the
                // caret at the clicked character, not select-all (R2).
                failures += TryCheck("todo: size refresh is not stale", TodoSizeRefreshIsNotStale);
                failures += TryCheck("todo: text rows use the I-beam cursor", TodoTextRowsUseTheIBeamCursor);
                failures += TryCheck("todo: click places the caret at the click point", TodoClickPlacesTheCaretAtTheClickPoint);

                // Fix round 18: the add pill carries the list's row pitch - the
                // whitespace from the last row's text to the pill's top border
                // matches the whitespace between two adjacent rows' texts.
                failures += TryCheck("todo: add pill keeps the list pitch", TodoAddPillKeepsTheListPitch);

                // Task 2.5 pins: the zh day cell carries the holiday/lunar
                // second line (region > festival > solar term > lunar day)
                // that fits the 28px cell; en keeps pure numbers; the todo
                // menu offers the five-region 假日地区 submenu backed by the
                // widget's HolidayRegion.
                failures += TryCheck("todo: zh day cell shows the region holiday label", TodoHolidayLabelShowsInZh);
                failures += TryCheck("todo: zh day cell labels fit the cell", TodoHolidayLabelFitsTheCell);
                failures += TryCheck("todo: zh base-layer labels use muted text", TodoBaseLayerLabelUsesMutedText);
                failures += TryCheck("todo: en day cells carry no label", TodoEnglishDayCellsCarryNoLabel);
                failures += TryCheck("todo: holiday region menu has five checked regions", TodoHolidayRegionMenu);

                // Task A4.1 pins: the calendar/list split comes from the pure
                // engine for any panel height, the day labels need the 32-high
                // cell, the panel size no longer follows the content, a resize
                // drag re-adapts the layout, the natural height matches the
                // engine's migration sizes and the add row stays visible at
                // the minimum size.
                failures += TryCheck("todo: the day labels need a 32-high cell", TodoDayLabelsAppearOnlyAtTheLabelThreshold);
                failures += TryCheck("todo: the panel size no longer follows the content", TodoPanelSizeNoLongerFollowsTheContent);
                failures += TryCheck("resize: todo re-adapts the cell height during the drag", ResizeReadaptsTodoCellHeightDuringTheDrag);
                failures += TryCheck("todo: the natural height matches the engine's migration sizes", TodoNaturalHeightMatchesTheEngine);
                failures += TryCheck("todo: the add row stays visible at the minimum size", TodoAddRowStaysVisibleAtTheMinimum);

                // Task A4.2 pins: the calendar/list divider drags the per-widget
                // split (22..36, persisted) and refuses on a locked panel; an
                // explicit split survives an outer resize, a resize never
                // rebuilds the list rows, clicking add scrolls an overflowing
                // list to the bottom, and a drag crossing the 32-high cell
                // threshold flips the day labels in place.
                failures += TryCheck("todo: the divider drags the calendar split", TodoSplitterDragsTheCalendarHeight);
                failures += TryCheck("resize: the explicit split stays while the panel resizes", TodoExplicitSplitSurvivesTheOuterResize);
                failures += TryCheck("resize: todo does not rebuild its list rows", TodoResizeKeepsTheListRows);
                failures += TryCheck("resize: the day labels appear when the drag crosses the 32 cell height", TodoResizeFlipsTheDayLabelsInPlace);
                failures += TryCheck("todo: clicking add scrolls the list to the bottom", TodoAddClickScrollsToTheBottom);

                failures += TryCheck("todo: the layout metrics compute in one pass", TodoMetricsAreComputedOnce);

                // Fix round 19: the click-to-edit interleaving the human smoke
                // reported - while one todo is edited, clicking another opens
                // its editor; the first editor's deferred commit then rebuilds
                // every row. The rebuild must neither select all in the
                // surviving editor nor let its destroyed box schedule a commit
                // that closes the live session; the child adder keeps the
                // select-all default.
                failures += TryCheck("todo: rebuild keeps the click caret", TodoRebuildKeepsTheClickCaret);
                failures += TryCheck("todo: rebuild does not close the surviving editor", TodoRebuildDoesNotCloseTheSurvivingEditor);
                failures += TryCheck("todo: child adder still selects all on load", TodoChildAdderStillSelectsAllOnLoad);

                // Task 3.3 pins: the icon style setting is classic + handdrawn
                // for both widget kinds; the island rebuilds its tiles when the
                // style changes, so the setting needs no restart.
                failures += TryCheck("icon: folder factory dispatches classic and hand-drawn", FolderFactoryDispatchesStyles);
                failures += TryCheck("icon: retired folder styles fall back to classic", FolderFactoryFallsBackToClassic);
                failures += TryCheck("icon: hand-drawn folder uses the widget colour and themed ink", HandDrawnFolderUsesWidgetColourAndInk);
                failures += TryCheck("icon: todo factory dispatches classic and hand-drawn", TodoFactoryDispatchesStyles);
                failures += TryCheck("icon: retired todo styles fall back to classic", TodoFactoryFallsBackToClassic);
                failures += TryCheck("icon: classic todo accents use the widget colour", ClassicTodoUsesWidgetColour);
                failures += TryCheck("icon: hand-drawn todo accents use the widget colour", HandDrawnTodoUsesWidgetColour);
                failures += TryCheck("icon: hand-drawn todo paper follows the theme", HandDrawnTodoPaperFollowsTheme);
                failures += TryCheck("icon: the by-key factory dispatches todo", ByKeyFactoryDispatchesTodo);
                failures += TryCheck("icon: the by-key factory falls back to folder for everything else", ByKeyFactoryFallsBackToFolder);

                // Task T4 pins: the note kind draws a page of its own in both
                // styles, dispatched from CreateByKey("note", ...).
                failures += TryCheck("icon: note factory dispatches classic and hand-drawn", NoteFactoryDispatchesStyles);
                failures += TryCheck("icon: the by-key factory dispatches note", ByKeyFactoryDispatchesNote);
                failures += TryCheck("icon: classic note accents use the widget colour", ClassicNoteUsesWidgetColour);
                failures += TryCheck("icon: hand-drawn note uses the widget colour and themed ink", HandDrawnNoteUsesWidgetColourAndInk);
                failures += TryCheck("icon: hand-drawn note geometry differs from classic", HandDrawnNoteGeometryDiffersFromClassic);
                failures += TryCheck("island: folder tile refreshes with the icon style", IslandFolderTileRefreshesWithStyle);
                failures += TryCheck("island: todo tile refreshes with the icon style", IslandTodoTileRefreshesWithStyle);

                // Task 3.6 pins: the three system entries (desktop, settings,
                // add) carry classic + hand-drawn canvases; the persisted icon
                // style switches them at startup and on RefreshIconStyle, and
                // the desktop slash follows the hide state in both styles.
                failures += TryCheck("island: system tiles follow the icon style", IslandSystemTilesFollowTheIconStyle);
                failures += TryCheck("island: desktop slash follows the hidden state in both styles",
                    IslandDesktopSlashFollowsHiddenState);
                failures += TryCheck("island: hand-drawn system geometry differs from classic",
                    IslandHandDrawnSystemGeometryDiffers);

                // Task 3.5 pins: the pure Quick Access path matcher and the
                // read-only state probe (never Pin/Unpin - invoking them here
                // would change the user's real Quick Access), plus the
                // Settings window's storage row.
                failures += TryCheck("quick access: matching ignores case", QuickAccessMatchIgnoresCase);
                failures += TryCheck("quick access: matching ignores a trailing separator", QuickAccessMatchIgnoresTrailingSeparator);
                failures += TryCheck("quick access: matching rejects different paths", QuickAccessMatchRejectsDifferentPaths);
                failures += TryCheck("quick access: matching rejects empty paths", QuickAccessMatchRejectsEmptyPaths);
                failures += TryCheck("quick access: the state probe is read-only", QuickAccessProbeIsReadOnly);
                failures += TryCheck("settings: the storage row is present", SettingsStorageRowIsPresent);
                failures += TryCheck("settings: storage buttons are comfortable and fit all languages",
                    SettingsStorageButtonsFitAllLanguages);

                // Config redirect pins (stage-0 hygiene): the compare logic
                // proves itself, the forced save proves the sandbox is the
                // write target, the real files must be byte-identical to the
                // startup snapshot (absent stays absent), and a late redirect
                // attempt must be rejected without touching the recorded paths.
                failures += TryCheck("config pin: the compare logic detects only real changes", ConfigPinCompareSelfTest);
                failures += TryCheck("config pin: a forced save lands in the sandbox", ForcedSaveLandsInTheSandbox);
                failures += TryCheck("config pin: the real config files are untouched", RealConfigFilesAreUnchanged);
                failures += TryCheck("config pin: a late override is rejected and leaves the paths alone", LateOverrideIsRejected);

                // Task T5 pins: the global scroll bar is a slim pill - its
                // thumb rests 4 DIP (2 DIP rounded) centred in the 8 DIP
                // click zone and thickens with the hover brush while hovered
                // or dragged, in both orientations.
                failures += TryCheck("scrollbar: the pill rests slim and centred", ScrollBarPillRestsSlim);
                failures += TryCheck("scrollbar: the horizontal pill mirrors the rest state", ScrollBarHorizontalPillMirrorsRestState);
                failures += TryCheck("scrollbar: the pill grows and darkens on hover or drag", ScrollBarPillGrowsWhenHoveredOrDragged);

                // Task T8 pins (user style A, 2026-10-13): every right-click
                // menu wears the rounded surface - a 12 DIP-radius 8 DIP-inset
                // panel under the soft shadow, 8 DIP-radius entry pills inset
                // 4 DIP with a dimmed disabled state, and a 30 DIP-inset
                // separator that lines up with the entry text.
                failures += TryCheck("menu: the surface wears the rounded style", MenuSurfaceWearsTheRoundedStyle);
                failures += TryCheck("menu: the item pill is inset and rounded", MenuItemPillIsInsetAndRounded);
                failures += TryCheck("menu: a disabled entry dims its pill", DisabledMenuItemDimsItsPill);
                failures += TryCheck("menu: the separator fits the rounded rows", SeparatorFitsTheRoundedRows);
                failures += TryCheck("menu: every kind choice resolves to a translated label",
                    EveryKindChoiceResolvesToATranslatedLabel);

                // Task T2 pins: the kind factory is the single kind-to-window
                // registry - folder and todo data build their windows, while an
                // unregistered kind or a null datum has no window.
                failures += TryCheck("factory: folder data builds the folder widget", FactoryFolderDataBuildsFolderWidget);
                failures += TryCheck("factory: todo data builds the todo widget", FactoryTodoDataBuildsTodoWidget);
                failures += TryCheck("factory: an unknown kind has no window", FactoryUnknownKindHasNoWindow);

                // Task T10 pins: the note kind builds through the factory (the
                // island's new-widget menu reaches it via the kind registry and
                // WidgetMenuBuilder's kinds loop), and its panel minimum fits
                // the chrome header + one card + the add row (spec 6.5).
                failures += TryCheck("factory: note data builds the note widget", FactoryNoteDataBuildsNoteWidget);
                failures += TryCheck("note: the panel minimum fits the shell", NotePanelMinimumFitsTheShell);

                // Task T11 pins: the list state - the cards (title / modified
                // time / two-line excerpt / task progress), the modified-time
                // order, the row menu's delete with its multi-step undo (capped
                // at twenty) and the empty list (add row only, no cards). The
                // reading/editing states and the creation flows are T13/T14.
                failures += TryCheck("note: cards show title, time and excerpt", NoteCardsShowTitleTimeAndExcerpt);
                failures += TryCheck("note: a task note shows its progress", NoteTaskShowsProgress);
                failures += TryCheck("note: the list orders by modified time", NoteListOrdersByModifiedTime);
                failures += TryCheck("note: the row menu deletes the note and undo restores it",
                    NoteRowMenuDeletesAndUndoRestores);
                failures += TryCheck("note: undo walks back beyond one step", NoteUndoWalksBackBeyondOneStep);
                failures += TryCheck("note: the stack keeps only the newest twenty",
                    NoteUndoStackKeepsOnlyTheNewestTwenty);
                failures += TryCheck("note: an empty list shows only the add row", NoteEmptyListShowsOnlyTheAddRow);

                Console.WriteLine();
                if (failures == 0)
                {
                    Console.WriteLine("ALL XAML LOAD CHECKS PASSED");
                    return 0;
                }

                Console.WriteLine("FAILURES: " + failures);
                return 1;
            }
            catch (Exception ex)
            {
                try
                {
                    Console.WriteLine("TOP FAIL " + ex.GetType().FullName + ": " + ex.Message);
                }
                catch
                {
                    Console.WriteLine("TOP FAIL " + ex.GetType().FullName + " (message unavailable)");
                }
                return 1;
            }
        }

        /// <summary>
        /// Exercises the shared chrome the way a host window does: theme first,
        /// then title/lock/pin, then a repaint at 80% opacity. WPF cannot toggle
        /// system high contrast, so that pass rebuilds the brushes under whatever
        /// the session reports (the palette itself is covered by HighContrastCheck).
        /// </summary>
        private static WidgetChrome LoadChrome(string theme)
        {
            if (theme == "dark") ThemeManager.Initialize("dark");
            else if (theme == "light") ThemeManager.Initialize("light");
            else ThemeManager.Initialize(ThemeManager.IsDarkTheme ? "dark" : "light");

            var chrome = new WidgetChrome
            {
                Title = "Chrome check",
                IsLocked = true,
                IsPinned = true
            };
            chrome.ApplyTheme();
            chrome.ApplyOpacity(0.8);
            return chrome;
        }

        /// <summary>
        /// Builds a Todo host the way WidgetManager does: its data first, then
        /// the window (chrome + TodoCalendarView) under the requested theme.
        /// The view must render empty data without throwing and must report a
        /// real content height - the window is still chrome-only otherwise.
        /// </summary>
        private static TodoWidget LoadTodo(string theme)
        {
            if (theme == "dark") ThemeManager.Initialize("dark");
            else if (theme == "light") ThemeManager.Initialize("light");
            else ThemeManager.Initialize(ThemeManager.IsDarkTheme ? "dark" : "light");

            var widget = new TodoWidget(new TodoData());
            var view = widget.CalendarView;
            if (view == null) throw new InvalidOperationException("TodoWidget exposes no calendar view");

            view.Refresh();
            if (view.DesiredHeight <= 0)
            {
                throw new InvalidOperationException("empty todo view measured " + view.DesiredHeight);
            }

            // The panel adds the chrome shell's 1px border on every side on top
            // of the content (fix round 12, R2): the window must cover the
            // whole chrome, not just its content.
            var (width, height) = widget.GetPanelSize();
            if (width != 342 || height <= 42)
            {
                throw new InvalidOperationException("todo panel size " + width + "x" + height);
            }
            return widget;
        }

        /// <summary>
        /// The same host with a carried parent (+child), a completed item and a
        /// future item on the selected day: renders every row kind (checkbox,
        /// carried badge, sub-todo row, completed-last) without a live window.
        /// </summary>
        private static TodoWidget LoadTodoWithItems()
        {
            ThemeManager.Initialize("dark");

            DateTime today = DateTime.Today;
            var data = new TodoData { SelectedDate = today };
            var parent = TodoListOps.AddItem(data, "Buy milk", today.AddDays(-1));
            TodoListOps.AddChild(parent, "Whole milk");
            var done = TodoListOps.AddItem(data, "Run checks", today);
            TodoRollover.Toggle(done, true, today);
            TodoListOps.AddItem(data, "Future", today.AddDays(7));

            var widget = new TodoWidget(data);
            widget.CalendarView.Refresh();
            return widget;
        }

        /// <summary>
        /// Task T10: builds a Note host the way WidgetManager does - its data
        /// first, then the window (chrome + NotePanelView) under the requested
        /// theme. The skeleton view must render empty data without throwing.
        /// </summary>
        private static NoteWidget LoadNote(string theme)
        {
            if (theme == "dark") ThemeManager.Initialize("dark");
            else if (theme == "light") ThemeManager.Initialize("light");
            else ThemeManager.Initialize(ThemeManager.IsDarkTheme ? "dark" : "light");

            var widget = new NoteWidget(new NoteData());
            var view = widget.PanelView;
            if (view == null) throw new InvalidOperationException("NoteWidget exposes no panel view");

            view.Refresh();
            return widget;
        }

        /// <summary>
        /// Task T10: the same host carrying one note with text. The skeleton
        /// must keep the handed-over data (the cards themselves arrive with
        /// T11) and load it without a live window.
        /// </summary>
        private static NoteWidget LoadNoteWithNotes()
        {
            ThemeManager.Initialize("dark");

            var data = new NoteData();
            data.Notes.Add(new NoteItem
            {
                Id = Guid.NewGuid().ToString(),
                Text = "# Shopping\n\n- milk\n- eggs",
                CreatedAt = DateTime.Today,
                ModifiedAt = DateTime.Today
            });

            var widget = new NoteWidget(data);
            widget.PanelView.Refresh();

            var carried = widget.Data as NoteData;
            if (carried == null || carried.Notes.Count != 1)
            {
                throw new InvalidOperationException("the note host did not carry its data");
            }
            return widget;
        }

        /// <summary>
        /// A forwarded move without a start event must leave the window alone
        /// (the chrome can pump a pending move while capturing the header), and
        /// a normal start + move must still follow the cursor by the DPI-scaled
        /// delta (20/30 DIP here). The window is never shown.
        /// </summary>
        private static void DragMoveWithoutStartIsIgnored()
        {
            var window = new TodoWidget(new TodoData());
            EnsureRealDpi(window);
            window.Left = 0;
            window.Top = 0;
            var moved = PrivateBaseMethod("OnHeaderDragMoved");

            // No start: even a far-away cursor must not move the window.
            moved.Invoke(window, new object[] { new Point(5000, 4000) });
            if (window.Left != 0 || window.Top != 0)
            {
                throw new InvalidOperationException(
                    "a move without a start changed Left/Top to " + window.Left + "," + window.Top);
            }
        }

        /// <summary>
        /// After a start event the same private move path must work: the window
        /// moves by the screen-pixel delta divided by its DPI scale.
        /// </summary>
        private static void DragStartThenMoveMoves()
        {
            var window = new TodoWidget(new TodoData());
            EnsureRealDpi(window);
            window.Left = 0;
            window.Top = 0;
            var started = PrivateBaseMethod("OnHeaderDragStarted");
            var moved = PrivateBaseMethod("OnHeaderDragMoved");

            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            started.Invoke(window, new object[] { new Point(100, 100) });
            moved.Invoke(window, new object[] { new Point(100 + 20 * scale, 100 + 30 * scale) });

            if (Math.Abs(window.Left - 20) > 0.5 || Math.Abs(window.Top - 30) > 0.5)
            {
                throw new InvalidOperationException(
                    "start+move moved to " + window.Left + "," + window.Top +
                    " (wanted 20,30 at scale " + scale + ")");
            }
        }

        /// <summary>
        /// A window that was never shown reports neither a real DPI nor a real
        /// position; creating its handle (without showing it) gives the drag
        /// maths the monitor scale a visible panel would have.
        /// </summary>
        private static void EnsureRealDpi(Window window)
        {
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        }

        /// <summary>
        /// Pins the chrome's handler order: when HeaderDragStarted is raised,
        /// the internal drag flag must still be false - it may only be set
        /// after Mouse.Capture returned. Otherwise a move pumped during the
        /// capture is forwarded before the host has its anchor.
        /// </summary>
        private static void ChromeRaisesStartBeforeDragActive()
        {
            var chrome = new WidgetChrome();
            bool? dragActiveAtStart = null;
            chrome.HeaderDragStarted += _ =>
                dragActiveAtStart = PrivateField<bool>(chrome, "_isDragActive");

            var down = typeof(WidgetChrome).GetMethod("Header_MouseLeftButtonDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (down == null) throw new InvalidOperationException("Header_MouseLeftButtonDown not found");

            try
            {
                var downArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                };
                down.Invoke(chrome, new object[] { chrome, downArgs });
            }
            finally
            {
                // The handler may have taken the (test-local) capture.
                Mouse.Capture(null);
                Mouse.OverrideCursor = null;
            }

            if (dragActiveAtStart != false)
            {
                throw new InvalidOperationException(
                    "HeaderDragStarted saw _isDragActive=" +
                    (dragActiveAtStart.HasValue ? dragActiveAtStart.Value.ToString() : "never raised"));
            }
        }

        /// <summary>
        /// Task F1: the file-clipboard payload carries the FileDrop list in
        /// order (blank entries dropped) plus the 4-byte Preferred DropEffect
        /// stream (Copy=1 / Move=2); the payload reads back through PathsFrom
        /// (string[] and StringCollection forms) and IsMoveEffect (short or
        /// missing streams are copies), and Resolve follows the Ctrl+C/X/V
        /// truth table. Everything uses plain DataObjects - no real clipboard.
        /// </summary>
        private static void FileClipboardPayloadAndResolve()
        {
            var movePayload = FileClipboard.CreatePayload(new[] { @"C:\a.txt", @"C:\b.txt" }, true);
            if (!movePayload.GetDataPresent(DataFormats.FileDrop))
            {
                throw new InvalidOperationException("the move payload carries no FileDrop list");
            }
            var moveFiles = movePayload.GetFileDropList();
            if (moveFiles.Count != 2 || moveFiles[0] != @"C:\a.txt" || moveFiles[1] != @"C:\b.txt")
            {
                throw new InvalidOperationException(
                    "the FileDrop list lost its order: " + moveFiles.Count + " entries");
            }
            if (!FileClipboard.IsMoveEffect(movePayload))
            {
                throw new InvalidOperationException("the move payload did not read back as a move effect");
            }

            var moveBack = FileClipboard.PathsFrom(movePayload);
            if (moveBack.Count != 2 || moveBack[0] != @"C:\a.txt" || moveBack[1] != @"C:\b.txt")
            {
                throw new InvalidOperationException(
                    "PathsFrom lost the move payload: " + moveBack.Count + " entries");
            }

            var copyPayload = FileClipboard.CreatePayload(new[] { @"C:\a.txt", @"C:\b.txt" }, false);
            if (FileClipboard.IsMoveEffect(copyPayload))
            {
                throw new InvalidOperationException("the copy payload read back as a move effect");
            }
            if (FileClipboard.PathsFrom(copyPayload).Count != 2)
            {
                throw new InvalidOperationException("PathsFrom lost the copy payload");
            }

            var filtered = FileClipboard.CreatePayload(new[] { @"C:\a.txt", "", "   ", null }, false);
            var filteredFiles = filtered.GetFileDropList();
            if (filteredFiles.Count != 1 || filteredFiles[0] != @"C:\a.txt")
            {
                throw new InvalidOperationException(
                    "blank paths leaked into the payload: " + filteredFiles.Count + " entries");
            }

            var legacy = new DataObject();
            legacy.SetData(DataFormats.FileDrop, new StringCollection { @"C:\legacy.txt", "" });
            var legacyBack = FileClipboard.PathsFrom(legacy);
            if (legacyBack.Count != 1 || legacyBack[0] != @"C:\legacy.txt")
            {
                throw new InvalidOperationException(
                    "PathsFrom did not read the StringCollection form: " + legacyBack.Count + " entries");
            }

            if (FileClipboard.PathsFrom(null).Count != 0 ||
                FileClipboard.PathsFrom(new DataObject()).Count != 0)
            {
                throw new InvalidOperationException("a payload without FileDrop was not read as empty");
            }
            if (FileClipboard.IsMoveEffect(null) || FileClipboard.IsMoveEffect(new DataObject()))
            {
                throw new InvalidOperationException("a missing drop-effect stream was not read as a copy");
            }

            var shortEffect = new DataObject();
            shortEffect.SetData(FileClipboard.PreferredDropEffectFormat, new IO.MemoryStream(new byte[] { 2, 0 }));
            if (FileClipboard.IsMoveEffect(shortEffect))
            {
                throw new InvalidOperationException("a short drop-effect stream was not rejected");
            }

            var copyEffect = new DataObject();
            copyEffect.SetData(FileClipboard.PreferredDropEffectFormat,
                new IO.MemoryStream(BitConverter.GetBytes(FileClipboard.CopyEffect)));
            if (FileClipboard.IsMoveEffect(copyEffect))
            {
                throw new InvalidOperationException("effect 1 was read as a move");
            }

            if (FileClipboard.Resolve(false, false, false, true, false, Key.C) != ClipboardAction.None ||
                FileClipboard.Resolve(false, false, false, false, false, Key.X) != ClipboardAction.None ||
                FileClipboard.Resolve(false, false, false, true, false, Key.V) != ClipboardAction.None)
            {
                throw new InvalidOperationException("a missing Ctrl was not rejected");
            }

            if (FileClipboard.Resolve(true, true, false, true, false, Key.C) != ClipboardAction.None ||
                FileClipboard.Resolve(true, false, true, true, false, Key.X) != ClipboardAction.None ||
                FileClipboard.Resolve(true, true, true, true, false, Key.V) != ClipboardAction.None)
            {
                throw new InvalidOperationException("Shift/Alt was not rejected");
            }

            if (FileClipboard.Resolve(true, false, false, true, true, Key.C) != ClipboardAction.None ||
                FileClipboard.Resolve(true, false, false, true, true, Key.X) != ClipboardAction.None ||
                FileClipboard.Resolve(true, false, false, true, true, Key.V) != ClipboardAction.None)
            {
                throw new InvalidOperationException("a locked panel was not rejected");
            }

            if (FileClipboard.Resolve(true, false, false, false, false, Key.C) != ClipboardAction.Copy ||
                FileClipboard.Resolve(true, false, false, true, false, Key.C) != ClipboardAction.Copy)
            {
                throw new InvalidOperationException("Ctrl+C was not Copy in both views");
            }

            if (FileClipboard.Resolve(true, false, false, true, false, Key.X) != ClipboardAction.Cut)
            {
                throw new InvalidOperationException("Ctrl+X in the browse view was not Cut");
            }
            if (FileClipboard.Resolve(true, false, false, false, false, Key.X) != ClipboardAction.None)
            {
                throw new InvalidOperationException("Ctrl+X outside the browse view was not rejected");
            }

            if (FileClipboard.Resolve(true, false, false, true, false, Key.V) != ClipboardAction.Paste ||
                FileClipboard.Resolve(true, false, false, false, false, Key.V) != ClipboardAction.Paste)
            {
                throw new InvalidOperationException("Ctrl+V was not Paste in both views");
            }

            if (FileClipboard.Resolve(true, false, false, true, false, Key.A) != ClipboardAction.None)
            {
                throw new InvalidOperationException("Ctrl+A was resolved as a clipboard action");
            }
        }

        /// <summary>
        /// Task F4: the shared ComboBox template carries the editable text
        /// host. An editable combo shows PART_EditableTextBox (and hides the
        /// selection presenter), while a plain combo keeps the text host
        /// collapsed and keeps rendering the selection presenter - the
        /// non-editable path renders exactly as before.
        /// </summary>
        private static void EditableComboBoxSupportsTextEntry()
        {
            var editable = new ComboBox { IsEditable = true };
            var editableHost = new Grid();
            editableHost.Children.Add(editable);
            editableHost.Measure(new Size(200, 40));
            editableHost.Arrange(new Rect(0, 0, 200, 40));
            editable.UpdateLayout();
            editable.ApplyTemplate();

            var box = editable.Template.FindName("PART_EditableTextBox", editable) as TextBox;
            if (box == null)
            {
                throw new InvalidOperationException("the editable combo has no PART_EditableTextBox");
            }
            if (box.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException("the editable text host is " + box.Visibility);
            }

            var presenter = editable.Template.FindName("SelectionPresenter", editable) as ContentPresenter;
            if (presenter == null)
            {
                throw new InvalidOperationException("the editable combo has no SelectionPresenter");
            }
            if (presenter.Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException("the editable selection presenter is " + presenter.Visibility);
            }

            var plain = new ComboBox();
            var plainHost = new Grid();
            plainHost.Children.Add(plain);
            plainHost.Measure(new Size(200, 40));
            plainHost.Arrange(new Rect(0, 0, 200, 40));
            plain.UpdateLayout();
            plain.ApplyTemplate();

            var plainBox = plain.Template.FindName("PART_EditableTextBox", plain) as TextBox;
            if (plainBox == null)
            {
                throw new InvalidOperationException("the plain combo has no PART_EditableTextBox");
            }
            if (plainBox.Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException("the plain text host is " + plainBox.Visibility);
            }

            var plainPresenter = plain.Template.FindName("SelectionPresenter", plain) as ContentPresenter;
            if (plainPresenter == null)
            {
                throw new InvalidOperationException("the plain combo has no SelectionPresenter");
            }
            if (plainPresenter.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException("the plain selection presenter is " + plainPresenter.Visibility);
            }
        }

        /// <summary>
        /// Smoke fix A: the New-File dialog's editable extension ComboBox
        /// must render ".txt" in full. PART_EditableTextBox inherited the
        /// global TextBox style, whose 10,6 padding (inside the fixed 32-high
        /// combo) shrank the text viewport below one glyph line - the human
        /// smoke saw the text clipped to a thin band. The pin renders the
        /// same combo offscreen and demands the glyph band match the text's
        /// real ink height and stay inside the text viewport.
        /// </summary>
        private static void EditableComboBoxTextIsNotClipped()
        {
            ThemeManager.Initialize("dark");

            var combo = new ComboBox
            {
                IsEditable = true,
                Text = ".txt",
                Width = 116,
                ItemsSource = new[] { ".txt", ".md", ".json" }
            };
            // An unparented control does not resolve the app's implicit styles;
            // the dialog's combo does. Apply the same style explicitly so the
            // template (and the editable host's inherited TextBox style) match
            // the production resource chain.
            combo.Style = Application.Current.TryFindResource(typeof(ComboBox)) as Style;
            if (combo.Style == null)
            {
                throw new InvalidOperationException("the app's implicit ComboBox style was not found");
            }
            combo.ApplyTemplate();
            combo.Measure(new Size(116, double.PositiveInfinity));
            combo.Arrange(new Rect(0, 0, 116, combo.DesiredSize.Height));
            combo.UpdateLayout();

            var textBox = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
            if (textBox == null) throw new InvalidOperationException("PART_EditableTextBox not found");
            textBox.ApplyTemplate();
            var contentHost = textBox.Template.FindName("PART_ContentHost", textBox) as FrameworkElement;
            if (contentHost == null) throw new InvalidOperationException("PART_ContentHost not found");

            var typeface = new Typeface(textBox.FontFamily, textBox.FontStyle, textBox.FontWeight, textBox.FontStretch);
            var formatted = new FormattedText(".txt", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, textBox.FontSize, Brushes.Black, 1.0);
            double expectedInk = formatted.BuildGeometry(new Point(0, 0)).Bounds.Height;

            Point boxOrigin = textBox.TransformToAncestor(combo).Transform(new Point(0, 0));
            Point hostOrigin = contentHost.TransformToAncestor(combo).Transform(new Point(0, 0));

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(combo.ActualWidth),
                (int)Math.Ceiling(combo.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(combo);
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);

            int x0 = Math.Max(0, (int)Math.Floor(boxOrigin.X));
            int x1 = Math.Min(width, (int)Math.Ceiling(boxOrigin.X + textBox.ActualWidth));
            int firstInkRow = int.MaxValue;
            int lastInkRow = int.MinValue;
            for (int y = 0; y < height; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * width + x) * 4;
                    if (pixels[i] > 128 || pixels[i + 1] > 128 || pixels[i + 2] > 128)
                    {
                        if (y < firstInkRow) firstInkRow = y;
                        if (y > lastInkRow) lastInkRow = y;
                        break;
                    }
                }
            }
            if (lastInkRow < firstInkRow)
            {
                throw new InvalidOperationException("no glyph pixels rendered for '.txt'");
            }

            int band = lastInkRow - firstInkRow + 1;
            double contentHeight = textBox.ActualHeight - textBox.BorderThickness.Top - textBox.BorderThickness.Bottom
                                   - textBox.Padding.Top - textBox.Padding.Bottom;
            Console.WriteLine("    probe: combo " + combo.ActualWidth + "x" + combo.ActualHeight +
                ", box h=" + textBox.ActualHeight + " pad=" + textBox.Padding + " border=" + textBox.BorderThickness +
                ", box.y=" + boxOrigin.Y.ToString("0.##") + ", host h=" + contentHost.ActualHeight +
                " host.y=" + hostOrigin.Y.ToString("0.##") + ", content=" + contentHeight.ToString("0.##") +
                ", ink rows " + firstInkRow + ".." + lastInkRow + " (band " + band + "), expected ink " +
                expectedInk.ToString("0.##") + ", font " + textBox.FontSize);

            if (band < expectedInk - 2.5)
            {
                throw new InvalidOperationException(
                    "the extension glyph band is " + band + "px, wanted ~" + expectedInk.ToString("0.##") +
                    " (content viewport " + contentHeight.ToString("0.##") + ")");
            }

            double hostTop = hostOrigin.Y;
            double hostBottom = hostOrigin.Y + contentHost.ActualHeight;
            if (firstInkRow + 0.5 < hostTop || lastInkRow + 0.5 > hostBottom)
            {
                throw new InvalidOperationException(
                    "glyph rows " + firstInkRow + ".." + lastInkRow + " leave the text viewport " +
                    hostTop.ToString("0.##") + ".." + hostBottom.ToString("0.##"));
            }
        }

        #region Task A2.2 resize pins

        /// <summary>
        /// Task A2.2: a widget whose data carries both persisted sizes opens at
        /// exactly that size, even though the legacy content maths would
        /// measure differently (500x600 here).
        /// </summary>
        private static void PanelPersistedSizeWins()
        {
            ThemeManager.Initialize("dark");
            var widget = new TodoWidget(new TodoData { PanelWidth = 500, PanelHeight = 600 });

            var (width, height) = widget.GetPanelSize();
            if (width != 500 || height != 600)
            {
                throw new InvalidOperationException(
                    "the persisted 500x600 panel measured " + width + "x" + height);
            }
        }

        /// <summary>
        /// Task A2.2: persisted sizes are clamped up to the kind minimum on
        /// read - todo 342x337, folder 186x154 (the dynamic MinFolderSize at
        /// the sandbox's 1.1 icon scale) - so a hand-edited or legacy tiny
        /// value cannot open an unusable panel.
        /// </summary>
        private static void PanelPersistedBelowMinimumIsClamped()
        {
            ThemeManager.Initialize("dark");

            var todo = new TodoWidget(new TodoData { PanelWidth = 50, PanelHeight = 100 });
            var (todoWidth, todoHeight) = todo.GetPanelSize();
            if (todoWidth != 342 || todoHeight != 337)
            {
                throw new InvalidOperationException(
                    "the tiny todo panel measured " + todoWidth + "x" + todoHeight + ", wanted 342x337");
            }

            var folder = new FolderWidget(new FolderData { PanelWidth = 50, PanelHeight = 100 });
            var (folderWidth, folderHeight) = folder.GetPanelSize();
            if (folderWidth != 186 || folderHeight != 154)
            {
                throw new InvalidOperationException(
                    "the tiny folder panel measured " + folderWidth + "x" + folderHeight + ", wanted 186x154");
            }
        }

        /// <summary>
        /// Task A2.2: without persisted sizes GetPanelSize() keeps the legacy
        /// path (content 340 + the 2 DIP shell border for the todo width).
        /// </summary>
        private static void PanelMissingSizesFallBackToTheLegacyContentSize()
        {
            ThemeManager.Initialize("dark");
            var (width, _) = new TodoWidget(new TodoData()).GetPanelSize();

            if (width != 342)
            {
                throw new InvalidOperationException(
                    "the legacy todo panel width is " + width + ", wanted 342 (340 content + 2 border)");
            }
        }

        /// <summary>
        /// Task A2.2: a forwarded resize move without a start event must leave
        /// the window alone - the chrome can pump a pending move while
        /// capturing the band, exactly like the drag anchor rule.
        /// </summary>
        private static void ResizeMoveWithoutStartIsIgnored()
        {
            var window = NewTodoForResize(342, 335, 0, 0);

            // No start: even a far-away cursor must not resize the window.
            PrivateBaseMethod("OnResizeMoved").Invoke(window, new object[] { new Point(5000, 4000) });

            if (window.Width != 342 || window.Height != 335 || window.Left != 0 || window.Top != 0)
            {
                throw new InvalidOperationException(
                    "a move without a start changed the panel to " + window.Width + "x" + window.Height +
                    " at " + window.Left + "," + window.Top);
            }
        }

        /// <summary>
        /// Task A2.2: after a bottom-right start the move grows width and
        /// height by the screen-pixel delta divided by the DPI scale
        /// (+40/+20 DIP here).
        /// </summary>
        private static void ResizeBottomRightStartThenMoveScalesByDpi()
        {
            var window = NewTodoForResize(342, 335, 0, 0);
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;

            InvokeResizeStarted(window, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(window,
                new object[] { new Point(100 + 40 * scale, 100 + 20 * scale) });

            if (Math.Abs(window.Width - 382) > 0.5 || Math.Abs(window.Height - 355) > 0.5)
            {
                throw new InvalidOperationException(
                    "the bottom-right drag made the panel " + window.Width + "x" + window.Height +
                    ", wanted 382x355 at scale " + scale);
            }
        }

        /// <summary>
        /// Task A2.2: the left edge moves the left side only - the window's
        /// right edge (start Left + start Width) stays fixed.
        /// </summary>
        private static void ResizeLeftEdgeKeepsTheRightEdgeFixed()
        {
            var window = NewTodoForResize(400, 335, 200, 0);
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;

            InvokeResizeStarted(window, WidgetResizeEdge.Left, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(window,
                new object[] { new Point(100 + 30 * scale, 100) });

            if (Math.Abs(window.Width - 370) > 0.5 || Math.Abs(window.Left - 230) > 0.5)
            {
                throw new InvalidOperationException(
                    "the left-edge drag made the panel " + window.Width + " wide at " + window.Left +
                    ", wanted 370 wide at 230 (right edge fixed at 600)");
            }
        }

        /// <summary>
        /// Task A2.2: the top edge moves the top side only - the window's
        /// bottom edge (start Top + start Height) stays fixed.
        /// </summary>
        private static void ResizeTopEdgeKeepsTheBottomEdgeFixed()
        {
            var window = NewTodoForResize(342, 400, 0, 300);
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;

            InvokeResizeStarted(window, WidgetResizeEdge.Top, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(window,
                new object[] { new Point(100, 100 + 30 * scale) });

            if (Math.Abs(window.Height - 370) > 0.5 || Math.Abs(window.Top - 330) > 0.5)
            {
                throw new InvalidOperationException(
                    "the top-edge drag made the panel " + window.Height + " tall at " + window.Top +
                    ", wanted 370 tall at 330 (bottom edge fixed at 700)");
            }
        }

        /// <summary>
        /// Task A2.2: shrinking stops at the kind minimum - todo 342x337,
        /// folder 186x154 (the dynamic MinFolderSize at the sandbox's 1.1
        /// icon scale) - and a corner drag never moves the panel's left/top.
        /// </summary>
        private static void ResizeShrinkingClampsAtTheKindMinimum()
        {
            var todo = NewTodoForResize(400, 400, 50, 60);
            double todoScale = VisualTreeHelper.GetDpi(todo).DpiScaleX;
            InvokeResizeStarted(todo, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(todo,
                new object[] { new Point(100 - 500 * todoScale, 100 - 500 * todoScale) });

            if (Math.Abs(todo.Width - 342) > 0.5 || Math.Abs(todo.Height - 337) > 0.5 ||
                todo.Left != 50 || todo.Top != 60)
            {
                throw new InvalidOperationException(
                    "the shrinking todo drag made the panel " + todo.Width + "x" + todo.Height +
                    " at " + todo.Left + "," + todo.Top + ", wanted 342x337 at 50,60");
            }

            var folder = new FolderWidget(new FolderData());
            EnsureRealDpi(folder);
            folder.Width = 400;
            folder.Height = 400;
            folder.Left = 50;
            folder.Top = 60;
            double folderScale = VisualTreeHelper.GetDpi(folder).DpiScaleX;
            InvokeResizeStarted(folder, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(folder,
                new object[] { new Point(100 - 500 * folderScale, 100 - 500 * folderScale) });

            if (Math.Abs(folder.Width - 186) > 0.5 || Math.Abs(folder.Height - 154) > 0.5 ||
                folder.Left != 50 || folder.Top != 60)
            {
                throw new InvalidOperationException(
                    "the shrinking folder drag made the panel " + folder.Width + "x" + folder.Height +
                    " at " + folder.Left + "," + folder.Top + ", wanted 186x154 at 50,60");
            }
        }

        /// <summary>
        /// Task A2.2: growing stops at the monitor work area (DIP), so a
        /// corner drag can never push the panel off the screen.
        /// </summary>
        private static void ResizeGrowingClampsAtTheWorkArea()
        {
            var window = NewTodoForResize(342, 335, 0, 0);
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;

            InvokeResizeStarted(window, WidgetResizeEdge.BottomRight, new Point(0, 0));
            PrivateBaseMethod("OnResizeMoved").Invoke(window,
                new object[] { new Point(10000 * scale, 10000 * scale) });

            Rect work = MonitorInterop.WorkAreaDipForWindow(window);
            if (Math.Abs(window.Width - work.Width) > 0.5 || Math.Abs(window.Height - work.Height) > 0.5)
            {
                throw new InvalidOperationException(
                    "the growing drag made the panel " + window.Width + "x" + window.Height +
                    ", wanted the work area " + work.Width + "x" + work.Height);
            }
        }

        /// <summary>
        /// Task A2.2: a drag in progress must not write the panel geometry to
        /// the config - only the release persists.
        /// </summary>
        private static void ResizeDuringDragWritesNothing()
        {
            var data = new TodoData();
            var window = new TodoWidget(data);
            EnsureRealDpi(window);
            window.Width = 342;
            window.Height = 335;
            window.Left = 0;
            window.Top = 0;
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;

            InvokeResizeStarted(window, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(window,
                new object[] { new Point(100 + 40 * scale, 100 + 20 * scale) });

            if (data.PanelWidth != null || data.PanelHeight != null ||
                data.PanelX != null || data.PanelY != null)
            {
                throw new InvalidOperationException(
                    "a drag wrote to the data: " + data.PanelWidth + "x" + data.PanelHeight +
                    " at " + data.PanelX + "," + data.PanelY);
            }
        }

        /// <summary>
        /// Task A2.2: releasing after a real drag persists the size and the
        /// fitted position (inside the virtual screen) for the next launch.
        /// </summary>
        private static void ResizeEndPersistsSizeAndPosition()
        {
            var data = new TodoData();
            var window = new TodoWidget(data);
            EnsureRealDpi(window);
            window.Width = 342;
            window.Height = 335;
            window.Left = 0;
            window.Top = 0;
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            var cursor = new Point(100 + 40 * scale, 100 + 20 * scale);

            InvokeResizeStarted(window, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(window, new object[] { cursor });
            PrivateBaseMethod("OnResizeEnded").Invoke(window, new object[] { cursor });

            if (data.PanelWidth != window.Width || data.PanelHeight != window.Height ||
                data.PanelX != window.Left || data.PanelY != window.Top)
            {
                throw new InvalidOperationException(
                    "the finished resize persisted " + data.PanelWidth + "x" + data.PanelHeight +
                    " at " + data.PanelX + "," + data.PanelY + ", but the window is " +
                    window.Width + "x" + window.Height + " at " + window.Left + "," + window.Top);
            }

            double minX = SystemParameters.VirtualScreenLeft;
            double minY = SystemParameters.VirtualScreenTop;
            double maxX = minX + SystemParameters.VirtualScreenWidth;
            double maxY = minY + SystemParameters.VirtualScreenHeight;
            if (window.Left < minX - 1 || window.Top < minY - 1 ||
                window.Left + window.Width > maxX + 1 || window.Top + window.Height > maxY + 1)
            {
                throw new InvalidOperationException(
                    "the resized panel at " + window.Left + "," + window.Top + " " +
                    window.Width + "x" + window.Height + " is outside the virtual screen");
            }
        }

        /// <summary>
        /// Task A2.2: pressing a band and releasing without moving is a click,
        /// not a resize - nothing may be written.
        /// </summary>
        private static void ResizeBandClickWithoutMovementDoesNotPersist()
        {
            var data = new TodoData();
            var window = new TodoWidget(data);
            EnsureRealDpi(window);
            window.Width = 342;
            window.Height = 335;
            window.Left = 0;
            window.Top = 0;

            InvokeResizeStarted(window, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeEnded").Invoke(window, new object[] { new Point(100, 100) });

            if (data.PanelWidth != null || data.PanelHeight != null)
            {
                throw new InvalidOperationException(
                    "a click without movement persisted " + data.PanelWidth + "x" + data.PanelHeight);
            }
        }

        /// <summary>
        /// Task A2.2: pins the chrome's handler order for the resize bands -
        /// when ResizeStarted is raised the internal resize flag must still be
        /// false (it may only be set after Mouse.Capture returned), and the
        /// grip must parse its Tag to the BottomRight edge.
        /// </summary>
        private static void ChromeResizeStartPrecedesTheResizeFlag()
        {
            var chrome = new WidgetChrome();
            bool? resizeActiveAtStart = null;
            WidgetResizeEdge? edgeAtStart = null;
            chrome.ResizeStarted += (edge, cursor) =>
            {
                resizeActiveAtStart = PrivateField<bool>(chrome, "_isResizeActive");
                edgeAtStart = edge;
            };

            var down = typeof(WidgetChrome).GetMethod("ResizeBand_MouseLeftButtonDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (down == null) throw new InvalidOperationException("ResizeBand_MouseLeftButtonDown not found");

            try
            {
                var downArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                };
                down.Invoke(chrome, new object[] { chrome.ResizeGrip, downArgs });
            }
            finally
            {
                // The handler may have taken the (test-local) capture.
                Mouse.Capture(null);
            }

            if (resizeActiveAtStart != false)
            {
                throw new InvalidOperationException(
                    "ResizeStarted saw _isResizeActive=" +
                    (resizeActiveAtStart.HasValue ? resizeActiveAtStart.Value.ToString() : "never raised"));
            }
            if (edgeAtStart != WidgetResizeEdge.BottomRight)
            {
                throw new InvalidOperationException(
                    "the grip raised edge " + edgeAtStart + ", wanted BottomRight");
            }
        }

        /// <summary>
        /// Task A2.2: a locked panel exposes no resize affordances at all -
        /// the grip collapses and the whole layer stops hit-testing.
        /// </summary>
        private static void ChromeLockedPanelsExposeNoResizeHandles()
        {
            var chrome = new WidgetChrome { IsLocked = true };
            if (chrome.ResizeGrip.Visibility != Visibility.Collapsed || chrome.ResizeLayer.IsHitTestVisible)
            {
                throw new InvalidOperationException(
                    "a locked panel still exposes its resize handles (grip " + chrome.ResizeGrip.Visibility +
                    ", layer hit-testable " + chrome.ResizeLayer.IsHitTestVisible + ")");
            }

            chrome.IsLocked = false;
            if (chrome.ResizeGrip.Visibility != Visibility.Visible || !chrome.ResizeLayer.IsHitTestVisible)
            {
                throw new InvalidOperationException(
                    "an unlocked panel hides its resize handles (grip " + chrome.ResizeGrip.Visibility +
                    ", layer hit-testable " + chrome.ResizeLayer.IsHitTestVisible + ")");
            }
        }

        /// <summary>
        /// Task A2.2: each band carries the cursor for its edge - SizeWE left/
        /// right, SizeNS top/bottom, SizeNWSE on the corner grip.
        /// </summary>
        private static void ChromeResizeCursorsMapToTheEdges()
        {
            var chrome = new WidgetChrome();
            if (chrome.ResizeLeft.Cursor != Cursors.SizeWE || chrome.ResizeRight.Cursor != Cursors.SizeWE)
            {
                throw new InvalidOperationException("the vertical edges do not show the SizeWE cursor");
            }
            if (chrome.ResizeTop.Cursor != Cursors.SizeNS || chrome.ResizeBottom.Cursor != Cursors.SizeNS)
            {
                throw new InvalidOperationException("the horizontal edges do not show the SizeNS cursor");
            }
            if (chrome.ResizeGrip.Cursor != Cursors.SizeNWSE)
            {
                throw new InvalidOperationException("the grip does not show the SizeNWSE cursor");
            }
        }

        /// <summary>
        /// Task A2.2: even a synthetic press on a band of a locked chrome is
        /// ignored - the lock is enforced in the handler, not only by the
        /// hit-test visual.
        /// </summary>
        private static void ResizeLockedBandPressIsIgnored()
        {
            var chrome = new WidgetChrome { IsLocked = true };
            bool started = false;
            chrome.ResizeStarted += (edge, cursor) => started = true;

            var down = typeof(WidgetChrome).GetMethod("ResizeBand_MouseLeftButtonDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (down == null) throw new InvalidOperationException("ResizeBand_MouseLeftButtonDown not found");

            try
            {
                var downArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                };
                down.Invoke(chrome, new object[] { chrome.ResizeGrip, downArgs });
            }
            finally
            {
                Mouse.Capture(null);
            }

            if (started)
            {
                throw new InvalidOperationException("a locked panel raised ResizeStarted");
            }
        }

        /// <summary>
        /// Task A2.2: a resize probe window with a real (handle-backed) DPI
        /// and the given off-screen geometry; never shown.
        /// </summary>
        private static TodoWidget NewTodoForResize(double width, double height, double left, double top)
        {
            var window = new TodoWidget(new TodoData());
            EnsureRealDpi(window);
            window.Width = width;
            window.Height = height;
            window.Left = left;
            window.Top = top;
            return window;
        }

        private static void InvokeResizeStarted(Window window, WidgetResizeEdge edge, Point cursor)
        {
            PrivateBaseMethod("OnResizeStarted").Invoke(window, new object[] { edge, cursor });
        }

        #endregion

        /// <summary>
        /// The shell backdrop must be tinted from the widget's own colour with
        /// FolderWidget's panel maths (dark: DarkenColor(base, 0.3) at 210
        /// alpha for opacity 1), not from the neutral surface.
        /// </summary>
        private static void ChromePanelColorTintsBackdrop()
        {
            ThemeManager.Initialize("dark");
            var chrome = new WidgetChrome { PanelColor = "#3B82F6" };

            Color stop = FirstGradientStop(chrome);
            Color wanted = Utils.DarkenColor(Utils.HexToColor("#3B82F6"), 0.3);
            if (stop.R != wanted.R || stop.G != wanted.G || stop.B != wanted.B || stop.A != 210)
            {
                throw new InvalidOperationException(
                    "panel stop " + Describe(stop) + ", wanted " + Describe(Color.FromArgb(210, wanted.R, wanted.G, wanted.B)));
            }

            Color neutral = Utils.DarkenColor(Utils.HexToColor(UiTokens.Dark.Surface), 0.3);
            if (stop.R == neutral.R && stop.G == neutral.G && stop.B == neutral.B)
            {
                throw new InvalidOperationException("panel stop is still the neutral surface colour");
            }
        }

        /// <summary>Clearing the colour returns the backdrop to the neutral surface.</summary>
        private static void ChromePanelColorClearedReturnsNeutral()
        {
            ThemeManager.Initialize("dark");
            var chrome = new WidgetChrome { PanelColor = "#3B82F6" };
            chrome.PanelColor = null;

            Color stop = FirstGradientStop(chrome);
            Color neutral = Utils.DarkenColor(Utils.HexToColor(UiTokens.Dark.Surface), 0.3);
            if (stop.R != neutral.R || stop.G != neutral.G || stop.B != neutral.B)
            {
                throw new InvalidOperationException(
                    "cleared panel stop " + Describe(stop) + " != neutral " + Describe(neutral));
            }
        }

        /// <summary>
        /// A panel placed at the bottom edge and grown downward must come out
        /// fully inside its work area after the base's fit runs - the add row
        /// and editors live at the panel's bottom, so they must stay reachable.
        /// </summary>
        private static void FitPanelKeepsPanelInWorkArea()
        {
            var window = new TodoWidget(new TodoData());
            EnsureRealDpi(window);

            Rect work = MonitorInterop.WorkAreaDipForWindow(window);
            window.Left = work.Right - 200;
            window.Top = work.Bottom - 20;
            window.Width = 340;
            window.Height = 300;

            var fit = PrivateBaseMethod("FitPanelToWorkArea");
            fit.Invoke(window, null);

            Rect after = MonitorInterop.WorkAreaDipForWindow(window);
            if (window.Left < after.Left - 1 || window.Top < after.Top - 1 ||
                window.Left + window.Width > after.Right + 1 ||
                window.Top + window.Height > after.Bottom + 1)
            {
                throw new InvalidOperationException(
                    "panel at " + window.Left + "," + window.Top + " " + window.Width + "x" + window.Height +
                    " is outside work area " + after);
            }
        }

        /// <summary>
        /// The bottom add row must lay out with room for the font's line box:
        /// content height (row minus border minus padding) at least the line
        /// height plus a 2px safety margin, or the glyph bottoms clip.
        /// </summary>
        private static void TodoAddBoxIsNotClipped()
        {
            var widget = new TodoWidget(new TodoData());
            var view = widget.CalendarView;
            RevealAddBox(view); // the input lives collapsed behind the pill
            LayoutView(view);

            var box = view.FindName("AddBox") as TextBox;
            if (box == null) throw new InvalidOperationException("AddBox not found");
            AssertTextBoxNotClipped("add box", box);
        }

        /// <summary>
        /// The add row must start as the collapsed pill (no placeholder). After
        /// its own click path runs on an EMPTY day, the checkbox input and the
        /// pill are both visible - the pill stacks below (fix round 11, R2).
        /// Keyboard focus cannot be asserted offscreen (no presentation
        /// source), so the human smoke covers the focus.
        /// </summary>
        private static void TodoAddRowIsPillUntilClicked()
        {
            ThemeManager.Initialize("dark");
            var widget = new TodoWidget(new TodoData());
            var view = widget.CalendarView;
            LayoutView(view);

            var pill = view.FindName("AddPill") as FrameworkElement;
            var host = view.FindName("AddInputHost") as FrameworkElement;
            if (pill == null || host == null)
            {
                throw new InvalidOperationException("add row parts not found (pill/host)");
            }

            if (pill.Visibility != Visibility.Visible)
                throw new InvalidOperationException("pill is " + pill.Visibility + " while collapsed");
            if (host.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("input is " + host.Visibility + " while collapsed");

            RevealAddBox(view);

            // Empty day: the input row appears and the pill stays below it.
            // The non-empty reveal is pinned by TodoNonEmptyRevealReplacesThePill.
            if (pill.Visibility != Visibility.Visible)
                throw new InvalidOperationException(
                    "pill is " + pill.Visibility + " after the reveal click on an empty day");
            if (host.Visibility != Visibility.Visible)
                throw new InvalidOperationException("input is " + host.Visibility + " after the reveal click");
        }

        /// <summary>
        /// On a non-empty day the reveal still replaces the pill with the input
        /// row - unchanged by fix round 11's empty-day stacking exception.
        /// </summary>
        private static void TodoNonEmptyRevealReplacesThePill()
        {
            var data = NewTodoData(out TodoItem item); // one item: the day is not empty
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);
            RevealAddBox(view);

            var pill = view.FindName("AddPill") as FrameworkElement;
            var host = view.FindName("AddInputHost") as FrameworkElement;
            if (pill == null || host == null)
            {
                throw new InvalidOperationException("add row parts not found (pill/host)");
            }

            if (pill.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException(
                    "pill is " + pill.Visibility + " after the reveal click on a non-empty day");
            if (host.Visibility != Visibility.Visible)
                throw new InvalidOperationException("input is " + host.Visibility + " after the reveal click");
        }

        /// <summary>
        /// On an empty day the revealed add row keeps BOTH: the checkbox input
        /// row and the full-width rounded pill stacked below it (R2). Fix round
        /// 18: the stack carries the pill's ADD_GAP top margin, so the pill
        /// sits ADD_GAP below the input row's bottom (the list's half-leading).
        /// </summary>
        private static void TodoEmptyRevealedInputKeepsThePill()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);
            RevealAddBox(view);
            LayoutView(view);

            var pill = view.FindName("AddPill") as Border;
            var host = view.FindName("AddInputHost") as FrameworkElement;
            if (pill == null || host == null)
                throw new InvalidOperationException("add row parts not found (pill/host)");
            if (host.Visibility != Visibility.Visible)
                throw new InvalidOperationException("the input host is " + host.Visibility + " after the reveal");
            if (pill.Visibility != Visibility.Visible)
                throw new InvalidOperationException("the pill is " + pill.Visibility + " on the empty revealed day");

            double hostTop = host.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            double pillTop = pill.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            if (Math.Abs(pillTop - (hostTop + host.ActualHeight) - ADD_GAP) > 1.0)
            {
                throw new InvalidOperationException(
                    "the pill is not stacked " + ADD_GAP + " below the input row: pill top " + pillTop.ToString("0.##") +
                    ", host top " + hostTop.ToString("0.##") + " + height " + host.ActualHeight.ToString("0.##"));
            }

            if (pill.CornerRadius.TopLeft <= 0)
                throw new InvalidOperationException("the stacked pill lost its rounded corners: " + pill.CornerRadius);
            if (Math.Abs(pill.ActualWidth - host.ActualWidth) > 1.0)
            {
                throw new InvalidOperationException(
                    "the stacked pill is not full-width: pill " + pill.ActualWidth.ToString("0.##") +
                    " vs host " + host.ActualWidth.ToString("0.##"));
            }
        }

        /// <summary>
        /// Fix round 18: the pill must keep the list's row pitch - the gap from
        /// the last row's text bottom to the pill's top border equals the gap
        /// between two adjacent rows' texts, for a parent last row (14px) and
        /// for a child last row (13px, the human's screenshot).
        /// </summary>
        private static void TodoAddPillKeepsTheListPitch()
        {
            ThemeManager.Initialize("dark");
            DateTime today = DateTime.Today;

            // Case A: the last row is a parent.
            var data = new TodoData { SelectedDate = today };
            TodoListOps.AddItem(data, "First", today);
            TodoListOps.AddItem(data, "Second", today);
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var firstText = FirstDescendant<TextBlock>(RowAt(view, 0));
            var secondText = FirstDescendant<TextBlock>(RowAt(view, 1));
            var pill = view.FindName("AddPill") as FrameworkElement;
            if (firstText == null || secondText == null || pill == null)
                throw new InvalidOperationException("the two-row pitch parts were not found");

            double firstBottom = firstText.TransformToAncestor(view)
                .Transform(new Point(0, firstText.ActualHeight)).Y;
            double secondTop = secondText.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            double secondBottom = secondText.TransformToAncestor(view)
                .Transform(new Point(0, secondText.ActualHeight)).Y;
            double pillTop = pill.TransformToAncestor(view).Transform(new Point(0, 0)).Y;

            double interRowGap = secondTop - firstBottom;
            double pillGap = pillTop - secondBottom;
            if (Math.Abs(pillGap - interRowGap) > 1.5)
            {
                throw new InvalidOperationException(
                    "the pill is not at the parent rows' pitch: pill gap " + pillGap.ToString("0.##") +
                    " vs inter-row gap " + interRowGap.ToString("0.##"));
            }

            // Case B: the last row is a child (the human's screenshot).
            var childData = new TodoData { SelectedDate = today };
            var parent = TodoListOps.AddItem(childData, "Parent", today);
            TodoListOps.AddChild(parent, "Child");
            var childView = new TodoWidget(childData).CalendarView;
            LayoutView(childView);

            var parentText = FirstDescendant<TextBlock>(RowAt(childView, 0));
            var childText = FirstDescendant<TextBlock>(RowAt(childView, 1));
            var childPill = childView.FindName("AddPill") as FrameworkElement;
            if (parentText == null || childText == null || childPill == null)
                throw new InvalidOperationException("the parent/child pitch parts were not found");

            double parentBottom = parentText.TransformToAncestor(childView)
                .Transform(new Point(0, parentText.ActualHeight)).Y;
            double childTop = childText.TransformToAncestor(childView).Transform(new Point(0, 0)).Y;
            double childBottom = childText.TransformToAncestor(childView)
                .Transform(new Point(0, childText.ActualHeight)).Y;
            double childPillTop = childPill.TransformToAncestor(childView).Transform(new Point(0, 0)).Y;

            double childInterRowGap = childTop - parentBottom;
            double childPillGap = childPillTop - childBottom;
            if (Math.Abs(childPillGap - childInterRowGap) > 1.5)
            {
                throw new InvalidOperationException(
                    "the pill is not at the child row's pitch: pill gap " + childPillGap.ToString("0.##") +
                    " vs inter-row gap " + childInterRowGap.ToString("0.##"));
            }
        }

        /// <summary>
        /// Task 2.5: in the zh UI the 2026-10-01 cell (region cn) shows the
        /// 国庆节 region holiday as the second line, below the day number and
        /// in the widget's readable accent (an off day, not 班). Task A4.1:
        /// the 342x355 fixture (content 313 -> cell 36) keeps the label
        /// available under the engine's 32-high threshold.
        /// </summary>
        private static void TodoHolidayLabelShowsInZh()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("zh");
            try
            {
                var data = new TodoData
                {
                    SelectedDate = new DateTime(2026, 10, 1),
                    HolidayRegion = "cn",
                    PanelWidth = 342,
                    PanelHeight = 355
                };
                var view = new TodoWidget(data).CalendarView;
                LayoutView(view);

                var cell = DayCellFor(view, 1);
                var label = DayCellLabelBlock(cell);
                if (label == null || label.Text != "国庆节")
                {
                    throw new InvalidOperationException(
                        "the 2026-10-01 cell shows '" + (label == null ? "<no label>" : label.Text) +
                        "', wanted 国庆节");
                }

                var number = FirstDescendant<TextBlock>(cell);
                if (number == null || number.Text != "1")
                    throw new InvalidOperationException("the labelled cell lost its day number");
                double numberBottom = number.TransformToAncestor(cell)
                    .Transform(new Point(0, number.ActualHeight)).Y;
                double labelTop = label.TransformToAncestor(cell).Transform(new Point(0, 0)).Y;
                if (labelTop < numberBottom - 0.5)
                {
                    throw new InvalidOperationException(
                        "the label is not on the second line: label top " + labelTop.ToString("0.##") +
                        " vs number bottom " + numberBottom.ToString("0.##"));
                }

                Color wanted = Utils.HexToColor(data.Color);
                var brush = label.Foreground as SolidColorBrush;
                if (brush == null || brush.Color != wanted)
                {
                    throw new InvalidOperationException(
                        "the off-day label is not the widget accent: " +
                        (brush == null ? "no solid brush" : Describe(brush.Color)) +
                        ", wanted " + Describe(wanted));
                }
            }
            finally
            {
                Localization.SetLanguage("en");
            }
        }

        /// <summary>
        /// Task 2.5: the label's geometry stays inside its day cell (zh,
        /// 2026-10-08 carries the 寒露 solar term as the base-layer label), so
        /// the stack number -> label -> marks fits the cell. Task A4.1: the
        /// 342x355 fixture (content 313 -> cell 36) first pins the engine's
        /// grown cell height.
        /// </summary>
        private static void TodoHolidayLabelFitsTheCell()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("zh");
            try
            {
                var data = new TodoData
                {
                    SelectedDate = new DateTime(2026, 10, 8),
                    PanelWidth = 342,
                    PanelHeight = 355
                };
                var view = new TodoWidget(data).CalendarView;
                LayoutView(view);

                var grid = view.FindName("DayGrid") as UniformGrid;
                if (grid == null || Math.Abs(grid.Height / 5.0 - 36) > 0.01)
                {
                    throw new InvalidOperationException(
                        "the 355-high panel's cell height is " +
                        (grid == null ? "<no grid>" : (grid.Height / 5.0).ToString("0.##")) +
                        ", wanted 36");
                }

                var cell = DayCellFor(view, 8);
                var label = DayCellLabelBlock(cell);
                if (label == null || label.Text != "寒露")
                {
                    throw new InvalidOperationException(
                        "the 2026-10-08 cell shows '" + (label == null ? "<no label>" : label.Text) +
                        "', wanted the base-layer 寒露");
                }

                if (cell.ActualHeight <= 0 || cell.ActualHeight > 36.01)
                {
                    throw new InvalidOperationException(
                        "the day cell height is " + cell.ActualHeight.ToString("0.##") + ", wanted <= 36");
                }
                double top = label.TransformToAncestor(cell).Transform(new Point(0, 0)).Y;
                double bottom = label.TransformToAncestor(cell)
                    .Transform(new Point(0, label.ActualHeight)).Y;
                if (top < -0.5)
                {
                    throw new InvalidOperationException(
                        "the label top " + top.ToString("0.##") + " pokes above the cell");
                }
                if (bottom > cell.ActualHeight + 0.5)
                {
                    throw new InvalidOperationException(
                        "the label bottom " + bottom.ToString("0.##") +
                        " pokes below the cell bottom " + cell.ActualHeight.ToString("0.##"));
                }

                // The whole number -> label -> marks stack must stay inside
                // the 36 DIP grid cell (the border adds the 1 DIP margin).
                var stack = FirstDescendant<StackPanel>(cell);
                var marks = stack == null
                    ? null
                    : stack.Children[stack.Children.Count - 1] as StackPanel;
                var dayGrid = view.FindName("DayGrid") as UniformGrid;
                if (stack == null || marks == null || dayGrid == null)
                {
                    throw new InvalidOperationException("the labelled cell stack / marks / day grid was not found");
                }
                double gridCellTop = cell.TransformToAncestor(dayGrid).Transform(new Point(0, 0)).Y
                                     - cell.Margin.Top;
                double stackTop = stack.TransformToAncestor(dayGrid).Transform(new Point(0, 0)).Y;
                double marksBottom = marks.TransformToAncestor(dayGrid)
                    .Transform(new Point(0, marks.ActualHeight)).Y;
                if (stackTop < gridCellTop - 0.5)
                {
                    throw new InvalidOperationException(
                        "the number+label+marks stack pokes above the 36 DIP cell: top " +
                        stackTop.ToString("0.##") + " < " + gridCellTop.ToString("0.##"));
                }
                if (marksBottom > gridCellTop + 36.0 + 0.5)
                {
                    throw new InvalidOperationException(
                        "the number+label+marks stack pokes below the 36 DIP cell: bottom " +
                        marksBottom.ToString("0.##") + " > " + (gridCellTop + 36.0).ToString("0.##"));
                }
            }
            finally
            {
                Localization.SetLanguage("en");
            }
        }

        /// <summary>
        /// Task 2.5: the base-layer labels (and the region's 班 workday mark)
        /// use the muted theme text, never the accent. 2026-10-10 is a
        /// 国庆节 make-up workday (班), 2026-10-08 a 寒露 term day.
        /// </summary>
        private static void TodoBaseLayerLabelUsesMutedText()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("zh");
            try
            {
                var workday = new TodoData
                {
                    SelectedDate = new DateTime(2026, 10, 10),
                    PanelWidth = 342,
                    PanelHeight = 355
                };
                var workdayView = new TodoWidget(workday).CalendarView;
                LayoutView(workdayView);
                var workdayLabel = DayCellLabelBlock(DayCellFor(workdayView, 10));
                if (workdayLabel == null || workdayLabel.Text != "班")
                {
                    throw new InvalidOperationException(
                        "the 2026-10-10 label is '" +
                        (workdayLabel == null ? "<missing>" : workdayLabel.Text) + "', wanted 班");
                }
                if (!ReferenceEquals(workdayLabel.Foreground, ThemeManager.MutedTextBrush))
                {
                    throw new InvalidOperationException("the 班 workday mark does not use the muted text brush");
                }

                var term = new TodoData
                {
                    SelectedDate = new DateTime(2026, 10, 8),
                    PanelWidth = 342,
                    PanelHeight = 355
                };
                var termView = new TodoWidget(term).CalendarView;
                LayoutView(termView);
                var termLabel = DayCellLabelBlock(DayCellFor(termView, 8));
                if (termLabel == null || !ReferenceEquals(termLabel.Foreground, ThemeManager.MutedTextBrush))
                {
                    throw new InvalidOperationException("the 寒露 base-layer label does not use the muted text brush");
                }
            }
            finally
            {
                Localization.SetLanguage("en");
            }
        }

        /// <summary>
        /// Task 2.5: the en UI keeps pure numbers - no label element at all.
        /// Task A4.1: the 342x365 fixture gives the cell the full 36 DIP, so
        /// the absence is the language, never a hidden label.
        /// </summary>
        private static void TodoEnglishDayCellsCarryNoLabel()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("en");
            try
            {
                var data = new TodoData
                {
                    SelectedDate = new DateTime(2026, 10, 1),
                    HolidayRegion = "cn",
                    PanelWidth = 342,
                    PanelHeight = 365
                };
                var view = new TodoWidget(data).CalendarView;
                LayoutView(view);

                var grid = view.FindName("DayGrid") as UniformGrid;
                if (grid == null || grid.Children.Count == 0)
                    throw new InvalidOperationException("the day grid was not built");
                foreach (var child in grid.Children)
                {
                    var cell = child as Border;
                    if (cell == null) continue;
                    if (DayCellLabelBlock(cell) != null)
                    {
                        throw new InvalidOperationException(
                            "an en day cell still carries a second-line label");
                    }
                }
            }
            finally
            {
                Localization.SetLanguage("en");
            }
        }

        /// <summary>
        /// Task 2.5: the todo widget menu carries the 5-region 假日地区 submenu
        /// (中国/日本/美国/英国/关闭), checks the data's current region
        /// (null normalizes to 中国) and writes a pick back into the widget data.
        /// </summary>
        private static void TodoHolidayRegionMenu()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("en");

            var data = new TodoData();
            var widget = new TodoWidget(data);

            var menu = WidgetMenuBuilder.Build(widget, null);
            var regionMenu = FindMenuByHeader(menu, Localization.Get("Menu_HolidayRegion"));
            if (regionMenu == null)
                throw new InvalidOperationException("the 假日地区 submenu is missing from the todo menu");
            if (regionMenu.Items.Count != 5)
                throw new InvalidOperationException(
                    "the 假日地区 submenu has " + regionMenu.Items.Count + " item(s), wanted 5");

            string[] keys =
            {
                "Holiday_RegionCn", "Holiday_RegionJp", "Holiday_RegionUs",
                "Holiday_RegionUk", "Holiday_RegionNone"
            };
            for (int i = 0; i < keys.Length; i++)
            {
                var item = regionMenu.Items[i] as MenuItem;
                if (item == null || (item.Header as string) != Localization.Get(keys[i]))
                {
                    throw new InvalidOperationException(
                        "region item " + i + " is '" +
                        (item == null ? "<not a MenuItem>" : item.Header as string) +
                        "', wanted '" + Localization.Get(keys[i]) + "'");
                }
                bool wanted = i == 0; // a null region normalizes to 中国 (cn)
                if (item.IsChecked != wanted)
                {
                    throw new InvalidOperationException(
                        "region item " + i + " check state is " + item.IsChecked +
                        ", wanted " + wanted + " for the default region");
                }
            }

            // Picking 日本 writes the region and repaints through the widget's
            // usual change path (a standalone widget has no save subscriber).
            var japan = regionMenu.Items[1] as MenuItem;
            japan.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (data.HolidayRegion != "jp")
            {
                throw new InvalidOperationException(
                    "clicking 日本 left HolidayRegion at '" + (data.HolidayRegion ?? "null") + "'");
            }

            var reopened = WidgetMenuBuilder.Build(widget, null);
            var reopenedMenu = FindMenuByHeader(reopened, Localization.Get("Menu_HolidayRegion"));
            if (reopenedMenu == null ||
                !((MenuItem)reopenedMenu.Items[1]).IsChecked ||
                ((MenuItem)reopenedMenu.Items[0]).IsChecked)
            {
                throw new InvalidOperationException(
                    "reopening the menu does not reflect the stored region jp");
            }
        }

        #region Task A4.1 layout pins

        /// <summary>
        /// Task A4.1: the day labels' second line only exists when the engine
        /// allows it - the zh cell needs a 32-high cell, the width never
        /// matters, and the en UI stays pure numbers even with a tall cell.
        /// </summary>
        private static void TodoDayLabelsAppearOnlyAtTheLabelThreshold()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("zh");
            try
            {
                // 342x364: content 322 -> (322 - 133 - 30) / 5 = 31.8.
                var below = NewTodoLabelData(342, 364);
                var belowView = new TodoWidget(below).CalendarView;
                LayoutView(belowView);
                if (DayCellLabelBlock(DayCellFor(belowView, 12)) != null)
                {
                    throw new InvalidOperationException(
                        "the 31.8-high zh cell still shows the second line");
                }

                // 342x365: content 323 -> (323 - 133 - 30) / 5 = 32.
                var at = NewTodoLabelData(342, 365);
                var atView = new TodoWidget(at).CalendarView;
                LayoutView(atView);
                if (DayCellLabelBlock(DayCellFor(atView, 12)) == null)
                {
                    throw new InvalidOperationException("the 32-high zh cell lost its second line");
                }

                // The same height at 500 wide still shows it (no width fallback).
                var wide = NewTodoLabelData(500, 365);
                var wideView = new TodoWidget(wide).CalendarView;
                LayoutView(wideView);
                if (DayCellLabelBlock(DayCellFor(wideView, 12)) == null)
                {
                    throw new InvalidOperationException(
                        "the 500-wide 365-high zh cell lost its second line");
                }

                // en keeps pure numbers even when the cell is tall.
                Localization.SetLanguage("en");
                var en = NewTodoLabelData(342, 365);
                var enView = new TodoWidget(en).CalendarView;
                LayoutView(enView);
                if (DayCellLabelBlock(DayCellFor(enView, 12)) != null)
                {
                    throw new InvalidOperationException("the en cell shows a second line at 32");
                }
            }
            finally
            {
                Localization.SetLanguage("en");
            }
        }

        /// <summary>
        /// One item on 2026-10-12 (a five-week month) at a panel size. The
        /// item is completed on that day so its row displays there on every
        /// run date - an open past-dated entry carries to today, which would
        /// shrink the list and grow the calendar cells.
        /// </summary>
        private static TodoData NewTodoLabelData(double width, double height)
        {
            var data = new TodoData
            {
                SelectedDate = new DateTime(2026, 10, 12),
                PanelWidth = width,
                PanelHeight = height
            };
            TodoRollover.Toggle(TodoListOps.AddItem(data, "item", new DateTime(2026, 10, 12)),
                true, new DateTime(2026, 10, 12));
            return data;
        }

        /// <summary>
        /// The 342x337 minimum panel with seven displayed rows on the
        /// six-week 2026-08-12: the entries are completed on 8/12 so they
        /// display there (open past-dated entries carry to today).
        /// </summary>
        private static TodoData NewSevenRowAugustDay()
        {
            DateTime day = new DateTime(2026, 8, 12);
            var data = new TodoData { PanelWidth = 342, PanelHeight = 337, SelectedDate = day };
            for (int i = 0; i < 7; i++)
            {
                var item = TodoListOps.AddItem(data, "item " + i, day);
                TodoRollover.Toggle(item, true, day);
            }
            return data;
        }

        /// <summary>
        /// Task A4.1: the panel size is the user's resize result alone - a day
        /// switch and an item edit never resize an open panel, neither with
        /// persisted sizes (400x500) nor with the legacy fallback, which used
        /// to shrink an open 342x400 panel to the empty day's auto size (315).
        /// </summary>
        private static void TodoPanelSizeNoLongerFollowsTheContent()
        {
            ThemeManager.Initialize("dark");
            DateTime today = DateTime.Today;

            // (a) Persisted 400x500 with one item.
            var data = new TodoData { PanelWidth = 400, PanelHeight = 500, SelectedDate = today };
            TodoListOps.AddItem(data, "item", today);
            var widget = new TodoWidget(data);
            EnsureRealDpi(widget);
            widget.Width = 400;
            widget.Height = 500;
            widget.Left = 100;
            widget.Top = 100;
            SetPrivateBaseField(widget, "_isExpanded", true);

            InvokeViewSelectDay(widget.CalendarView, today.AddDays(1)); // an empty day
            AssertPanelSizeUnchanged(widget, 400, 500, "after the day switch");

            TodoListOps.AddItem(data, "another", today.AddDays(1));
            InvokeViewAction(widget.CalendarView, "AfterItemEdit");
            AssertPanelSizeUnchanged(widget, 400, 500, "after the item edit");

            // (b) No persisted size: the open panel's live height must survive
            // the day switch too (pre-fix: 42 + 273 = 315).
            var liveData = new TodoData { SelectedDate = today };
            TodoListOps.AddItem(liveData, "one", today);
            TodoListOps.AddItem(liveData, "two", today);
            var live = new TodoWidget(liveData);
            EnsureRealDpi(live);
            live.Width = 342;
            live.Height = 400;
            live.Left = 100;
            live.Top = 100;
            SetPrivateBaseField(live, "_isExpanded", true);

            InvokeViewSelectDay(live.CalendarView, today.AddDays(1));
            if (live.Height != 400)
            {
                throw new InvalidOperationException(
                    "selecting an empty day resized the open panel to " + live.Height + ", wanted 400");
            }
        }

        private static void AssertPanelSizeUnchanged(TodoWidget widget, double width, double height, string label)
        {
            if (widget.Width != width || widget.Height != height)
            {
                throw new InvalidOperationException(
                    label + ": the window is " + widget.Width + "x" + widget.Height +
                    ", wanted " + width + "x" + height);
            }
            var size = widget.GetPanelSize();
            if (size.width != width || size.height != height)
            {
                throw new InvalidOperationException(
                    label + ": GetPanelSize reports " + size.width + "x" + size.height +
                    ", wanted " + width + "x" + height);
            }
        }

        private static void InvokeViewAction(TodoCalendarView view, string method)
        {
            var action = typeof(TodoCalendarView).GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (action == null) throw new InvalidOperationException(method + " not found on TodoCalendarView");
            action.Invoke(view, null);
        }

        /// <summary>Invokes a private view method with arguments (B3 pins).</summary>
        private static void InvokeViewActionWith(TodoCalendarView view, string method, params object[] args)
        {
            var action = typeof(TodoCalendarView).GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (action == null) throw new InvalidOperationException(method + " not found on TodoCalendarView");
            action.Invoke(view, args);
        }

        /// <summary>The view's undo-stack size, read reflectively (B3).</summary>
        private static int UndoStackCount(TodoCalendarView view)
        {
            var field = typeof(TodoCalendarView).GetField("_undoStack",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("_undoStack not found on TodoCalendarView");
            var stack = field.GetValue(view) as System.Collections.ICollection;
            if (stack == null) throw new InvalidOperationException("_undoStack is not a collection");
            return stack.Count;
        }

        /// <summary>
        /// Builds the view's day-area menu through reflection (T4; the RED run
        /// has no such method yet) - the typed variant of InvokeViewAction.
        /// </summary>
        private static ContextMenu InvokeBuildDayAreaMenu(TodoCalendarView view)
        {
            var method = typeof(TodoCalendarView).GetMethod("BuildDayAreaMenu",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("BuildDayAreaMenu not found on TodoCalendarView");
            return (ContextMenu)method.Invoke(view, null);
        }

        /// <summary>
        /// Task A4.1: a resize drag re-adapts the todo layout against the live
        /// window height (the base's OnPanelSizeChanged refreshes the view):
        /// 337 (cell 26.4) -> +200 -> 537 (cell 36 clamp) -> -300 -> 337 again.
        /// The open flag is set reflectively, so no window is ever shown.
        /// </summary>
        private static void ResizeReadaptsTodoCellHeightDuringTheDrag()
        {
            ThemeManager.Initialize("dark");
            DateTime selected = new DateTime(2026, 10, 12);
            var data = new TodoData { PanelWidth = 342, PanelHeight = 337, SelectedDate = selected };
            // Completed on the day: the row must display there on every run
            // date (an open past-dated entry carries to today).
            TodoRollover.Toggle(TodoListOps.AddItem(data, "item", selected), true, selected);
            var widget = new TodoWidget(data);
            EnsureRealDpi(widget);
            widget.Width = 342;
            widget.Height = 337;
            widget.Left = 100;
            widget.Top = 100;

            // Closed: the persisted size already drives the layout
            // (295 - 133 - 30 = 132 -> 26.4 per cell, 132 total for five weeks).
            if (Math.Abs(DayGridHeight(widget.CalendarView) - 132) > 0.5)
            {
                throw new InvalidOperationException(
                    "the closed panel's day grid is " +
                    DayGridHeight(widget.CalendarView).ToString("0.##") + ", wanted 132");
            }

            SetPrivateBaseField(widget, "_isExpanded", true);
            double scale = VisualTreeHelper.GetDpi(widget).DpiScaleX;

            InvokeResizeStarted(widget, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(widget,
                new object[] { new Point(100 + 200 * scale, 100 + 200 * scale) });
            if (Math.Abs(widget.Height - 537) > 0.5)
            {
                throw new InvalidOperationException(
                    "the drag made the panel " + widget.Height + " high, wanted 537");
            }
            if (Math.Abs(DayGridHeight(widget.CalendarView) - 180) > 0.5)
            {
                throw new InvalidOperationException(
                    "the grown panel's day grid is " +
                    DayGridHeight(widget.CalendarView).ToString("0.##") +
                    ", wanted 180 (content 495 -> cell 36)");
            }

            InvokeResizeStarted(widget, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(widget,
                new object[] { new Point(100 - 300 * scale, 100 - 300 * scale) });
            if (Math.Abs(widget.Height - 337) > 0.5)
            {
                throw new InvalidOperationException(
                    "the shrinking drag made the panel " + widget.Height + " high, wanted the 337 minimum");
            }
            if (Math.Abs(DayGridHeight(widget.CalendarView) - 132) > 0.5)
            {
                throw new InvalidOperationException(
                    "the shrunk panel's day grid is " +
                    DayGridHeight(widget.CalendarView).ToString("0.##") + ", wanted 132");
            }

            PrivateBaseMethod("OnResizeEnded").Invoke(widget, new object[] { new Point(100, 100) });
        }

        private static double DayGridHeight(TodoCalendarView view)
        {
            var grid = view.FindName("DayGrid") as UniformGrid;
            if (grid == null) throw new InvalidOperationException("the day grid was not found");
            return grid.Height;
        }

        /// <summary>
        /// Task A4.1: the view's natural height (the legacy fallback) matches
        /// the engine's one-shot migration sizes - the engine adds the 40-high
        /// header to the same content, and the 2 DIP shell border is chrome,
        /// not part of the engine's formula - and the revealed empty day adds
        /// exactly the extra input row (30).
        /// </summary>
        private static void TodoNaturalHeightMatchesTheEngine()
        {
            ThemeManager.Initialize("dark");
            int weeks = TodayWeekCount();

            var empty = new TodoWidget(new TodoData());
            var emptyView = empty.CalendarView;
            var (_, wantEmpty) = PanelLayoutAdapter.DefaultTodoSize(weeks, 0);
            AssertNaturalHeightMatchesTheEngine(emptyView, wantEmpty, "the empty day");

            var oneData = new TodoData { SelectedDate = DateTime.Today };
            TodoListOps.AddItem(oneData, "item", DateTime.Today);
            var oneView = new TodoWidget(oneData).CalendarView;
            var (_, wantOne) = PanelLayoutAdapter.DefaultTodoSize(weeks, 1);
            AssertNaturalHeightMatchesTheEngine(oneView, wantOne, "the one-item day");

            double natural = emptyView.DesiredHeight;
            RevealAddBox(emptyView);
            if (Math.Abs(emptyView.DesiredHeight - (natural + 30)) > 0.001)
            {
                throw new InvalidOperationException(
                    "the revealed empty day wants " + emptyView.DesiredHeight.ToString("0.##") +
                    ", wanted the natural " + natural.ToString("0.##") + " + 30");
            }
            AssertNaturalHeightMatchesTheEngine(emptyView, wantEmpty + 30, "the revealed empty day");
        }

        private static int TodayWeekCount()
        {
            var culture = TodoCalendar.CultureForLanguage(Localization.CurrentLanguage);
            return TodoCalendar.WeekCount(DateTime.Today.Year, DateTime.Today.Month,
                culture.DateTimeFormat.FirstDayOfWeek);
        }

        private static void AssertNaturalHeightMatchesTheEngine(TodoCalendarView view, double engineHeight, string label)
        {
            // DefaultTodoSize.h = HEADER_HEIGHT + the view's natural content
            // (its 2 DIP shell border is chrome, not part of the engine's
            // migration formula), so this is the content formula match.
            double actual = view.DesiredHeight + WidgetConstants.HEADER_HEIGHT;
            if (Math.Abs(actual - engineHeight) > 0.001)
            {
                throw new InvalidOperationException(
                    label + ": the natural height is " + actual.ToString("0.###") +
                    " (content " + view.DesiredHeight.ToString("0.###") + " + header " +
                    WidgetConstants.HEADER_HEIGHT + "), wanted the engine's " +
                    engineHeight.ToString("0.###"));
            }
        }

        /// <summary>
        /// Task A4.1 (the user's smoke hard requirement): at the 342x337
        /// minimum the add row stays fully visible - a six-week month with
        /// seven items squeezes the cells to 22 and the list to one row, and
        /// a revealed empty-day input still leaves the add row inside the
        /// 295-high content host.
        /// </summary>
        private static void TodoAddRowStaysVisibleAtTheMinimum()
        {
            ThemeManager.Initialize("dark");

            // Seven items on a six-week month: the list shrinks to one row.
            var data = NewSevenRowAugustDay();
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            var chrome = (WidgetChrome)widget.Content;
            ArrangeChrome(chrome, 342, 337);

            if (Math.Abs(chrome.ContentHost.ActualHeight - 295) > 0.5)
            {
                throw new InvalidOperationException(
                    "the content host is " + chrome.ContentHost.ActualHeight.ToString("0.##") +
                    " high, wanted 295");
            }
            var dayGrid = view.FindName("DayGrid") as UniformGrid;
            if (dayGrid == null || Math.Abs(dayGrid.Height - 132) > 0.5)
            {
                throw new InvalidOperationException(
                    "the minimum panel's day grid is " +
                    (dayGrid == null ? "<no grid>" : dayGrid.Height.ToString("0.##")) + ", wanted 132");
            }
            var scroller = view.FindName("ItemsScroller") as FrameworkElement;
            if (scroller == null || Math.Abs(scroller.Height - 30) > 0.5)
            {
                throw new InvalidOperationException(
                    "the minimum panel's list is not one row: " +
                    (scroller == null ? "ItemsScroller missing" : scroller.Height.ToString("0.##")));
            }
            var pill = view.FindName("AddPill") as FrameworkElement;
            if (pill == null || pill.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException("the add pill is not visible at the minimum size");
            }
            double addBottom = AddRowBottom(view, chrome);
            if (addBottom > 295 + 0.5)
            {
                throw new InvalidOperationException(
                    "the add row's bottom " + addBottom.ToString("0.##") +
                    " pokes below the 295-high content host");
            }

            // Revealed input on an empty day of the same six-week month.
            DateTime emptyDay = new DateTime(2026, 8, 19);
            var emptyData = new TodoData { PanelWidth = 342, PanelHeight = 337, SelectedDate = emptyDay };
            var emptyWidget = new TodoWidget(emptyData);
            var emptyView = emptyWidget.CalendarView;
            RevealAddBox(emptyView);
            var emptyChrome = (WidgetChrome)emptyWidget.Content;
            ArrangeChrome(emptyChrome, 342, 337);

            if (Math.Abs(emptyChrome.ContentHost.ActualHeight - 295) > 0.5)
            {
                throw new InvalidOperationException(
                    "the revealed content host is " + emptyChrome.ContentHost.ActualHeight.ToString("0.##") +
                    " high, wanted 295");
            }
            var emptyGrid = emptyView.FindName("DayGrid") as UniformGrid;
            if (emptyGrid == null || Math.Abs(emptyGrid.Height - 132) > 0.5)
            {
                throw new InvalidOperationException(
                    "the revealed minimum panel's day grid is " +
                    (emptyGrid == null ? "<no grid>" : emptyGrid.Height.ToString("0.##")) + ", wanted 132");
            }
            var emptyScroller = emptyView.FindName("ItemsScroller") as FrameworkElement;
            if (emptyScroller == null || Math.Abs(emptyScroller.Height) > 0.01)
            {
                throw new InvalidOperationException(
                    "the revealed empty day reserves a row: " +
                    (emptyScroller == null ? "ItemsScroller missing" : emptyScroller.Height.ToString("0.##")));
            }
            var emptyHost = emptyView.FindName("AddInputHost") as FrameworkElement;
            if (emptyHost == null || emptyHost.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException("the revealed input is not visible at the minimum size");
            }
            var emptyPill = emptyView.FindName("AddPill") as FrameworkElement;
            if (emptyPill == null || emptyPill.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException("the stacked pill is not visible at the minimum size");
            }
            double emptyBottom = AddRowBottom(emptyView, emptyChrome);
            if (emptyBottom > 295 + 0.5)
            {
                throw new InvalidOperationException(
                    "the revealed add row's bottom " + emptyBottom.ToString("0.##") +
                    " pokes below the 295-high content host");
            }
        }

        /// <summary>
        /// Task A4.2: dragging the calendar/list divider edits the widget's
        /// explicit cell height (-30 drag = 6 per the five weeks, clamped to
        /// 22..36); the locked panel refuses the drag entirely. The normal
        /// release runs the real handler and must persist through
        /// OnDataChanged (the capture-loss path never persists).
        /// </summary>
        private static void TodoSplitterDragsTheCalendarHeight()
        {
            ThemeManager.Initialize("dark");
            var data = new TodoData
            {
                PanelWidth = 342,
                PanelHeight = 385,
                SelectedDate = new DateTime(2026, 10, 12)
            };
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;

            if (Math.Abs(DayGridHeight(view) - 180) > 0.01)
            {
                throw new InvalidOperationException(
                    "the untouched divider fixture is " + DayGridHeight(view).ToString("0.##") +
                    ", wanted the auto 36-cell 180");
            }

            var started = ViewPrivateMethod("SplitterDragStarted");
            var moved = ViewPrivateMethod("SplitterDragMoved");

            if (!(bool)started.Invoke(view, new object[] { 100.0 }))
            {
                throw new InvalidOperationException("an unlocked splitter refused the drag");
            }
            moved.Invoke(view, new object[] { 70.0 });
            if (data.CalendarCellHeight == null ||
                Math.Abs(data.CalendarCellHeight.Value - 30) > 0.01)
            {
                throw new InvalidOperationException(
                    "a -30 drag set the cell to " +
                    (data.CalendarCellHeight.HasValue
                        ? data.CalendarCellHeight.Value.ToString("0.##") : "<null>") +
                    ", wanted 36 - 30/5 = 30");
            }
            if (Math.Abs(DayGridHeight(view) - 150) > 0.01)
            {
                throw new InvalidOperationException(
                    "the 30-cell day grid is " + DayGridHeight(view).ToString("0.##") + ", wanted 150");
            }

            moved.Invoke(view, new object[] { 1100.0 });
            if (data.CalendarCellHeight == null ||
                Math.Abs(data.CalendarCellHeight.Value - 36) > 0.01)
            {
                throw new InvalidOperationException(
                    "a huge downward drag did not clamp the cell to 36: " +
                    (data.CalendarCellHeight.HasValue
                        ? data.CalendarCellHeight.Value.ToString("0.##") : "<null>"));
            }
            if (Math.Abs(DayGridHeight(view) - 180) > 0.01)
            {
                throw new InvalidOperationException(
                    "the clamped 36-cell day grid is " + DayGridHeight(view).ToString("0.##") +
                    ", wanted 180");
            }

            moved.Invoke(view, new object[] { -900.0 });
            if (data.CalendarCellHeight == null ||
                Math.Abs(data.CalendarCellHeight.Value - 22) > 0.01)
            {
                throw new InvalidOperationException(
                    "a huge upward drag did not clamp the cell to 22: " +
                    (data.CalendarCellHeight.HasValue
                        ? data.CalendarCellHeight.Value.ToString("0.##") : "<null>"));
            }
            if (Math.Abs(DayGridHeight(view) - 110) > 0.01)
            {
                throw new InvalidOperationException(
                    "the clamped 22-cell day grid is " + DayGridHeight(view).ToString("0.##") +
                    ", wanted 110");
            }

            // The real release path: releasing the capture raises
            // LostMouseCapture synchronously, so the handler must end the drag
            // (and persist it) before that. No capture is held offscreen, so
            // ReleaseMouseCapture stays a no-op.
            bool raised = false;
            widget.OnDataChanged += () => raised = true;
            var splitterBand = view.FindName("SplitterBand");
            var upArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            };
            ViewPrivateMethod("SplitterBand_MouseLeftButtonUp")
                .Invoke(view, new object[] { splitterBand, upArgs });
            if (!raised)
            {
                throw new InvalidOperationException(
                    "the real release path did not persist the split (NotifyDataChanged was not raised)");
            }
            if (PrivateField<bool>(view, "_splitDragging"))
            {
                throw new InvalidOperationException("the release path left _splitDragging set");
            }

            // Locked: the band is inert and the logic refuses the drag.
            var lockedData = new TodoData
            {
                IsLocked = true,
                PanelWidth = 342,
                PanelHeight = 385,
                SelectedDate = new DateTime(2026, 10, 12)
            };
            var lockedView = new TodoWidget(lockedData).CalendarView;
            var band = lockedView.FindName("SplitterBand") as FrameworkElement;
            if (band == null || band.IsHitTestVisible)
            {
                throw new InvalidOperationException(
                    "the locked panel's splitter band is " +
                    (band == null ? "missing" : "hit-testable"));
            }
            if ((bool)started.Invoke(lockedView, new object[] { 100.0 }))
            {
                throw new InvalidOperationException("the locked splitter started a drag");
            }
            moved.Invoke(lockedView, new object[] { 70.0 });
            if (lockedData.CalendarCellHeight != null)
            {
                throw new InvalidOperationException(
                    "a moved locked splitter wrote the cell " +
                    lockedData.CalendarCellHeight.Value.ToString("0.##"));
            }
        }

        /// <summary>
        /// Task A4.2: an explicit split (30) stays put while the outer panel
        /// grows - the engine keeps the user's cell height and only the list
        /// viewport absorbs the new space.
        /// </summary>
        private static void TodoExplicitSplitSurvivesTheOuterResize()
        {
            ThemeManager.Initialize("dark");
            DateTime selected = new DateTime(2026, 10, 12);
            var data = new TodoData
            {
                PanelWidth = 342,
                PanelHeight = 385,
                SelectedDate = selected,
                CalendarCellHeight = 30
            };
            TodoListOps.AddItem(data, "item", selected);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;

            if (Math.Abs(DayGridHeight(view) - 150) > 0.01)
            {
                throw new InvalidOperationException(
                    "the explicit 30-cell day grid is " + DayGridHeight(view).ToString("0.##") +
                    ", wanted 150");
            }

            EnsureRealDpi(widget);
            widget.Width = 342;
            widget.Height = 385;
            widget.Left = 100;
            widget.Top = 100;
            SetPrivateBaseField(widget, "_isExpanded", true);
            double scale = VisualTreeHelper.GetDpi(widget).DpiScaleX;

            InvokeResizeStarted(widget, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(widget,
                new object[] { new Point(100 + 200 * scale, 100 + 200 * scale) });
            if (Math.Abs(widget.Height - 585) > 0.5)
            {
                throw new InvalidOperationException(
                    "the drag made the panel " + widget.Height + " high, wanted 585");
            }
            if (Math.Abs(DayGridHeight(view) - 150) > 0.01)
            {
                throw new InvalidOperationException(
                    "the resized panel's day grid is " + DayGridHeight(view).ToString("0.##") +
                    ", wanted the explicit 30-cell 150");
            }
            if (data.CalendarCellHeight == null ||
                Math.Abs(data.CalendarCellHeight.Value - 30) > 0.01)
            {
                throw new InvalidOperationException(
                    "the resize changed the stored split to " +
                    (data.CalendarCellHeight.HasValue
                        ? data.CalendarCellHeight.Value.ToString("0.##") : "<null>") +
                    ", wanted 30");
            }
            PrivateBaseMethod("OnResizeEnded").Invoke(widget, new object[] { new Point(100, 100) });
        }

        /// <summary>
        /// Task A4.2: a resize drag only re-lays-out the calendar geometry; the
        /// built day-list rows are kept (their references survive the drag).
        /// </summary>
        private static void TodoResizeKeepsTheListRows()
        {
            ThemeManager.Initialize("dark");
            DateTime selected = new DateTime(2026, 10, 12);
            var data = new TodoData { PanelWidth = 342, PanelHeight = 337, SelectedDate = selected };
            // Completed on the day: the row must display there on every run
            // date (an open past-dated entry carries to today).
            TodoRollover.Toggle(TodoListOps.AddItem(data, "item", selected), true, selected);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;

            var panel = view.FindName("ItemsPanel") as Panel;
            if (panel == null || panel.Children.Count == 0)
            {
                throw new InvalidOperationException("the list rows were not built");
            }
            if (Math.Abs(DayGridHeight(view) - 132) > 0.5)
            {
                throw new InvalidOperationException(
                    "the minimum panel's day grid is " + DayGridHeight(view).ToString("0.##") +
                    ", wanted 132");
            }
            var firstRow = panel.Children[0];

            EnsureRealDpi(widget);
            widget.Width = 342;
            widget.Height = 337;
            widget.Left = 100;
            widget.Top = 100;
            SetPrivateBaseField(widget, "_isExpanded", true);
            double scale = VisualTreeHelper.GetDpi(widget).DpiScaleX;

            InvokeResizeStarted(widget, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(widget,
                new object[] { new Point(100 + 200 * scale, 100 + 200 * scale) });
            if (Math.Abs(widget.Height - 537) > 0.5)
            {
                throw new InvalidOperationException(
                    "the drag made the panel " + widget.Height + " high, wanted 537");
            }
            if (Math.Abs(DayGridHeight(view) - 180) > 0.5)
            {
                throw new InvalidOperationException(
                    "the grown panel's day grid is " + DayGridHeight(view).ToString("0.##") +
                    ", wanted 180 (content 495 -> cell 36)");
            }
            if (!ReferenceEquals(firstRow, panel.Children[0]))
            {
                throw new InvalidOperationException("the resize rebuilt the day-list rows");
            }
            PrivateBaseMethod("OnResizeEnded").Invoke(widget, new object[] { new Point(100, 100) });
        }

        /// <summary>
        /// Task A4.2 (fix wave): a resize drag crossing the 32-high label
        /// threshold flips the labels in place - the zh second lines appear
        /// and the day-list rows survive (only the calendar rebuilds).
        /// </summary>
        private static void TodoResizeFlipsTheDayLabelsInPlace()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("zh");
            try
            {
                DateTime selected = new DateTime(2026, 10, 1);
                var data = new TodoData
                {
                    PanelWidth = 342,
                    PanelHeight = 363,
                    SelectedDate = selected
                };
                // Completed on 10/1 so it displays there (an open past-dated
                // entry would carry to today and leave the day empty).
                var item = TodoListOps.AddItem(data, "item", selected);
                TodoRollover.Toggle(item, true, selected);
                var widget = new TodoWidget(data);
                var view = widget.CalendarView;

                // 363-high closed: content 321 -> (321 - 133 - 30) / 5 = 31.6.
                if (DayCellLabelBlock(DayCellFor(view, 1)) != null)
                {
                    throw new InvalidOperationException(
                        "the 31.6-high zh cell already shows its second line");
                }

                var panel = view.FindName("ItemsPanel") as Panel;
                if (panel == null || panel.Children.Count == 0)
                {
                    throw new InvalidOperationException("the list rows were not built");
                }
                var firstRow = panel.Children[0];

                EnsureRealDpi(widget);
                widget.Width = 342;
                widget.Height = 363;
                widget.Left = 100;
                widget.Top = 100;
                SetPrivateBaseField(widget, "_isExpanded", true);
                double scale = VisualTreeHelper.GetDpi(widget).DpiScaleX;

                InvokeResizeStarted(widget, WidgetResizeEdge.BottomRight, new Point(100, 100));
                PrivateBaseMethod("OnResizeMoved").Invoke(widget,
                    new object[] { new Point(100 + 2 * scale, 100 + 2 * scale) });
                if (Math.Abs(widget.Height - 365) > 0.5)
                {
                    throw new InvalidOperationException(
                        "the drag made the panel " + widget.Height + " high, wanted 365");
                }
                // 365-high open: content 323 -> (323 - 133 - 30) / 5 = 32.0.
                if (DayCellLabelBlock(DayCellFor(view, 1)) == null)
                {
                    throw new InvalidOperationException(
                        "the 32-high zh cell lost its second line after the drag");
                }
                if (!ReferenceEquals(firstRow, panel.Children[0]))
                {
                    throw new InvalidOperationException(
                        "the label flip rebuilt the day-list rows");
                }
                PrivateBaseMethod("OnResizeEnded").Invoke(widget, new object[] { new Point(100, 100) });
            }
            finally
            {
                Localization.SetLanguage("en");
            }
        }

        /// <summary>
        /// Task A4.2: clicking add on an overflowing list reveals the input and
        /// scrolls the list to the bottom; an empty day has no extent, so the
        /// reveal stays at offset zero.
        /// </summary>
        private static void TodoAddClickScrollsToTheBottom()
        {
            ThemeManager.Initialize("dark");
            var data = NewSevenRowAugustDay();
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            ArrangeChrome((WidgetChrome)widget.Content, 342, 337);

            var scroller = view.FindName("ItemsScroller") as ScrollViewer;
            if (scroller == null || scroller.ScrollableHeight <= 0)
            {
                throw new InvalidOperationException(
                    "the seven-row fixture does not scroll: " +
                    (scroller == null
                        ? "ItemsScroller missing"
                        : scroller.ScrollableHeight.ToString("0.##")));
            }

            RevealAddBox(view);
            scroller.UpdateLayout();
            if (!(scroller.VerticalOffset > 0) ||
                Math.Abs(scroller.VerticalOffset - scroller.ScrollableHeight) > 0.5)
            {
                throw new InvalidOperationException(
                    "the reveal left the list at " + scroller.VerticalOffset.ToString("0.##") +
                    " of " + scroller.ScrollableHeight.ToString("0.##"));
            }

            // An empty day has nothing to scroll: the reveal stays at zero.
            var emptyData = new TodoData
            {
                PanelWidth = 342,
                PanelHeight = 337,
                SelectedDate = new DateTime(2026, 8, 19)
            };
            var emptyWidget = new TodoWidget(emptyData);
            var emptyView = emptyWidget.CalendarView;
            ArrangeChrome((WidgetChrome)emptyWidget.Content, 342, 337);

            RevealAddBox(emptyView);
            var emptyScroller = emptyView.FindName("ItemsScroller") as ScrollViewer;
            if (emptyScroller == null)
            {
                throw new InvalidOperationException("the empty day's ItemsScroller is missing");
            }
            emptyScroller.UpdateLayout();
            if (Math.Abs(emptyScroller.VerticalOffset) > 0.01)
            {
                throw new InvalidOperationException(
                    "the empty day's reveal scrolled to " +
                    emptyScroller.VerticalOffset.ToString("0.##"));
            }
        }

        private static void TodoMetricsAreComputedOnce()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("zh");
            try
            {
                var data = new TodoData
                {
                    PanelWidth = 342,
                    PanelHeight = 385,
                    SelectedDate = new DateTime(2026, 10, 12)
                };
                var widget = new TodoWidget(data);
                var view = widget.CalendarView;

                var metrics = ViewPrivateMethod("ComputeMetrics")
                    .Invoke(view, new object[] { new DateTime(2026, 10, 12) });
                var type = metrics.GetType();
                int weekCount = (int)type.GetField("WeekCount").GetValue(metrics);
                double cell = (double)type.GetField("Cell").GetValue(metrics);

                if (weekCount != 5 || Math.Abs(cell - 36) > 0.01)
                {
                    throw new InvalidOperationException(
                        "the metrics fixture computed " + weekCount + " weeks / cell " + cell.ToString("0.##") +
                        ", wanted 5 / 36");
                }
                if (!(bool)type.GetField("ShowLabels").GetValue(metrics))
                    throw new InvalidOperationException("zh with a 36-high cell must show the day labels");
                if (!(bool)type.GetField("DayIsEmpty").GetValue(metrics) ||
                    (int)type.GetField("NaturalRows").GetValue(metrics) != 0)
                    throw new InvalidOperationException("the itemless fixture must read as an empty day");
                if (Math.Abs((double)type.GetField("ContentH").GetValue(metrics) - 343) > 0.01 ||
                    Math.Abs((double)type.GetField("AddBlock").GetValue(metrics) - 36) > 0.01)
                    throw new InvalidOperationException(
                        "the metrics fixture must carry the 343 content height and the 36 add block");
                if (Math.Abs(DayGridHeight(view) - weekCount * cell) > 0.01)
                    throw new InvalidOperationException(
                        "the computed cell does not match the rendered day grid: " +
                        DayGridHeight(view).ToString("0.##") + " vs " + (weekCount * cell).ToString("0.##"));
            }
            finally
            {
                Localization.SetLanguage("en");
            }
        }

        private static MethodInfo ViewPrivateMethod(string name)
        {
            var method = typeof(TodoCalendarView).GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException(name + " not found on TodoCalendarView");
            return method;
        }

        private static MethodInfo FolderPrivateMethod(string name)
        {
            var method = typeof(FolderWidget).GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException(name + " not found on FolderWidget");
            return method;
        }

        /// <summary>
        /// Asserts the first menu entry with the given header exists and
        /// carries the wanted enabled state - menu structures only, nothing
        /// is opened.
        /// </summary>
        private static void AssertMenuItem(ContextMenu menu, string header, bool enabled, string label)
        {
            foreach (var entry in menu.Items)
            {
                var item = entry as MenuItem;
                if (item == null || !Equals(item.Header, header)) continue;
                if (item.IsEnabled != enabled)
                {
                    throw new InvalidOperationException(
                        label + ": '" + header + "' is enabled=" + item.IsEnabled +
                        ", wanted " + enabled);
                }
                return;
            }
            throw new InvalidOperationException(label + ": no entry with header '" + header + "'");
        }

        /// <summary>
        /// Asserts no menu entry carries the header - the absent case of the
        /// paste gate (smoke fix B): menu structures only.
        /// </summary>
        private static void AssertMenuItemAbsent(ContextMenu menu, string header, string label)
        {
            foreach (var entry in menu.Items)
            {
                var item = entry as MenuItem;
                if (item != null && Equals(item.Header, header))
                {
                    throw new InvalidOperationException(
                        label + ": unexpected entry '" + header + "' (enabled=" + item.IsEnabled + ")");
                }
            }
        }

        private static void ArrangeChrome(WidgetChrome chrome, double width, double height)
        {
            chrome.Measure(new Size(width, height));
            chrome.Arrange(new Rect(0, 0, width, height));
            chrome.UpdateLayout();
        }

        private static double AddRowBottom(TodoCalendarView view, WidgetChrome chrome)
        {
            var addRow = view.FindName("AddRow") as FrameworkElement;
            if (addRow == null) throw new InvalidOperationException("AddRow not found");
            return addRow.TransformToAncestor(chrome.ContentHost)
                .Transform(new Point(0, addRow.ActualHeight)).Y;
        }

        /// <summary>The day header's row (the parent of the header text).</summary>
        private static FrameworkElement DayHeaderRow(TodoCalendarView view)
        {
            var header = view.FindName("DayHeaderText") as FrameworkElement;
            var row = header == null ? null : header.Parent as FrameworkElement;
            if (row == null) throw new InvalidOperationException("the day header row was not found");
            return row;
        }

        #endregion

        /// <summary>The second-line label TextBlock of a day cell, or null.</summary>
        private static TextBlock DayCellLabelBlock(Border cell)
        {
            var stack = FirstDescendant<StackPanel>(cell);
            if (stack == null) return null;

            int seen = 0;
            foreach (var child in stack.Children)
            {
                var block = child as TextBlock;
                if (block == null) continue;
                if (++seen == 2) return block;
            }
            return null;
        }

        /// <summary>The top-level menu item with the given header, or null.</summary>
        private static MenuItem FindMenuByHeader(ContextMenu menu, string header)
        {
            foreach (var entry in menu.Items)
            {
                var item = entry as MenuItem;
                if (item != null && (item.Header as string) == header) return item;
            }
            return null;
        }

        /// <summary>Parent row text >= 14 DIP, child one step smaller at >= 13.</summary>
        private static void TodoListTextIsLarger()
        {
            var data = NewTodoData(out TodoItem item);
            TodoListOps.AddChild(item, "Whole milk");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var panel = view.FindName("ItemsPanel") as Panel;
            if (panel == null || panel.Children.Count < 2)
            {
                throw new InvalidOperationException("day list rows not built");
            }

            double parentFont = FirstFontSize(panel.Children[0]);
            double childFont = FirstFontSize(panel.Children[1]);
            if (parentFont < 14)
                throw new InvalidOperationException("parent font " + parentFont + " < 14");
            if (childFont < 13)
                throw new InvalidOperationException("child font " + childFont + " < 13");
            if (childFont >= parentFont)
                throw new InvalidOperationException("child font " + childFont + " is not smaller than parent " + parentFont);
        }

        /// <summary>
        /// Task A4.1: the calendar's per-cell height follows the panel height
        /// through the engine - the unpersisted fallback keeps the migration
        /// 28, a 385-high panel reaches the 36 clamp, and the 337-high minimum
        /// with a six-week month and seven items squeezes the cells to 22 with
        /// the list at its single scrolling row.
        /// </summary>
        private static void TodoCalendarCellHeightTracksThePanel()
        {
            ThemeManager.Initialize("dark");

            // No persisted size, empty day: the fallback (the old look).
            var fallback = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(fallback);
            AssertCalendarCellHeight(fallback, 28, "the unpersisted fallback");

            // 342x385, no items: content 343 -> (343 - 133) / 5 = 42 -> 36.
            var tallData = new TodoData
            {
                PanelWidth = 342,
                PanelHeight = 385,
                SelectedDate = new DateTime(2026, 10, 12)
            };
            var tall = new TodoWidget(tallData).CalendarView;
            LayoutView(tall);
            AssertCalendarCellHeight(tall, 36, "the 385-high panel");

            // 342x337, seven items on 2026-08-12 (a six-week month): content
            // 295 -> (295 - 133 - 30) / 6 = 22, the list keeps one row. The
            // items are completed on 8/12 so they display there (open
            // past-dated entries carry to today).
            var minData = NewSevenRowAugustDay();
            var min = new TodoWidget(minData).CalendarView;
            LayoutView(min);
            AssertCalendarCellHeight(min, 22, "the minimum panel");

            var scroller = min.FindName("ItemsScroller") as FrameworkElement;
            if (scroller == null || Math.Abs(scroller.Height - 30) > 0.01)
            {
                throw new InvalidOperationException(
                    "the minimum panel's list is not one row: " +
                    (scroller == null ? "ItemsScroller missing" : scroller.Height.ToString("0.##")));
            }
        }

        private static void AssertCalendarCellHeight(TodoCalendarView view, double wanted, string label)
        {
            var dayGrid = view.FindName("DayGrid") as UniformGrid;
            if (dayGrid == null || dayGrid.Children.Count == 0 || dayGrid.Children.Count % 7 != 0)
            {
                throw new InvalidOperationException(label + ": day grid not built as whole weeks");
            }

            double cellHeight = dayGrid.Height / (dayGrid.Children.Count / 7.0);
            if (Math.Abs(cellHeight - wanted) > 0.01)
            {
                throw new InvalidOperationException(
                    label + ": day cell height " + cellHeight.ToString("0.##") + ", wanted " + wanted);
            }
        }

        /// <summary>
        /// An open item editor commits its typed text when keyboard focus is
        /// lost - the deferred path the click-away flow relies on.
        /// </summary>
        private static void TodoEditorCommitsOnFocusLoss()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "Buy milk (edited)";
            RaiseLostFocus(box);

            if (data.Items[0].Text != "Buy milk")
                throw new InvalidOperationException("blur committed synchronously instead of deferring");

            RunPendingEditorCommit(view);
            if (data.Items.Count != 1 || data.Items[0].Text != "Buy milk (edited)")
            {
                throw new InvalidOperationException("deferred blur commit did not save: '" + data.Items[0].Text + "'");
            }
            if (EditorBoxInListOrNull(view) != null)
                throw new InvalidOperationException("editor still open after blur");
        }

        /// <summary>Empty text committed by focus loss deletes the item (spec rule).</summary>
        private static void TodoEmptyEditorDeletesOnFocusLoss()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "   ";
            RaiseLostFocus(box);
            if (data.Items.Count != 1)
                throw new InvalidOperationException("blur deleted synchronously instead of deferring");

            RunPendingEditorCommit(view);
            if (data.Items.Count != 0)
                throw new InvalidOperationException("empty blur kept " + data.Items.Count + " item(s)");
        }

        /// <summary>Esc cancels; the focus loss that follows must not commit anything.</summary>
        private static void TodoCanceledEditorDoesNotCommitOnFocusLoss()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "Changed";
            RaiseKey(widget, view, box, Key.Escape);
            RaiseLostFocus(box);

            if (RunPendingEditorCommitIfAny(view))
                throw new InvalidOperationException("a canceled editor scheduled a commit");
            if (data.Items.Count != 1 || data.Items[0].Text != "Buy milk")
            {
                throw new InvalidOperationException("canceled edit was committed: '" + data.Items[0].Text + "'");
            }
        }

        /// <summary>A non-empty adder commits once on blur and closes.</summary>
        private static void TodoAdderBlurCommitsOnceAndCloses()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            InvokeViewEdit(view, "StartChildAdd", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "From blur";
            RaiseLostFocus(box);
            RunPendingEditorCommit(view);

            if (item.Children.Count != 1 || item.Children[0].Text != "From blur")
            {
                throw new InvalidOperationException("adder blur added " + item.Children.Count + " child(ren)");
            }
            if (EditorBoxInListOrNull(view) != null)
                throw new InvalidOperationException("adder still open after blur");
        }

        /// <summary>Enter adds and refreshes the adder; the stale blur must not add again.</summary>
        private static void TodoAdderEnterThenBlurAddsOnce()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartChildAdd", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "Once";
            RaiseKey(widget, view, box, Key.Enter);
            RaiseLostFocus(box);

            if (RunPendingEditorCommitIfAny(view))
                throw new InvalidOperationException("a settled adder scheduled a second commit");
            if (item.Children.Count != 1)
                throw new InvalidOperationException("child added " + item.Children.Count + " times");
        }

        /// <summary>Empty adder text on blur just closes the adder.</summary>
        private static void TodoAdderEmptyBlurClosesWithoutAdding()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            InvokeViewEdit(view, "StartChildAdd", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = string.Empty;
            RaiseLostFocus(box);
            RunPendingEditorCommit(view);

            if (item.Children.Count != 0)
                throw new InvalidOperationException("empty adder blur added a child");
            if (EditorBoxInListOrNull(view) != null)
                throw new InvalidOperationException("adder still open after empty blur");
        }

        /// <summary>
        /// Smoke fix C1 + B2: Enter in a parent editor commits the text; a
        /// childless parent then opens its child adder in the same state change
        /// (a parent with children chains to the first child's editor instead -
        /// the B2 pin), while the blur commit stays a plain commit and never
        /// opens the adder.
        /// </summary>
        private static void TodoParentEditorEnterOpensChildAdder()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "Buy milk (entered)";
            RaiseKey(widget, view, box, Key.Enter);

            if (data.Items.Count != 1 || data.Items[0].Text != "Buy milk (entered)")
            {
                throw new InvalidOperationException(
                    "the parent Enter did not commit: '" +
                    (data.Items.Count == 0 ? "<deleted>" : data.Items[0].Text) + "'");
            }
            if (EditorBoxInListOrNull(view) == null)
                throw new InvalidOperationException("no child adder opened after the parent Enter");
            string adderFor = PrivateField<string>(view, "_addingChildToId");
            if (adderFor != item.Id)
            {
                throw new InvalidOperationException(
                    "the adder targets '" + (adderFor ?? "<null>") + "', wanted '" + item.Id + "'");
            }
            if (RunPendingEditorCommitIfAny(view))
                throw new InvalidOperationException("the settled parent Enter scheduled a second commit");

            // The blur path must not open the adder: a fresh editor's focus
            // loss still commits the text, nothing else.
            var blurData = NewTodoData(out TodoItem blurItem);
            var blurView = new TodoWidget(blurData).CalendarView;
            InvokeViewEdit(blurView, "StartItemEdit", blurItem.Id);
            LayoutView(blurView);
            var blurBox = EditorBoxInList(blurView);
            blurBox.Text = "Buy milk (blurred)";
            RaiseLostFocus(blurBox);
            RunPendingEditorCommit(blurView);

            if (blurData.Items.Count != 1 || blurData.Items[0].Text != "Buy milk (blurred)")
                throw new InvalidOperationException("the blur commit did not save the edited text");
            if (PrivateField<string>(blurView, "_addingChildToId") != null)
                throw new InvalidOperationException("the blur commit opened a child adder");

            // An empty-name Enter deletes the item and opens no adder either.
            var deleteData = NewTodoData(out TodoItem deleteItem);
            var deleteWidget = new TodoWidget(deleteData);
            var deleteView = deleteWidget.CalendarView;
            InvokeViewEdit(deleteView, "StartItemEdit", deleteItem.Id);
            LayoutView(deleteView);
            var deleteBox = EditorBoxInList(deleteView);
            deleteBox.Text = "   ";
            RaiseKey(deleteWidget, deleteView, deleteBox, Key.Enter);

            if (deleteData.Items.Count != 0)
                throw new InvalidOperationException(
                    "the empty-name parent Enter kept " + deleteData.Items.Count + " item(s)");
            if (PrivateField<string>(deleteView, "_addingChildToId") != null)
                throw new InvalidOperationException("the empty-name parent Enter opened a child adder");
            if (RunPendingEditorCommitIfAny(deleteView))
                throw new InvalidOperationException("the deleted parent scheduled a stale commit");
        }

        /// <summary>
        /// Polish round (T1): the inline item editor's caret wears the widget
        /// colour, so the insertion point reads at a glance instead of fading
        /// into the pale theme text (high contrast keeps the system ink).
        /// </summary>
        private static void TodoItemEditorCaretFollowsTheWidgetColor()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            var brush = box.CaretBrush as SolidColorBrush;
            Color wanted = Utils.HexToColor(data.Color);
            if (brush == null || brush.Color != wanted)
            {
                throw new InvalidOperationException(
                    "the item editor caret is " +
                    (brush == null ? "no solid brush" : Describe(brush.Color)) +
                    ", wanted the widget colour " + Describe(wanted));
            }
        }

        /// <summary>Same caret rule for the bottom add box (polish round T1).</summary>
        private static void TodoAddBoxCaretFollowsTheWidgetColor()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;

            var addBox = view.FindName("AddBox") as TextBox;
            if (addBox == null) throw new InvalidOperationException("AddBox not found");
            var brush = addBox.CaretBrush as SolidColorBrush;
            Color wanted = Utils.HexToColor(data.Color);
            if (brush == null || brush.Color != wanted)
            {
                throw new InvalidOperationException(
                    "the add box caret is " +
                    (brush == null ? "no solid brush" : Describe(brush.Color)) +
                    ", wanted the widget colour " + Describe(wanted));
            }
        }

        /// <summary>
        /// Polish round (T3): Backspace with the caret at the text start is an
        /// entry action, not a text edit - an open entry completes first (the
        /// normal tick semantics) and its editor stays open for the second
        /// press, exactly the way the checkbox tick keeps the row.
        /// </summary>
        private static void TodoBackspaceAtStartCompletesAnOpenItem()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back);

            if (!item.Done)
                throw new InvalidOperationException(
                    "Backspace at the text start did not complete the open item");
            EditorBoxInList(view); // still open for the second press
        }

        /// <summary>
        /// Polish round (T3): the second press at the text start deletes the
        /// completed entry (the whole parent subtree rides along, here none).
        /// </summary>
        private static void TodoBackspaceAgainDeletesTheCompletedItem()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back); // completes

            box = EditorBoxInList(view);
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back); // deletes

            if (data.Items.IndexOf(item) >= 0)
                throw new InvalidOperationException("the second Backspace kept the completed item in the list");
        }

        /// <summary>
        /// Polish round (T3): the ladder's delete lands in the one-step undo
        /// slot - the restore puts the entry back at its original index with
        /// its text untouched.
        /// </summary>
        private static void TodoUndoRestoresTheLastDeletedItem()
        {
            var data = NewTodoData(out TodoItem item); // stays at index 0
            var second = TodoListOps.AddItem(data, "Second", data.SelectedDate);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", second.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back); // completes

            box = EditorBoxInList(view);
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back); // deletes

            if (data.Items.Count != 1 || data.Items[0] != item)
            {
                throw new InvalidOperationException(
                    "the ladder did not delete the completed item (" + data.Items.Count + " left)");
            }
            if (!InvokeTryUndo(view))
                throw new InvalidOperationException("TryUndo reported nothing to restore");
            if (data.Items.Count != 2 || !ReferenceEquals(data.Items[1], second) || second.Text != "Second")
            {
                throw new InvalidOperationException(
                    "the undo did not put the deleted item back at its index with its text");
            }
        }

        /// <summary>
        /// Polish round (T3): the child ladder mirrors the parent - the first
        /// Backspace completes the sub-todo, the second deletes it from its
        /// parent, and the undo restores its slot.
        /// </summary>
        private static void TodoChildLadderCompletesThenDeletes()
        {
            var data = NewTodoData(out TodoItem item);
            var child = TodoListOps.AddChild(item, "Buy eggs");
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartChildEdit", child.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back);

            if (!child.Done)
                throw new InvalidOperationException(
                    "Backspace at the text start did not complete the open child");

            box = EditorBoxInList(view);
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back);

            if (item.Children.IndexOf(child) >= 0)
                throw new InvalidOperationException("the second Backspace kept the completed child");

            if (!InvokeTryUndo(view))
                throw new InvalidOperationException("TryUndo reported nothing to restore");
            if (item.Children.Count != 1 || !ReferenceEquals(item.Children[0], child))
                throw new InvalidOperationException("the undo did not put the deleted child back in its slot");
        }

        /// <summary>
        /// Polish round (T3): a Backspace away from the text start (or with a
        /// selection) stays a plain text edit - no completion, no deletion.
        /// </summary>
        private static void TodoBackspaceMidTextStaysPlainEditing()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.CaretIndex = 2;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back);

            if (data.Items.IndexOf(item) < 0)
                throw new InvalidOperationException("a mid-text Backspace removed the entry");
            if (item.Done)
                throw new InvalidOperationException("a mid-text Backspace completed the entry");
        }

        /// <summary>
        /// Polish round (T3 fix): the ladder commits the editor's live text
        /// before completing - typing in the box and pressing Backspace at the
        /// start must keep the edit (not toggle the stale stored text).
        /// </summary>
        private static void TodoLadderCommitsTheBoxTextBeforeCompleting()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "edited";
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back);

            if (item.Text != "edited")
                throw new InvalidOperationException(
                    "the ladder completed with '" + item.Text + "' instead of the box's live text");
            if (!item.Done)
                throw new InvalidOperationException("the ladder did not complete the item");
            EditorBoxInList(view); // still open
        }

        /// <summary>
        /// Polish round (T3 fix): an emptied box deletes on the ladder press -
        /// the same rule as the empty blur commit - and the undo restores the
        /// entry at its place with its original text.
        /// </summary>
        private static void TodoLadderDeletesWhenTheBoxWasEmptied()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = string.Empty;
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back);

            if (data.Items.IndexOf(item) >= 0)
                throw new InvalidOperationException("the emptied box kept the item on the ladder press");

            if (!InvokeTryUndo(view))
                throw new InvalidOperationException("TryUndo reported nothing to restore");
            if (data.Items.Count != 1 || !ReferenceEquals(data.Items[0], item) || item.Text != "Buy milk")
            {
                throw new InvalidOperationException(
                    "the undo did not restore the emptied-box deletion with its original text");
            }
        }

        /// <summary>
        /// Polish round (T3 fix): the completion clears the session's click
        /// point before the rebuild, so the rebuilt editor keeps its default
        /// caret position instead of being re-anchored to the old click.
        /// </summary>
        private static void TodoLadderClearsTheClickAnchorBeforeTheRebuild()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id); // carries a click point
            LayoutView(view);

            if (!PrivateField<Point?>(view, "_editorCaretAt").HasValue)
                throw new InvalidOperationException("the session did not start with a click point");

            var box = EditorBoxInList(view);
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back);

            if (PrivateField<Point?>(view, "_editorCaretAt").HasValue)
                throw new InvalidOperationException("the completed ladder left the old click anchor in place");
        }

        /// <summary>
        /// B2 (Enter chain, user 2026-10-13; was smoke fix C2): an empty Enter
        /// in a child adder opens the next day item's text editor in display
        /// order; on the last parent it jumps to the bottom add-new input
        /// (revealed, adder state cleared) instead of closing. No child is
        /// ever added by the empty Enter.
        /// </summary>
        private static void TodoAdderEmptyEnterOpensTheNextParentEditor()
        {
            var data = NewTodoData(out TodoItem first);
            var second = TodoListOps.AddItem(data, "Second", data.SelectedDate);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;

            InvokeViewEdit(view, "StartChildAdd", first.Id);
            LayoutView(view);
            var box = EditorBoxInList(view);
            box.Text = string.Empty;
            RaiseKey(widget, view, box, Key.Enter);

            string editingItem = PrivateField<string>(view, "_editingItemId");
            if (editingItem != second.Id)
            {
                throw new InvalidOperationException(
                    "the empty Enter left the editor on item '" + (editingItem ?? "<null>") +
                    "', wanted the next parent's editor '" + second.Id + "'");
            }
            if (PrivateField<string>(view, "_editingChildId") != null)
                throw new InvalidOperationException("the empty Enter left a child editor open");
            if (PrivateField<string>(view, "_addingChildToId") != null)
                throw new InvalidOperationException("the empty Enter left the adder open");
            if (first.Children != null && first.Children.Count != 0)
                throw new InvalidOperationException("the empty Enter added a child to the first item");
            if (EditorBoxInListOrNull(view) == null)
                throw new InvalidOperationException("the next parent's editor did not open");
            if (RunPendingEditorCommitIfAny(view))
                throw new InvalidOperationException("the chain move scheduled a stale commit");

            // The chain reaches the last parent's adder through its editor
            // (Enter commits and, childless, opens that adder); the adder's
            // empty Enter keeps the rule-4 jump to the bottom input.
            var nextBox = EditorBoxInList(view);
            RaiseKey(widget, view, nextBox, Key.Enter);
            if (PrivateField<string>(view, "_addingChildToId") != second.Id)
                throw new InvalidOperationException("the next parent's Enter did not open its child adder");
            if (RunPendingEditorCommitIfAny(view))
                throw new InvalidOperationException("the next parent's Enter scheduled a stale commit");

            var adderBox = EditorBoxInList(view);
            adderBox.Text = string.Empty;
            RaiseKey(widget, view, adderBox, Key.Enter);

            if (EditorBoxInListOrNull(view) != null)
                throw new InvalidOperationException("the last parent's adder stayed open after the empty Enter");
            if (PrivateField<string>(view, "_addingChildToId") != null)
                throw new InvalidOperationException("the adder id survived the last-parent empty Enter");
            if (!PrivateField<bool>(view, "_addBoxRevealed"))
            {
                throw new InvalidOperationException(
                    "the last-parent empty Enter did not reveal the bottom add input");
            }
            var addHost = view.FindName("AddInputHost") as FrameworkElement;
            if (addHost == null || addHost.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException(
                    "the bottom add input is not visible after the last-parent empty Enter");
            }
            if (RunPendingEditorCommitIfAny(view))
                throw new InvalidOperationException("the last-parent jump scheduled a stale commit");
        }

        /// <summary>
        /// B2 (Enter chain, user 2026-10-13): a parent editor's Enter commits
        /// and hands the caret to the first child's editor (select-all), not
        /// the adder; the adder stays the childless fallback.
        /// </summary>
        private static void TodoParentEnterOpensTheFirstChildsEditor()
        {
            var data = NewTodoData(out TodoItem item);
            var first = TodoListOps.AddChild(item, "Buy eggs");
            TodoListOps.AddChild(item, "Buy bread");
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "Buy milk (entered)";
            RaiseKey(widget, view, box, Key.Enter);

            if (item.Text != "Buy milk (entered)")
                throw new InvalidOperationException("the parent Enter did not commit the text");
            if (PrivateField<string>(view, "_editingChildId") != first.Id)
                throw new InvalidOperationException("the parent Enter did not open the first child's editor");
            if (PrivateField<string>(view, "_editingItemId") != null)
                throw new InvalidOperationException("the parent editor stayed open after its Enter");
            if (PrivateField<string>(view, "_addingChildToId") != null)
                throw new InvalidOperationException("the parent Enter opened the adder despite children");
            EditorBoxInList(view); // the child editor box is really up
            if (RunPendingEditorCommitIfAny(view))
                throw new InvalidOperationException("the chain move scheduled a stale commit");
        }

        /// <summary>
        /// B2 (Enter chain, user 2026-10-13): a child editor's Enter commits
        /// and moves to the next sibling's editor; the adder only opens after
        /// the last sibling.
        /// </summary>
        private static void TodoChildEnterWalksToTheNextSibling()
        {
            var data = NewTodoData(out TodoItem item);
            var first = TodoListOps.AddChild(item, "Buy eggs");
            var second = TodoListOps.AddChild(item, "Buy bread");
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartChildEdit", first.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "Buy eggs (entered)";
            RaiseKey(widget, view, box, Key.Enter);

            if (first.Text != "Buy eggs (entered)")
                throw new InvalidOperationException("the child Enter did not commit the text");
            if (PrivateField<string>(view, "_editingChildId") != second.Id)
                throw new InvalidOperationException("the child Enter did not walk to the next sibling");
            if (PrivateField<string>(view, "_addingChildToId") != null)
                throw new InvalidOperationException("the child Enter opened the adder with a sibling left");
            EditorBoxInList(view);
        }

        /// <summary>B2: the last sibling's Enter opens the parent's child adder.</summary>
        private static void TodoChildEnterAtTheLastOneOpensTheAdder()
        {
            var data = NewTodoData(out TodoItem item);
            TodoListOps.AddChild(item, "Buy eggs");
            var last = TodoListOps.AddChild(item, "Buy bread");
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartChildEdit", last.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "Buy bread (entered)";
            RaiseKey(widget, view, box, Key.Enter);

            if (last.Text != "Buy bread (entered)")
                throw new InvalidOperationException("the last child's Enter did not commit the text");
            if (PrivateField<string>(view, "_editingChildId") != null)
                throw new InvalidOperationException("the last child's Enter kept a child editor open");
            if (PrivateField<string>(view, "_addingChildToId") != item.Id)
                throw new InvalidOperationException("the last child's Enter did not open the parent's adder");
            EditorBoxInList(view);
        }

        /// <summary>
        /// B2 (rule 5): an emptied child box deletes on Enter and never walks
        /// the chain (the blur path's rule, now shared by Enter).
        /// </summary>
        private static void TodoChildEnterOnAnEmptiedBoxDeletesWithoutWalking()
        {
            var data = NewTodoData(out TodoItem item);
            var first = TodoListOps.AddChild(item, "Buy eggs");
            TodoListOps.AddChild(item, "Buy bread");
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartChildEdit", first.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = string.Empty;
            RaiseKey(widget, view, box, Key.Enter);

            if (item.Children.IndexOf(first) >= 0)
                throw new InvalidOperationException("the emptied-box Enter kept the child");
            if (PrivateField<string>(view, "_editingChildId") != null)
                throw new InvalidOperationException("the emptied-box Enter opened a child editor");
            if (PrivateField<string>(view, "_addingChildToId") != null)
                throw new InvalidOperationException("the emptied-box Enter advanced to the adder");
            if (PrivateField<string>(view, "_editingItemId") != null)
                throw new InvalidOperationException("the emptied-box Enter advanced to a parent editor");
        }

        /// <summary>
        /// B2: every chain-opened editor carries the adder's select-all default
        /// (the session was opened by the keyboard, not by a click).
        /// </summary>
        private static void TodoChainOpenedEditorsSelectAll()
        {
            var data = NewTodoData(out TodoItem item);
            TodoListOps.AddChild(item, "Buy eggs");
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "Buy milk (entered)";
            RaiseKey(widget, view, box, Key.Enter);
            LayoutView(view);

            var chainBox = EditorBoxInList(view);
            InvokeApplyEditorCaret(view, chainBox); // the editor's Loaded half, run offscreen
            if (chainBox.CaretIndex != 0 || chainBox.SelectionLength != chainBox.Text.Length ||
                chainBox.Text.Length == 0)
            {
                throw new InvalidOperationException(
                    "the chain-opened editor carries caret " + chainBox.CaretIndex +
                    "/selection " + chainBox.SelectionLength + " of " + chainBox.Text.Length +
                    " characters, wanted the select-all default");
            }
        }

        /// <summary>
        /// Smoke fix C (final spec rule 5): Enter in the bottom add box adds
        /// the parent and hands the caret to the new item's child adder - the
        /// add box no longer keeps focus (spec change). The offscreen harness
        /// cannot observe keyboard focus reliably, so the pin asserts the
        /// state (adder targeted at the new item, editor present, add box
        /// cleared, no pending commit) and the reveal contract only.
        /// </summary>
        private static void TodoAddRowEnterOpensTheNewItemsAdder()
        {
            ThemeManager.Initialize("dark");
            var data = new TodoData { SelectedDate = DateTime.Today };
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            LayoutView(view);
            RevealAddBox(view);

            var addBox = view.FindName("AddBox") as TextBox;
            if (addBox == null) throw new InvalidOperationException("AddBox not found");
            addBox.Text = "New parent";
            RaiseKey(widget, view, addBox, Key.Enter);

            if (data.Items.Count != 1 || data.Items[0].Text != "New parent")
            {
                throw new InvalidOperationException(
                    "the add-row Enter added " + data.Items.Count + " item(s): " +
                    (data.Items.Count == 0 ? "<none>" : "'" + data.Items[0].Text + "'"));
            }
            TodoItem added = data.Items[0];
            string adderFor = PrivateField<string>(view, "_addingChildToId");
            if (adderFor != added.Id)
            {
                throw new InvalidOperationException(
                    "the add-row Enter left the adder on '" + (adderFor ?? "<null>") +
                    "', wanted the new item '" + added.Id + "'");
            }
            if (EditorBoxInListOrNull(view) == null)
                throw new InvalidOperationException("the new item's child adder did not open");
            if (addBox.Text.Length != 0)
                throw new InvalidOperationException("the add box kept its text: '" + addBox.Text + "'");
            if (RunPendingEditorCommitIfAny(view))
                throw new InvalidOperationException("the add-row Enter scheduled a stale commit");
        }

        /// <summary>
        /// One item, one 30px row: the scroller must not reserve a second row
        /// and the collapsed pill must follow the list bottom at the pill's
        /// half-leading (ADD_GAP; fix round 18's top margin).
        /// </summary>
        private static void TodoPillFollowsASingleItem()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var scroller = view.FindName("ItemsScroller") as FrameworkElement;
            var pill = view.FindName("AddPill") as FrameworkElement;
            if (scroller == null || pill == null)
                throw new InvalidOperationException("ItemsScroller / AddPill not found");
            if (Math.Abs(scroller.Height - 30) > 0.01)
                throw new InvalidOperationException("single-item scroller " + scroller.Height + " != 30 (one row)");

            double pillTop = pill.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            double scrollerBottom = scroller.TransformToAncestor(view)
                .Transform(new Point(0, scroller.ActualHeight)).Y;
            if (Math.Abs(pillTop - scrollerBottom - ADD_GAP) > 1.0)
            {
                throw new InvalidOperationException(
                    "gap between list bottom and pill: " + (pillTop - scrollerBottom).ToString("0.##") +
                    ", wanted the pill's half-leading " + ADD_GAP);
            }
        }

        /// <summary>
        /// Fix round 12 (R1): an empty day renders no message at all and the
        /// list reserves no row - the collapsed pill hangs ADD_GAP below the
        /// day header (the same half-leading; fix round 18).
        /// </summary>
        private static void TodoEmptyDayShowsNoMessage()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);

            var scroller = view.FindName("ItemsScroller") as FrameworkElement;
            if (scroller == null) throw new InvalidOperationException("ItemsScroller not found");
            if (Math.Abs(scroller.Height) > 0.01)
                throw new InvalidOperationException(
                    "empty-day scroller " + scroller.Height.ToString("0.##") + " != 0 (no row)");
            if (ContainsText(view, Localization.Get("Todo_EmptyDay")))
                throw new InvalidOperationException("the empty-day message is still rendered");

            var pill = view.FindName("AddPill") as FrameworkElement;
            var header = view.FindName("DayHeaderText") as FrameworkElement;
            var headerRow = header == null ? null : header.Parent as FrameworkElement;
            if (pill == null || headerRow == null)
                throw new InvalidOperationException("AddPill / the day header row not found");
            if (pill.Visibility != Visibility.Visible)
                throw new InvalidOperationException("the pill is " + pill.Visibility + " on the collapsed empty day");

            double pillTop = pill.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            double headerBottom = headerRow.TransformToAncestor(view)
                .Transform(new Point(0, headerRow.ActualHeight)).Y;
            if (Math.Abs(pillTop - headerBottom - ADD_GAP) > 1.0)
            {
                throw new InvalidOperationException(
                    "the pill is not " + ADD_GAP + " below the day header's bottom: pill top " + pillTop.ToString("0.##") +
                    " vs header bottom " + headerBottom.ToString("0.##"));
            }
        }

        /// <summary>More than six rows still clamp to six rows of 30px.</summary>
        private static void TodoOverflowingListKeepsSixRows()
        {
            var data = NewTodoData(out TodoItem item);
            for (int i = 0; i < 7; i++) TodoListOps.AddItem(data, "Item " + i, DateTime.Today);
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var scroller = view.FindName("ItemsScroller") as FrameworkElement;
            if (scroller == null) throw new InvalidOperationException("ItemsScroller not found");
            if (Math.Abs(scroller.Height - 180) > 0.01)
                throw new InvalidOperationException("overflowing scroller " + scroller.Height + " != 180 (six rows)");
        }

        /// <summary>
        /// Parent menu: exactly [Todo_AddChild, Menu_DeleteItem]; child menu:
        /// exactly [Menu_DeleteItem] - headers resolved through Localization.
        /// </summary>
        private static void TodoRowMenusHoldExpectedActions()
        {
            var data = NewTodoData(out TodoItem item);
            TodoListOps.AddChild(item, "Whole milk");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var parentMenu = RowAt(view, 0).ContextMenu;
            var childMenu = RowAt(view, 1).ContextMenu;
            if (parentMenu == null || childMenu == null)
                throw new InvalidOperationException("a row has no context menu");

            if (parentMenu.Items.Count != 2)
                throw new InvalidOperationException("parent menu has " + parentMenu.Items.Count + " item(s)");
            string addChild = HeaderOf(parentMenu.Items[0]);
            string delete = HeaderOf(parentMenu.Items[1]);
            if (addChild != Localization.Get("Todo_AddChild") || delete != Localization.Get("Menu_DeleteItem"))
                throw new InvalidOperationException("parent menu headers: '" + addChild + "', '" + delete + "'");

            if (childMenu.Items.Count != 1)
                throw new InvalidOperationException("child menu has " + childMenu.Items.Count + " item(s)");
            string childDelete = HeaderOf(childMenu.Items[0]);
            if (childDelete != Localization.Get("Menu_DeleteItem"))
                throw new InvalidOperationException("child menu header: '" + childDelete + "'");
        }

        /// <summary>Invoking the menu entries runs the row actions on the data.</summary>
        private static void TodoRowMenusActOnTheirRow()
        {
            var data = NewTodoData(out TodoItem item);
            TodoListOps.AddChild(item, "Whole milk");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var parentRow = RowAt(view, 0);
            var childRow = RowAt(view, 1);

            ClickMenuItem(parentRow, 0); // 添加子项 -> inline adder
            if (FirstDescendant<TextBox>(view.FindName("ItemsPanel") as DependencyObject) == null)
                throw new InvalidOperationException("Todo_AddChild did not open the inline child adder");

            ClickMenuItem(childRow, 0); // delete the child
            if (item.Children.Count != 0)
                throw new InvalidOperationException("child delete left " + item.Children.Count + " child(ren)");

            ClickMenuItem(parentRow, 1); // delete the parent
            if (data.Items.Count != 0)
                throw new InvalidOperationException("parent delete left " + data.Items.Count + " item(s)");
        }

        /// <summary>Rows keep only the checkbox and the text (no hover ＋/×).</summary>
        private static void TodoRowsCarryNoHoverButtons()
        {
            var data = NewTodoData(out TodoItem item);
            TodoListOps.AddChild(item, "Whole milk");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var parentRow = RowAt(view, 0);
            var childRow = RowAt(view, 1);
            if (RowContent(parentRow).Children.Count != 2)
            {
                throw new InvalidOperationException(
                    "parent row has " + RowContent(parentRow).Children.Count + " children (expected check + text)");
            }
            if (RowContent(childRow).Children.Count != 2)
            {
                throw new InvalidOperationException(
                    "child row has " + RowContent(childRow).Children.Count + " children (expected check + text)");
            }
            if (HasGlyph(parentRow, "＋") || HasGlyph(parentRow, "×") ||
                HasGlyph(childRow, "＋") || HasGlyph(childRow, "×"))
            {
                throw new InvalidOperationException("a row still carries the hover ＋/× glyphs");
            }
        }

        /// <summary>
        /// T4: the day-area menu (empty list/calendar space) holds exactly the
        /// day-scoped clear entry.
        /// </summary>
        private static void TodoDayAreaMenuHoldsTheClearEntry()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var menu = InvokeBuildDayAreaMenu(view);
            if (menu.Items.Count != 1)
                throw new InvalidOperationException("the day-area menu has " + menu.Items.Count + " item(s)");
            string header = HeaderOf(menu.Items[0]);
            if (header != Localization.Get("Menu_ClearCompleted"))
                throw new InvalidOperationException("the day-area menu header is '" + header + "'");
        }

        /// <summary>
        /// T4: the clear entry is greyed out while the selected day has no
        /// completed row and enabled as soon as one exists (the menu is rebuilt
        /// per right-click, so every build sees the current day).
        /// </summary>
        private static void TodoClearEntryEnablesOnlyWithCompletedRows()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var emptyEntry = InvokeBuildDayAreaMenu(view).Items[0] as MenuItem;
            if (emptyEntry == null) throw new InvalidOperationException("the clear entry is not a MenuItem");
            if (emptyEntry.IsEnabled)
            {
                throw new InvalidOperationException(
                    "the clear entry is enabled on a day without completed rows");
            }

            TodoRollover.Toggle(item, true, DateTime.Today); // a row completed today
            var filledEntry = InvokeBuildDayAreaMenu(view).Items[0] as MenuItem;
            if (filledEntry == null || !filledEntry.IsEnabled)
            {
                throw new InvalidOperationException(
                    "the clear entry stays disabled with a completed row on the day");
            }
        }

        /// <summary>
        /// T4: clicking the clear entry removes the selected day's completed
        /// rows as one batch - a completed parent goes whole (subtree riding
        /// along), a completed sub-todo of an open parent goes alone, an open
        /// parent stays - and the undo restores the whole batch in place.
        /// </summary>
        private static void TodoClickingClearRemovesTheDaysCompletedRows()
        {
            DateTime today = DateTime.Today;
            var data = new TodoData { SelectedDate = today };
            var doneParent = TodoListOps.AddItem(data, "Done parent", today);
            var doneChild = TodoListOps.AddChild(doneParent, "Done child of the completed parent");
            TodoRollover.Toggle(doneParent, true, today); // the parent and its subtree
            var openParent = TodoListOps.AddItem(data, "Open parent", today);
            var openDoneChild = TodoListOps.AddChild(openParent, "Done child of the open parent");
            TodoRollover.ToggleChild(openDoneChild, true);
            var plainParent = TodoListOps.AddItem(data, "Plain parent", today);

            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var entry = InvokeBuildDayAreaMenu(view).Items[0] as MenuItem;
            if (entry == null || !entry.IsEnabled)
                throw new InvalidOperationException("the clear entry is not enabled with completed rows on the day");
            entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            if (data.Items.IndexOf(doneParent) >= 0)
                throw new InvalidOperationException("the completed parent survived the clear");
            if (openParent.Children.IndexOf(openDoneChild) >= 0)
                throw new InvalidOperationException("the completed sub-todo survived the clear");
            if (data.Items.IndexOf(openParent) < 0 || data.Items.IndexOf(plainParent) < 0)
                throw new InvalidOperationException("the clear removed an open entry");
            if (openParent.Children.Count != 0)
                throw new InvalidOperationException("the clear left a completed sub-todo behind");

            if (!InvokeTryUndo(view))
                throw new InvalidOperationException("TryUndo reported nothing to restore");
            if (data.Items.Count != 3 || data.Items[0] != doneParent ||
                data.Items[1] != openParent || data.Items[2] != plainParent)
            {
                throw new InvalidOperationException("the undo did not restore the parent order");
            }
            if (openParent.Children.Count != 1 || openParent.Children[0] != openDoneChild || !openDoneChild.Done)
                throw new InvalidOperationException("the undo did not restore the completed sub-todo");
        }

        /// <summary>
        /// B3: the undo stack is multi-step - a removal, a parent tick and a
        /// child tick walk back newest-first, each press restoring exactly one
        /// edit; the empty stack reports false.
        /// </summary>
        private static void TodoUndoWalksBackThroughSeveralEdits()
        {
            var data = NewTodoData(out TodoItem item); // A
            var b = TodoListOps.AddItem(data, "Second", data.SelectedDate);
            var d = TodoListOps.AddItem(data, "Third", data.SelectedDate);
            var c = TodoListOps.AddChild(d, "Nested");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            InvokeViewActionWith(view, "RemoveItem", item);      // 1: delete A
            InvokeViewActionWith(view, "ApplyItemToggle", b, true);  // 2: tick B
            InvokeViewActionWith(view, "ApplyChildToggle", c, true); // 3: tick C

            if (!c.Done || !b.Done) throw new InvalidOperationException("the fixture toggles did not stick");
            if (data.Items.IndexOf(item) >= 0) throw new InvalidOperationException("the fixture removal did not stick");

            if (!InvokeTryUndo(view)) throw new InvalidOperationException("the child tick did not undo");
            if (c.Done || !b.Done) throw new InvalidOperationException("the first undo restored the wrong edit");
            if (!InvokeTryUndo(view)) throw new InvalidOperationException("the parent tick did not undo");
            if (b.Done) throw new InvalidOperationException("the second undo restored the wrong edit");
            if (!InvokeTryUndo(view)) throw new InvalidOperationException("the removal did not undo");
            if (data.Items.IndexOf(item) != 0)
                throw new InvalidOperationException("the removal undo did not put the entry back at its index");
            if (data.Items.Count != 3 || data.Items[0] != item || data.Items[1] != b || data.Items[2] != d)
                throw new InvalidOperationException("the removal undo did not restore the order");
            if (InvokeTryUndo(view))
                throw new InvalidOperationException("the drained stack still reported an undo");
        }

        /// <summary>
        /// B3: the stack is capped at twenty - the oldest edits fall off, so
        /// exactly twenty undos succeed and the twenty-first reports false.
        /// The stack size is read reflectively (the entry type is an
        /// implementation detail; the cap is what matters here).
        /// </summary>
        private static void TodoUndoStackKeepsOnlyTheNewestTwenty()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            for (int i = 0; i < 21; i++)
                InvokeViewActionWith(view, "ApplyItemToggle", item, i % 2 == 0);

            int count = UndoStackCount(view);
            if (count != 20)
                throw new InvalidOperationException("the undo stack kept " + count + " entries, wanted 20");

            // The oldest edit fell off, not the newest: the first undo must walk
            // back the last toggle (i = 20 set the entry done, so its restore
            // turns the entry open again).
            if (!InvokeTryUndo(view))
                throw new InvalidOperationException("the first undo reported nothing to restore");
            if (item.Done)
                throw new InvalidOperationException("the cap dropped the newest edit, not the oldest");

            int remaining = 0;
            while (InvokeTryUndo(view)) remaining++;
            if (remaining != 19)
                throw new InvalidOperationException("the stack yielded " + (remaining + 1) + " undos, wanted 20");
        }

        /// <summary>
        /// B3: ticking a parent is undoable - the restore brings back the open
        /// state, the cleared stamp and every child flag (the cascade included),
        /// keeping a pre-completed child done.
        /// </summary>
        private static void TodoUndoRestoresATickedParentWithItsCascade()
        {
            var data = NewTodoData(out TodoItem parent);
            var doneChild = TodoListOps.AddChild(parent, "Already done");
            TodoRollover.ToggleChild(doneChild, true);
            var openChild = TodoListOps.AddChild(parent, "Still open");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            InvokeViewActionWith(view, "ApplyItemToggle", parent, true);
            if (!parent.Done || parent.CompletedDate != DateTime.Today)
                throw new InvalidOperationException("the parent tick did not complete and stamp the entry");
            if (!doneChild.Done || !openChild.Done)
                throw new InvalidOperationException("the parent tick did not cascade");

            if (!InvokeTryUndo(view)) throw new InvalidOperationException("the parent tick did not undo");
            if (parent.Done || parent.CompletedDate != null)
                throw new InvalidOperationException("the undo left the parent completed or stamped");
            if (!doneChild.Done || openChild.Done)
                throw new InvalidOperationException("the undo did not restore the child flags");
        }

        /// <summary>B3: a lone sub-todo tick is undoable too.</summary>
        private static void TodoUndoRestoresAChildTick()
        {
            var data = NewTodoData(out TodoItem item);
            var child = TodoListOps.AddChild(item, "Nested");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            InvokeViewActionWith(view, "ApplyChildToggle", child, true);
            if (!child.Done) throw new InvalidOperationException("the child tick did not stick");

            if (!InvokeTryUndo(view)) throw new InvalidOperationException("the child tick did not undo");
            if (child.Done) throw new InvalidOperationException("the undo left the child ticked");
        }

        /// <summary>
        /// B3: the backspace ladder's completion lands on the undo stack like
        /// the checkbox tick does.
        /// </summary>
        private static void TodoLadderTickLandsOnTheUndoStack()
        {
            var data = NewTodoData(out TodoItem item);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.CaretIndex = 0;
            box.SelectionLength = 0;
            RaisePreviewKey(widget, view, box, Key.Back);
            if (!item.Done) throw new InvalidOperationException("the ladder did not complete the item");

            if (!InvokeTryUndo(view)) throw new InvalidOperationException("the ladder tick did not undo");
            if (item.Done) throw new InvalidOperationException("the undo left the ladder tick in place");
        }

        /// <summary>
        /// Task A4.1 (was fix round 12 R1): empty day + reveal - the list
        /// still contributes no row and no message appears, but the reveal now
        /// shrinks the calendar instead of growing the panel, so the revealed
        /// input sits directly at the day header row's bottom (the list slot
        /// is zero-high) and the pill stacks one ADD_GAP below the input row.
        /// </summary>
        private static void TodoEmptyDayInputSitsInThePillSlot()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);

            RevealAddBox(view);
            LayoutView(view);

            var scroller = view.FindName("ItemsScroller") as FrameworkElement;
            if (scroller == null || Math.Abs(scroller.Height) > 0.01)
                throw new InvalidOperationException(
                    "the list still reserves a row: " +
                    (scroller == null ? "ItemsScroller missing" : scroller.Height.ToString("0.##")));
            if (ContainsText(view, Localization.Get("Todo_EmptyDay")))
                throw new InvalidOperationException("the empty-day message is still rendered under the revealed input");

            var host = view.FindName("AddInputHost") as FrameworkElement;
            if (host == null || host.Visibility != Visibility.Visible)
                throw new InvalidOperationException("the input is not visible after the reveal");

            var headerRow = DayHeaderRow(view);
            double headerBottom = headerRow.TransformToAncestor(view)
                .Transform(new Point(0, headerRow.ActualHeight)).Y;
            double hostTop = host.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            if (Math.Abs(hostTop - headerBottom) > 1.0)
            {
                throw new InvalidOperationException(
                    "the revealed input is not at the day header's bottom: host top " +
                    hostTop.ToString("0.##") + " vs header bottom " + headerBottom.ToString("0.##"));
            }

            var pill = view.FindName("AddPill") as FrameworkElement;
            if (pill == null) throw new InvalidOperationException("AddPill not found");
            double pillTop = pill.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            if (Math.Abs(pillTop - (hostTop + host.ActualHeight) - ADD_GAP) > 1.0)
            {
                throw new InvalidOperationException(
                    "the pill is not stacked " + ADD_GAP + " below the input row: pill top " +
                    pillTop.ToString("0.##") + ", host top " + hostTop.ToString("0.##") +
                    " + height " + host.ActualHeight.ToString("0.##"));
            }
        }

        /// <summary>
        /// Task A4.1 (was fix round 12 R1): an empty blur collapses the
        /// revealed input back to the pill, which lands one ADD_GAP below the
        /// day header row's bottom (no message row appears and the list stays
        /// at zero rows). B1: the collapse is deferred now, so the offscreen
        /// check runs its dispatcher half explicitly.
        /// </summary>
        private static void TodoCollapsingEmptyDayInputRestoresThePill()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);
            RevealAddBox(view);
            LayoutView(view);

            var box = view.FindName("AddBox") as TextBox;
            if (box == null) throw new InvalidOperationException("AddBox not found");
            RaiseLostFocus(box); // an empty blur queues the collapse
            InvokeViewAction(view, "CollapseEmptyAddBox"); // the deferred half, run explicitly offscreen
            LayoutView(view);

            var scroller = view.FindName("ItemsScroller") as FrameworkElement;
            if (scroller == null || Math.Abs(scroller.Height) > 0.01)
                throw new InvalidOperationException(
                    "the collapsed empty day reserves a row: " +
                    (scroller == null ? "ItemsScroller missing" : scroller.Height.ToString("0.##")));
            if (ContainsText(view, Localization.Get("Todo_EmptyDay")))
                throw new InvalidOperationException("the empty-day message came back after the collapse");

            var pill = view.FindName("AddPill") as FrameworkElement;
            if (pill == null || pill.Visibility != Visibility.Visible)
                throw new InvalidOperationException("the pill is not visible after the collapse");
            var host = view.FindName("AddInputHost") as FrameworkElement;
            if (host.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("the input is still visible after the collapse");

            var headerRow = DayHeaderRow(view);
            double headerBottom = headerRow.TransformToAncestor(view)
                .Transform(new Point(0, headerRow.ActualHeight)).Y;
            double pillTop = pill.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            if (Math.Abs(pillTop - headerBottom - ADD_GAP) > 1.0)
            {
                throw new InvalidOperationException(
                    "the pill is not " + ADD_GAP + " below the day header's bottom: pill top " +
                    pillTop.ToString("0.##") + " vs header bottom " + headerBottom.ToString("0.##"));
            }
        }

        /// <summary>
        /// B1 (reported crash): the empty add box's blur must not rebuild the
        /// list inside the click that is still being routed - the rebuild
        /// detaches the clicked row mid-event. The repaint is deferred, so the
        /// captured row survives the blur.
        /// </summary>
        private static void TodoEmptyAddBoxBlurKeepsTheList()
        {
            ThemeManager.Initialize("dark");
            var data = NewTodoData(out TodoItem item);
            var child = TodoListOps.AddChild(item, "Buy eggs");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);
            RevealAddBox(view);
            LayoutView(view);

            var box = view.FindName("AddBox") as TextBox;
            if (box == null) throw new InvalidOperationException("AddBox not found");
            var block = TextBlockWith(view.FindName("ItemsPanel") as DependencyObject, child.Text);
            if (block == null) throw new InvalidOperationException("the child row text was not found");

            RaiseLostFocus(box); // the empty blur must not detach the row now
            if (!((Visual)block).IsDescendantOf(view))
            {
                throw new InvalidOperationException(
                    "the empty add box blur rebuilt the list and detached the clicked row");
            }
        }

        /// <summary>
        /// B1 end-to-end: the very sequence from the crash report - the empty
        /// add box blurs (its collapse deferred), then the click that blurred
        /// it still targets the row. The click must reach the row, open the
        /// child editor and never resolve coordinates on a detached visual.
        /// The handler resolves screen coordinates, so the view lives in an
        /// invisible offscreen HwndSource (the r17 caret pin's harness) -
        /// an unshown window's tree is not connected to a PresentationSource.
        /// </summary>
        private static void TodoClickAfterEmptyAddBoxBlurOpensTheEditor()
        {
            ThemeManager.Initialize("dark");
            var data = NewTodoData(out TodoItem item);
            var child = TodoListOps.AddChild(item, "Buy eggs");
            var view = new TodoCalendarView();
            view.Attach(data, null);

            var source = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters("xamlloadcheck-b1")
                {
                    WindowStyle = 0,
                    Width = 400,
                    Height = 700,
                    PositionX = -10000,
                    PositionY = -10000
                });
            source.RootVisual = view;
            try
            {
                LayoutView(view);
                RevealAddBox(view);
                LayoutView(view);

                var box = view.FindName("AddBox") as TextBox;
                if (box == null) throw new InvalidOperationException("AddBox not found");
                var block = TextBlockWith(view.FindName("ItemsPanel") as DependencyObject, child.Text);
                if (block == null) throw new InvalidOperationException("the child row text was not found");

                RaiseLostFocus(box); // the crash's step 1: the empty box blurs

                // The crash's step 2: the same click reaches the row.
                block.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                });

                if (PrivateField<string>(view, "_editingChildId") != child.Id)
                {
                    throw new InvalidOperationException(
                        "the click after the empty add box blur did not open the child editor");
                }
                EditorBoxInList(view); // and its box is really up
            }
            finally
            {
                source.Dispose();
            }
        }

        /// <summary>
        /// B1 guard: whenever a row is detached between the event's route and
        /// its handler (any rebuild path, not just the add box), the click
        /// handler must bail out instead of resolving coordinates on the dead
        /// visual.
        /// </summary>
        private static void TodoStaleRowClickNeverThrows()
        {
            ThemeManager.Initialize("dark");
            var data = NewTodoData(out TodoItem item);
            TodoListOps.AddChild(item, "Buy eggs");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var block = FirstDescendant<TextBlock>(RowAt(view, 1));
            if (block == null) throw new InvalidOperationException("the child row text was not found");

            view.Refresh(); // detach the captured row
            if (((Visual)block).IsDescendantOf(view))
                throw new InvalidOperationException("the row survived the forced rebuild");

            try
            {
                block.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "a stale row click threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>The first text block whose text matches (a row's entry text).</summary>
        private static TextBlock TextBlockWith(DependencyObject root, string text)
        {
            var block = root as TextBlock;
            if (block != null && block.Text == text) return block;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = TextBlockWith(VisualTreeHelper.GetChild(root, i), text);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Fix round 12 (R2): GetPanelSize() must include the chrome shell's
        /// 1px border on every side, or the arranged content host loses 2 DIP
        /// and WPF's layout clip cuts the view's bottom (the pill's lower
        /// border). Pinned for the collapsed and the revealed empty day, whose
        /// DesiredHeight differ (fix round 12, R1). Task A4.1 adds the exact
        /// legacy composition: panel height == view.DesiredHeight + 42.
        /// </summary>
        private static void TodoPanelFitsItsContent()
        {
            AssertPanelFitsContent(false);
            AssertPanelFitsContent(true);
        }

        private static void AssertPanelFitsContent(bool revealEmptyDayInput)
        {
            ThemeManager.Initialize("dark");
            var widget = new TodoWidget(new TodoData());
            var view = widget.CalendarView;
            if (revealEmptyDayInput)
            {
                RevealAddBox(view);
                LayoutView(view);
            }

            var size = widget.GetPanelSize();
            var chrome = (WidgetChrome)widget.Content;
            chrome.Measure(new Size(size.width, size.height));
            chrome.Arrange(new Rect(0, 0, size.width, size.height));

            if (chrome.ContentHost.ActualWidth < 340)
            {
                throw new InvalidOperationException(
                    "content host width " + chrome.ContentHost.ActualWidth.ToString("0.##") +
                    " < 340" + (revealEmptyDayInput ? " (revealed)" : " (collapsed)"));
            }
            if (chrome.ContentHost.ActualHeight < view.DesiredHeight)
            {
                throw new InvalidOperationException(
                    "content host height " + chrome.ContentHost.ActualHeight.ToString("0.##") +
                    " < the view's desired height " + view.DesiredHeight.ToString("0.##") +
                    (revealEmptyDayInput ? " (revealed)" : " (collapsed)"));
            }
            // Task A4.1: the legacy panel height is exactly the view's wanted
            // content plus the chrome (40 header + 2 border), in both states.
            if (Math.Abs(size.height - (view.DesiredHeight + 42)) > 0.001)
            {
                throw new InvalidOperationException(
                    "the panel height " + size.height.ToString("0.###") +
                    " != the view's desired height " + view.DesiredHeight.ToString("0.###") +
                    " + 42 (header 40 + border 2)" +
                    (revealEmptyDayInput ? " (revealed)" : " (collapsed)"));
            }
        }

        /// <summary>
        /// Fix round 17 (R1): a day switch must publish the new day's height
        /// even on an unrealized tree, where the rebuilt list can otherwise be
        /// measured from the previous day's cache. One item = 303, an empty
        /// day = 273 in this layout (each +6 for fix round 18's pill margin);
        /// Refresh publishes DesiredHeight straight from the NaturalHeight
        /// formula, so the value is fresh even before any manual measure.
        /// </summary>
        private static void TodoSizeRefreshIsNotStale()
        {
            ThemeManager.Initialize("dark");
            var data = new TodoData();
            TodoListOps.AddItem(data, "item", DateTime.Today);
            var widget = new TodoWidget(data);
            var view = widget.CalendarView;

            double withItem = view.DesiredHeight;
            if (Math.Abs(withItem - 303) > 0.5)
            {
                throw new InvalidOperationException(
                    "the one-item day measured " + withItem.ToString("0.##") + ", expected 303");
            }

            InvokeViewSelectDay(view, DateTime.Today.AddDays(1));
            double empty = view.DesiredHeight;
            if (Math.Abs(empty - 273) > 0.5)
            {
                throw new InvalidOperationException(
                    "after SelectDay on an empty day the height is " + empty.ToString("0.##") +
                    ", wanted the smaller empty-day height 273" +
                    (Math.Abs(empty - withItem) <= 0.5 ? " (stale: still the with-item height)" : ""));
            }

            InvokeViewSelectDay(view, DateTime.Today);
            double back = view.DesiredHeight;
            if (Math.Abs(back - withItem) > 0.5)
            {
                throw new InvalidOperationException(
                    "after selecting the item's day again the height is " + back.ToString("0.##") +
                    ", wanted " + withItem.ToString("0.##"));
            }
        }

        /// <summary>
        /// Fix round 17 (R2): the row text is an editing affordance, so it
        /// shows the I-beam hover cursor - parent and child rows both build
        /// through BuildItemText.
        /// </summary>
        private static void TodoTextRowsUseTheIBeamCursor()
        {
            ThemeManager.Initialize("dark");
            var data = NewTodoData(out TodoItem item);
            TodoListOps.AddChild(item, "Whole milk");
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var parentText = FirstDescendant<TextBlock>(RowAt(view, 0));
            var childText = FirstDescendant<TextBlock>(RowAt(view, 1));
            if (parentText == null || childText == null)
            {
                throw new InvalidOperationException("the parent/child row text was not found");
            }
            if (parentText.Cursor != Cursors.IBeam)
            {
                throw new InvalidOperationException(
                    "the parent row cursor is " + CursorName(parentText.Cursor) + ", wanted IBeam");
            }
            if (childText.Cursor != Cursors.IBeam)
            {
                throw new InvalidOperationException(
                    "the child row cursor is " + CursorName(childText.Cursor) + ", wanted IBeam");
            }
        }

        /// <summary>
        /// Fix round 17 (R2): a click on the row text opens the editor with the
        /// caret at the clicked character (no select-all). Production resolves
        /// the caret through screen coordinates, so the check connects the
        /// view to an invisible offscreen HwndSource and invokes the very
        /// method the editor's Loaded handler calls. The three screen points
        /// (left edge, middle, beyond the text's right edge) come from the
        /// row's text block before the first rebuild detaches it.
        /// </summary>
        private static void TodoClickPlacesTheCaretAtTheClickPoint()
        {
            ThemeManager.Initialize("dark");
            DateTime today = DateTime.Today;
            var data = new TodoData { SelectedDate = today };
            var item = TodoListOps.AddItem(data, "Hello wonderful world", today);
            var view = new TodoCalendarView();
            view.Attach(data, null);

            var source = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters("xamlloadcheck-caret")
                {
                    WindowStyle = 0,
                    Width = 400,
                    Height = 700,
                    PositionX = -10000,
                    PositionY = -10000
                });
            source.RootVisual = view;
            try
            {
                LayoutView(view);

                var block = FirstDescendant<TextBlock>(RowAt(view, 0));
                if (block == null) throw new InvalidOperationException("the row text block was not found");
                double y = block.ActualHeight / 2.0;
                double mid = TextWidth(block) / 2.0;
                Point leftClick = block.PointToScreen(new Point(0, y));
                Point midClick = block.PointToScreen(new Point(mid, y));
                Point rightClick = block.PointToScreen(new Point(block.ActualWidth + 100, y));
                string text = block.Text;

                var left = EditorCaretAfterClick(view, item.Id, leftClick);
                if (left.index != 0 || left.selectionLength != 0)
                {
                    throw new InvalidOperationException(
                        "the left-edge click placed index " + left.index +
                        "/selection " + left.selectionLength + ", wanted index 0 and no selection");
                }

                var middle = EditorCaretAfterClick(view, item.Id, midClick);
                if (middle.index <= 0 || middle.index >= text.Length || middle.selectionLength != 0)
                {
                    throw new InvalidOperationException(
                        "the middle click placed index " + middle.index +
                        "/selection " + middle.selectionLength + " of " + text.Length +
                        " characters, wanted an index strictly inside the text and no selection");
                }

                var right = EditorCaretAfterClick(view, item.Id, rightClick);
                if (right.index != text.Length || right.selectionLength != 0)
                {
                    throw new InvalidOperationException(
                        "the beyond-text click placed index " + right.index +
                        "/selection " + right.selectionLength + ", wanted index " + text.Length +
                        " and no selection");
                }
            }
            finally
            {
                source.Dispose();
            }
        }

        private static (int index, int selectionLength) EditorCaretAfterClick(
            TodoCalendarView view, string itemId, Point screenPoint)
        {
            InvokeViewEditAt(view, "StartItemEdit", itemId, screenPoint);
            LayoutView(view);
            var box = EditorBoxInList(view);
            InvokeApplyEditorCaret(view, box);
            return (box.CaretIndex, box.SelectionLength);
        }

        /// <summary>
        /// Fix round 19 (R3): the exact interleaving of the human smoke report.
        /// Child editor A is open (click caret); a click on child B blurs A
        /// (the window hook's ClearFocus -> deferred commit) and opens B with
        /// its own click point; A's deferred commit then rebuilds every row.
        /// The rebuilt editor for B must come back at B's click caret with
        /// SelectionLength 0 - a select-all flash is the defect (D1).
        /// </summary>
        private static void TodoRebuildKeepsTheClickCaret()
        {
            RunInterleavedChildEditors((view, b, rebuilt, firstEditorFocused) =>
            {
                if (rebuilt.CaretIndex != b.Text.Length || rebuilt.SelectionLength != 0)
                {
                    throw new InvalidOperationException(
                        "the rebuilt editor carries caret " + rebuilt.CaretIndex +
                        "/selection " + rebuilt.SelectionLength + " of " + b.Text.Length +
                        " characters, wanted the click caret with no select-all");
                }
                // Real keyboard focus needs an active window; when the
                // offscreen source granted it to the first editor, the
                // rebuilt one must have taken it over too.
                if (firstEditorFocused && !rebuilt.IsKeyboardFocused)
                {
                    throw new InvalidOperationException(
                        "the rebuilt editor is not keyboard-focused (the first editor was)");
                }
            });
        }

        /// <summary>
        /// Fix round 19 (R3): the same interleaving, checking D2 - the box the
        /// rebuild destroyed must not schedule a deferred commit (a stale
        /// pending) and must not end B's still-active session. Runs any
        /// pending so the failure shows the closed session, not just the
        /// queued callback.
        /// </summary>
        private static void TodoRebuildDoesNotCloseTheSurvivingEditor()
        {
            RunInterleavedChildEditors((view, b, rebuilt, firstEditorFocused) =>
            {
                bool stalePending = PendingEditorCommit(view) != null;
                RunPendingEditorCommitIfAny(view);
                bool stillEditing = PrivateField<string>(view, "_editingChildId") == b.Id;
                bool boxOpen = EditorBoxInListOrNull(view) != null;
                if (stalePending || !stillEditing || !boxOpen)
                {
                    throw new InvalidOperationException(
                        "stalePending=" + stalePending + " editingChild=" + stillEditing +
                        " boxOpen=" + boxOpen + ", wanted no deferred commit from the " +
                        "destroyed box and B's session still open");
                }
            });
        }

        /// <summary>
        /// Fix round 19 (R3) guard: the child adder is the only editor opened
        /// without a click point, so the select-all default must stay - the
        /// session's own click point must never leak into a later adder.
        /// </summary>
        private static void TodoChildAdderStillSelectsAllOnLoad()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            InvokeViewEdit(view, "StartChildAdd", item.Id);
            LayoutView(view);

            var box = EditorBoxInList(view);
            box.Text = "New child";
            InvokeApplyEditorCaret(view, box);

            if (box.CaretIndex != 0 || box.SelectionLength != box.Text.Length)
            {
                throw new InvalidOperationException(
                    "the adder's editor carries caret " + box.CaretIndex +
                    "/selection " + box.SelectionLength + " of " + box.Text.Length +
                    " characters, wanted the intact select-all default");
            }
        }

        /// <summary>
        /// Fix round 19 (R3) harness: drives the deterministic offscreen
        /// interleaving and hands the rebuilt surviving editor to the check.
        /// The view lives in an invisible offscreen HwndSource because the
        /// caret path resolves screen coordinates. The destroyed box's focus
        /// loss (which production raises when the rebuilt box takes focus) is
        /// raised explicitly; Loaded handlers do not run in this harness, so
        /// each editor's caret placement is invoked the same way r17's pin
        /// does. The flag tells the check whether keyboard focus worked at
        /// all in this offscreen environment.
        /// </summary>
        private static void RunInterleavedChildEditors(
            Action<TodoCalendarView, SubTodoItem, TextBox, bool> check)
        {
            ThemeManager.Initialize("dark");
            DateTime today = DateTime.Today;
            var data = new TodoData { SelectedDate = today };
            var item = TodoListOps.AddItem(data, "Parent", today);
            var a = TodoListOps.AddChild(item, "Alpha");
            var b = TodoListOps.AddChild(item, "Bravo");
            var view = new TodoCalendarView();
            view.Attach(data, null);

            var source = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters("xamlloadcheck-fix19")
                {
                    WindowStyle = 0,
                    Width = 400,
                    Height = 700,
                    PositionX = -10000,
                    PositionY = -10000
                });
            source.RootVisual = view;
            try
            {
                LayoutView(view);

                // A click past B's text resolves to its trailing insertion
                // edge (r17's trailing-edge rule), a deterministic caret
                // expectation that also distinguishes it from a select-all.
                Point aPoint = BeyondTextPoint(RowAt(view, 1));
                Point bPoint = BeyondTextPoint(RowAt(view, 2));

                InvokeViewEditAt(view, "StartChildEdit", a.Id, aPoint);
                LayoutView(view);
                var aBox = EditorBoxInList(view);
                InvokeApplyEditorCaret(view, aBox);
                bool firstEditorFocused = aBox.IsKeyboardFocused;

                // The click on B: the window hook clears A's focus first,
                // which schedules A's deferred commit.
                RaiseLostFocus(aBox);

                InvokeViewEditAt(view, "StartChildEdit", b.Id, bPoint);
                LayoutView(view);
                var bBox = EditorBoxInList(view);
                InvokeApplyEditorCaret(view, bBox);

                // A's deferred commit now runs and rebuilds every row,
                // destroying B's focused box.
                RunPendingEditorCommit(view);

                // The rebuild's focus hand-off on the destroyed box.
                RaiseLostFocus(bBox);

                // The rebuilt B editor's Loaded handler.
                var rebuilt = EditorBoxInList(view);
                InvokeApplyEditorCaret(view, rebuilt);

                check(view, b, rebuilt, firstEditorFocused);
            }
            finally
            {
                source.Dispose();
            }
        }

        /// <summary>The screen point just past a row text's trailing edge.</summary>
        private static Point BeyondTextPoint(FrameworkElement row)
        {
            var block = FirstDescendant<TextBlock>(row);
            if (block == null) throw new InvalidOperationException("row text block not found");
            return block.PointToScreen(new Point(block.ActualWidth + 100, block.ActualHeight / 2.0));
        }

        private static void InvokeViewSelectDay(TodoCalendarView view, DateTime day)
        {
            var select = typeof(TodoCalendarView).GetMethod("SelectDay",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (select == null) throw new InvalidOperationException("SelectDay not found on TodoCalendarView");
            select.Invoke(view, new object[] { day });
        }

        private static void InvokeApplyEditorCaret(TodoCalendarView view, TextBox box)
        {
            var apply = typeof(TodoCalendarView).GetMethod("ApplyEditorCaret",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (apply == null)
            {
                throw new InvalidOperationException("ApplyEditorCaret not found (no caret placement)");
            }
            apply.Invoke(view, new object[] { box });
        }

        /// <summary>The rendered width of a text block's text at its own font.</summary>
        private static double TextWidth(TextBlock block)
        {
            var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
            var formatted = new FormattedText(block.Text, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, block.FontSize, Brushes.Black, 1.0);
            return formatted.Width;
        }

        private static string CursorName(Cursor cursor)
        {
            return cursor == null ? "null" : cursor.ToString();
        }

        /// <summary>
        /// The revealed add input leads with the same unticked checkbox visual
        /// as a list row (16x16, radius 4, 1.5 border), then the text box.
        /// </summary>
        private static void TodoRevealedInputLooksLikeARow()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);
            RevealAddBox(view);
            LayoutView(view);

            var host = view.FindName("AddInputHost") as Border;
            if (host == null || host.Visibility != Visibility.Visible)
                throw new InvalidOperationException("the revealed input host is not visible");

            var checkHost = view.FindName("AddCheckHost") as ContentPresenter;
            var addCheck = checkHost == null ? null : checkHost.Content as Border;
            if (addCheck == null)
                throw new InvalidOperationException("the add row has no leading checkbox visual");

            // A list row's checkbox comes from the same builder; compare signature.
            var data = NewTodoData(out TodoItem item);
            var rowView = new TodoWidget(data).CalendarView;
            LayoutView(rowView);
            var rowCheck = RowContent(RowAt(rowView, 0)).Children[0] as Border;
            if (rowCheck == null)
                throw new InvalidOperationException("a list row has no checkbox border to compare");

            if (addCheck.Width != rowCheck.Width || addCheck.Height != rowCheck.Height ||
                addCheck.BorderThickness != rowCheck.BorderThickness ||
                addCheck.CornerRadius != rowCheck.CornerRadius)
            {
                throw new InvalidOperationException(
                    "add checkbox " + addCheck.Width + "x" + addCheck.Height + " r" + addCheck.CornerRadius +
                    " differs from the row checkbox " + rowCheck.Width + "x" + rowCheck.Height +
                    " r" + rowCheck.CornerRadius);
            }
        }

        /// <summary>The placeholder element is gone from the view and its text is not rendered.</summary>
        private static void TodoAddInputHasNoPlaceholder()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);
            RevealAddBox(view);
            LayoutView(view);

            if (view.FindName("AddPlaceholder") != null)
                throw new InvalidOperationException("the placeholder element is still part of the view");

            var addRow = view.FindName("AddRow") as DependencyObject;
            if (ContainsText(addRow, Localization.Get("Todo_AddPlaceholder")))
                throw new InvalidOperationException("the placeholder text is still rendered in the add row");
        }

        /// <summary>
        /// The editing cue is the caret, never a background band: the revealed
        /// input host must carry no fill (A == 0) in dark, and again after the
        /// live switch to light - where the old tint resolved to white.
        /// </summary>
        private static void TodoAddRowHasNoWhiteBackground()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);
            RevealAddBox(view);
            LayoutView(view);

            AssertNoFill("the revealed input host [dark]", view.FindName("AddInputHost") as Border);

            ThemeManager.Initialize("light");
            view.Refresh(); // the app's theme path ends here: RefreshTheme -> UpdateUI -> Refresh
            AssertNoFill("the revealed input host [light]", view.FindName("AddInputHost") as Border);
            ThemeManager.Initialize("dark"); // restore for the later checks

            // Collapsed: the host (and its fill) is hidden, the pill carries the state.
            var collapsedView = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(collapsedView);
            var collapsedHost = collapsedView.FindName("AddInputHost") as Border;
            if (collapsedHost == null || collapsedHost.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("the input host is visible while collapsed");
        }

        /// <summary>The element must carry no fill: a null background or an A == 0 brush.</summary>
        private static void AssertNoFill(string label, Border element)
        {
            if (element == null) throw new InvalidOperationException(label + ": element not found");
            var brush = element.Background as SolidColorBrush;
            if (element.Background != null && (brush == null || brush.Color.A != 0))
            {
                throw new InvalidOperationException(
                    label + ": background is not transparent: " +
                    (brush == null ? element.Background.ToString() : Describe(brush.Color)));
            }
        }

        /// <summary>
        /// After a live theme switch the add row's checkbox outline must match a
        /// fresh list-row checkbox (both from BuildCheckBox under the new
        /// theme), not keep the brush captured when it was first built.
        /// </summary>
        private static void TodoAddCheckboxFollowsLiveTheme()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);

            Color? dark = AddCheckBrushColor(view);
            if (dark == null) throw new InvalidOperationException("the add checkbox has no border brush");

            ThemeManager.Initialize("light");
            view.Refresh(); // the app's theme path ends here: RefreshTheme -> UpdateUI -> Refresh
            Color? afterSwitch = AddCheckBrushColor(view);

            var data = new TodoData { SelectedDate = DateTime.Today };
            TodoListOps.AddItem(data, "Buy milk", DateTime.Today);
            var rowView = new TodoWidget(data).CalendarView; // built under the light theme
            LayoutView(rowView);
            var rowCheck = RowContent(RowAt(rowView, 0)).Children[0] as Border;
            var rowBrush = rowCheck == null ? null : rowCheck.BorderBrush as SolidColorBrush;
            if (rowBrush == null) throw new InvalidOperationException("no list-row checkbox to compare");

            if (afterSwitch == null || afterSwitch.Value != rowBrush.Color)
            {
                throw new InvalidOperationException(
                    "add checkbox brush " + (afterSwitch.HasValue ? Describe(afterSwitch.Value) : "null") +
                    " != list row brush " + Describe(rowBrush.Color) + " after the theme switch");
            }
            if (dark.Value == afterSwitch.Value)
                throw new InvalidOperationException("the add checkbox brush never changed with the theme");

            ThemeManager.Initialize("dark"); // restore for the later checks
        }

        private static Color? AddCheckBrushColor(TodoCalendarView view)
        {
            var check = AddCheckBorder(view);
            var brush = check == null ? null : check.BorderBrush as SolidColorBrush;
            if (brush == null) return null;
            return brush.Color;
        }

        private static Border AddCheckBorder(TodoCalendarView view)
        {
            var host = view.FindName("AddCheckHost") as ContentPresenter;
            return host == null ? null : host.Content as Border;
        }

        /// <summary>
        /// Fix round 13: the pill's ＋ glyph follows the widget's own colour -
        /// under the dark theme and across a live switch to light (the same
        /// Refresh lifecycle as the add-row checkbox), not the theme's fixed
        /// brand accent. Fix round 14 (R1) pinned the pill's border to the same
        /// readable colour; fix round 16 (R1) flips that: the glyph keeps the
        /// readable widget accent while the background and border become
        /// Material-style derived tints of the RAW widget colour (≈12% and
        /// ≈30% alpha), and MouseEnter/MouseLeave swap the background between
        /// the ≈22% hover tint and the base brush. The lookup prefers the named
        /// glyph but falls back to the pill's TextBlock so the pre-fix run
        /// reports the actual colour.
        /// </summary>
        private static void TodoAddPillFollowsTheWidgetColor()
        {
            ThemeManager.Initialize("dark");
            var data = new TodoData { SelectedDate = DateTime.Today, Color = "#E8A33D" };
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            AssertAddPillColor("dark", view, "#E8A33D");

            ThemeManager.Initialize("light");
            view.Refresh(); // the app's theme path ends here: RefreshTheme -> UpdateUI -> Refresh
            AssertAddPillColor("light", view, "#E8A33D");

            ThemeManager.Initialize("dark"); // restore for the later checks
        }

        /// <summary>
        /// Fix round 13: extreme widget colours stay readable on the theme's
        /// panel backdrop - a near-white colour darkens on the light theme
        /// (DarkenColor 0.6, brightness <= 0.8), a near-black colour lightens
        /// on the dark theme (LightenColor 0.5, brightness >= 0.3). Fix round
        /// 14 (R1) pinned the pill's border to that same fallback colour; fix
        /// round 16 (R1) flips it: only the ＋ glyph keeps the readability
        /// fallback, while background and border are the raw widget colour at
        /// ≈12% / ≈30% alpha.
        /// </summary>
        private static void TodoAddPillStaysReadableForExtremeColors()
        {
            ThemeManager.Initialize("light");
            var lightData = new TodoData { SelectedDate = DateTime.Today, Color = "#FFFFFF" };
            var lightView = new TodoWidget(lightData).CalendarView;
            LayoutView(lightView);

            Color lightGlyph = AddPillGlyphColor(lightView);
            if (lightGlyph == Colors.White)
                throw new InvalidOperationException("the ＋ glyph is still white on the light backdrop");
            if (PerceivedBrightness(lightGlyph) > 0.8)
                throw new InvalidOperationException(
                    "the light-theme glyph keeps brightness " +
                    PerceivedBrightness(lightGlyph).ToString("0.###") + " > 0.8");
            Color lightWanted = Utils.DarkenColor(Utils.HexToColor("#FFFFFF"), 0.6);
            if (lightGlyph != lightWanted)
            {
                throw new InvalidOperationException(
                    "the light-theme glyph is " + Describe(lightGlyph) +
                    ", wanted the darkened fallback " + Describe(lightWanted));
            }

            // Fix round 16 (R1): the tints derive from the RAW near-white
            // colour, not from the glyph's readability fallback.
            Color lightRaw = Utils.HexToColor("#FFFFFF");
            AssertPillTint("the light-theme pill background",
                AddPillBackgroundBrush(lightView).Color, lightRaw, 0x1F);
            AssertPillTint("the light-theme pill border",
                AddPillBorderColor(lightView), lightRaw, 0x4D);

            ThemeManager.Initialize("dark");
            var darkData = new TodoData { SelectedDate = DateTime.Today, Color = "#101010" };
            var darkView = new TodoWidget(darkData).CalendarView;
            LayoutView(darkView);

            Color darkGlyph = AddPillGlyphColor(darkView);
            if (PerceivedBrightness(darkGlyph) < 0.3)
                throw new InvalidOperationException(
                    "the dark-theme glyph keeps brightness " +
                    PerceivedBrightness(darkGlyph).ToString("0.###") + " < 0.3");
            Color darkWanted = Utils.LightenColor(Utils.HexToColor("#101010"), 0.5);
            if (darkGlyph != darkWanted)
            {
                throw new InvalidOperationException(
                    "the dark-theme glyph is " + Describe(darkGlyph) +
                    ", wanted the lightened fallback " + Describe(darkWanted));
            }

            Color darkRaw = Utils.HexToColor("#101010");
            AssertPillTint("the dark-theme pill background",
                AddPillBackgroundBrush(darkView).Color, darkRaw, 0x1F);
            AssertPillTint("the dark-theme pill border",
                AddPillBorderColor(darkView), darkRaw, 0x4D);
        }

        private static void AssertAddPillColor(string label, TodoCalendarView view, string expectedHex)
        {
            Color wanted = Utils.HexToColor(expectedHex);
            Color glyph = AddPillGlyphColor(view);
            if (glyph != wanted)
            {
                throw new InvalidOperationException(
                    label + ": the ＋ glyph is " + Describe(glyph) +
                    ", wanted the widget colour " + Describe(wanted));
            }

            SolidColorBrush background = AddPillBackgroundBrush(view);
            AssertPillTint(label + ": the pill background", background.Color, wanted, 0x1F);
            AssertPillTint(label + ": the pill border", AddPillBorderColor(view), wanted, 0x4D);

            InvokePillMouseHandler(view, "AddPill_MouseEnter");
            SolidColorBrush hovered = AddPillBackgroundBrush(view);
            AssertPillTint(label + ": the hovered pill background", hovered.Color, wanted, 0x38);

            InvokePillMouseHandler(view, "AddPill_MouseLeave");
            SolidColorBrush restored = AddPillBackgroundBrush(view);
            if (!ReferenceEquals(restored, background))
            {
                throw new InvalidOperationException(
                    label + ": AddPill_MouseLeave did not restore the base pill brush (" +
                    Describe(restored.Color) + ")");
            }
        }

        /// <summary>
        /// Fix round 16 (R1): a Material-style derived tint keeps the widget's
        /// RGB and only changes the alpha (0x1F ≈ 12% base, 0x38 ≈ 22% hover,
        /// 0x4D ≈ 30% border, each within the briefed ±2%).
        /// </summary>
        private static void AssertPillTint(string label, Color actual, Color wanted, int alpha)
        {
            if (actual.R != wanted.R || actual.G != wanted.G || actual.B != wanted.B)
            {
                throw new InvalidOperationException(
                    label + " is " + Describe(actual) + ", wanted widget RGB " +
                    Describe(wanted) + " at alpha ≈0x" + alpha.ToString("X2"));
            }
            if (Math.Abs(actual.A - alpha) > 5)
            {
                throw new InvalidOperationException(
                    label + " is " + Describe(actual) + ", wanted alpha ≈0x" +
                    alpha.ToString("X2") + " (α±2%)");
            }
        }

        private static void InvokePillMouseHandler(TodoCalendarView view, string method)
        {
            var handler = typeof(TodoCalendarView).GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic);
            var pill = view.FindName("AddPill");
            if (handler == null || pill == null)
            {
                throw new InvalidOperationException(method + " / AddPill not found");
            }

            handler.Invoke(view, new object[] { pill, new MouseEventArgs(Mouse.PrimaryDevice, 0) });
        }

        private static Color AddPillBorderColor(TodoCalendarView view)
        {
            var pill = view.FindName("AddPill") as Border;
            if (pill == null) throw new InvalidOperationException("the add pill was not found");

            var brush = pill.BorderBrush as SolidColorBrush;
            if (brush == null)
            {
                throw new InvalidOperationException(
                    "the add pill has no solid border brush: " +
                    (pill.BorderBrush == null ? "null" : pill.BorderBrush.ToString()));
            }
            return brush.Color;
        }

        private static SolidColorBrush AddPillBackgroundBrush(TodoCalendarView view)
        {
            var pill = view.FindName("AddPill") as Border;
            if (pill == null) throw new InvalidOperationException("the add pill was not found");

            var brush = pill.Background as SolidColorBrush;
            if (brush == null)
            {
                throw new InvalidOperationException(
                    "the add pill has no solid background brush: " +
                    (pill.Background == null ? "null" : pill.Background.ToString()));
            }
            return brush;
        }

        private static Color AddPillGlyphColor(TodoCalendarView view)
        {
            var glyph = view.FindName("AddPillGlyph") as TextBlock;
            if (glyph == null) // pre-fix: the pill's TextBlock still carries the brand accent
                glyph = FirstDescendant<TextBlock>(view.FindName("AddPill") as DependencyObject);
            if (glyph == null) throw new InvalidOperationException("the add pill's ＋ glyph was not found");

            var brush = glyph.Foreground as SolidColorBrush;
            if (brush == null)
            {
                throw new InvalidOperationException(
                    "the ＋ glyph has no solid foreground: " +
                    (glyph.Foreground == null ? "null" : glyph.Foreground.ToString()));
            }
            return brush.Color;
        }

        private static double PerceivedBrightness(Color color)
        {
            return (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        }

        /// <summary>
        /// Fix round 14 (R2): the selected day drops the solid rounded-square
        /// fill and shows the human's hand-drawn circle instead - at least
        /// three Paths stroked in the widget colour, rotated 183 degrees,
        /// with the day number unchanged. The day 15 cell is unique in any
        /// month grid, so the number text finds exactly the selected cell.
        /// Fix round 15 (R1): the circle's Viewbox grows to >= 30 DIP.
        /// Task A4.2: the Viewbox is the applied cell height + 4 (the
        /// unpersisted fallback keeps the migration cell 28 -> 32; a
        /// persisted 385-high panel lands on the 36 clamp -> 40).
        /// </summary>
        private static void TodoSelectedDayUsesTheHandDrawnCircle()
        {
            ThemeManager.Initialize("dark");
            DateTime today = DateTime.Today;
            DateTime selected = new DateTime(today.Year, today.Month, 15);
            var data = new TodoData { SelectedDate = selected, Color = "#E8A33D" };
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var cell = DayCellFor(view, selected.Day);

            var background = cell.Background as SolidColorBrush;
            if (cell.Background != null && (background == null || background.Color.A != 0))
            {
                throw new InvalidOperationException(
                    "the selected cell still has a solid fill: " +
                    (background == null ? cell.Background.ToString() : Describe(background.Color)));
            }

            Color wanted = Utils.HexToColor("#E8A33D");
            int stroked = CountPathsStroked(cell, wanted);
            if (stroked < 3)
            {
                throw new InvalidOperationException(
                    "the selected cell has " + stroked +
                    " path(s) stroked in the widget colour, wanted at least 3");
            }

            var number = FirstDescendant<TextBlock>(cell);
            if (number == null || number.Text != selected.Day.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("the selected cell lost its day number");
            }

            if (!HasRotateTransform(cell, 183))
            {
                throw new InvalidOperationException(
                    "the selected cell carries no RotateTransform with Angle 183");
            }

            var viewbox = FirstDescendant<Viewbox>(cell);
            if (viewbox == null)
            {
                throw new InvalidOperationException("the selected cell has no circle Viewbox");
            }
            if (viewbox.Width < 30 || viewbox.Height < 30)
            {
                throw new InvalidOperationException(
                    "the circle Viewbox is " + viewbox.Width + "x" + viewbox.Height +
                    ", wanted at least 30x30");
            }
            if (Math.Abs(viewbox.Width - 32) > 0.01 || Math.Abs(viewbox.Height - 32) > 0.01)
            {
                throw new InvalidOperationException(
                    "the fallback circle Viewbox is " + viewbox.Width.ToString("0.##") + "x" +
                    viewbox.Height.ToString("0.##") + ", wanted the cell 28 + 4 = 32");
            }

            // A persisted 342x385 panel (5 weeks, empty day) lands on the 36
            // cell clamp, so the circle box grows to 40.
            var sizedData = new TodoData
            {
                PanelWidth = 342,
                PanelHeight = 385,
                SelectedDate = new DateTime(2026, 10, 12),
                Color = "#E8A33D"
            };
            var sizedView = new TodoWidget(sizedData).CalendarView;
            LayoutView(sizedView);
            var sizedBox = FirstDescendant<Viewbox>(DayCellFor(sizedView, 12));
            if (sizedBox == null ||
                Math.Abs(sizedBox.Width - 40) > 0.01 || Math.Abs(sizedBox.Height - 40) > 0.01)
            {
                throw new InvalidOperationException(
                    "the 36-cell circle Viewbox is " +
                    (sizedBox == null
                        ? "<none>"
                        : sizedBox.Width.ToString("0.##") + "x" + sizedBox.Height.ToString("0.##")) +
                    ", wanted the cell 36 + 4 = 40");
            }
        }

        /// <summary>
        /// Task A4.2 (was fix round 15 R2): the today cell wears the human's
        /// hand-drawn ring plus a hand-drawn check tucked into the ring's
        /// bottom-right corner (the solid disc is gone). The ring keeps its
        /// 183-degree rotation and the widget-colour strokes; the check's two
        /// main strokes reuse the ring's brush. The number stays the plain
        /// themed text colour. A today cell is found by its TodayCheck canvas.
        /// </summary>
        private static void TodoTodayMarkerIsTheHandDrawnRingWithACheck()
        {
            ThemeManager.Initialize("dark");
            DateTime today = DateTime.Today;
            DateTime other = new DateTime(today.Year, today.Month, today.Day == 15 ? 16 : 15);
            Color wanted = Utils.HexToColor("#E8A33D");

            // Today NOT selected: the ring plus the check mark it.
            var data = new TodoData { SelectedDate = other, Color = "#E8A33D" };
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var cell = TodayCheckCell(view);
            if (!ContainsText(cell, today.Day.ToString(CultureInfo.InvariantCulture)))
            {
                throw new InvalidOperationException(
                    "the today cell does not show today's number " + today.Day);
            }
            AssertTodayRingAndCheck(cell, wanted);
            AssertTransparentCellBorder(cell);
            if (FindTodayDisc(cell) != null)
            {
                throw new InvalidOperationException("the today cell still carries a solid disc");
            }
            AssertTodayNumberIsPlain(cell, today.Day);

            // The same view's selected-only day (not today): ring, no check,
            // no disc.
            var selectedOnly = DayCellFor(view, other.Day);
            if (TodayCheckCanvas(selectedOnly) != null)
            {
                throw new InvalidOperationException(
                    "the selected-only cell " + other.Day + " wears today's check");
            }
            if (FindTodayDisc(selectedOnly) != null)
            {
                throw new InvalidOperationException(
                    "the selected-only cell " + other.Day + " still carries a today disc");
            }
            int selectedOnlyPaths = CountPathsStroked(selectedOnly, wanted);
            if (selectedOnlyPaths < 3)
            {
                throw new InvalidOperationException(
                    "the selected-only cell has " + selectedOnlyPaths +
                    " path(s) stroked in the widget colour, wanted at least 3");
            }

            // Today SELECTED: the same ring + check, still no disc.
            var selectedData = new TodoData { SelectedDate = today, Color = "#E8A33D" };
            var selectedView = new TodoWidget(selectedData).CalendarView;
            LayoutView(selectedView);

            var selectedCell = TodayCheckCell(selectedView);
            AssertTodayRingAndCheck(selectedCell, wanted);
            if (FindTodayDisc(selectedCell) != null)
            {
                throw new InvalidOperationException(
                    "the selected today cell still carries a solid disc");
            }
            AssertTodayNumberIsPlain(selectedCell, today.Day);

            // A near-white widget colour under the dark theme keeps the ring
            // and the check in the widget colour; there is no disc to darken.
            var lightData = new TodoData { SelectedDate = other, Color = "#FFFFFF" };
            var lightView = new TodoWidget(lightData).CalendarView;
            LayoutView(lightView);

            var lightCell = TodayCheckCell(lightView);
            if (!ContainsText(lightCell, today.Day.ToString(CultureInfo.InvariantCulture)))
            {
                throw new InvalidOperationException(
                    "the near-white today cell does not show today's number " + today.Day);
            }
            AssertTodayRingAndCheck(lightCell, Utils.HexToColor("#FFFFFF"));
            if (FindTodayDisc(lightCell) != null)
            {
                throw new InvalidOperationException(
                    "the near-white today cell still carries a solid disc");
            }
            AssertTodayNumberIsPlain(lightCell, today.Day);
        }

        /// <summary>
        /// Task A4.2: the today cell carries the rotated ring (widget-colour
        /// strokes) and a TodayCheck canvas whose two main strokes reuse the
        /// ring's brush.
        /// </summary>
        private static void AssertTodayRingAndCheck(Border cell, Color wanted)
        {
            if (!HasRotateTransform(cell, 183))
            {
                throw new InvalidOperationException(
                    "the today cell carries no RotateTransform with Angle 183 (the ring is gone)");
            }
            int stroked = CountPathsStroked(cell, wanted);
            if (stroked < 3)
            {
                throw new InvalidOperationException(
                    "the today cell has " + stroked +
                    " path(s) stroked in the widget colour, wanted at least 3");
            }

            var check = TodayCheckCanvas(cell);
            if (check == null)
            {
                throw new InvalidOperationException("the today cell has no TodayCheck canvas");
            }
            int checkPaths = 0;
            foreach (var child in check.Children)
            {
                if (child is Path) checkPaths++;
            }
            if (checkPaths < 3)
            {
                throw new InvalidOperationException(
                    "the today check has " + checkPaths + " path(s), wanted at least 3");
            }

            Brush ringStroke = MainRingStroke(cell);
            if (ringStroke == null)
            {
                throw new InvalidOperationException("the today ring has no main stroked path");
            }
            int mains = 0;
            foreach (var child in check.Children)
            {
                var path = child as Path;
                if (path == null || path.Stroke == null || path.StrokeThickness < 3.5) continue;
                mains++;
                if (!ReferenceEquals(ringStroke, path.Stroke) && !SameBrushColor(ringStroke, path.Stroke))
                {
                    throw new InvalidOperationException(
                        "a today check main stroke does not reuse the ring's brush");
                }
            }
            if (mains < 2)
            {
                throw new InvalidOperationException(
                    "the today check has " + mains + " main stroke(s), wanted 2");
            }
        }

        /// <summary>Task A4.2: the ring's first main widget-coloured stroke.</summary>
        private static Brush MainRingStroke(Border cell)
        {
            var canvas = RingCanvas(cell);
            if (canvas == null) return null;
            foreach (var child in canvas.Children)
            {
                var path = child as Path;
                if (path != null && path.Stroke != null && path.StrokeThickness >= 3.2)
                {
                    return path.Stroke;
                }
            }
            return null;
        }

        /// <summary>Task A4.2: the rotated 183-degree canvas of the hand-drawn ring.</summary>
        private static Canvas RingCanvas(DependencyObject root)
        {
            var canvas = root as Canvas;
            if (canvas != null && MatchesRotate(canvas.RenderTransform, 183)) return canvas;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = RingCanvas(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private static bool SameBrushColor(Brush first, Brush second)
        {
            var a = first as SolidColorBrush;
            var b = second as SolidColorBrush;
            return a != null && b != null && a.Color == b.Color;
        }

        /// <summary>Task A4.2: the today check's tagged canvas, or null.</summary>
        private static Canvas TodayCheckCanvas(DependencyObject root)
        {
            var canvas = root as Canvas;
            if (canvas != null && (canvas.Tag as string) == "TodayCheck") return canvas;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = TodayCheckCanvas(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>The today cell's own border must be transparent, not a ring.</summary>
        private static void AssertTransparentCellBorder(Border cell)
        {
            var brush = cell.BorderBrush as SolidColorBrush;
            if (cell.BorderBrush != null && (brush == null || brush.Color.A != 0))
            {
                throw new InvalidOperationException(
                    "the today cell still has a visible border: " +
                    (brush == null ? cell.BorderBrush.ToString() : Describe(brush.Color)));
            }
        }

        /// <summary>Task A4.2: today's number is the plain themed text colour (no disc).</summary>
        private static void AssertTodayNumberIsPlain(Border cell, int day)
        {
            var number = FirstDescendant<TextBlock>(cell);
            if (number == null || number.Text != day.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("the today cell lost its day number");
            }
            if (number.Foreground != ThemeManager.TextBrush)
            {
                throw new InvalidOperationException(
                    "the today number is not the themed text colour: " +
                    (number.Foreground == null ? "null" : number.Foreground.ToString()));
            }
        }

        /// <summary>The day cell whose number TextBlock shows the day (15 is unique in any grid).</summary>
        private static Border DayCellFor(TodoCalendarView view, int day)
        {
            var grid = view.FindName("DayGrid") as UniformGrid;
            if (grid == null) throw new InvalidOperationException("the day grid was not found");

            string wanted = day.ToString(CultureInfo.InvariantCulture);
            foreach (var child in grid.Children)
            {
                var cell = child as Border;
                if (cell == null) continue;
                var number = FirstDescendant<TextBlock>(cell);
                if (number != null && number.Text == wanted) return cell;
            }
            throw new InvalidOperationException("no day cell shows " + wanted);
        }

        /// <summary>Task A4.2: the day cell whose subtree holds the TodayCheck canvas.</summary>
        private static Border TodayCheckCell(TodoCalendarView view)
        {
            var grid = view.FindName("DayGrid") as UniformGrid;
            if (grid == null) throw new InvalidOperationException("the day grid was not found");

            foreach (var child in grid.Children)
            {
                var cell = child as Border;
                if (cell == null) continue;
                if (TodayCheckCanvas(cell) != null) return cell;
            }
            throw new InvalidOperationException("no today check cell was found");
        }

        /// <summary>Fix round 15b: the today disc by size alone, whatever its fill.</summary>
        private static Ellipse FindTodayDisc(DependencyObject root)
        {
            var ellipse = root as Ellipse;
            if (ellipse != null && ellipse.Width >= 18 && ellipse.Height >= 18)
            {
                return ellipse;
            }
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindTodayDisc(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// The revealed add input must match a list row: same checkbox x/size
        /// and the input text starting exactly at the list text's left edge
        /// (no inset - the shared style padding is stripped too).
        /// </summary>
        private static void TodoAddInputAlignsWithARow()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView; // one row as the reference
            LayoutView(view);
            RevealAddBox(view);
            LayoutView(view);

            var addCheck = AddCheckBorder(view);
            var box = view.FindName("AddBox") as TextBox;
            if (addCheck == null || box == null) throw new InvalidOperationException("add input parts missing");

            var row = RowContent(RowAt(view, 0));
            var rowCheck = row.Children[0] as Border;
            var rowText = FirstDescendant<TextBlock>(row);
            if (rowCheck == null || rowText == null) throw new InvalidOperationException("reference row parts missing");

            if (Math.Abs(addCheck.Width - rowCheck.Width) > 0.01 ||
                Math.Abs(addCheck.Height - rowCheck.Height) > 0.01)
            {
                throw new InvalidOperationException("the add checkbox size differs from a row checkbox");
            }

            double addCheckX = addCheck.TransformToAncestor(view).Transform(new Point(0, 0)).X;
            double rowCheckX = rowCheck.TransformToAncestor(view).Transform(new Point(0, 0)).X;
            if (Math.Abs(addCheckX - rowCheckX) > 1.0)
            {
                throw new InvalidOperationException(
                    "add checkbox x " + addCheckX.ToString("0.##") +
                    " vs row checkbox x " + rowCheckX.ToString("0.##"));
            }

            double boxX = box.TransformToAncestor(view).Transform(new Point(0, 0)).X;
            double textX = rowText.TransformToAncestor(view).Transform(new Point(0, 0)).X;
            double inputTextX = boxX + box.Padding.Left + box.BorderThickness.Left;
            if (Math.Abs(inputTextX - textX) > 1.0)
            {
                throw new InvalidOperationException(
                    "input text x " + inputTextX.ToString("0.##") +
                    " vs row text x " + textX.ToString("0.##"));
            }
        }

        /// <summary>
        /// An edited row shows the borderless/transparent editor and no
        /// background band - not in dark, and not after the live switch to
        /// light (fix round 11, R1); rows that are not editing carry no fill
        /// either.
        /// </summary>
        private static void TodoInlineEditorIsBorderlessAndUnbanded()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;
            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);

            var editor = EditorBoxInList(view);
            if (editor.BorderThickness != new Thickness(0))
                throw new InvalidOperationException("the editor still has a border: " + editor.BorderThickness);
            var editorBackground = editor.Background as SolidColorBrush;
            if (editorBackground == null || editorBackground.Color.A != 0)
                throw new InvalidOperationException("the editor background is not transparent");

            AssertNoFill("the edited row shell [dark]", RowShell(view, 0));

            // The app's theme path is RefreshTheme -> UpdateUI -> Refresh, which
            // rebuilds the rows; the shell must stay unbanded under the new theme.
            ThemeManager.Initialize("light");
            view.Refresh();
            AssertNoFill("the edited row shell [light]", RowShell(view, 0));
            ThemeManager.Initialize("dark");

            var plainData = NewTodoData(out TodoItem plainItem);
            var plainView = new TodoWidget(plainData).CalendarView;
            LayoutView(plainView);
            AssertNoFill("a non-editing row shell", RowShell(plainView, 0));
        }

        /// <summary>
        /// Fix round 15 (R3): the folder widget's item-selection visual follows
        /// the widget's own colour. The panel shell (task 3.1: the chrome's
        /// Shell border, exposed as PanelShell) carries widget-local brush
        /// resources (loaded from the colour path before the first read), and
        /// a live colour change + RefreshTheme updates both; the app-level
        /// default fill stays the shared blue gradient fallback.
        /// </summary>
        private static void FolderSelectionFollowsTheWidgetColor()
        {
            ThemeManager.Initialize("dark");
            var data = new FolderData { Color = "#E8A33D" };
            var widget = new FolderWidget(data);
            // The constructor subscribes the Loaded handler but never runs the
            // colour path offscreen; the app reaches it through Loaded ->
            // RefreshTheme (the pin runs the same method explicitly).
            widget.RefreshTheme();

            var panel = widget.PanelShell;
            if (panel == null) throw new InvalidOperationException("panel shell not found");
            AssertSelectionResources("initial", panel, "#E8A33D");

            data.Color = "#3B82F6";
            widget.RefreshTheme();
            AssertSelectionResources("after the colour change", panel, "#3B82F6");

            var appDefault = Application.Current.Resources["Kobold.Brush.SelectionFill"] as LinearGradientBrush;
            if (appDefault == null)
            {
                throw new InvalidOperationException(
                    "the app-level Kobold.Brush.SelectionFill default is not a LinearGradientBrush");
            }
        }

        /// <summary>
        /// T6: a two-item root-mode folder for the title pins, with the list
        /// UpdateUI rebuilt handed back so the pins can flip IsSelected
        /// directly. The first entry carries a long file name (no .lnk, so
        /// the display name and Path.GetFileName agree).
        /// </summary>
        private static FolderWidget NewTitleProbeWidget(out List<DisplayItem> items)
        {
            ThemeManager.Initialize("dark");
            const string longName =
                "the quarterly budget spreadsheet for the whole year 2026 - final version.txt";
            var data = new FolderData { Name = "title-probe" };
            data.Items.Add(new WidgetItem(System.IO.Path.Combine(@"C:\kobold-title-probe", longName)));
            data.Items.Add(new WidgetItem(System.IO.Path.Combine(@"C:\kobold-title-probe", "second entry.txt")));

            var widget = new FolderWidget(data);
            widget.UpdateUI();

            var container = PrivateField<ItemsControl>(widget, "ItemsContainer");
            items = container.ItemsSource as List<DisplayItem>;
            if (items == null || items.Count != 2)
            {
                throw new InvalidOperationException("the title probe items were not built");
            }
            return widget;
        }

        /// <summary>
        /// T6: one selected entry shows its full name in the chrome title and
        /// its whole path in the tooltip.
        /// </summary>
        private static void FolderSingleSelectionShowsTheFullNameInTheTitle()
        {
            List<DisplayItem> items;
            var widget = NewTitleProbeWidget(out items);
            var chrome = ChromeOf(widget);

            items[0].IsSelected = true;
            FolderPrivateMethod("RefreshChromeTitle").Invoke(widget, null);

            string wanted = System.IO.Path.GetFileName(items[0].Path);
            if (chrome.Title != wanted)
            {
                throw new InvalidOperationException(
                    "the title is '" + chrome.Title + "', wanted the full name '" + wanted + "'");
            }
            if (!Equals(chrome.TitleText.ToolTip, items[0].Path))
            {
                throw new InvalidOperationException(
                    "the title tooltip is '" + chrome.TitleText.ToolTip +
                    "', wanted the path '" + items[0].Path + "'");
            }
        }

        /// <summary>
        /// T6: several selected entries collapse the title to the localized
        /// count and drop the tooltip.
        /// </summary>
        private static void FolderMultipleSelectionsShowTheCount()
        {
            List<DisplayItem> items;
            var widget = NewTitleProbeWidget(out items);
            var chrome = ChromeOf(widget);

            items[0].IsSelected = true;
            items[1].IsSelected = true;
            FolderPrivateMethod("RefreshChromeTitle").Invoke(widget, null);

            string wanted = Localization.Format("UI_SelectedCountFormat", 2);
            if (chrome.Title != wanted)
            {
                throw new InvalidOperationException(
                    "the title is '" + chrome.Title + "', wanted the count '" + wanted + "'");
            }
            if (chrome.TitleText.ToolTip != null)
            {
                throw new InvalidOperationException(
                    "the count title kept a tooltip: '" + chrome.TitleText.ToolTip + "'");
            }
        }

        /// <summary>
        /// T6: clearing the selection falls back to the widget's name (root
        /// mode) and clears the tooltip. The title is first taken through the
        /// selection branch, so the empty branch really runs on the way back
        /// (an early-return regression would keep the selected title).
        /// </summary>
        private static void FolderEmptySelectionKeepsTheWidgetName()
        {
            List<DisplayItem> items;
            var widget = NewTitleProbeWidget(out items);
            var chrome = ChromeOf(widget);

            items[0].IsSelected = true;
            FolderPrivateMethod("RefreshChromeTitle").Invoke(widget, null);
            if (chrome.Title == widget.Data.Name)
            {
                throw new InvalidOperationException("the selection title did not take over before the clear");
            }

            foreach (var item in items) item.IsSelected = false;
            FolderPrivateMethod("RefreshChromeTitle").Invoke(widget, null);

            if (chrome.Title != widget.Data.Name)
            {
                throw new InvalidOperationException(
                    "the empty selection's title is '" + chrome.Title +
                    "', wanted the widget name '" + widget.Data.Name + "'");
            }
            if (chrome.TitleText.ToolTip != null)
            {
                throw new InvalidOperationException(
                    "the empty selection kept a tooltip: '" + chrome.TitleText.ToolTip + "'");
            }
        }

        /// <summary>
        /// T7 (user spec 2026-10-13): the arrow keys walk the item grid
        /// through the window's tunnelling preview handler. Right moves the
        /// selection to the next entry and the chrome title follows it (the
        /// T6 refresh); at the grid's end the boundary press leaves the
        /// selection alone and stays unhandled so the scroller keeps it.
        /// Synthetic keyboard only - no pointer input.
        /// </summary>
        private static void FolderArrowKeysMoveTheSelectionAcrossTheGrid()
        {
            ThemeManager.Initialize("dark");
            var data = new FolderData { Name = "grid-probe" };
            for (int i = 0; i < 4; i++)
            {
                data.Items.Add(new WidgetItem(System.IO.Path.Combine(
                    @"C:\kobold-grid-probe", "entry " + i + ".txt")));
            }

            var widget = new FolderWidget(data);
            widget.UpdateUI();

            // Realize the grid like the adaptive-layout pins: the movement
            // reads the live column count off the UniformGrid.
            var root = (FrameworkElement)widget.Content;
            root.Measure(new Size(433, 285));
            root.Arrange(new Rect(0, 0, 433, 285));
            root.UpdateLayout();

            var container = PrivateField<ItemsControl>(widget, "ItemsContainer");
            var items = container.ItemsSource as List<DisplayItem>;
            if (items == null || items.Count != 4)
            {
                throw new InvalidOperationException("the grid probe items were not built");
            }

            var chrome = ChromeOf(widget);
            items[0].IsSelected = true;

            var moved = RaiseFolderKey(widget, Key.Right);
            if (!items[1].IsSelected || items[0].IsSelected)
            {
                throw new InvalidOperationException(
                    "Right did not move the selection from entry 0 to entry 1");
            }
            string wanted = System.IO.Path.GetFileName(items[1].Path);
            if (chrome.Title != wanted)
            {
                throw new InvalidOperationException(
                    "the title is '" + chrome.Title + "', wanted the moved entry's name '" + wanted + "'");
            }
            if (!moved.Handled)
            {
                throw new InvalidOperationException("the successful move did not handle the key");
            }

            // The last entry: Right must keep the selection and leave the
            // key to the scroller (no Handled).
            foreach (var item in items) item.IsSelected = false;
            items[3].IsSelected = true;
            var boundary = RaiseFolderKey(widget, Key.Right);
            if (!items[3].IsSelected || items[0].IsSelected || items[1].IsSelected || items[2].IsSelected)
            {
                throw new InvalidOperationException("Right at the last entry moved the selection");
            }
            if (boundary.Handled)
            {
                throw new InvalidOperationException("the boundary press was handled");
            }
        }

        /// <summary>
        /// Task A3.1: without persisted sizes the panel falls back to the
        /// engine's one-shot migration of the old per-widget settings (the
        /// sandbox config carries the default ItemScale 1.1). The old auto
        /// size formula is gone, so the composed size must equal the engine's
        /// migrate maths exactly for an empty panel, a full grid and an
        /// overflowing listing (which the engine gives a footer row instead
        /// of a scrollbar column).
        /// </summary>
        private static void FolderPanelSizeFallbackMatchesTheEngine()
        {
            ThemeManager.Initialize("dark");

            AssertFolderFallbackSize(0, 3, PanelRows.DefaultMaxRows, 270, 154, "empty");
            AssertFolderFallbackSize(12, 4, PanelRows.DefaultMaxRows, 354, 350, "full grid");
            AssertFolderFallbackSize(30, 2, PanelRows.MinAllowedRows, 186, 174, "overflowing");
        }

        private static void AssertFolderFallbackSize(int itemCount, int columns, int maxRows,
            double wantWidth, double wantHeight, string label)
        {
            var data = new FolderData { GridColumns = columns, MaxPanelRows = maxRows };
            for (int i = 0; i < itemCount; i++)
            {
                data.Items.Add(new WidgetItem
                {
                    Name = "item" + i,
                    Path = System.IO.Path.Combine(@"C:\kobold-size-probe", "item" + i + ".txt")
                });
            }

            var widget = new FolderWidget(data);
            widget.UpdateUI(); // publishes _lastItemCount for the fallback size

            var (width, height) = widget.GetPanelSize();
            if (width != wantWidth || height != wantHeight)
            {
                throw new InvalidOperationException(
                    label + ": the fallback panel " + width + "x" + height +
                    " != the engine's " + wantWidth + "x" + wantHeight);
            }
        }

        /// <summary>
        /// Task A3.2 (P3): the grid consumes the engine's column and icon-
        /// scale output for the panel's persisted size at the configured 1.1 -
        /// 433 wide holds four columns, the dynamic minimum 186 holds two -
        /// and the rendered row never exceeds the usable width (no horizontal
        /// clipping: 4 x 76 x 1.1 = 334.4 &lt;= 415, 2 x 76 x 1.1 = 167.2
        /// &lt;= 168).
        /// </summary>
        private static void FolderAdaptsColumnsAndScaleToItsSize()
        {
            ThemeManager.Initialize("dark");

            AssertFolderAdapts(433, 285, 4, 1.1, 431 - WidgetConstants.PADDING, "the 433-wide panel");
            AssertFolderAdapts(186, 154, 2, 1.1, 184 - WidgetConstants.PADDING, "the minimum panel");
        }

        private static void AssertFolderAdapts(double panelWidth, double panelHeight, int wantColumns,
            double wantScale, double usableWidth, string label)
        {
            var data = new FolderData { PanelWidth = panelWidth, PanelHeight = panelHeight };
            for (int i = 0; i < 6; i++)
            {
                data.Items.Add(new WidgetItem
                {
                    Name = "item" + i,
                    Path = System.IO.Path.Combine(@"C:\kobold-adapt-probe", "item" + i + ".txt")
                });
            }

            var widget = new FolderWidget(data);
            widget.UpdateUI();

            var root = (FrameworkElement)widget.Content;
            root.Measure(new Size(panelWidth, panelHeight));
            root.Arrange(new Rect(0, 0, panelWidth, panelHeight));
            root.UpdateLayout();

            var grid = FindDescendant<BindableUniformGrid>(root);
            if (grid == null) throw new InvalidOperationException(label + ": the items grid was not realized");
            if (grid.Columns != wantColumns)
            {
                throw new InvalidOperationException(
                    label + ": " + grid.Columns + " column(s), wanted " + wantColumns);
            }

            var container = PrivateField<ItemsControl>(widget, "ItemsContainer");
            var transform = container.LayoutTransform as ScaleTransform;
            if (transform == null)
            {
                throw new InvalidOperationException(label + ": the item scale transform is missing");
            }
            if (Math.Abs(transform.ScaleX - wantScale) > 1e-6)
            {
                throw new InvalidOperationException(
                    label + ": scale " + transform.ScaleX + ", wanted " + wantScale);
            }

            double rendered = grid.Columns * 76 * transform.ScaleX;
            if (rendered > usableWidth + 0.01)
            {
                throw new InvalidOperationException(
                    label + ": the grid renders " + rendered.ToString("0.###") +
                    " wide, more than the usable " + usableWidth.ToString("0.###"));
            }
        }

        /// <summary>
        /// Task A3.2: the folder menu carries exactly one submenu - the item-
        /// size picker (Menu_ItemSize) with the six Size_* entries - and the
        /// sandbox's 1.1 scale checks the Default entry. Clicking Extra Large
        /// writes 1.5 into the config; the finally block restores the sandbox
        /// default 1.1 (in memory only).
        /// </summary>
        private static void FolderMenuOffersTheItemSizeSubmenu()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("en");

            try
            {
                var widget = new FolderWidget(new FolderData());
                var menu = WidgetMenuBuilder.Build(widget, null);

                MenuItem submenu = null;
                int submenuCount = 0;
                foreach (var entry in menu.Items)
                {
                    var item = entry as MenuItem;
                    if (item == null || !item.HasItems) continue;
                    submenuCount++;
                    if (submenu == null) submenu = item;
                }
                if (submenuCount != 1)
                {
                    throw new InvalidOperationException(
                        "the folder menu has " + submenuCount + " submenu(s), wanted only the item-size one");
                }
                if ((submenu.Header as string) != Localization.Get("Menu_ItemSize"))
                {
                    throw new InvalidOperationException(
                        "the folder submenu header is '" + submenu.Header + "', wanted '" +
                        Localization.Get("Menu_ItemSize") + "'");
                }
                if (submenu.Items.Count != 6)
                {
                    throw new InvalidOperationException(
                        "the item-size submenu has " + submenu.Items.Count + " item(s), wanted 6");
                }

                string[] keys =
                {
                    "Size_Small", "Size_Normal", "Size_Default", "Size_Medium", "Size_Large", "Size_ExtraLarge"
                };
                for (int i = 0; i < keys.Length; i++)
                {
                    var item = submenu.Items[i] as MenuItem;
                    if (item == null || (item.Header as string) != Localization.Get(keys[i]))
                    {
                        throw new InvalidOperationException(
                            "item-size entry " + i + " is '" +
                            (item == null ? "<not a MenuItem>" : item.Header as string) +
                            "', wanted '" + Localization.Get(keys[i]) + "'");
                    }
                    bool wanted = keys[i] == "Size_Default"; // the sandbox runs at 1.1
                    if (item.IsChecked != wanted)
                    {
                        throw new InvalidOperationException(
                            "item-size entry " + i + " check state is " + item.IsChecked +
                            ", wanted " + wanted + " for the sandbox's 1.1");
                    }
                }

                ((MenuItem)submenu.Items[5]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (WidgetManager.Instance.Config.ItemScale != 1.5)
                {
                    throw new InvalidOperationException(
                        "clicking Extra Large left ItemScale at " +
                        WidgetManager.Instance.Config.ItemScale + ", wanted 1.5");
                }
            }
            finally
            {
                WidgetManager.Instance.Config.ItemScale = 1.1;
            }
        }

        /// <summary>
        /// Task F2 + smoke fix B: the four clipboard-aware menu builders hold
        /// the right entries with the right enabled state - browse item
        /// Copy/Cut (disabled while the widget is locked), the root item
        /// Copy, and the Paste entries: present and gated by the lock only
        /// when canPaste, absent entirely when the clipboard holds no files
        /// (the smoke spec change; they used to stay visible greyed out).
        /// Inspected offline; no menu is opened and the system clipboard is
        /// never touched.
        /// </summary>
        private static void FolderClipboardMenuEntries()
        {
            ThemeManager.Initialize("dark");

            string dir = IO.Path.Combine(IO.Path.GetTempPath(), "kobold-f2-clipboard-pin");
            if (IO.Directory.Exists(dir)) IO.Directory.Delete(dir, true);
            IO.Directory.CreateDirectory(dir);
            string filePath = IO.Path.Combine(dir, "a.txt");
            IO.File.WriteAllText(filePath, "x");

            var item = new DisplayItem { Name = "a.txt", Path = filePath, IsDirectory = false };
            var data = new FolderData();
            var widget = new FolderWidget(data);
            var selected = new List<DisplayItem> { item };

            var buildItemMenu = FolderPrivateMethod("BuildBrowseItemMenu");
            var itemMenu = (ContextMenu)buildItemMenu.Invoke(widget, new object[] { item, selected });
            AssertMenuItem(itemMenu, Localization.Get("Menu_Copy"), true, "the browse item menu");
            AssertMenuItem(itemMenu, Localization.Get("Menu_Cut"), true, "the browse item menu");

            data.IsLocked = true;
            var lockedMenu = (ContextMenu)buildItemMenu.Invoke(widget, new object[] { item, selected });
            AssertMenuItem(lockedMenu, Localization.Get("Menu_Copy"), false, "the locked browse item menu");
            AssertMenuItem(lockedMenu, Localization.Get("Menu_Cut"), false, "the locked browse item menu");
            data.IsLocked = false;

            var buildBackgroundMenu = FolderPrivateMethod("BuildBrowseBackgroundMenu");
            var pasteMenu = (ContextMenu)buildBackgroundMenu.Invoke(widget, new object[] { dir, true });
            AssertMenuItem(pasteMenu, Localization.Get("Menu_Paste"), true, "the browse background menu");
            var noPasteMenu = (ContextMenu)buildBackgroundMenu.Invoke(widget, new object[] { dir, false });
            AssertMenuItemAbsent(noPasteMenu, Localization.Get("Menu_Paste"), "the paste-less background menu");

            data.IsLocked = true;
            var lockedPasteMenu = (ContextMenu)buildBackgroundMenu.Invoke(widget, new object[] { dir, true });
            AssertMenuItem(lockedPasteMenu, Localization.Get("Menu_Paste"), false, "the locked browse background menu");
            data.IsLocked = false;

            var buildPanelMenu = FolderPrivateMethod("BuildPanelContextMenu");
            var panelMenu = (ContextMenu)buildPanelMenu.Invoke(widget, new object[] { true });
            AssertMenuItem(panelMenu, Localization.Get("Menu_Paste"), true, "the panel menu");
            var panelNoPaste = (ContextMenu)buildPanelMenu.Invoke(widget, new object[] { false });
            AssertMenuItemAbsent(panelNoPaste, Localization.Get("Menu_Paste"), "the paste-less panel menu");

            var rootMenu = (ContextMenu)FolderPrivateMethod("BuildRootItemMenu")
                .Invoke(widget, new object[] { item });
            AssertMenuItem(rootMenu, Localization.Get("Menu_Copy"), true, "the root item menu");
        }

        /// <summary>
        /// Task F5: the browse background menu offers the shared new-file
        /// entry - item[1] wears the same label as the root panel menu
        /// (Menu_NewFile), so both entry points read "New File".
        /// </summary>
        private static void FolderNewFileMenuUsesTheFileLabel()
        {
            ThemeManager.Initialize("dark");

            var widget = new FolderWidget(new FolderData());
            var menu = (ContextMenu)FolderPrivateMethod("BuildBrowseBackgroundMenu")
                .Invoke(widget, new object[] { IO.Path.GetTempPath(), true });

            var newFolder = menu.Items[0] as MenuItem;
            var newFile = menu.Items[1] as MenuItem;
            if (newFolder == null || !Equals(newFolder.Header, Localization.Get("Menu_NewFolder")))
            {
                throw new InvalidOperationException(
                    "browse menu item[0] is '" +
                    (newFolder == null ? "<not a menu item>" : newFolder.Header.ToString()) +
                    "', wanted '" + Localization.Get("Menu_NewFolder") + "'");
            }
            if (newFile == null || !Equals(newFile.Header, Localization.Get("Menu_NewFile")))
            {
                throw new InvalidOperationException(
                    "browse menu item[1] is '" +
                    (newFile == null ? "<not a menu item>" : newFile.Header.ToString()) +
                    "', wanted '" + Localization.Get("Menu_NewFile") + "'");
            }
        }

        /// <summary>
        /// Task F6: the browse header's back button is an item-level drop
        /// target - dropping a browse drag on it moves the items into the
        /// current folder's parent. The chrome moves the button out of the
        /// staging grid in the constructor, so the pin reads the runtime
        /// property offscreen (reusing F3's BrowseMove.CanDropInto pins for
        /// the drop verdict itself).
        /// </summary>
        private static void BrowseBackButtonAcceptsDrops()
        {
            var widget = new FolderWidget(new FolderData());
            var button = widget.FindName("BackButton") as Border;
            if (button == null) throw new InvalidOperationException("BackButton not found");
            if (!button.AllowDrop) throw new InvalidOperationException("the browse back button does not accept drops");
        }

        /// <summary>
        /// Task A3.2: raising the item size with an open panel below the new
        /// minimum grows it to the minimum (the one allowed automatic resize)
        /// and re-renders at the new scale; lowering it again never shrinks an
        /// open panel. A closed panel keeps its stored size - only
        /// GetPanelSize clamps it up on read.
        /// </summary>
        private static void FolderItemSizeChangeGrowsAnOpenPanel()
        {
            ThemeManager.Initialize("dark");

            try
            {
                var open = new FolderWidget(new FolderData { PanelWidth = 186, PanelHeight = 154 });
                EnsureRealDpi(open);
                open.Width = 186;
                open.Height = 154;
                open.Left = 100;
                open.Top = 100;
                SetPrivateBaseField(open, "_isExpanded", true);

                WidgetManager.Instance.Config.ItemScale = 1.5;
                open.ApplyItemScaleChange();

                if (Math.Abs(open.Width - 246) > 0.5 || Math.Abs(open.Height - 190) > 0.5)
                {
                    throw new InvalidOperationException(
                        "raising the scale left the open panel " + open.Width + "x" + open.Height +
                        ", wanted 246x190 at scale 1.5");
                }

                var container = PrivateField<ItemsControl>(open, "ItemsContainer");
                var transform = container.LayoutTransform as ScaleTransform;
                if (transform == null || Math.Abs(transform.ScaleX - 1.5) > 1e-6)
                {
                    throw new InvalidOperationException(
                        "the open panel renders at scale " +
                        (transform == null ? "<no transform>" : transform.ScaleX.ToString()) + ", wanted 1.5");
                }
                if (open.GridColumns != 2)
                {
                    throw new InvalidOperationException(
                        "the 246-wide panel holds " + open.GridColumns + " columns, wanted 2");
                }

                // Lowering the scale again only re-arranges - an open panel is
                // never shrunk automatically.
                WidgetManager.Instance.Config.ItemScale = 1.1;
                open.ApplyItemScaleChange();
                if (Math.Abs(open.Width - 246) > 0.5)
                {
                    throw new InvalidOperationException(
                        "lowering the scale shrank the open panel to " + open.Width + ", wanted 246");
                }

                // A closed panel keeps its stored size; only the read clamps.
                var closed = new FolderWidget(new FolderData { PanelWidth = 186, PanelHeight = 154 });
                closed.Width = 186;
                closed.Height = 154;
                WidgetManager.Instance.Config.ItemScale = 1.5;
                closed.ApplyItemScaleChange();
                if (Math.Abs(closed.Width - 186) > 0.5)
                {
                    throw new InvalidOperationException(
                        "the closed panel resized to " + closed.Width + ", wanted to stay 186");
                }
                var (closedWidth, closedHeight) = closed.GetPanelSize();
                if (Math.Abs(closedWidth - 246) > 0.5 || Math.Abs(closedHeight - 190) > 0.5)
                {
                    throw new InvalidOperationException(
                        "the closed panel read back " + closedWidth + "x" + closedHeight + ", wanted 246x190");
                }
            }
            finally
            {
                WidgetManager.Instance.Config.ItemScale = 1.1;
            }
        }

        /// <summary>
        /// Task A3.2 (P3): widening a folder panel never grows the icons -
        /// 433 and 435 wide both render at the configured 1.1 with the same
        /// 72 DIP item width - the extra width becomes spacing, measured as
        /// the items grid widening by more than a DIP.
        /// </summary>
        private static void FolderWideningKeepsTheIconSizeAndAddsSpacing()
        {
            ThemeManager.Initialize("dark");

            double narrowItemWidth = MeasureWidenedFolder(433, 300, out double narrowGridWidth);
            double wideItemWidth = MeasureWidenedFolder(435, 300, out double wideGridWidth);

            if (Math.Abs(narrowItemWidth - 72) > 0.5 || Math.Abs(wideItemWidth - 72) > 0.5)
            {
                throw new InvalidOperationException(
                    "the item width follows the panel width: " + narrowItemWidth.ToString("0.##") +
                    " and " + wideItemWidth.ToString("0.##") + ", wanted 72 for both");
            }
            if (wideGridWidth <= narrowGridWidth + 1.0)
            {
                throw new InvalidOperationException(
                    "widening 433 -> 435 did not add spacing: the grid widths are " +
                    narrowGridWidth.ToString("0.##") + " and " + wideGridWidth.ToString("0.##"));
            }
        }

        /// <summary>
        /// Offscreen half of the widening pin: lays a six-item folder out at
        /// the persisted size, returns the first item's width and the items
        /// grid's width.
        /// </summary>
        private static double MeasureWidenedFolder(double panelWidth, double panelHeight, out double gridWidth)
        {
            var data = new FolderData { PanelWidth = panelWidth, PanelHeight = panelHeight };
            for (int i = 0; i < 6; i++)
            {
                data.Items.Add(new WidgetItem
                {
                    Name = "item" + i,
                    Path = System.IO.Path.Combine(@"C:\kobold-widen-probe", "item" + i + ".txt")
                });
            }

            var widget = new FolderWidget(data);
            widget.UpdateUI();

            var root = (FrameworkElement)widget.Content;
            root.Measure(new Size(panelWidth, panelHeight));
            root.Arrange(new Rect(0, 0, panelWidth, panelHeight));
            root.UpdateLayout();

            var grid = FindDescendant<BindableUniformGrid>(root);
            if (grid == null) throw new InvalidOperationException("the items grid was not realized");
            if (grid.Columns != 4)
            {
                throw new InvalidOperationException(
                    "the " + panelWidth + "-wide panel holds " + grid.Columns + " columns, wanted 4");
            }

            var container = PrivateField<ItemsControl>(widget, "ItemsContainer");
            var transform = container.LayoutTransform as ScaleTransform;
            if (transform == null || Math.Abs(transform.ScaleX - 1.1) > 1e-6)
            {
                throw new InvalidOperationException(
                    "the " + panelWidth + "-wide panel renders at scale " +
                    (transform == null ? "<no transform>" : transform.ScaleX.ToString()) + ", wanted 1.1");
            }

            var presenter = grid.Children[0] as ContentPresenter;
            var itemRoot = presenter == null || VisualTreeHelper.GetChildrenCount(presenter) == 0
                ? null
                : VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement;
            if (itemRoot == null)
            {
                throw new InvalidOperationException("the first item container was not realized");
            }

            gridWidth = grid.ActualWidth;
            return itemRoot.ActualWidth;
        }

        /// <summary>
        /// Task A3.3: the folder's icon row pitch is independent of the panel
        /// height - a 400-tall and a 700-tall panel both lay the twelve items
        /// (4 columns) out in three 89-DIP rows (85 border + 2x2 margin),
        /// because the items grid no longer stretches vertically; the extra
        /// height stays below the grid.
        /// </summary>
        private static void FolderRowPitchIsIndependentOfThePanelHeight()
        {
            ThemeManager.Initialize("dark");

            var shortPanel = MeasureFolderRowPitch(400);
            var tallPanel = MeasureFolderRowPitch(700);

            if (Math.Abs(shortPanel.ActualHeight - tallPanel.ActualHeight) > 0.01)
            {
                throw new InvalidOperationException(
                    "the grid height follows the panel height: 400 -> " +
                    shortPanel.ActualHeight.ToString("0.##") + ", 700 -> " +
                    tallPanel.ActualHeight.ToString("0.##") + ", wanted 267 for both");
            }

            AssertFolderRowPitch(shortPanel, "the 400-tall panel");
            AssertFolderRowPitch(tallPanel, "the 700-tall panel");
        }

        private static void AssertFolderRowPitch(BindableUniformGrid grid, string label)
        {
            if (Math.Abs(grid.ActualHeight - 267) > 0.01)
            {
                throw new InvalidOperationException(
                    label + ": the grid is " + grid.ActualHeight.ToString("0.##") +
                    " tall, wanted 267 (3 rows x 89)");
            }

            var firstRow = grid.Children[0] as FrameworkElement;
            if (firstRow == null || Math.Abs(firstRow.ActualHeight - 89) > 0.01)
            {
                throw new InvalidOperationException(
                    label + ": the first row is " +
                    (firstRow == null ? "<not realized>" : firstRow.ActualHeight.ToString("0.##")) +
                    " tall, wanted 89");
            }

            var secondRow = grid.Children[4] as FrameworkElement;
            if (secondRow == null)
            {
                throw new InvalidOperationException(label + ": the second row was not realized");
            }
            double pitch = secondRow.TransformToAncestor(grid).Transform(new Point(0, 0)).Y;
            if (Math.Abs(pitch - 89) > 0.01)
            {
                throw new InvalidOperationException(
                    label + ": the second row starts at Y " + pitch.ToString("0.##") +
                    ", wanted 89 (one 89-DIP row pitch)");
            }
        }

        /// <summary>
        /// Offscreen half of the row-pitch pin: lays a twelve-item folder out
        /// at 433 wide and the persisted panel height, returns the items grid.
        /// </summary>
        private static BindableUniformGrid MeasureFolderRowPitch(double panelHeight)
        {
            var data = new FolderData { PanelWidth = 433, PanelHeight = panelHeight };
            for (int i = 0; i < 12; i++)
            {
                data.Items.Add(new WidgetItem
                {
                    Name = "item" + i,
                    Path = System.IO.Path.Combine(@"C:\kobold-row-pitch-probe", "item" + i + ".txt")
                });
            }

            var widget = new FolderWidget(data);
            widget.UpdateUI();

            var root = (FrameworkElement)widget.Content;
            root.Measure(new Size(433, panelHeight));
            root.Arrange(new Rect(0, 0, 433, panelHeight));
            root.UpdateLayout();

            var grid = FindDescendant<BindableUniformGrid>(root);
            if (grid == null) throw new InvalidOperationException("the items grid was not realized");
            if (grid.Children.Count != 12)
            {
                throw new InvalidOperationException(
                    "the grid realized " + grid.Children.Count + " container(s), wanted 12");
            }
            return grid;
        }

        /// <summary>
        /// Task A3.2: during a resize drag the panel re-arranges against the
        /// live window size - the minimum 186 wide holds two columns; a
        /// 300 DIP wider drag (486 DIP, content 484, usable 468) re-adapts to
        /// five (468/84 = 5.57). The open flag is set reflectively, so no
        /// window is ever shown.
        /// </summary>
        private static void FolderReadaptsDuringResize()
        {
            ThemeManager.Initialize("dark");
            var data = new FolderData { PanelWidth = 186, PanelHeight = 154 };
            var widget = new FolderWidget(data);
            EnsureRealDpi(widget);
            if (widget.GridColumns != 2)
            {
                throw new InvalidOperationException(
                    "the closed minimum panel holds " + widget.GridColumns + " columns, wanted 2");
            }

            // Offscreen stand-in for the open state (ShowPanel sets the same
            // flag after sizing the window): the live path reads Width/Height.
            widget.Width = 186;
            widget.Height = 154;
            widget.Left = 100;
            widget.Top = 100;
            SetPrivateBaseField(widget, "_isExpanded", true);

            double scale = VisualTreeHelper.GetDpi(widget).DpiScaleX;
            InvokeResizeStarted(widget, WidgetResizeEdge.BottomRight, new Point(100, 100));
            PrivateBaseMethod("OnResizeMoved").Invoke(widget,
                new object[] { new Point(100 + 300 * scale, 100) });

            if (Math.Abs(widget.Width - 486) > 0.5)
            {
                throw new InvalidOperationException(
                    "the drag made the panel " + widget.Width + " wide, wanted 486");
            }
            if (widget.GridColumns != 5)
            {
                throw new InvalidOperationException(
                    "the widened panel holds " + widget.GridColumns +
                    " columns, wanted 5 (content 484 -> usable 468)");
            }

            PrivateBaseMethod("OnResizeEnded").Invoke(widget,
                new object[] { new Point(100 + 300 * scale, 100) });
        }

        private static T FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var nested = FindDescendant<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private static void AssertSelectionResources(string label, FrameworkElement panel, string hex)
        {
            Color baseColor = Utils.HexToColor(hex);

            var border = panel.Resources["Kobold.Brush.SelectionBorder"] as SolidColorBrush;
            if (border == null || border.Color != baseColor)
            {
                throw new InvalidOperationException(
                    label + ": SelectionBorder is " +
                    (border == null ? "missing/not a solid brush" : Describe(border.Color)) +
                    ", wanted the widget colour " + Describe(baseColor));
            }

            var fill = panel.Resources["Kobold.Brush.SelectionFill"] as LinearGradientBrush;
            if (fill == null || fill.GradientStops.Count != 2)
            {
                throw new InvalidOperationException(
                    label + ": SelectionFill is " +
                    (fill == null ? "missing/not a linear gradient" : fill.GradientStops.Count + " stop(s)"));
            }
            AssertGradientStop(label, fill.GradientStops[0], Color.FromArgb(0x15, baseColor.R, baseColor.G, baseColor.B), 0);
            AssertGradientStop(label, fill.GradientStops[1], Color.FromArgb(0x25, baseColor.R, baseColor.G, baseColor.B), 1);
        }

        private static void AssertGradientStop(string label, GradientStop stop, Color wanted, double offset)
        {
            if (stop.Color != wanted || Math.Abs(stop.Offset - offset) > 0.001)
            {
                throw new InvalidOperationException(
                    label + ": gradient stop " + stop.Offset.ToString("0.##") + " is " +
                    Describe(stop.Color) + ", wanted " + Describe(wanted) + " at " + offset.ToString("0.##"));
            }
        }

        /// <summary>The transparent row shell Border at a list index, if the row is one.</summary>
        private static Border RowShell(TodoCalendarView view, int index)
        {
            var panel = view.FindName("ItemsPanel") as Panel;
            if (panel == null || panel.Children.Count <= index) return null;
            return panel.Children[index] as Border;
        }

        private static bool ContainsText(DependencyObject root, string text)
        {
            var block = root as TextBlock;
            if (block != null && block.Text == text) return true;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                if (ContainsText(VisualTreeHelper.GetChild(root, i), text)) return true;
            }
            return false;
        }

        private static FrameworkElement RowAt(TodoCalendarView view, int index)
        {
            var panel = view.FindName("ItemsPanel") as Panel;
            if (panel == null || panel.Children.Count <= index)
                throw new InvalidOperationException("row " + index + " not found");

            var row = panel.Children[index] as FrameworkElement;
            if (row == null) throw new InvalidOperationException("row " + index + " is not a FrameworkElement");
            return row;
        }

        /// <summary>The content grid inside a row shell (checkbox/text columns).</summary>
        private static Grid RowContent(FrameworkElement row)
        {
            var shell = row as Border;
            var grid = shell == null ? null : shell.Child as Grid;
            if (grid == null) throw new InvalidOperationException("row content grid not found");
            return grid;
        }

        private static string HeaderOf(object menuEntry)
        {
            var item = menuEntry as MenuItem;
            return item == null ? null : item.Header as string;
        }

        private static void ClickMenuItem(FrameworkElement row, int index)
        {
            if (row.ContextMenu == null)
                throw new InvalidOperationException("the row has no menu");
            var item = row.ContextMenu.Items[index] as MenuItem;
            if (item == null) throw new InvalidOperationException("menu entry " + index + " is not a MenuItem");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }

        private static bool HasGlyph(DependencyObject root, string glyph)
        {
            var text = root as TextBlock;
            if (text != null && text.Text == glyph) return true;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                if (HasGlyph(VisualTreeHelper.GetChild(root, i), glyph)) return true;
            }
            return false;
        }

        private static TextBox EditorBoxInList(TodoCalendarView view)
        {
            var box = EditorBoxInListOrNull(view);
            if (box == null) throw new InvalidOperationException("inline editor box not found");
            return box;
        }

        private static TextBox EditorBoxInListOrNull(TodoCalendarView view)
        {
            return FirstDescendant<TextBox>(view.FindName("ItemsPanel") as DependencyObject);
        }

        private static void RaiseLostFocus(TextBox box)
        {
            box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, box, null)
            {
                RoutedEvent = UIElement.LostKeyboardFocusEvent
            });
        }

        /// <summary>Key input needs a real presentation source; EnsureHandle creates the HwndSource.</summary>
        private static void RaiseKey(Window window, TodoCalendarView view, TextBox box, Key key)
        {
            EnsureRealDpi(window);
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            PresentationSource source = System.Windows.Interop.HwndSource.FromHwnd(handle);
            if (source == null) throw new InvalidOperationException("no presentation source for key input");

            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = UIElement.KeyDownEvent
            });
        }

        /// <summary>
        /// The tunnelling half of the same key input (polish round T3): the
        /// inline editor intercepts Backspace at the text start in
        /// PreviewKeyDown, because the TextBox's own class handler consumes
        /// Key.Back before any bubbling KeyDown handler runs.
        /// </summary>
        private static void RaisePreviewKey(Window window, TodoCalendarView view, TextBox box, Key key)
        {
            EnsureRealDpi(window);
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            PresentationSource source = System.Windows.Interop.HwndSource.FromHwnd(handle);
            if (source == null) throw new InvalidOperationException("no presentation source for key input");

            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent
            });
        }

        /// <summary>
        /// T7: folder key input - the same real presentation source as
        /// <see cref="RaiseKey"/> (EnsureHandle creates the HwndSource), but
        /// raised on the widget window itself as the tunnelling
        /// PreviewKeyDown the folder's keyboard handler listens for. Returns
        /// the args so the pin can read their Handled state.
        /// </summary>
        private static KeyEventArgs RaiseFolderKey(Window window, Key key)
        {
            // The folder keeps the arrows for a focused text box (the rename
            // editor); the earlier key pins can leave one focused, so the
            // real card's premise - focus outside any editor - is set here.
            Keyboard.ClearFocus();

            EnsureRealDpi(window);
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            PresentationSource source = System.Windows.Interop.HwndSource.FromHwnd(handle);
            if (source == null) throw new InvalidOperationException("no presentation source for key input");

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            window.RaiseEvent(args);
            return args;
        }

        /// <summary>
        /// Runs the deferred editor commit the blur queued, without pumping the
        /// app dispatcher (a pump would run the dormant App.OnStartup op). The
        /// production code schedules the same delegate at Background priority.
        /// </summary>
        private static void RunPendingEditorCommit(TodoCalendarView view)
        {
            var pending = PendingEditorCommit(view);
            if (pending == null) throw new InvalidOperationException("no deferred editor commit was pending");
            pending.Invoke();
        }

        private static bool RunPendingEditorCommitIfAny(TodoCalendarView view)
        {
            var pending = PendingEditorCommit(view);
            if (pending == null) return false;
            pending.Invoke();
            return true;
        }

        private static Action PendingEditorCommit(TodoCalendarView view)
        {
            var field = typeof(TodoCalendarView).GetField("_pendingEditorCommit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : (Action)field.GetValue(view);
        }

        private static double FirstFontSize(DependencyObject root)
        {
            var text = FirstDescendant<TextBlock>(root);
            return text == null ? 0 : text.FontSize;
        }

        /// <summary>Runs the pill's own reveal click path (offscreen: Focus() has no source).</summary>
        private static void RevealAddBox(TodoCalendarView view)
        {
            var pill = view.FindName("AddPill");
            var handler = typeof(TodoCalendarView).GetMethod("AddPill_MouseLeftButtonDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (pill == null || handler == null)
            {
                throw new InvalidOperationException("AddPill / AddPill_MouseLeftButtonDown not found");
            }

            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent
            };
            handler.Invoke(view, new object[] { pill, args });
        }

        /// <summary>Opens the inline item editor through the view's own path and pins its geometry.</summary>
        private static void TodoItemEditorIsNotClipped()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;

            InvokeViewEdit(view, "StartItemEdit", item.Id);
            LayoutView(view);
            AssertItemsPanelEditorNotClipped("item editor", view);
        }

        /// <summary>Same pin for the child adder that BuildEditor creates.</summary>
        private static void TodoChildAdderIsNotClipped()
        {
            var data = NewTodoData(out TodoItem item);
            var view = new TodoWidget(data).CalendarView;

            InvokeViewEdit(view, "StartChildAdd", item.Id);
            LayoutView(view);
            AssertItemsPanelEditorNotClipped("child adder", view);
        }

        private static TodoData NewTodoData(out TodoItem item)
        {
            ThemeManager.Initialize("dark");
            DateTime today = DateTime.Today;
            var data = new TodoData { SelectedDate = today };
            item = TodoListOps.AddItem(data, "Buy milk", today);
            return data;
        }

        private static void InvokeViewEdit(TodoCalendarView view, string method, string id)
        {
            var start = typeof(TodoCalendarView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (start == null) throw new InvalidOperationException(method + " not found on TodoCalendarView");
            object[] args = start.GetParameters().Length == 1
                ? new object[] { id }
                : new object[] { id, default(Point) };
            start.Invoke(view, args);
        }

        private static void InvokeViewEditAt(TodoCalendarView view, string method, string id, Point screenPoint)
        {
            var start = typeof(TodoCalendarView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(Point) }, null);
            if (start == null)
            {
                throw new InvalidOperationException(method + " does not take a click point (no caret placement)");
            }
            start.Invoke(view, new object[] { id, screenPoint });
        }

        /// <summary>
        /// Calls the view's multi-step undo through reflection (B3; the RED run
        /// has no such method yet; afterwards it is public, reflection keeps the
        /// pin stable either way).
        /// </summary>
        private static bool InvokeTryUndo(TodoCalendarView view)
        {
            var method = typeof(TodoCalendarView).GetMethod("TryUndo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("TryUndo not found on TodoCalendarView");
            return (bool)method.Invoke(view, null);
        }

        private static void AssertItemsPanelEditorNotClipped(string label, TodoCalendarView view)
        {
            var panel = view.FindName("ItemsPanel") as DependencyObject;
            var box = FirstDescendant<TextBox>(panel);
            if (box == null) throw new InvalidOperationException(label + " box not found");
            AssertTextBoxNotClipped(label, box);
        }

        private static void AssertTextBoxNotClipped(string label, TextBox box)
        {
            // Real line height for the box's own font (with CJK fallback in the
            // sample), plus a 2px safety margin: the buggy add box measured
            // content 16 vs 15.24 line (zh-CN fallback here) and clipped; the
            // fixed layout measures 22.
            var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
            var formatted = new FormattedText("Ag全脂", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, box.FontSize, Brushes.Black, 1.0);
            double lineHeight = formatted.Height;
            double content = box.ActualHeight - box.BorderThickness.Top - box.BorderThickness.Bottom
                             - box.Padding.Top - box.Padding.Bottom;
            const double safety = 2.0;

            if (content < lineHeight + safety)
            {
                throw new InvalidOperationException(
                    label + ": content " + content.ToString("0.##") + " < line " + lineHeight.ToString("0.##") +
                    " + " + safety.ToString("0.##") + " (ActualHeight " + box.ActualHeight +
                    ", padding " + box.Padding + ", border " + box.BorderThickness + ")");
            }
        }

        private static int CountPaths(DependencyObject root)
        {
            int found = root is Path ? 1 : 0;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                found += CountPaths(VisualTreeHelper.GetChild(root, i));
            }
            return found;
        }

        private static int CountPathsStroked(DependencyObject root, Color stroke)
        {
            int found = 0;
            var path = root as Path;
            if (path != null)
            {
                var brush = path.Stroke as SolidColorBrush;
                if (brush != null && brush.Color == stroke) found++;
            }
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                found += CountPathsStroked(VisualTreeHelper.GetChild(root, i), stroke);
            }
            return found;
        }

        private static bool HasRotateTransform(DependencyObject root, double angle)
        {
            var element = root as FrameworkElement;
            if (element != null && MatchesRotate(element.RenderTransform, angle)) return true;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                if (HasRotateTransform(VisualTreeHelper.GetChild(root, i), angle)) return true;
            }
            return false;
        }

        private static bool MatchesRotate(Transform transform, double angle)
        {
            var rotate = transform as RotateTransform;
            if (rotate != null) return Math.Abs(rotate.Angle - angle) < 0.001;

            var group = transform as TransformGroup;
            if (group != null)
            {
                foreach (var child in group.Children)
                {
                    if (MatchesRotate(child, angle)) return true;
                }
            }
            return false;
        }

        private static T FirstDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var match = child as T;
                if (match != null) return match;
                var deeper = FirstDescendant<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>Lays the view out at its real width so ActualHeight/Padding are settled.</summary>
        private static void LayoutView(TodoCalendarView view)
        {
            view.Measure(new Size(340, double.PositiveInfinity));
            view.Arrange(new Rect(0, 0, 340, view.DesiredSize.Height));
            view.UpdateLayout();
        }

        private static Color FirstGradientStop(WidgetChrome chrome)
        {
            var brush = chrome.Shell.Background as LinearGradientBrush;
            if (brush == null || brush.GradientStops.Count == 0)
            {
                throw new InvalidOperationException("shell backdrop is not a gradient");
            }
            return brush.GradientStops[0].Color;
        }

        private static string Describe(Color color)
        {
            return "#" + color.A.ToString("X2") + color.R.ToString("X2") +
                   color.G.ToString("X2") + color.B.ToString("X2");
        }

        private static MethodInfo PrivateBaseMethod(string name)
        {
            var method = typeof(WidgetWindowBase).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException(name + " not found on WidgetWindowBase");
            return method;
        }

        private static T PrivateField<T>(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException(name + " field not found");
            return (T)field.GetValue(target);
        }

        /// <summary>
        /// The widget's attached chrome (a protected base property), for pins
        /// that inspect the shared header.
        /// </summary>
        private static WidgetChrome ChromeOf(WidgetWindowBase widget)
        {
            var property = typeof(WidgetWindowBase).GetProperty("Chrome",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var chrome = property == null ? null : (WidgetChrome)property.GetValue(widget);
            if (chrome == null) throw new InvalidOperationException("the widget chrome is not attached");
            return chrome;
        }

        private static void SetPrivateBaseField<T>(object target, string name, T value)
        {
            var field = typeof(WidgetWindowBase).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException(name + " field not found on WidgetWindowBase");
            field.SetValue(target, value);
        }

        #region Task 3.3 icon-style pins

        private static Canvas RenderIcon(IIconRenderer renderer, string color)
        {
            var canvas = new Canvas { Width = 64, Height = 64 };
            renderer.Render(canvas, color);
            return canvas;
        }

        private static Shape FindShape(DependencyObject root, Func<Shape, bool> match)
        {
            var shape = root as Shape;
            if (shape != null && match(shape)) return shape;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindShape(VisualTreeHelper.GetChild(root, i), match);
                if (found != null) return found;
            }
            return null;
        }

        private static void FolderFactoryDispatchesStyles()
        {
            if (!(IconRendererFactory.Create("classic") is ClassicIconRenderer))
                throw new InvalidOperationException("Create(classic) is not a ClassicIconRenderer");
            if (!(IconRendererFactory.Create("handdrawn") is HandDrawnIconRenderer))
                throw new InvalidOperationException("Create(handdrawn) is not a HandDrawnIconRenderer");
        }

        private static void FolderFactoryFallsBackToClassic()
        {
            if (!(IconRendererFactory.Create("modern") is ClassicIconRenderer))
                throw new InvalidOperationException("the retired modern style did not fall back to classic");
            if (!(IconRendererFactory.Create("nonsense") is ClassicIconRenderer))
                throw new InvalidOperationException("an unknown style did not fall back to classic");
        }

        private static void HandDrawnFolderUsesWidgetColourAndInk()
        {
            const string hex = "#EF4444";
            Color wanted = Utils.HexToColor(hex);

            ThemeManager.Initialize("light");
            var light = RenderIcon(new HandDrawnIconRenderer(), hex);
            Shape lightFront = FindShape(light,
                s => s.Fill is SolidColorBrush fill && fill.Color == wanted);
            if (lightFront == null)
                throw new InvalidOperationException("no hand-drawn front panel carries the widget colour");
            var lightStroke = lightFront.Stroke as SolidColorBrush;
            if (lightStroke == null)
                throw new InvalidOperationException("the hand-drawn front panel has no solid outline");

            ThemeManager.Initialize("dark");
            var dark = RenderIcon(new HandDrawnIconRenderer(), hex);
            Shape darkFront = FindShape(dark,
                s => s.Fill is SolidColorBrush fill && fill.Color == wanted);
            var darkStroke = darkFront == null ? null : darkFront.Stroke as SolidColorBrush;
            if (darkStroke == null)
                throw new InvalidOperationException("the dark-theme front panel has no solid outline");
            if (lightStroke.Color == darkStroke.Color)
            {
                throw new InvalidOperationException(
                    "the outline is " + Describe(lightStroke.Color) + " under both themes");
            }

            Shape highlight = FindShape(light, s =>
            {
                var stroke = s.Stroke as SolidColorBrush;
                return stroke != null && stroke.Color.R == 255 && stroke.Color.G == 255 &&
                       stroke.Color.B == 255 && stroke.Color.A < 255;
            });
            if (highlight == null)
                throw new InvalidOperationException("the hand-drawn folder has no translucent white highlight");
        }

        private static void ByKeyFactoryDispatchesTodo()
        {
            if (!(IconRendererFactory.CreateByKey("todo", "handdrawn") is HandDrawnTodoIconRenderer))
                throw new InvalidOperationException("CreateByKey(todo, handdrawn) is not a HandDrawnTodoIconRenderer");
            if (!(IconRendererFactory.CreateByKey("todo", "classic") is ClassicTodoIconRenderer))
                throw new InvalidOperationException("CreateByKey(todo, classic) is not a ClassicTodoIconRenderer");
        }

        private static void ByKeyFactoryFallsBackToFolder()
        {
            if (!(IconRendererFactory.CreateByKey("folder", "classic") is ClassicIconRenderer))
                throw new InvalidOperationException("CreateByKey(folder, classic) is not a ClassicIconRenderer");
            if (!(IconRendererFactory.CreateByKey("nonsense", "classic") is ClassicIconRenderer))
                throw new InvalidOperationException("an unknown icon key did not fall back to the folder renderer");
            if (!(IconRendererFactory.CreateByKey(null, "handdrawn") is HandDrawnIconRenderer))
                throw new InvalidOperationException("a null icon key did not fall back to the hand-drawn folder renderer");
        }

        private static void TodoFactoryDispatchesStyles()
        {
            if (!(IconRendererFactory.CreateTodo("classic") is ClassicTodoIconRenderer))
                throw new InvalidOperationException("CreateTodo(classic) is not a ClassicTodoIconRenderer");
            if (!(IconRendererFactory.CreateTodo("handdrawn") is HandDrawnTodoIconRenderer))
                throw new InvalidOperationException("CreateTodo(handdrawn) is not a HandDrawnTodoIconRenderer");
        }

        private static void TodoFactoryFallsBackToClassic()
        {
            if (!(IconRendererFactory.CreateTodo("rounded") is ClassicTodoIconRenderer))
                throw new InvalidOperationException("the retired rounded todo style did not fall back to classic");
            if (!(IconRendererFactory.CreateTodo("nonsense") is ClassicTodoIconRenderer))
                throw new InvalidOperationException("an unknown todo style did not fall back to classic");
        }

        private static void ClassicTodoUsesWidgetColour()
        {
            ThemeManager.Initialize("dark");
            const string hex = "#3B82F6";
            Color wanted = Utils.HexToColor(hex);
            var canvas = RenderIcon(new ClassicTodoIconRenderer(), hex);

            var header = FindShape(canvas, s => s is Path && s.Fill is LinearGradientBrush) as Path;
            var gradient = header == null ? null : header.Fill as LinearGradientBrush;
            if (gradient == null || gradient.GradientStops.Count < 2)
                throw new InvalidOperationException("the classic todo header carries no accent gradient");
            if (gradient.GradientStops[0].Color != wanted)
            {
                throw new InvalidOperationException(
                    "the header top is " + Describe(gradient.GradientStops[0].Color) +
                    ", wanted the widget colour " + Describe(wanted));
            }
            Color darker = Utils.DarkenColor(wanted, 0.2);
            Color headerBottom = gradient.GradientStops[gradient.GradientStops.Count - 1].Color;
            if (headerBottom != darker)
            {
                throw new InvalidOperationException(
                    "the header bottom is " + Describe(headerBottom) + ", wanted " + Describe(darker));
            }

            Shape block = FindShape(canvas, s =>
            {
                var rect = s as Rectangle;
                if (rect == null || rect.Width != 22 || rect.Height != 10) return false;
                var fill = rect.Fill as SolidColorBrush;
                return fill != null && fill.Color.R == wanted.R && fill.Color.G == wanted.G &&
                       fill.Color.B == wanted.B && fill.Color.A == 0xCC;
            });
            if (block == null)
                throw new InvalidOperationException("no classic todo date block carries the widget colour at 0.8 alpha");
        }

        private static void HandDrawnTodoUsesWidgetColour()
        {
            ThemeManager.Initialize("dark");
            Color wanted = Utils.HexToColor("#8B5CF6");
            var canvas = RenderIcon(new HandDrawnTodoIconRenderer(), "#8B5CF6");

            Shape accent = FindShape(canvas, s =>
            {
                var stroke = s.Stroke as SolidColorBrush;
                return stroke != null && stroke.Color == wanted;
            });
            if (accent == null)
                throw new InvalidOperationException("no hand-drawn todo accent stroke carries the widget colour");
        }

        private static void HandDrawnTodoPaperFollowsTheme()
        {
            const string hex = "#EF4444";
            ThemeManager.Initialize("light");
            Color? light = HandDrawnTodoPaperColour(RenderIcon(new HandDrawnTodoIconRenderer(), hex));

            ThemeManager.Initialize("dark");
            Color? dark = HandDrawnTodoPaperColour(RenderIcon(new HandDrawnTodoIconRenderer(), hex));

            if (light == null || dark == null)
                throw new InvalidOperationException("the hand-drawn todo paper path was not found");
            if (light.Value == dark.Value)
            {
                throw new InvalidOperationException(
                    "the paper is " + Describe(light.Value) + " under both themes");
            }
        }

        private static Color? HandDrawnTodoPaperColour(Canvas root)
        {
            Shape paper = FindShape(root, s => s is Path && s.Fill is SolidColorBrush && s.Stroke is SolidColorBrush);
            return paper == null ? (Color?)null : ((SolidColorBrush)paper.Fill).Color;
        }

        private static void NoteFactoryDispatchesStyles()
        {
            if (!(IconRendererFactory.CreateNote("classic") is ClassicNoteIconRenderer))
                throw new InvalidOperationException("CreateNote(classic) is not a ClassicNoteIconRenderer");
            if (!(IconRendererFactory.CreateNote("handdrawn") is HandDrawnNoteIconRenderer))
                throw new InvalidOperationException("CreateNote(handdrawn) is not a HandDrawnNoteIconRenderer");
            if (!(IconRendererFactory.CreateNote("rounded") is ClassicNoteIconRenderer))
                throw new InvalidOperationException("the retired rounded note style did not fall back to classic");
        }

        private static void ByKeyFactoryDispatchesNote()
        {
            if (!(IconRendererFactory.CreateByKey("note", "classic") is ClassicNoteIconRenderer))
                throw new InvalidOperationException("CreateByKey(note, classic) is not a ClassicNoteIconRenderer");
            if (!(IconRendererFactory.CreateByKey("note", "handdrawn") is HandDrawnNoteIconRenderer))
                throw new InvalidOperationException("CreateByKey(note, handdrawn) is not a HandDrawnNoteIconRenderer");
        }

        private static void ClassicNoteUsesWidgetColour()
        {
            ThemeManager.Initialize("dark");
            const string hex = "#3B82F6";
            Color wanted = Utils.HexToColor(hex);
            var canvas = RenderIcon(new ClassicNoteIconRenderer(), hex);

            Shape accent = FindShape(canvas, s =>
            {
                var fill = s.Fill as SolidColorBrush;
                return fill != null && fill.Color == wanted;
            });
            if (accent == null)
            {
                throw new InvalidOperationException(
                    "no classic note accent carries the widget colour " + Describe(wanted));
            }
        }

        private static void HandDrawnNoteUsesWidgetColourAndInk()
        {
            const string hex = "#EF4444";
            Color wanted = Utils.HexToColor(hex);

            ThemeManager.Initialize("light");
            var light = RenderIcon(new HandDrawnNoteIconRenderer(), hex);
            Shape accent = FindShape(light, s =>
            {
                var fill = s.Fill as SolidColorBrush;
                if (fill != null && fill.Color == wanted) return true;
                var stroke = s.Stroke as SolidColorBrush;
                return stroke != null && stroke.Color == wanted;
            });
            if (accent == null)
                throw new InvalidOperationException("no hand-drawn note accent carries the widget colour");

            ThemeManager.Initialize("dark");
            var dark = RenderIcon(new HandDrawnNoteIconRenderer(), hex);

            Color? lightPaper = HandDrawnNotePaperColour(light);
            Color? darkPaper = HandDrawnNotePaperColour(dark);
            if (lightPaper == null || darkPaper == null)
                throw new InvalidOperationException("the hand-drawn note paper path was not found");
            if (lightPaper.Value == darkPaper.Value)
            {
                throw new InvalidOperationException(
                    "the paper is " + Describe(lightPaper.Value) + " under both themes");
            }
        }

        private static Color? HandDrawnNotePaperColour(Canvas root)
        {
            Shape paper = FindShape(root, s => s is Path && s.Fill is SolidColorBrush && s.Stroke is SolidColorBrush);
            return paper == null ? (Color?)null : ((SolidColorBrush)paper.Fill).Color;
        }

        private static void HandDrawnNoteGeometryDiffersFromClassic()
        {
            ThemeManager.Initialize("dark");
            const string hex = "#EF4444";
            var classic = RenderIcon(new ClassicNoteIconRenderer(), hex);
            var handDrawn = RenderIcon(new HandDrawnNoteIconRenderer(), hex);

            if (handDrawn.Children.Count == 0)
                throw new InvalidOperationException("the hand-drawn note draws nothing");

            string handFingerprint = IconFingerprint(handDrawn);
            if (handFingerprint.IndexOf("|d=", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("the hand-drawn note has no baked path geometry");
            if (IconFingerprint(classic) == handFingerprint)
                throw new InvalidOperationException("the hand-drawn note geometry matches classic");
            if (classic.Children.Count == handDrawn.Children.Count)
                throw new InvalidOperationException("both note styles draw the same number of child elements");
        }

        private static void IslandFolderTileRefreshesWithStyle()
        {
            ThemeManager.Initialize("dark");
            var island = new IslandWindow();
            string original = WidgetManager.Instance.Config.IconStyle;
            try
            {
                island.SetWidgets(new WidgetData[] { new FolderData { Name = "style-check", Color = "#EF4444" } });
                // Start from a known style: the persisted config may already pick hand-drawn.
                WidgetManager.Instance.Config.IconStyle = "classic";
                island.RefreshIconStyle();
                string before = IconFingerprint(TileIconCanvas(island));

                WidgetManager.Instance.Config.IconStyle = "handdrawn";
                island.RefreshIconStyle();
                string after = IconFingerprint(TileIconCanvas(island));
                if (before == after)
                    throw new InvalidOperationException("the folder tile did not change with the icon style");
            }
            finally
            {
                WidgetManager.Instance.Config.IconStyle = original;
                island.RefreshIconStyle();
                island.Close();
            }
        }

        private static void IslandTodoTileRefreshesWithStyle()
        {
            ThemeManager.Initialize("dark");
            var island = new IslandWindow();
            string original = WidgetManager.Instance.Config.IconStyle;
            try
            {
                island.SetWidgets(new WidgetData[] { new TodoData { Name = "style-check", Color = "#EF4444" } });
                // Start from a known style: the persisted config may already pick hand-drawn.
                WidgetManager.Instance.Config.IconStyle = "classic";
                island.RefreshIconStyle();
                string before = IconFingerprint(TileIconCanvas(island));

                WidgetManager.Instance.Config.IconStyle = "handdrawn";
                island.RefreshIconStyle();
                string after = IconFingerprint(TileIconCanvas(island));
                if (before == after)
                    throw new InvalidOperationException("the todo tile did not change with the icon style");
            }
            finally
            {
                WidgetManager.Instance.Config.IconStyle = original;
                island.RefreshIconStyle();
                island.Close();
            }
        }

        private static void IslandSystemTilesFollowTheIconStyle()
        {
            ThemeManager.Initialize("dark");
            string original = WidgetManager.Instance.Config.IconStyle;
            // Startup path: the constructor itself applies the persisted style.
            WidgetManager.Instance.Config.IconStyle = "classic";
            var island = new IslandWindow();
            try
            {
                AssertSystemTileStyle(island, classic: true);

                WidgetManager.Instance.Config.IconStyle = "handdrawn";
                island.RefreshIconStyle();
                AssertSystemTileStyle(island, classic: false);

                WidgetManager.Instance.Config.IconStyle = "classic";
                island.RefreshIconStyle();
                AssertSystemTileStyle(island, classic: true);
            }
            finally
            {
                WidgetManager.Instance.Config.IconStyle = original;
                island.RefreshIconStyle();
                island.Close();
            }
        }

        private static void AssertSystemTileStyle(IslandWindow island, bool classic)
        {
            foreach (string tile in new[] { "DesktopTile", "SettingsTile", "AddTile" })
            {
                AssertTileCanvasPair(island, tile, classic);
            }
        }

        private static void AssertTileCanvasPair(IslandWindow island, string tileName, bool classic)
        {
            Visibility classicWanted = classic ? Visibility.Visible : Visibility.Collapsed;
            Visibility handDrawnWanted = classic ? Visibility.Collapsed : Visibility.Visible;

            var classicCanvas = island.FindName(tileName + "Classic") as Canvas;
            if (classicCanvas == null)
                throw new InvalidOperationException(tileName + "Classic was not found");
            if (classicCanvas.Visibility != classicWanted)
                throw new InvalidOperationException(tileName + "Classic is " + classicCanvas.Visibility +
                    ", wanted " + classicWanted);

            var handDrawnCanvas = island.FindName(tileName + "HandDrawn") as Canvas;
            if (handDrawnCanvas == null)
                throw new InvalidOperationException(tileName + "HandDrawn was not found");
            if (handDrawnCanvas.Visibility != handDrawnWanted)
                throw new InvalidOperationException(tileName + "HandDrawn is " + handDrawnCanvas.Visibility +
                    ", wanted " + handDrawnWanted);
        }

        private static void IslandDesktopSlashFollowsHiddenState()
        {
            ThemeManager.Initialize("dark");
            string originalStyle = WidgetManager.Instance.Config.IconStyle;
            bool originalHidden = WidgetManager.Instance.Config.HideDesktopIcons;
            var island = new IslandWindow();
            try
            {
                var update = typeof(IslandWindow).GetMethod("UpdateDesktopTile",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (update == null)
                    throw new InvalidOperationException("UpdateDesktopTile was not found");

                // Each style carries its own slash; both follow the hide state
                // while the style switch only moves whole canvases.
                foreach (string style in new[] { "classic", "handdrawn" })
                {
                    WidgetManager.Instance.Config.IconStyle = style;
                    island.RefreshIconStyle();

                    WidgetManager.Instance.Config.HideDesktopIcons = true;
                    update.Invoke(island, null);
                    AssertSlashVisibility(island, "DesktopSlash", true, style);
                    AssertSlashVisibility(island, "DesktopSlashHandDrawn", true, style);

                    WidgetManager.Instance.Config.HideDesktopIcons = false;
                    update.Invoke(island, null);
                    AssertSlashVisibility(island, "DesktopSlash", false, style);
                    AssertSlashVisibility(island, "DesktopSlashHandDrawn", false, style);
                }
            }
            finally
            {
                WidgetManager.Instance.Config.HideDesktopIcons = originalHidden;
                WidgetManager.Instance.Config.IconStyle = originalStyle;
                island.RefreshIconStyle();
                island.Close();
            }
        }

        private static void AssertSlashVisibility(IslandWindow island, string name, bool hidden, string style)
        {
            var slash = island.FindName(name) as Shape;
            if (slash == null)
                throw new InvalidOperationException(name + " was not found");
            Visibility wanted = hidden ? Visibility.Visible : Visibility.Collapsed;
            if (slash.Visibility != wanted)
                throw new InvalidOperationException(style + ": " + name + " is " + slash.Visibility +
                    " with HideDesktopIcons=" + hidden + ", wanted " + wanted);
        }

        private static void IslandHandDrawnSystemGeometryDiffers()
        {
            ThemeManager.Initialize("dark");
            var island = new IslandWindow();
            try
            {
                foreach (string tile in new[] { "DesktopTile", "SettingsTile", "AddTile" })
                {
                    var classicCanvas = island.FindName(tile + "Classic") as Canvas;
                    var handDrawnCanvas = island.FindName(tile + "HandDrawn") as Canvas;
                    if (classicCanvas == null || handDrawnCanvas == null)
                        throw new InvalidOperationException(tile + " is missing a style canvas");
                    if (handDrawnCanvas.Children.Count == 0)
                        throw new InvalidOperationException(tile + "HandDrawn draws nothing");

                    string handFingerprint = IconFingerprint(handDrawnCanvas);
                    if (handFingerprint.IndexOf("|d=", StringComparison.Ordinal) < 0)
                        throw new InvalidOperationException(tile + "HandDrawn has no baked path geometry");
                    if (IconFingerprint(classicCanvas) == handFingerprint)
                        throw new InvalidOperationException(tile + "HandDrawn matches classic");
                }
            }
            finally
            {
                island.Close();
            }
        }

        private static Canvas TileIconCanvas(IslandWindow island)
        {
            var panel = island.FindName("TilePanel") as Panel;
            if (panel == null || panel.Children.Count == 0)
                throw new InvalidOperationException("the island has no tiles");
            var tile = panel.Children[0] as Border;
            var viewbox = tile == null ? null : tile.Child as Viewbox;
            var icon = viewbox == null ? null : viewbox.Child as Canvas;
            if (icon == null) throw new InvalidOperationException("the tile icon canvas was not found");
            return icon;
        }

        private static string IconFingerprint(DependencyObject root)
        {
            var sb = new StringBuilder();
            AppendIconFingerprint(root, sb);
            return sb.ToString();
        }

        private static void AppendIconFingerprint(DependencyObject node, StringBuilder sb)
        {
            var shape = node as Shape;
            if (shape != null)
            {
                sb.Append(shape.GetType().Name);
                var fill = shape.Fill as SolidColorBrush;
                if (fill != null) sb.Append("|f=").Append(Describe(fill.Color));
                var gradient = shape.Fill as LinearGradientBrush;
                if (gradient != null)
                {
                    foreach (var stop in gradient.GradientStops)
                        sb.Append("|g=").Append(Describe(stop.Color));
                }
                var stroke = shape.Stroke as SolidColorBrush;
                if (stroke != null) sb.Append("|s=").Append(Describe(stroke.Color));
                var path = shape as Path;
                if (path != null && path.Data != null) sb.Append("|d=").Append(path.Data.ToString());
                sb.Append(';');
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                AppendIconFingerprint(VisualTreeHelper.GetChild(node, i), sb);
        }

        #endregion

        #region Task 3.5 Quick Access pins

        private static void QuickAccessMatchIgnoresCase()
        {
            if (!QuickAccess.MatchesPinnedEntry(
                    @"C:\Users\Test\AppData\Roaming\Kobold\Storage",
                    @"c:\users\test\appdata\roaming\kobold\storage"))
            {
                throw new InvalidOperationException(
                    "the same path in a different case did not match");
            }
        }

        private static void QuickAccessMatchIgnoresTrailingSeparator()
        {
            if (!QuickAccess.MatchesPinnedEntry(
                    @"C:\Users\Test\AppData\Roaming\Kobold\Storage\",
                    @"C:\Users\Test\AppData\Roaming\Kobold\Storage"))
            {
                throw new InvalidOperationException("a trailing backslash broke the match");
            }
            if (!QuickAccess.MatchesPinnedEntry(
                    @"C:\Users\Test\AppData\Roaming\Kobold\Storage/",
                    @"C:\Users\Test\AppData\Roaming\Kobold\Storage"))
            {
                throw new InvalidOperationException("a trailing forward slash broke the match");
            }
        }

        private static void QuickAccessMatchRejectsDifferentPaths()
        {
            if (QuickAccess.MatchesPinnedEntry(
                    @"C:\Users\Test\AppData\Roaming\Kobold\Storage2",
                    @"C:\Users\Test\AppData\Roaming\Kobold\Storage"))
            {
                throw new InvalidOperationException("a sibling path matched the storage path");
            }
            if (QuickAccess.MatchesPinnedEntry(
                    @"C:\Users\Test\AppData\Roaming\Kobold\Storage\Sub",
                    @"C:\Users\Test\AppData\Roaming\Kobold\Storage"))
            {
                throw new InvalidOperationException("a child path matched the storage path");
            }
        }

        private static void QuickAccessMatchRejectsEmptyPaths()
        {
            if (QuickAccess.MatchesPinnedEntry(null, @"C:\Kobold") ||
                QuickAccess.MatchesPinnedEntry(@"C:\Kobold", null) ||
                QuickAccess.MatchesPinnedEntry("", @"C:\Kobold") ||
                QuickAccess.MatchesPinnedEntry("   ", @"C:\Kobold") ||
                QuickAccess.MatchesPinnedEntry(@"C:\Kobold", "   "))
            {
                throw new InvalidOperationException("an empty path matched");
            }
        }

        /// <summary>
        /// Read-only by contract: TryIsPinned enumerates the Quick Access
        /// namespace and compares paths, so it is safe to exercise here; the
        /// pin/unpin actions stay smoke-only.
        /// </summary>
        private static void QuickAccessProbeIsReadOnly()
        {
            string storage = Utils.GetStoragePath();
            bool? state = QuickAccess.TryIsPinned(storage);
            if (state.HasValue)
            {
                bool? again = QuickAccess.TryIsPinned(storage);
                if (again != state)
                {
                    throw new InvalidOperationException(
                        "the read-only state probe changed between calls: " + state + " -> " + again);
                }
            }
        }

        /// <summary>
        /// The Settings window carries the storage row (header, path, open and
        /// pin buttons). The pin button label must be one of the two localized
        /// toggles whatever the machine's real Quick Access state is.
        /// </summary>
        private static void SettingsStorageRowIsPresent()
        {
            ThemeManager.Initialize("dark");
            var window = new SettingsWindow();

            var header = window.FindName("StorageHeader") as TextBlock;
            var pathText = window.FindName("StoragePathText") as TextBlock;
            var openButton = window.FindName("StorageOpenButton") as Button;
            var pinButton = window.FindName("StoragePinButton") as Button;
            if (header == null || pathText == null || openButton == null || pinButton == null)
            {
                throw new InvalidOperationException("the storage row parts are missing");
            }

            string storage = Utils.GetStoragePath();
            if (pathText.Text != storage)
            {
                throw new InvalidOperationException(
                    "the storage row shows '" + pathText.Text + "', wanted '" + storage + "'");
            }
            if (header.Text != "📦 " + Localization.Get("Settings_StorageHeader"))
            {
                throw new InvalidOperationException(
                    "the storage header shows '" + header.Text + "'");
            }
            if (openButton.Content as string != Localization.Get("Settings_StorageOpen"))
            {
                throw new InvalidOperationException(
                    "the open button shows '" + openButton.Content + "'");
            }

            string label = pinButton.Content as string;
            if (label != Localization.Get("Settings_StoragePin") &&
                label != Localization.Get("Settings_StorageUnpin"))
            {
                throw new InvalidOperationException(
                    "the pin button shows '" + label + "', wanted one of the localized Pin/Unpin labels");
            }
        }

        /// <summary>
        /// Fix round 1 (R1): the storage buttons must be comfortable (>= 34
        /// high, >= 16 horizontal padding) and the two buttons + gap must fit
        /// the section in en/zh/ja without clipping or overflowing - checked
        /// for BOTH toggle labels (the machine's real pin state is irrelevant;
        /// the ja pin label is the longest).
        /// </summary>
        private static void SettingsStorageButtonsFitAllLanguages()
        {
            ThemeManager.Initialize("dark");
            string original = Localization.CurrentLanguage;
            try
            {
                foreach (var lang in new[] { "en", "zh", "ja" })
                {
                    Localization.SetLanguage(lang);
                    var window = new SettingsWindow();

                    var root = window.Content as FrameworkElement;
                    if (root == null)
                        throw new InvalidOperationException(lang + ": the settings window has no content");

                    var row = window.FindName("StorageButtonsRow") as FrameworkElement;
                    var open = window.FindName("StorageOpenButton") as Button;
                    var pin = window.FindName("StoragePinButton") as Button;
                    if (row == null || open == null || pin == null)
                        throw new InvalidOperationException(lang + ": the storage button row is missing");

                    open.Content = Localization.Get("Settings_StorageOpen");
                    foreach (var labelKey in new[] { "Settings_StoragePin", "Settings_StorageUnpin" })
                    {
                        pin.Content = Localization.Get(labelKey);

                        root.InvalidateMeasure();
                        root.Measure(new Size(window.Width, window.Height));
                        root.Arrange(new Rect(0, 0, window.Width, window.Height));
                        root.UpdateLayout();

                        // The offscreen layout must have produced real numbers,
                        // or the fit comparison would silently pass on NaN. The
                        // row is right-aligned (content-sized); the section's
                        // inner StackPanel carries the available width.
                        var section = row.Parent as FrameworkElement;
                        if (section == null || double.IsNaN(section.ActualWidth) || section.ActualWidth < 200)
                        {
                            throw new InvalidOperationException(
                                lang + ": the storage section was not laid out (width " +
                                (section == null ? "no parent" : section.ActualWidth.ToString("0.##")) + ")");
                        }
                        if (double.IsNaN(open.ActualWidth) || double.IsNaN(pin.ActualWidth) ||
                            open.ActualWidth <= 0 || pin.ActualWidth <= 0)
                        {
                            throw new InvalidOperationException(
                                lang + ": the buttons were not laid out (" + open.ActualWidth + "/" + pin.ActualWidth + ")");
                        }

                        if (open.MinHeight < 34 || pin.MinHeight < 34)
                        {
                            throw new InvalidOperationException(
                                lang + ": MinHeight " + open.MinHeight + "/" + pin.MinHeight + " < 34");
                        }
                        if (open.ActualHeight < 34 || pin.ActualHeight < 34)
                        {
                            throw new InvalidOperationException(
                                lang + ": button height " + open.ActualHeight.ToString("0.##") + "/" +
                                pin.ActualHeight.ToString("0.##") + " < 34");
                        }
                        if (open.Padding.Left < 16 || pin.Padding.Left < 16)
                        {
                            throw new InvalidOperationException(
                                lang + ": horizontal padding " + open.Padding + "/" + pin.Padding + " < 16");
                        }

                        double total = open.ActualWidth + pin.Margin.Left + pin.ActualWidth;
                        if (total > section.ActualWidth + 0.5)
                        {
                            throw new InvalidOperationException(
                                lang + " [" + labelKey + "]: the buttons need " + total.ToString("0.##") +
                                " of the section's " + section.ActualWidth.ToString("0.##") + " (overflow)");
                        }
                    }

                    // Auto width is what guarantees the localized text is never
                    // clipped; an explicit Width would break that silently.
                    if (!double.IsNaN(open.Width) || !double.IsNaN(pin.Width))
                    {
                        throw new InvalidOperationException(
                            lang + ": an explicit button Width can clip the localized text");
                    }
                }
            }
            finally
            {
                Localization.SetLanguage(original);
            }
        }

        #endregion

        #region Config redirect pins (stage 0)

        /// <summary>
        /// SHA256 of a file as lowercase hex, or <see cref="AbsentHash"/> when
        /// the file is not there. Hashes are the pin's comparison basis:
        /// bytes cannot lie about a rewrite the way timestamps can.
        /// </summary>
        private static string HashOrAbsent(string path)
        {
            if (!IO.File.Exists(path)) return AbsentHash;

            using (var sha = SHA256.Create())
            using (var stream = IO.File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>True while the file's bytes still hash to the snapshot.</summary>
        private static bool FileMatchesSnapshot(string path, string snapshotHash)
        {
            return HashOrAbsent(path) == snapshotHash;
        }

        /// <summary>
        /// Proves the pin's comparison logic on throwaway files before it
        /// guards the real config: identical bytes compare equal, a changed
        /// file is detected, and absent compares as absent.
        /// </summary>
        private static void ConfigPinCompareSelfTest()
        {
            string dir = IO.Path.Combine(IO.Path.GetTempPath(),
                "kobold-check-xamlload-pin-" + Guid.NewGuid().ToString("N"));
            IO.Directory.CreateDirectory(dir);
            try
            {
                string file = IO.Path.Combine(dir, "sample.bin");
                IO.File.WriteAllText(file, "one");
                string snapshot = HashOrAbsent(file);
                if (snapshot == AbsentHash)
                {
                    throw new InvalidOperationException("an existing file hashed as absent");
                }

                IO.File.WriteAllText(file, "one");
                if (!FileMatchesSnapshot(file, snapshot))
                {
                    throw new InvalidOperationException("identical bytes compared as changed");
                }

                IO.File.WriteAllText(file, "two");
                if (FileMatchesSnapshot(file, snapshot))
                {
                    throw new InvalidOperationException("a changed file compared as unchanged");
                }

                string missing = IO.Path.Combine(dir, "missing.bin");
                if (HashOrAbsent(missing) != AbsentHash || !FileMatchesSnapshot(missing, AbsentHash))
                {
                    throw new InvalidOperationException("a missing file did not read as absent");
                }
            }
            finally
            {
                try { IO.Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// Saves synchronously (not through the debounce) after removing the
        /// sandbox file the defaulted load writes, so the file we assert on
        /// can only come from this save - proving the singleton's write path
        /// is the sandbox, not the real config.
        /// </summary>
        private static void ForcedSaveLandsInTheSandbox()
        {
            string sandboxConfig = IO.Path.Combine(_checkSandbox, "config.json");
            if (IO.File.Exists(sandboxConfig)) IO.File.Delete(sandboxConfig);

            WidgetManager.Instance.Config.Save();

            if (!IO.File.Exists(sandboxConfig))
            {
                throw new InvalidOperationException(
                    "the forced save did not write the sandbox config " + sandboxConfig);
            }
            if (!IO.File.ReadAllText(sandboxConfig).Contains("\"Language\""))
            {
                throw new InvalidOperationException(
                    "the sandbox config " + sandboxConfig + " is not a serialized config");
            }
        }

        /// <summary>
        /// The real config and backup must be byte-identical to the startup
        /// snapshot - including "absent at start" staying absent at the end.
        /// </summary>
        private static void RealConfigFilesAreUnchanged()
        {
            string configNow = HashOrAbsent(_realConfigPath);
            string backupNow = HashOrAbsent(_realBackupPath);
            if (configNow != _realConfigHashBefore || backupNow != _realBackupHashBefore)
            {
                throw new InvalidOperationException(
                    "the real config changed: " + _realConfigPath + " " + _realConfigHashBefore +
                    " -> " + configNow + " ; " + _realBackupPath + " " + _realBackupHashBefore +
                    " -> " + backupNow);
            }
        }

        /// <summary>The private static override path recorded by the seam.</summary>
        private static string StaticOverrideField(string name)
        {
            var field = typeof(WidgetManager).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("WidgetManager." + name + " field not found");
            return (string)field.GetValue(null);
        }

        /// <summary>
        /// The seam's guard: once the singleton exists a second redirect must
        /// throw InvalidOperationException and leave the recorded paths alone -
        /// a future edit dropping the throw must fail here, not silently
        /// repoint a live manager.
        /// </summary>
        private static void LateOverrideIsRejected()
        {
            string pathBefore = StaticOverrideField("_configPathOverride");
            string backupBefore = StaticOverrideField("_backupPathOverride");
            if (pathBefore == null || backupBefore == null)
            {
                throw new InvalidOperationException(
                    "the startup redirect did not record both paths (" + pathBefore + "/" + backupBefore + ")");
            }

            bool rejected = false;
            try
            {
                WidgetManager.OverrideConfigPathsForChecks("x", "y");
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "the late override threw " + ex.GetType().Name +
                    " instead of InvalidOperationException: " + ex.Message);
            }

            if (!rejected)
            {
                throw new InvalidOperationException(
                    "the late override was accepted after WidgetManager.Instance existed");
            }

            string pathAfter = StaticOverrideField("_configPathOverride");
            string backupAfter = StaticOverrideField("_backupPathOverride");
            if (pathAfter != pathBefore || backupAfter != backupBefore)
            {
                throw new InvalidOperationException(
                    "the rejected late override changed the recorded paths: " + pathBefore + "/" + backupBefore +
                    " -> " + pathAfter + "/" + backupAfter);
            }
        }

        #endregion

        #region Task T5 scroll-bar pins

        /// <summary>
        /// Task T5: the implicit ScrollBar style makes the thumb a pill - it
        /// rests 4 DIP wide (2 DIP rounded) centred in the 8 DIP click zone,
        /// instead of the old margin-inset border that stretched edge to edge.
        /// The logical host mirrors the windowed bars' resource chain - a
        /// bare unparented control resolves no implicit styles (see
        /// EditableComboBoxTextIsNotClipped).
        /// </summary>
        private static void ScrollBarPillRestsSlim()
        {
            var bar = new ScrollBar { Orientation = Orientation.Vertical };
            var host = new Grid();
            host.Children.Add(bar);
            bar.ApplyTemplate();
            if (bar.Template == null)
            {
                throw new InvalidOperationException("the app's implicit ScrollBar style was not applied");
            }
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            track.Thumb.ApplyTemplate();
            var th = (Border)track.Thumb.Template.FindName("Th", track.Thumb);

            if (th.Width != 4)
            {
                throw new InvalidOperationException("the vertical pill rests " + th.Width + " wide, wanted 4");
            }
            if (th.CornerRadius.TopLeft != 2)
            {
                throw new InvalidOperationException(
                    "the vertical pill's corner radius is " + th.CornerRadius.TopLeft + ", wanted 2");
            }
            if (th.HorizontalAlignment != HorizontalAlignment.Center)
            {
                throw new InvalidOperationException(
                    "the vertical pill is aligned " + th.HorizontalAlignment + ", wanted Center");
            }
        }

        /// <summary>
        /// Task T5: the horizontal bar mirrors the rest state - a 4 DIP tall
        /// pill centred vertically in the 8 DIP lane (its own template swaps
        /// the axis and un-reverses the track direction).
        /// </summary>
        private static void ScrollBarHorizontalPillMirrorsRestState()
        {
            var bar = new ScrollBar { Orientation = Orientation.Horizontal };
            var host = new Grid();
            host.Children.Add(bar);
            bar.ApplyTemplate();
            if (bar.Template == null)
            {
                throw new InvalidOperationException("the app's implicit ScrollBar style was not applied");
            }
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            track.Thumb.ApplyTemplate();
            var th = (Border)track.Thumb.Template.FindName("Th", track.Thumb);

            if (th.Height != 4)
            {
                throw new InvalidOperationException("the horizontal pill rests " + th.Height + " tall, wanted 4");
            }
            if (th.VerticalAlignment != VerticalAlignment.Center)
            {
                throw new InvalidOperationException(
                    "the horizontal pill is aligned " + th.VerticalAlignment + ", wanted Center");
            }
        }

        /// <summary>
        /// Task T5 (final-review F1): the advertised growth - the resting pill
        /// thickens to 6 DIP and takes the hover brush under IsMouseOver and
        /// IsDragging, in both orientations. The setters live in the Thumb
        /// template's triggers; this inspects them directly (the resting
        /// geometry pins cannot see the trigger values).
        /// </summary>
        private static void ScrollBarPillGrowsWhenHoveredOrDragged()
        {
            AssertThumbGrowth(Orientation.Vertical, FrameworkElement.WidthProperty);
            AssertThumbGrowth(Orientation.Horizontal, FrameworkElement.HeightProperty);
        }

        private static void AssertThumbGrowth(Orientation orientation, DependencyProperty longAxis)
        {
            var bar = new ScrollBar { Orientation = orientation };
            var host = new Grid();
            host.Children.Add(bar);
            bar.ApplyTemplate();
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            track.Thumb.ApplyTemplate();

            var triggers = track.Thumb.Template.Triggers;
            RequireGrowthTrigger(triggers, UIElement.IsMouseOverProperty, longAxis, orientation);
            RequireGrowthTrigger(triggers, Thumb.IsDraggingProperty, longAxis, orientation);
        }

        private static void RequireGrowthTrigger(TriggerCollection triggers, DependencyProperty state,
            DependencyProperty longAxis, Orientation orientation)
        {
            foreach (var triggerBase in triggers)
            {
                var trigger = triggerBase as Trigger;
                if (trigger == null || trigger.Property != state) continue;

                bool grown = false;
                bool hoverInk = false;
                foreach (var setterBase in trigger.Setters)
                {
                    var setter = setterBase as Setter;
                    if (setter == null || !"Th".Equals(setter.TargetName)) continue;
                    if (setter.Property == longAxis && IsSix(setter.Value)) grown = true;
                    if (setter.Property == Border.BackgroundProperty) hoverInk = true;
                }
                if (grown && hoverInk) return;
            }

            throw new InvalidOperationException(
                "the " + state.Name + " trigger does not grow the pill along " +
                longAxis.Name + " to 6 and darken it (" + orientation + ")");
        }

        private static bool IsSix(object value)
        {
            if (value is double d) return d == 6.0;
            return value is string s && s == "6";
        }

        #endregion

        #region Task T8 context-menu restyle pins

        /// <summary>
        /// Task T8 (style A): the shared ContextMenu template wears the rounded
        /// surface - a 12 DIP corner radius with 8 DIP padding, seated in the
        /// 10 DIP transparent gutter that gives the soft drop shadow room (and
        /// keeps the stack panel as the items host, so the Overlay* dynamic
        /// keys and the widget tint keep resolving).
        /// </summary>
        private static void MenuSurfaceWearsTheRoundedStyle()
        {
            var menu = new MenuBuilder(null).AddItem("Menu_Delete", () => { }).Build();
            ApplyAppImplicitStyle(menu);
            menu.ApplyTemplate();

            if (menu.Template == null)
            {
                throw new InvalidOperationException("the menu carries no template");
            }
            var surface = menu.Template.FindName("Surface", menu) as Border;
            if (surface == null)
            {
                throw new InvalidOperationException("the menu template has no Surface border");
            }
            if (surface.CornerRadius.TopLeft != 12)
            {
                throw new InvalidOperationException(
                    "the surface corner radius is " + surface.CornerRadius.TopLeft + ", wanted 12");
            }
            if (surface.Padding != new Thickness(8))
            {
                throw new InvalidOperationException(
                    "the surface padding is '" + surface.Padding + "', wanted 8 on every side");
            }
        }

        /// <summary>
        /// Task T8 (style A): the entry's highlight is a 4 DIP-inset, 8 DIP-
        /// radius capsule around the header + arrow, not a full-bleed band.
        /// </summary>
        private static void MenuItemPillIsInsetAndRounded()
        {
            var item = new MenuItem { Header = "x" };
            ApplyAppImplicitStyle(item);
            item.ApplyTemplate();

            var pill = PillOf(item);
            if (pill.Margin != new Thickness(4, 1, 4, 1))
            {
                throw new InvalidOperationException(
                    "the item pill margin is '" + pill.Margin + "', wanted 4,1,4,1");
            }
            if (pill.CornerRadius.TopLeft != 8)
            {
                throw new InvalidOperationException(
                    "the item pill corner radius is " + pill.CornerRadius.TopLeft + ", wanted 8");
            }
        }

        /// <summary>
        /// Task T8 (style A): a disabled entry dims its whole pill to half
        /// opacity so the greying reads as one rounded shape.
        /// </summary>
        private static void DisabledMenuItemDimsItsPill()
        {
            var item = new MenuItem { Header = "x", IsEnabled = false };
            ApplyAppImplicitStyle(item);
            item.ApplyTemplate();

            var pill = PillOf(item);
            if (pill.Opacity != 0.5)
            {
                throw new InvalidOperationException(
                    "the disabled pill opacity is " + pill.Opacity + ", wanted 0.5");
            }
        }

        /// <summary>
        /// Task T8 (style A): the separator line insets 30 DIP on both sides,
        /// lining up with the entry text of the 4 DIP-inset pills.
        /// </summary>
        private static void SeparatorFitsTheRoundedRows()
        {
            var sep = new Separator();
            ApplyAppImplicitStyle(sep);
            sep.ApplyTemplate();

            if (sep.Template == null)
            {
                throw new InvalidOperationException("the separator carries no template");
            }
            var line = sep.Template.FindName("Line", sep) as Border;
            if (line == null)
            {
                throw new InvalidOperationException("the separator template has no Line border");
            }
            if (line.Margin != new Thickness(30, 5, 30, 5))
            {
                throw new InvalidOperationException(
                    "the separator line margin is '" + line.Margin + "', wanted 30,5,30,5");
            }
        }

        /// <summary>
        /// The island's + tile and the tray's new-widget submenu build one entry
        /// per registered kind through <see cref="WidgetMenuBuilder.BuildKindChoices"/>.
        /// A kind whose label key is missing would leak the raw "UI_WidgetKind…"
        /// key into the menu (<see cref="Localization.Get"/> returns the key on a
        /// miss), so every entry's header must be a real, translated label.
        /// </summary>
        private static void EveryKindChoiceResolvesToATranslatedLabel()
        {
            var choices = WidgetMenuBuilder.BuildKindChoices(kind => { });

            if (choices.Count != WidgetKindRegistry.All.Count)
            {
                throw new InvalidOperationException(
                    "the menu built " + choices.Count + " kind choices for "
                    + WidgetKindRegistry.All.Count + " registered kinds");
            }

            foreach (var choice in choices)
            {
                string header = choice.Header as string;
                if (header == null)
                {
                    throw new InvalidOperationException("a kind choice has no string header");
                }
                if (header.Contains("UI_"))
                {
                    throw new InvalidOperationException(
                        "a kind choice leaks a raw localization key: '" + header + "'");
                }
            }
        }

        /// <summary>
        /// An unparented control resolves no implicit style from App.xaml (see
        /// EditableComboBoxTextIsNotClipped), so the pins put the app's own
        /// style on the control before probing its template.
        /// </summary>
        private static void ApplyAppImplicitStyle(Control control)
        {
            var style = Application.Current.TryFindResource(control.GetType()) as Style;
            if (style == null)
            {
                throw new InvalidOperationException(
                    "the app's implicit " + control.GetType().Name + " style was not found");
            }
            control.Style = style;
        }

        /// <summary>The rounded highlight capsule inside a menu item's template.</summary>
        private static Border PillOf(MenuItem item)
        {
            if (item.Template == null)
            {
                throw new InvalidOperationException("the menu item carries no template");
            }
            var pill = item.Template.FindName("Pill", item) as Border;
            if (pill == null)
            {
                throw new InvalidOperationException("the menu item template has no Pill border");
            }
            return pill;
        }

        /// <summary>Task T2: folder data builds the folder window through the factory.</summary>
        private static void FactoryFolderDataBuildsFolderWidget()
        {
            if (!(WidgetWindowFactory.Create(new FolderData()) is FolderWidget))
            {
                throw new InvalidOperationException("folder data did not build a FolderWidget");
            }
        }

        /// <summary>Task T2: todo data builds the todo window through the factory.</summary>
        private static void FactoryTodoDataBuildsTodoWidget()
        {
            if (!(WidgetWindowFactory.Create(new TodoData()) is TodoWidget))
            {
                throw new InvalidOperationException("todo data did not build a TodoWidget");
            }
        }

        /// <summary>Task T2: an unregistered kind and a null datum both have no window.</summary>
        private static void FactoryUnknownKindHasNoWindow()
        {
            if (WidgetWindowFactory.Create(new UnknownData { Kind = "weather" }) != null)
            {
                throw new InvalidOperationException("an unknown kind built a window");
            }
            if (WidgetWindowFactory.Create(null) != null)
            {
                throw new InvalidOperationException("null data built a window");
            }
        }

        /// <summary>Task T10: note data builds the note window through the factory.</summary>
        private static void FactoryNoteDataBuildsNoteWidget()
        {
            if (!(WidgetWindowFactory.Create(new NoteData()) is NoteWidget))
            {
                throw new InvalidOperationException("note data did not build a NoteWidget");
            }
        }

        /// <summary>
        /// Task T10 (spec 6.5): the note panel's minimum fits the chrome header
        /// plus one card and the add row. Read through the base's protected
        /// hook, the same reflection the resize checks use.
        /// </summary>
        private static void NotePanelMinimumFitsTheShell()
        {
            ThemeManager.Initialize("dark");
            var widget = new NoteWidget(new NoteData());

            var result = PrivateBaseMethod("GetMinPanelSize").Invoke(widget, null);
            var min = ((double, double))result;
            if (min.Item1 != 260 || min.Item2 != 220)
            {
                throw new InvalidOperationException(
                    "the note panel minimum is " + min.Item1 + "x" + min.Item2 + ", wanted 260x220");
            }
        }

        #endregion

        #region Task T11: note list pins

        /// <summary>
        /// T11: a card carries the title, the modified time ("g") and the
        /// 80-char excerpt as its stack children; a note with task boxes adds
        /// the progress line as a fourth. The three fixtures cover a heading +
        /// body note, a note with tasks and an empty note (empty title and
        /// excerpt, but a real time).
        /// </summary>
        private static void NoteCardsShowTitleTimeAndExcerpt()
        {
            ThemeManager.Initialize("dark");
            var when = new DateTime(2026, 3, 5, 14, 30, 0);
            var data = new NoteData();
            data.Notes.Add(NewNote("a", "# Shopping\n\n- milk\n- eggs", when));
            data.Notes.Add(NewNote("b", "# Tasks\n\n- [x] a\n- [ ] b", when.AddHours(1)));
            data.Notes.Add(NewNote("c", string.Empty, when.AddHours(2)));

            var view = new NoteWidget(data).PanelView;
            view.Refresh();

            if (NoteCardCount(view) != 3)
                throw new InvalidOperationException("the list rendered " + NoteCardCount(view) + " card(s), wanted 3");

            // Newest first (SortForDisplay): c (16:30), b (15:30), a (14:30).
            AssertNoteCardTexts(view, 0, new[] { string.Empty, when.AddHours(2).ToString("g"), string.Empty });
            AssertNoteCardTexts(view, 1, new[] { "Tasks", when.AddHours(1).ToString("g"), "Tasks a b", "✓ 1/2" });
            AssertNoteCardTexts(view, 2, new[] { "Shopping", when.ToString("g"), "Shopping milk eggs" });
        }

        /// <summary>
        /// T11: the progress line counts the parser's task boxes - an open and
        /// a completed box read "✓ 1/2" - and only a note with tasks carries a
        /// fourth line.
        /// </summary>
        private static void NoteTaskShowsProgress()
        {
            ThemeManager.Initialize("dark");
            var when = new DateTime(2026, 3, 5, 14, 30, 0);
            var data = new NoteData();
            data.Notes.Add(NewNote("t", "- [x] a\n- [ ] b", when));

            var view = new NoteWidget(data).PanelView;
            view.Refresh();

            var texts = NoteCardTexts(view, 0);
            if (texts.Count != 4)
                throw new InvalidOperationException("the task card has " + texts.Count + " line(s), wanted 4");
            if (texts[3] != "✓ 1/2")
                throw new InvalidOperationException("the progress line is '" + texts[3] + "', wanted '✓ 1/2'");
        }

        /// <summary>
        /// T11: the cards follow SortForDisplay - the newest modification
        /// first - while the source list keeps its own order.
        /// </summary>
        private static void NoteListOrdersByModifiedTime()
        {
            ThemeManager.Initialize("dark");
            var data = new NoteData();
            data.Notes.Add(NewNote("a", "First", new DateTime(2026, 3, 5, 9, 0, 0)));
            data.Notes.Add(NewNote("b", "Later", new DateTime(2026, 3, 5, 11, 0, 0)));
            data.Notes.Add(NewNote("c", "Middle", new DateTime(2026, 3, 5, 10, 0, 0)));

            var view = new NoteWidget(data).PanelView;
            view.Refresh();

            if (NoteCardCount(view) != 3)
                throw new InvalidOperationException("the list rendered " + NoteCardCount(view) + " card(s), wanted 3");
            if (NoteCardLine(view, 0, 0) != "Later")
                throw new InvalidOperationException("the first card is '" + NoteCardLine(view, 0, 0) + "', wanted 'Later'");
            if (NoteCardLine(view, 1, 0) != "Middle")
                throw new InvalidOperationException("the second card is '" + NoteCardLine(view, 1, 0) + "', wanted 'Middle'");
            if (NoteCardLine(view, 2, 0) != "First")
                throw new InvalidOperationException("the third card is '" + NoteCardLine(view, 2, 0) + "', wanted 'First'");

            if (data.Notes[0].Id != "a" || data.Notes[1].Id != "b" || data.Notes[2].Id != "c")
                throw new InvalidOperationException("the render re-ordered the source list");
        }

        /// <summary>
        /// T11: the card's row menu holds one delete entry; clicking it removes
        /// that note, rebuilds the list, and the undo puts the note back at its
        /// original index (the second note here) with the list rebuilt again.
        /// </summary>
        private static void NoteRowMenuDeletesAndUndoRestores()
        {
            ThemeManager.Initialize("dark");
            var first = NewNote("a", "First", new DateTime(2026, 3, 5, 9, 0, 0));
            var second = NewNote("b", "Second", new DateTime(2026, 3, 5, 11, 0, 0));
            var data = new NoteData();
            data.Notes.Add(first);
            data.Notes.Add(second);

            var view = new NoteWidget(data).PanelView;
            view.Refresh();

            if (NoteCardLine(view, 0, 0) != "Second")
                throw new InvalidOperationException("the newest note is not the first card");
            var card = NoteCardAt(view, 0);
            if (card.ContextMenu == null)
                throw new InvalidOperationException("the card carries no row menu");
            if (card.ContextMenu.Items.Count != 1)
                throw new InvalidOperationException("the row menu has " + card.ContextMenu.Items.Count + " item(s)");
            string header = HeaderOf(card.ContextMenu.Items[0]);
            if (header != Localization.Get("Menu_DeleteItem"))
                throw new InvalidOperationException("the row menu header is '" + header + "'");

            ClickMenuItem(card, 0); // delete the newest note through its row menu

            if (data.Notes.Count != 1 || data.Notes[0] != first)
                throw new InvalidOperationException("the menu delete did not remove the newest note");
            if (NoteCardCount(view) != 1)
                throw new InvalidOperationException("the card list did not rebuild after the delete");

            if (!InvokeNoteTryUndo(view))
                throw new InvalidOperationException("TryUndo reported nothing to restore");
            if (data.Notes.Count != 2 || data.Notes[0] != first || data.Notes[1] != second)
                throw new InvalidOperationException("the undo did not put the note back at its index");
            if (NoteCardCount(view) != 2)
                throw new InvalidOperationException("the card list did not rebuild after the undo");
        }

        /// <summary>
        /// T11: the undo stack is multi-step - two deletes walk back newest
        /// first, each restoring exactly one note in its slot, and the drained
        /// stack reports false.
        /// </summary>
        private static void NoteUndoWalksBackBeyondOneStep()
        {
            ThemeManager.Initialize("dark");
            var a = NewNote("a", "A", new DateTime(2026, 3, 5, 9, 0, 0));
            var b = NewNote("b", "B", new DateTime(2026, 3, 5, 10, 0, 0));
            var c = NewNote("c", "C", new DateTime(2026, 3, 5, 11, 0, 0));
            var data = new NoteData();
            data.Notes.Add(a);
            data.Notes.Add(b);
            data.Notes.Add(c);

            var view = new NoteWidget(data).PanelView;
            view.Refresh();

            InvokeNoteDelete(view, c);
            InvokeNoteDelete(view, b);
            if (data.Notes.Count != 1 || data.Notes[0] != a)
                throw new InvalidOperationException("the fixture deletes did not stick");

            if (!InvokeNoteTryUndo(view))
                throw new InvalidOperationException("the second delete did not undo");
            if (data.Notes.Count != 2 || data.Notes[0] != a || data.Notes[1] != b)
                throw new InvalidOperationException("the first undo restored the wrong note or slot");
            if (!InvokeNoteTryUndo(view))
                throw new InvalidOperationException("the first delete did not undo");
            if (data.Notes.Count != 3 || data.Notes[0] != a || data.Notes[1] != b || data.Notes[2] != c)
                throw new InvalidOperationException("the second undo did not restore the order");
            if (InvokeNoteTryUndo(view))
                throw new InvalidOperationException("the drained stack still reported an undo");
        }

        /// <summary>
        /// T11: the stack is capped at twenty - the oldest delete falls off, so
        /// exactly twenty undos succeed (restoring the newest twenty notes in
        /// their order) and the twenty-first reports false with the oldest note
        /// still gone.
        /// </summary>
        private static void NoteUndoStackKeepsOnlyTheNewestTwenty()
        {
            ThemeManager.Initialize("dark");
            var data = new NoteData();
            var notes = new List<NoteItem>();
            for (int i = 0; i < 21; i++)
            {
                var note = NewNote("n" + i, "Note " + i, new DateTime(2026, 3, 5, 9, 0, 0).AddMinutes(i));
                notes.Add(note);
                data.Notes.Add(note);
            }

            var view = new NoteWidget(data).PanelView;
            view.Refresh();

            foreach (var note in notes) InvokeNoteDelete(view, note);
            if (data.Notes.Count != 0)
                throw new InvalidOperationException("the fixture deletes left " + data.Notes.Count + " note(s)");

            int undone = 0;
            while (InvokeNoteTryUndo(view)) undone++;
            if (undone != 20)
                throw new InvalidOperationException("the stack yielded " + undone + " undos, wanted 20");
            if (data.Notes.Count != 20 || data.Notes.Contains(notes[0]))
                throw new InvalidOperationException(
                    "the cap did not drop the oldest delete: " + data.Notes.Count + " note(s) came back");
            for (int i = 0; i < 20; i++)
            {
                if (data.Notes[i] != notes[i + 1])
                    throw new InvalidOperationException("the undo order broke at index " + i);
            }
        }

        /// <summary>
        /// T11 (fix round 1): an empty note list carries no card at all - the
        /// top add row is the panel's only content (no empty-state message).
        /// The pin reaches that state through the real delete path: two notes
        /// render two cards, each row menu deletes one, and the list is then
        /// empty. The two-card premise is what gives the pin teeth against a
        /// renderer that paints no cards (the T10 skeleton).
        /// </summary>
        private static void NoteEmptyListShowsOnlyTheAddRow()
        {
            ThemeManager.Initialize("dark");
            var data = new NoteData();
            data.Notes.Add(NewNote("a", "First", new DateTime(2026, 3, 5, 9, 0, 0)));
            data.Notes.Add(NewNote("b", "Second", new DateTime(2026, 3, 5, 11, 0, 0)));

            var view = new NoteWidget(data).PanelView;
            view.Refresh();

            if (NoteCardCount(view) != 2)
                throw new InvalidOperationException(
                    "the two-note fixture rendered " + NoteCardCount(view) + " card(s), wanted 2");

            // Empty the list through the row menus, re-fetching the card each
            // time (every delete rebuilds the cards).
            int[] remaining = { 1, 0 };
            for (int i = 0; i < remaining.Length; i++)
            {
                var card = NoteCardAt(view, 0);
                if (card.ContextMenu == null)
                    throw new InvalidOperationException("the card carries no row menu");
                ClickMenuItem(card, 0);
                if (data.Notes.Count != remaining[i])
                {
                    throw new InvalidOperationException(
                        "the row-menu delete left " + data.Notes.Count + " note(s), wanted " + remaining[i]);
                }
            }

            if (NoteCardCount(view) != 0)
                throw new InvalidOperationException("the emptied list still rendered " + NoteCardCount(view) + " card(s)");

            var pill = view.FindName("AddPill") as FrameworkElement;
            if (pill == null) throw new InvalidOperationException("the add row is missing");
            if (pill.Visibility != Visibility.Visible)
                throw new InvalidOperationException("the add row is hidden on an empty list: " + pill.Visibility);
        }

        private static NoteItem NewNote(string id, string text, DateTime modifiedAt)
        {
            return new NoteItem { Id = id, Text = text, CreatedAt = modifiedAt, ModifiedAt = modifiedAt };
        }

        private static Panel NoteItemsPanel(NotePanelView view)
        {
            var panel = view.FindName("ItemsPanel") as Panel;
            if (panel == null) throw new InvalidOperationException("NotePanelView has no ItemsPanel");
            return panel;
        }

        private static int NoteCardCount(NotePanelView view)
        {
            return NoteItemsPanel(view).Children.Count;
        }

        private static Border NoteCardAt(NotePanelView view, int index)
        {
            var panel = NoteItemsPanel(view);
            if (panel.Children.Count <= index)
            {
                throw new InvalidOperationException(
                    "note card " + index + " not found (" + panel.Children.Count + " card(s))");
            }
            var card = panel.Children[index] as Border;
            if (card == null) throw new InvalidOperationException("note card " + index + " is not a Border");
            return card;
        }

        private static List<string> NoteCardTexts(NotePanelView view, int index)
        {
            var stack = NoteCardAt(view, index).Child as StackPanel;
            if (stack == null) throw new InvalidOperationException("note card " + index + " has no content stack");

            var texts = new List<string>();
            foreach (var child in stack.Children)
            {
                var text = child as TextBlock;
                if (text == null) throw new InvalidOperationException("note card " + index + " has a non-text child");
                texts.Add(text.Text);
            }
            return texts;
        }

        private static string NoteCardLine(NotePanelView view, int index, int line)
        {
            var texts = NoteCardTexts(view, index);
            if (texts.Count <= line)
                throw new InvalidOperationException("note card " + index + " has no line " + line);
            return texts[line];
        }

        private static void AssertNoteCardTexts(NotePanelView view, int index, string[] expected)
        {
            var actual = NoteCardTexts(view, index);
            if (actual.Count != expected.Length)
                throw new InvalidOperationException(
                    "note card " + index + " has " + actual.Count + " line(s), wanted " + expected.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i])
                    throw new InvalidOperationException(
                        "note card " + index + " line " + i + " is '" + actual[i] + "', wanted '" + expected[i] + "'");
            }
        }

        /// <summary>
        /// Calls the view's undo through reflection (T11; the RED run has no
        /// such method yet; afterwards it is public, reflection keeps the pin
        /// stable either way - the TodoCalendarView convention).
        /// </summary>
        private static bool InvokeNoteTryUndo(NotePanelView view)
        {
            var method = typeof(NotePanelView).GetMethod("TryUndo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("TryUndo not found on NotePanelView");
            return (bool)method.Invoke(view, null);
        }

        /// <summary>Calls the view's private delete (T11; the RED run has no such
        /// method yet).</summary>
        private static void InvokeNoteDelete(NotePanelView view, NoteItem note)
        {
            var method = typeof(NotePanelView).GetMethod("InvokeDelete",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("InvokeDelete not found on NotePanelView");
            method.Invoke(view, new object[] { note });
        }

        #endregion

        private static int TryCheck(string name, Action check)
        {
            try
            {
                check();
                Console.WriteLine("PASS " + name);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                {
                    Console.WriteLine("  INNER " + inner.GetType().FullName + ": " + inner.Message);
                }
                return 1;
            }
        }

        private static int TryLoad(string name, Func<object> create)
        {
            try
            {
                create();
                Console.WriteLine("PASS " + name + " loads");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                {
                    Console.WriteLine("  INNER " + inner.GetType().FullName + ": " + inner.Message);
                }
                return 1;
            }
        }
    }
}
