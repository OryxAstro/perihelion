using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Perihelion.Astrometry {

    /// <summary>
    /// Real, current observer-reported brightness for a comet -- a cross-check against
    /// OrbitalTracking's own predicted (H, G model) magnitude, which has no way to know about a
    /// real outburst or under-performance. Ported from OryxAstro's own
    /// server/utils/cometActivity.ts -- that file's own doc comment cites verified real cases:
    /// 10P/Tempel and 220P/McNaught were both observed 4+ magnitudes brighter than the model
    /// predicts during active outbursts, a gap large enough to matter for real session planning.
    /// Sourced from COBS (Comet OBServation database)'s public obs_list.api, which returns real
    /// submitted observations in the 80-column ICQ format. Comet-only -- asteroids have no
    /// equivalent observer-reporting community/format.
    /// </summary>
    public sealed class CometObservation {
        public required DateTime DateUtc { get; init; }
        public required double Magnitude { get; init; }

        /// <summary>Arcminutes -- null when the observer didn't report a coma size.</summary>
        public double? ComaDiameterArcmin { get; init; }

        /// <summary>Arcminutes -- null when not reported.</summary>
        public double? TailLengthArcmin { get; init; }
    }

    public sealed class CometActivityStatus {
        public required CometObservation MostRecent { get; init; }

        /// <summary>Mean of up to the 5 most recent magnitudes -- smooths one noisy observer's
        /// estimate without hiding a genuine, sustained trend.</summary>
        public required double RecentAverageMagnitude { get; init; }

        public required int ObservationCount { get; init; }
    }

    public static class CometActivity {
        private const string CobsObsListUrl = "https://cobs.si/api/obs_list.api";
        private const int RecentSampleSize = 5;

        // Matches OryxAstro's own comet-activity cache window exactly (2h) -- shorter than comet
        // elements' own 6h, since this is "current status" that the website's own comment
        // explicitly calls out as needing to stay fresher than orbital elements do.
        private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(2);
        private static readonly Dictionary<string, (CometActivityStatus? Status, DateTime FetchedAtUtc)> _cache = new();
        private static readonly SemaphoreSlim CacheLock = new(1, 1);

        // Real problem measured on real hardware: this cache used to be in-memory only, so every
        // PINS restart wiped it -- the very next Browse-tab open then had to make a fresh COBS
        // round-trip for every one of up to MaxComets comets (capped at 6 concurrent to stay a
        // reasonable citizen of a third-party API), which measured at ~18 seconds. Disk-persisting
        // it, same pattern as CometOrbits' own comet-elements cache, means a restart only pays
        // that cost once ever (or once per comet, staggered, as each entry's own 2h TTL happens to
        // lapse) rather than on every single restart.
        private static readonly string CacheDirectory = Path.Combine(NINA.Core.Utility.CoreUtil.APPLICATIONTEMPPATH, "PerihelionData");
        private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "comet-activity-cache.json");
        private static bool _diskCacheLoaded = false;

        // When the explicit "Refresh COBS" action last completed a full sweep (see
        // MarkFullRefreshComplete) -- distinct from any individual comet's own FetchedAtUtc above,
        // since those update piecemeal as each comet's own 2h TTL happens to lapse and don't mean
        // much as a single "COBS refreshed X ago" status the way one MPC file fetch does for
        // comet elements. Persisted alongside the per-comet cache so it survives a restart too.
        private static DateTime? _lastFullRefreshUtc;

        private sealed class DiskEntry {
            public CometActivityStatus? Status { get; set; }
            public DateTime FetchedAtUtc { get; set; }
        }

        private sealed class DiskCache {
            public Dictionary<string, DiskEntry> Entries { get; set; } = new();
            public DateTime? LastFullRefreshUtc { get; set; }
        }

        /// <summary>When the explicit "Refresh COBS" action last completed, on this run or a
        /// previous one via the on-disk cache -- null if never explicitly refreshed. Mirrors
        /// CometOrbits.LastSyncedUtc's own "read without needing a full fetch first" shape.</summary>
        public static DateTime? LastFullRefreshUtc => _diskCacheLoaded ? _lastFullRefreshUtc : LoadDiskLastFullRefreshTimestampOnly();

        private static DateTime? LoadDiskLastFullRefreshTimestampOnly() {
            try {
                if (!File.Exists(CacheFilePath)) return null;
                var disk = Newtonsoft.Json.JsonConvert.DeserializeObject<DiskCache>(File.ReadAllText(CacheFilePath));
                return disk?.LastFullRefreshUtc;
            } catch {
                return null;
            }
        }

        /// <summary>Seeds _cache from disk if this is the first call this run -- so a fresh PINS
        /// restart doesn't start from a completely cold cache. Caller must already hold
        /// CacheLock.</summary>
        private static void LoadDiskCacheIfNeeded() {
            if (_diskCacheLoaded) return;
            _diskCacheLoaded = true;
            try {
                if (!File.Exists(CacheFilePath)) return;
                var disk = Newtonsoft.Json.JsonConvert.DeserializeObject<DiskCache>(File.ReadAllText(CacheFilePath));
                if (disk == null) return;
                foreach (var kvp in disk.Entries) {
                    _cache[kvp.Key] = (kvp.Value.Status, kvp.Value.FetchedAtUtc);
                }
                _lastFullRefreshUtc = disk.LastFullRefreshUtc;
            } catch {
                // A corrupt or unreadable cache file just means starting cold, same as a first
                // install -- not worth failing the whole fetch over.
            }
        }

        /// <summary>Caller must already hold CacheLock.</summary>
        private static void PersistToDisk() {
            try {
                Directory.CreateDirectory(CacheDirectory);
                var disk = new DiskCache {
                    Entries = _cache.ToDictionary(kvp => kvp.Key, kvp => new DiskEntry { Status = kvp.Value.Status, FetchedAtUtc = kvp.Value.FetchedAtUtc }),
                    LastFullRefreshUtc = _lastFullRefreshUtc,
                };
                File.WriteAllText(CacheFilePath, Newtonsoft.Json.JsonConvert.SerializeObject(disk));
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Warning($"Perihelion: could not persist comet-activity cache: {ex.Message}");
            }
        }

        /// <summary>Called once after ListBrowseObjectsAsync's own forceRefreshCobs sweep
        /// completes -- marks the moment the explicit "Refresh COBS" action last actually
        /// finished, for the panel's own "COBS refreshed X ago" status line.</summary>
        public static async Task MarkFullRefreshCompleteAsync(CancellationToken ct = default) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                LoadDiskCacheIfNeeded();
                _lastFullRefreshUtc = DateTime.UtcNow;
                PersistToDisk();
            } finally {
                CacheLock.Release();
            }
        }

        private static string ParseFixed(string line, int startCol1, int endCol1) {
            var start = startCol1 - 1;
            var len = Math.Min(endCol1, line.Length) - start;
            if (start >= line.Length || len <= 0) return string.Empty;
            return line.Substring(start, len).Trim();
        }

        /// <summary>
        /// COBS's own `des` query value -- bare short-period number+letter ("114P", "2P", "3I")
        /// or the year-based long-period code with any parenthetical common name dropped
        /// ("C/2024 A1 (ATLAS)" -> "C/2024 A1"). Ported from OryxAstro's toCobsDesignation, which
        /// verified this exact split against live COBS responses.
        /// </summary>
        public static string ToCobsDesignation(string name) {
            var withoutCommonName = Regex.Replace(name, @"\s*\([^)]*\)\s*$", string.Empty).Trim();
            var numbered = Regex.Match(withoutCommonName, @"^(\d+[A-Za-z])/");
            return numbered.Success ? numbered.Groups[1].Value : withoutCommonName;
        }

        /// <summary>
        /// Parses one 80-column ICQ observation line. Returns null for a blank, malformed, or
        /// magnitude-less line rather than throwing -- matches ParseCometElementsLine's own
        /// tolerance in CometOrbits.cs, since a single bad row from a third party shouldn't take
        /// down the whole fetch.
        /// </summary>
        public static CometObservation? ParseIcqObservationLine(string line) {
            if (line.Trim().Length < 33) return null;
            try {
                var dateField = ParseFixed(line, 12, 24);
                var dateMatch = Regex.Match(dateField, @"^(\d{4})\s+(\d{2})\s+(\d{1,2}(?:\.\d+)?)$");
                if (!dateMatch.Success) return null;
                var year = int.Parse(dateMatch.Groups[1].Value);
                var month = int.Parse(dateMatch.Groups[2].Value);
                var dayValue = double.Parse(dateMatch.Groups[3].Value);
                var day = (int)Math.Floor(dayValue);
                var date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc)
                    .AddMilliseconds((dayValue - day) * 86400000);

                var magRaw = Regex.Replace(ParseFixed(line, 28, 33), "[^0-9.]", string.Empty);
                if (!double.TryParse(magRaw, out var magnitude)) return null;

                var comaRaw = Regex.Replace(ParseFixed(line, 49, 54), "[^0-9.]", string.Empty);
                double? comaDiameterArcmin = double.TryParse(comaRaw, out var coma) ? coma : null;

                // The raw field can be in degrees or arcmin -- a trailing "m" marks arcmin;
                // converted here so callers only ever deal with one unit.
                var tailField = ParseFixed(line, 59, 64);
                var tailRaw = Regex.Replace(tailField, "[^0-9.]", string.Empty);
                double? tailLengthArcmin = null;
                if (double.TryParse(tailRaw, out var tailValue)) {
                    tailLengthArcmin = Regex.IsMatch(tailField, "m", RegexOptions.IgnoreCase) ? tailValue : tailValue * 60;
                }

                return new CometObservation {
                    DateUtc = date,
                    Magnitude = magnitude,
                    ComaDiameterArcmin = comaDiameterArcmin,
                    TailLengthArcmin = tailLengthArcmin,
                };
            } catch {
                return null;
            }
        }

        private static async Task<CometActivityStatus?> FetchUncachedAsync(HttpClient httpClient, string name, CancellationToken ct) {
            try {
                var designation = ToCobsDesignation(name);
                var url = $"{CobsObsListUrl}?des={Uri.EscapeDataString(designation)}";
                var text = await httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
                var observations = new List<CometObservation>();
                foreach (var line in text.Split('\n')) {
                    var parsed = ParseIcqObservationLine(line);
                    if (parsed != null) observations.Add(parsed);
                }
                if (observations.Count == 0) return null;

                // COBS returned newest-first in every sample checked while porting this, but sort
                // explicitly rather than trust that ordering holds for every object.
                observations.Sort((a, b) => b.DateUtc.CompareTo(a.DateUtc));
                var recent = observations.Take(RecentSampleSize).ToList();

                return new CometActivityStatus {
                    MostRecent = observations[0],
                    RecentAverageMagnitude = recent.Average(o => o.Magnitude),
                    ObservationCount = observations.Count,
                };
            } catch {
                return null;
            }
        }

        /// <summary>Null when COBS has no observations for this comet, or the fetch failed --
        /// callers should treat that as "no cross-check available", not an error, and fall back
        /// to the predicted magnitude alone. <paramref name="forceRefresh"/> bypasses the 2h TTL
        /// for an explicit "Refresh COBS" action (see ListBrowseObjectsAsync's own
        /// forceRefreshCobs parameter) -- an explicit user action deserves today's real numbers,
        /// not whatever happened to already be cached.</summary>
        public static async Task<CometActivityStatus?> FetchAsync(HttpClient httpClient, string name, CancellationToken ct = default, bool forceRefresh = false) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                LoadDiskCacheIfNeeded();
                if (!forceRefresh && _cache.TryGetValue(name, out var entry) && DateTime.UtcNow - entry.FetchedAtUtc < CacheMaxAge) {
                    return entry.Status;
                }
                var fresh = await FetchUncachedAsync(httpClient, name, ct).ConfigureAwait(false);
                // FetchUncachedAsync collapses "genuinely no COBS data" and "the fetch failed"
                // into the same null -- can't tell them apart here. That's fine for a comet
                // that's never had a cached entry (cache the null so a data-less comet doesn't
                // hammer COBS on every call), but overwriting an EXISTING known-good status with
                // null on every transient network blip would mean a bad moment on COBS' end (or
                // this box's own connectivity) quietly erases real data from disk -- exactly the
                // resilience this cache is supposed to buy. So a null result only overwrites when
                // there was nothing worth keeping; otherwise the stale-but-real status rides
                // another cycle and the next request just tries again.
                var hadGoodEntry = _cache.TryGetValue(name, out var existing) && existing.Status != null;
                if (fresh != null || !hadGoodEntry) {
                    _cache[name] = (fresh, DateTime.UtcNow);
                    PersistToDisk();
                    return fresh;
                }
                return existing.Status;
            } finally {
                CacheLock.Release();
            }
        }
    }
}
