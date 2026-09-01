using EmbedIO;
using EmbedIO.WebApi;
using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Threading;

namespace Perihelion.Api {

    /// <summary>
    /// Lifecycle wrapper mirroring nitr57/ninaAPI's own API class (WebService/API.cs) --
    /// same WebServer construction/background-thread pattern, proven working on this exact
    /// PINS build. A separate server from ninaAPI's own, on its own port: see CLAUDE.md's
    /// "Quick Track" architecture section for why (avoids needing a PR into either ninaAPI or
    /// the Touch-N-Stars NINA plugin's own hardcoded controller list).
    /// </summary>
    public class PerihelionApiServer {
        // TODO: make configurable (a plugin options page) once one exists -- picked to avoid
        // colliding with ninaAPI's own common default (1888) and Stellarium Remote Control's
        // (8090). Not yet checked against every other PINS plugin's own port choice.
        public const int DefaultPort = 1899;

        private readonly int port;
        private WebServer? server;
        private Thread? serverThread;
        private CancellationTokenSource? cts;

        public PerihelionApiServer(ITelescopeMediator telescopeMediator, IGuiderMediator guiderMediator, int port = DefaultPort) {
            this.port = port;
            PerihelionApiController.TelescopeMediator = telescopeMediator;
            PerihelionApiController.GuiderMediator = guiderMediator;
        }

        public void Start() {
            server = new WebServer(o => o
                    .WithUrlPrefix($"http://*:{port}")
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new PerihelionCorsModule())
                .WithWebApi("/perihelion/api", m => m.WithController<PerihelionApiController>());

            cts = new CancellationTokenSource();
            var token = cts.Token;
            var runningServer = server;
            serverThread = new Thread(() => {
                try {
                    runningServer.RunAsync(token).Wait();
                } catch (Exception) {
                    // Swallowed on shutdown (Stop() cancels the token, which surfaces here as an
                    // exception from RunAsync) -- same shape as ninaAPI's own APITask.
                }
            }) {
                Name = "Perihelion API Thread",
                IsBackground = true,
            };
            serverThread.Start();
        }

        public void Stop() {
            cts?.Cancel();
            server?.Dispose();
            server = null;
        }
    }
}
