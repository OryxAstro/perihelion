using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;
using NINA.Core.Model;
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
    }

    internal class TrackResponse {
        [JsonProperty]
        public bool Success { get; set; }

        [JsonProperty]
        public string Message { get; set; } = string.Empty;
    }

    internal class PathPointResponse {
        [JsonProperty]
        public string Date { get; set; } = string.Empty;

        [JsonProperty]
        public double RaHours { get; set; }

        [JsonProperty]
        public double DecDeg { get; set; }
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

        // Shared, not created per request -- same reasoning as the sequence items' own HttpClient
        // (SetPerihelionTrackingRate.cs): per-request instances risk socket exhaustion.
        private static readonly HttpClient HttpClient = new();

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

                var points = await OrbitalTracking.ComputeOrbitalPathAsync(HttpClient, type, targetName, DateTime.UtcNow.Date, effectiveDays, HttpContext.CancellationToken);
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

        [Route(HttpVerbs.Post, "/track")]
        public async Task Track() {
            var response = new TrackResponse();
            try {
                var body = await HttpContext.GetRequestBodyAsStringAsync();
                var request = JsonConvert.DeserializeObject<TrackRequest>(body) ?? new TrackRequest();

                if (TelescopeMediator == null) {
                    response.Message = "Perihelion API server started before the telescope mediator was available";
                } else {
                    var trackingItem = new SetPerihelionTrackingRate(TelescopeMediator) {
                        ObjectType = request.ObjectType,
                        TargetName = request.TargetName,
                    };
                    await trackingItem.Execute(new Progress<ApplicationStatus>(), HttpContext.CancellationToken);

                    if (request.Guiding && GuiderMediator != null) {
                        var guiderItem = new SetPerihelionGuiderShiftRate(GuiderMediator) {
                            ObjectType = request.ObjectType,
                            TargetName = request.TargetName,
                        };
                        await guiderItem.Execute(new Progress<ApplicationStatus>(), HttpContext.CancellationToken);
                    }

                    response.Success = true;
                    response.Message = "Quick Track started";
                }
            } catch (SequenceEntityFailedException ex) {
                response.Message = ex.Message;
            } catch (Exception ex) {
                response.Message = $"Unexpected error: {ex.Message}";
            }

            var json = JsonConvert.SerializeObject(response);
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        /// <summary>Undoes what Quick Track did: back to sidereal tracking, and stops any guider shift.</summary>
        [Route(HttpVerbs.Post, "/stop")]
        public async Task Stop() {
            var response = new TrackResponse();
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
    }
}
