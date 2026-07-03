using System.Reflection;
using System.Runtime.InteropServices;

// [MANDATORY] The GUID is the unique identifier of this plugin. It must never change after publication.
[assembly: Guid("EA55D694-4CCD-4865-861E-97DE9042FC60")]

// [MANDATORY] The assembly version of this project
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// [MANDATORY] The name of your plugin
[assembly: AssemblyTitle("Mount Backup")]
// [MANDATORY] A short description of your plugin
[assembly: AssemblyDescription("Continuously saves the mount's physical axis pose (hour angle, declination and pier side) and restores it via sync on connect or unpark. Made for clutchless mounts that cannot move while powered off.")]

[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Ferenc Sagi")]
[assembly: AssemblyProduct("Mount Backup")]
[assembly: AssemblyCopyright("Copyright © 2026 Ferenc Sagi")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
// The repository where your plugin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/sagifer/mountbackup")]

// Common tags for searching the plugin
[assembly: AssemblyMetadata("Tags", "Mount,Backup,Restore,Sync,Harmonic,Clutchless")]

[assembly: AssemblyMetadata("LongDescription", @"Mount Backup is made for mounts **without clutches** (e.g. harmonic drive mounts). Such a mount is guaranteed not to move physically while powered off — but after a firmware freeze or power loss it forgets where it is pointing.

This plugin:
* Saves the connected mount's **physical axis pose** — hour angle, declination and pier side — to a file every second (SSD-friendly: appended lines with periodic rotation, the last valid line wins). The pose is time-independent (a powered-off mount keeps its HA/Dec) and stays unambiguous even at the celestial pole, unlike an Alt/Az direction.
* When the mount **connects** or is **unparked**, it syncs the mount to the last saved pose: RA is recomputed from the saved hour angle for the *current* sidereal time, so the mount points to the same place both physically and in coordinates — no matter how much time has passed. The pier side is verified after the sync.
* If tracking is off, it is enabled just for the sync and switched back off afterwards; tracking that was already running is left on.
* Provides a dockable panel for the imaging tab showing the last saved coordinates and a timestamped log of every save/restore action.
* A **Reset** button clears the saved position so no restore happens.")]

[assembly: ComVisible(false)]
