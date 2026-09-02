using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using Perihelion.Astrometry;
using Perihelion.SequenceItems;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
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
        public int? AutoReapplyMinutes { get; set; }
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
        public int? AutoReapplyMinutes { get; set; }

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
    }

    internal class PathPointResponse {
        [JsonProperty]
        public string Date { get; set; } = string.Empty;

        [JsonProperty]
        public double RaHours { get; set; }

        [JsonProperty]
        public double DecDeg { get; set; }
    }

    internal class SyncStatusResponse {
        [JsonProperty]
        public DateTime? CometsLastSyncedUtc { get; set; }
    }

    internal class SyncResponse {
        [JsonProperty]
        public bool Success { get; set; }

        [JsonProperty]
        public string Message { get; set; } = string.Empty;

        [JsonProperty]
        public DateTime? CometsLastSyncedUtc { get; set; }
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
        public double RaHours { get; set; }

        [JsonProperty]
        public double DecDeg { get; set; }
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

        // One shared HttpClient across the whole plugin (PerihelionHttpClient.cs).
        private static readonly HttpClient HttpClient = PerihelionHttpClient.Instance;

        /// <summary>
        /// Every bright asteroid plus every comet in the current MPC feed worth showing, each
        /// with today's real magnitude/RA/Dec -- backs the Touch-N-Stars panel's Browse tab.
        /// The panel is a thin client of this computation, not a second implementation of the
        /// same orbital math in JavaScript (see CLAUDE.md's "Quick Track" architecture section
        /// for the fuller reasoning -- the panel and this plugin run on the same Pi, so there's
        /// no internet-round-trip argument for duplicating it client-side).
        /// </summary>
        [Route(HttpVerbs.Get, "/objects")]
        public async Task ListObjects() {
            try {
                var objects = await OrbitalTracking.ListBrowseObjectsAsync(HttpClient, DateTime.UtcNow, HttpContext.CancellationToken);
                var response = new List<BrowseObjectResponse>(objects.Count);
                foreach (var o in objects) {
                    response.Add(new BrowseObjectResponse {
                        Id = o.Id,
                        Name = o.Name,
                        ObjectType = o.ObjectType,
                        Magnitude = o.Magnitude,
                        RaHours = o.RaHours,
                        DecDeg = o.DecDeg,
                    });
                }
                var json = JsonConvert.SerializeObject(response);
                await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
            } catch (Exception ex) {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { Message = ex.Message }), "application/json", Encoding.UTF8);
            }
        }

        /// <summary>
        /// When comet data was last actually fetched from MPC (on this run or a previous one, via
        /// the on-disk cache) -- null if never synced at all. Backs the panel's "last synced: X
        /// ago" indicator, matching NINA Orbitals' own per-object-type download screen.
        /// </summary>
        [Route(HttpVerbs.Get, "/sync/status")]
        public async Task SyncStatus() {
            var json = JsonConvert.SerializeObject(new SyncStatusResponse { CometsLastSyncedUtc = CometOrbits.LastSyncedUtc });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// Explicit "download comets now" action -- the deliberate "do this while I still have a
        /// connection, before heading to the dark site" step. Unlike ListObjects/Track's own
        /// passive stale-cache fallback, this always attempts a live fetch and reports whether it
        /// actually worked, since a user pressing a sync button deserves a real answer.
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
        /// One position per day for the requested number of nights -- backs the Position &amp;
        /// Path tab's finder-chart plot (the object's real path against the fixed stars, not
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
                // true live position by however many hours have passed since midnight (a real bug:
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
        /// Real, observer-reported "last seen" brightness for a comet, as a cross-check against
        /// the predicted (H, G model) magnitude already in the /objects list -- see
        /// CometActivity.cs's own doc comment for real verified cases where the two disagreed by
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
                    QuickTrackStatus.Started(request.ObjectType, request.TargetName, request.Guiding, request.AutoReapplyMinutes is > 0 ? request.AutoReapplyMinutes : null);

                    var trackingItem = new SetPerihelionTrackingRate(TelescopeMediator) {
                        ObjectType = request.ObjectType,
                        TargetName = request.TargetName,
                    };
                    await trackingItem.Execute(new Progress<ApplicationStatus>(), HttpContext.CancellationToken);
                    if (trackingItem.LastAppliedRate is OrbitalRate appliedRate) {
                        QuickTrackStatus.Applied(appliedRate);
                        Logger.Info($"Perihelion: Quick Track applied for {request.TargetName} -- RA {appliedRate.RaArcsecPerSec:F4} arcsec/s, Dec {appliedRate.DecArcsecPerSec:F4} arcsec/s");
                    }

                    if (request.Guiding && GuiderMediator != null) {
                        var guiderItem = new SetPerihelionGuiderShiftRate(GuiderMediator) {
                            ObjectType = request.ObjectType,
                            TargetName = request.TargetName,
                        };
                        await guiderItem.Execute(new Progress<ApplicationStatus>(), HttpContext.CancellationToken);
                    }

                    if (request.AutoReapplyMinutes is > 0) {
                        QuickTrackReapply.Start(TelescopeMediator, GuiderMediator, request.ObjectType, request.TargetName, request.Guiding, request.AutoReapplyMinutes.Value);
                    } else {
                        QuickTrackReapply.Stop();
                    }

                    response.Success = true;
                    response.Message = request.AutoReapplyMinutes is > 0
                        ? $"Quick Track started, re-applying every {request.AutoReapplyMinutes} min"
                        : "Quick Track started";
                }
            } catch (SequenceEntityFailedException ex) {
                response.Message = ex.Message;
                QuickTrackStatus.Failed(ex.Message);
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
            var response = new TrackResponse();
            QuickTrackReapply.Stop();
            QuickTrackStatus.Stopped();
            try {
                if (TelescopeMediator == null) {
                    response.Message = "Perihelion API server started before the telescope mediator was available";
                } else {
                    response.Success = TelescopeMediator.SetTrackingMode(TrackingMode.Sidereal);
                    response.Message = response.Success ? "Back to sidereal tracking" : "Setting tracking mode failed";
                    if (GuiderMediator != null) {
                        await GuiderMediator.StopShifting(HttpContext.CancellationToken);
                    }
                }
            } catch (Exception ex) {
                response.Message = $"Unexpected error: {ex.Message}";
            }

            var json = JsonConvert.SerializeObject(response);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>
        /// The actual state of whatever Quick Track session is running -- in particular the real
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
                AutoReapplyMinutes = s.AutoReapplyMinutes,
                StartedUtc = s.StartedUtc,
                LastAppliedUtc = s.LastAppliedUtc,
                LastRaArcsecPerSec = s.LastRaArcsecPerSec,
                LastDecArcsecPerSec = s.LastDecArcsecPerSec,
                LastApplySucceeded = s.LastApplySucceeded,
                LastError = s.LastError,
            };
            return HttpContext.SendStringAsync(JsonConvert.SerializeObject(response), "application/json", Encoding.UTF8);
        }
    }
}
