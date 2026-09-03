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
    /// -- matches NINA Orbitals' own ShiftTrackingRate convention (SetGuiderShiftRate /
    /// SetTelescopeShiftRate combine RA/Dec via sqrt(RA² + Dec²) directly, which only makes
    /// sense if RA is already linear). By a unit coincidence (3600 arcsec/deg ÷ 3600 sec/hour
    /// = 1), these arcsec/sec values are numerically identical to degrees/hour, so they plug
    /// directly into NINA.Astrometry.SiderealShiftTrackingRate.Create(raDegPerHour, decDegPerHour)
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

        /// <summary>Comet-only: the most recent real COBS-reported magnitude, and the mean of
        /// the last up-to-5 reports (see CometActivity's own doc comment for why an average).
        /// Both null for an asteroid, or a comet COBS has no reports for. Shown alongside
        /// Magnitude in the Browse list specifically because the predicted (H/G model) value can
        /// be badly wrong during a real outburst -- 10P/Tempel and 220P/McNaught are verified
        /// real cases several magnitudes off -- and that's invisible unless the real observed
        /// value is right there next to it, not one tap away on a detail view.</summary>
        public double? ObservedMagnitude { get; init; }
        public double? ObservedAverageMagnitude { get; init; }

        public required double RaHours { get; init; }
        public required double DecDeg { get; init; }

        /// <summary>Heliocentric distance (AU) -- the object's own already-computed heliocentric
        /// vector's magnitude, so this is free alongside RA/Dec/Magnitude above.</summary>
        public required double SunDistanceAu { get; init; }

        /// <summary>Geocentric distance (AU) -- same free-from-the-existing-vector reasoning as
        /// SunDistanceAu.</summary>
        public required double EarthDistanceAu { get; init; }

        /// <summary>Angular separation from the Sun as seen from Earth (degrees) -- how close to
        /// the Sun's glare the object currently sits, which real observed-brightness readouts
        /// like TheSkyLive show alongside distance for exactly this reason.</summary>
        public required double SolarElongationDeg { get; init; }
    }

    /// <summary>
    /// Entry point: given a comet or (bright, catalogued) asteroid by name, computes its current
    /// on-sky tracking rate. Ported from OryxAstro's server/utils/orbitalTracking.ts.
    /// </summary>
    public static class OrbitalTracking {
        // Matches OryxAstro's own COMET_MAGNITUDE_THRESHOLD (cometOrbits.ts) -- "reachable with
        // a typical astrophotography setup", not a hard physical limit.
        private const double CometMagnitudeThreshold = 16;

        // Keeps ListBrowseObjectsAsync's response bounded -- the live MPC feed has thousands of
        // rows; nobody's picking a tracking target from more than this many candidates anyway.
        private const int MaxComets = 30;
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
        /// The object's real apparent position -- light-time corrected (the direction light
        /// actually left the object from, not its instantaneous "right now" position) and, when
        /// an observer site is given, from that real site rather than Earth's center
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
        /// difference -- matches Orbitals' own MaxExposureSeconds formula exactly
        /// (pixelScale / sqrt(RA² + Dec²)) so the two numbers mean the same thing. Returns null
        /// if <paramref name="name"/> isn't found (comet not in the current MPC feed, or
        /// asteroid not in AsteroidOrbits.BrightAsteroids).
        /// </summary>
        /// <param name="atDateUtc">Must have DateTime.Kind == Utc.</param>
        private static async Task<Func<DateTime, EclipticVector>?> ResolveHeliocentricAtAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, CancellationToken ct) {
            if (objectType == OrbitalObjectType.Comet) {
                var comet = await CometOrbits.FindByNameAsync(httpClient, name, ct).ConfigureAwait(false);
                if (comet == null) return null;
                return d => CometOrbits.HeliocentricEcliptic(comet, d);
            } else {
                var asteroid = AsteroidOrbits.FindByName(name);
                if (asteroid == null) return null;
                return d => AsteroidOrbits.HeliocentricEcliptic(asteroid, new AstroTime(d));
            }
        }

        /// <param name="observer">
        /// The real observer site (lat/lon/elevation) -- ApparentPosition always applies
        /// light-time and aberration correction regardless (neither needs a specific site,
        /// only Earth's own position/velocity), but the topocentric parallax piece specifically
        /// needs a real site to correct FROM. Null skips just that piece -- still strictly more
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
        /// The object's current real apparent position (see ApparentPosition's own doc comment)
        /// -- backs the live coordinate-refresh loop in SetPerihelionTrackingRate, which keeps a
        /// sequence's GoTo target current rather than frozen at whatever it was when the
        /// sequence was built. Null if the object isn't found.
        /// </summary>
        public static async Task<(double raHours, double decDeg)?> ComputeApparentPositionAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, DateTime atDateUtc, Observer? observer, CancellationToken ct = default) {
            var heliocentricAt = await ResolveHeliocentricAtAsync(httpClient, objectType, name, ct).ConfigureAwait(false);
            if (heliocentricAt == null) return null;
            return ApparentPosition(heliocentricAt, atDateUtc, observer);
        }

        /// <summary>Real apparent magnitude right now (or at any given date) -- null if the object isn't found, or is a comet with no reliable H in the current feed.</summary>
        public static async Task<double?> ComputeCurrentMagnitudeAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, DateTime atDateUtc, CancellationToken ct = default) {
            var t = new AstroTime(atDateUtc);
            if (objectType == OrbitalObjectType.Comet) {
                var comet = await CometOrbits.FindByNameAsync(httpClient, name, ct).ConfigureAwait(false);
                return comet == null ? null : CometOrbits.PredictedMagnitude(comet, atDateUtc, t);
            } else {
                var asteroid = AsteroidOrbits.FindByName(name);
                if (asteroid == null) return null;
                var helio = AsteroidOrbits.HeliocentricEcliptic(asteroid, t);
                var earth = OrbitalMechanics.EarthHeliocentricEcliptic(t);
                return AsteroidOrbits.ApparentMagnitude(asteroid, helio, earth);
            }
        }

        /// <summary>
        /// Every bright asteroid (always -- it's a small, fixed list) plus every comet in the
        /// current MPC feed bright enough to be worth showing, each with today's real
        /// magnitude/RA/Dec -- backs the Touch-N-Stars panel's Browse tab.
        /// </summary>
        /// <param name="forceRefreshCobs">Bypasses CometActivity's own 2h TTL for every comet in
        /// the list -- the explicit "Refresh COBS" action, separate from the passive default
        /// where a cold/disk-loaded cache is good enough. Deliberately not tied to the comet
        /// elements sync (Sync Now): a full COBS refresh across every listed comet costs the same
        /// several-seconds-to-tens-of-seconds round-trip that disk-persisting the cache exists to
        /// keep off the normal load path, so it stays a separate, deliberate action.</param>
        /// <param name="includeCobs">Real hardware feedback (2026-09-03): even with a warm cache,
        /// waiting on COBS at all before the list can render was still felt as "the page is slow"
        /// -- a cold cache (first run, or a comet's own 2h TTL lapsing) made it much worse (14-16s
        /// measured on real hardware). Default false: /objects returns comets/asteroids with only
        /// their predicted magnitude, instantly, and the panel fills in real observed-brightness
        /// badges afterward via a background per-comet GET /objects/activity sweep (see
        /// fetchBrowseObjects.js's own comment) -- COBS never blocks the initial render again.
        /// True only for the explicit "Refresh COBS" action (POST /objects/refresh-cobs), where
        /// blocking IS the point -- an explicit refresh should report success/failure for real.</param>
        public static async Task<IReadOnlyList<BrowseObject>> ListBrowseObjectsAsync(HttpClient httpClient, DateTime atDateUtc, CancellationToken ct = default, bool includeCobs = false, bool forceRefreshCobs = false) {
            var overallStopwatch = Stopwatch.StartNew();
            var t = new AstroTime(atDateUtc);
            var earth = OrbitalMechanics.EarthHeliocentricEcliptic(t);
            var results = new List<BrowseObject>();

            foreach (var asteroid in AsteroidOrbits.BrightAsteroids) {
                var helio = AsteroidOrbits.HeliocentricEcliptic(asteroid, t);
                var geo = helio - earth;
                results.Add(new BrowseObject {
                    Id = asteroid.Id,
                    Name = asteroid.Name,
                    ObjectType = OrbitalObjectType.Asteroid,
                    Magnitude = AsteroidOrbits.ApparentMagnitude(asteroid, helio, earth),
                    RaHours = OrbitalMechanics.GeocentricRightAscensionHours(geo, t),
                    DecDeg = OrbitalMechanics.GeocentricDeclinationDeg(geo, t),
                    SunDistanceAu = helio.Length(),
                    EarthDistanceAu = geo.Length(),
                    SolarElongationDeg = SolarElongationDeg(earth, geo),
                });
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
                // Some real MPC feed entries share the same display Name (e.g. distinct fragments
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
                    cometResults.Add(new BrowseObject {
                        Id = comet.Designation,
                        Name = comet.Name,
                        ObjectType = OrbitalObjectType.Comet,
                        Magnitude = mag,
                        RaHours = OrbitalMechanics.GeocentricRightAscensionHours(geo, t),
                        DecDeg = OrbitalMechanics.GeocentricDeclinationDeg(geo, t),
                        SunDistanceAu = helio.Length(),
                        EarthDistanceAu = geo.Length(),
                        SolarElongationDeg = SolarElongationDeg(earth, geo),
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
                    // Real observed brightness for each comet, fetched in parallel (capped
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

            // The asteroid loop above adds entries in BrightAsteroids' own hardcoded order, not
            // by brightness -- only cometResults got sorted, so without this the combined list
            // is really "asteroids in list-definition order, then comets sorted", not a single
            // brightest-first ranking across both (the sort must run after both halves are in
            // one list, or the asteroid half never gets touched at all).
            results.Sort((a, b) => Nullable.Compare(a.Magnitude, b.Magnitude));

            return results;
        }

        /// <summary>
        /// One position per day for <paramref name="days"/> days starting at
        /// <paramref name="fromDateUtc"/> -- the object's real path against the fixed star
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
