using System;
using System.Collections.Generic;
using CosineKitty;

namespace Perihelion.Astrometry {

    /// <summary>
    /// Real orbital elements for a curated list of bright, numbered asteroids -- NOT the full
    /// MPC asteroid catalog. Ported from OryxAstro's server/utils/asteroidOrbits.ts.
    /// </summary>
    public sealed class AsteroidElements {
        public required string Id { get; init; }
        public required string Name { get; init; }

        /// <summary>Julian Date of the epoch these elements (esp. MeanAnomalyDeg) are valid for.</summary>
        public required double EpochJd { get; init; }

        /// <summary>Semi-major axis, AU.</summary>
        public required double A { get; init; }
        public required double Eccentricity { get; init; }
        public required double InclinationDeg { get; init; }
        public required double NodeDeg { get; init; }
        public required double ArgPeriDeg { get; init; }
        public required double MeanAnomalyDeg { get; init; }

        /// <summary>Absolute magnitude.</summary>
        public required double H { get; init; }

        /// <summary>Magnitude slope parameter.</summary>
        public required double G { get; init; }
    }

    public static class AsteroidOrbits {
        // Fetched 2026-08-27 from https://ssd-api.jpl.nasa.gov/sbdb.api -- all share epoch
        // JD 2461200.5 (2026-06-09). Kept identical to OryxAstro's own BRIGHT_ASTEROIDS table
        // so results agree between the two codebases; refresh both together.
        public static readonly IReadOnlyList<AsteroidElements> BrightAsteroids = new List<AsteroidElements> {
            new() { Id = "ceres", Name = "1 Ceres", EpochJd = 2461200.5, A = 2.765552595, Eccentricity = 0.079692295, InclinationDeg = 10.588028, NodeDeg = 80.248627, ArgPeriDeg = 73.294215, MeanAnomalyDeg = 274.419346, H = 3.34, G = 0.12 },
            new() { Id = "vesta", Name = "4 Vesta", EpochJd = 2461200.5, A = 2.361365965, Eccentricity = 0.090203744, InclinationDeg = 7.143926, NodeDeg = 103.701293, ArgPeriDeg = 151.468648, MeanAnomalyDeg = 81.190156, H = 3.25, G = 0.32 },
            new() { Id = "pallas", Name = "2 Pallas", EpochJd = 2461200.5, A = 2.769559011, Eccentricity = 0.230700100, InclinationDeg = 34.932793, NodeDeg = 172.886619, ArgPeriDeg = 310.969916, MeanAnomalyDeg = 254.249652, H = 4.12, G = 0.11 },
            new() { Id = "juno", Name = "3 Juno", EpochJd = 2461200.5, A = 2.670989527, Eccentricity = 0.255699984, InclinationDeg = 12.986592, NodeDeg = 169.811595, ArgPeriDeg = 247.895074, MeanAnomalyDeg = 262.732294, H = 5.19, G = 0.32 },
            new() { Id = "hebe", Name = "6 Hebe", EpochJd = 2461200.5, A = 2.425661373, Eccentricity = 0.202254997, InclinationDeg = 14.736277, NodeDeg = 138.613321, ArgPeriDeg = 239.730454, MeanAnomalyDeg = 44.721188, H = 5.62, G = 0.24 },
            new() { Id = "iris", Name = "7 Iris", EpochJd = 2461200.5, A = 2.385746303, Eccentricity = 0.230274685, InclinationDeg = 5.518568, NodeDeg = 259.485140, ArgPeriDeg = 145.407128, MeanAnomalyDeg = 115.291667, H = 5.70, G = 0.15 },
            new() { Id = "flora", Name = "8 Flora", EpochJd = 2461200.5, A = 2.201560614, Eccentricity = 0.156196403, InclinationDeg = 5.890315, NodeDeg = 110.843307, ArgPeriDeg = 285.408348, MeanAnomalyDeg = 259.262132, H = 6.62, G = 0.28 },
            new() { Id = "metis", Name = "9 Metis", EpochJd = 2461200.5, A = 2.386566897, Eccentricity = 0.122489195, InclinationDeg = 5.577308, NodeDeg = 68.865804, ArgPeriDeg = 5.932345, MeanAnomalyDeg = 252.616795, H = 6.18, G = 0.17 },
            new() { Id = "hygiea", Name = "10 Hygiea", EpochJd = 2461200.5, A = 3.150974034, Eccentricity = 0.106709274, InclinationDeg = 3.829530, NodeDeg = 283.119893, ArgPeriDeg = 312.424239, MeanAnomalyDeg = 252.034424, H = 5.65, G = 0.15 },
            new() { Id = "eunomia", Name = "15 Eunomia", EpochJd = 2461200.5, A = 2.641958731, Eccentricity = 0.187770768, InclinationDeg = 11.761393, NodeDeg = 292.880783, ArgPeriDeg = 98.461318, MeanAnomalyDeg = 159.689105, H = 5.42, G = 0.23 },
            new() { Id = "psyche", Name = "16 Psyche", EpochJd = 2461200.5, A = 2.925720466, Eccentricity = 0.134932474, InclinationDeg = 3.098749, NodeDeg = 149.975386, ArgPeriDeg = 230.032678, MeanAnomalyDeg = 79.769395, H = 6.20, G = 0.20 },
            new() { Id = "astraea", Name = "5 Astraea", EpochJd = 2461200.5, A = 2.576807734, Eccentricity = 0.187573966, InclinationDeg = 5.359677, NodeDeg = 141.447209, ArgPeriDeg = 359.375634, MeanAnomalyDeg = 181.478205, H = 6.95, G = 0.15 },
            new() { Id = "nausikaa", Name = "192 Nausikaa", EpochJd = 2461200.5, A = 2.403355374, Eccentricity = 0.245173273, InclinationDeg = 6.795704, NodeDeg = 343.070377, ArgPeriDeg = 30.576891, MeanAnomalyDeg = 326.274898, H = 7.21, G = 0.03 },
        };

        public static AsteroidElements? FindByName(string name) {
            foreach (var a in BrightAsteroids) {
                if (a.Name == name) return a;
            }
            return null;
        }

        private static double SolveKeplerEccentricAnomaly(double meanAnomalyRad, double eccentricity) {
            var e = meanAnomalyRad;
            for (var i = 0; i < 30; i++) {
                var dE = (e - eccentricity * Math.Sin(e) - meanAnomalyRad) / (1 - eccentricity * Math.Cos(e));
                e -= dE;
                if (Math.Abs(dE) < 1e-12) break;
            }
            return e;
        }

        /// <summary>The classical anomalies and heliocentric distance at a given instant --
        /// display-only data for the Perihelion dockable panel's elements card (mirrors what
        /// NINA.Joko.Plugin.Orbitals' own panel shows). Deliberately a separate method rather
        /// than exposing internals of the already physics-audited HeliocentricEcliptic below:
        /// same formulas, kept independent so nothing here can regress that method.</summary>
        public readonly record struct OrbitAnomalies(double MeanAnomalyDeg, double EccentricAnomalyDeg, double TrueAnomalyDeg, double DistanceAu);

        public static OrbitAnomalies ComputeAnomalies(AsteroidElements elements, AstroTime t) {
            var daysSinceEpoch = OrbitalMechanics.JulianDate(t) - elements.EpochJd;
            var meanMotion = Math.Sqrt(OrbitalMechanics.GaussianKSquared / Math.Pow(elements.A, 3)); // rad/day
            var meanAnomaly = OrbitalMechanics.NormalizeRad(elements.MeanAnomalyDeg * OrbitalMechanics.Deg2Rad + meanMotion * daysSinceEpoch);
            var eccentricAnomaly = SolveKeplerEccentricAnomaly(meanAnomaly, elements.Eccentricity);

            var e = elements.Eccentricity;
            var trueAnomaly = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(eccentricAnomaly / 2), Math.Sqrt(1 - e) * Math.Cos(eccentricAnomaly / 2));
            var radius = elements.A * (1 - e * Math.Cos(eccentricAnomaly));

            return new OrbitAnomalies(
                MeanAnomalyDeg: meanAnomaly / OrbitalMechanics.Deg2Rad,
                EccentricAnomalyDeg: OrbitalMechanics.NormalizeRad(eccentricAnomaly) / OrbitalMechanics.Deg2Rad,
                TrueAnomalyDeg: OrbitalMechanics.NormalizeRad(trueAnomaly) / OrbitalMechanics.Deg2Rad,
                DistanceAu: radius);
        }

        /// <summary>Heliocentric ecliptic (mean equinox J2000) position, AU.</summary>
        public static EclipticVector HeliocentricEcliptic(AsteroidElements elements, AstroTime t) {
            var daysSinceEpoch = OrbitalMechanics.JulianDate(t) - elements.EpochJd;
            var meanMotion = Math.Sqrt(OrbitalMechanics.GaussianKSquared / Math.Pow(elements.A, 3)); // rad/day
            var meanAnomaly = elements.MeanAnomalyDeg * OrbitalMechanics.Deg2Rad + meanMotion * daysSinceEpoch;
            var eccentricAnomaly = SolveKeplerEccentricAnomaly(OrbitalMechanics.NormalizeRad(meanAnomaly), elements.Eccentricity);

            var e = elements.Eccentricity;
            var trueAnomaly = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(eccentricAnomaly / 2), Math.Sqrt(1 - e) * Math.Cos(eccentricAnomaly / 2));
            var radius = elements.A * (1 - e * Math.Cos(eccentricAnomaly));

            var xOrbit = radius * Math.Cos(trueAnomaly);
            var yOrbit = radius * Math.Sin(trueAnomaly);

            return OrbitalMechanics.RotatePerifocalToEcliptic(xOrbit, yOrbit, elements.InclinationDeg, elements.NodeDeg, elements.ArgPeriDeg);
        }

        private static double VectorLength(EclipticVector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

        /// <summary>Real current apparent (visual) magnitude via the standard IAU H-G two-term phase function (Bowell et al. 1989).</summary>
        public static double ApparentMagnitude(AsteroidElements elements, EclipticVector helio, EclipticVector earthHelio) {
            var geo = new EclipticVector(helio.X - earthHelio.X, helio.Y - earthHelio.Y, helio.Z - earthHelio.Z);
            var r = VectorLength(helio); // Sun-asteroid distance, AU
            var delta = VectorLength(geo); // Earth-asteroid distance, AU
            var cosAlpha = (helio.X * geo.X + helio.Y * geo.Y + helio.Z * geo.Z) / (r * delta);
            var alpha = Math.Acos(Math.Min(1, Math.Max(-1, cosAlpha)));
            var tanHalfAlpha = Math.Tan(alpha / 2);
            var phi1 = Math.Exp(-3.33 * Math.Pow(tanHalfAlpha, 0.63));
            var phi2 = Math.Exp(-1.87 * Math.Pow(tanHalfAlpha, 1.22));
            return elements.H + 5 * Math.Log10(r * delta) - 2.5 * Math.Log10((1 - elements.G) * phi1 + elements.G * phi2);
        }
    }
}
