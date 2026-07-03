# Mount Backup — N.I.N.A. plugin

A NINA 3.2 plugin that continuously backs up your mount's position and restores it
automatically after a mount freeze, an unplanned power-off, or a power outage.

*Magyar leírás: [README.hu.md](README.hu.md)*

## Why

The plugin was built for **clutchless mounts** (e.g. harmonic / strain wave drives).
Their mechanics guarantee that the mount cannot physically move while powered off — but
after a firmware freeze, a shutdown without parking, or a power outage the mount loses
its idea of where it is pointing.

In practice the mount then "wakes up" believing it points somewhere it doesn't, and quite
often the direction it actually points to is **obstructed** (trees, wall, roof), so plate
solving cannot recover the position either — you have to walk out and manually move the
mount to a clear patch of sky before anything else works.

Mount Backup removes that step: since the mount physically stayed where it was, the last
saved position is still correct, and a simple **sync** teaches the mount its real position
again — no plate solve, no manual repositioning.

## What it does

- **Saves the mount's physical axis pose to disk every second**: **hour angle,
  declination and pier side**. The pose is time-independent — a powered-off mount keeps
  its HA/Dec no matter how much time passes — and, unlike an Alt/Az *direction*, it stays
  unambiguous even at the celestial pole (at Dec 90° every hour-axis angle points the same
  way, but the driver derives RA from the hour-axis encoder, so HA = LST − RA preserves
  it). Writes are SSD-friendly (append + rotation above 256 KB; on load the last valid
  line wins).
- **Restores on connect or unpark**: recomputes RA from the saved hour angle for the
  *current* sidereal time and syncs the mount to it. Works after any downtime — minutes
  or weeks. After the sync the reported **pier side is verified** against the saved one;
  a mismatch (the driver calibrated to the mirrored axis solution) raises a loud error.
- If tracking is off, it is enabled just for the sync and **switched back off afterwards**;
  tracking that was already running (e.g. started by NINA on unpark) is left on.
- **Deviation threshold**: if the mount's reported axis pose (ΔHA, ΔDec, pier side) is
  already within the configured angle of the saved one (default 1°), the sync is skipped —
  on a healthy night the plugin never touches the mount. Set 0 to always sync.
- **Freeze watchdog**: if the reported pose stops changing while tracking, the plugin
  cross-checks RA and the driver's sidereal clock to tell the failure modes apart:
  - sidereal clock not advancing → the driver or the connection is frozen → **alarm**;
  - RA drifting at sidereal rate → the mount is standing still, not tracking → **alarm**;
  - RA steady while the sidereal clock advances → the mount tracks fine and the driver
    merely reports Alt/Az at coarse resolution → no alarm.
- **Boot protection**: a mount that has not finished starting up reports defaults — NaNs,
  zeros, or a sidereal clock that disagrees with the wall clock. Such snapshots are never
  saved and never used as a sync reference: the restore waits for two consecutive sane
  polls (up to 30 s), and if the restore fails or times out, **saving stays paused** so
  bogus data can never overwrite the last good position (resume with Restore now or Reset).
- **Restore verification**: a few seconds after a successful sync the reported pose is read
  back; if the driver accepted the sync but still reports the old position, or calibrated
  to the mirrored pier side, an error notification is raised.
- **Restore now** button: manual restore at any time (ignores the auto-restore switch and
  the deviation threshold).
- **Reset** button: deletes the saved position; nothing is restored until a new one is
  saved.
- Every step is logged with a timestamp to the plugin's dockable panel (Imaging tab) and
  to the NINA log; important events (successful/failed restore, watchdog alarm) also
  raise **NINA toast notifications**. Logging can be turned off on the Options page
  (errors and alarms are always logged).

Position file: `%LOCALAPPDATA%\NINA\MountBackup\position_<profileId>.csv` (one per profile).

## Install / build

Requires the .NET 8 SDK (or Visual Studio 2022) on Windows.

```bat
dotnet build -c Release
```

The post-build step copies the DLL to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\MountBackup\`;
NINA migrates it into its own version folder on startup. **Close NINA before building**
(the loaded DLL is locked), then start it again.

Check the result:

1. NINA → Options → Plugins → Installed: **Mount Backup** appears.
2. Imaging tab → panel selector (top right): enable the **Mount Backup** panel.
3. If the plugin does not load, search the newest file in `%LOCALAPPDATA%\NINA\Logs\`
   for `MountBackup` / MEF composition errors.

Tip: the freshness of the saved position follows NINA's *Device Polling Interval*
(default 2 s) — set it to 1 s under Options → General for best results.

## Settings (Options → Plugins → Mount Backup)

| Setting | Default | Meaning |
|---|---|---|
| Auto-restore on connect/unpark | on | restore runs automatically when the mount connects or unparks |
| Deviation threshold | 1° | skip the sync when the reported position is already this close to the saved one; 0 = always sync |
| Freeze watchdog + timeout | on, 60 s | alarm when the reported position freezes while tracking (see above) |
| Log plugin activity | on | Info/Warning logging to the NINA log and the panel; errors are always logged |

All settings are stored per NINA profile.
