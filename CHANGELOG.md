# Changelog

All notable changes to Perihelion are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.0.5] — 2026-09-09

### Added

- **Record counts for Asteroids and COBS**, matching the "Comets (4108)" label the Update
  Sources block already had. Asteroids always shows the same fixed 13 once synced (the curated
  list itself doesn't grow), shown anyway for the same "confirms it actually loaded" reason the
  comet count is. COBS counts comets with an actual cached observation, not every comet ever
  checked.

## [1.1.0.4] — 2026-09-09

### Removed

- **"Slew and Track" and "Set Guider Shift Rate"**, the two standalone action buttons on the
  Windows dockable panel — Touch-N-Stars ships the identical workflow (Frame, then Quick Track
  or Add to Sequence) with no equivalent of either at all: confirmed from its own source that
  Quick Track never slews on its own, and no standalone guider-shift action exists anywhere in
  it. Both buttons sat in the same button grid as Frame/Set Tracking Rate/Clear Offset, which
  made them look like a required step before Quick Track or Add to Sequence rather than the
  optional, narrower-case alternates they actually were.

## [1.1.0.3] — 2026-09-09

### Fixed

- **Removed three leftover references to "Capture Offset from Mount"** in the Framing
  Composer's own status text (both the default text and all three post-slew messages) — missed
  in 1.1.0.2's removal of that feature, since they lived in `.cs` defaults rather than the XAML
  already checked.
- **"Include guiding" now has an explanatory line**, matching what it actually does (shifts the
  guider's lock position, starts guiding if needed) rather than a bare label.
- **Quick Track's always-on meridian safety cutoff is now visible in the UI.** It was already
  active on every Quick Track session on both platforms (`QuickTrackReapply.CheckMeridian`,
  unconditional regardless of the auto-reapply setting) — just never surfaced, so it looked
  like Quick Track had no meridian protection at all when it already did.
- **Browse list's "Observed Mag (COBS)" column is now diff-colored too** — the Position tab's
  own card got this in 1.1.0.2, but the separate Browse list location was missed. Same
  thresholds, via a new `MagnitudeDiffBrushConverter` (the coloring couldn't live directly on
  `BrowseObject` itself, since that class is also compiled into the cross-platform PINS build).

## [1.1.0.2] — 2026-09-09

### Changed

- **The "Include guiding" checkbox now sits above both Add to Sequence and Quick Track**, not
  just inside Quick Track's own column — it always governed both (confirmed from
  `AddToSequenceAction`'s own call into `BuildTargetContainer`), so showing it under only one
  was misleading, the same class of scoping bug already fixed once on the Touch-N-Stars side.
- **COBS observed magnitude is now diff-colored**, matching Touch-N-Stars' own `magDiffTier`
  convention exactly (green when notably brighter than predicted, amber/red as it falls short,
  neutral when close) — previously a single flattened string with no visual cue.
- **Removed "Capture Offset from Mount"** from the Framing Composer — it converted the mount's
  position into the same offset state that dragging the sky map already produces directly, with
  no advantage for the ordinary framing workflow.

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
