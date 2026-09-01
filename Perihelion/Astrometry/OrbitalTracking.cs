using System;
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
    /// Entry point: given a comet or (bright, catalogued) asteroid by name, computes its current
    /// on-sky tracking rate. Ported from OryxAstro's server/utils/orbitalTracking.ts -- only
    /// ComputeOrbitalRateAsync itself; the magnitude/finder-chart exports in the original file
    /// aren't needed for a tracking-rate sequence item and were left out of this port.
    /// </summary>
    public static class OrbitalTracking {
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
        public static async Task<OrbitalRate?> ComputeOrbitalRateAsync(HttpClient httpClient, OrbitalObjectType objectType, string name, DateTime atDateUtc, CancellationToken ct = default) {
            Func<DateTime, EclipticVector> heliocentricAt;

            if (objectType == OrbitalObjectType.Comet) {
                var comet = await CometOrbits.FindByNameAsync(httpClient, name, ct).ConfigureAwait(false);
                if (comet == null) return null;
                heliocentricAt = d => CometOrbits.HeliocentricEcliptic(comet, d);
            } else {
                var asteroid = AsteroidOrbits.FindByName(name);
                if (asteroid == null) return null;
                heliocentricAt = d => AsteroidOrbits.HeliocentricEcliptic(asteroid, new AstroTime(d));
            }

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
    }
}
