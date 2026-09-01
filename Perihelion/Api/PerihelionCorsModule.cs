using EmbedIO;
using System.Text;
using System.Threading.Tasks;

namespace Perihelion.Api {

    /// <summary>
    /// Same CORS pattern nitr57/ninaAPI's own PreprocessRequestModule uses (WebService/API.cs) --
    /// the Touch-N-Stars panel calls this server from a different origin/port than the main app,
    /// so without this the browser silently blocks reading the response.
    /// </summary>
    public class PerihelionCorsModule : WebModuleBase {
        public PerihelionCorsModule() : base("/") {
        }

        protected override async Task OnRequestAsync(IHttpContext context) {
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (context.Request.HttpVerb == HttpVerbs.Options) {
                context.Response.StatusCode = 200;
                await context.SendStringAsync(string.Empty, "text/plain", Encoding.UTF8);
            }
        }

        public override bool IsFinalHandler => false;
    }
}
