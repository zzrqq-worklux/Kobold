using System;
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

                // Regression pins for the drag-anchor defect the manual smoke
                // found (see report): a forwarded move must never move the
                // window before its start event, and the chrome must raise the
                // start while its internal drag flag is still false.
                failures += TryCheck("drag: move without start is ignored", DragMoveWithoutStartIsIgnored);
                failures += TryCheck("drag: start then move moves by the DPI-scaled delta", DragStartThenMoveMoves);
                failures += TryCheck("chrome: start event precedes the drag flag", ChromeRaisesStartBeforeDragActive);

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
                failures += TryCheck("todo: calendar cells are compact", TodoCalendarCellsAreCompact);

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

                // Fix round 12 (R1): the empty-day message is gone; the reveal
                // keeps the input in the pill's collapsed slot (fix round 11's
                // stacking), and an empty blur restores the pill there.
                failures += TryCheck("todo: empty-day input sits in the pill slot", TodoEmptyDayInputSitsInThePillSlot);
                failures += TryCheck("todo: collapsing the empty-day input restores the pill", TodoCollapsingEmptyDayInputRestoresThePill);

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
                // the disc plus the hand-drawn circle (R2).
                failures += TryCheck("todo: selected day uses the hand-drawn circle", TodoSelectedDayUsesTheHandDrawnCircle);
                failures += TryCheck("todo: today marker is a filled circle", TodoTodayMarkerIsAFilledCircle);
                failures += TryCheck("folder: item selection follows the widget colour", FolderSelectionFollowsTheWidgetColor);
                failures += TryCheck("folder: the panel size fallback matches the engine", FolderPanelSizeFallbackMatchesTheEngine);
                failures += TryCheck("folder: the panel adapts columns and scale to its size", FolderAdaptsColumnsAndScaleToItsSize);
                failures += TryCheck("folder: the widget menu offers the item-size submenu", FolderMenuOffersTheItemSizeSubmenu);
                failures += TryCheck("folder: raising the item size grows an open panel", FolderItemSizeChangeGrowsAnOpenPanel);
                failures += TryCheck("folder: widening adds spacing at the set icon size", FolderWideningKeepsTheIconSizeAndAddsSpacing);
                failures += TryCheck("resize: folder re-adapts columns during the drag", FolderReadaptsDuringResize);

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
        /// read - todo 342x335, folder 186x154 (the dynamic MinFolderSize at
        /// the sandbox's 1.1 icon scale) - so a hand-edited or legacy tiny
        /// value cannot open an unusable panel.
        /// </summary>
        private static void PanelPersistedBelowMinimumIsClamped()
        {
            ThemeManager.Initialize("dark");

            var todo = new TodoWidget(new TodoData { PanelWidth = 50, PanelHeight = 100 });
            var (todoWidth, todoHeight) = todo.GetPanelSize();
            if (todoWidth != 342 || todoHeight != 335)
            {
                throw new InvalidOperationException(
                    "the tiny todo panel measured " + todoWidth + "x" + todoHeight + ", wanted 342x335");
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
        /// Task A2.2: shrinking stops at the kind minimum - todo 342x335,
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

            if (Math.Abs(todo.Width - 342) > 0.5 || Math.Abs(todo.Height - 335) > 0.5 ||
                todo.Left != 50 || todo.Top != 60)
            {
                throw new InvalidOperationException(
                    "the shrinking todo drag made the panel " + todo.Width + "x" + todo.Height +
                    " at " + todo.Left + "," + todo.Top + ", wanted 342x335 at 50,60");
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
        /// in the widget's readable accent (an off day, not 班).
        /// </summary>
        private static void TodoHolidayLabelShowsInZh()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("zh");
            try
            {
                var data = new TodoData { SelectedDate = new DateTime(2026, 10, 1), HolidayRegion = "cn" };
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
        /// the stack number -> label -> marks fits the 28 DIP cell.
        /// </summary>
        private static void TodoHolidayLabelFitsTheCell()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("zh");
            try
            {
                var data = new TodoData { SelectedDate = new DateTime(2026, 10, 8) };
                var view = new TodoWidget(data).CalendarView;
                LayoutView(view);

                var cell = DayCellFor(view, 8);
                var label = DayCellLabelBlock(cell);
                if (label == null || label.Text != "寒露")
                {
                    throw new InvalidOperationException(
                        "the 2026-10-08 cell shows '" + (label == null ? "<no label>" : label.Text) +
                        "', wanted the base-layer 寒露");
                }

                if (cell.ActualHeight <= 0 || cell.ActualHeight > 28.01)
                {
                    throw new InvalidOperationException(
                        "the day cell height is " + cell.ActualHeight.ToString("0.##") + ", wanted <= 28");
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
                // the 28 DIP grid cell (the border adds the 1 DIP margin).
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
                        "the number+label+marks stack pokes above the 28 DIP cell: top " +
                        stackTop.ToString("0.##") + " < " + gridCellTop.ToString("0.##"));
                }
                if (marksBottom > gridCellTop + 28.0 + 0.5)
                {
                    throw new InvalidOperationException(
                        "the number+label+marks stack pokes below the 28 DIP cell: bottom " +
                        marksBottom.ToString("0.##") + " > " + (gridCellTop + 28.0).ToString("0.##"));
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
                var workday = new TodoData { SelectedDate = new DateTime(2026, 10, 10) };
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

                var term = new TodoData { SelectedDate = new DateTime(2026, 10, 8) };
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

        /// <summary>Task 2.5: the en UI keeps pure numbers - no label element at all.</summary>
        private static void TodoEnglishDayCellsCarryNoLabel()
        {
            ThemeManager.Initialize("dark");
            Localization.SetLanguage("en");
            try
            {
                var data = new TodoData { SelectedDate = new DateTime(2026, 10, 1), HolidayRegion = "cn" };
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
            var kind = WidgetKindRegistry.Find("todo");

            var menu = WidgetMenuBuilder.Build(widget, kind, null);
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

            var reopened = WidgetMenuBuilder.Build(widget, kind, null);
            var reopenedMenu = FindMenuByHeader(reopened, Localization.Get("Menu_HolidayRegion"));
            if (reopenedMenu == null ||
                !((MenuItem)reopenedMenu.Items[1]).IsChecked ||
                ((MenuItem)reopenedMenu.Items[0]).IsChecked)
            {
                throw new InvalidOperationException(
                    "reopening the menu does not reflect the stored region jp");
            }
        }

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

        /// <summary>The calendar's per-cell height must stay at or under 28 DIP.</summary>
        private static void TodoCalendarCellsAreCompact()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);

            var dayGrid = view.FindName("DayGrid") as UniformGrid;
            if (dayGrid == null || dayGrid.Children.Count == 0 || dayGrid.Children.Count % 7 != 0)
            {
                throw new InvalidOperationException("day grid not built as whole weeks");
            }

            double cellHeight = dayGrid.Height / (dayGrid.Children.Count / 7.0);
            if (cellHeight > 28.0)
                throw new InvalidOperationException("day cell height " + cellHeight.ToString("0.##") + " > 28");
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
        /// Fix round 12 (R1): empty day + reveal - the list still contributes
        /// no row, no message appears, and the revealed input takes the pill's
        /// collapsed slot (fix round 18: the pill's own top margin ADD_GAP is
        /// the only difference between the two positions).
        /// </summary>
        private static void TodoEmptyDayInputSitsInThePillSlot()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);

            var pill = view.FindName("AddPill") as FrameworkElement;
            if (pill == null) throw new InvalidOperationException("AddPill not found");
            double collapsedPillTop = pill.TransformToAncestor(view).Transform(new Point(0, 0)).Y;

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
            double hostTop = host.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            if (Math.Abs(hostTop - (collapsedPillTop - ADD_GAP)) > 1.0)
            {
                throw new InvalidOperationException(
                    "the revealed input did not take the pill's slot: host top " + hostTop.ToString("0.##") +
                    " vs collapsed pill top " + collapsedPillTop.ToString("0.##") +
                    " minus the pill's " + ADD_GAP + " top margin");
            }
        }

        /// <summary>
        /// Fix round 12 (R1): an empty blur collapses the revealed input back
        /// to the pill, which lands in the revealed input's slot plus its own
        /// ADD_GAP top margin (fix round 18; no message row appears and the
        /// list stays at zero rows).
        /// </summary>
        private static void TodoCollapsingEmptyDayInputRestoresThePill()
        {
            ThemeManager.Initialize("dark");
            var view = new TodoWidget(new TodoData()).CalendarView;
            LayoutView(view);
            RevealAddBox(view);
            LayoutView(view);

            var host = view.FindName("AddInputHost") as FrameworkElement;
            if (host == null) throw new InvalidOperationException("AddInputHost not found");
            double revealedHostTop = host.TransformToAncestor(view).Transform(new Point(0, 0)).Y;

            var box = view.FindName("AddBox") as TextBox;
            if (box == null) throw new InvalidOperationException("AddBox not found");
            RaiseLostFocus(box); // an empty blur collapses the revealed input
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
            if (host.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("the input is still visible after the collapse");
            double pillTop = pill.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            if (Math.Abs(pillTop - revealedHostTop - ADD_GAP) > 1.0)
            {
                throw new InvalidOperationException(
                    "the pill did not return " + ADD_GAP + " below the revealed input's slot: pill top " +
                    pillTop.ToString("0.##") + " vs host top " + revealedHostTop.ToString("0.##"));
            }
        }

        /// <summary>
        /// Fix round 12 (R2): GetPanelSize() must include the chrome shell's
        /// 1px border on every side, or the arranged content host loses 2 DIP
        /// and WPF's layout clip cuts the view's bottom (the pill's lower
        /// border). Pinned for the collapsed and the revealed empty day, whose
        /// DesiredHeight differ (fix round 12, R1).
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
        }

        /// <summary>
        /// Fix round 17 (R1): a day switch must publish the new day's height
        /// even on an unrealized tree, where the rebuilt list can otherwise be
        /// measured from the previous day's cache. One item = 303, an empty
        /// day = 273 in this layout (each +6 for fix round 18's pill margin);
        /// Refresh now invalidates the root before the manual measure.
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
        }

        /// <summary>
        /// Fix round 15 (R2): the today cell carries a filled widget-colour
        /// disc (w == h >= 18) with the white OnAccent number and a transparent
        /// border; with today SELECTED it showed the disc inside the hand-drawn
        /// circle. Fix round 16 (R2) flips the not-selected expectation: today
        /// always shows the disc AND the circle (>= 3 paths stroked in the
        /// widget colour) together, while a selected-only day keeps the circle
        /// and no disc. The disc is the only day-cell Ellipse at >= 18 DIP, so
        /// it finds the today cell under either contract.
        /// Fix round 15b: a near-white widget colour under the dark theme must
        /// still give the disc a non-light fill (brightness <= 0.8) so the
        /// white number does not vanish; the number stays OnAccent.
        /// </summary>
        private static void TodoTodayMarkerIsAFilledCircle()
        {
            ThemeManager.Initialize("dark");
            DateTime today = DateTime.Today;
            DateTime other = new DateTime(today.Year, today.Month, today.Day == 15 ? 16 : 15);
            Color wanted = Utils.HexToColor("#E8A33D");

            // Today NOT selected: the disc AND the hand-drawn circle mark it.
            var data = new TodoData { SelectedDate = other, Color = "#E8A33D" };
            var view = new TodoWidget(data).CalendarView;
            LayoutView(view);

            var cell = TodayDiscCell(view, wanted);
            if (!ContainsText(cell, today.Day.ToString(CultureInfo.InvariantCulture)))
            {
                throw new InvalidOperationException(
                    "the disc cell does not show today's number " + today.Day);
            }
            AssertTodayDisc(cell, wanted);
            AssertTransparentCellBorder(cell);
            int paths = CountPathsStroked(cell, wanted);
            if (paths < 3)
            {
                throw new InvalidOperationException(
                    "the unselected today cell has " + paths +
                    " path(s) stroked in the widget colour, wanted at least 3");
            }
            AssertTodayNumberIsOnAccent(cell, today.Day);

            // The same view's selected-only day (not today): circle, no disc.
            var selectedOnly = DayCellFor(view, other.Day);
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

            // Today SELECTED: the disc sits inside the hand-drawn circle.
            var selectedData = new TodoData { SelectedDate = today, Color = "#E8A33D" };
            var selectedView = new TodoWidget(selectedData).CalendarView;
            LayoutView(selectedView);

            var selectedCell = TodayDiscCell(selectedView, wanted);
            AssertTodayDisc(selectedCell, wanted);
            int strokes = CountPathsStroked(selectedCell, wanted);
            if (strokes < 3)
            {
                throw new InvalidOperationException(
                    "the selected today cell has " + strokes +
                    " path(s) stroked in the widget colour, wanted at least 3");
            }
            AssertTodayNumberIsOnAccent(selectedCell, today.Day);

            // Fix round 15b: a near-white widget colour under the dark theme
            // must not leave a near-white disc under the white number.
            var lightData = new TodoData { SelectedDate = other, Color = "#FFFFFF" };
            var lightView = new TodoWidget(lightData).CalendarView;
            LayoutView(lightView);

            var lightCell = TodayAnyDiscCell(lightView);
            if (!ContainsText(lightCell, today.Day.ToString(CultureInfo.InvariantCulture)))
            {
                throw new InvalidOperationException(
                    "the near-white disc cell does not show today's number " + today.Day);
            }
            var lightDisc = FindTodayDisc(lightCell);
            var lightFill = lightDisc.Fill as SolidColorBrush;
            if (lightFill == null)
            {
                throw new InvalidOperationException(
                    "the near-white disc has no solid fill: " +
                    (lightDisc.Fill == null ? "null" : lightDisc.Fill.ToString()));
            }
            double lightBrightness = PerceivedBrightness(lightFill.Color);
            if (lightBrightness > 0.8)
            {
                throw new InvalidOperationException(
                    "the near-white disc keeps brightness " +
                    lightBrightness.ToString("0.###") + " > 0.8 under the dark theme");
            }
            AssertTodayNumberIsOnAccent(lightCell, today.Day);
        }

        /// <summary>The cell's disc: a widget-coloured Ellipse at 18 DIP or more, square.</summary>
        private static void AssertTodayDisc(Border cell, Color wanted)
        {
            var disc = FindDisc(cell, wanted);
            if (disc == null)
            {
                throw new InvalidOperationException(
                    "the today cell has no Ellipse filled with the widget colour " + Describe(wanted));
            }
            if (disc.Width != disc.Height || disc.Width < 18)
            {
                throw new InvalidOperationException(
                    "the today disc is " + disc.Width.ToString("0.##") + "x" +
                    disc.Height.ToString("0.##") + ", wanted a square of at least 18");
            }
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

        /// <summary>Today's number is white (the shared OnAccent brush) on the disc.</summary>
        private static void AssertTodayNumberIsOnAccent(Border cell, int day)
        {
            var number = FirstDescendant<TextBlock>(cell);
            if (number == null || number.Text != day.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("the today cell lost its day number");
            }
            if (number.Foreground != ThemeManager.OnAccentBrush)
            {
                throw new InvalidOperationException(
                    "the today number is not OnAccent: " +
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

        /// <summary>The only day cell whose subtree holds the widget-coloured disc (today).</summary>
        private static Border TodayDiscCell(TodoCalendarView view, Color wanted)
        {
            var grid = view.FindName("DayGrid") as UniformGrid;
            if (grid == null) throw new InvalidOperationException("the day grid was not found");

            foreach (var child in grid.Children)
            {
                var cell = child as Border;
                if (cell == null) continue;
                if (FindDisc(cell, wanted) != null) return cell;
            }
            throw new InvalidOperationException("no today disc cell was found");
        }

        /// <summary>
        /// Fix round 15b: the today cell whatever the disc's fill - the only
        /// day-cell Ellipse at >= 18 DIP (the circle's ink dots stay below).
        /// </summary>
        private static Border TodayAnyDiscCell(TodoCalendarView view)
        {
            var grid = view.FindName("DayGrid") as UniformGrid;
            if (grid == null) throw new InvalidOperationException("the day grid was not found");

            foreach (var child in grid.Children)
            {
                var cell = child as Border;
                if (cell == null) continue;
                if (FindTodayDisc(cell) != null) return cell;
            }
            throw new InvalidOperationException("no today disc cell was found");
        }

        /// <summary>
        /// The widget-coloured disc: an Ellipse of at least 18 DIP filled with
        /// the colour. The circle's ink dots are smaller and stay ignored.
        /// </summary>
        private static Ellipse FindDisc(DependencyObject root, Color fill)
        {
            var ellipse = root as Ellipse;
            if (ellipse != null)
            {
                var brush = ellipse.Fill as SolidColorBrush;
                if (brush != null && brush.Color == fill &&
                    ellipse.Width >= 18 && ellipse.Height >= 18)
                {
                    return ellipse;
                }
            }
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindDisc(VisualTreeHelper.GetChild(root, i), fill);
                if (found != null) return found;
            }
            return null;
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
                var menu = WidgetMenuBuilder.Build(widget, WidgetKindRegistry.Find("folder"), null);

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
