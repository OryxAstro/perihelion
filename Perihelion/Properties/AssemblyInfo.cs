using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Perihelion")]
[assembly: AssemblyDescription("Standalone comet/asteroid non-sidereal tracking for PiNStars/Touch-N-Stars.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("OryxAstro")]
[assembly: AssemblyProduct("Perihelion")]
[assembly: AssemblyCopyright("Copyright © 2026 OryxAstro")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("263c5e4b-47d6-4ce1-83b8-b7b0b14ac6c9")]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

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
[assembly: AssemblyMetadata("LongDescription", @"Sets a mount's custom RA/Dec tracking rate for a comet or asteroid so it stays centered
in frame without fighting the sidereal rate — computed live, on-device, from real orbital
elements, with no external service or internet dependency in the field.

Features

* Every position and rate that actually drives hardware (Frame, Slew and Track, Set
  Tracking Rate, Quick Track) is corrected for light-time, stellar aberration, and the
  real observing site — a true topocentric position, not Earth's center — rather than a
  naive instantaneous geocentric snapshot.
* A native dockable panel (Imaging tab, real Windows NINA only) to browse live comet and
  asteroid brightness, preview tonight's altitude, a 10-night path, rate and orbital
  elements, then Frame, Slew and Track, Set Tracking Rate, Set Guider Shift Rate, or start
  an ad-hoc Quick Track — all in one place.
* The same tracking, browsing and Quick Track features from the Perihelion panel in
  Touch-N-Stars, for PINS and remote/mobile control either way.
* Quick Track's own optional auto re-apply, recomputing and resending the rate on an
  interval as a fast-moving object's true rate drifts over a session.
* Falls back to guiding-only shift tracking (PHD2's own native mechanism) when the mount's
  own driver can't take a custom base tracking rate at all.
* An ""Add to Sequence"" step for the Advanced Sequencer, for a full unattended run.
* Real observed comet brightness from COBS (the Comet OBServation database) shown alongside
  the predicted magnitude — the predicted value can be badly wrong during a real outburst,
  which is only obvious when the real observed number sits right next to it.

Object Types

* Comets — live elements from the Minor Planet Center's public comet-elements feed, with
  real observed brightness from COBS.
* Numbered asteroids — a curated list of bright, well-known asteroids (not the full MPC
  catalog).

Standalone: no shared code, cache format, or affiliation with NINA.Joko.Plugin.Orbitals.")]
