using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CosineKitty;
using Newtonsoft.Json.Linq;

namespace Perihelion.Astrometry {

    /// <summary>
    /// Orbital elements for asteroids meeting the configured absolute-magnitude (H) threshold --
    /// live-fetched in bulk from JPL's Small-Body Database, not a fixed table. Filtering happens
    /// server-side on H (JPL's bulk query API doesn't support filtering or sorting by apparent
    /// magnitude, which depends on the observer's current date), unlike CometOrbits' own
    /// threshold, which filters an already-fetched, already-small comet list by true predicted
    /// apparent magnitude -- the two threshold numbers aren't directly comparable.
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

        // JPL's bulk query API returns the whole qualifying set in one response -- it has no
        // pagination or result-size limit of its own. This is an upper bound independent of
        // PerihelionPlugin.AsteroidMagnitudeThreshold, so a user raising that setting well past
        // what the Browse list could ever usefully show can't turn a routine sync into a
        // multi-thousand-row-beyond-reason download.
        private const double HardCapH = 12.0;

        // Main-belt asteroid osculating elements barely move month to month (unlike a comet's
        // perihelion-passage time), and JPL/MPC only republish a fresh epoch for these objects
        // roughly every ~200 days in practice. A much longer window than comets' 6 hours is both
        // physically reasonable and considerate of JPL's public API for a periodic background sync.
        private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

        private static List<AsteroidElements>? _cache;
        private static DateTime _cacheFetchedAtUtc;
        private static double _cacheThreshold = double.NaN;
        private static readonly SemaphoreSlim CacheLock = new(1, 1);

        // Sibling to CometOrbits' own disk cache, same "survive a plugin reinstall" reasoning.
        private static readonly string CacheDirectory = Path.Combine(NINA.Core.Utility.CoreUtil.APPLICATIONTEMPPATH, "PerihelionData");
        private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "asteroid-elements-cache.json");

        private sealed class DiskCache {
            public DateTime FetchedAtUtc { get; set; }
            public double Threshold { get; set; }
            public List<AsteroidElements> Elements { get; set; } = new();
        }

        /// <summary>Last time asteroid elements were actually fetched from JPL (this run or a
        /// previous one, via the on-disk cache) -- null if never successfully synced.</summary>
        public static DateTime? LastSyncedUtc => _cache != null ? _cacheFetchedAtUtc : LoadDiskCacheTimestampOnly();

        /// <summary>Number of asteroids in the currently cached elements (0 if never synced in
        /// this run or on disk) -- mirrors CometOrbits.CachedCount. Shown for the same "confirms
        /// it actually loaded" reason the comet count is; grows or shrinks with the configured
        /// threshold, unlike the fixed-13 curated list this replaced.</summary>
        public static int CachedCount {
            get {
                LoadDiskCacheIfNeeded();
                return _cache?.Count ?? 0;
            }
        }

        private static double CurrentThreshold => Math.Min(PerihelionPlugin.Instance?.AsteroidMagnitudeThreshold ?? 9.0, HardCapH);

        private static DateTime? LoadDiskCacheTimestampOnly() {
            try {
                if (!File.Exists(CacheFilePath)) return null;
                var disk = Newtonsoft.Json.JsonConvert.DeserializeObject<DiskCache>(File.ReadAllText(CacheFilePath));
                return disk?.FetchedAtUtc;
            } catch {
                return null;
            }
        }

        private static void LoadDiskCacheIfNeeded() {
            if (_cache != null) return;
            try {
                if (!File.Exists(CacheFilePath)) return;
                var disk = Newtonsoft.Json.JsonConvert.DeserializeObject<DiskCache>(File.ReadAllText(CacheFilePath));
                if (disk == null || disk.Elements.Count == 0) return;
                _cache = disk.Elements;
                _cacheFetchedAtUtc = disk.FetchedAtUtc;
                _cacheThreshold = disk.Threshold;
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: could not read asteroid elements disk cache: {ex.Message}");
            }
        }

        private static void PersistToDisk(List<AsteroidElements> elements, DateTime fetchedAtUtc, double threshold) {
            try {
                Directory.CreateDirectory(CacheDirectory);
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(new DiskCache { FetchedAtUtc = fetchedAtUtc, Threshold = threshold, Elements = elements });
                File.WriteAllText(CacheFilePath, json);
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: could not persist asteroid elements to disk cache: {ex.Message}");
            }
        }

        // JPL's own "full_name" field is a fixed-width string like "     1 Ceres (A801 AA)" --
        // leading spaces, catalog number, name, and a provisional-designation parenthetical this
        // class has no use for. An unnumbered object (rare at any threshold small enough to be
        // useful here) has no leading number at all; its whole designation becomes both Id and
        // Name rather than failing to parse.
        private static readonly Regex FullNamePattern = new(@"^(\d+)\s+([^()]+?)\s*(\(.*\))?$", RegexOptions.Compiled);

        private static (string Id, string Name) ParseFullName(string fullName) {
            var trimmed = fullName.Trim();
            var match = FullNamePattern.Match(trimmed);
            if (match.Success) {
                var number = match.Groups[1].Value;
                var name = match.Groups[2].Value.Trim();
                return (number, $"{number} {name}");
            }
            return (trimmed, trimmed);
        }

        private static List<AsteroidElements> ParseBulkResponse(string rawJson) {
            var root = JObject.Parse(rawJson);
            var fields = root["fields"]!.Select(f => (string)f!).ToList();
            var columnIndex = fields.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i);
            var rows = (JArray?)root["data"] ?? new JArray();

            string? Cell(JArray row, string field) => (string?)row[columnIndex[field]];
            double ParseRequired(JArray row, string field) => double.Parse(Cell(row, field)!, CultureInfo.InvariantCulture);

            var result = new List<AsteroidElements>(rows.Count);
            var skipped = 0;
            foreach (var token in rows) {
                var row = (JArray)token;
                try {
                    var (id, name) = ParseFullName(Cell(row, "full_name")!);
                    var g = Cell(row, "G");
                    result.Add(new AsteroidElements {
                        Id = id,
                        Name = name,
                        EpochJd = ParseRequired(row, "epoch"),
                        A = ParseRequired(row, "a"),
                        Eccentricity = ParseRequired(row, "e"),
                        InclinationDeg = ParseRequired(row, "i"),
                        NodeDeg = ParseRequired(row, "om"),
                        ArgPeriDeg = ParseRequired(row, "w"),
                        MeanAnomalyDeg = ParseRequired(row, "ma"),
                        H = ParseRequired(row, "H"),
                        // Most entries at a generous threshold have no published slope parameter
                        // at all (confirmed live -- the large majority of a wide H-filtered pull
                        // are distant, less-studied objects) -- 0.15 is the IAU's own conventional
                        // default for an unknown G, not a Perihelion invention.
                        G = g != null ? double.Parse(g, CultureInfo.InvariantCulture) : 0.15,
                    });
                } catch (Exception ex) {
                    skipped++;
                    NINA.Core.Utility.Logger.Warning($"Perihelion: skipped one malformed asteroid row from JPL: {ex.Message}");
                }
            }
            if (skipped > 0) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: skipped {skipped} malformed asteroid row(s) out of {rows.Count} from JPL's bulk query.");
            }

            // Sorted brightest-by-H first for a deterministic, meaningful order on this raw set
            // (Export, disk cache) -- JPL's bulk query API filters but does not sort. NOT the
            // order the Browse list itself is shown in: ListBrowseObjectsAsync computes each
            // candidate's current apparent magnitude and re-sorts by that instead, since H
            // alone favors intrinsically huge-but-distant objects (dwarf planets, TNOs) over
            // smaller, much-brighter-right-now main-belt asteroids.
            result.Sort((a, b) => a.H.CompareTo(b.H));
            return result;
        }

        private static async Task<List<AsteroidElements>> FetchBulkAsync(HttpClient httpClient, double threshold, CancellationToken ct) {
            var cdata = new JObject { ["AND"] = new JArray { $"H|LT|{threshold.ToString(CultureInfo.InvariantCulture)}" } };
            var cdataParam = Uri.EscapeDataString(cdata.ToString(Newtonsoft.Json.Formatting.None));
            var url = $"https://ssd-api.jpl.nasa.gov/sbdb_query.api?fields=full_name,e,a,i,om,w,ma,epoch,H,G&sb-cdata={cdataParam}&full-prec=true";
            var rawJson = await httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
            return ParseBulkResponse(rawJson);
        }

        /// <summary>
        /// Returns the best available asteroid elements: a still-fresh cache fetched at the
        /// currently configured threshold, or a live refetch when stale or the threshold itself
        /// has changed since the cache was built (raising AsteroidMagnitudeThreshold has to be
        /// able to reveal newly-qualifying objects immediately, not wait out the 24h TTL). Falls
        /// back to the existing cache on a failed refetch, and only throws when there is truly
        /// nothing to fall back to.
        /// </summary>
        public static async Task<IReadOnlyList<AsteroidElements>> FetchAsteroidElementsAsync(HttpClient httpClient, CancellationToken ct = default) {
            var threshold = CurrentThreshold;
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                LoadDiskCacheIfNeeded();
                if (_cache != null && DateTime.UtcNow - _cacheFetchedAtUtc < CacheMaxAge && Math.Abs(_cacheThreshold - threshold) < 0.001) {
                    return _cache;
                }

                try {
                    var fresh = await FetchBulkAsync(httpClient, threshold, ct).ConfigureAwait(false);
                    _cache = fresh;
                    _cacheFetchedAtUtc = DateTime.UtcNow;
                    _cacheThreshold = threshold;
                    PersistToDisk(fresh, _cacheFetchedAtUtc, threshold);
                    return fresh;
                } catch (Exception ex) {
                    if (_cache != null) {
                        NINA.Core.Utility.Logger.Warning($"Perihelion: asteroid elements refresh failed, using data from {_cacheFetchedAtUtc:u}: {ex.Message}");
                        return _cache;
                    }
                    throw new InvalidOperationException("Could not fetch asteroid elements from JPL and no cached data exists on this install.", ex);
                }
            } finally {
                CacheLock.Release();
            }
        }

        /// <summary>
        /// Explicit "Sync Now" action, matching CometOrbits.SyncNowAsync -- always attempts a live
        /// fetch at the current threshold regardless of cache age, and leaves the existing cache
        /// alone on failure rather than reporting a misleading success.
        /// </summary>
        public static async Task<bool> SyncNowAsync(HttpClient httpClient, CancellationToken ct = default) {
            var threshold = CurrentThreshold;
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                var fresh = await FetchBulkAsync(httpClient, threshold, ct).ConfigureAwait(false);
                _cache = fresh;
                _cacheFetchedAtUtc = DateTime.UtcNow;
                _cacheThreshold = threshold;
                PersistToDisk(fresh, _cacheFetchedAtUtc, threshold);
                return true;
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: asteroid Sync Now failed: {ex.Message}");
                return false;
            } finally {
                CacheLock.Release();
            }
        }

        public static async Task<AsteroidElements?> FindByNameAsync(HttpClient httpClient, string name, CancellationToken ct = default) {
            var all = await FetchAsteroidElementsAsync(httpClient, ct).ConfigureAwait(false);
            foreach (var a in all) {
                if (a.Name == name) return a;
            }
            return null;
        }

        /// <summary>
        /// Imports asteroid elements from a local file -- unlike CometOrbits.ImportFromFileAsync,
        /// there's no universal third-party bulk format for this data, so this accepts the same
        /// plain JSON list Export below produces: Perihelion-to-Perihelion sharing rather than an
        /// MPC-file-compatible import. Still solves the same motivating case (an install behind a
        /// blocked/rate-limited network egress can receive a colleague's already-synced data
        /// instead of hitting JPL itself), just via Perihelion's own format since no external one
        /// fits. Replaces the entire current cache and is treated as satisfying the current
        /// threshold (so it isn't immediately re-fetched and discarded on the next lookup);
        /// returns the number of asteroids actually parsed.
        /// </summary>
        public static async Task<int> ImportFromFileAsync(string filePath, CancellationToken ct = default) {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var elements = Newtonsoft.Json.JsonConvert.DeserializeObject<List<AsteroidElements>>(json)
                ?? throw new InvalidOperationException("File did not contain a recognizable Perihelion asteroid elements list.");
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                _cache = elements;
                _cacheFetchedAtUtc = DateTime.UtcNow;
                _cacheThreshold = CurrentThreshold;
                PersistToDisk(elements, _cacheFetchedAtUtc, _cacheThreshold);
                return elements.Count;
            } finally {
                CacheLock.Release();
            }
        }

        /// <summary>
        /// Exports the currently cached asteroid elements as a plain JSON file -- see
        /// ImportFromFileAsync's own doc comment for why this is Perihelion's own format rather
        /// than an MPC-compatible one. Returns false (writes nothing) when there is genuinely no
        /// cache yet on this install.
        /// </summary>
        public static async Task<bool> ExportToFileAsync(string filePath, CancellationToken ct = default) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                LoadDiskCacheIfNeeded();
                if (_cache == null) return false;
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(_cache, Newtonsoft.Json.Formatting.Indented);
                await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
                return true;
            } finally {
                CacheLock.Release();
            }
        }

        /// <summary>
        /// Explicit "Clear" action, mirroring CometOrbits.ClearAsync exactly -- wipes both the
        /// in-memory and on-disk asteroid cache, so the next lookup does a full live re-fetch from
        /// JPL at the currently configured threshold.
        /// </summary>
        public static async Task ClearAsync(CancellationToken ct = default) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                _cache = null;
                _cacheFetchedAtUtc = default;
                _cacheThreshold = double.NaN;
                try {
                    if (File.Exists(CacheFilePath)) File.Delete(CacheFilePath);
                } catch (Exception ex) {
                    NINA.Core.Utility.Logger.Warning($"Perihelion: could not delete asteroid elements disk cache: {ex.Message}");
                }
            } finally {
                CacheLock.Release();
            }
        }

        /// <summary>
        /// Epoch-staleness guardrail -- pure two-body Keplerian propagation far in time from an
        /// object's own reference epoch accumulates unmodeled error from ongoing planetary
        /// perturbation. The live fetch above keeps this bounded under normal operation, but
        /// doesn't guarantee it (a fetch can succeed while simply reporting old data) -- this is a
        /// direct, per-object check so that shows up somewhere a user can actually see it, rather
        /// than staying invisible. 180 days is roughly JPL/MPC's own observed re-osculation
        /// cadence for these objects, not a claim that error becomes unacceptable at exactly that
        /// number -- past it, the elements are simply older than they'd normally ever be, which is
        /// worth flagging.
        /// </summary>
        public const double StaleEpochThresholdDays = 180;

        public static double EpochAgeDays(AsteroidElements elements, DateTime atUtc) =>
            Math.Abs(OrbitalMechanics.JulianDate(new AstroTime(atUtc)) - elements.EpochJd);

        public static bool IsEpochStale(AsteroidElements elements, DateTime atUtc) =>
            EpochAgeDays(elements, atUtc) > StaleEpochThresholdDays;

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
        /// display-only data for the Perihelion dockable panel's elements card. Deliberately a
        /// separate method rather than exposing internals of the already physics-audited
        /// HeliocentricEcliptic below: same formulas, kept independent so nothing here can
        /// regress that method.</summary>
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

        /// <summary>current apparent (visual) magnitude via the standard IAU H-G two-term phase function (Bowell et al. 1989).</summary>
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
