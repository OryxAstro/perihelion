using System;
using System.Collections.Generic;
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

        public required double RaHours { get; init; }
        public required double DecDeg { get; init; }
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
        private static (double raHours, double decDeg) GeocentricPosition(Func<DateTime, EclipticVector> heliocentricAt, DateTime date) {
            var t = new AstroTime(date);
            var helio = heliocentricAt(date);
            var earth = OrbitalMechanics.EarthHeliocentricEcliptic(t);
            var geo = helio - earth;
            return (OrbitalMechanics.GeocentricRightAscensionHours(geo, t), OrbitalMechanics.GeocentricDeclinationDeg(geo, t));
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

        public static async Task<OrbitalRate?> ComputeOrbitalRateAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, DateTime atDateUtc, CancellationToken ct = default) {
            var heliocentricAt = await ResolveHeliocentricAtAsync(httpClient, objectType, name, ct).ConfigureAwait(false);
            if (heliocentricAt == null) return null;

            const int dtSec = 60;
            var p1 = GeocentricPosition(heliocentricAt, atDateUtc);
            var p2 = GeocentricPosition(heliocentricAt, atDateUtc.AddSeconds(dtSec));

            var dRaDeg = (p2.raHours - p1.raHours) * 15;
            if (dRaDeg > 180) dRaDeg -= 360;
            if (dRaDeg < -180) dRaDeg += 360;
            var decRad = p1.decDeg * OrbitalMechanics.Deg2Rad;

            return new OrbitalRate(
                raArcsecPerSec: dRaDeg * Math.Cos(decRad) * 3600 / dtSec,
                decArcsecPerSec: (p2.decDeg - p1.decDeg) * 3600 / dtSec
            );
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
        public static async Task<IReadOnlyList<BrowseObject>> ListBrowseObjectsAsync(HttpClient httpClient, DateTime atDateUtc, CancellationToken ct = default) {
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
                });
            }

            var comets = await CometOrbits.FetchCometElementsAsync(httpClient, ct).ConfigureAwait(false);
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
                });
            }
            cometResults.Sort((a, b) => Nullable.Compare(a.Magnitude, b.Magnitude));
            results.AddRange(cometResults.GetRange(0, Math.Min(MaxComets, cometResults.Count)));

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
