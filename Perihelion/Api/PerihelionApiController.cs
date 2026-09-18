using CosineKitty;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using Perihelion.Astrometry;
using Perihelion.Sequencing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Perihelion.Api {

    internal class TrackRequest {
        [JsonProperty]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public OrbitalObjectType ObjectType { get; set; }

        [JsonProperty]
        public string TargetName { get; set; } = string.Empty;

        /// <summary>Also apply the guider shift rate (see SetPerihelionGuiderShiftRate) -- needed whenever the mount is guided.</summary>
        [JsonProperty]
        public bool Guiding { get; set; }

        /// <summary>Null/omitted/&lt;=0 disables it. Otherwise, re-applies the rate on this interval -- see QuickTrackReapply.</summary>
        [JsonProperty]
        public int? AutoReapplySeconds { get; set; }
    }

    internal class TrackResponse {
        [JsonProperty]
        public bool Success { get; set; }

        [JsonProperty]
        public string Message { get; set; } = string.Empty;
    }

    internal class QuickTrackStatusResponse {
        [JsonProperty]
        public bool Active { get; set; }

        [JsonProperty]
        public string? ObjectType { get; set; }

        [JsonProperty]
        public string? TargetName { get; set; }

        [JsonProperty]
        public bool Guiding { get; set; }

        [JsonProperty]
        public int? AutoReapplySeconds { get; set; }

        [JsonProperty]
        public DateTime? StartedUtc { get; set; }

        [JsonProperty]
        public DateTime? LastAppliedUtc { get; set; }

        [JsonProperty]
        public double? LastRaArcsecPerSec { get; set; }

        [JsonProperty]
        public double? LastDecArcsecPerSec { get; set; }

        [JsonProperty]
        public bool LastApplySucceeded { get; set; }

        [JsonProperty]
        public string? LastError { get; set; }

        /// <summary>Null for a plain manual stop -- set when Quick Track stopped itself, in
        /// particular the meridian safety cutoff (see QuickTrackReapply's own CheckMeridian).</summary>
        [JsonProperty]
        public string? StopReason { get; set; }

        /// <summary>Null when guiding is off, or its last attempt succeeded. Independent of
        /// LastApplySucceeded/LastError -- see QuickTrackStatus.GuidingFailed's own doc comment
        /// for why a guiding hiccup is tracked separately from the mount's own tracking-rate
        /// application, rather than making the whole attempt read as failed.</summary>
        [JsonProperty]
        public string? GuidingError { get; set; }

        /// <summary>True when the mount's own driver doesn't support a custom tracking rate at
        /// all, and the guider's own shift rate is the entire tracking mechanism for this
        /// session instead of a companion to a base-rate change -- the mount itself is still on
        /// plain sidereal. See QuickTrackStatus.SetGuidingOnlyFallback's own doc comment.</summary>
        [JsonProperty]
        public bool GuidingOnlyFallback { get; set; }
    }

    internal class PathPointResponse {
        [JsonProperty]
        public string Date { get; set; } = string.Empty;

        [JsonProperty]
        public double RaHours { get; set; }

        [JsonProperty]
        public double DecDeg { get; set; }
    }

    internal class RateResponse {
        [JsonProperty]
        public double RaArcsecPerSec { get; set; }

        [JsonProperty]
        public double DecArcsecPerSec { get; set; }

        /// <summary>Null when the active profile's CameraSettings.PixelSize/TelescopeSettings.
        /// FocalLength aren't fully configured -- same "can't compute a number, don't fake
        /// one" convention as the Windows panel's own MaxExposureText.</summary>
        [JsonProperty]
        public double? MaxExposureSeconds { get; set; }
    }

    internal class SettingsResponse {
        [JsonProperty]
        public bool EqmodRaRateCorrection { get; set; }

        [JsonProperty]
        public int QuickTrackReapplyIntervalSeconds { get; set; }

        [JsonProperty]
        public double CometMagnitudeThreshold { get; set; }

        [JsonProperty]
        public int MaxComets { get; set; }

        [JsonProperty]
        public double AsteroidMagnitudeThreshold { get; set; }

        [JsonProperty]
        public int MaxAsteroids { get; set; }
    }

    internal class SyncStatusResponse {
        [JsonProperty]
        public DateTime? CometsLastSyncedUtc { get; set; }

        [JsonProperty]
        public DateTime? AsteroidsLastSyncedUtc { get; set; }

        [JsonProperty]
        public DateTime? CobsLastRefreshedUtc { get; set; }

        [JsonProperty]
        public int CometsCachedCount { get; set; }

        [JsonProperty]
        public int AsteroidsCachedCount { get; set; }

        [JsonProperty]
        public int CobsCachedCount { get; set; }
    }

    internal class SyncResponse {
        [JsonProperty]
        public bool Success { get; set; }

        [JsonProperty]
        public string Message { get; set; } = string.Empty;

        [JsonProperty]
        public DateTime? CometsLastSyncedUtc { get; set; }

        [JsonProperty]
        public DateTime? AsteroidsLastSyncedUtc { get; set; }
    }

    internal class AddToSequenceExposureRequest {
        [JsonProperty]
        public string? FilterName { get; set; }

        [JsonProperty]
        public double ExposureSeconds { get; set; }

        [JsonProperty]
        public int FrameCount { get; set; } = 1;
    }

    internal class AddToSequenceRequest {
        [JsonProperty]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public OrbitalObjectType ObjectType { get; set; }

        [JsonProperty]
        public string TargetName { get; set; } = string.Empty;

        [JsonProperty]
        public double RaHours { get; set; }

        [JsonProperty]
        public double DecDeg { get; set; }

        /// <summary>Null means slew to RaHours/DecDeg exactly.</summary>
        [JsonProperty]
        public double? FrameOffsetRaDeg { get; set; }

        [JsonProperty]
        public double? FrameOffsetDecDeg { get; set; }

        /// <summary>Null means plain Center; CenterAndRotate needs a rotator.</summary>
        [JsonProperty]
        public double? RotationAngle { get; set; }

        [JsonProperty]
        public bool Guiding { get; set; }

        [JsonProperty]
        public bool MeridianFlip { get; set; }

        [JsonProperty]
        public double? AutofocusMinutes { get; set; }

        [JsonProperty]
        public AddToSequenceExposureRequest Exposure { get; set; } = new();
    }

    internal class AddToSequenceResponse {
        [JsonProperty]
        public bool Success { get; set; }

        [JsonProperty]
        public string Message { get; set; } = string.Empty;
    }

    internal class ImportResponse {
        [JsonProperty]
        public bool Success { get; set; }

        [JsonProperty]
        public string Message { get; set; } = string.Empty;

        [JsonProperty]
        public int Count { get; set; }
    }

    internal class CometActivityResponse {
        [JsonProperty]
        public bool Available { get; set; }

        [JsonProperty]
        public DateTime? MostRecentDateUtc { get; set; }

        [JsonProperty]
        public double? MostRecentMagnitude { get; set; }

        [JsonProperty]
        public double? RecentAverageMagnitude { get; set; }

        [JsonProperty]
        public int ObservationCount { get; set; }
    }

    internal class BrowseObjectResponse {
        [JsonProperty]
        public string Id { get; set; } = string.Empty;

        [JsonProperty]
        public string Name { get; set; } = string.Empty;

        [JsonProperty]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public OrbitalObjectType ObjectType { get; set; }

        [JsonProperty]
        public double? Magnitude { get; set; }

        [JsonProperty]
        public double? ObservedMagnitude { get; set; }

        [JsonProperty]
        public double? ObservedAverageMagnitude { get; set; }

        [JsonProperty]
        public double RaHours { get; set; }

        [JsonProperty]
        public double DecDeg { get; set; }

        [JsonProperty]
        public double SunDistanceAu { get; set; }

        [JsonProperty]
        public double EarthDistanceAu { get; set; }

        [JsonProperty]
        public double SolarElongationDeg { get; set; }

        [JsonProperty]
        public string ConstellationName { get; set; } = string.Empty;

        [JsonProperty]
        public DateTime? PerihelionDateUtc { get; set; }

        [JsonProperty]
        public double EpochAgeDays { get; set; }

        [JsonProperty]
        public bool IsEpochStale { get; set; }
    }

    /// <summary>
    /// Perihelion's own tiny HTTP API, independent of ninaAPI -- exists specifically for
    /// "Quick Track": running SetPerihelionTrackingRate (and optionally
    /// SetPerihelionGuiderShiftRate) directly, right now, without going through NINA's
    /// Advanced Sequencer at all (which would otherwise mean replacing whatever sequence the
    /// user currently has loaded). Constructs the sequence item classes directly rather than
    /// via MEF/ISequencerFactory -- Perihelion already knows its own concrete types, no need
    /// for the reflection ninaAPI's own /sequence/load path uses to resolve an arbitrary tree.
    /// </summary>
    public class PerihelionApiController : WebApiController {
        // Set once by PerihelionApiServer.Start() before the server begins accepting requests.
        internal static ITelescopeMediator? TelescopeMediator;
        internal static IGuiderMediator? GuiderMediator;
        internal static IProfileService? ProfileService;
        internal static string? ApiToken;
        internal static DateTime PairingDeadlineUtc;

        // One shared HttpClient across the whole plugin (PerihelionHttpClient.cs).
        private static readonly HttpClient HttpClient = PerihelionHttpClient.Instance;

        /// <summary>
        /// Every bright asteroid plus every comet in the current MPC feed worth showing, each
        /// with today's magnitude/RA/Dec -- backs the Touch-N-Stars panel's Browse tab.
        /// The panel is a thin client of this computation, not a second implementation of the
        /// same orbital math in JavaScript (see CLAUDE.md's "Quick Track" architecture section
        /// for the fuller reasoning -- the panel and this plugin run on the same Pi, so there's
        /// no internet-round-trip argument for duplicating it client-side).
        /// </summary>
        [Route(HttpVerbs.Get, "/objects")]
        public async Task ListObjects() {
            try {
                var objects = await OrbitalTracking.ListBrowseObjectsAsync(HttpClient, DateTime.UtcNow, HttpContext.CancellationToken);
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(BuildBrowseObjectResponses(objects)), "application/json", Encoding.UTF8);
            } catch (Exception ex) {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = ex.Message }), "application/json", Encoding.UTF8);
            }
        }

        private static List<BrowseObjectResponse> BuildBrowseObjectResponses(IReadOnlyList<BrowseObject> objects) {
            var response = new List<BrowseObjectResponse>(objects.Count);
            foreach (var o in objects) {
                response.Add(new BrowseObjectResponse {
                    Id = o.Id,
                    Name = o.Name,
                    ObjectType = o.ObjectType,
                    Magnitude = o.Magnitude,
                    ObservedMagnitude = o.ObservedMagnitude,
                    ObservedAverageMagnitude = o.ObservedAverageMagnitude,
                    RaHours = o.RaHours,
                    DecDeg = o.DecDeg,
                    SunDistanceAu = o.SunDistanceAu,
                    EarthDistanceAu = o.EarthDistanceAu,
                    SolarElongationDeg = o.SolarElongationDeg,
                    ConstellationName = o.ConstellationName,
                    PerihelionDateUtc = o.PerihelionDateUtc,
                    EpochAgeDays = o.EpochAgeDays,
                    IsEpochStale = o.IsEpochStale,
                });
            }
            return response;
        }

        /// <summary>
        /// Explicit "Refresh COBS" action -- bypasses CometActivity's own 2h TTL for every comet
        /// currently in the list, so a user who wants today's observed-brightness numbers
        /// right now can get them without waiting for each comet's own cache to lapse naturally.
        /// Deliberately separate from /sync/comets (comet orbital elements): that's a single,
        /// fast MPC file fetch, while this is a full COBS round-trip per comet -- the same
        /// several-seconds-to-tens-of-seconds cost that disk-persisting CometActivity's cache
        /// exists to keep off the normal /objects load path, so it stays an explicit, separate
        /// action rather than riding along with Sync Now. Returns the same shape as GET /objects
        /// so the panel can just replace its list from this response directly.
        /// </summary>
        [Route(HttpVerbs.Post, "/objects/refresh-cobs")]
        public async Task RefreshCobs() {
            try {
                var objects = await OrbitalTracking.ListBrowseObjectsAsync(HttpClient, DateTime.UtcNow, HttpContext.CancellationToken, includeCobs: true, forceRefreshCobs: true);
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(BuildBrowseObjectResponses(objects)), "application/json", Encoding.UTF8);
            } catch (Exception ex) {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = ex.Message }), "application/json", Encoding.UTF8);
            }
        }

        /// <summary>
        /// When comet data was last actually fetched from MPC (on this run or a previous one, via
        /// the on-disk cache) -- null if never synced at all. Backs the panel's "last synced: X
        /// ago" indicator.
        /// </summary>
        [Route(HttpVerbs.Get, "/sync/status")]
        public async Task SyncStatus() {
            var json = JsonConvert.SerializeObject(new SyncStatusResponse {
                CometsLastSyncedUtc = CometOrbits.LastSyncedUtc,
                AsteroidsLastSyncedUtc = AsteroidOrbits.LastSyncedUtc,
                CobsLastRefreshedUtc = CometActivity.LastFullRefreshUtc,
                CometsCachedCount = CometOrbits.CachedCount,
                AsteroidsCachedCount = AsteroidOrbits.CachedCount,
                CobsCachedCount = CometActivity.CachedCount,
            });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// Explicit "download comets now" action -- the deliberate "do this while I still have a
        /// connection, before heading to the dark site" step. Unlike ListObjects/Track's own
        /// passive stale-cache fallback, this always attempts a live fetch and reports whether it
        /// actually worked, since a user pressing a sync button deserves an answer.
        /// </summary>
        [Route(HttpVerbs.Post, "/sync/comets")]
        public async Task SyncComets() {
            var success = await CometOrbits.SyncNowAsync(HttpClient, HttpContext.CancellationToken);
            var response = new SyncResponse {
                Success = success,
                Message = success ? "Comet elements synced" : "Sync failed -- check the connection and try again",
                CometsLastSyncedUtc = CometOrbits.LastSyncedUtc,
            };
            var json = JsonConvert.SerializeObject(response);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// Same explicit-sync contract as /sync/comets, for the (much smaller, much slower to
        /// change) curated asteroid list -- fetches current elements for every tracked asteroid
        /// from JPL's Small-Body Database right now, regardless of the passive 24h cache window.
        /// </summary>
        [Route(HttpVerbs.Post, "/sync/asteroids")]
        public async Task SyncAsteroids() {
            var success = await AsteroidOrbits.SyncNowAsync(HttpClient, HttpContext.CancellationToken);
            var response = new SyncResponse {
                Success = success,
                Message = success ? "Asteroid elements synced" : "Sync failed -- check the connection and try again",
                AsteroidsLastSyncedUtc = AsteroidOrbits.LastSyncedUtc,
            };
            var json = JsonConvert.SerializeObject(response);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// Accepts a plain MPC CometEls.txt body directly (the same format the live sync fetches
        /// and Export below produces) -- lets Touch-N-Stars offer the same "distribute a synced
        /// file to an install behind a blocked network" workflow the Windows dockable panel
        /// already has via a local file dialog, since a browser has no equivalent file-path API
        /// and instead hands over whatever a file picker/paste read as text.
        /// </summary>
        [Route(HttpVerbs.Post, "/import/comets")]
        public Task ImportComets() => RunImport(CometOrbits.ImportFromTextAsync, "comet(s)");

        /// <summary>
        /// Returns the currently cached comet elements as the exact raw MPC CometEls.txt they
        /// were parsed from -- a convenience re-share, not a requirement, since Import above
        /// already accepts MPC's own file directly. Always 200 (an empty body means nothing has
        /// ever been synced) -- a non-2xx status here gets its response body discarded by
        /// Touch-N-Stars' own global axios error interceptor, which replaces it with a generic
        /// message before this route's caller ever sees it.
        /// </summary>
        [Route(HttpVerbs.Get, "/export/comets")]
        public async Task ExportComets() {
            var rawText = await CometOrbits.ExportToTextAsync(HttpContext.CancellationToken);
            await HttpContext.SendStringAsync(rawText ?? string.Empty, "text/plain", Encoding.UTF8);
        }

        /// <summary>
        /// Wipes both the in-memory and on-disk comet cache -- the next lookup does a full live
        /// fetch instead of using anything currently held.
        /// </summary>
        [Route(HttpVerbs.Post, "/clear/comets")]
        public Task ClearComets() => RunClear(CometOrbits.ClearAsync, "Comet cache cleared");

        /// <summary>
        /// Accepts Perihelion's own asteroid-elements JSON directly (the same format Export below
        /// produces) -- see AsteroidOrbits.ImportFromTextAsync's own doc comment for why this uses
        /// Perihelion's own format rather than an MPC-compatible one (no universal third-party
        /// bulk format exists for asteroid elements the way CometEls.txt does for comets).
        /// </summary>
        [Route(HttpVerbs.Post, "/import/asteroids")]
        public Task ImportAsteroids() => RunImport(AsteroidOrbits.ImportFromTextAsync, "asteroid(s)");

        /// <summary>
        /// Returns the currently cached asteroid elements as Perihelion's own JSON list -- always
        /// 200 (an empty body means nothing has ever been synced), same reasoning as
        /// ExportComets's own doc comment.
        /// </summary>
        [Route(HttpVerbs.Get, "/export/asteroids")]
        public async Task ExportAsteroids() {
            var json = await AsteroidOrbits.ExportToTextAsync(HttpContext.CancellationToken);
            await HttpContext.SendStringAsync(json ?? string.Empty, "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// Wipes both the in-memory and on-disk asteroid cache -- the next lookup does a full
        /// live re-fetch from JPL at the currently configured threshold.
        /// </summary>
        [Route(HttpVerbs.Post, "/clear/asteroids")]
        public Task ClearAsteroids() => RunClear(AsteroidOrbits.ClearAsync, "Asteroid cache cleared");

        /// <summary>Wipes the COBS observed-brightness cache -- no Import/Export for this one,
        /// unlike comets/asteroids: it's a per-comet, on-demand cache with a 2h TTL, not a
        /// distributable bulk dataset. Clear alone covers "reset a corrupt cache."</summary>
        [Route(HttpVerbs.Post, "/clear/cobs")]
        public Task ClearCobs() => RunClear(CometActivity.ClearAsync, "COBS cache cleared");

        /// <summary>Shared body for every Import route -- always 200, even on failure, so
        /// Touch-N-Stars' own global axios interceptor never discards the Message (see
        /// ExportComets's own doc comment for the full reasoning).</summary>
        private async Task RunImport(Func<string, CancellationToken, Task<int>> importFn, string label) {
            var response = new ImportResponse();
            try {
                var body = await HttpContext.GetRequestBodyAsStringAsync();
                response.Count = await importFn(body, HttpContext.CancellationToken);
                response.Success = true;
                response.Message = $"Imported {response.Count} {label}";
            } catch (Exception ex) {
                response.Message = ex.Message;
            }
            await HttpContext.SendStringAsync(JsonConvert.SerializeObject(response), "application/json", Encoding.UTF8);
        }

        /// <summary>Shared body for every Clear route.</summary>
        private async Task RunClear(Func<CancellationToken, Task> clearFn, string successMessage) {
            await clearFn(HttpContext.CancellationToken);
            var json = JsonConvert.SerializeObject(new { Success = true, Message = successMessage });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// One position per day for the requested number of nights -- backs the Position &amp;
        /// Path tab's finder-chart plot (the object's path against the fixed stars, not
        /// movement within a tracked frame).
        /// </summary>
        [Route(HttpVerbs.Get, "/objects/path")]
        public async Task GetPath([QueryField] string objectType, [QueryField] string targetName, [QueryField] int days) {
            try {
                if (!Enum.TryParse<OrbitalObjectType>(objectType, ignoreCase: true, out var type)) {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = $"Unknown objectType '{objectType}'" }), "application/json", Encoding.UTF8);
                    return;
                }
                var effectiveDays = days > 0 ? days : 10;

                // Full precision, not .Date (midnight UTC) -- day 0 has to be the same reference
                // instant as /objects' own current-position computation (also DateTime.UtcNow), or
                // the framing view's "Tonight" path point silently drifts away from the object's
                // true live position by however many hours have passed since midnight (a bug:
                // for a fast-moving comet this can be a large enough offset to land outside the
                // framing view entirely, even though both endpoints are describing "now"). The
                // displayed date label is unaffected -- PathPointResponse.Date is formatted
                // "yyyy-MM-dd" below regardless of the time-of-day carried on each point.
                var points = await OrbitalTracking.ComputeOrbitalPathAsync(HttpClient, type, targetName, DateTime.UtcNow, effectiveDays, HttpContext.CancellationToken);
                if (points == null) {
                    HttpContext.Response.StatusCode = 404;
                    await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = $"Could not find {type} '{targetName}'" }), "application/json", Encoding.UTF8);
                    return;
                }

                var response = new List<PathPointResponse>(points.Count);
                foreach (var p in points) {
                    response.Add(new PathPointResponse { Date = p.date.ToString("yyyy-MM-dd"), RaHours = p.raHours, DecDeg = p.decDeg });
                }
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(response), "application/json", Encoding.UTF8);
            } catch (Exception ex) {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = ex.Message }), "application/json", Encoding.UTF8);
            }
        }

        /// <summary>
        /// Current RA/Dec rate for one target, plus the derived "seconds until a 1px drift
        /// relative to the background stars" figure using this profile's own
        /// CameraSettings.PixelSize/TelescopeSettings.FocalLength -- the same numbers and
        /// formula the native Windows panel's own Position section shows on Load
        /// (PerihelionDockableVM.RateText/MaxExposureText). Deliberately its own route mirroring
        /// /objects/path, not folded into /objects' own bright-object list -- computing this for
        /// all 30-ish browse objects on every load would be wasted work for the ones never
        /// actually selected.
        /// </summary>
        [Route(HttpVerbs.Get, "/objects/rate")]
        public async Task GetRate([QueryField] string objectType, [QueryField] string targetName) {
            try {
                if (!Enum.TryParse<OrbitalObjectType>(objectType, ignoreCase: true, out var type)) {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = $"Unknown objectType '{objectType}'" }), "application/json", Encoding.UTF8);
                    return;
                }

                var profile = ProfileService?.ActiveProfile;
                Observer? observer = profile == null
                    ? null
                    : new Observer(profile.AstrometrySettings.Latitude, profile.AstrometrySettings.Longitude, profile.AstrometrySettings.Elevation);

                var rate = await OrbitalTracking.ComputeOrbitalRateAsync(HttpClient, type, targetName, DateTime.UtcNow, HttpContext.CancellationToken, observer);
                if (rate == null) {
                    HttpContext.Response.StatusCode = 404;
                    await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = $"Could not find {type} '{targetName}'" }), "application/json", Encoding.UTF8);
                    return;
                }

                double? maxExposureSeconds = null;
                if (profile != null) {
                    var pixelScale = AstroUtil.ArcsecPerPixel(profile.CameraSettings.PixelSize, profile.TelescopeSettings.FocalLength);
                    var totalRate = Math.Sqrt(rate.Value.RaArcsecPerSec * rate.Value.RaArcsecPerSec + rate.Value.DecArcsecPerSec * rate.Value.DecArcsecPerSec);
                    maxExposureSeconds = totalRate > 0 ? pixelScale / totalRate : (double?)null;
                }

                var response = new RateResponse {
                    RaArcsecPerSec = rate.Value.RaArcsecPerSec,
                    DecArcsecPerSec = rate.Value.DecArcsecPerSec,
                    MaxExposureSeconds = maxExposureSeconds,
                };
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(response), "application/json", Encoding.UTF8);
            } catch (Exception ex) {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = ex.Message }), "application/json", Encoding.UTF8);
            }
        }

        /// <summary>
        /// Current, observer-reported "last seen" brightness for a comet, as a cross-check against
        /// the predicted (H, G model) magnitude already in the /objects list -- see
        /// CometActivity.cs's own doc comment for verified cases where the two disagreed by
        /// 4+ magnitudes. Comet-only, so there's no objectType param; asteroids have no COBS
        /// equivalent. Available: false (not a 404) when COBS simply has nothing for this comet,
        /// or the fetch failed -- that's a normal, expected case for most comets, not an error.
        /// </summary>
        [Route(HttpVerbs.Get, "/objects/activity")]
        public async Task GetActivity([QueryField] string targetName) {
            try {
                var status = await CometActivity.FetchAsync(HttpClient, targetName, HttpContext.CancellationToken);
                var response = status == null
                    ? new CometActivityResponse { Available = false }
                    : new CometActivityResponse {
                        Available = true,
                        MostRecentDateUtc = status.MostRecent.DateUtc,
                        MostRecentMagnitude = status.MostRecent.Magnitude,
                        RecentAverageMagnitude = status.RecentAverageMagnitude,
                        ObservationCount = status.ObservationCount,
                    };
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(response), "application/json", Encoding.UTF8);
            } catch (Exception ex) {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = ex.Message }), "application/json", Encoding.UTF8);
            }
        }

        [Route(HttpVerbs.Post, "/track")]
        public async Task Track() {
            var response = new TrackResponse();
            try {
                var body = await HttpContext.GetRequestBodyAsStringAsync();
                var request = JsonConvert.DeserializeObject<TrackRequest>(body) ?? new TrackRequest();

                if (TelescopeMediator == null) {
                    response.Message = "Perihelion API server started before the telescope mediator was available";
                } else {
                    var result = await QuickTrackEngine.StartAsync(
                        TelescopeMediator, GuiderMediator, ProfileService!,
                        request.ObjectType, request.TargetName, request.Guiding, request.AutoReapplySeconds,
                        HttpContext.CancellationToken);
                    response.Success = result.Success;
                    response.Message = result.Message;
                }
            } catch (Exception ex) {
                response.Message = $"Unexpected error: {ex.Message}";
                QuickTrackStatus.Failed(ex.Message);
            }

            var json = JsonConvert.SerializeObject(response);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>Undoes what Quick Track did: back to sidereal tracking, and stops any guider shift.</summary>
        [Route(HttpVerbs.Post, "/stop")]
        public async Task Stop() {
            var result = await QuickTrackEngine.StopAsync(TelescopeMediator, GuiderMediator, HttpContext.CancellationToken);
            var response = new TrackResponse { Success = result.Success, Message = result.Message };
            var json = JsonConvert.SerializeObject(response);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>Appends one target to the loaded Advanced Sequence via ISequenceMediator.AddAdvancedTarget -- unlike ninaAPI's POST /sequence/load, this never replaces it.</summary>
        [Route(HttpVerbs.Post, "/sequence/add-target")]
        public async Task AddTargetToSequence() {
            var response = new AddToSequenceResponse();
            try {
                var body = await HttpContext.GetRequestBodyAsStringAsync();
                var request = JsonConvert.DeserializeObject<AddToSequenceRequest>(body) ?? new AddToSequenceRequest();

                var sequenceMediator = PerihelionPlugin.SequenceMediator;
                if (sequenceMediator == null) {
                    response.Message = "Perihelion: sequencer not available yet.";
                } else if (!sequenceMediator.Initialized) {
                    response.Message = "Perihelion: Advanced Sequencer not started yet.";
                } else {
                    var factory = PerihelionSequenceBuilder.ResolveFactory(sequenceMediator);
                    if (factory == null) {
                        response.Message = "Perihelion: could not reach the sequencer's item factory.";
                        Logger.Error("Perihelion: AddTargetToSequence -- ResolveFactory returned null with Initialized true");
                    } else {
                        var trueCoordinates = new Coordinates(request.RaHours, request.DecDeg, Epoch.J2000, Coordinates.RAType.Hours);
                        // FrameOffsetRaDeg/DecDeg are a delta from RaHours/DecDeg (see their own
                        // doc comment: "null means slew to RaHours/DecDeg exactly"), not an
                        // absolute position on their own -- this previously discarded
                        // trueCoordinates entirely whenever an offset was supplied, slewing to a
                        // position near the tiny offset value itself (e.g. a few arcminutes from
                        // RA=0h/Dec=0) instead of near the actual target.
                        var slewCoordinates = request.FrameOffsetRaDeg is double offsetRa && request.FrameOffsetDecDeg is double offsetDec
                            ? new Coordinates(trueCoordinates.RA + offsetRa / 15.0, trueCoordinates.Dec + offsetDec, Epoch.J2000, Coordinates.RAType.Hours)
                            : trueCoordinates;

                        NINA.Core.Model.Equipment.FilterInfo? filter = null;
                        if (!string.IsNullOrEmpty(request.Exposure.FilterName)) {
                            filter = ProfileService?.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters
                                ?.FirstOrDefault(f => f.Name == request.Exposure.FilterName);
                        }

                        var container = PerihelionSequenceBuilder.BuildTargetContainer(
                            factory,
                            request.ObjectType,
                            request.TargetName,
                            trueCoordinates,
                            slewCoordinates,
                            request.Guiding,
                            request.RotationAngle,
                            request.AutofocusMinutes,
                            new PerihelionSequenceBuilder.ExposureSettings(filter, request.Exposure.ExposureSeconds, request.Exposure.FrameCount));

                        sequenceMediator.AddAdvancedTarget(container);

                        if (request.MeridianFlip) {
                            var root = PerihelionSequenceBuilder.ResolveSequenceRoot(sequenceMediator);
                            if (root != null) {
                                PerihelionSequenceBuilder.EnsureGlobalMeridianFlipTrigger(factory, root);
                            } else {
                                Logger.Warning("Perihelion: added target to sequence, but could not reach the sequence root for the global Meridian Flip trigger");
                            }
                        }

                        response.Success = true;
                        response.Message = $"Added {request.TargetName} to the Advanced Sequencer.";
                    }
                }
            } catch (Exception ex) {
                response.Message = $"Unexpected error: {ex.Message}";
                Logger.Error("Perihelion: AddTargetToSequence failed", ex);
            }

            var json = JsonConvert.SerializeObject(response);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// Hands out ApiToken to whichever client asks first, then refuses everyone after --
        /// deliberately the only unauthenticated route (see PerihelionAuthModule's own
        /// exemption), so Touch-N-Stars can pair with a fresh install with nothing typed in by
        /// hand. A second device gets the token by having it typed in manually instead (visible
        /// on the first device's own Settings tab, or the Windows Options page). Only open for
        /// PerihelionApiServer.PairingWindow after each startup/Regenerate/Clear -- narrows how
        /// long a stranger on the same network could race the legitimate user to claim it.
        /// </summary>
        private static readonly object PairLock = new object();

        [Route(HttpVerbs.Post, "/pair")]
        public Task Pair() {
            var plugin = PerihelionPlugin.Instance;
            if (plugin == null) {
                return HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Plugin not ready yet" }), "application/json", Encoding.UTF8);
            }

            string? token = null;
            string? failureMessage = null;
            lock (PairLock) {
                if (string.IsNullOrEmpty(plugin.ApiToken)) {
                    failureMessage = "No token configured -- use Regenerate on Windows, or restart PINS to generate one";
                } else if (DateTime.UtcNow > PairingDeadlineUtc) {
                    failureMessage = "Pairing window has closed -- restart the plugin (or use Regenerate on Windows) to reopen it";
                } else if (plugin.ApiTokenClaimed) {
                    failureMessage = "Already paired with another client";
                } else {
                    token = plugin.ApiToken;
                    plugin.ApiTokenClaimed = true;
                }
            }

            var response = new { Success = token != null, Token = token, Message = failureMessage };
            return HttpContext.SendStringAsync(JsonConvert.SerializeObject(response), "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// EqmodRaRateCorrection, QuickTrackReapplyIntervalSeconds, CometMagnitudeThreshold,
        /// MaxComets, AsteroidMagnitudeThreshold, and MaxAsteroids, all persisted via
        /// PerihelionPlugin's own PluginOptionsAccessor -- PINS has no reachable settings UI of
        /// its own (no WPF shell renders there at all), so the Touch-N-Stars panel reads and
        /// writes these through this route instead of the Windows-only Options page. Unlike Port,
        /// all six are read fresh on every use (not baked into a fixed binding at startup), so a
        /// change here takes effect on the very next Quick Track start, tracking-rate
        /// application, or Browse list refresh -- no restart needed.
        /// </summary>
        [Route(HttpVerbs.Get, "/settings")]
        public Task GetSettings() {
            var response = new SettingsResponse {
                EqmodRaRateCorrection = PerihelionPlugin.Instance?.EqmodRaRateCorrection ?? false,
                QuickTrackReapplyIntervalSeconds = PerihelionPlugin.Instance?.QuickTrackReapplyIntervalSeconds ?? 900,
                CometMagnitudeThreshold = PerihelionPlugin.Instance?.CometMagnitudeThreshold ?? 16.0,
                MaxComets = PerihelionPlugin.Instance?.MaxComets ?? 30,
                AsteroidMagnitudeThreshold = PerihelionPlugin.Instance?.AsteroidMagnitudeThreshold ?? 9.0,
                MaxAsteroids = PerihelionPlugin.Instance?.MaxAsteroids ?? 30,
            };
            return HttpContext.SendStringAsync(JsonConvert.SerializeObject(response), "application/json", Encoding.UTF8);
        }

        [Route(HttpVerbs.Post, "/settings")]
        public async Task PostSettings() {
            try {
                var body = await HttpContext.GetRequestBodyAsStringAsync();
                var json = JObject.Parse(body);
                // Partial update -- only touches fields the caller actually included, rather than
                // deserializing into a fully-populated SettingsResponse and writing back all six
                // unconditionally (a bug this replaces: Touch-N-Stars' own saveSettings()
                // only ever sends EqmodRaRateCorrection/QuickTrackReapplyIntervalSeconds, so every
                // call was silently zeroing CometMagnitudeThreshold/MaxComets/
                // AsteroidMagnitudeThreshold/MaxAsteroids to C#'s own numeric default -- and
                // MaxComets/MaxAsteroids = 0 caps the Browse list to nothing regardless of what's
                // actually fetched).
                if (PerihelionPlugin.Instance != null) {
                    if (json.TryGetValue(nameof(SettingsResponse.EqmodRaRateCorrection), out var eqmod)) {
                        PerihelionPlugin.Instance.EqmodRaRateCorrection = eqmod.Value<bool>();
                    }
                    if (json.TryGetValue(nameof(SettingsResponse.QuickTrackReapplyIntervalSeconds), out var reapplySeconds)) {
                        PerihelionPlugin.Instance.QuickTrackReapplyIntervalSeconds = reapplySeconds.Value<int>();
                    }
                    if (json.TryGetValue(nameof(SettingsResponse.CometMagnitudeThreshold), out var cometMag)) {
                        PerihelionPlugin.Instance.CometMagnitudeThreshold = cometMag.Value<double>();
                    }
                    if (json.TryGetValue(nameof(SettingsResponse.MaxComets), out var maxComets)) {
                        PerihelionPlugin.Instance.MaxComets = maxComets.Value<int>();
                    }
                    if (json.TryGetValue(nameof(SettingsResponse.AsteroidMagnitudeThreshold), out var asteroidMag)) {
                        PerihelionPlugin.Instance.AsteroidMagnitudeThreshold = asteroidMag.Value<double>();
                    }
                    if (json.TryGetValue(nameof(SettingsResponse.MaxAsteroids), out var maxAsteroids)) {
                        PerihelionPlugin.Instance.MaxAsteroids = maxAsteroids.Value<int>();
                    }
                }
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Success = true }), "application/json", Encoding.UTF8);
            } catch (Exception ex) {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Success = false, Message = ex.Message }), "application/json", Encoding.UTF8);
            }
        }

        /// <summary>
        /// The actual state of whatever Quick Track session is running -- in particular the
        /// RA/Dec rate last computed and sent, not just whether the toggle was on when the
        /// session started. Backs a live status readout in the Track tab, and is the
        /// unambiguous way to confirm the mount actually received a comet-specific rate rather
        /// than reading tea leaves out of an INDI/ASCOM control panel's own property layout.
        /// </summary>
        [Route(HttpVerbs.Get, "/status")]
        public Task GetStatus() {
            var s = QuickTrackStatus.Current;
            var response = new QuickTrackStatusResponse {
                Active = s.Active,
                ObjectType = s.ObjectType,
                TargetName = s.TargetName,
                Guiding = s.Guiding,
                AutoReapplySeconds = s.AutoReapplySeconds,
                StartedUtc = s.StartedUtc,
                LastAppliedUtc = s.LastAppliedUtc,
                LastRaArcsecPerSec = s.LastRaArcsecPerSec,
                LastDecArcsecPerSec = s.LastDecArcsecPerSec,
                LastApplySucceeded = s.LastApplySucceeded,
                LastError = s.LastError,
                StopReason = s.StopReason,
                GuidingError = s.GuidingError,
                GuidingOnlyFallback = s.GuidingOnlyFallback,
            };
            return HttpContext.SendStringAsync(JsonConvert.SerializeObject(response), "application/json", Encoding.UTF8);
        }
    }
}
