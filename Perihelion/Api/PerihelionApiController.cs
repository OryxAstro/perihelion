using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using Perihelion.Astrometry;
using Perihelion.SequenceItems;
using System;
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
    }
}
