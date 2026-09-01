using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CosineKitty;

namespace Perihelion.Astrometry {

    /// <summary>
    /// Live comet orbital elements from the Minor Planet Center's public comet elements file,
    /// and the universal-variable two-body propagator used to get a position from them at an
    /// arbitrary date (works uniformly for elliptical, parabolic, and hyperbolic comets).
    /// Ported from OryxAstro's server/utils/cometOrbits.ts.
    /// </summary>
    public sealed class CometElements {
        public required string Designation { get; init; }
        public required string Name { get; init; }
        public required DateTime PerihelionDate { get; init; } // must have Kind == Utc

        /// <summary>Perihelion distance, AU.</summary>
        public required double Q { get; init; }
        public required double Eccentricity { get; init; }
        public required double ArgPeriDeg { get; init; }
        public required double NodeDeg { get; init; }
        public required double InclinationDeg { get; init; }
    }

    public static class CometOrbits {
        private const string CometElementsUrl = "https://www.minorplanetcenter.net/iau/MPCORB/CometEls.txt";

        // Mirrors OryxAstro's own 6-hour cache window for the same feed (orbitalTracking.ts) --
        // comet elements don't change fast enough to need anything shorter, and re-fetching the
        // whole multi-thousand-row file on every sequence-item Validate() tick would be wasteful.
        private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(6);
        private static List<CometElements>? _cache;
        private static DateTime _cacheFetchedAtUtc;
        private static readonly SemaphoreSlim CacheLock = new(1, 1);

        private static string ParseFixed(string line, int startCol1, int endCol1) {
            var start = startCol1 - 1;
            var len = Math.Min(endCol1, line.Length) - start;
            if (start >= line.Length || len <= 0) return string.Empty;
            return line.Substring(start, len).Trim();
        }

        /// <summary>
        /// Parses one line of MPC's CometEls.txt fixed-width format. Returns null for
        /// blank/malformed lines rather than throwing -- a single bad row shouldn't take down
        /// the whole fetch.
        /// </summary>
        public static CometElements? ParseCometElementsLine(string line) {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try {
                var periYear = int.Parse(ParseFixed(line, 15, 18));
                var periMonth = int.Parse(ParseFixed(line, 20, 21));
                var periDay = double.Parse(ParseFixed(line, 23, 29));
                var q = double.Parse(ParseFixed(line, 31, 39));
                var eccentricity = double.Parse(ParseFixed(line, 42, 49));
                var argPeriDeg = double.Parse(ParseFixed(line, 52, 59));
                var nodeDeg = double.Parse(ParseFixed(line, 62, 69));
                var inclinationDeg = double.Parse(ParseFixed(line, 72, 79));
                var name = ParseFixed(line, 103, 158);

                // Periodic comets carry their number in columns 1-4 (e.g. "0022P") with columns
                // 5-12 either blank or a fragment-letter suffix; non-periodic comets carry their
                // whole provisional designation in columns 5-12 instead. Only combining both
                // ranges gives a designation that's actually unique per comet.
                var periodicNumber = ParseFixed(line, 1, 4);
                var designationSuffix = ParseFixed(line, 5, 12);
                var designation = (periodicNumber + designationSuffix).Trim();

                if (string.IsNullOrEmpty(name)) return null;

                var dayInt = (int)Math.Floor(periDay);
                var dayFraction = periDay - dayInt;
                var perihelionDate = new DateTime(periYear, periMonth, dayInt, 0, 0, 0, DateTimeKind.Utc)
                    .AddMilliseconds(dayFraction * 86400000);

                return new CometElements {
                    Designation = designation,
                    Name = name,
                    PerihelionDate = perihelionDate,
                    Q = q,
                    Eccentricity = eccentricity,
                    ArgPeriDeg = argPeriDeg,
                    NodeDeg = nodeDeg,
                    InclinationDeg = inclinationDeg,
                };
            } catch {
                return null;
            }
        }

        private static async Task<List<CometElements>> FetchCometElementsUncachedAsync(HttpClient httpClient, CancellationToken ct) {
            var text = await httpClient.GetStringAsync(CometElementsUrl, ct).ConfigureAwait(false);
            var result = new List<CometElements>();
            foreach (var line in text.Split('\n')) {
                var parsed = ParseCometElementsLine(line);
                if (parsed != null) result.Add(parsed);
            }
            return result;
        }

        public static async Task<IReadOnlyList<CometElements>> FetchCometElementsAsync(HttpClient httpClient, CancellationToken ct = default) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                if (_cache != null && DateTime.UtcNow - _cacheFetchedAtUtc < CacheMaxAge) {
                    return _cache;
                }
                var fresh = await FetchCometElementsUncachedAsync(httpClient, ct).ConfigureAwait(false);
                _cache = fresh;
                _cacheFetchedAtUtc = DateTime.UtcNow;
                return fresh;
            } finally {
                CacheLock.Release();
            }
        }

        public static async Task<CometElements?> FindByNameAsync(HttpClient httpClient, string name, CancellationToken ct = default) {
            var comets = await FetchCometElementsAsync(httpClient, ct).ConfigureAwait(false);
            return comets.FirstOrDefault(c => c.Name == name);
        }

        // --- Universal-variable two-body propagation from perihelion ---

        private static double StumpffC(double z) {
            if (z > 1e-6) return (1 - Math.Cos(Math.Sqrt(z))) / z;
            if (z < -1e-6) return (Math.Cosh(Math.Sqrt(-z)) - 1) / -z;
            return 0.5 - z / 24 + (z * z) / 720;
        }

        private static double StumpffS(double z) {
            if (z > 1e-6) {
                var sq = Math.Sqrt(z);
                return (sq - Math.Sin(sq)) / Math.Pow(sq, 3);
            }
            if (z < -1e-6) {
                var sq = Math.Sqrt(-z);
                return (Math.Sinh(sq) - sq) / Math.Pow(sq, 3);
            }
            return 1.0 / 6 - z / 120 + (z * z) / 5040;
        }

        /// <summary>
        /// Solves the universal Kepler equation for the universal anomaly (chi), given a start
        /// exactly at perihelion (so the radial-velocity term vanishes). f(chi) is provably
        /// monotonic (its derivative equals the orbit's instantaneous radius, always positive),
        /// so plain bisection after a geometric bracket expansion is simple and guaranteed to
        /// converge -- deliberately chosen over a Newton-only solver with a conic-specific
        /// initial-guess formula.
        /// </summary>
        private static double SolveUniversalAnomaly(double alpha, double r0, double deltaDays, double mu) {
            if (deltaDays == 0) return 0;
            var sqrtMu = Math.Sqrt(mu);
            var target = sqrtMu * deltaDays;

            double Residual(double chi) {
                var z = alpha * chi * chi;
                return (1 - alpha * r0) * Math.Pow(chi, 3) * StumpffS(z) + r0 * chi - target;
            }

            var a = 0.0;
            var fa = Residual(a); // always -target
            var b = Math.Sign(deltaDays) * Math.Max(Math.Sqrt(r0), 1);
            var fb = Residual(b);
            var guard = 0;
            while (Math.Sign(fb) == Math.Sign(fa) && guard < 200) {
                b *= 2;
                fb = Residual(b);
                guard++;
            }

            for (var i = 0; i < 100; i++) {
                var mid = (a + b) / 2;
                var fmid = Residual(mid);
                if (fmid == 0 || Math.Abs(b - a) < 1e-10) return mid;
                if (Math.Sign(fmid) == Math.Sign(fa)) {
                    a = mid;
                    fa = fmid;
                } else {
                    b = mid;
                }
            }
            return (a + b) / 2;
        }

        /// <summary>
        /// Heliocentric ecliptic (mean equinox J2000) position, AU, at an arbitrary date --
        /// works uniformly for elliptical, parabolic, and hyperbolic comets.
        /// </summary>
        public static EclipticVector HeliocentricEcliptic(CometElements comet, DateTime date) {
            var mu = OrbitalMechanics.GaussianKSquared;
            var q = comet.Q;
            var e = comet.Eccentricity;
            var alpha = (1 - e) / q; // = 1/a; exactly 0 at e=1, no division-by-zero singularity
            var deltaDays = (date.ToUniversalTime() - comet.PerihelionDate).TotalDays;

            var chi = SolveUniversalAnomaly(alpha, q, deltaDays, mu);
            var z = alpha * chi * chi;
            var c = StumpffC(z);
            var s = StumpffS(z);

            var v0 = Math.Sqrt(mu * (1 + e) / q); // tangential speed at perihelion, valid for any conic
            var fLagrange = 1 - (chi * chi / q) * c;
            var gLagrange = deltaDays - (Math.Pow(chi, 3) / Math.Sqrt(mu)) * s;

            // Perifocal frame: r0_vec = (q, 0), v0_vec = (0, v0) -- position is
            // f*r0_vec + g*v0_vec, which collapses to (f*q, g*v0) since the cross terms are zero.
            var xOrbit = fLagrange * q;
            var yOrbit = gLagrange * v0;

            return OrbitalMechanics.RotatePerifocalToEcliptic(xOrbit, yOrbit, comet.InclinationDeg, comet.NodeDeg, comet.ArgPeriDeg);
        }
    }
}
