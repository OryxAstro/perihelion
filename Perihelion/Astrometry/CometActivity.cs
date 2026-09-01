using System;
using System.Collections.Generic;
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
        // explicitly calls out as needing to stay fresher than orbital elements do. In-memory
        // only, deliberately NOT disk-persisted like CometOrbits' own elements cache: this is a
        // supplementary cross-check for session planning, not required for the actual
        // tracking-rate math, so losing it on a restart with no connectivity just means falling
        // back to predicted-only magnitude -- a degraded display, not a functional failure.
        private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(2);
        private static readonly Dictionary<string, (CometActivityStatus? Status, DateTime FetchedAtUtc)> _cache = new();
        private static readonly SemaphoreSlim CacheLock = new(1, 1);

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
        /// to the predicted magnitude alone.</summary>
        public static async Task<CometActivityStatus?> FetchAsync(HttpClient httpClient, string name, CancellationToken ct = default) {
            await CacheLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                if (_cache.TryGetValue(name, out var entry) && DateTime.UtcNow - entry.FetchedAtUtc < CacheMaxAge) {
                    return entry.Status;
                }
                var fresh = await FetchUncachedAsync(httpClient, name, ct).ConfigureAwait(false);
                _cache[name] = (fresh, DateTime.UtcNow);
                return fresh;
            } finally {
                CacheLock.Release();
            }
        }
    }
}
