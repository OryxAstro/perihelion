using EmbedIO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Perihelion.Api {

    /// <summary>
    /// Requires the X-Perihelion-Token header to match PerihelionApiController.ApiToken on
    /// every request except CORS preflight (OPTIONS) -- browsers never send custom headers on a
    /// preflight request, so requiring the token there would block every cross-origin call
    /// outright -- and /pair itself, which exists specifically to hand the token to a client
    /// that doesn't have it yet (see PerihelionApiController.Pair). Registered after
    /// PerihelionCorsModule so a 401 still carries CORS headers and the browser can actually
    /// read it instead of reporting a blocked cross-origin request.
    /// </summary>
    public class PerihelionAuthModule : WebModuleBase {
        public PerihelionAuthModule() : base("/") {
        }

        protected override async Task OnRequestAsync(IHttpContext context) {
            if (context.Request.HttpVerb == HttpVerbs.Options || context.RequestedPath == "/perihelion/api/pair") {
                return;
            }

            var expected = PerihelionApiController.ApiToken;
            var provided = context.Request.Headers["X-Perihelion-Token"];

            if (!TokensMatch(expected, provided)) {
                context.Response.StatusCode = 401;
                await context.SendStringAsync("{\"Message\":\"Missing or incorrect X-Perihelion-Token header\"}", "application/json", Encoding.UTF8);
                context.SetHandled();
            }
        }

        private static bool TokensMatch(string? expected, string? provided) {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided)) {
                return false;
            }
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var providedBytes = Encoding.UTF8.GetBytes(provided);
            return expectedBytes.Length == providedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
        }

        public override bool IsFinalHandler => false;
    }
}
