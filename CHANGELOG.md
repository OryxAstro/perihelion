# Changelog

All notable changes to Perihelion are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.0.1] — 2026-09-09

### Added

- **Periodic tracking-rate reapply for Add to Sequence** (`PerihelionReapplyTrigger`) — Add to
  Sequence previously applied the tracking rate exactly once, when the sequencer first reached
  it; a long unattended run had nothing keeping it current as the object's true rate drifted,
  unlike Quick Track, which already reapplies on a timer. Reuses the same configured interval.
  Windows native panel only for now — Touch-N-Stars' own sequence builder doesn't add this
  trigger yet.

### Fixed

- The Windows Options page's reapply-interval field was labeled "Quick Track Reapply Interval"
  even though it now also governs the sequence-side trigger above — renamed to just "Reapply
  Interval".

## [1.1.0.0] — 2026-09-08

### Added

- **Live-fetched asteroid orbital elements** from JPL's Small-Body Database, replacing a fixed
  table baked into the plugin itself — same 24-hour cache and explicit Sync Now pattern already
  used for comets.
- **Epoch-staleness warning** for both comets and asteroids — flags an object whose orbital
  elements sit unusually far from their own reference epoch, since pure two-body propagation
  degrades the further out it runs with no way to account for ongoing planetary perturbation.
- **EQMOD RA Rate Correction** — an opt-in fix for a unit mismatch confirmed independently in
  both the Windows ASCOM EQMOD driver and INDI's own separate EQMod driver: both read the RA
  tracking rate as raw arcsec/sec rather than the seconds-of-RA-per-sidereal-second NINA itself
  sends, which makes RA tracking come out roughly 15× too slow unless corrected.
- **Configurable Quick Track reapply interval** (seconds, default 900 — the same as the previous
  fixed 15 minutes).
- A dedicated Windows Options page (Options → Plugins → Perihelion) for both settings above,
  alongside the existing port/API toggle.

## [1.0.0.0] — 2026-09-07

First public release, shipping two independent front ends on one shared tracking core.

### Added

- **Custom tracking rate**, computed live from current orbital elements and corrected for
  light-time, stellar aberration, and the observer's own site (true topocentric parallax,
  not Earth's center) rather than a naive instantaneous-position snapshot.
- **Guider coordination** — shifts PHD2's lock position to match the deliberate drift instead
  of fighting it, starting guiding itself if needed, and unparks the mount first if it's parked.
- **Quick Track** — sets the rate right now, independent of any sequence, with an optional
  15-minute auto re-apply and a live status readout (applied rate, last-applied time,
  next-reapply countdown, and separate tracking/guiding error lines).
- **Add to Sequence** — builds an Advanced Sequencer container (unpark → slew/center →
  track → guide → imaging loop, with optional meridian-flip and autofocus triggers) and loads
  it for review without auto-starting. Also downloadable as a JSON file directly.
- **Meridian safety cutoff** for Quick Track, which has no sequence/trigger infrastructure of
  its own — polls NINA's own `TimeToMeridianFlip` and stops tracking cleanly at the configured
  limit rather than letting a GEM track straight through it.
- **Observed brightness from COBS**, cross-checked against the predicted (H/G model)
  magnitude, disk-cached per comet so a cold restart doesn't re-fetch the whole list live.
- **Offline-first comet/asteroid data** — MPC elements and COBS brightness both cached to disk,
  not just in memory.
- **Native Windows NINA plugin** (`Perihelion.Windows`) — a dockable panel in NINA's own
  Imaging tab (Browse, Position & Path, Track, driven natively instead of over HTTP) plus a
  popup **Framing Composer**: a sky map with six image sources (five live photographic ones,
  plus NINA's own Offline Sky Map and Cache, both fully offline-capable once a field's imagery
  has been fetched once), pan/zoom, rotation, "Determine Rotation from Camera" (a plate-solve
  reading), and offset capture for framing off-center on a comet's tail rather than its nucleus.
- **Touch-N-Stars panel** (PINS) — Browse, Position & Path, and Track tabs talking to
  Perihelion's own standalone HTTP server, independent of `ninaAPI`.

### Fixed

- A hard-to-diagnose bug where Offline Sky Map rendered correctly exactly once per NINA
  process and then silently failed on every subsequent attempt, regardless of target —
  constructing a fresh `SkyMapAnnotator` per Composer window turned out to be unsafe to do more
  than once per process; it's now a single shared instance owned by the dockable panel for the
  whole session, matching how Windows NINA's own Framing Assistant does it.
- Parked-mount handling: both the tracking-rate and guider-shift-only paths now unpark the
  mount themselves before attempting to slew or track, rather than silently doing nothing.

## [Unreleased]

- Official listing in NINA's own in-app Plugin Manager — not yet submitted; until then, the
  Windows build is a manual install from the GitHub release only.
- Touch-N-Stars: the combined "All" filter's Sync Now syncing both comets and asteroids together
  (currently comets only there), and a contextual suggestion when the connected mount's own
  driver name looks EQMOD-driven — both written, sitting in open PRs against
  `Touch-N-Stars/Touch-N-Stars`, not yet merged.
