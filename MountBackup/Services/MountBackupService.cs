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
    /// Core engine of the plugin: a 1 s save loop persisting the mount's axis pose (hour
    /// angle / declination / pier side, all earth-fixed for a powered-off mount),
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
        private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan SlewWaitTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TrackingEnableTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DataSettleTimeout = TimeSpan.FromSeconds(30);
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
        private DateTime? implausibleSince;
        private bool implausibleWarned;
        private DateTime lastPoseChangeUtc = DateTime.UtcNow;
        private bool watchdogAlarmed;
        private (double RaHours, double LstHours)? freezeReference;
        private bool coarseReportingNoted;

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
                    Log(PluginLogLevel.Info, $"Loaded saved position: HA {AstroUtil.HoursToHMS(saved.HaHours)} / Dec {AstroUtil.DegreesToDMS(saved.DecDeg)} ({FormatPier(saved.PierSide)}), saved {FormatAge(DateTime.UtcNow - saved.TimestampUtc)} ago.");
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

            if (!IsPlausible(info, out var implausibleReason)) {
                implausibleSince ??= DateTime.UtcNow;
                if (!implausibleWarned && DateTime.UtcNow - implausibleSince > TimeSpan.FromSeconds(60)) {
                    implausibleWarned = true;
                    Log(PluginLogLevel.Warning, $"Mount data has been implausible for 60 s ({implausibleReason}) — nothing is saved until the mount reports sane data. Did it finish booting?");
                }
                return;
            }
            if (implausibleSince != null) {
                implausibleSince = null;
                implausibleWarned = false;
            }

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

            var (ha, _, decJnow) = AxisPoseFrom(info, siteLon);
            var position = new SavedPosition(
                DateTime.UtcNow,
                ha,
                decJnow,
                siteLat,
                siteLon,
                siteElev,
                info.RightAscension,
                info.Altitude,
                info.Azimuth,
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
        /// Called when the saved pose (HA/Dec) stopped changing while tracking. A static pose
        /// alone is not yet an alarm — RA and the driver's sidereal clock discriminate the
        /// failure modes:
        ///   - LST not advancing        → the driver/connection is frozen (stale data);
        ///   - RA climbing at ~sidereal → the encoders say the mount is standing still
        ///                                (HA = LST − RA stays constant) — a real stall;
        ///   - RA steady, LST advancing → the mount follows the sky; the pose only looked
        ///     static because the driver reports at coarse resolution — not an alarm.
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

            if (lstKnown && SavedPosition.Wrap24(info.SiderealTime - lstStartHours) < elapsedSiderealHours * 0.5) {
                RaiseWatchdogAlarm($"Mount driver data has been frozen for {(int)frozenFor.TotalSeconds} s (the reported sidereal clock is not advancing) — the driver or the connection may have hung!");
            } else if (raKnown && Math.Abs(SavedPosition.WrapPm12(info.RightAscension - raStartHours)) > elapsedSiderealHours * 0.5) {
                RaiseWatchdogAlarm($"The mount does not appear to be tracking: the reported position has been static for {(int)frozenFor.TotalSeconds} s while RA drifts at sidereal rate!");
            } else if (lstKnown && raKnown) {
                // positive evidence of healthy tracking — the driver just reports coarsely
                if (!coarseReportingNoted) {
                    coarseReportingNoted = true;
                    Log(PluginLogLevel.Info, $"Reported pose was static for {(int)frozenFor.TotalSeconds} s but RA and the sidereal clock show normal tracking — this driver reports at coarse resolution; the watchdog accounts for that.");
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

        /// <summary>
        /// A mount that has not finished booting reports defaults — NaNs, zeros, or a sidereal
        /// clock that disagrees with the wall clock. Nothing from such a snapshot may be trusted:
        /// neither saved to disk nor used as a sync reference. The driver computes RA from its
        /// own sidereal clock, so a broken clock invalidates the whole snapshot.
        /// </summary>
        private bool IsPlausible(TelescopeInfo info, out string reason) {
            if (double.IsNaN(info.RightAscension) || double.IsNaN(info.Declination)) {
                reason = "the driver does not report RA/Dec";
                return false;
            }
            if (info.RightAscension < 0 || info.RightAscension > 24 || Math.Abs(info.Declination) > 90) {
                reason = $"RA/Dec out of range (RA {info.RightAscension:F4} h, Dec {info.Declination:F4}°)";
                return false;
            }
            if (info.RightAscension == 0 && info.Declination == 0) {
                reason = "RA and Dec are both exactly 0 — boot-time defaults";
                return false;
            }
            if (!double.IsNaN(info.SiderealTime)) {
                var lon = double.IsNaN(info.SiteLongitude)
                    ? profileService.ActiveProfile.AstrometrySettings.Longitude
                    : info.SiteLongitude;
                var offSeconds = Math.Abs(SavedPosition.WrapPm12(info.SiderealTime - AstroUtil.GetLocalSiderealTime(DateTime.Now, lon))) * 3600.0;
                if (offSeconds > 60) {
                    reason = $"the driver's sidereal clock is {offSeconds:F0} s off from the computed one";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        /// <summary>Driver-reported RA/Dec normalized to JNOW plus the hour angle, using the
        /// driver's own sidereal clock when available so save and restore share one convention.</summary>
        private static (double HaHours, double RaJnowHours, double DecJnowDeg) AxisPoseFrom(TelescopeInfo info, double siteLonDeg) {
            var ra = info.RightAscension;
            var dec = info.Declination;
            if (info.EquatorialSystem == Epoch.J2000) {
                var coords = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.J2000).Transform(Epoch.JNOW);
                ra = coords.RA;
                dec = coords.Dec;
            }
            var lst = info.SiderealTime;
            if (double.IsNaN(lst)) { lst = AstroUtil.GetLocalSiderealTime(DateTime.Now, siteLonDeg); }
            return (AstroUtil.GetHourAngle(lst, ra), ra, dec);
        }

        private const string SavingPausedHint =
            "Saving stays paused to protect the last good position — use Restore now (or Reset) once the mount is healthy.";

        /// <summary>Some drivers accept a sync and still keep reporting the old position, and
        /// ASCOM has no way to demand a pier side — the driver picks the axis solution itself.
        /// Read the state back after a polling round and alarm loudly when the restore did not
        /// actually take.</summary>
        private async Task VerifyRestoreAfterSyncAsync(SavedPosition saved, CancellationToken token) {
            try {
                // wait out one polling round so GetInfo reflects the post-sync state
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            } catch (OperationCanceledException) { return; }

            var info = telescopeMediator.GetInfo();
            if (info == null || !info.Connected || !IsPlausible(info, out _)) { return; }

            var (haNow, _, decNow) = AxisPoseFrom(info, saved.SiteLonDeg);
            var haOffsetDeg = Math.Abs(SavedPosition.WrapPm12(haNow - saved.HaHours)) * 15.0;
            var decOffsetDeg = Math.Abs(decNow - saved.DecDeg);
            if (haOffsetDeg > 0.5 || decOffsetDeg > 0.5) {
                var message = $"The driver accepted the sync but still reports a pose {Math.Max(haOffsetDeg, decOffsetDeg):F2}° away from the restored one — the restore did NOT take effect. Check the driver's sync handling ('No Sync' option, alignment model).";
                Log(PluginLogLevel.Error, message);
                Notification.ShowError($"Mount Backup: {message}");
                return;
            }

            var pierNow = info.SideOfPier.ToString();
            if (!PierUnknown(saved.PierSide) && !PierUnknown(pierNow) && !string.Equals(pierNow, saved.PierSide, StringComparison.Ordinal)) {
                var message = $"After the sync the mount reports {FormatPier(pierNow)} but the saved pose was {FormatPier(saved.PierSide)} — the driver calibrated to the mirrored axis solution and GoTos may be wrong. Re-home or re-park the mount and restore again.";
                Log(PluginLogLevel.Error, message);
                Notification.ShowError($"Mount Backup: {message}");
                return;
            }

            Log(PluginLogLevel.Info, $"Restore verified: the driver reports the restored pose ({FormatPier(pierNow)}).");
        }

        private static bool PierUnknown(string pierSide) {
            return string.IsNullOrEmpty(pierSide) || pierSide.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string FormatPier(string pierSide) {
            if (PierUnknown(pierSide)) { return "pier side unknown"; }
            if (pierSide.IndexOf("east", StringComparison.OrdinalIgnoreCase) >= 0) { return "pier East"; }
            if (pierSide.IndexOf("west", StringComparison.OrdinalIgnoreCase) >= 0) { return "pier West"; }
            return pierSide;
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
                coarseReportingNoted = false;
                implausibleSince = null;
                implausibleWarned = false;
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
                        keepSuspended = true;
                        SetState(RestoreState.RestorePending);
                        Log(PluginLogLevel.Error, "Restore aborted — mount kept slewing for 30 s. " + SavingPausedHint);
                        return;
                    }
                    await Task.Delay(500, timeout.Token);
                }

                // a mount that is still booting reports defaults (zeros, bogus sidereal clock);
                // require two consecutive sane polls before trusting anything it says
                var settleWait = Stopwatch.StartNew();
                var saneStreak = 0;
                while (saneStreak < 2) {
                    info = telescopeMediator.GetInfo();
                    if (info == null || !info.Connected) {
                        Log(PluginLogLevel.Warning, "Restore aborted — mount disconnected while waiting for plausible data.");
                        return;
                    }
                    saneStreak = IsPlausible(info, out var implausibleReason) ? saneStreak + 1 : 0;
                    if (saneStreak < 2) {
                        if (settleWait.Elapsed > DataSettleTimeout) {
                            Interlocked.Exchange(ref restoreArmed, 1);
                            keepSuspended = true;
                            SetState(RestoreState.RestorePending);
                            var message = $"Restore aborted — the mount kept reporting implausible data for 30 s ({implausibleReason ?? "data not stable yet"}). Did it finish booting? " + SavingPausedHint;
                            Log(PluginLogLevel.Error, message);
                            Notification.ShowError($"Mount Backup: {message}");
                            return;
                        }
                        await Task.Delay(1000, timeout.Token);
                    }
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
                // saved pose says, it did not lose its state — leave it alone. Compared in AXIS
                // space (HA/Dec/pier), not as sky directions: at the pole all hour axis angles
                // point the same way, and a direction comparison would wrongly skip the sync.
                var threshold = DeviationThresholdDegrees;
                if (!force && threshold > 0 && !double.IsNaN(info.RightAscension) && !double.IsNaN(info.Declination)) {
                    var (haNow, _, decNow) = AxisPoseFrom(info, saved.SiteLonDeg);
                    var haOffsetDeg = Math.Abs(SavedPosition.WrapPm12(haNow - saved.HaHours)) * 15.0;
                    var decOffsetDeg = Math.Abs(decNow - saved.DecDeg);
                    var pierNow = info.SideOfPier.ToString();
                    var pierMatches = PierUnknown(pierNow) || PierUnknown(saved.PierSide)
                        || string.Equals(pierNow, saved.PierSide, StringComparison.Ordinal);
                    if (haOffsetDeg < threshold && decOffsetDeg < threshold && pierMatches) {
                        Log(PluginLogLevel.Info, $"Mount's reported axis pose is within the threshold of the saved one (ΔHA {haOffsetDeg:F3}°, ΔDec {decOffsetDeg:F3}°, threshold {threshold:F2}°) — sync skipped.");
                        Notification.ShowInformation("Mount Backup: mount position already matches the saved position — sync skipped.");
                        return;
                    }
                    Log(PluginLogLevel.Info, $"Mount's reported axis pose deviates from the saved one (ΔHA {haOffsetDeg:F3}°, ΔDec {decOffsetDeg:F3}°{(pierMatches ? "" : ", pier side differs")}; threshold {threshold:F2}°) — restoring.");
                }

                // the time-independent core: the saved hour angle is fixed to the earth, so the
                // equivalent RA is simply current LST minus saved HA
                var lstNow = info.SiderealTime;
                if (double.IsNaN(lstNow)) { lstNow = AstroUtil.GetLocalSiderealTime(DateTime.Now, saved.SiteLonDeg); }
                var coords = new Coordinates(
                    Angle.ByHours(SavedPosition.Wrap24(lstNow - saved.HaHours)),
                    Angle.ByDegree(saved.DecDeg),
                    Epoch.JNOW);
                if (info.EquatorialSystem == Epoch.J2000) {
                    coords = coords.Transform(Epoch.J2000);
                }

                Log(PluginLogLevel.Info,
                    $"Restoring HA {AstroUtil.HoursToHMS(saved.HaHours)} / Dec {AstroUtil.DegreesToDMS(saved.DecDeg)} ({FormatPier(saved.PierSide)}, saved {FormatAge(age)} ago) → RA {coords.RAString} / Dec {coords.DecString} ({coords.Epoch}).");

                var wasTracking = info.TrackingEnabled;
                if (!wasTracking) {
                    Log(PluginLogLevel.Info, "Tracking is off — enabling it for the sync.");
                    telescopeMediator.SetTrackingEnabled(true);
                    var trackWait = Stopwatch.StartNew();
                    while (telescopeMediator.GetInfo()?.TrackingEnabled != true) {
                        if (trackWait.Elapsed > TrackingEnableTimeout) {
                            keepSuspended = true;
                            SetState(RestoreState.RestorePending);
                            Log(PluginLogLevel.Error, "Could not enable tracking within 5 s — sync skipped. " + SavingPausedHint);
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
                    // only revert what the plugin enabled itself for the sync; tracking that
                    // was already running (e.g. started by NINA on unpark) is left alone
                    if (!wasTracking) {
                        telescopeMediator.SetTrackingEnabled(false);
                        Log(PluginLogLevel.Info, "Tracking switched back off after sync (it was off before the restore).");
                    }
                }

                if (synced) {
                    Log(PluginLogLevel.Info, $"Position restored: the mount now points to RA {coords.RAString} / Dec {coords.DecString}.");
                    Notification.ShowSuccess($"Mount Backup: position restored (RA {coords.RAString} / Dec {coords.DecString}).");
                    await VerifyRestoreAfterSyncAsync(saved, timeout.Token);
                } else {
                    Interlocked.Exchange(ref restoreArmed, 1);
                    keepSuspended = true;
                    SetState(RestoreState.RestorePending);
                    Log(PluginLogLevel.Error, "Sync was rejected. Check that 'No Sync' is not enabled under Options > Equipment > Telescope and that the driver accepts syncs. Park and unpark to retry. " + SavingPausedHint);
                    Notification.ShowError("Mount Backup: sync was rejected by the mount — position NOT restored.");
                }
            } catch (OperationCanceledException) {
                keepSuspended = true;
                SetState(RestoreState.RestorePending);
                Log(PluginLogLevel.Error, $"Restore timed out after {(int)RestoreTimeout.TotalSeconds} s. " + SavingPausedHint);
                Notification.ShowError("Mount Backup: position restore timed out.");
            } catch (Exception ex) {
                Logger.Error(ex);
                keepSuspended = true;
                SetState(RestoreState.RestorePending);
                Log(PluginLogLevel.Error, $"Restore failed: {ex.Message}. " + SavingPausedHint);
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
