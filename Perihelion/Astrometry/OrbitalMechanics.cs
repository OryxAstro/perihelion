using System;
using CosineKitty;

namespace Perihelion.Astrometry {

    /// <summary>
    /// Low-level two-body orbital mechanics primitives shared by AsteroidOrbits (elliptical
    /// Kepler solver) and CometOrbits (universal-variable solver). Ported from OryxAstro's
    /// server/utils/orbitalMechanics.ts -- only the subset OrbitalTracking.ComputeOrbitalRate
    /// actually needs; the finder-chart/opposition-search helpers in the original file were
    /// left out as out of scope for a tracking-rate plugin.
    /// </summary>
    public static class OrbitalMechanics {
        public const double Deg2Rad = Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / Math.PI;

        // Gaussian gravitational constant squared, AU^3/day^2 -- standard value for two-body
        // heliocentric mean-motion/vis-viva calculations.
        public const double GaussianKSquared = 2.959122082855911e-4;

        // J2000.0 epoch (noon UTC, 2000-01-01) is exactly JD 2451545.0. Deriving Julian Date
        // from AstroTime.ut (rather than converting a raw DateTime independently) guarantees
        // this stays consistent with AstronomyEngine's own UTC handling -- AstroTime(DateTime)
        // calls DateTime.ToUniversalTime() internally, which silently assumes local time for
        // a DateTime with Kind == Unspecified. Every DateTime handed to this namespace's APIs
        // must therefore have Kind == Utc; there is no way to catch a wrong Kind here.
        public static double JulianDate(AstroTime t) => t.ut + 2451545.0;

        public static double NormalizeRad(double angle) {
            const double twoPi = 2 * Math.PI;
            return ((angle % twoPi) + twoPi) % twoPi;
        }

        /// <summary>
        /// Rotates a perifocal-frame (x,y) position (z=0, orbital plane) into heliocentric
        /// ecliptic (mean equinox J2000) via the standard 3-1-3 (argument of perihelion,
        /// inclination, longitude of ascending node) rotation.
        /// </summary>
        public static EclipticVector RotatePerifocalToEcliptic(double xOrbit, double yOrbit, double inclinationDeg, double nodeDeg, double argPeriDeg) {
            var incl = inclinationDeg * Deg2Rad;
            var node = nodeDeg * Deg2Rad;
            var argPeri = argPeriDeg * Deg2Rad;

            var cosNode = Math.Cos(node);
            var sinNode = Math.Sin(node);
            var cosArgPeri = Math.Cos(argPeri);
            var sinArgPeri = Math.Sin(argPeri);
            var cosIncl = Math.Cos(incl);
            var sinIncl = Math.Sin(incl);

            var x = (cosNode * cosArgPeri - sinNode * sinArgPeri * cosIncl) * xOrbit
                + (-cosNode * sinArgPeri - sinNode * cosArgPeri * cosIncl) * yOrbit;
            var y = (sinNode * cosArgPeri + cosNode * sinArgPeri * cosIncl) * xOrbit
                + (-sinNode * sinArgPeri + cosNode * cosArgPeri * cosIncl) * yOrbit;
            var z = (sinArgPeri * sinIncl) * xOrbit + (cosArgPeri * sinIncl) * yOrbit;

            return new EclipticVector(x, y, z);
        }

        /// <summary>Heliocentric ecliptic position of any AstronomyEngine body (planets -- asteroids/comets have their own from-scratch propagators, since AstronomyEngine has no small-body support).</summary>
        public static EclipticVector BodyHeliocentricEcliptic(Body body, AstroTime t) {
            var eqj = Astronomy.HelioVector(body, t);
            var ecl = Astronomy.RotateVector(Astronomy.Rotation_EQJ_ECL(), eqj);
            return new EclipticVector(ecl.x, ecl.y, ecl.z);
        }

        public static EclipticVector EarthHeliocentricEcliptic(AstroTime t) => BodyHeliocentricEcliptic(Body.Earth, t);

        /// <summary>Declination (degrees, J2000 equatorial) of a geocentric ecliptic vector.</summary>
        public static double GeocentricDeclinationDeg(EclipticVector geoEcliptic, AstroTime t) {
            var eclVector = new AstroVector(geoEcliptic.X, geoEcliptic.Y, geoEcliptic.Z, t);
            var eqj = Astronomy.RotateVector(Astronomy.Rotation_ECL_EQJ(), eclVector);
            return Astronomy.EquatorFromVector(eqj).dec;
        }

        /// <summary>Right ascension (hours, J2000 equatorial) sibling of GeocentricDeclinationDeg above.</summary>
        public static double GeocentricRightAscensionHours(EclipticVector geoEcliptic, AstroTime t) {
            var eclVector = new AstroVector(geoEcliptic.X, geoEcliptic.Y, geoEcliptic.Z, t);
            var eqj = Astronomy.RotateVector(Astronomy.Rotation_ECL_EQJ(), eclVector);
            return Astronomy.EquatorFromVector(eqj).ra;
        }

        private static EclipticVector RotateEqjToEcliptic(double x, double y, double z, AstroTime t) {
            var eqj = new AstroVector(x, y, z, t);
            var ecl = Astronomy.RotateVector(Astronomy.Rotation_EQJ_ECL(), eqj);
            return new EclipticVector(ecl.x, ecl.y, ecl.z);
        }

        /// <summary>Position (AU) and velocity (AU/day), heliocentric ecliptic (mean equinox J2000).</summary>
        public readonly struct EclipticState {
            public readonly EclipticVector Position;
            public readonly EclipticVector Velocity;
            public EclipticState(EclipticVector position, EclipticVector velocity) {
                Position = position;
                Velocity = velocity;
            }
        }

        /// <summary>
        /// The real observer's heliocentric position and velocity -- Earth's own state plus,
        /// when a site (lat/lon/elevation) is given, the offset and velocity contributed by
        /// standing on Earth's rotating surface rather than at its center. A null observer
        /// means "Earth's center" (no topocentric correction) -- the right choice for anything
        /// that doesn't need this rigor (a browse-list position, a multi-night finder chart),
        /// since real site coordinates aren't always available to every caller.
        /// </summary>
        public static EclipticState ObserverHeliocentricState(AstroTime t, Observer? observer) {
            var earth = Astronomy.HelioState(Body.Earth, t);
            var earthPos = RotateEqjToEcliptic(earth.x, earth.y, earth.z, t);
            var earthVel = RotateEqjToEcliptic(earth.vx, earth.vy, earth.vz, t);
            if (observer == null) return new EclipticState(earthPos, earthVel);

            // ObserverState gives the SITE's offset/velocity relative to Earth's center --
            // adding it to Earth's own heliocentric state gives the observer's true heliocentric
            // state, including both Earth's orbital motion and the site's own rotational
            // velocity (small, ~0.3-0.5 km/s at most, but real).
            var site = Astronomy.ObserverState(t, observer.Value, EquatorEpoch.J2000);
            var sitePos = RotateEqjToEcliptic(site.x, site.y, site.z, t);
            var siteVel = RotateEqjToEcliptic(site.vx, site.vy, site.vz, t);
            return new EclipticState(
                new EclipticVector(earthPos.X + sitePos.X, earthPos.Y + sitePos.Y, earthPos.Z + sitePos.Z),
                new EclipticVector(earthVel.X + siteVel.X, earthVel.Y + siteVel.Y, earthVel.Z + siteVel.Z)
            );
        }
    }
}
