using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kobold.Core;
using Localization = Kobold.Core.Localization;

namespace Kobold.Services.Update
{
    /// <summary>Lifecycle of the in-app updater.</summary>
    public enum UpdateState
    {
        /// <summary>No check has produced an offer since the service started (or after a reset).</summary>
        Idle,
        Checking,
        Available,
        Downloading,
        Ready,
        Failed
    }

    /// <summary>
    /// Why a check/download/apply ended in <see cref="UpdateState.Failed"/>.
    /// The status snapshot carries this stable value, never an exception string,
    /// so the settings line can translate the reason.
    /// </summary>
    public enum UpdateFailureReason
    {
        None,
        Network,
        Timeout,
        Disk,
        Manifest,
        Integrity,
        Helper,
        InstallLocation,
        StagedFiles,
        Unexpected
    }

    /// <summary>The action a tray entry for <see cref="UpdateState"/> performs.</summary>
    public enum UpdateTrayAction
    {
        None,
        Download,
        Apply,
        OpenDownloadPage
    }

    /// <summary>
    /// Immutable-ish snapshot of the service, safe to hand to a UI event handler.
    /// <see cref="UpdateService.Status"/> returns copies so a reader can never
    /// race an in-flight update.
    /// </summary>
    public sealed class UpdateStatus
    {
        public UpdateState State { get; set; }

        /// <summary>Version of the release that is offered, downloading or staged.</summary>
        public string Version { get; set; } = "";

        /// <summary>Download progress in whole percent (0-100).</summary>
        public int ProgressPercent { get; set; }

        /// <summary>Absolute staging directory once <see cref="State"/> is Ready.</summary>
        public string StageDirectory { get; set; } = "";

        /// <summary>Stable failure category; the settings window owns the localized sentence.</summary>
        public UpdateFailureReason FailureReason { get; set; }

        /// <summary>True once a check has reached a decision in this process, so Idle can read as "up to date".</summary>
        public bool HasChecked { get; set; }

        internal UpdateStatus Clone()
        {
            return new UpdateStatus
            {
                State = State,
                Version = Version,
                ProgressPercent = ProgressPercent,
                StageDirectory = StageDirectory,
                FailureReason = FailureReason,
                HasChecked = HasChecked
            };
        }
    }

    /// <summary>
    /// Pure rules shared by the service, the settings window and the tray. They
    /// answer "may this action run now?" without touching the network, the file
    /// system or the dispatcher, so <c>tests/UpdateCheck</c> can pin them. The
    /// localized status/tray wording lives here too, so the checks can prove
    /// which sentence (and which placeholder argument) each state produces.
    /// </summary>
    public static class UpdateStateRules
    {
        /// <summary>
        /// A check is useful from a fresh/offered/failed state. A staged update
        /// (Ready) must not be replaced by another check, and the two busy
        /// states must never start a second operation. Checking is independent
        /// of the self-update capability: a portable/read-only copy must still
        /// learn that a release exists so it can point at the download page.
        /// </summary>
        public static bool CanCheck(UpdateState state)
        {
            return state == UpdateState.Idle
                || state == UpdateState.Available
                || state == UpdateState.Failed;
        }

        /// <summary>Only a self-updatable install may download, and only while an offer waits.</summary>
        public static bool CanDownload(UpdateState state, bool selfUpdatable)
        {
            return selfUpdatable && state == UpdateState.Available;
        }

        /// <summary>Only a self-updatable install with a staged release may apply.</summary>
        public static bool CanApply(UpdateState state, bool selfUpdatable)
        {
            return selfUpdatable && state == UpdateState.Ready;
        }

        /// <summary>Automatic download needs the user's auto-install choice plus the capability and an offer.</summary>
        public static bool ShouldAutoDownload(UpdateState state, bool autoInstall, bool selfUpdatable)
        {
            return autoInstall && CanDownload(state, selfUpdatable);
        }

        /// <summary>The OnExit auto-apply promise: staged, requested, and possible for this install.</summary>
        public static bool ShouldInstallOnExit(UpdateState state, bool autoInstall, bool selfUpdatable)
        {
            return autoInstall && CanApply(state, selfUpdatable);
        }

        /// <summary>
        /// The tray shows an update entry in the three actionable states. It is
        /// shown for a non-updatable copy too: the click opens the download page
        /// instead of downloading.
        /// </summary>
        public static bool ShowsTrayItem(UpdateState state)
        {
            return state == UpdateState.Available
                || state == UpdateState.Downloading
                || state == UpdateState.Ready;
        }

        /// <summary>
        /// The offer gate: the manifest must be a newer release and not the one
        /// the user skipped. The capability gate belongs to the download/apply
        /// action, not to discovering that a release exists.
        /// </summary>
        public static bool ShouldOfferUpdate(UpdateManifest manifest, string localVersion,
            string skippedVersion)
        {
            return UpdatePolicy.ShouldOffer(manifest, localVersion, skippedVersion);
        }

        /// <summary>
        /// The settings-window status sentence for one snapshot. Every key that
        /// carries a placeholder is rendered through <see cref="Localization.Format"/>.
        /// A non-updatable copy still reports an offer, with the manual-download
        /// wording; its other states read as unsupported and the download page
        /// button stays available next to it.
        /// </summary>
        public static string DescribeStatus(UpdateStatus status, bool selfUpdatable, bool autoInstall)
        {
            if (status == null) return Localization.Get("Settings_UpdateIdle");

            if (!selfUpdatable)
            {
                switch (status.State)
                {
                    case UpdateState.Checking:
                        return Localization.Get("Settings_UpdateChecking");
                    case UpdateState.Available:
                        return Localization.Format("Settings_UpdateAvailableManualFormat", status.Version);
                    case UpdateState.Failed:
                        return Localization.Format("Settings_UpdateFailedFormat",
                            DescribeFailureReason(status.FailureReason));
                    default:
                        return Localization.Get("Settings_UpdateUnsupported");
                }
            }

            switch (status.State)
            {
                case UpdateState.Checking:
                    return Localization.Get("Settings_UpdateChecking");
                case UpdateState.Available:
                    return Localization.Format("Settings_UpdateAvailableFormat", status.Version);
                case UpdateState.Downloading:
                    return Localization.Format("Settings_UpdateDownloadingFormat", status.ProgressPercent);
                case UpdateState.Ready:
                    // With auto-install off nothing installs on exit, so the
                    // normal promise would be a lie; the manual wording points
                    // at the tray restart action instead.
                    return Localization.Format(
                        autoInstall ? "Settings_UpdateReadyFormat" : "Settings_UpdateReadyManualFormat",
                        status.Version);
                case UpdateState.Failed:
                    return Localization.Format("Settings_UpdateFailedFormat",
                        DescribeFailureReason(status.FailureReason));
                default:
                    return Localization.Get(status.HasChecked
                        ? "Settings_UpdateUpToDate"
                        : "Settings_UpdateIdle");
            }
        }

        /// <summary>The localized sentence for a stable failure category.</summary>
        public static string DescribeFailureReason(UpdateFailureReason reason)
        {
            switch (reason)
            {
                case UpdateFailureReason.Network: return Localization.Get("Settings_UpdateFailureNetwork");
                case UpdateFailureReason.Timeout: return Localization.Get("Settings_UpdateFailureTimeout");
                case UpdateFailureReason.Disk: return Localization.Get("Settings_UpdateFailureDisk");
                case UpdateFailureReason.Manifest: return Localization.Get("Settings_UpdateFailureManifest");
                case UpdateFailureReason.Integrity: return Localization.Get("Settings_UpdateFailureIntegrity");
                case UpdateFailureReason.Helper: return Localization.Get("Settings_UpdateFailureHelper");
                case UpdateFailureReason.InstallLocation:
                    return Localization.Get("Settings_UpdateFailureInstallLocation");
                case UpdateFailureReason.StagedFiles:
                    return Localization.Get("Settings_UpdateFailureStagedFiles");
                default: return Localization.Get("Settings_UpdateFailureUnexpected");
            }
        }

        /// <summary>
        /// The tray entry's label for one snapshot, or null when the tray must not
        /// show an update entry at all.
        /// </summary>
        public static string TrayText(UpdateStatus status, bool selfUpdatable)
        {
            if (status == null || !ShowsTrayItem(status.State)) return null;

            string version = status.Version ?? "";
            switch (status.State)
            {
                case UpdateState.Available:
                    return Localization.Format(selfUpdatable
                        ? "Tray_UpdateAvailableFormat"
                        : "Tray_UpdateAvailableManualFormat", version);
                case UpdateState.Downloading:
                    return Localization.Format("Tray_UpdateDownloadingFormat", version, status.ProgressPercent);
                case UpdateState.Ready:
                    return Localization.Format("Tray_UpdateReadyFormat", version);
                default:
                    return null;
            }
        }

        /// <summary>
        /// What clicking the tray entry does for one snapshot. A non-updatable
        /// copy is sent to the download page instead of starting a download it
        /// could never apply.
        /// </summary>
        public static UpdateTrayAction TrayAction(UpdateStatus status, bool selfUpdatable)
        {
            if (status == null) return UpdateTrayAction.None;

            switch (status.State)
            {
                case UpdateState.Available:
                    return selfUpdatable ? UpdateTrayAction.Download : UpdateTrayAction.OpenDownloadPage;
                case UpdateState.Downloading:
                    return selfUpdatable ? UpdateTrayAction.Download : UpdateTrayAction.None;
                case UpdateState.Ready:
                    return selfUpdatable ? UpdateTrayAction.Apply : UpdateTrayAction.None;
                default:
                    return UpdateTrayAction.None;
            }
        }

        /// <summary>
        /// Whether an automatic check should run now. The launch check respects
        /// both the enable switch and the stored 24-hour stamp; a timer tick
        /// after that is due by construction, because the stamp is written a
        /// moment after the tick, and re-applying the interval there would slip
        /// a 24-hour timer to 48 hours.
        /// </summary>
        public static bool ShouldRunAutomaticCheck(bool enabled, bool enforceInterval,
            string lastCheckUtcIso, DateTime nowUtc)
        {
            if (!enabled) return false;
            if (!enforceInterval) return true;
            return UpdatePolicy.ShouldCheck(true, lastCheckUtcIso, nowUtc,
                UpdatePolicy.DefaultCheckInterval);
        }

        /// <summary>
        /// The manifest address for this run. The fixed HTTPS address is the
        /// production value; <c>KOBOLD_UPDATE_MANIFEST_URL</c> exists only so an
        /// end-to-end test can point the app at a loopback server, and is used
        /// only when it holds an absolute http(s) URL.
        /// </summary>
        public static string ResolveManifestUrl(string environmentUrl)
        {
            if (string.IsNullOrWhiteSpace(environmentUrl)) return UpdateManifest.DefaultManifestUrl;

            string value = environmentUrl.Trim();
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return UpdateManifest.DefaultManifestUrl;

            bool https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            // Plain http exists only for the loopback end-to-end test server;
            // a non-loopback http manifest would be a downgrade.
            bool loopbackHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && uri.IsLoopback;
            if (!https && !loopbackHttp) return UpdateManifest.DefaultManifestUrl;
            return value;
        }
    }

    /// <summary>
    /// Process-wide owner of the updater lifecycle: a delayed automatic check,
    /// the optional background download, and the explicit/exit-time apply. All
    /// network and disk work happens away from the UI thread; every failure is
    /// logged through <see cref="AppDiagnostics"/> and lands in a visible
    /// <see cref="UpdateState.Failed"/> state (transient check failures instead
    /// keep the previous state silently) rather than escaping to a caller.
    ///
    /// Every copy checks, so a portable/read-only copy still learns that a
    /// release exists; only a writable installed copy downloads and applies it.
    /// The other copies are pointed at the download page.
    /// </summary>
    public sealed class UpdateService
    {
        /// <summary>The public download page used by manual/unsupported installs.</summary>
        public const string DownloadPageUrl = "https://api.merrickzq.work/downloads/kobold/";

        private const string ManifestUrlEnvironmentVariable = "KOBOLD_UPDATE_MANIFEST_URL";
        private const string TempFolderName = "Kobold-update";
        private const string StageFolderName = "stage";
        private const string PackageFileNameFormat = "Kobold-v{0}.zip";
        private const long MaxManifestBytes = 256 * 1024;

        private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// A startup sweep only removes stage folders this old. The applier
        /// keeps the stage until its 15-second launch check passes, and a
        /// rollback still needs it; a fresh stage must never be swept from
        /// under an in-flight swap.
        /// </summary>
        private static readonly TimeSpan StageMinimumAge = TimeSpan.FromHours(1);

        /// <summary>
        /// How long a launch waits before sweeping stage leftovers. The
        /// applier that performed the swap may still be verifying the new
        /// build; its rollback needs the stage if that check fails.
        /// </summary>
        private static readonly TimeSpan StageSweepDelay = TimeSpan.FromSeconds(60);

        private static readonly Lazy<UpdateService> DefaultInstance =
            new Lazy<UpdateService>(() => new UpdateService(), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly object _gate = new object();
        private readonly UpdateStatus _status = new UpdateStatus();
        private readonly DispatcherTimer _checkTimer;

        private readonly Task<bool> _capabilityTask;
        private UpdateManifest _manifest;
        private CancellationTokenSource _downloadCts;
        private volatile bool _selfUpdatable;
        private int _started;
        private bool _firstAutomaticCheck = true;
        private bool _checkInFlight;
        private bool _downloadInFlight;
        private bool _applied;

        private UpdateService()
        {
            _checkTimer = new DispatcherTimer { Interval = StartupCheckDelay };
            _checkTimer.Tick += OnCheckTimerTick;

            // Resolve the writability capability once, off the UI thread: the
            // tray and settings constructors query it while the window/stage is
            // still being built, and the probe writes a file.
            _capabilityTask = Task.Run(() => ProbeSelfUpdatable());
        }

        /// <summary>The single production service; it never starts until <see cref="Start"/>.</summary>
        public static UpdateService Instance
        {
            get { return DefaultInstance.Value; }
        }

        /// <summary>
        /// A copy of the current state; safe to read from the settings window and
        /// the tray on any thread.
        /// </summary>
        public UpdateStatus Status
        {
            get
            {
                lock (_gate) return _status.Clone();
            }
        }

        /// <summary>
        /// Whether the running copy is the writable installed copy. Probed once
        /// on a worker thread (see the constructor); readers never block on the
        /// filesystem. Until the probe lands this is false.
        /// </summary>
        public bool IsSelfUpdatable
        {
            get { return _selfUpdatable; }
        }

        /// <summary>
        /// Raised after every state/progress change. Subscribers must marshal to
        /// the dispatcher themselves (the service may report from a worker
        /// thread); a throwing subscriber is logged and never breaks the updater.
        /// </summary>
        public event Action StateChanged;

        /// <summary>
        /// Starts the automatic cycle: the first check runs after 30 seconds,
        /// then every 24 hours. Call on the dispatcher; calling twice is a no-op.
        /// </summary>
        public void Start()
        {
            // Atomic claim: a throw while starting must leave the service
            // startable instead of permanently dead (and OnExit apply enabled
            // only through the attempted instance, not a poisoned flag).
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
            try
            {
                _checkTimer.Interval = StartupCheckDelay;
                _checkTimer.Start();

                // A successful apply leaves the applied version's stage folder
                // under %TEMP%. Sweep it later, not at once: the applier that
                // performed the swap may still be inside its 15-second launch
                // check, and a rollback needs the stage. Fire-and-forget: the
                // sweep is age-gated and catches its own errors.
                Task.Run(async () =>
                {
                    await Task.Delay(StageSweepDelay).ConfigureAwait(false);
                    CleanupStaleStages(null, StageMinimumAge);
                });

                AppDiagnostics.Write("[update] service started; first automatic check in 30 seconds");
            }
            catch
            {
                Volatile.Write(ref _started, 0);
                throw;
            }
        }

        /// <summary>
        /// Checks now, even when the automatic check is disabled or recently ran;
        /// the user asked for it. A newer release becomes Available, and an
        /// automatic install then downloads it in the background.
        /// </summary>
        public Task CheckNow()
        {
            return CheckAsync(automatic: false, enforceInterval: false);
        }

        /// <summary>
        /// Downloads the offered release and stages it. Never throws; failures
        /// land in Failed with a logged reason.
        /// </summary>
        public async Task DownloadNow()
        {
            CancellationTokenSource downloadCts = null;
            try
            {
                bool selfUpdatable = await GetSelfUpdatableAsync().ConfigureAwait(false);
                UpdateManifest manifest;
                lock (_gate)
                {
                    if (_downloadInFlight || _manifest == null ||
                        !UpdateStateRules.CanDownload(_status.State, selfUpdatable))
                    {
                        return;
                    }
                    _downloadInFlight = true;
                    manifest = _manifest;
                    // Own the token before anyone can observe Downloading:
                    // Reset/SkipVersion between these lines must be able to
                    // cancel this download, not miss it.
                    downloadCts = new CancellationTokenSource();
                    _downloadCts = downloadCts;
                }

                SetState(UpdateState.Downloading, status =>
                {
                    status.Version = manifest.Version;
                    status.ProgressPercent = 0;
                    status.StageDirectory = "";
                    status.FailureReason = UpdateFailureReason.None;
                });

                string version = manifest.Version;
                string root = Path.Combine(Path.GetTempPath(), TempFolderName, version);
                string zipPath = Path.Combine(root,
                    string.Format(CultureInfo.InvariantCulture, PackageFileNameFormat, version));
                string stagePath = Path.Combine(root, StageFolderName);

                try
                {
                    TryDeleteDirectory(root);
                    Directory.CreateDirectory(root);

                    AppDiagnostics.Write("[update] downloading " + version + " from " + manifest.Url);
                    var progress = new Progress<long>(received => ReportDownloadProgress(received, manifest.Size));
                    await UpdateDownloader.DownloadAsync(manifest, zipPath, progress, downloadCts.Token)
                        .ConfigureAwait(false);

                    if (downloadCts.IsCancellationRequested)
                    {
                        TryDeleteDirectory(root);
                        return;
                    }

                    string stage = UpdateDownloader.Extract(zipPath, stagePath, version);
                    TryDeleteFile(zipPath);

                    if (downloadCts.IsCancellationRequested)
                    {
                        TryDeleteDirectory(root);
                        return;
                    }

                    // This stage supersedes an older version's folder; keep
                    // only one install-sized copy in %TEMP%. The version just
                    // staged is protected; the sweep of older versions is
                    // immediate because only one apply can be in flight.
                    CleanupStaleStages(version, TimeSpan.Zero);

                    SetState(UpdateState.Ready, status =>
                    {
                        status.StageDirectory = stage;
                        status.ProgressPercent = 100;
                        status.FailureReason = UpdateFailureReason.None;
                    });
                    AppDiagnostics.Write("[update] release " + version + " staged at " + stage);
                }
                catch (OperationCanceledException)
                {
                    // A failed download or extract must not leave the zip or a
                    // half-unpacked stage behind.
                    TryDeleteDirectory(root);
                    if (downloadCts.IsCancellationRequested)
                    {
                        // SkipVersion/Reset already moved the service to Idle;
                        // do not overwrite their outcome with a failure.
                        AppDiagnostics.Write("[update] download cancelled");
                    }
                    else
                    {
                        AppDiagnostics.Write("[update] download timed out");
                        Fail(UpdateFailureReason.Timeout);
                    }
                }
                catch (Exception ex)
                {
                    // A failed download or extract must not leave the zip or a
                    // half-unpacked stage behind.
                    TryDeleteDirectory(root);
                    AppDiagnostics.Write("[update] download failed: " + ex.Message);
                    if (!downloadCts.IsCancellationRequested)
                    {
                        Fail(ClassifyFailure(ex));
                    }
                }
            }
            catch (Exception ex)
            {
                // Last line of defence: a download failure must never escape.
                AppDiagnostics.Write("[update] unexpected download failure: " + ex);
                if (downloadCts == null || !downloadCts.IsCancellationRequested)
                {
                    Fail(ClassifyFailure(ex));
                }
            }
            finally
            {
                lock (_gate)
                {
                    _downloadInFlight = false;
                    if (ReferenceEquals(_downloadCts, downloadCts)) _downloadCts = null;
                }
                if (downloadCts != null) downloadCts.Dispose();
            }
        }

        /// <summary>
        /// Starts the detached helper for a staged update and shuts the app down.
        /// This is the only explicit restart path; the helper waits for this
        /// process to exit before replacing anything.
        /// </summary>
        public void ApplyNow()
        {
            try
            {
                if (!IsSelfUpdatable)
                {
                    AppDiagnostics.Write("[update] apply skipped: this installation cannot replace itself");
                    return;
                }
                if (!TryStartApplier()) return;
                Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] apply failed: " + ex);
                Fail(UpdateFailureReason.Helper);
            }
        }

        /// <summary>
        /// The OnExit gate: a staged update installs only when the user left
        /// automatic installation on and this install can replace itself. It
        /// never shuts the app down (OnExit is already running), and never spawns
        /// a second helper after <see cref="ApplyNow"/> did.
        /// </summary>
        internal bool ApplyForExit()
        {
            try
            {
                AppConfig config = LoadConfig();
                bool autoInstall = config != null && config.UpdateAutoInstall;
                UpdateState state;
                lock (_gate) state = _status.State;

                if (!UpdateStateRules.ShouldInstallOnExit(state, autoInstall, IsSelfUpdatable)) return false;
                return TryStartApplier();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] on-exit apply failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Hides the currently offered release for good (persisted in the config)
        /// and returns to Idle. No version offered means nothing to skip.
        /// </summary>
        public void SkipVersion()
        {
            try
            {
                string version;
                lock (_gate) version = _status.Version;
                if (string.IsNullOrEmpty(version)) return;

                AppConfig config = LoadConfig();
                if (config != null)
                {
                    config.SkippedUpdateVersion = version;
                    try { config.Save(); }
                    catch (Exception ex)
                    {
                        AppDiagnostics.Write("[update] could not persist the skipped version: " + ex.Message);
                    }
                }

                CancelDownload();
                lock (_gate) _manifest = null;
                SetState(UpdateState.Idle, status =>
                {
                    status.Version = "";
                    status.ProgressPercent = 0;
                    status.StageDirectory = "";
                    status.FailureReason = UpdateFailureReason.None;
                    // The user has seen a check happen, so Idle means "up to
                    // date" rather than "not checked yet" after a skip.
                    status.HasChecked = true;
                });
                AppDiagnostics.Write("[update] skipped version " + version);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] skip failed: " + ex);
            }
        }

        /// <summary>Cancels any download and returns to the initial Idle state.</summary>
        public void Reset()
        {
            try
            {
                CancelDownload();
                lock (_gate) _manifest = null;
                SetState(UpdateState.Idle, status =>
                {
                    status.Version = "";
                    status.ProgressPercent = 0;
                    status.StageDirectory = "";
                    status.FailureReason = UpdateFailureReason.None;
                    status.HasChecked = false;
                });
                AppDiagnostics.Write("[update] reset");
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] reset failed: " + ex);
            }
        }

        private async Task CheckAsync(bool automatic, bool enforceInterval)
        {
            UpdateStatus previous = null;
            AppConfig config = null;
            try
            {
                config = LoadConfig();
                if (config == null)
                {
                    Fail(UpdateFailureReason.Unexpected);
                    return;
                }

                if (automatic)
                {
                    if (!UpdateStateRules.ShouldRunAutomaticCheck(config.UpdateCheckEnabled,
                            enforceInterval, config.LastUpdateCheckUtc, DateTime.UtcNow))
                    {
                        return;
                    }
                }

                lock (_gate)
                {
                    if (_checkInFlight || !UpdateStateRules.CanCheck(_status.State)) return;
                    _checkInFlight = true;
                    // Keep the offer intact while the request is in flight. A
                    // transient failure restores this exact snapshot instead of
                    // replacing a waiting update with "failed".
                    previous = _status.Clone();
                }

                string configuredUrl = Environment.GetEnvironmentVariable(ManifestUrlEnvironmentVariable);
                string manifestUrl = UpdateStateRules.ResolveManifestUrl(configuredUrl);
                if (!string.IsNullOrWhiteSpace(configuredUrl))
                {
                    AppDiagnostics.Write("[update] manifest URL override in effect: " + manifestUrl);
                }

                SetState(UpdateState.Checking, status =>
                {
                    status.FailureReason = UpdateFailureReason.None;
                });
                AppDiagnostics.Write("[update] checking " + manifestUrl);

                UpdateManifest manifest;
                try
                {
                    string json = await FetchManifestAsync(manifestUrl).ConfigureAwait(false);
                    string error;
                    if (!UpdateManifest.TryParse(json, out manifest, out error))
                    {
                        throw new UpdateException("invalid manifest: " + error);
                    }
                }
                catch (OperationCanceledException ex)
                {
                    // Timeout. An automatic check stays silent: keep the
                    // previous offer (or "up to date" line) and only wait out
                    // the interval. A manual check must say why it failed,
                    // otherwise the card keeps reading "Not checked yet".
                    AppDiagnostics.Write("[update] check timed out: " + ex.Message);
                    StampCheckAttempt(config);
                    if (automatic) RestoreStatus(previous);
                    else Fail(UpdateFailureReason.Timeout);
                    return;
                }
                catch (HttpRequestException ex)
                {
                    // No network, 404, 5xx, ...: an automatic check stays
                    // silent; a manual check surfaces the reason.
                    AppDiagnostics.Write("[update] check transport failure: " + ex.Message);
                    StampCheckAttempt(config);
                    if (automatic) RestoreStatus(previous);
                    else Fail(UpdateFailureReason.Network);
                    return;
                }
                catch (UpdateException ex)
                {
                    // A malformed/sha-invalid manifest is an integrity problem
                    // and is the one check failure worth surfacing.
                    AppDiagnostics.Write("[update] check parse failure: " + ex.Message);
                    Fail(UpdateFailureReason.Manifest);
                    return;
                }
                catch (Exception ex)
                {
                    AppDiagnostics.Write("[update] check failed: " + ex);
                    Fail(ClassifyFailure(ex));
                    return;
                }
                finally
                {
                    lock (_gate) _checkInFlight = false;
                }

                StampCheckAttempt(config);

                bool offered = UpdateStateRules.ShouldOfferUpdate(manifest, CurrentVersion,
                    config.SkippedUpdateVersion);
                if (!offered)
                {
                    lock (_gate) _manifest = null;
                    SetState(UpdateState.Idle, status =>
                    {
                        status.Version = "";
                        status.ProgressPercent = 0;
                        status.StageDirectory = "";
                        status.FailureReason = UpdateFailureReason.None;
                        status.HasChecked = true;
                    });
                    AppDiagnostics.Write("[update] no newer release (running " + CurrentVersion + ")");
                    return;
                }

                lock (_gate) _manifest = manifest;
                SetState(UpdateState.Available, status =>
                {
                    status.Version = manifest.Version;
                    status.ProgressPercent = 0;
                    status.StageDirectory = "";
                    status.FailureReason = UpdateFailureReason.None;
                    status.HasChecked = true;
                });
                AppDiagnostics.Write("[update] release " + manifest.Version + " is available");

                bool selfUpdatable = await GetSelfUpdatableAsync().ConfigureAwait(false);
                if (UpdateStateRules.ShouldAutoDownload(UpdateState.Available,
                        config.UpdateAutoInstall, selfUpdatable))
                {
                    await DownloadNow().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Never let a network/disk/config problem escape into the UI
                // thread that fired the click or the timer tick.
                AppDiagnostics.Write("[update] unexpected check failure: " + ex);
                lock (_gate) _checkInFlight = false;
                Fail(UpdateFailureReason.Unexpected);
            }
        }

        private void OnCheckTimerTick(object sender, EventArgs e)
        {
            bool firstCheck = _firstAutomaticCheck;
            _firstAutomaticCheck = false;

            // The 30-second timer switches to the daily cycle before the check
            // runs, so a slow check cannot reschedule itself.
            try
            {
                _checkTimer.Stop();
                _checkTimer.Interval = UpdatePolicy.DefaultCheckInterval;
                _checkTimer.Start();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] could not reschedule the update check: " + ex.Message);
            }

            RunAutomaticCheck(firstCheck);
        }

        private async void RunAutomaticCheck(bool enforceInterval)
        {
            try
            {
                await CheckAsync(automatic: true, enforceInterval: enforceInterval)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] automatic check crashed: " + ex);
            }
        }

        private static async Task<string> FetchManifestAsync(string url)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = ManifestTimeout;
                // The manifest is a few hundred bytes; the buffer cap keeps a
                // hostile or broken server from streaming an unbounded body.
                client.MaxResponseContentBufferSize = MaxManifestBytes;
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "Kobold/" + CurrentVersion);
                return await client.GetStringAsync(url).ConfigureAwait(false);
            }
        }

        private void ReportDownloadProgress(long received, long total)
        {
            if (total <= 0) return;

            int percent = (int)Math.Min(100L, Math.Max(0L, received * 100L / total));
            bool changed;
            lock (_gate)
            {
                changed = _status.State == UpdateState.Downloading && percent > _status.ProgressPercent;
                if (changed) _status.ProgressPercent = percent;
            }
            if (changed) RaiseChanged();
        }

        private bool TryStartApplier()
        {
            bool selfUpdatable = IsSelfUpdatable;
            string stage;
            string version;
            lock (_gate)
            {
                if (_applied || !UpdateStateRules.CanApply(_status.State, selfUpdatable)) return false;
                stage = _status.StageDirectory;
                version = _status.Version;
            }

            if (string.IsNullOrEmpty(stage) || !Directory.Exists(stage))
            {
                Fail(UpdateFailureReason.StagedFiles);
                return false;
            }

            string installDir;
            if (!UpdateInstall.TryGetInstallDir(out installDir))
            {
                Fail(UpdateFailureReason.InstallLocation);
                return false;
            }

            int processId;
            using (Process process = Process.GetCurrentProcess())
            {
                processId = process.Id;
            }

            if (!UpdateApplier.Apply(stage, installDir, processId, null))
            {
                AppDiagnostics.Write("[update] could not start the update helper");
                Fail(UpdateFailureReason.Helper);
                return false;
            }

            lock (_gate) _applied = true;
            AppDiagnostics.Write("[update] update helper started for " + version
                + " in " + installDir);
            return true;
        }

        private void CancelDownload()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                cts = _downloadCts;
                _downloadCts = null;
            }
            if (cts == null) return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] could not cancel the download: " + ex.Message);
            }
        }

        private bool ProbeSelfUpdatable()
        {
            bool value = false;
            try
            {
                value = UpdateInstall.IsSelfUpdatable();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] could not determine the install state: " + ex.Message);
            }
            _selfUpdatable = value;
            RaiseChanged();
            return value;
        }

        private async Task<bool> GetSelfUpdatableAsync()
        {
            try
            {
                return await _capabilityTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] install-state probe failed: " + ex.Message);
                return _selfUpdatable;
            }
        }

        private static void StampCheckAttempt(AppConfig config)
        {
            if (config == null) return;
            config.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            try { config.Save(); }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] could not save the check time: " + ex.Message);
            }
        }

        private void RestoreStatus(UpdateStatus previous)
        {
            if (previous == null) return;

            UpdateState previousState;
            lock (_gate)
            {
                previousState = _status.State;
                _status.State = previous.State;
                _status.Version = previous.Version;
                _status.ProgressPercent = previous.ProgressPercent;
                _status.StageDirectory = previous.StageDirectory;
                _status.FailureReason = previous.FailureReason;
                _status.HasChecked = previous.HasChecked;
            }

            if (previousState != previous.State)
            {
                AppDiagnostics.Write("[update] state " + previousState + " -> " + previous.State
                    + " (previous state restored after a transient failure)");
            }
            RaiseChanged();
        }

        private void Fail(UpdateFailureReason reason)
        {
            AppDiagnostics.Write("[update] failed: " + reason);
            SetState(UpdateState.Failed, status => status.FailureReason = reason);
        }

        private void SetState(UpdateState state, Action<UpdateStatus> change)
        {
            UpdateState previous;
            string version;
            lock (_gate)
            {
                previous = _status.State;
                _status.State = state;
                if (change != null) change(_status);
                version = _status.Version;
            }

            if (previous != state)
            {
                AppDiagnostics.Write("[update] state " + previous + " -> " + state
                    + (string.IsNullOrEmpty(version) ? "" : " (" + version + ")"));
            }
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            Action handler = StateChanged;
            if (handler == null) return;

            // One bad subscriber must not stop the others (or the updater).
            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action)subscriber)();
                }
                catch (Exception ex)
                {
                    AppDiagnostics.Write("[update] state handler failed: " + ex.Message);
                }
            }
        }

        private static AppConfig LoadConfig()
        {
            try
            {
                return WidgetManager.Instance.Config;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] could not read the configuration: " + ex.Message);
                return null;
            }
        }

        private static string CurrentVersion
        {
            get
            {
                Version version = typeof(UpdateService).Assembly.GetName().Version;
                if (version == null) return "0.0.0";
                int build = version.Build < 0 ? 0 : version.Build;
                return version.Major + "." + version.Minor + "." + build;
            }
        }

        /// <summary>
        /// A stable failure category; the localized sentence lives in
        /// <see cref="UpdateStateRules.DescribeFailureReason"/>.
        /// </summary>
        private static UpdateFailureReason ClassifyFailure(Exception ex)
        {
            if (ex == null) return UpdateFailureReason.Unexpected;
            if (ex is TaskCanceledException) return UpdateFailureReason.Timeout;
            if (ex is HttpRequestException) return UpdateFailureReason.Network;
            if (ex is UnauthorizedAccessException) return UpdateFailureReason.Disk;
            if (ex is IOException) return UpdateFailureReason.Disk;
            if (ex is UpdateException) return UpdateFailureReason.Integrity;
            return UpdateFailureReason.Unexpected;
        }

        /// <summary>
        /// Removes stale stage folders under %TEMP%\Kobold-update, plus the zip
        /// a failed extract left inside one. A download keeps
        /// <paramref name="keepVersion"/> and sweeps the older stage it
        /// supersedes immediately (<paramref name="minimumAge"/> is zero). The
        /// startup sweep passes null and a minimum age: a swap in flight keeps
        /// its stage until the applier's launch check has passed, because a
        /// rollback still needs it to find the files the failed package added.
        /// </summary>
        private static void CleanupStaleStages(string keepVersion, TimeSpan minimumAge)
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), TempFolderName);
                if (!Directory.Exists(root)) return;

                DateTime nowUtc = DateTime.UtcNow;
                foreach (string directory in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(directory);
                    if (string.Equals(name, keepVersion, StringComparison.OrdinalIgnoreCase)) continue;

                    // Only our own version-named folders; leave anything else
                    // under %TEMP% alone.
                    if (!UpdateVersion.TryParse(name, out _)) continue;

                    if (minimumAge > TimeSpan.Zero)
                    {
                        DateTime lastWriteUtc;
                        try
                        {
                            lastWriteUtc = Directory.GetLastWriteTimeUtc(directory);
                        }
                        catch (Exception)
                        {
                            // Unreadable timestamp: leave the folder alone.
                            continue;
                        }

                        // apply-update.ps1 touches the stage folder when it
                        // starts a swap, so an old stage that is only now being
                        // applied still counts as in use until the applier's
                        // launch check finishes.
                        string stageFolder = Path.Combine(directory, StageFolderName);
                        if (Directory.Exists(stageFolder))
                        {
                            try
                            {
                                DateTime stageWriteUtc = Directory.GetLastWriteTimeUtc(stageFolder);
                                if (stageWriteUtc > lastWriteUtc) lastWriteUtc = stageWriteUtc;
                            }
                            catch (Exception)
                            {
                                // Keep the version folder's timestamp.
                            }
                        }

                        if (nowUtc - lastWriteUtc < minimumAge) continue;
                    }

                    TryDeleteDirectory(directory);
                    AppDiagnostics.Write("[update] removed the stale stage " + directory);
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("[update] could not clean the stale stages: " + ex.Message);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
