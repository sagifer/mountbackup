using MountBackup.Model;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MountBackup.Services {

    /// <summary>
    /// Core engine of the plugin: a 1 s save loop persisting the mount pose in Alt/Az,
    /// and a restore state machine that syncs the last saved pose back to the mount on
    /// connect/unpark. Created exactly once; the plugin manifest and the dockable VM are
    /// both instantiated by MEF in unspecified order, so both attach via <see cref="GetOrCreate"/>.
    /// </summary>
    public sealed class MountBackupService : ITelescopeConsumer {
        public static readonly Guid PluginId = Guid.Parse("EA55D694-4CCD-4865-861E-97DE9042FC60");

        private static readonly object gate = new object();
        private static MountBackupService instance;

        public static MountBackupService GetOrCreate(IProfileService profileService, ITelescopeMediator telescopeMediator) {
            lock (gate) {
                if (instance == null) {
                    instance = new MountBackupService(profileService, telescopeMediator);
                    instance.Start();
                }
                return instance;
            }
        }

        public static async Task DisposeInstanceAsync() {
            MountBackupService toDispose;
            lock (gate) {
                toDispose = instance;
                instance = null;
            }
            if (toDispose != null) {
                await toDispose.ShutdownAsync();
            }
        }

        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SlewWaitTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TrackingEnableTimeout = TimeSpan.FromSeconds(5);
        private const int MaxBufferedLogEntries = 200;

        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly PositionFileStore store;
        private readonly PluginOptionsAccessor optionsAccessor;
        private readonly SemaphoreSlim restoreLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly LinkedList<PluginLogEntry> recentLog = new LinkedList<PluginLogEntry>();

        private Task saveLoopTask;
        private int saveSuspended;              // 1 = the save loop must not write (restore decision in flight)
        private int restoreArmed;               // 1 = the next connect/unpark trigger performs a restore
        private volatile TelescopeInfo lastInfo;
        private bool? lastAtPark;
        private volatile SavedPosition lastSaved;
        private SavedPosition lastWritten;
        private volatile RestoreState state = RestoreState.NotConnected;
        private DateTime? altAzNaNSince;
        private bool altAzNaNWarned;
        private DateTime lastPoseChangeUtc = DateTime.UtcNow;
        private bool watchdogAlarmed;
        private (double RaHours, double LstHours)? freezeReference;
        private bool coarseAltAzNoted;

        public event Action<PluginLogEntry> LogEmitted;
        public event Action StateChanged;

        private MountBackupService(IProfileService profileService, ITelescopeMediator telescopeMediator) {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            optionsAccessor = new PluginOptionsAccessor(profileService, PluginId);
            store = new PositionFileStore(profileService.ActiveProfile.Id);
        }

        public SavedPosition LastSaved => lastSaved;
        public TelescopeInfo LastTelescopeInfo => lastInfo;
        public RestoreState State => state;
        public string CurrentFilePath => store.FilePath;

        public bool AutoRestoreEnabled {
            get => optionsAccessor.GetValueBoolean(nameof(AutoRestoreEnabled), true);
            set {
                optionsAccessor.SetValueBoolean(nameof(AutoRestoreEnabled), value);
                Log(PluginLogLevel.Info, value ? "Auto-restore enabled." : "Auto-restore disabled.");
                StateChanged?.Invoke();
            }
        }

        /// <summary>Skip the restore sync when the mount's reported position is already within
        /// this many degrees of the saved position. 0 disables the check (always sync).</summary>
        public double DeviationThresholdDegrees {
            get => optionsAccessor.GetValueDouble(nameof(DeviationThresholdDegrees), 1.0);
            set {
                optionsAccessor.SetValueDouble(nameof(DeviationThresholdDegrees), Math.Max(0, value));
                StateChanged?.Invoke();
            }
        }

        public bool WatchdogEnabled {
            get => optionsAccessor.GetValueBoolean(nameof(WatchdogEnabled), true);
            set {
                optionsAccessor.SetValueBoolean(nameof(WatchdogEnabled), value);
                Log(PluginLogLevel.Info, value ? "Freeze watchdog enabled." : "Freeze watchdog disabled.");
                StateChanged?.Invoke();
            }
        }

        public int WatchdogTimeoutSeconds {
            get => optionsAccessor.GetValueInt32(nameof(WatchdogTimeoutSeconds), 60);
            set {
                optionsAccessor.SetValueInt32(nameof(WatchdogTimeoutSeconds), Math.Max(5, value));
                StateChanged?.Invoke();
            }
        }

        /// <summary>When disabled, Info/Warning entries are suppressed in both the NINA log and
        /// the panel log window; errors are always logged.</summary>
        public bool LoggingEnabled {
            get => optionsAccessor.GetValueBoolean(nameof(LoggingEnabled), true);
            set {
                optionsAccessor.SetValueBoolean(nameof(LoggingEnabled), value);
                Log(PluginLogLevel.Info, value ? "Plugin logging enabled." : "Plugin logging disabled (errors are still logged).", always: true);
                StateChanged?.Invoke();
            }
        }

        public PluginLogEntry[] GetRecentLogSnapshot() {
            lock (recentLog) {
                var arr = new PluginLogEntry[recentLog.Count];
                recentLog.CopyTo(arr, 0);
                return arr;
            }
        }

        private void Start() {
            telescopeMediator.Connected += OnConnectedAsync;
            telescopeMediator.Disconnected += OnDisconnectedAsync;
            telescopeMediator.Parked += OnParkedAsync;
            telescopeMediator.Unparked += OnUnparkedAsync;
            telescopeMediator.RegisterConsumer(this);
            profileService.ProfileChanged += OnProfileChanged;

            saveLoopTask = Task.Run(() => SaveLoopAsync(cts.Token));
            _ = InitializeAsync();
        }

        private async Task InitializeAsync() {
            try {
                var saved = await store.LoadLastAsync();
                lastSaved = saved;
                if (saved != null) {
                    Log(PluginLogLevel.Info, $"Loaded saved position: Alt {AstroUtil.DegreesToDMS(saved.AltDeg)} / Az {AstroUtil.DegreesToDMS(saved.AzDeg)}, saved {FormatAge(DateTime.UtcNow - saved.TimestampUtc)} ago.");
                } else {
                    Log(PluginLogLevel.Info, "No saved position on disk yet.");
                }

                var polling = profileService.ActiveProfile.ApplicationSettings.DevicePollingInterval;
                if (polling > SaveInterval.TotalSeconds) {
                    Log(PluginLogLevel.Info, $"Note: NINA's Device Polling Interval is {polling:F0} s, so the saved position is at most {polling:F0} s old. Set it to 1 s in Options > General for best results.");
                }
                StateChanged?.Invoke();
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private async Task ShutdownAsync() {
            try {
                profileService.ProfileChanged -= OnProfileChanged;
                telescopeMediator.Connected -= OnConnectedAsync;
                telescopeMediator.Disconnected -= OnDisconnectedAsync;
                telescopeMediator.Parked -= OnParkedAsync;
                telescopeMediator.Unparked -= OnUnparkedAsync;
                telescopeMediator.RemoveConsumer(this);

                cts.Cancel();
                if (saveLoopTask != null) {
                    try { await saveLoopTask; } catch (OperationCanceledException) { }
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                store.Dispose();
                restoreLock.Dispose();
                cts.Dispose();
            }
        }

        #region save loop

        private async Task SaveLoopAsync(CancellationToken token) {
            using var timer = new PeriodicTimer(SaveInterval);
            while (await timer.WaitForNextTickAsync(token)) {
                try {
                    await SaveTickAsync();
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }
        }

        private async Task SaveTickAsync() {
            if (Volatile.Read(ref saveSuspended) != 0) { return; }

            var info = telescopeMediator.GetInfo();
            if (info == null || !info.Connected) { return; }

            if (double.IsNaN(info.Altitude) || double.IsNaN(info.Azimuth)) {
                altAzNaNSince ??= DateTime.UtcNow;
                if (!altAzNaNWarned && DateTime.UtcNow - altAzNaNSince > TimeSpan.FromSeconds(60)) {
                    altAzNaNWarned = true;
                    Log(PluginLogLevel.Warning, "The mount driver does not report Alt/Az — Mount Backup cannot save the position with this driver.");
                }
                return;
            }
            altAzNaNSince = null;

            var siteLat = info.SiteLatitude;
            var siteLon = info.SiteLongitude;
            var siteElev = info.SiteElevation;
            if (double.IsNaN(siteLat) || double.IsNaN(siteLon)) {
                var astrometry = profileService.ActiveProfile.AstrometrySettings;
                siteLat = astrometry.Latitude;
                siteLon = astrometry.Longitude;
                siteElev = astrometry.Elevation;
            }
            if (double.IsNaN(siteElev)) { siteElev = 0; }

            var position = new SavedPosition(
                DateTime.UtcNow,
                info.Altitude,
                info.Azimuth,
                siteLat,
                siteLon,
                siteElev,
                info.RightAscension,
                info.Declination,
                info.EquatorialSystem.ToString(),
                info.SideOfPier.ToString());

            if (position.SamePoseAs(lastWritten)) {
                CheckWatchdog(info);
                return;
            }

            ResetWatchdogWindow();
            await store.AppendAsync(position);
            lastWritten = position;
            lastSaved = position;
            StateChanged?.Invoke();
        }

        /// <summary>
        /// A static reported Alt/Az alone is weak evidence of a freeze: many drivers round
        /// Alt/Az coarsely, and near the pole the true motion is only arcseconds per minute,
        /// so exact-equality "freezes" are normal while tracking works fine. RA and the
        /// driver's sidereal clock discriminate the real failure modes:
        ///   - LST not advancing        → the driver/connection is frozen (stale data);
        ///   - RA climbing at ~sidereal → the encoders say the mount is standing still;
        ///   - RA steady, LST advancing → the mount follows the sky and only the reported
        ///     Alt/Az is too coarse for the 1 s equality check — not an alarm.
        /// </summary>
        private void CheckWatchdog(TelescopeInfo info) {
            if (!WatchdogEnabled || !info.TrackingEnabled || info.AtPark || info.Slewing) {
                // pose is legitimately static here — keep the timer fresh so a later
                // tracking start does not alarm instantly
                ResetWatchdogWindow();
                return;
            }

            // RA/LST snapshot from the first tick of the static-pose window; the checks
            // below need the drift across the whole window, not a single reading
            freezeReference ??= (info.RightAscension, info.SiderealTime);

            var frozenFor = DateTime.UtcNow - lastPoseChangeUtc;
            if (watchdogAlarmed || frozenFor <= TimeSpan.FromSeconds(WatchdogTimeoutSeconds)) { return; }

            var (raStartHours, lstStartHours) = freezeReference.Value;
            var elapsedSiderealHours = frozenFor.TotalHours * 1.0027379;
            var lstKnown = !double.IsNaN(lstStartHours) && !double.IsNaN(info.SiderealTime);
            var raKnown = !double.IsNaN(raStartHours) && !double.IsNaN(info.RightAscension);

            if (lstKnown && Wrap24(info.SiderealTime - lstStartHours) < elapsedSiderealHours * 0.5) {
                RaiseWatchdogAlarm($"Mount driver data has been frozen for {(int)frozenFor.TotalSeconds} s (the reported sidereal clock is not advancing) — the driver or the connection may have hung!");
            } else if (raKnown && Math.Abs(WrapPm12(info.RightAscension - raStartHours)) > elapsedSiderealHours * 0.5) {
                RaiseWatchdogAlarm($"The mount does not appear to be tracking: the reported position has been static for {(int)frozenFor.TotalSeconds} s while RA drifts at sidereal rate!");
            } else if (lstKnown && raKnown) {
                // positive evidence of healthy tracking — the driver just reports coarse Alt/Az
                if (!coarseAltAzNoted) {
                    coarseAltAzNoted = true;
                    Log(PluginLogLevel.Info, $"Reported Alt/Az was static for {(int)frozenFor.TotalSeconds} s but RA and the sidereal clock show normal tracking — this driver reports Alt/Az at coarse resolution; the watchdog accounts for that.");
                }
                ResetWatchdogWindow();
            } else {
                // driver reports no usable RA/LST — fall back to the plain static-pose alarm
                RaiseWatchdogAlarm($"Mount position has not changed for {(int)frozenFor.TotalSeconds} s while tracking — the mount may have frozen!");
            }
        }

        private void RaiseWatchdogAlarm(string message) {
            watchdogAlarmed = true;
            Log(PluginLogLevel.Error, message);
            Notification.ShowError($"Mount Backup: {message}");
        }

        private void ResetWatchdogWindow() {
            lastPoseChangeUtc = DateTime.UtcNow;
            watchdogAlarmed = false;
            freezeReference = null;
        }

        private static double Wrap24(double hours) {
            hours %= 24;
            return hours < 0 ? hours + 24 : hours;
        }

        private static double WrapPm12(double hours) {
            var h = Wrap24(hours);
            return h > 12 ? h - 24 : h;
        }

        #endregion

        #region telescope events

        public void UpdateDeviceInfo(TelescopeInfo deviceInfo) {
            try {
                var previousAtPark = lastAtPark;
                lastInfo = deviceInfo;

                if (deviceInfo != null && deviceInfo.Connected) {
                    lastAtPark = deviceInfo.AtPark;
                    if (previousAtPark == false && deviceInfo.AtPark) {
                        // park observed via polling (covers parks initiated outside NINA)
                        Interlocked.Exchange(ref restoreArmed, 1);
                    } else if (previousAtPark == true && !deviceInfo.AtPark) {
                        // unpark observed via polling; the CAS inside the restore keeps this
                        // from double-firing with NINA's own Unparked event
                        _ = RunRestoreSafeAsync("unpark detected via polling");
                    }
                } else {
                    lastAtPark = null;
                }
                StateChanged?.Invoke();
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private async Task OnConnectedAsync(object sender, EventArgs e) {
            try {
                // suspend saving before anything awaits: a mount that lost its state reports a
                // bogus position right after connect and that must never reach the file
                Interlocked.Exchange(ref saveSuspended, 1);
                SetState(RestoreState.RestorePending);
                Log(PluginLogLevel.Info, "Mount connected.");

                if (!AutoRestoreEnabled) {
                    Log(PluginLogLevel.Info, "Auto-restore is disabled — skipping position restore.");
                    ResumeSaving();
                    return;
                }

                var saved = await store.LoadLastAsync();
                lastSaved = saved;
                if (saved == null) {
                    Log(PluginLogLevel.Info, "No saved position — nothing to restore.");
                    ResumeSaving();
                    return;
                }

                Interlocked.Exchange(ref restoreArmed, 1);
                var info = telescopeMediator.GetInfo();
                if (info != null && info.AtPark) {
                    SetState(RestoreState.WaitingForUnpark);
                    Log(PluginLogLevel.Info, "Mount is parked — restore deferred until unpark (saving stays paused).");
                    return;
                }

                await TriggerRestoreAsync("mount connected");
            } catch (Exception ex) {
                Logger.Error(ex);
                Log(PluginLogLevel.Error, $"Connect handling failed: {ex.Message}");
                ResumeSaving();
            }
        }

        private Task OnDisconnectedAsync(object sender, EventArgs e) {
            try {
                Interlocked.Exchange(ref saveSuspended, 0);
                Interlocked.Exchange(ref restoreArmed, 0);
                lastAtPark = null;
                lastWritten = null;
                ResetWatchdogWindow();
                coarseAltAzNoted = false;
                SetState(RestoreState.NotConnected);
                Log(PluginLogLevel.Info, "Mount disconnected. Last saved position is kept for the next connect.");
            } catch (Exception ex) {
                Logger.Error(ex);
            }
            return Task.CompletedTask;
        }

        private Task OnParkedAsync(object sender, EventArgs e) {
            try {
                // the park pose is a real physical pose — keep saving; arm a restore for the unpark
                Interlocked.Exchange(ref restoreArmed, 1);
                Log(PluginLogLevel.Info, "Mount parked.");
            } catch (Exception ex) {
                Logger.Error(ex);
            }
            return Task.CompletedTask;
        }

        private async Task OnUnparkedAsync(object sender, EventArgs e) {
            try {
                Log(PluginLogLevel.Info, "Mount unparked.");
                Interlocked.Exchange(ref restoreArmed, 1);
                await TriggerRestoreAsync("mount unparked");
            } catch (Exception ex) {
                Logger.Error(ex);
                Log(PluginLogLevel.Error, $"Unpark handling failed: {ex.Message}");
            }
        }

        private async Task RunRestoreSafeAsync(string reason, bool force = false) {
            try {
                await TriggerRestoreAsync(reason, force);
            } catch (Exception ex) {
                Logger.Error(ex);
                Log(PluginLogLevel.Error, $"Restore failed: {ex.Message}");
            }
        }

        private void OnProfileChanged(object sender, EventArgs e) {
            _ = HandleProfileChangeAsync();
        }

        private async Task HandleProfileChangeAsync() {
            try {
                var profileId = profileService.ActiveProfile.Id;
                await store.SwitchProfileAsync(profileId);
                lastWritten = null;
                lastSaved = await store.LoadLastAsync();
                Log(PluginLogLevel.Info, $"Profile changed — now using {store.FilePath}.");
                StateChanged?.Invoke();
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        #endregion

        #region restore

        /// <summary>Manual restore from the panel button: runs even when auto-restore is
        /// disabled and ignores the deviation threshold.</summary>
        public Task RestoreNowAsync() {
            return RunRestoreSafeAsync("manual restore", force: true);
        }

        private async Task TriggerRestoreAsync(string reason, bool force = false) {
            if (force) {
                // a manual restore also satisfies any pending automatic one
                Interlocked.Exchange(ref restoreArmed, 0);
            } else {
                if (!AutoRestoreEnabled) {
                    Interlocked.Exchange(ref restoreArmed, 0);
                    ResumeSaving();
                    return;
                }
                // exactly one of the competing triggers (Connected / Unparked event / polled AtPark
                // transition, possibly within the same second on different threads) may proceed
                if (Interlocked.CompareExchange(ref restoreArmed, 0, 1) != 1) { return; }
            }

            await restoreLock.WaitAsync();
            var keepSuspended = false;
            try {
                Interlocked.Exchange(ref saveSuspended, 1);
                SetState(RestoreState.Restoring);
                Log(PluginLogLevel.Info, $"Restore started ({reason}).");
                using var timeout = new CancellationTokenSource(RestoreTimeout);

                var info = telescopeMediator.GetInfo();
                if (info == null || !info.Connected) {
                    Log(PluginLogLevel.Warning, "Restore aborted — mount is not connected.");
                    if (force) { Notification.ShowWarning("Mount Backup: the mount is not connected."); }
                    return;
                }
                if (info.AtPark) {
                    Interlocked.Exchange(ref restoreArmed, 1);
                    SetState(RestoreState.WaitingForUnpark);
                    keepSuspended = true;
                    Log(PluginLogLevel.Info, "Mount is parked — restore deferred until unpark.");
                    return;
                }

                var slewWait = Stopwatch.StartNew();
                while (telescopeMediator.GetInfo()?.Slewing == true) {
                    if (slewWait.Elapsed > SlewWaitTimeout) {
                        Log(PluginLogLevel.Error, "Restore aborted — mount kept slewing for 30 s.");
                        return;
                    }
                    await Task.Delay(500, timeout.Token);
                }

                var saved = await store.LoadLastAsync();
                if (saved == null) {
                    Log(PluginLogLevel.Info, "No saved position — nothing to restore.");
                    return;
                }

                var age = DateTime.UtcNow - saved.TimestampUtc;
                if (age > TimeSpan.FromHours(24)) {
                    Log(PluginLogLevel.Warning, $"Saved position is {FormatAge(age)} old — restoring anyway (a clutchless mount cannot have moved).");
                }

                if (!double.IsNaN(info.SiteLatitude) && !double.IsNaN(info.SiteLongitude) &&
                    (Math.Abs(info.SiteLatitude - saved.SiteLatDeg) > 0.01 || Math.Abs(info.SiteLongitude - saved.SiteLonDeg) > 0.01)) {
                    Log(PluginLogLevel.Warning,
                        $"Saved site ({saved.SiteLatDeg:F4}, {saved.SiteLonDeg:F4}) differs from the mount's current site ({info.SiteLatitude:F4}, {info.SiteLongitude:F4}) — was the rig moved? The restored position may be wrong.");
                }

                // deviation threshold: if the mount already believes it is (nearly) where the
                // saved pose says, it did not lose its state — leave it alone
                var threshold = DeviationThresholdDegrees;
                if (!force && threshold > 0 && !double.IsNaN(info.Altitude) && !double.IsNaN(info.Azimuth)) {
                    var separation = AngularSeparationDeg(saved.AltDeg, saved.AzDeg, info.Altitude, info.Azimuth);
                    if (separation < threshold) {
                        Log(PluginLogLevel.Info, $"Mount's reported position is within {separation:F3}° of the saved position (threshold {threshold:F2}°) — sync skipped.");
                        Notification.ShowInformation("Mount Backup: mount position already matches the saved position — sync skipped.");
                        return;
                    }
                    Log(PluginLogLevel.Info, $"Mount's reported position deviates {separation:F3}° from the saved position — restoring.");
                }

                // the time-independent core: saved Alt/Az + saved site, evaluated at the CURRENT time
                var topo = new TopocentricCoordinates(
                    Angle.ByDegree(saved.AzDeg),
                    Angle.ByDegree(saved.AltDeg),
                    Angle.ByDegree(saved.SiteLatDeg),
                    Angle.ByDegree(saved.SiteLonDeg),
                    saved.SiteElevM);
                var coords = topo.Transform(Epoch.JNOW);
                if (info.EquatorialSystem == Epoch.J2000) {
                    coords = coords.Transform(Epoch.J2000);
                }

                Log(PluginLogLevel.Info,
                    $"Restoring Alt {AstroUtil.DegreesToDMS(saved.AltDeg)} / Az {AstroUtil.DegreesToDMS(saved.AzDeg)} (saved {FormatAge(age)} ago) → RA {coords.RAString} / Dec {coords.DecString} ({coords.Epoch}).");

                var wasTracking = info.TrackingEnabled;
                if (!wasTracking) {
                    Log(PluginLogLevel.Info, "Tracking is off — enabling it for the sync.");
                    telescopeMediator.SetTrackingEnabled(true);
                    var trackWait = Stopwatch.StartNew();
                    while (telescopeMediator.GetInfo()?.TrackingEnabled != true) {
                        if (trackWait.Elapsed > TrackingEnableTimeout) {
                            Log(PluginLogLevel.Error, "Could not enable tracking within 5 s — sync skipped.");
                            Notification.ShowError("Mount Backup: could not enable tracking — position restore skipped.");
                            telescopeMediator.SetTrackingEnabled(false);
                            return;
                        }
                        await Task.Delay(200, timeout.Token);
                    }
                }

                bool synced;
                try {
                    synced = await telescopeMediator.Sync(coords);
                } finally {
                    // tracking is ALWAYS switched off after the sync, even if it was on before
                    telescopeMediator.SetTrackingEnabled(false);
                    Log(PluginLogLevel.Info, "Tracking switched off after sync.");
                }

                if (synced) {
                    Log(PluginLogLevel.Info, $"Position restored: the mount now points to RA {coords.RAString} / Dec {coords.DecString}.");
                    Notification.ShowSuccess($"Mount Backup: position restored (RA {coords.RAString} / Dec {coords.DecString}).");
                } else {
                    Log(PluginLogLevel.Error, "Sync was rejected. Check that 'No Sync' is not enabled under Options > Equipment > Telescope and that the driver accepts syncs. Park and unpark to retry.");
                    Notification.ShowError("Mount Backup: sync was rejected by the mount — position NOT restored.");
                    Interlocked.Exchange(ref restoreArmed, 1);
                }
            } catch (OperationCanceledException) {
                Log(PluginLogLevel.Error, "Restore timed out after 60 s.");
                Notification.ShowError("Mount Backup: position restore timed out.");
            } catch (Exception ex) {
                Logger.Error(ex);
                Log(PluginLogLevel.Error, $"Restore failed: {ex.Message}");
                Notification.ShowError($"Mount Backup: position restore failed — {ex.Message}");
            } finally {
                if (!keepSuspended) {
                    ResumeSaving();
                }
                restoreLock.Release();
            }
        }

        public async Task ResetAsync() {
            try {
                await store.DeleteAsync();
                lastSaved = null;
                lastWritten = null;
                Interlocked.Exchange(ref restoreArmed, 0);
                Log(PluginLogLevel.Warning, "RESET — saved position deleted; no position will be restored until a new one is saved.");

                if (state == RestoreState.RestorePending || state == RestoreState.WaitingForUnpark) {
                    ResumeSaving();
                } else {
                    StateChanged?.Invoke();
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                Log(PluginLogLevel.Error, $"Reset failed: {ex.Message}");
            }
        }

        private void ResumeSaving() {
            Interlocked.Exchange(ref saveSuspended, 0);
            // the suspension window must not count as "frozen" for the watchdog
            ResetWatchdogWindow();
            var connected = telescopeMediator.GetInfo()?.Connected == true;
            SetState(connected ? RestoreState.Monitoring : RestoreState.NotConnected);
        }

        #endregion

        private void SetState(RestoreState newState) {
            state = newState;
            StateChanged?.Invoke();
        }

        private void Log(PluginLogLevel level, string message, bool always = false) {
            if (!always && level != PluginLogLevel.Error && !LoggingEnabled) { return; }

            switch (level) {
                case PluginLogLevel.Warning:
                    Logger.Warning($"[Mount Backup] {message}");
                    break;
                case PluginLogLevel.Error:
                    Logger.Error($"[Mount Backup] {message}");
                    break;
                default:
                    Logger.Info($"[Mount Backup] {message}");
                    break;
            }

            var entry = new PluginLogEntry(DateTime.Now, level, message);
            lock (recentLog) {
                recentLog.AddFirst(entry);
                while (recentLog.Count > MaxBufferedLogEntries) {
                    recentLog.RemoveLast();
                }
            }
            LogEmitted?.Invoke(entry);
        }

        public static double AngularSeparationDeg(double alt1Deg, double az1Deg, double alt2Deg, double az2Deg) {
            const double d2r = Math.PI / 180.0;
            var cosSep = Math.Sin(alt1Deg * d2r) * Math.Sin(alt2Deg * d2r)
                       + Math.Cos(alt1Deg * d2r) * Math.Cos(alt2Deg * d2r) * Math.Cos((az1Deg - az2Deg) * d2r);
            return Math.Acos(Math.Clamp(cosSep, -1.0, 1.0)) / d2r;
        }

        public static string FormatAge(TimeSpan age) {
            if (age < TimeSpan.Zero) { age = TimeSpan.Zero; }
            if (age.TotalDays >= 1) { return $"{(int)age.TotalDays}d {age.Hours}h"; }
            if (age.TotalHours >= 1) { return $"{(int)age.TotalHours}h {age.Minutes}m"; }
            if (age.TotalMinutes >= 1) { return $"{(int)age.TotalMinutes}m {age.Seconds}s"; }
            return $"{(int)age.TotalSeconds}s";
        }

        public void Dispose() {
            // NINA never disposes consumers itself; real cleanup happens in ShutdownAsync via plugin Teardown
        }
    }
}
