using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Perihelion")]
[assembly: AssemblyDescription("Standalone comet/asteroid non-sidereal tracking for PiNStars/Touch-N-Stars.")]
// Kept in sync with Perihelion.Windows' own AssemblyInfo.cs for consistency -- see its own
// comment for why this exists separately from AssemblyDescription above (the official
// NINA plugin template lists it as a required, distinct field).
[assembly: AssemblyMetadata("ShortDescription", "Standalone comet/asteroid non-sidereal tracking for PiNStars/Touch-N-Stars.")]
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

// The minimum version of PiNStars/N.I.N.A. this plugin is compatible with.
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.1031")]

[assembly: AssemblyMetadata("Homepage", "https://www.buymeacoffee.com/OryxAstro")]
[assembly: AssemblyMetadata("License", "GPL-3.0-or-later")]
[assembly: AssemblyMetadata("LicenseURL", "https://github.com/OryxAstro/perihelion/blob/main/LICENSE")]
[assembly: AssemblyMetadata("Repository", "https://github.com/OryxAstro/perihelion")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/OryxAstro/perihelion/blob/main/CHANGELOG.md")]
[assembly: AssemblyMetadata("Tags", "Comet,Asteroid,Tracking,Orbital")]
[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/OryxAstro/perihelion/main/docs/icon.png")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"Perihelion sets a mount's custom RA/Dec tracking rate for a comet or asteroid — or Quick
Tracks it right now for immediate use — so it stays centered in frame instead of drifting
against the sidereal rate.

To get started, open the Perihelion tab in Touch-N-Stars. Comets and asteroids load
automatically (from a local cache if one's warm, otherwise a live fetch). An internet
connection is needed for the live fetch, and whenever you use Update Sources to force a
fresh sync, but not continuously — once loaded, Perihelion computes position and rate
entirely on-device from the cached elements, with no external service or internet
dependency in the field beyond that.

Search or scroll the list, sorted brightest first, Load the object you want, then either
Quick Track it immediately or Add it to a sequence for an unattended run.

Features

* Every position and rate that actually drives hardware (Frame, Set Tracking Rate, Quick
  Track) is corrected for light-time, stellar aberration, and the observing site — a true
  topocentric position, not Earth's center — rather than a naive instantaneous geocentric
  snapshot.
* A native dockable panel (Imaging tab, Windows NINA only) to browse live comet and
  asteroid brightness, preview tonight's altitude, a 10-night path, rate and orbital
  elements, then Frame, Set Tracking Rate, Add to Sequence, or start an ad-hoc Quick
  Track — all in one place.
* The same tracking, browsing and Quick Track features from the Perihelion panel in
  Touch-N-Stars, for PINS and remote/mobile control either way.
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
