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
[assembly: AssemblyDescription("Continuously saves the mount position in a time-independent Alt/Az format and restores it via sync on connect or unpark. Made for clutchless mounts that cannot move while powered off.")]

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
[assembly: AssemblyMetadata("Repository", "https://github.com/garanddesign/nina.plugin.mountbackup")]

// Common tags for searching the plugin
[assembly: AssemblyMetadata("Tags", "Mount,Backup,Restore,Sync,Harmonic,Clutchless")]

[assembly: AssemblyMetadata("LongDescription", @"Mount Backup is made for mounts **without clutches** (e.g. harmonic drive mounts). Such a mount is guaranteed not to move physically while powered off — but after a firmware freeze or power loss it forgets where it is pointing.

This plugin:
* Saves the connected mount's position to a file every second in a **time-independent Alt/Az format** (SSD-friendly: appended lines with periodic rotation, the last valid line wins).
* When the mount **connects** or is **unparked**, it syncs the mount to the last saved position. The saved Alt/Az is converted to RA/Dec for the *current* time, so the mount points to the same place both physically and in coordinates — no matter how much time has passed.
* If tracking is off, it is enabled just for the sync, and tracking is **always switched off after the sync**.
* Provides a dockable panel for the imaging tab showing the last saved coordinates and a timestamped log of every save/restore action.
* A **Reset** button clears the saved position so no restore happens.")]

[assembly: ComVisible(false)]
