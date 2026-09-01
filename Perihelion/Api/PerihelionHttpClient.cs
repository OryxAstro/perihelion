using System.Net.Http;

namespace Perihelion.Api {

    /// <summary>
    /// The one HttpClient shared by every part of Perihelion that talks to an external feed (MPC,
    /// eventually COBS) -- consolidates what used to be three separate static instances (one each
    /// in SetPerihelionTrackingRate, SetPerihelionGuiderShiftRate, PerihelionApiController), and
    /// gives every outbound request a real User-Agent identifying the plugin, which none of them
    /// previously sent (neither does OryxAstro's own equivalent website fetch -- worth being a
    /// better API citizen here regardless).
    /// </summary>
    public static class PerihelionHttpClient {
        public static readonly HttpClient Instance = Create();

        private static HttpClient Create() {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Perihelion-NINA-Plugin/0.1.0 (+https://github.com/OryxAstro/perihelion)");
            return client;
        }
    }
}
