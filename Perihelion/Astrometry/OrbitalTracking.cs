using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CosineKitty;

namespace Perihelion.Astrometry {

    public enum OrbitalObjectType {
        Comet,
        Asteroid,
    }

    /// <summary>
    /// True on-sky linear rate: RA is already cos(dec)-compensated (ΔRA·cos(dec), not raw ΔRA)
    /// -- needed because NINA's own shift-rate mediator calls combine RA/Dec via
    /// sqrt(RA² + Dec²) directly (SetTelescopeShiftRate/SetGuiderShiftRate on both sequence
    /// items here), which only makes sense if RA is already linear. By a unit coincidence
    /// (3600 arcsec/deg ÷ 3600 sec/hour = 1), these arcsec/sec values are numerically identical
    /// to degrees/hour, so they plug directly into
    /// NINA.Astrometry.SiderealShiftTrackingRate.Create(raDegPerHour, decDegPerHour)
    /// with no conversion.
    /// </summary>
    public readonly struct OrbitalRate {
        public readonly double RaArcsecPerSec;
        public readonly double DecArcsecPerSec;

        public OrbitalRate(double raArcsecPerSec, double decArcsecPerSec) {
            RaArcsecPerSec = raArcsecPerSec;
            DecArcsecPerSec = decArcsecPerSec;
        }
    }

    /// <summary>
    /// One object's current on-sky position, for the Touch-N-Stars panel's Browse tab -- the
    /// panel is a thin client of these already-computed values rather than a second
    /// implementation of the orbital math in JavaScript (both the panel and this plugin run on
    /// the same Pi, so there's no "avoid an internet round-trip" reason to duplicate it the way
    /// there was for avoiding a call to OryxAstro's own website API).
    /// </summary>
    public sealed class BrowseObject {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required OrbitalObjectType ObjectType { get; init; }

        /// <summary>Null for a comet with no reliable H in the current MPC feed.</summary>
        public double? Magnitude { get; init; }

        /// <summary>Comet-only: the most recent COBS-reported magnitude, and the mean of
        /// the last up-to-5 reports (see CometActivity's own doc comment for why an average).
        /// Both null for an asteroid, or a comet COBS has no reports for. Shown alongside
        /// Magnitude in the Browse list specifically because the predicted (H/G model) value can
        /// be badly wrong during an outburst -- 10P/Tempel and 220P/McNaught are verified
        /// cases several magnitudes off -- and that's invisible unless the observed
        /// value is right there next to it, not one tap away on a detail view.</summary>
        // Settable, not init-only: the native Windows panel populates the list instantly without
        // COBS (same reasoning as includeCobs's own doc comment below -- don't block the initial
        // render), then fills these in per-comet in the background as each one's own COBS fetch
        // completes, same pattern as the Touch-N-Stars panel's own fetchBrowseObjects.js.
        public double? ObservedMagnitude { get; set; }
        public double? ObservedAverageMagnitude { get; set; }

        public required double RaHours { get; init; }
        public required double DecDeg { get; init; }

        /// <summary>Heliocentric distance (AU) -- the object's own already-computed heliocentric
        /// vector's magnitude, so this is free alongside RA/Dec/Magnitude above.</summary>
        public required double SunDistanceAu { get; init; }

        /// <summary>Geocentric distance (AU) -- same free-from-the-existing-vector reasoning as
        /// SunDistanceAu.</summary>
        public required double EarthDistanceAu { get; init; }

        /// <summary>Angular separation from the Sun as seen from Earth (degrees) -- how close to
        /// the Sun's glare the object currently sits, which observed-brightness readouts
        /// like TheSkyLive show alongside distance for exactly this reason.</summary>
        public required double SolarElongationDeg { get; init; }

        /// <summary>IAU constellation the object's current position falls in, e.g. "Orion" --
        /// via AstronomyEngine's own Astronomy.Constellation(raHours, decDeg), already bundled
        /// with the NuGet package Perihelion already depends on, so this needed no new data or
        /// dependency.</summary>
        public required string ConstellationName { get; init; }

        /// <summary>Comet-only -- when this comet last (or will next) reach perihelion, straight
        /// from the MPC feed's own T (time of perihelion passage), already parsed into
        /// CometElements.PerihelionDate but previously unused past feeding the orbit solver
        /// itself. Null for an asteroid (parameterized by Mean Anomaly at Epoch instead, no
        /// direct equivalent field).</summary>
        public DateTime? PerihelionDateUtc { get; init; }

        /// <summary>Days between "now" and this object's own reference epoch (a comet's
        /// perihelion passage time T, or an asteroid's stored EpochJd) -- see CometOrbits/
        /// AsteroidOrbits' own EpochAgeDays for why this matters: a large value means positions
        /// computed here rely on pure two-body propagation over a long span with no perturbation
        /// modeling.</summary>
        public required double EpochAgeDays { get; init; }

        /// <summary>True when EpochAgeDays exceeds the type-specific staleness threshold --
        /// worth surfacing to the user as "this object's data may be less accurate than usual",
        /// not a hard error.</summary>
        public required bool IsEpochStale { get; init; }
    }

    /// <summary>
    /// Entry point: given a comet or (bright, catalogued) asteroid by name, computes its current
    /// on-sky tracking rate. Ported from OryxAstro's server/utils/orbitalTracking.ts.
    /// </summary>
    public static class OrbitalTracking {
        // Originally a hardcoded 16/30 (matching OryxAstro's own COMET_MAGNITUDE_THRESHOLD,
        // cometOrbits.ts -- "reachable with a typical astrophotography setup", not a hard
        // physical limit) -- now PerihelionPlugin settings (see its own doc comments for
        // the full reasoning), read fresh on every call rather than cached, same as every other
        // configurable setting elsewhere in this plugin. These two static properties keep every
        // call site below unchanged in shape, just no longer a compile-time constant.
        private static double CometMagnitudeThreshold => PerihelionPlugin.Instance?.CometMagnitudeThreshold ?? 16.0;
        private static int MaxComets => PerihelionPlugin.Instance?.MaxComets ?? 30;
        private static int MaxAsteroids => PerihelionPlugin.Instance?.MaxAsteroids ?? 30;
        /// <summary>Angular separation from the Sun as seen from Earth -- the angle at Earth
        /// between the Sun-Earth line and the Earth-object line. Sun-Earth = -earth (Earth's own
        /// heliocentric vector, negated); Earth-object = geo (already the geocentric vector every
        /// caller here has on hand).</summary>
        private static double SolarElongationDeg(EclipticVector earth, EclipticVector geo) {
            var cosElongation = -earth.Dot(geo) / (earth.Length() * geo.Length());
            // Clamp against floating-point overshoot past +/-1 (would otherwise make Acos return
            // NaN for a genuinely-0-or-180-degree elongation).
            cosElongation = Math.Max(-1.0, Math.Min(1.0, cosElongation));
            return Math.Acos(cosElongation) * OrbitalMechanics.Rad2Deg;
        }

        private static (double raHours, double decDeg) GeocentricPosition(Func<DateTime, EclipticVector> heliocentricAt, DateTime date) {
            var t = new AstroTime(date);
            var helio = heliocentricAt(date);
            var earth = OrbitalMechanics.EarthHeliocentricEcliptic(t);
            var geo = helio - earth;
            return (OrbitalMechanics.GeocentricRightAscensionHours(geo, t), OrbitalMechanics.GeocentricDeclinationDeg(geo, t));
        }

        // Exact: 299792.458 km/s * 86400 s/day / 149597870.7 km/AU.
        private const double AuPerDaySpeedOfLight = 173.14463267424031;

        /// <summary>
        /// The object's apparent position -- light-time corrected (the direction light
        /// actually left the object from, not its instantaneous "right now" position) and, when
        /// an observer site is given, from that site rather than Earth's center
        /// (topocentric parallax) and corrected for the observer's own velocity (classical
        /// stellar aberration, first order in v/c -- plenty accurate given v/c ~ 1e-4 for any
        /// observer on or near Earth). This is what actually drives a mount and what a live
        /// re-centered sequence target should show; GeocentricPosition above (no light-time, no
        /// observer, no aberration) stays in use for the browse list and the multi-night finder
        /// chart, where arcsecond-scale rigor buys nothing over a simple geometric position.
        /// </summary>
        private static (double raHours, double decDeg) ApparentPosition(Func<DateTime, EclipticVector> heliocentricAt, DateTime atDateUtc, Observer? observer) {
            var t = new AstroTime(atDateUtc);
            var observerState = OrbitalMechanics.ObserverHeliocentricState(t, observer);

            // Light-time: solve for the retarded emission time by fixed-point iteration. This
            // converges fast (light-time here is minutes; the object's own position barely
            // changes across that span relative to the AU-scale distances involved), so a fixed
            // 3 iterations is comfortably enough rather than needing a convergence check.
            var lightTimeDays = 0.0;
            var targetHelio = heliocentricAt(atDateUtc);
            for (var i = 0; i < 3; i++) {
                targetHelio = heliocentricAt(atDateUtc.AddDays(-lightTimeDays));
                var dx = targetHelio.X - observerState.Position.X;
                var dy = targetHelio.Y - observerState.Position.Y;
                var dz = targetHelio.Z - observerState.Position.Z;
                var dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                lightTimeDays = dist / AuPerDaySpeedOfLight;
            }

            var dirX = targetHelio.X - observerState.Position.X;
            var dirY = targetHelio.Y - observerState.Position.Y;
            var dirZ = targetHelio.Z - observerState.Position.Z;
            var dirLen = Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
            var ux = dirX / dirLen;
            var uy = dirY / dirLen;
            var uz = dirZ / dirLen;

            // Classical stellar aberration to first order in beta = v/c: u' = u + beta -
            // (beta.u)u -- the same effect that makes stars trace a small annual ellipse,
            // applied here to the observer's full velocity (Earth's orbital motion, plus the
            // site's own rotational velocity when an observer is given).
            var bx = observerState.Velocity.X / AuPerDaySpeedOfLight;
            var by = observerState.Velocity.Y / AuPerDaySpeedOfLight;
            var bz = observerState.Velocity.Z / AuPerDaySpeedOfLight;
            var dot = ux * bx + uy * by + uz * bz;
            var apparent = new EclipticVector(ux + bx - dot * ux, uy + by - dot * uy, uz + bz - dot * uz);

            return (
                OrbitalMechanics.GeocentricRightAscensionHours(apparent, t),
                OrbitalMechanics.GeocentricDeclinationDeg(apparent, t)
            );
        }

        /// <summary>
        /// Instantaneous angular rate at <paramref name="atDateUtc"/>, via a 60-second finite
        /// difference. Max exposure is derived from this as pixelScale / sqrt(RA² + Dec²).
        /// Returns null
        /// if <paramref name="name"/> isn't found (comet not in the current MPC feed, or
        /// asteroid not in Perihelion's own curated asteroid list).
        /// </summary>
        /// <param name="atDateUtc">Must have DateTime.Kind == Utc.</param>
        private static async Task<Func<DateTime, EclipticVector>?> ResolveHeliocentricAtAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, CancellationToken ct) {
            if (objectType == OrbitalObjectType.Comet) {
                var comet = await CometOrbits.FindByNameAsync(httpClient, name, ct).ConfigureAwait(false);
                if (comet == null) return null;
                return d => CometOrbits.HeliocentricEcliptic(comet, d);
            } else {
                var asteroid = await AsteroidOrbits.FindByNameAsync(httpClient, name, ct).ConfigureAwait(false);
                if (asteroid == null) return null;
                return d => AsteroidOrbits.HeliocentricEcliptic(asteroid, new AstroTime(d));
            }
        }

        /// <param name="observer">
        /// The observer site (lat/lon/elevation) -- ApparentPosition always applies
        /// light-time and aberration correction regardless (neither needs a specific site,
        /// only Earth's own position/velocity), but the topocentric parallax piece specifically
        /// needs a site to correct FROM. Null skips just that piece -- still strictly more
        /// accurate than the old plain-geocentric calculation, just without the site-specific
        /// correction on top.
        /// </param>
        public static async Task<OrbitalRate?> ComputeOrbitalRateAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, DateTime atDateUtc, CancellationToken ct = default, Observer? observer = null) {
            var heliocentricAt = await ResolveHeliocentricAtAsync(httpClient, objectType, name, ct).ConfigureAwait(false);
            if (heliocentricAt == null) return null;

            const int dtSec = 60;
            var p1 = ApparentPosition(heliocentricAt, atDateUtc, observer);
            var p2 = ApparentPosition(heliocentricAt, atDateUtc.AddSeconds(dtSec), observer);

            var dRaDeg = (p2.raHours - p1.raHours) * 15;
            if (dRaDeg > 180) dRaDeg -= 360;
            if (dRaDeg < -180) dRaDeg += 360;
            var decRad = p1.decDeg * OrbitalMechanics.Deg2Rad;

            return new OrbitalRate(
                raArcsecPerSec: dRaDeg * Math.Cos(decRad) * 3600 / dtSec,
                decArcsecPerSec: (p2.decDeg - p1.decDeg) * 3600 / dtSec
            );
        }

        /// <summary>
        /// The object's current apparent position (see ApparentPosition's own doc comment)
        /// -- backs the live coordinate-refresh loop in SetPerihelionTrackingRate, which keeps a
        /// sequence's GoTo target current rather than frozen at whatever it was when the
        /// sequence was built. Null if the object isn't found.
        /// </summary>
        public static async Task<(double raHours, double decDeg)?> ComputeApparentPositionAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, DateTime atDateUtc, Observer? observer, CancellationToken ct = default) {
            var heliocentricAt = await ResolveHeliocentricAtAsync(httpClient, objectType, name, ct).ConfigureAwait(false);
            if (heliocentricAt == null) return null;
            return ApparentPosition(heliocentricAt, atDateUtc, observer);
        }

        /// <summary>apparent magnitude right now (or at any given date) -- null if the object isn't found, or is a comet with no reliable H in the current feed.</summary>
        public static async Task<double?> ComputeCurrentMagnitudeAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, DateTime atDateUtc, CancellationToken ct = default) {
            var t = new AstroTime(atDateUtc);
            if (objectType == OrbitalObjectType.Comet) {
                var comet = await CometOrbits.FindByNameAsync(httpClient, name, ct).ConfigureAwait(false);
                return comet == null ? null : CometOrbits.PredictedMagnitude(comet, atDateUtc, t);
            } else {
                var asteroid = await AsteroidOrbits.FindByNameAsync(httpClient, name, ct).ConfigureAwait(false);
                if (asteroid == null) return null;
                var helio = AsteroidOrbits.HeliocentricEcliptic(asteroid, t);
                var earth = OrbitalMechanics.EarthHeliocentricEcliptic(t);
                return AsteroidOrbits.ApparentMagnitude(asteroid, helio, earth);
            }
        }

        /// <summary>
        /// Every bright asteroid (always -- it's a small, fixed list) plus every comet in the
        /// current MPC feed bright enough to be worth showing, each with today's
        /// magnitude/RA/Dec -- backs the Touch-N-Stars panel's Browse tab.
        /// </summary>
        /// <param name="forceRefreshCobs">Bypasses CometActivity's own 2h TTL for every comet in
        /// the list -- the explicit "Refresh COBS" action, separate from the passive default
        /// where a cold/disk-loaded cache is good enough. Deliberately not tied to the comet
        /// elements sync (Sync Now): a full COBS refresh across every listed comet costs the same
        /// several-seconds-to-tens-of-seconds round-trip that disk-persisting the cache exists to
        /// keep off the normal load path, so it stays a separate, deliberate action.</param>
        /// <param name="includeCobs">Waiting on COBS at all before the list can render is
        /// noticeably slow, worse on a cold cache (first run, or a comet's own 2h TTL lapsing).
        /// Default false: /objects returns comets/asteroids with only their predicted magnitude,
        /// instantly, and the panel fills in observed-brightness badges afterward via a
        /// background per-comet GET /objects/activity sweep (see fetchBrowseObjects.js's own
        /// comment) -- COBS never blocks the initial render.
        /// True only for the explicit "Refresh COBS" action (POST /objects/refresh-cobs), where
        /// blocking IS the point -- an explicit refresh should report success/failure for.</param>
        public static async Task<IReadOnlyList<BrowseObject>> ListBrowseObjectsAsync(HttpClient httpClient, DateTime atDateUtc, CancellationToken ct = default, bool includeCobs = false, bool forceRefreshCobs = false) {
            var overallStopwatch = Stopwatch.StartNew();
            var t = new AstroTime(atDateUtc);
            var earth = OrbitalMechanics.EarthHeliocentricEcliptic(t);
            var results = new List<BrowseObject>();

            // Isolated the same way the comet fetch below is -- a JPL outage shouldn't blank the
            // whole Browse tab when FetchAsteroidElementsAsync still has nothing to fall back on
            // (never synced on this install, and the live fetch also failed).
            try {
                var asteroids = await AsteroidOrbits.FetchAsteroidElementsAsync(httpClient, ct).ConfigureAwait(false);
                // Computed for every fetched candidate, then sorted/capped by the REAL result --
                // not by H first. H alone is a poor stand-in for "worth showing here": the
                // candidate pool at any useful threshold includes distant dwarf planets/TNOs
                // (Pluto, Eris, Makemake...) whose low H reflects sheer size, not current
                // brightness -- capping on H before computing position would let those crowd out
                // much fainter-by-H but far brighter-right-now main-belt asteroids. Pure math, no
                // I/O, so processing the whole pool (thousands of objects even at a generous
                // threshold) before capping costs single-digit milliseconds, not a concern.
                var asteroidResults = new List<BrowseObject>(asteroids.Count);
                foreach (var asteroid in asteroids) {
                    var helio = AsteroidOrbits.HeliocentricEcliptic(asteroid, t);
                    var geo = helio - earth;
                    var raHours = OrbitalMechanics.GeocentricRightAscensionHours(geo, t);
                    var decDeg = OrbitalMechanics.GeocentricDeclinationDeg(geo, t);
                    asteroidResults.Add(new BrowseObject {
                        Id = asteroid.Id,
                        Name = asteroid.Name,
                        ObjectType = OrbitalObjectType.Asteroid,
                        Magnitude = AsteroidOrbits.ApparentMagnitude(asteroid, helio, earth),
                        RaHours = raHours,
                        DecDeg = decDeg,
                        SunDistanceAu = helio.Length(),
                        EarthDistanceAu = geo.Length(),
                        SolarElongationDeg = SolarElongationDeg(earth, geo),
                        ConstellationName = Astronomy.Constellation(raHours, decDeg).Name,
                        EpochAgeDays = AsteroidOrbits.EpochAgeDays(asteroid, atDateUtc),
                        IsEpochStale = AsteroidOrbits.IsEpochStale(asteroid, atDateUtc),
                    });
                }
                asteroidResults.Sort((a, b) => Nullable.Compare(a.Magnitude, b.Magnitude));
                results.AddRange(asteroidResults.Count > MaxAsteroids ? asteroidResults.GetRange(0, MaxAsteroids) : asteroidResults);
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: could not list asteroids, continuing with comets only: {ex.Message}");
            }

            // Isolated from the asteroid loop above on purpose -- FetchCometElementsAsync only
            // throws when there's truly no comet data anywhere (never synced, live fetch also
            // failed); that shouldn't take the already-built, fully offline asteroid list down
            // with it. A comet-less Browse tab beats an empty one.
            try {
                var elementsStopwatch = Stopwatch.StartNew();
                var comets = await CometOrbits.FetchCometElementsAsync(httpClient, ct).ConfigureAwait(false);
                elementsStopwatch.Stop();
                var cometResults = new List<BrowseObject>();
                // Some MPC feed entries share the same display Name (e.g. distinct fragments
                // of a split comet) -- FindByNameAsync/tracking match by Name via FirstOrDefault, so
                // a later duplicate is functionally indistinguishable from the first for tracking
                // purposes anyway (both resolve to the same match). Skip it here rather than show
                // two list rows that would behave identically if either were tracked.
                var seenNames = new HashSet<string>();
                foreach (var comet in comets) {
                    if (!seenNames.Add(comet.Name)) continue;

                    var mag = CometOrbits.PredictedMagnitude(comet, atDateUtc, t);
                    if (mag == null || mag > CometMagnitudeThreshold) continue;

                    var helio = CometOrbits.HeliocentricEcliptic(comet, atDateUtc);
                    var geo = helio - earth;
                    var raHours = OrbitalMechanics.GeocentricRightAscensionHours(geo, t);
                    var decDeg = OrbitalMechanics.GeocentricDeclinationDeg(geo, t);
                    cometResults.Add(new BrowseObject {
                        Id = comet.Designation,
                        Name = comet.Name,
                        ObjectType = OrbitalObjectType.Comet,
                        Magnitude = mag,
                        RaHours = raHours,
                        DecDeg = decDeg,
                        SunDistanceAu = helio.Length(),
                        EarthDistanceAu = geo.Length(),
                        SolarElongationDeg = SolarElongationDeg(earth, geo),
                        ConstellationName = Astronomy.Constellation(raHours, decDeg).Name,
                        PerihelionDateUtc = comet.PerihelionDate,
                        EpochAgeDays = CometOrbits.EpochAgeDays(comet, atDateUtc),
                        IsEpochStale = CometOrbits.IsEpochStale(comet, atDateUtc),
                    });
                }
                cometResults.Sort((a, b) => Nullable.Compare(a.Magnitude, b.Magnitude));
                var trimmedComets = cometResults.GetRange(0, Math.Min(MaxComets, cometResults.Count));

                if (!includeCobs) {
                    // trimmedComets already have ObservedMagnitude/ObservedAverageMagnitude null
                    // (never set above) -- exactly the "predicted only, COBS fills in later"
                    // shape the fast path needs, no separate object construction required.
                    results.AddRange(trimmedComets);
                    NINA.Core.Utility.Logger.Info($"Perihelion: ListBrowseObjectsAsync timing (COBS excluded) -- comet elements: {elementsStopwatch.ElapsedMilliseconds}ms, total: {overallStopwatch.ElapsedMilliseconds}ms");
                } else {
                    // Observed brightness for each comet, fetched in parallel (capped
                    // concurrency, to stay a reasonable citizen of a third-party public API)
                    // rather than sequentially. Only reached for the explicit "Refresh COBS"
                    // action now (includeCobs defaults false) -- see this method's own
                    // includeCobs doc comment for why the normal /objects path no longer takes
                    // this branch at all.
                    using var cobsThrottle = new SemaphoreSlim(6);
                    var cobsStopwatch = Stopwatch.StartNew();
                    var cometsWithActivity = await Task.WhenAll(trimmedComets.Select(async comet => {
                        await cobsThrottle.WaitAsync(ct).ConfigureAwait(false);
                        try {
                            var activity = await CometActivity.FetchAsync(httpClient, comet.Name, ct, forceRefresh: forceRefreshCobs).ConfigureAwait(false);
                            return new BrowseObject {
                                Id = comet.Id,
                                Name = comet.Name,
                                ObjectType = comet.ObjectType,
                                Magnitude = comet.Magnitude,
                                ObservedMagnitude = activity?.MostRecent.Magnitude,
                                ObservedAverageMagnitude = activity?.RecentAverageMagnitude,
                                RaHours = comet.RaHours,
                                DecDeg = comet.DecDeg,
                                SunDistanceAu = comet.SunDistanceAu,
                                EarthDistanceAu = comet.EarthDistanceAu,
                                SolarElongationDeg = comet.SolarElongationDeg,
                                ConstellationName = comet.ConstellationName,
                                PerihelionDateUtc = comet.PerihelionDateUtc,
                                EpochAgeDays = comet.EpochAgeDays,
                                IsEpochStale = comet.IsEpochStale,
                            };
                        } finally {
                            cobsThrottle.Release();
                        }
                    })).ConfigureAwait(false);
                    cobsStopwatch.Stop();
                    results.AddRange(cometsWithActivity);
                    if (forceRefreshCobs) await CometActivity.MarkFullRefreshCompleteAsync(ct).ConfigureAwait(false);
                    NINA.Core.Utility.Logger.Info($"Perihelion: ListBrowseObjectsAsync timing -- comet elements: {elementsStopwatch.ElapsedMilliseconds}ms, COBS ({trimmedComets.Count} comets, forceRefresh={forceRefreshCobs}): {cobsStopwatch.ElapsedMilliseconds}ms, total: {overallStopwatch.ElapsedMilliseconds}ms");
                }
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: comet list unavailable, showing asteroids only: {ex.Message}");
            }

            // The asteroid loop above adds entries in the curated target list's own order, not
            // by brightness -- only cometResults got sorted, so without this the combined list
            // is really "asteroids in list-definition order, then comets sorted", not a single
            // brightest-first ranking across both (the sort must run after both halves are in
            // one list, or the asteroid half never gets touched at all).
            results.Sort((a, b) => Nullable.Compare(a.Magnitude, b.Magnitude));

            return results;
        }

        /// <summary>
        /// One position per day for <paramref name="days"/> days starting at
        /// <paramref name="fromDateUtc"/> -- the object's path against the fixed star
        /// background, for the Position &amp; Path tab's finder-chart plot. Null if the object
        /// isn't found.
        /// </summary>
        public static async Task<IReadOnlyList<(DateTime date, double raHours, double decDeg)>?> ComputeOrbitalPathAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, DateTime fromDateUtc, int days, CancellationToken ct = default) {
            var heliocentricAt = await ResolveHeliocentricAtAsync(httpClient, objectType, name, ct).ConfigureAwait(false);
            if (heliocentricAt == null) return null;

            var points = new List<(DateTime, double, double)>(days);
            for (var i = 0; i < days; i++) {
                var date = fromDateUtc.AddDays(i);
                var p = GeocentricPosition(heliocentricAt, date);
                points.Add((date, p.raHours, p.decDeg));
            }
            return points;
        }
    }
}
