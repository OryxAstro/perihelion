using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>Absolute magnitude -- null when the feed has no reliable value.</summary>
        public double? H { get; init; }

        /// <summary>Magnitude slope parameter -- defaults to 4.0 (a common generic value for long-period comets) when the feed has none.</summary>
        public double G { get; init; } = 4.0;
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

        // Sibling to the Plugins/ folder under NINA's own data root (CoreUtil.APPLICATIONTEMPPATH,
        // ~/.local/share/NINA on Linux) -- deliberately NOT under Plugins/3.0.0/Perihelion/ itself,
        // so a plugin update/reinstall (which replaces that folder's contents) doesn't wipe a
        // dataset the user may have specifically synced before heading out with no connectivity.
        private static readonly string CacheDirectory = Path.Combine(NINA.Core.Utility.CoreUtil.APPLICATIONTEMPPATH, "PerihelionData");
        private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "comet-elements-cache.json");

        private sealed class DiskCache {
            public DateTime FetchedAtUtc { get; set; }
            public string RawText { get; set; } = string.Empty;
        }

        /// <summary>Last time comet elements were actually fetched from MPC (in this run or a
        /// previous one, via the on-disk cache) -- null if never successfully synced at all.
        /// Surfaced via the API so the panel can show "last synced: X ago" rather than pretending
        /// the data is always current.</summary>
        public static DateTime? LastSyncedUtc => _cache != null ? _cacheFetchedAtUtc : LoadDiskCacheTimestampOnly();

        private static DateTime? LoadDiskCacheTimestampOnly() {
            try {
                if (!File.Exists(CacheFilePath)) return null;
                var disk = Newtonsoft.Json.JsonConvert.DeserializeObject<DiskCache>(File.ReadAllText(CacheFilePath));
                return disk?.FetchedAtUtc;
            } catch {
                return null;
            }
        }

        /// <summary>Seeds _cache from disk if this is the first call this run -- so a fresh PINS
        /// restart with no connectivity still has whatever was last successfully synced, instead
        /// of starting from nothing until the next live fetch succeeds.</summary>
        private static void LoadDiskCacheIfNeeded() {
            if (_cache != null) return;
            try {
                if (!File.Exists(CacheFilePath)) return;
                var disk = Newtonsoft.Json.JsonConvert.DeserializeObject<DiskCache>(File.ReadAllText(CacheFilePath));
                if (disk == null || string.IsNullOrEmpty(disk.RawText)) return;
                _cache = ParseCometElementsText(disk.RawText);
                _cacheFetchedAtUtc = disk.FetchedAtUtc;
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: could not read comet elements disk cache: {ex.Message}");
            }
        }

        private static void PersistToDisk(string rawText, DateTime fetchedAtUtc) {
            try {
                Directory.CreateDirectory(CacheDirectory);
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(new DiskCache { FetchedAtUtc = fetchedAtUtc, RawText = rawText });
                File.WriteAllText(CacheFilePath, json);
            } catch (Exception ex) {
                // Not fatal -- the in-memory cache from this fetch is still good for the rest of
                // this run, it just won't survive the next restart. Worth knowing about, not
                // worth failing the fetch over.
                NINA.Core.Utility.Logger.Warning($"Perihelion: could not persist comet elements to disk cache: {ex.Message}");
            }
        }

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
                var hRaw = ParseFixed(line, 92, 95);
                var gRaw = ParseFixed(line, 97, 100);
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

                var h = double.TryParse(hRaw, out var hParsed) ? hParsed : (double?)null;
                var g = double.TryParse(gRaw, out var gParsed) ? gParsed : 4.0;

                return new CometElements {
                    Designation = designation,
                    Name = name,
                    PerihelionDate = perihelionDate,
                    Q = q,
                    Eccentricity = eccentricity,
                    ArgPeriDeg = argPeriDeg,
                    NodeDeg = nodeDeg,
                    InclinationDeg = inclinationDeg,
                    H = h,
                    G = g,
                };
            } catch {
                return null;
            }
        }

        private static List<CometElements> ParseCometElementsText(string text) {
            var result = new List<CometElements>();
            foreach (var line in text.Split('\n')) {
                var parsed = ParseCometElementsLine(line);
                if (parsed != null) result.Add(parsed);
            }
            return result;
        }

        private static async Task<string> FetchCometElementsRawTextAsync(HttpClient httpClient, CancellationToken ct) {
            return await httpClient.GetStringAsync(CometElementsUrl, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the best available comet elements: a still-fresh cache, or a live refetch when
        /// stale. Deliberately does NOT throw when a live fetch fails and a cache (even a stale,
        /// disk-loaded one) already exists -- a parked field rig with no signal should keep
        /// tracking off whatever was last synced, not hard-fail every lookup the moment the
        /// 6-hour window lapses. Only throws when there is truly nothing to fall back to (never
        /// synced, ever, on this install, and the live fetch also failed).
        /// </summary>
        public static async Task<IReadOnlyList<CometElements>> FetchCometElementsAsync(HttpClient httpClient, CancellationToken ct = default) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                LoadDiskCacheIfNeeded();
                if (_cache != null && DateTime.UtcNow - _cacheFetchedAtUtc < CacheMaxAge) {
                    return _cache;
                }
                try {
                    var rawText = await FetchCometElementsRawTextAsync(httpClient, ct).ConfigureAwait(false);
                    var fresh = ParseCometElementsText(rawText);
                    _cache = fresh;
                    _cacheFetchedAtUtc = DateTime.UtcNow;
                    PersistToDisk(rawText, _cacheFetchedAtUtc);
                    return fresh;
                } catch (Exception ex) when (_cache != null) {
                    // Stale beats nothing -- log it so a genuinely broken feed doesn't go
                    // unnoticed forever, but keep tracking off the last good sync.
                    NINA.Core.Utility.Logger.Warning($"Perihelion: comet elements refresh failed, using data from {_cacheFetchedAtUtc:u}: {ex.Message}");
                    return _cache;
                }
            } finally {
                CacheLock.Release();
            }
        }

        /// <summary>
        /// Explicit "Sync Now" action for the panel's own sync button (matching NINA Orbitals'
        /// own per-object-type "download" screen) -- unlike FetchCometElementsAsync, this always
        /// attempts a live fetch regardless of cache age, and reports success/failure directly
        /// rather than silently falling back, since an explicit user action deserves a real
        /// answer about whether it worked. Leaves the existing cache (disk and in-memory) alone
        /// on failure, so a failed manual sync attempt can't make things worse.
        /// </summary>
        public static async Task<bool> SyncNowAsync(HttpClient httpClient, CancellationToken ct = default) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                var rawText = await FetchCometElementsRawTextAsync(httpClient, ct).ConfigureAwait(false);
                var fresh = ParseCometElementsText(rawText);
                _cache = fresh;
                _cacheFetchedAtUtc = DateTime.UtcNow;
                PersistToDisk(rawText, _cacheFetchedAtUtc);
                return true;
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Error($"Perihelion: comet elements sync failed: {ex.Message}");
                return false;
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

        private static double SolveKeplerEccentricAnomaly(double meanAnomalyRad, double eccentricity) {
            var e = meanAnomalyRad;
            for (var i = 0; i < 30; i++) {
                var dE = (e - eccentricity * Math.Sin(e) - meanAnomalyRad) / (1 - eccentricity * Math.Cos(e));
                e -= dE;
                if (Math.Abs(dE) < 1e-12) break;
            }
            return e;
        }

        /// <summary>Classical mean/eccentric/true anomaly "now" for an elliptical comet (e &lt; 1)
        /// -- null for a parabolic/hyperbolic one, where these don't apply the same way. A comet
        /// has no separate "epoch" the way an asteroid does: its own perihelion passage time T
        /// IS the instant mean anomaly is exactly zero, by definition, so mean anomaly here is
        /// simply the mean motion times days-since-T -- the same classical two-body relation
        /// AsteroidOrbits.ComputeAnomalies uses, just anchored at T instead of a stored epoch.
        /// A genuinely separate, simpler solve than HeliocentricEcliptic's own universal-variable
        /// propagator above (which has to handle parabolic/hyperbolic too); this only ever runs
        /// for e &lt; 1, where the classical Kepler equation is exact and directly solvable.</summary>
        public static AsteroidOrbits.OrbitAnomalies? ComputeAnomalies(CometElements comet, DateTime date) {
            if (comet.Eccentricity >= 1) return null;

            var a = comet.Q / (1 - comet.Eccentricity);
            var meanMotion = Math.Sqrt(OrbitalMechanics.GaussianKSquared / Math.Pow(a, 3)); // rad/day
            var daysSincePerihelion = (date.ToUniversalTime() - comet.PerihelionDate).TotalDays;
            var meanAnomaly = OrbitalMechanics.NormalizeRad(meanMotion * daysSincePerihelion);
            var eccentricAnomaly = SolveKeplerEccentricAnomaly(meanAnomaly, comet.Eccentricity);

            var e = comet.Eccentricity;
            var trueAnomaly = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(eccentricAnomaly / 2), Math.Sqrt(1 - e) * Math.Cos(eccentricAnomaly / 2));
            var radius = a * (1 - e * Math.Cos(eccentricAnomaly));

            return new AsteroidOrbits.OrbitAnomalies(
                MeanAnomalyDeg: meanAnomaly / OrbitalMechanics.Deg2Rad,
                EccentricAnomalyDeg: OrbitalMechanics.NormalizeRad(eccentricAnomaly) / OrbitalMechanics.Deg2Rad,
                TrueAnomalyDeg: OrbitalMechanics.NormalizeRad(trueAnomaly) / OrbitalMechanics.Deg2Rad,
                DistanceAu: radius);
        }

        /// <summary>
        /// Standard (and well-known to be approximate) comet magnitude formula:
        /// m = H + 5*log10(delta) + 2.5*G*log10(r), where r = heliocentric distance and
        /// delta = geocentric distance, both AU. Null when the comet has no H value.
        /// </summary>
        public static double? PredictedMagnitude(CometElements comet, DateTime date, AstroTime t) {
            if (comet.H == null) return null;
            var helio = HeliocentricEcliptic(comet, date);
            var earth = OrbitalMechanics.EarthHeliocentricEcliptic(t);
            var r = Math.Sqrt(helio.X * helio.X + helio.Y * helio.Y + helio.Z * helio.Z);
            var dx = helio.X - earth.X;
            var dy = helio.Y - earth.Y;
            var dz = helio.Z - earth.Z;
            var delta = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return comet.H.Value + 5 * Math.Log10(delta) + 2.5 * comet.G * Math.Log10(r);
        }
    }
}
