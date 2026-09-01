using EmbedIO;
using EmbedIO.WebApi;
using NINA.Core.Utility;
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

        /// <summary>
        /// Deliberately does NOT let an exception propagate out of here -- PerihelionPlugin.
        /// Initialize() calls this directly, and a real startup failure (e.g. port already in
        /// use) throwing out of Initialize() risks NINA treating the whole plugin as failed to
        /// activate (a real, previously-silent bug: the very first version of this method had
        /// no logging at all here, so a startup failure was completely invisible -- nothing in
        /// the NINA log, no exception anywhere, just a port that silently never opened).
        /// </summary>
        public void Start() {
            try {
                server = new WebServer(o => o
                        .WithUrlPrefix($"http://*:{port}")
                        .WithMode(HttpListenerMode.EmbedIO))
                    .WithModule(new PerihelionCorsModule())
                    .WithWebApi("/perihelion/api", m => m.WithController<PerihelionApiController>());
            } catch (Exception ex) {
                Logger.Error($"Perihelion: failed to construct API server on port {port}: {ex}");
                return;
            }

            cts = new CancellationTokenSource();
            var token = cts.Token;
            var runningServer = server;
            serverThread = new Thread(() => {
                try {
                    runningServer.RunAsync(token).Wait();
                } catch (Exception ex) when (token.IsCancellationRequested) {
                    // Expected: Stop() cancelled the token, which surfaces here as an exception
                    // from RunAsync/Wait -- same shape as ninaAPI's own APITask. Not an error.
                    Logger.Debug($"Perihelion: API server stopped ({ex.GetType().Name})");
                } catch (Exception ex) {
                    // NOT a shutdown -- a genuine startup/runtime failure (e.g. port already in
                    // use, permission denied). Must be logged: this is the exact failure mode
                    // that was previously invisible.
                    Logger.Error($"Perihelion: API server on port {port} failed: {ex}");
                }
            }) {
                Name = "Perihelion API Thread",
                IsBackground = true,
            };
            serverThread.Start();
            Logger.Info($"Perihelion: API server starting on port {port}");
        }

        public void Stop() {
            cts?.Cancel();
            server?.Dispose();
            server = null;
        }
    }
}
