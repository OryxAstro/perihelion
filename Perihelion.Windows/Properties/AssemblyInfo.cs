using System.Reflection;
using System.Runtime.InteropServices;

// A Windows-only counterpart to Perihelion/Properties/AssemblyInfo.cs (PINS), not a copy
// merged in via wildcard include -- Perihelion.Windows.csproj compiles this file instead of that
// one. Two fields genuinely need to differ per platform: MinimumApplicationVersion (PINS' own
// baseline means nothing to NINA's own version family, and vice versa) and
// AssemblyDescription (the one-line summary shown in NINA's own Plugin Manager list should
// describe what this DLL does when running there, not lead with PINS/Touch-N-Stars). Everything
// else -- GUID, License*, Repository, Tags, Homepage, LongDescription, screenshots -- stays the
// same on purpose: it's the same plugin, same identity, just two frontends, and the GUID
// specifically must never differ (PINS and Windows never run in the same NINA process at once,
// so sharing it causes no conflict, and NINA's own per-plugin settings storage on both
// platforms already keys off this exact value).
[assembly: AssemblyTitle("Perihelion")]
[assembly: AssemblyDescription("Comet and asteroid non-sidereal tracking, natively in NINA's Imaging tab -- live orbital tracking, Quick Track, and a Framing Composer.")]
// The official plugin template (isbeorn/nina.plugin.template) lists
// AssemblyMetadata("ShortDescription") as a REQUIRED field, distinct from the standard
// AssemblyDescription above -- CreateManifest.ps1 reads assembly metadata to auto-populate the
// manifest, so without this the manifest's own required ShortDescription could end up empty
// even with AssemblyDescription set. Same text as AssemblyDescription, kept in sync deliberately.
[assembly: AssemblyMetadata("ShortDescription", "Comet and asteroid non-sidereal tracking, natively in NINA's Imaging tab -- live orbital tracking, Quick Track, and a Framing Composer.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("OryxAstro")]
[assembly: AssemblyProduct("Perihelion")]
[assembly: AssemblyCopyright("Copyright © 2026 OryxAstro")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("263c5e4b-47d6-4ce1-83b8-b7b0b14ac6c9")]

[assembly: AssemblyVersion("1.1.0.8")]
[assembly: AssemblyFileVersion("1.1.0.8")]

// NINA's own version family this build actually targets -- NINA.Plugin 3.2.0.9001 is the
// exact pinned package version Perihelion.Windows.csproj references (see its own comment for
// why). NOT PINS' own "3.0.0.1031" baseline, which has no meaning for a Windows NINA
// install.
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]

[assembly: AssemblyMetadata("Homepage", "https://www.buymeacoffee.com/OryxAstro")]
[assembly: AssemblyMetadata("License", "GPL-3.0-or-later")]
[assembly: AssemblyMetadata("LicenseURL", "https://github.com/OryxAstro/perihelion/blob/main/LICENSE")]
[assembly: AssemblyMetadata("Repository", "https://github.com/OryxAstro/perihelion")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/OryxAstro/perihelion/blob/main/CHANGELOG.md")]
[assembly: AssemblyMetadata("Tags", "Comet,Asteroid,Tracking,Orbital,Framing")]
[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/OryxAstro/perihelion/main/docs/icon.png")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"Perihelion sets a mount's custom RA/Dec tracking rate for a comet or asteroid — or Quick
Tracks it right now for immediate use — so it stays centered in frame instead of drifting
against the sidereal rate.

To get started, open the Perihelion panel in NINA's own Imaging tab. Comets and asteroids
load automatically (from a local cache if one's warm, otherwise a live fetch). An internet
connection is needed for the live fetch, and whenever you use Update Sources to force a
fresh sync, but not continuously — once loaded, Perihelion computes position and rate
entirely on-device from the cached elements, with no external service or internet
dependency in the field beyond that.

Search or scroll the list, sorted brightest first, click Load on the object you want, then
Frame, Set Tracking Rate, Add to Sequence, or Quick Track it.

Features

* Every position and rate that actually drives hardware (Frame, Set Tracking Rate, Quick
  Track) is corrected for light-time, stellar aberration, and the observing site — a true
  topocentric position, not Earth's center — rather than a naive instantaneous geocentric
  snapshot.
* A native dockable panel in NINA's own Imaging tab to browse live comet and asteroid
  brightness, preview tonight's altitude, a 10-night path, rate and orbital elements,
  then Frame, Set Tracking Rate, Add to Sequence, or start an ad-hoc Quick Track — all
  in one place.
* A popup Framing Composer — a sky map (live photographic sources, plus NINA's own
  Offline Sky Map and Cache), pan/zoom, rotation, and offset capture for framing
  off-center targets like a comet's tail.
* Quick Track's own optional auto re-apply, recomputing and resending the rate on an
  interval as a fast-moving object's true rate drifts over a session.
* Falls back to guiding-only shift tracking (PHD2's own native mechanism) when the mount's
  own driver can't take a custom base tracking rate at all.
* An ""Add to Sequence"" step for the Advanced Sequencer, for a full unattended run.
* Observed comet brightness from COBS (the Comet OBServation database) shown alongside
  the predicted magnitude — the predicted value can be badly wrong during an outburst,
  which is only obvious when the observed number sits right next to it.

Object Types

* Comets — live elements from the Minor Planet Center's public comet-elements feed, with
  observed brightness from COBS.
* Asteroids — live elements from JPL's Small-Body Database, filtered to a configurable
  brightness threshold (not the full minor-planet catalog).")]
