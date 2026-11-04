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

            if (MemoryTrimmer.Trim()) _trimPolicy.NotifyTrimmed();
        }

        /// <summary>
        /// Refreshes the widget tiles shown by the island. A grouped member
        /// carries its group's badge/tooltip, and only the open group's active
        /// member carries the open ring.
        /// </summary>
        public void RefreshIsland()
        {
            var groupInfo = BuildGroupTileInfo();
            _island?.SetWidgets(_config.Widgets, groupInfo);
            _island?.SetOpenStates(GroupHostPolicy.OpenTileIds(
                _config.Groups, _config.Widgets, IsHostOpen));
        }

        /// <summary>The island projection of every group with at least two members.</summary>
        private Dictionary<string, WidgetGroupTileInfo> BuildGroupTileInfo()
        {
            var info = new Dictionary<string, WidgetGroupTileInfo>(StringComparer.Ordinal);
            if (_config.Groups == null) return info;

            foreach (var group in _config.Groups)
            {
                var members = GroupHostPolicy.MembersOf(group, _config.Widgets);
                if (members.Count < 2) continue;

                var names = new List<string>();
                foreach (var member in members)
                {
                    string label = string.IsNullOrWhiteSpace(member.Name)
                        ? WidgetGroupTabStrip.KindLabel(member)
                        : member.Name.Trim();
                    names.Add(label);
                }

                var tileInfo = new WidgetGroupTileInfo
                {
                    MemberCount = members.Count,
                    MemberNames = string.Join(" / ", names)
                };

                foreach (var member in members)
                {
                    if (!string.IsNullOrEmpty(member.Id)) info[member.Id] = tileInfo;
                }
            }
            return info;
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
        /// member opens/switches/collapses its group instead of a window of
        /// its own.
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

            if (isMember && string.Equals(group.ActiveMemberId, widgetId, StringComparison.Ordinal))
            {
                // Clicking the already-active tab collapses the whole group.
                group.HidePanel();
                RefreshIsland();
                return;
            }

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

        /// <summary>Opens a widget's panel; defaults to centered below the island when unplaced.</summary>
        private void OpenPanel(IWidgetWindow widget, bool activate = true)
        {
            if (widget == null || _island == null) return;
            var (panelWidth, _) = widget.GetPanelSize();
            double left = _island.CenterX - panelWidth / 2; // clamped per-monitor inside ShowPanel
            widget.ShowPanel(left, _island.PanelTop, activate);
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
        /// the tray new-widget submenu.
        /// </summary>
        public IWidgetWindow CreateWidget(string kindId)
        {
            var kind = WidgetKindRegistry.Find(kindId);
            if (kind == null) return null;

            var data = kind.CreateDefault();
            int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            int screenHeight = (int)SystemParameters.PrimaryScreenHeight;
            int widgetCount = _widgets.Count;

            data.PosX = screenWidth / 2 - 50 + (widgetCount % 5) * 30;
            data.PosY = screenHeight / 2 - 50 + (widgetCount % 5) * 30;
            data.Color = CreationColors[widgetCount % CreationColors.Length];
            data.Name = Localization.Get(kind.NameKey);
            data.IsPanelPinned = true; // opens pinned so it survives while being filled

            if (data is FolderData folder) folder.GridColumns = _config.DefaultGridColumns;

            // The one-time default size lands before the first save, so a new
            // widget opens at its migrated size and keeps it afterwards.
            PanelSizeMigration.FillMissingSizes(data, _config.ItemScale, _config.Language, DateTime.Today);

            _config.AddWidget(data);
            var widget = CreateWidgetInternal(data);
            RefreshIsland();
            OpenPanel(widget);
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
        /// Creates the window for an existing config entry and wires it to the
        /// manager. Unknown kinds have no window: they stay config-only.
        /// </summary>
        private IWidgetWindow CreateWidgetInternal(WidgetData data)
        {
            var widget = CreateWindow(data);
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
        /// Creates the shared window for a config group and wires it to the
        /// manager. Group state is live in <see cref="AppConfig.Groups"/>, so the
        /// existing save path persists geometry, flags and the active member.
        /// </summary>
        private WidgetGroupWindow CreateGroupInternal(WidgetGroup group, List<WidgetData> members)
        {
            if (group == null || members == null || members.Count < 2) return null;

            var window = WidgetWindowFactory.CreateGroup(group, members) as WidgetGroupWindow;
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
            _widgets.Remove(window);
            window.Close();

            foreach (var member in members) _config.RemoveWidget(member.Id);

            RefreshIsland();
            FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
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
        /// Removes one member from a group host and rebuilds its window:
        /// the group survives with its remaining members, or the last survivor
        /// becomes a standalone widget. Open state and the active member are
        /// preserved (the active falls back to the first surviving tab).
        /// </summary>
        private void RemoveGroupMember(WidgetGroupWindow oldWindow, string memberId)
        {
            WidgetGroup group = oldWindow.Group;
            var membersBefore = GroupHostPolicy.MembersOf(group, _config.Widgets);
            bool wasOpen = oldWindow.IsPanelOpen;

            int index = _widgets.IndexOf(oldWindow);
            _widgets.Remove(oldWindow);
            oldWindow.Close();

            _config.RemoveWidget(memberId);

            var survivors = new List<WidgetData>();
            foreach (var member in membersBefore)
            {
                if (string.Equals(member.Id, memberId, StringComparison.Ordinal)) continue;
                if (IsInConfig(member.Id)) survivors.Add(member);
            }

            bool groupSurvives = group != null
                && survivors.Count >= 2
                && _config.Groups != null
                && _config.Groups.Contains(group);

            if (groupSurvives)
            {
                if (GroupHostPolicy.IndexOfMember(group, _config.Widgets, group.ActiveWidgetId) < 0)
                    group.ActiveWidgetId = survivors[0].Id;

                try
                {
                    var rebuilt = CreateGroupInternal(group, survivors);
                    if (rebuilt != null)
                    {
                        MoveHostTo(rebuilt, index);
                        if (wasOpen) OpenPanel(rebuilt, activate: false);
                    }
                }
                catch (Exception ex)
                {
                    // A member kind without a group view cannot be hosted: keep
                    // every survivor reachable through its own window instead.
                    Debug.WriteLine("[Kobold] group rebuild fallback: " + ex.Message);
                    CreateStandaloneHosts(survivors, index, wasOpen);
                }
            }
            else if (survivors.Count == 1)
            {
                CreateStandaloneHosts(survivors, index, wasOpen);
            }

            RefreshIsland();
            FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
        }

        /// <summary>Last-resort hosts: one standalone window per surviving member.</summary>
        private void CreateStandaloneHosts(List<WidgetData> members, int index, bool open)
        {
            if (members == null) return;

            foreach (var member in members)
            {
                var standalone = CreateWidgetInternal(member);
                if (standalone == null) continue;

                MoveHostTo(standalone, index);
                if (index >= 0) index++;
                if (open) OpenPanel(standalone, activate: false);
            }
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
        /// Adds the source as the last tab of an existing group. The group's
        /// active member and open state survive; the rebuilt window replaces
        /// the old one and the source never takes focus.
        /// </summary>
        private bool MergeIntoGroup(IWidgetWindow source, WidgetGroupWindow targetGroup)
        {
            if (source == null || targetGroup == null) return false;

            WidgetGroup group = targetGroup.Group;
            if (group == null || _config.Groups == null || !_config.Groups.Contains(group)) return false;

            var members = GroupHostPolicy.MembersOf(group, _config.Widgets);
            if (GroupDragPolicy.ShouldDissolve(members.Count)) return false;

            bool wasOpen = targetGroup.IsPanelOpen;
            string activeId = GroupHostPolicy.ActiveMemberId(group, _config.Widgets);
            int index = _widgets.IndexOf(targetGroup);

            // The source takes the last tab; its own panel geometry is untouched.
            source.Data.GroupId = group.Id;
            source.Data.GroupOrder = GroupDragPolicy.InsertIndex(members.Count);
            group.ActiveWidgetId = activeId;

            RemoveHost(source);
            RemoveHost(targetGroup); // flushes every member view before the rebuild

            var rebuiltMembers = new List<WidgetData>(members);
            rebuiltMembers.Add(source.Data);

            WidgetGroupWindow rebuilt = null;
            try
            {
                rebuilt = CreateGroupInternal(group, rebuiltMembers);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] merge rebuild fallback: " + ex.Message);
            }

            if (rebuilt == null)
            {
                // No group view for the pair: keep every widget reachable as
                // its own panel (the same fallback startup uses).
                CreateStandaloneHosts(rebuiltMembers, index, wasOpen);
                SaveConfig();
                RefreshIsland();
                FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
                return false;
            }

            MoveHostTo(rebuilt, index);
            if (wasOpen)
            {
                OpenPanel(rebuilt, activate: false);
                if (activeId != null) rebuilt.SetActiveByWidgetId(activeId);
            }

            SaveConfig();
            RefreshIsland();
            FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
            return true;
        }

        /// <summary>
        /// Creates one group from two standalone hosts. The group inherits the
        /// target's placement/open/lock/pin, is sized for the larger panel (the
        /// tab row is the extra 32 DIP on the height in the source term) and
        /// the window clamps that up to its own minimum.
        /// </summary>
        private bool MergeIntoStandalone(IWidgetWindow source, IWidgetWindow target)
        {
            WidgetData targetData = target.Data;
            WidgetData sourceData = source.Data;
            if (targetData == null || sourceData == null) return false;

            bool wasOpen = target.IsPanelOpen;
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
                IsPanelPinned = targetData.IsPanelPinned
            };

            if (_config.Groups == null) _config.Groups = new List<WidgetGroup>();
            _config.Groups.Add(group);

            targetData.GroupId = group.Id;
            targetData.GroupOrder = 0;
            sourceData.GroupId = group.Id;
            sourceData.GroupOrder = 1;

            RemoveHost(source);
            RemoveHost(target);

            WidgetGroupWindow groupWindow = null;
            try
            {
                groupWindow = CreateGroupInternal(group, new List<WidgetData> { targetData, sourceData });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] merge group fallback: " + ex.Message);
            }

            if (groupWindow == null)
            {
                // Undo the membership and keep both widgets as own panels.
                _config.Groups.Remove(group);
                if (_config.Groups.Count == 0) _config.Groups = null;
                targetData.GroupId = null;
                targetData.GroupOrder = null;
                sourceData.GroupId = null;
                sourceData.GroupOrder = null;
                CreateStandaloneHosts(new List<WidgetData> { targetData, sourceData }, index, wasOpen);
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

            MoveHostTo(groupWindow, index);
            if (wasOpen) OpenPanel(groupWindow, activate: false);

            SaveConfig();
            RefreshIsland();
            FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
            return true;
        }

        /// <summary>
        /// Detaches one tab from its group. Two or more tabs left rebuild the
        /// group without the member; one left dissolves it, and both the
        /// survivor and the detached member become standalone hosts. The
        /// detached member keeps its own panel size, inherits the group's
        /// open/pin/lock flags and cascades from the group's corner.
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
            string activeId = GroupHostPolicy.ActiveMemberId(group, _config.Widgets);
            int index = _widgets.IndexOf(groupWindow);

            // Capture the group's placement before its window goes.
            double? left = group.PanelX ?? Finite(groupWindow.Left);
            double? top = group.PanelY ?? Finite(groupWindow.Top);
            double currentDpi = DpiScaleOf(groupWindow);
            double? dpi = group.PanelX.HasValue || group.PanelY.HasValue
                ? (group.PanelDpiScale ?? currentDpi)
                : currentDpi;

            // The member stands on its own: its panel size stays its own, the
            // group-level flags follow it out.
            detached.GroupId = null;
            detached.GroupOrder = null;
            detached.IsExpanded = wasOpen;
            detached.IsPanelPinned = group.IsPanelPinned;
            detached.IsLocked = group.IsLocked;
            if (left.HasValue) detached.PanelX = left.Value + DETACH_CASCADE;
            if (top.HasValue) detached.PanelY = top.Value + DETACH_CASCADE;
            detached.PanelDpiScale = dpi;

            RemoveHost(groupWindow); // flushes every member view before the rebuild

            var survivors = new List<WidgetData>();
            foreach (var member in members)
            {
                if (member == null || string.Equals(member.Id, widgetId, StringComparison.Ordinal)) continue;
                if (IsInConfig(member.Id)) survivors.Add(member);
            }

            if (!GroupDragPolicy.ShouldDissolve(survivors.Count))
            {
                for (int i = 0; i < survivors.Count; i++) survivors[i].GroupOrder = i;
                if (string.IsNullOrEmpty(activeId) || string.Equals(activeId, widgetId, StringComparison.Ordinal))
                {
                    group.ActiveWidgetId = survivors[0].Id;
                }

                WidgetGroupWindow rebuilt = null;
                try
                {
                    rebuilt = CreateGroupInternal(group, survivors);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Kobold] detach rebuild fallback: " + ex.Message);
                }

                if (rebuilt != null)
                {
                    MoveHostTo(rebuilt, index);
                    if (wasOpen)
                    {
                        OpenPanel(rebuilt, activate: false);
                        if (group.ActiveWidgetId != null) rebuilt.SetActiveByWidgetId(group.ActiveWidgetId);
                    }
                }
                else
                {
                    CreateStandaloneHosts(survivors, index, wasOpen);
                }

                var detachedHost = CreateWidgetInternal(detached);
                if (detachedHost != null)
                {
                    MoveHostTo(detachedHost, index + 1);
                    if (wasOpen) OpenPanel(detachedHost, activate: false);
                }
            }
            else
            {
                // The last pair: the group is gone and both widgets stand alone.
                if (_config.Groups != null)
                {
                    _config.Groups.Remove(group);
                    if (_config.Groups.Count == 0) _config.Groups = null;
                }

                foreach (var survivor in survivors)
                {
                    survivor.GroupId = null;
                    survivor.GroupOrder = null;
                }

                var standalone = new List<WidgetData>(survivors);
                standalone.Add(detached);
                CreateStandaloneHosts(standalone, index, wasOpen);
            }

            SaveConfig();
            RefreshIsland();
            FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);
            return true;
        }

        /// <summary>
        /// Unregisters and closes a host. Closing is the flush point: a group
        /// window settles every member view, a note/search window releases its
        /// view. The standalone todo editor is flushed explicitly first.
        /// </summary>
        private void RemoveHost(IWidgetWindow host)
        {
            if (host == null) return;

            FlushHost(host);
            _widgets.Remove(host);
            var window = host as Window;
            if (window != null) window.Close();
        }

        /// <summary>
        /// Settles an inline editor that a standalone host owns. Group windows
        /// flush all member views in their Closing handler; note/search flush
        /// on close too, while the todo editor needs this explicit call.
        /// </summary>
        private static void FlushHost(IWidgetWindow host)
        {
            var todo = host as TodoWidget;
            if (todo != null)
            {
                var view = todo.CalendarView as IWidgetContentView;
                if (view != null) view.FlushPendingEdit();
                return;
            }

            var note = host as NoteWidget;
            if (note != null)
            {
                var view = note.PanelView as IWidgetContentView;
                if (view != null) view.FlushPendingEdit();
            }
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


