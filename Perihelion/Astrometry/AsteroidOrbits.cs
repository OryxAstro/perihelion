using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CosineKitty;
using Newtonsoft.Json.Linq;

namespace Perihelion.Astrometry {

    /// <summary>
    /// Real orbital elements for a curated list of bright, numbered asteroids -- NOT the full
    /// MPC asteroid catalog. The curated NAME list mirrors OryxAstro's own BRIGHT_ASTEROIDS
    /// table, but unlike that table (and unlike this class's own original version), the
    /// ELEMENTS themselves are live-fetched from JPL's Small-Body Database rather than
    /// hardcoded -- see the epoch-staleness reasoning on FetchAsteroidElementsAsync below.
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

        private sealed class AsteroidTarget {
            public required string Id;
            public required string Name;

            /// <summary>JPL/MPC catalog number -- resolves unambiguously via SBDB's "sstr" query
            /// param (verified against a live call for every object in this list), unlike
            /// searching by name, which risks matching the wrong object for a common word.</summary>
            public required int Number;
        }

        /// <summary>
        /// Which asteroids Perihelion tracks -- identity only (id/name/catalog number), kept
        /// deliberately the same curated set as OryxAstro's own BRIGHT_ASTEROIDS (bright,
        /// well-known, unambiguous main-belt objects a user would actually search for). If
        /// Perihelion needs an asteroid outside this list, add it here (its elements are fetched
        /// live, so nothing else needs updating).
        /// </summary>
        private static readonly IReadOnlyList<AsteroidTarget> Targets = new List<AsteroidTarget> {
            new() { Id = "ceres", Name = "1 Ceres", Number = 1 },
            new() { Id = "vesta", Name = "4 Vesta", Number = 4 },
            new() { Id = "pallas", Name = "2 Pallas", Number = 2 },
            new() { Id = "juno", Name = "3 Juno", Number = 3 },
            new() { Id = "hebe", Name = "6 Hebe", Number = 6 },
            new() { Id = "iris", Name = "7 Iris", Number = 7 },
            new() { Id = "flora", Name = "8 Flora", Number = 8 },
            new() { Id = "metis", Name = "9 Metis", Number = 9 },
            new() { Id = "hygiea", Name = "10 Hygiea", Number = 10 },
            new() { Id = "eunomia", Name = "15 Eunomia", Number = 15 },
            new() { Id = "psyche", Name = "16 Psyche", Number = 16 },
            new() { Id = "astraea", Name = "5 Astraea", Number = 5 },
            new() { Id = "nausikaa", Name = "192 Nausikaa", Number = 192 },
        };

        // Main-belt asteroid osculating elements barely move month to month (unlike a comet's
        // perihelion-passage time), and JPL/MPC only republish a fresh epoch for these objects
        // roughly every ~200 days in practice (their own "epoch" field advances in steps, not
        // continuously -- confirmed against the live feed while building this). A much longer
        // window than comets' 6 hours is both physically reasonable and considerate of JPL's
        // public API for a list this small and this stable.
        private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

        private static List<AsteroidElements>? _cache;
        private static DateTime _cacheFetchedAtUtc;
        private static readonly SemaphoreSlim CacheLock = new(1, 1);

        // Sibling to CometOrbits' own disk cache, same "survive a plugin reinstall" reasoning.
        private static readonly string CacheDirectory = Path.Combine(NINA.Core.Utility.CoreUtil.APPLICATIONTEMPPATH, "PerihelionData");
        private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "asteroid-elements-cache.json");

        private sealed class DiskCache {
            public DateTime FetchedAtUtc { get; set; }
            public List<AsteroidElements> Elements { get; set; } = new();
        }

        /// <summary>Last time asteroid elements were actually fetched from JPL (this run or a
        /// previous one, via the on-disk cache) -- null if never successfully synced.</summary>
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

        private static void LoadDiskCacheIfNeeded() {
            if (_cache != null) return;
            try {
                if (!File.Exists(CacheFilePath)) return;
                var disk = Newtonsoft.Json.JsonConvert.DeserializeObject<DiskCache>(File.ReadAllText(CacheFilePath));
                if (disk == null || disk.Elements.Count == 0) return;
                _cache = disk.Elements;
                _cacheFetchedAtUtc = disk.FetchedAtUtc;
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: could not read asteroid elements disk cache: {ex.Message}");
            }
        }

        private static void PersistToDisk(List<AsteroidElements> elements, DateTime fetchedAtUtc) {
            try {
                Directory.CreateDirectory(CacheDirectory);
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(new DiskCache { FetchedAtUtc = fetchedAtUtc, Elements = elements });
                File.WriteAllText(CacheFilePath, json);
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: could not persist asteroid elements to disk cache: {ex.Message}");
            }
        }

        private static double ParseElementValue(JToken orbit, string name) {
            var token = orbit["elements"]!.First(e => (string)e["name"]! == name)["value"]!;
            return double.Parse((string)token!, CultureInfo.InvariantCulture);
        }

        private static async Task<AsteroidElements?> FetchOneAsync(HttpClient httpClient, AsteroidTarget target, CancellationToken ct) {
            try {
                var url = $"https://ssd-api.jpl.nasa.gov/sbdb.api?sstr={target.Number}&full-prec=true&phys-par=true";
                var rawJson = await httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
                var root = JObject.Parse(rawJson);
                var orbit = root["orbit"];
                if (orbit == null) return null;

                var hToken = root["phys_par"]?.FirstOrDefault(p => (string)p["name"]! == "H")?["value"];
                var gToken = root["phys_par"]?.FirstOrDefault(p => (string)p["name"]! == "G")?["value"];
                // Generic fallbacks would be surprising for any of these well-known, well-observed
                // objects (every one of them has real published H/G) -- present only as a safety
                // net so a transient feed hiccup can't produce a null-reference instead of a
                // slightly-off magnitude. 15.0/0.15 are unremarkable, non-alarming placeholders,
                // not physically meaningful defaults.
                var h = hToken != null ? double.Parse((string)hToken!, CultureInfo.InvariantCulture) : 15.0;
                var g = gToken != null ? double.Parse((string)gToken!, CultureInfo.InvariantCulture) : 0.15;

                return new AsteroidElements {
                    Id = target.Id,
                    Name = target.Name,
                    EpochJd = double.Parse((string)orbit["epoch"]!, CultureInfo.InvariantCulture),
                    A = ParseElementValue(orbit, "a"),
                    Eccentricity = ParseElementValue(orbit, "e"),
                    InclinationDeg = ParseElementValue(orbit, "i"),
                    NodeDeg = ParseElementValue(orbit, "om"),
                    ArgPeriDeg = ParseElementValue(orbit, "w"),
                    MeanAnomalyDeg = ParseElementValue(orbit, "ma"),
                    H = h,
                    G = g,
                };
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: failed fetching elements for {target.Name} from JPL: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns the best available asteroid elements: a still-fresh cache, or a live refetch
        /// when stale. Per-object resilient -- if one asteroid's own JPL request fails, the
        /// previous cached value for just that object is kept rather than dropping it from the
        /// whole list (mirrors CometOrbits' own "stale beats nothing" philosophy, just scoped per
        /// object here instead of per whole-feed, since this is 13 independent requests, not one
        /// file). Only throws when there is truly nothing to fall back to for any object.
        /// </summary>
        public static async Task<IReadOnlyList<AsteroidElements>> FetchAsteroidElementsAsync(HttpClient httpClient, CancellationToken ct = default) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                LoadDiskCacheIfNeeded();
                if (_cache != null && DateTime.UtcNow - _cacheFetchedAtUtc < CacheMaxAge) {
                    return _cache;
                }

                var fresh = new List<AsteroidElements>(Targets.Count);
                var anySucceeded = false;
                foreach (var target in Targets) {
                    var elements = await FetchOneAsync(httpClient, target, ct).ConfigureAwait(false);
                    if (elements != null) {
                        fresh.Add(elements);
                        anySucceeded = true;
                    } else {
                        var previous = _cache?.FirstOrDefault(a => a.Id == target.Id);
                        if (previous != null) fresh.Add(previous);
                    }
                }

                if (anySucceeded) {
                    _cache = fresh;
                    _cacheFetchedAtUtc = DateTime.UtcNow;
                    PersistToDisk(fresh, _cacheFetchedAtUtc);
                    return fresh;
                }

                if (_cache != null) {
                    NINA.Core.Utility.Logger.Warning($"Perihelion: asteroid elements refresh failed entirely, using data from {_cacheFetchedAtUtc:u}");
                    return _cache;
                }

                throw new InvalidOperationException("Could not fetch asteroid elements from JPL and no cached data exists on this install.");
            } finally {
                CacheLock.Release();
            }
        }

        /// <summary>
        /// Explicit "Sync Now" action, matching CometOrbits.SyncNowAsync -- always attempts a
        /// live fetch for every target regardless of cache age, and only reports success when
        /// every single one actually succeeded (unlike the passive path above, an explicit user
        /// action deserves an honest answer, not a silent partial refresh). Leaves the existing
        /// cache alone on failure.
        /// </summary>
        public static async Task<bool> SyncNowAsync(HttpClient httpClient, CancellationToken ct = default) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                var fresh = new List<AsteroidElements>(Targets.Count);
                foreach (var target in Targets) {
                    var elements = await FetchOneAsync(httpClient, target, ct).ConfigureAwait(false);
                    if (elements == null) return false;
                    fresh.Add(elements);
                }
                _cache = fresh;
                _cacheFetchedAtUtc = DateTime.UtcNow;
                PersistToDisk(fresh, _cacheFetchedAtUtc);
                return true;
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
        /// Epoch-staleness guardrail -- pure two-body Keplerian propagation far in time from an
        /// object's own reference epoch accumulates real, unmodeled error from ongoing planetary
        /// perturbation. The live fetch above keeps this bounded under normal operation, but
        /// doesn't guarantee it (a fetch can succeed while simply reporting old data, or every
        /// fetch since some point could have silently been falling back to an aging cache) --
        /// this is a direct, per-object check so that shows up somewhere a user can actually see
        /// it, rather than staying invisible. 180 days is roughly JPL/MPC's own observed
        /// re-osculation cadence for these objects (empirical, from watching the live feed while
        /// building this), not a claim that error becomes unacceptable at exactly that number --
        /// past it, the elements are simply older than they'd normally ever be, which is worth
        /// flagging.
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
