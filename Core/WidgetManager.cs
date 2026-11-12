using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Kobold.Core;
using Kobold.Core.FileSearch;
using Kobold.Core.Icons;
using Kobold.Controls;
using Kobold.Services;
using Kobold.Windows;

namespace Kobold.Core
{
    /// <summary>
    /// Manages all folder widgets - singleton pattern
    /// </summary>
    public class WidgetManager
    {
        private static WidgetManager _instance;
        private static readonly object _lock = new object();

        // Set by OverrideConfigPathsForChecks before the first Instance access:
        // the desktop self-checks point the singleton at a throwaway sandbox
        // so their saves can never rewrite the user's real config. Null in
        // production, where the shared %AppData% paths are used.
        private static string _configPathOverride;
        private static string _backupPathOverride;
        
        private AppConfig _config;
        private List<IWidgetWindow> _widgets = new List<IWidgetWindow>();
        private IslandWindow _island;
        private FullscreenWatcher _fullscreenWatcher;

        private readonly IdleTrimPolicy _trimPolicy = new IdleTrimPolicy();
        private readonly System.Windows.Threading.DispatcherTimer _trimTimer;
        private DateTime _lastInteractionUtc = DateTime.UtcNow;
        private DateTime? _lastTrimUtc;

        // >0 while a merge/detach swap is in flight. Island refreshes raised
        // by visibility/data events inside the scope only set the pending flag;
        // the outermost EndHostTransition repaints once, after the new host is
        // in place and the old one is gone.
        private int _hostTransitionDepth;
        private bool _islandRefreshPending;

        public static WidgetManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new WidgetManager();
                        }
                    }
                }
                return _instance;
            }
        }

        public AppConfig Config => _config;
        public IReadOnlyList<IWidgetWindow> Widgets => _widgets.AsReadOnly();

        /// <summary>The last successful idle working-set trim, or null before the first one.</summary>
        public DateTime? LastTrimUtc => _lastTrimUtc;

        /// <summary>True while the current idle period has already been trimmed.</summary>
        public bool TrimmedThisIdlePeriod => _trimPolicy.TrimmedThisIdlePeriod;

        /// <summary>True while the fullscreen watcher says the island must step aside.</summary>
        public bool FullscreenAvoiding => _fullscreenWatcher != null && _fullscreenWatcher.IsAvoiding;

        /// <summary>True while the island window is on screen.</summary>
        public bool IslandVisible => _island != null && _island.IsVisible;

        /// <summary>True while the island is expanded into its tile row.</summary>
        public bool IslandExpanded => _island != null && _island.IsExpanded;

        /// <summary>
        /// Redirects the singleton's config/backup paths before it is first
        /// created. Desktop self-checks use this to keep their saves (debounced
        /// ones included) out of the user's real config; production code must
        /// never call it.
        /// </summary>
        public static void OverrideConfigPathsForChecks(string configPath, string backupPath)
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    throw new InvalidOperationException(
                        "OverrideConfigPathsForChecks must run before the first WidgetManager.Instance access");
                }
                _configPathOverride = configPath;
                _backupPathOverride = backupPath;
            }
        }

        private WidgetManager()
        {
            _config = _configPathOverride != null
                ? AppConfig.Load(_configPathOverride, _backupPathOverride)
                : AppConfig.Load();
            Localization.CurrentLanguage = _config.Language;
            
            // Initialize theme system with current theme
            ThemeManager.Initialize(_config.Theme ?? "dark");
            IconStyleManager.Set(_config.IconStyle ?? IconStyleIds.Classic);

            // WPF keeps its render caches for the life of the process (hiding or
            // closing a window returns almost nothing), so a long-idle app hands
            // its working set back to the OS instead. See Services/MemoryTrimmer.
            _trimTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = IdleTrimPolicy.CheckInterval
            };
            _trimTimer.Tick += OnTrimTick;
        }

        /// <summary>
        /// Initializes and creates all widgets from config
        /// </summary>
        public void Initialize()
        {
            // The index service never reads config itself: hand it the saved
            // settings first, then let the presence of a search widget decide
            // whether the store is opened (and whether onboarding must run).
            FileSearchIndexService.Instance.ApplySettings(_config.FileSearch);

            // Group members get exactly one host: the group window. A group
            // whose window cannot be built (an unsupported member kind) falls
            // back to standalone member windows instead of failing startup.
            var orphanedByGroup = new HashSet<string>(StringComparer.Ordinal);
            if (_config.Groups != null)
            {
                foreach (var group in _config.Groups)
                {
                    var members = GroupHostPolicy.MembersOf(group, _config.Widgets);
                    if (members.Count < 2)
                    {
                        foreach (var member in members) orphanedByGroup.Add(member.Id);
                        continue;
                    }

                    try
                    {
                        if (CreateGroupInternal(group, members) == null)
                            foreach (var member in members) orphanedByGroup.Add(member.Id);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[Kobold] group window fallback: " + ex.Message);
                        foreach (var member in members) orphanedByGroup.Add(member.Id);
                    }
                }
            }

            // One window per config entry of a known kind; unknown kinds stay
            // in the config untouched but get no window. Grouped members never
            // get a standalone window (except the fallback above).
            foreach (var data in _config.Widgets)
            {
                if (data == null) continue;
                if (!string.IsNullOrEmpty(data.GroupId) && !orphanedByGroup.Contains(data.Id)) continue;
                CreateWidgetInternal(data);
            }

            FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);

            // The island is the shared entry point that launches widget panels
            _island = new IslandWindow();
            _island.WidgetActivated += OnWidgetActivated;
            _island.WidgetMenuRequested += OnWidgetMenuRequested;
            _island.GroupTileClicked += OnIslandGroupTileClicked;
            RefreshIsland();
            _island.Show();

            // Keep the always-on-top island out of the way of fullscreen games/videos.
            _fullscreenWatcher = new FullscreenWatcher(avoid => _island?.SetTopmost(!avoid));
            _fullscreenWatcher.Start();

            // Reopen the panels that were still open when the app last closed.
            // No activation: starting up must not steal focus from other apps.
            // Groups restore first (once, through their own state) so their
            // members are not reopened as standalone panels afterwards.
            foreach (var group in GroupStartupPolicy.ToRestoreGroups(_config.Groups, _config.Widgets))
            {
                var groupWindow = FindGroupWindow(group.Id);
                if (groupWindow == null) continue;

                OpenPanel(groupWindow, activate: false);
                string activeId = GroupHostPolicy.ActiveMemberId(group, _config.Widgets);
                if (activeId != null) groupWindow.SetActiveByWidgetId(activeId);
            }

            foreach (var data in StartupPanels.ToRestore(_config.Widgets))
            {
                var widget = _widgets.FirstOrDefault(w => w.WidgetId == data.Id);
                if (widget != null) OpenPanel(widget, activate: false);
            }

            _trimTimer.Start();
        }

        /// <summary>Any input anywhere in the app restarts the idle window the trim uses.</summary>
        public void NotifyInteraction()
        {
            _lastInteractionUtc = DateTime.UtcNow;
            _trimPolicy.NotifyInteraction();
        }

        /// <summary>True while something in flight must not be paged out mid-way.</summary>
        private bool IsBusy()
        {
            if (DragDropSession.Current != null) return true;
            return _widgets.Any(w => w.IsModalOpen);
        }

        private void OnTrimTick(object sender, EventArgs e)
        {
            if (!_trimPolicy.ShouldTrim(DateTime.UtcNow - _lastInteractionUtc, IsBusy())) return;

            if (MemoryTrimmer.Trim())
            {
                _lastTrimUtc = DateTime.UtcNow;
                _trimPolicy.NotifyTrimmed();
            }
        }

        /// <summary>
        /// Refreshes the widget tiles shown by the island. A group folds into
        /// one tile carrying its member descriptors; only the open group tile
        /// carries the neutral open ring.
        /// </summary>
        public void RefreshIsland()
        {
            if (_hostTransitionDepth > 0)
            {
                _islandRefreshPending = true;
                return;
            }
            RefreshIslandNow();
        }

        private void RefreshIslandNow()
        {
            _island?.SetWidgets(_config.Widgets,
                GroupHostPolicy.BuildIslandGroupTiles(_config.Groups, _config.Widgets, IsHostOpen));
            _island?.SetOpenStates(GroupHostPolicy.OpenProjectedTileIds(
                _config.Groups, _config.Widgets, IsHostOpen));
        }

        /// <summary>
        /// Starts a host swap. Every island refresh raised until the matching
        /// <see cref="EndHostTransition"/> is folded into one deferred repaint,
        /// so a visibility event cannot rebuild the island mid-swap.
        /// </summary>
        private void BeginHostTransition()
        {
            _hostTransitionDepth++;
        }

        /// <summary>Ends a host swap; the outermost call performs exactly one refresh.</summary>
        private void EndHostTransition()
        {
            if (_hostTransitionDepth <= 0) return;
            _hostTransitionDepth--;
            if (_hostTransitionDepth > 0 || !_islandRefreshPending) return;

            _islandRefreshPending = false;
            RefreshIslandNow();
        }

        /// <summary>True while the host window with this id (group id or widget id) is open.</summary>
        private bool IsHostOpen(string hostId)
        {
            if (string.IsNullOrEmpty(hostId)) return false;

            foreach (var widget in _widgets)
            {
                if (widget != null && string.Equals(widget.WidgetId, hostId, StringComparison.Ordinal))
                    return widget.IsPanelOpen;
            }
            return false;
        }

        /// <summary>True when the widget's panel lives in a widget window or a group tab.</summary>
        private WidgetGroupWindow FindGroupWindow(string groupId)
        {
            if (string.IsNullOrEmpty(groupId)) return null;

            foreach (var widget in _widgets)
            {
                var group = widget as WidgetGroupWindow;
                if (group != null && string.Equals(group.WidgetId, groupId, StringComparison.Ordinal))
                    return group;
            }
            return null;
        }

        /// <summary>
        /// The window that owns a widget: a direct window id first (group id or
        /// standalone widget id), then the group window whose tabs contain it.
        /// </summary>
        public IWidgetWindow FindHostForWidget(string widgetId)
        {
            if (string.IsNullOrEmpty(widgetId)) return null;

            foreach (var widget in _widgets)
            {
                if (widget != null && string.Equals(widget.WidgetId, widgetId, StringComparison.Ordinal))
                    return widget;
            }

            foreach (var widget in _widgets)
            {
                var group = widget as WidgetGroupWindow;
                if (group != null && group.ContainsWidget(widgetId)) return group;
            }
            return null;
        }

        /// <summary>
        /// Opens the clicked widget's panel centered below the island. A group
        /// member opens its group and switches to that tab - a member click
        /// never collapses the group.
        /// </summary>
        private void OnWidgetActivated(string widgetId)
        {
            var host = FindHostForWidget(widgetId);
            if (host == null || _island == null) return;

            var group = host as WidgetGroupWindow;
            if (group == null)
            {
                // Standalone widget: the unchanged toggle. Its own visibility
                // event mirrors the state onto the tile.
                if (host.IsPanelOpen)
                {
                    host.HidePanel();
                    return;
                }
                OpenPanel(host);
                return;
            }

            bool isMember = group.ContainsWidget(widgetId);
            if (!group.IsPanelOpen)
            {
                OpenPanel(group);
                if (isMember) group.SetActiveByWidgetId(widgetId);
                SaveConfig();
                RefreshIsland();
                return;
            }

            // The group is already open: always switch to the clicked member,
            // even when it is the active tab. A member click never closes the
            // group (only a standalone tile toggles).
            if (isMember)
            {
                group.SetActiveByWidgetId(widgetId);
                SaveConfig();
            }
            RefreshIsland();
        }

        /// <summary>
        /// Opens a widget's menu when its island tile is right-clicked, keeping
        /// the island expanded while the menu is up. A grouped member's menu is
        /// the group menu, with that member switched to first.
        /// </summary>
        private void OnWidgetMenuRequested(string widgetId)
        {
            var host = FindHostForWidget(widgetId);
            if (host == null || _island == null) return;

            var group = host as WidgetGroupWindow;
            if (group != null && group.ContainsWidget(widgetId))
            {
                group.SetActiveByWidgetId(widgetId);
                SaveConfig();
                RefreshIsland();
            }

            _island.PauseAutoCollapse();
            host.ShowWidgetMenu(() => _island.ResumeAutoCollapse());
        }

        /// <summary>
        /// A group tile click toggles that group's inline island expansion and
        /// repaints the row. The island itself never reads the group model; it
        /// only reports the click, and the persisted flag survives an island
        /// collapse and a restart.
        /// </summary>
        private void OnIslandGroupTileClicked(string groupId)
        {
            var group = _config.Groups?.FirstOrDefault(g => g != null && g.Id == groupId);
            if (group == null) return;
            group.IslandExpanded = !group.IslandExpanded;
            SaveConfig();
            RefreshIsland();
        }

        /// <summary>Opens a widget's panel; defaults to centered below the island when unplaced.</summary>
        private void OpenPanel(IWidgetWindow widget, bool activate = true, bool animate = true)
        {
            if (widget == null || _island == null) return;
            var (panelWidth, _) = widget.GetPanelSize();
            double left = _island.CenterX - panelWidth / 2; // clamped per-monitor inside ShowPanel
            widget.ShowPanel(left, _island.PanelTop, activate, animate);
        }

        /// <summary>Toggles a widget's panel - shared by the widget menu.</summary>
        public void ToggleWidgetPanel(IWidgetWindow widget)
        {
            if (widget == null) return;
            if (widget.IsPanelOpen)
            {
                widget.HidePanel();
                return;
            }
            OpenPanel(widget);
        }

        /// <summary>
        /// New widgets cycle blue -&gt; red -&gt; green -&gt; purple -&gt; orange, the
        /// look the old 12-colour picker produced. The hexes are read from
        /// <see cref="UiTokens.FolderPalette"/> (pinned to the folder palette by
        /// tests/MenuRenderCheck) so they stay defined in one place.
        /// </summary>
        private static readonly string[] CreationColors =
        {
            UiTokens.FolderPalette[6], // blue
            UiTokens.FolderPalette[0], // red
            UiTokens.FolderPalette[4], // green
            UiTokens.FolderPalette[7], // purple
            UiTokens.FolderPalette[1]  // orange
        };

        /// <summary>
        /// Creates a widget of the given kind with the default name, palette
        /// colour and staggered position - shared by the island add menu and
        /// the tray new-widget submenu. The interactive path opens the panel
        /// pinned so it survives while being filled.
        /// </summary>
        public IWidgetWindow CreateWidget(string kindId)
        {
            return CreateWidgetCore(kindId, open: true, pinned: true);
        }

        /// <summary>
        /// The control-plane variant: the same default data and config entry as
        /// <see cref="CreateWidget"/>, but the panel stays closed and unpinned.
        /// The caller can apply its own name/color/position and refresh the
        /// island afterwards (see WidgetGroupControlService).
        /// </summary>
        public IWidgetWindow CreateWidgetForControl(string kindId)
        {
            return CreateWidgetCore(kindId, open: false, pinned: false);
        }

        /// <summary>
        /// The one create path both callers share, so the UI and the control
        /// plane cannot drift in default data, position, colour, naming,
        /// folder grid, size migration, config insertion or island refresh.
        /// </summary>
        private IWidgetWindow CreateWidgetCore(string kindId, bool open, bool pinned)
        {
            var kind = WidgetKindRegistry.Find(kindId);
            if (kind == null) return null;

            var data = kind.CreateDefault();
            int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            int screenHeight = (int)SystemParameters.PrimaryScreenHeight;
            int count = _widgets.Count;

            data.PosX = screenWidth / 2 - 50 + (count % 5) * 30;
            data.PosY = screenHeight / 2 - 50 + (count % 5) * 30;
            data.Color = CreationColors[count % CreationColors.Length];
            data.Name = Localization.Get(kind.NameKey);
            data.IsPanelPinned = pinned;

            if (data is FolderData folder) folder.GridColumns = _config.DefaultGridColumns;

            // The one-time default size lands before the first save, so a new
            // widget opens at its migrated size and keeps it afterwards.
            PanelSizeMigration.FillMissingSizes(data, _config.ItemScale, _config.Language, DateTime.Today);

            _config.AddWidget(data);
            var widget = CreateWidgetInternal(data);
            RefreshIsland();
            if (open) OpenPanel(widget);
            SaveConfig();
            return widget;
        }

        /// <summary>
        /// Brings the island up so all widget entries are visible.
        /// </summary>
        public void ShowIsland()
        {
            _island?.ShowIsland();
        }

        /// <summary>Opens the settings window - shared by the tray menu and the island entry.</summary>
        public void ShowSettings()
        {
            new SettingsWindow().ShowDialog();
        }

        /// <summary>Pushes the persisted island settings (collapse delay + position) to the island.</summary>
        public void ApplyIslandSettings()
        {
            _island?.ApplySettings();
        }

        /// <summary>Rebuilds the island tiles after the widget icon style changed.</summary>
        public void ApplyIconStyle()
        {
            _island?.RefreshIconStyle();
        }

        /// <summary>
        /// Re-adapts every open folder host to a changed item scale. A group
        /// re-adapts its active folder through its content box; standalone
        /// folders regrow when the new size falls below their minimum. The
        /// final repaint picks the new scale up everywhere.
        /// </summary>
        public void ApplyItemScaleChange()
        {
            foreach (var host in _widgets.ToList())
            {
                var folder = host as FolderWidget;
                if (folder != null) folder.ApplyItemScaleChange();

                var group = host as WidgetGroupWindow;
                if (group != null) group.ApplyItemScaleChange();
            }
            RefreshAllWidgets();
        }

        /// <summary>Applies the saved "hide desktop icons" preference at startup.</summary>
        public void ApplyDesktopIconsOnStartup()
        {
            // Remember the state before this run changes anything.
            _config.HideIconsOriginal = DesktopIcons.ReadRaw();
            if (_config.HideDesktopIcons) DesktopIcons.SetHidden(true);
            _config.Save();
        }

        /// <summary>
        /// Restores the pre-run desktop-icon state on exit, but only while the
        /// preference is still "hidden" - if the user unchecked it we leave icons shown.
        /// </summary>
        public void RestoreDesktopIconsOnExit()
        {
            if (_config.HideDesktopIcons) DesktopIcons.Restore(_config.HideIconsOriginal);
        }

        /// <summary>
        /// Hides/shows the desktop icons directly and remembers the choice.
        /// Returns false when the system refused the change, so the caller can revert.
        /// </summary>
        public bool SetHideDesktopIcons(bool hide)
        {
            if (!DesktopIcons.SetHidden(hide)) return false;
            _config.HideDesktopIcons = hide;
            _config.Save();
            return true;
        }

        /// <summary>
        /// The control-plane variant: records the requested value and applies
        /// it without saving (the router owns the save). Returns false when the
        /// desktop refused the change; the requested value is still kept so the
        /// next startup retries it.
        /// </summary>
        public bool ApplyHideDesktopIconsForControl(bool hide)
        {
            _config.HideDesktopIcons = hide;
            return DesktopIcons.SetHidden(hide);
        }

        /// <summary>
        /// Creates the window for an existing config entry and wires it to the
        /// manager. Unknown kinds have no window: they stay config-only.
        /// </summary>
        private IWidgetWindow CreateWidgetInternal(WidgetData data, IWidgetContentView contents = null)
        {
            IWidgetWindow widget = contents == null
                ? CreateWindow(data)
                : WidgetWindowFactory.Create(data, contents);
            if (widget == null) return null;

            widget.OnDeleted += (w) =>
            {
                _widgets.Remove(w);
                _config.RemoveWidget(w.WidgetId);
                RefreshIsland();
                // The last search widget stops the shared index; the service keeps
                // the database so re-adding the widget reopens it without a rescan.
                FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
            };

            widget.OnDataChanged += () =>
            {
                SaveConfig();
                RefreshIsland();
            };

            // Keep the island tile in step with the real panel state. The
            // island may not exist yet during startup; the closure resolves it
            // when the event fires, and RefreshIsland also seeds the snapshot.
            widget.PanelVisibilityChanged += open => _island?.SetOpenState(widget.WidgetId, open);

            _widgets.Add(widget);
            FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);

            return widget;
        }

        /// <summary>
        /// Creates a standalone host around an already-released live view. A
        /// kind/adoption failure falls back to a fresh view so the datum never
        /// loses its reachable host; the flushed data is the source of truth.
        /// </summary>
        private IWidgetWindow CreateStandaloneHost(WidgetData data, IWidgetContentView contents)
        {
            if (data == null) return null;

            try
            {
                return CreateWidgetInternal(data, contents);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] adopted-host fallback: " + ex.Message);
            }

            try
            {
                return CreateWidgetInternal(data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] standalone-host fallback: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Creates the shared window for a config group and wires it to the
        /// manager. Group state is live in <see cref="AppConfig.Groups"/>, so the
        /// existing save path persists geometry, flags and the active member.
        /// </summary>
        private WidgetGroupWindow CreateGroupInternal(WidgetGroup group, List<WidgetData> members,
            List<IWidgetContentView> views = null)
        {
            if (group == null || members == null || members.Count < 2) return null;

            var window = WidgetWindowFactory.CreateGroup(group, members, views) as WidgetGroupWindow;
            if (window == null) return null;

            WireGroupWindow(window);
            _widgets.Add(window);
            return window;
        }

        /// <summary>
        /// The group window's manager wiring: edits save + repaint the island,
        /// visibility repaints the open ring, and a group delete removes every
        /// member (a group delete is never a single-member delete).
        /// </summary>
        private void WireGroupWindow(WidgetGroupWindow window)
        {
            if (window == null) return;

            window.OnDeleted += OnGroupDeleted;

            window.OnDataChanged += () =>
            {
                SaveConfig();
                RefreshIsland();
            };

            window.PanelVisibilityChanged += open => RefreshIsland();
        }

        /// <summary>Removes a group window together with every member it hosts.</summary>
        private void OnGroupDeleted(IWidgetWindow deleted)
        {
            var window = deleted as WidgetGroupWindow;
            if (window != null) RemoveGroupHost(window);
        }

        private void RemoveGroupHost(WidgetGroupWindow window)
        {
            if (window == null) return;

            var members = GroupHostPolicy.MembersOf(window.Group, _config.Widgets);
            WidgetGroup group = window.Group;

            BeginHostTransition();
            try
            {
                _widgets.Remove(window);
                window.Close();

                // Drop the group entry once; removing each member afterwards
                // must not re-discover and clean the same group repeatedly.
                RemoveGroupFromConfig(group);
                foreach (var member in members) _config.RemoveWidget(member.Id);

                RefreshIsland();
                FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
            }
            finally
            {
                EndHostTransition();
            }
        }

        /// <summary>Removes one group state entry (never its member widgets).</summary>
        private void RemoveGroupFromConfig(WidgetGroup group)
        {
            if (group == null || _config.Groups == null) return;

            _config.Groups.Remove(group);
            if (_config.Groups.Count == 0) _config.Groups = null;
        }

        /// <summary>
        /// The window class for a data entry, chosen through the kind registry
        /// so unknown kinds are skipped without touching their data.
        /// </summary>
        private static IWidgetWindow CreateWindow(WidgetData data)
        {
            if (data == null || WidgetKindRegistry.Find(data.Kind) == null) return null;

            return WidgetWindowFactory.Create(data);
        }

        /// <summary>
        /// Removes a widget by ID. A grouped member keeps the rest of the group
        /// alive: the old group window is replaced by one for the survivors, or
        /// by a standalone window once the group dissolved.
        /// </summary>
        public void RemoveWidget(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            // A group member has no window whose WidgetId is its own id (the
            // group window carries the group id), so the host lookup must fall
            // through to group membership or member removal would silently
            // no-op - and T6's detach/dissolve could never reach this path.
            var host = FindHostForWidget(id);
            if (host == null) return;

            var groupHost = host as WidgetGroupWindow;
            if (groupHost == null)
            {
                _widgets.Remove(host);
                if (host is Window window) window.Close();
                _config.RemoveWidget(id);
                RefreshIsland();
                FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
                return;
            }

            if (string.Equals(groupHost.WidgetId, id, StringComparison.Ordinal))
            {
                // The group id was addressed: a group removal takes its members.
                RemoveGroupHost(groupHost);
                return;
            }

            if (groupHost.ContainsWidget(id)) RemoveGroupMember(groupHost, id);
        }

        /// <summary>
        /// Deletes one member from a group host. The group survives in place
        /// with its remaining members, or dissolves into standalone hosts (the
        /// survivor keeps its exact live view). The deleted member's view is
        /// discarded; no surviving view is recreated.
        /// </summary>
        private void RemoveGroupMember(WidgetGroupWindow groupWindow, string memberId)
        {
            if (groupWindow == null) return;

            WidgetGroup group = groupWindow.Group;
            if (group == null)
            {
                RemoveWidgetFromConfigOnly(memberId);
                return;
            }

            var membersBefore = GroupHostPolicy.MembersOf(group, _config.Widgets);
            bool wasOpen = groupWindow.IsPanelOpen;
            int index = _widgets.IndexOf(groupWindow);

            // Capture the group placement before the window may go away.
            double? left = group.PanelX ?? Finite(groupWindow.Left);
            double? top = group.PanelY ?? Finite(groupWindow.Top);
            double currentDpi = DpiScaleOf(groupWindow);
            double? dpi = group.PanelX.HasValue || group.PanelY.HasValue
                ? (group.PanelDpiScale ?? currentDpi)
                : currentDpi;

            var survivors = new List<WidgetData>();
            foreach (var member in membersBefore)
            {
                if (member == null || string.Equals(member.Id, memberId, StringComparison.Ordinal)) continue;
                if (IsInConfig(member.Id)) survivors.Add(member);
            }

            bool groupSurvives = survivors.Count >= 2
                && _config.Groups != null
                && _config.Groups.Contains(group);

            BeginHostTransition();
            try
            {
                // Take the deleted member out first, then either keep the
                // surviving group in place or dissolve its last survivor.
                DiscardContentView(groupWindow.RemoveMember(memberId));

                if (groupSurvives)
                {
                    for (int i = 0; i < survivors.Count; i++) survivors[i].GroupOrder = i;
                    if (GroupHostPolicy.IndexOfMember(group, _config.Widgets, group.ActiveWidgetId) < 0)
                        group.ActiveWidgetId = survivors[0].Id;
                    group.IsPanelPinned = GroupHostPolicy.AnyMemberPinned(survivors);

                    // The group window is untouched: same instance, same tabs,
                    // same size/position/open state.
                    _config.RemoveWidget(memberId);

                    SaveConfig();
                    RefreshIsland();
                    FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
                    return;
                }

                // The group cannot survive: move every remaining member's live
                // view into its own host before the old window closes.
                var dissolved = new List<KeyValuePair<WidgetData, IWidgetContentView>>();
                for (int i = 0; i < survivors.Count; i++)
                {
                    WidgetData survivor = survivors[i];
                    IWidgetContentView view = groupWindow.RemoveMember(survivor.Id);
                    ApplyDissolvedStandaloneState(survivor, group, left, top, dpi, wasOpen, i + 1);
                    dissolved.Add(new KeyValuePair<WidgetData, IWidgetContentView>(survivor, view));
                }

                RemoveGroupFromConfig(group);

                int insert = index;
                foreach (var entry in dissolved)
                {
                    var host = CreateStandaloneHost(entry.Key, entry.Value);
                    if (host == null) continue;

                    MoveHostTo(host, insert);
                    if (insert >= 0) insert++;
                    if (wasOpen) OpenPanel(host, activate: false, animate: false);
                }

                CloseHost(groupWindow, flush: false);
                RemoveWidgetFromConfigOnly(memberId);

                SaveConfig();
                RefreshIsland();
                FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
            }
            finally
            {
                EndHostTransition();
            }
        }

        /// <summary>Drops a config entry without going through a host lookup.</summary>
        private void RemoveWidgetFromConfigOnly(string widgetId)
        {
            if (string.IsNullOrEmpty(widgetId)) return;
            if (IsInConfig(widgetId)) _config.RemoveWidget(widgetId);
        }

        /// <summary>
        /// The deleted member's view is no longer hosted: let a folder eject
        /// its stored files, then settle any service subscription it owns (the
        /// search kind) before letting it go.
        /// </summary>
        internal static void DiscardContentView(IWidgetContentView view)
        {
            if (view == null) return;

            var folder = view as IFolderPanelLifecycle;
            if (folder != null) folder.OnDeleting();

            var search = view as SearchPanelView;
            if (search != null) search.Detach();
        }

        private bool IsInConfig(string widgetId)
        {
            foreach (var widget in _config.Widgets)
            {
                if (widget != null && string.Equals(widget.Id, widgetId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Keeps a rebuilt host in the slot of the window it replaced.</summary>
        private void MoveHostTo(IWidgetWindow widget, int index)
        {
            if (widget == null) return;

            _widgets.Remove(widget);
            if (index < 0 || index > _widgets.Count) _widgets.Add(widget);
            else _widgets.Insert(index, widget);
        }

        #region Merge / detach (T6)

        /// <summary>Offset a detached member is placed at from its old group.</summary>
        private const double DETACH_CASCADE = 24;

        /// <summary>
        /// Merges a standalone widget into another host: into a group the
        /// source becomes the last tab with the group's active member kept,
        /// into a standalone widget the pair becomes one new group with the
        /// target as the first/active tab. Every reject (locked, group source,
        /// Folder, same widget) leaves both hosts untouched.
        /// </summary>
        public bool MergeWidgets(IWidgetWindow source, IWidgetWindow target)
        {
            if (!GroupDragPolicy.CanMerge(source, target)) return false;
            if (source.Data == null || target.Data == null) return false;
            if (!IsInConfig(source.Data.Id)) return false;

            var targetGroup = target as WidgetGroupWindow;
            if (targetGroup != null) return MergeIntoGroup(source, targetGroup);
            return MergeIntoStandalone(source, target);
        }

        /// <summary>
        /// Adds the source as the last tab of an existing group without
        /// touching that group's window: the live view moves in, the active
        /// member and open state survive, and only the now-empty source host
        /// is closed. A failed adoption restores the source standalone.
        /// </summary>
        private bool MergeIntoGroup(IWidgetWindow source, WidgetGroupWindow targetGroup)
        {
            if (source == null || targetGroup == null) return false;

            WidgetGroup group = targetGroup.Group;
            if (group == null || _config.Groups == null || !_config.Groups.Contains(group)) return false;

            var members = GroupHostPolicy.MembersOf(group, _config.Widgets);
            if (GroupDragPolicy.ShouldDissolve(members.Count)) return false;

            bool wasOpen = targetGroup.IsPanelOpen;
            bool groupWasActive = IsHostActive(targetGroup);
            bool sourceWasActive = IsHostActive(source);
            bool sourceWasOpen = source.IsPanelOpen;
            string activeId = GroupHostPolicy.ActiveMemberId(group, _config.Widgets);
            int sourceIndex = _widgets.IndexOf(source);

            BeginHostTransition();
            try
            {
                var (sourceWidth, sourceHeight) = source.GetPanelSize();
                IWidgetContentView sourceView = TakeStandaloneView(source);

                // The existing group's dimensions win over the incoming widget:
                // its remembered size is recorded only when it has none (P0-B),
                // and the incoming size never feeds the group geometry.
                if (!source.Data.PanelWidth.HasValue) source.Data.PanelWidth = sourceWidth;
                if (!source.Data.PanelHeight.HasValue) source.Data.PanelHeight = sourceHeight;

                string previousGroupId = source.Data.GroupId;
                int? previousGroupOrder = source.Data.GroupOrder;
                source.Data.GroupId = group.Id;
                source.Data.GroupOrder = GroupDragPolicy.InsertIndex(members.Count);
                group.ActiveWidgetId = activeId;

                try
                {
                    // grow:false: an existing group always keeps its size; the
                    // incoming view simply uses the group's content box.
                    targetGroup.AddMember(source.Data, sourceView, grow: false);
                    group.IsPanelPinned = GroupHostPolicy.AnyMemberPinned(targetGroup.Members);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Kobold] merge into group fallback: " + ex.Message);

                    // Undo a partially-added tab, then restore the source's
                    // live view in its own window. Never leave it host-less.
                    if (targetGroup.ContainsWidget(source.Data.Id))
                    {
                        try { targetGroup.RemoveMember(source.Data.Id); }
                        catch (Exception undo) { Debug.WriteLine("[Kobold] merge undo: " + undo.Message); }
                    }
                    source.Data.GroupId = previousGroupId;
                    source.Data.GroupOrder = previousGroupOrder;
                    group.ActiveWidgetId = activeId;

                    CloseHost(source, flush: false);
                    var restored = CreateStandaloneHost(source.Data, sourceView);
                    if (restored != null)
                    {
                        MoveHostTo(restored, sourceIndex);
                        if (sourceWasOpen) OpenPanel(restored, activate: false, animate: false);
                    }

                    SaveConfig();
                    RefreshIsland();
                    FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
                    return false;
                }

                // The group owns the moved view now; close the empty shell
                // without flushing/deactivating that view a second time.
                CloseHost(source, flush: false);

                if (wasOpen && (groupWasActive || sourceWasActive))
                {
                    ActivateHost(targetGroup);
                }

                SaveConfig();
                RefreshIsland();
                FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
                return true;
            }
            finally
            {
                EndHostTransition();
            }
        }

        /// <summary>
        /// Creates one group from two standalone hosts around their live views.
        /// The group inherits the target's placement/open/lock/pin, is sized
        /// for the larger panel (the tab row is the extra 32 DIP on the height
        /// in the source term) and the window clamps that up to its own
        /// minimum. An open target shows the new group before either old
        /// window closes.
        /// </summary>
        private bool MergeIntoStandalone(IWidgetWindow source, IWidgetWindow target)
        {
            WidgetData targetData = target.Data;
            WidgetData sourceData = source.Data;
            if (targetData == null || sourceData == null) return false;

            bool wasOpen = target.IsPanelOpen;
            bool sourceWasOpen = source.IsPanelOpen;
            bool targetWasActive = IsHostActive(target);
            bool sourceWasActive = IsHostActive(source);
            int index = _widgets.IndexOf(target);

            var targetWindow = target as Window;
            double? left = targetData.PanelX ?? Finite(targetWindow == null ? double.NaN : targetWindow.Left);
            double? top = targetData.PanelY ?? Finite(targetWindow == null ? double.NaN : targetWindow.Top);
            double? dpi = targetData.PanelDpiScale;
            if (!dpi.HasValue && targetWindow != null)
            {
                dpi = VisualTreeHelper.GetDpi(targetWindow).DpiScaleX;
            }

            var (targetWidth, targetHeight) = target.GetPanelSize();
            var (sourceWidth, sourceHeight) = source.GetPanelSize();

            BeginHostTransition();
            try
            {
                // Capture both live views before any state mutates; the two old
                // shells are closed only after their replacements are arranged.
                IWidgetContentView targetView = TakeStandaloneView(target);
                IWidgetContentView sourceView = TakeStandaloneView(source);

                var group = new WidgetGroup
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ActiveWidgetId = targetData.Id,
                    PanelX = left,
                    PanelY = top,
                    PanelDpiScale = dpi,
                    PanelWidth = Math.Max(targetWidth, sourceWidth),
                    PanelHeight = Math.Max(targetHeight, sourceHeight + WidgetGroupTabStrip.RowHeight),
                    IsExpanded = wasOpen,
                    IsLocked = targetData.IsLocked,
                    IsPanelPinned = targetData.IsPanelPinned || sourceData.IsPanelPinned
                };

                if (_config.Groups == null) _config.Groups = new List<WidgetGroup>();
                _config.Groups.Add(group);

                targetData.GroupId = group.Id;
                targetData.GroupOrder = 0;
                sourceData.GroupId = group.Id;
                sourceData.GroupOrder = 1;

                WidgetGroupWindow groupWindow = null;
                try
                {
                    groupWindow = CreateGroupInternal(group,
                        new List<WidgetData> { targetData, sourceData },
                        new List<IWidgetContentView> { targetView, sourceView });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Kobold] merge group fallback: " + ex.Message);
                }

                if (groupWindow == null)
                {
                    // Undo the membership and keep both widgets as own panels,
                    // around their exact live views when adoption works.
                    RemoveGroupFromConfig(group);
                    targetData.GroupId = null;
                    targetData.GroupOrder = null;
                    sourceData.GroupId = null;
                    sourceData.GroupOrder = null;

                    CloseHost(source, flush: false);
                    CloseHost(target, flush: false);

                    var restoredTarget = CreateStandaloneHost(targetData, targetView);
                    var restoredSource = CreateStandaloneHost(sourceData, sourceView);
                    if (restoredTarget != null)
                    {
                        MoveHostTo(restoredTarget, index);
                        if (wasOpen) OpenPanel(restoredTarget, activate: false, animate: false);
                    }
                    if (restoredSource != null)
                    {
                        MoveHostTo(restoredSource, index < 0 ? -1 : index + 1);
                        if (sourceWasOpen) OpenPanel(restoredSource, activate: false, animate: false);
                    }

                    SaveConfig();
                    RefreshIsland();
                    FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
                    return false;
                }

                // GetPanelSize clamps the requested size up to the group's kind
                // minimum; that clamped size is what the group remembers.
                var (groupWidth, groupHeight) = groupWindow.GetPanelSize();
                group.PanelWidth = groupWidth;
                group.PanelHeight = groupHeight;

                if (wasOpen)
                {
                    // Show-before-close: the replacement is visible and laid
                    // out at the target's position before the old hosts go.
                    groupWindow.ShowPanel(left ?? 0, top ?? 0, activate: false, animate: false);
                    groupWindow.UpdateLayout();
                }

                CloseHost(source, flush: false);
                CloseHost(target, flush: false);

                MoveHostTo(groupWindow, index);
                if (wasOpen && (targetWasActive || sourceWasActive))
                {
                    ActivateHost(groupWindow);
                }

                SaveConfig();
                RefreshIsland();
                FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
                return true;
            }
            finally
            {
                EndHostTransition();
            }
        }

        /// <summary>
        /// Detaches one tab from its group. Two or more tabs left keep the
        /// same group window and move only the detached live view out; one
        /// left dissolves the group into two standalone hosts, both around the
        /// exact views the group carried. The detached member keeps its own
        /// panel size and its own pin; it inherits the group's open/lock flags
        /// and cascades from the group's corner.
        /// </summary>
        public bool DetachWidget(string groupId, string widgetId)
        {
            if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(widgetId)) return false;

            var groupWindow = FindGroupWindow(groupId);
            if (groupWindow == null) return false;

            WidgetGroup group = groupWindow.Group;
            if (group == null || group.IsLocked) return false;

            var members = GroupHostPolicy.MembersOf(group, _config.Widgets);
            if (GroupDragPolicy.ShouldDissolve(members.Count)) return false;
            if (!groupWindow.ContainsWidget(widgetId)) return false;

            WidgetData detached = null;
            foreach (var member in members)
            {
                if (member != null && string.Equals(member.Id, widgetId, StringComparison.Ordinal))
                {
                    detached = member;
                    break;
                }
            }
            if (detached == null) return false;

            bool wasOpen = groupWindow.IsPanelOpen;
            bool groupWasActive = IsHostActive(groupWindow);
            string activeId = GroupHostPolicy.ActiveMemberId(group, _config.Widgets);
            bool detachedWasActiveTab = string.Equals(activeId, widgetId, StringComparison.Ordinal);
            int index = _widgets.IndexOf(groupWindow);

            // Capture the group's placement before its window may go.
            double? left = group.PanelX ?? Finite(groupWindow.Left);
            double? top = group.PanelY ?? Finite(groupWindow.Top);
            double currentDpi = DpiScaleOf(groupWindow);
            double? dpi = group.PanelX.HasValue || group.PanelY.HasValue
                ? (group.PanelDpiScale ?? currentDpi)
                : currentDpi;

            var survivors = new List<WidgetData>();
            foreach (var member in members)
            {
                if (member == null || string.Equals(member.Id, widgetId, StringComparison.Ordinal)) continue;
                if (IsInConfig(member.Id)) survivors.Add(member);
            }

            BeginHostTransition();
            try
            {
                if (!GroupDragPolicy.ShouldDissolve(survivors.Count))
                {
                    // The group survives in place: same window, size and tabs.
                    IWidgetContentView standaloneView = groupWindow.RemoveMember(widgetId);

                    for (int i = 0; i < survivors.Count; i++) survivors[i].GroupOrder = i;
                    if (GroupHostPolicy.IndexOfMember(group, _config.Widgets, group.ActiveWidgetId) < 0)
                        group.ActiveWidgetId = survivors[0].Id;
                    group.IsPanelPinned = GroupHostPolicy.AnyMemberPinned(survivors);

                    ApplyDetachedStandaloneState(detached, group, left, top, dpi, wasOpen);

                    var standaloneHost = CreateStandaloneHost(detached, standaloneView);
                    if (standaloneHost != null)
                    {
                        MoveHostTo(standaloneHost, index + 1);
                        // Never activate the detached host here: deactivating
                        // an unpinned group would collapse it. Focus stays on
                        // the surviving group.
                        if (wasOpen) OpenPanel(standaloneHost, activate: false, animate: false);
                    }

                    SaveConfig();
                    RefreshIsland();
                    FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
                    return true;
                }

                // The last pair: take both live views out, stand both hosts up
                // (shown first when the group was open), then close the group.
                IWidgetContentView detachedView = groupWindow.RemoveMember(widgetId);
                WidgetData survivor = survivors[0];
                IWidgetContentView survivorView = groupWindow.RemoveMember(survivor.Id);

                ApplyDissolvedStandaloneState(detached, group, left, top, dpi, wasOpen, 2);
                ApplyDissolvedStandaloneState(survivor, group, left, top, dpi, wasOpen, 1);
                RemoveGroupFromConfig(group);

                var detachedHost = CreateStandaloneHost(detached, detachedView);
                var survivorHost = CreateStandaloneHost(survivor, survivorView);
                if (detachedHost != null)
                {
                    MoveHostTo(detachedHost, index);
                    if (wasOpen) OpenPanel(detachedHost, activate: false, animate: false);
                }
                if (survivorHost != null)
                {
                    MoveHostTo(survivorHost, index < 0 ? -1 : index + 1);
                    if (wasOpen) OpenPanel(survivorHost, activate: false, animate: false);
                }

                CloseHost(groupWindow, flush: false);

                if (groupWasActive)
                {
                    IWidgetWindow focusTarget = detachedWasActiveTab ? detachedHost : survivorHost;
                    ActivateHost(focusTarget);
                }

                SaveConfig();
                RefreshIsland();
                FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
                return true;
            }
            finally
            {
                EndHostTransition();
            }
        }

        /// <summary>
        /// Reorders one member inside its group and persists the new
        /// <see cref="WidgetData.GroupOrder"/>. The group window is mutated in
        /// place (member/view/slot/tab move together, the dragged tab stays
        /// active) and is never closed, rebuilt or resized. A locked group, a
        /// missing group, an invalid index and a no-op are refused.
        /// </summary>
        public bool ReorderGroupMember(string groupId, int from, int to)
        {
            return ReorderGroupMember(groupId, from, to, null);
        }

        /// <summary>
        /// The snapshot-guarded reorder: when
        /// <paramref name="expectedMemberIds"/> is non-null it must match the
        /// group window's current member ids exactly (count and order,
        /// ordinal) before anything moves. A stale snapshot - a merge/detach
        /// after the drag press - returns false and changes nothing. A null
        /// snapshot keeps the unguarded three-argument behaviour.
        /// </summary>
        public bool ReorderGroupMember(string groupId, int from, int to,
            IReadOnlyList<string> expectedMemberIds)
        {
            if (string.IsNullOrEmpty(groupId)) return false;

            var groupWindow = FindGroupWindow(groupId);
            if (groupWindow == null) return false;

            if (expectedMemberIds != null &&
                !MatchesMemberSnapshot(groupWindow.Members, expectedMemberIds))
            {
                return false;
            }

            WidgetGroup group = groupWindow.Group;
            if (group == null || group.IsLocked) return false;

            int count = groupWindow.Members.Count;
            if (count < 2 || from < 0 || from >= count || to < 0 || to >= count) return false;
            if (from == to) return false;

            groupWindow.ReorderMember(from, to);
            group.ActiveWidgetId = groupWindow.ActiveMemberId;

            SaveConfig();
            RefreshIsland();
            return true;
        }

        /// <summary>Exact count and ordinal id match against a drag snapshot.</summary>
        private static bool MatchesMemberSnapshot(IReadOnlyList<WidgetData> members,
            IReadOnlyList<string> expectedMemberIds)
        {
            if (members == null || expectedMemberIds == null) return false;
            if (members.Count != expectedMemberIds.Count) return false;

            for (int i = 0; i < members.Count; i++)
            {
                string currentId = members[i] == null ? null : members[i].Id;
                if (!string.Equals(currentId, expectedMemberIds[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Unregisters and closes a host. Closing is the flush point: a group
        /// window settles every member view, a note/search window releases its
        /// view. The standalone todo editor is flushed explicitly first.
        /// </summary>
        private void RemoveHost(IWidgetWindow host)
        {
            CloseHost(host, flush: true);
        }

        /// <summary>
        /// Unregisters and closes one host. A host whose live view was already
        /// released is closed without a second flush: that view belongs to its
        /// adopting window now, and settling it here would touch the new host.
        /// </summary>
        private void CloseHost(IWidgetWindow host, bool flush = true)
        {
            if (host == null) return;

            if (flush) FlushHost(host);
            _widgets.Remove(host);
            var window = host as Window;
            if (window != null) window.Close();
        }

        /// <summary>
        /// Hands a standalone host's live view out (deactivate + flush, clear
        /// the chrome slot). A group host owns its views through its tab list
        /// and is never released this way.
        /// </summary>
        private static IWidgetContentView TakeStandaloneView(IWidgetWindow host)
        {
            var window = host as WidgetWindowBase;
            if (window == null || window is WidgetGroupWindow) return null;
            if (window.HasReleasedContentView) return null;
            return window.ReleaseContentView();
        }

        /// <summary>
        /// Settles an inline editor that a standalone host owns. Group windows
        /// flush all member views in their Closing handler; note/search flush
        /// on close too, while the todo editor needs this explicit call. A
        /// released view is skipped - it already belongs to another host.
        /// </summary>
        private static void FlushHost(IWidgetWindow host)
        {
            var todo = host as TodoWidget;
            if (todo != null)
            {
                if (todo.HasReleasedContentView) return;
                var view = todo.CalendarView as IWidgetContentView;
                if (view != null) view.FlushPendingEdit();
                return;
            }

            var note = host as NoteWidget;
            if (note != null)
            {
                if (note.HasReleasedContentView) return;
                var view = note.PanelView as IWidgetContentView;
                if (view != null) view.FlushPendingEdit();
            }
        }

        /// <summary>The detached member's own panel size and pin stay; only the group's placement/flags follow it out.</summary>
        private static void ApplyDetachedStandaloneState(
            WidgetData data, WidgetGroup group, double? groupLeft, double? groupTop, double? dpi, bool groupOpen)
        {
            if (data == null) return;

            data.GroupId = null;
            data.GroupOrder = null;
            data.IsExpanded = groupOpen;
            data.IsLocked = group.IsLocked;
            if (groupLeft.HasValue) data.PanelX = groupLeft.Value + DETACH_CASCADE;
            if (groupTop.HasValue) data.PanelY = groupTop.Value + DETACH_CASCADE;
            data.PanelDpiScale = dpi;
        }

        /// <summary>
        /// A dissolved member keeps its own pin and cascades from the group's
        /// corner; its own PanelWidth/PanelHeight are deliberately not touched.
        /// </summary>
        private static void ApplyDissolvedStandaloneState(
            WidgetData data, WidgetGroup group, double? groupLeft, double? groupTop,
            double? dpi, bool groupOpen, int cascadeSteps)
        {
            if (data == null) return;

            data.GroupId = null;
            data.GroupOrder = null;
            data.IsExpanded = groupOpen;
            data.IsLocked = group.IsLocked;
            if (groupLeft.HasValue) data.PanelX = groupLeft.Value + DETACH_CASCADE * Math.Max(1, cascadeSteps);
            if (groupTop.HasValue) data.PanelY = groupTop.Value + DETACH_CASCADE * Math.Max(1, cascadeSteps);
            data.PanelDpiScale = dpi;
        }

        /// <summary>True while a host's native window owns focus; false for a non-window host.</summary>
        private static bool IsHostActive(IWidgetWindow host)
        {
            var window = host as Window;
            return window != null && window.IsActive;
        }

        /// <summary>Brings a host's native window forward; a non-window host is a no-op.</summary>
        private static void ActivateHost(IWidgetWindow host)
        {
            var window = host as Window;
            if (window != null) window.Activate();
        }

        /// <summary>A window coordinate, or null when it is not a real number.</summary>
        private static double? Finite(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            return value;
        }

        private static double DpiScaleOf(Window window)
        {
            if (window == null) return 1.0;
            double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
            return dpi > 0 ? dpi : 1.0;
        }

        #endregion

        /// <summary>
        /// Opens every widget's panel, one at a time. Rendering them all in the
        /// same frame flickers, so each is opened a short interval after the last.
        /// </summary>
        public void ShowAll()
        {
            var pending = _widgets.Where(w => !w.IsPanelOpen).ToList();
            if (pending.Count == 0) return;

            int index = 0;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            timer.Tick += (s, e) =>
            {
                if (index >= pending.Count)
                {
                    timer.Stop();
                    return;
                }
                OpenPanel(pending[index], activate: false);
                index++;
            };
            timer.Start();
        }

        /// <summary>
        /// Hides every widget's panel
        /// </summary>
        public void HideAll()
        {
            foreach (var widget in _widgets)
            {
                widget.HidePanel();
            }
        }

        /// <summary>
        /// Saves configuration
        /// </summary>
        public void SaveConfig()
        {
            _config.SaveDebounced();
        }

        /// <summary>
        /// Lands a save synchronously, for a path that cannot wait for the
        /// debounce (a window close during tray exit or a Windows session end).
        /// <see cref="AppConfig.Save"/> is atomic and keeps the backup; it also
        /// writes while the config is in its closing state, where
        /// <see cref="AppConfig.SaveDebounced"/> deliberately drops the write.
        /// </summary>
        public void SaveConfigNow()
        {
            _config.Save();
        }

        /// <summary>
        /// The same synchronous save, but only when the singleton already
        /// exists: exit paths must never construct a manager (and load the
        /// user's real config) just to save it. Exit paths only - per-open
        /// bookkeeping schedules a debounced write through
        /// <see cref="SaveConfig"/> instead.
        /// </summary>
        public static void SaveConfigIfLoaded()
        {
            var instance = _instance;
            if (instance != null) instance.SaveConfigNow();
        }

        /// <summary>
        /// Sets language and updates UI
        /// </summary>
        public void SetLanguage(string lang)
        {
            _config.Language = lang;
            Localization.CurrentLanguage = lang;
            _config.Save();
            
            // Update all widgets
            foreach (var widget in _widgets)
            {
                widget.UpdateUI();
            }
        }

        /// <summary>
        /// Refreshes all widgets UI with current theme
        /// </summary>
        public void RefreshAllWidgets()
        {
            foreach (var widget in _widgets)
            {
                widget.RefreshTheme();
            }
        }

        /// <summary>
        /// Final save for a Windows logoff/shutdown: the session may not live
        /// long enough for the debounced timer, and no further writes should
        /// start afterwards. The tray-exit path keeps its own Shutdown().
        /// </summary>
        public void BeginExitSave()
        {
            _config.BeginClosing();
        }

        /// <summary>
        /// Shuts down all widgets
        /// </summary>
        public void Shutdown()
        {
            // Stop the index before the widget windows close: a scan/watcher must
            // not keep writing into the database while the UI is tearing down.
            FileSearchIndexService.Instance.Shutdown(TimeSpan.FromSeconds(5));

            _fullscreenWatcher?.Dispose();
            _fullscreenWatcher = null;
            _trimTimer.Stop();

            // Ensure config is saved before exit. This first write covers the
            // state as of the exit request; the writes must not be the last
            // word, because the close handlers below flush pending note edits.
            _config.Save();
            
            foreach (var widget in _widgets.ToList())
            {
                if (widget is Window window) window.Close();
            }
            _widgets.Clear();

            // The close handlers just spliced their pending edits into the
            // data and asked for a debounced save, which will never fire once
            // this process is gone (and is dropped outright during a Windows
            // session end). Land those edits synchronously - the second write
            // is what makes the final keystrokes survive a tray exit.
            _config.Save();
        }
    }
}


