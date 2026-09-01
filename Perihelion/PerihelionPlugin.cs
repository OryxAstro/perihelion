using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using Perihelion.Api;
using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;

namespace Perihelion {

    [Export(typeof(IPluginManifest))]
    public class PerihelionPlugin : PluginBase {
        private readonly PerihelionApiServer apiServer;

        [ImportingConstructor]
        public PerihelionPlugin(ITelescopeMediator telescopeMediator, IGuiderMediator guiderMediator) {
            apiServer = new PerihelionApiServer(telescopeMediator, guiderMediator);
        }

        // PerihelionApiServer.Start() already catches its own exceptions (see its own comment
        // for why), but this belt-and-braces try/catch exists so that even a completely
        // unanticipated failure here can never propagate out of Initialize() and risk NINA
        // treating the whole plugin as failed to activate -- a real, previously-observed
        // symptom (the plugin's own "enabled" toggle didn't persist across a PINS restart,
        // consistent with a failed Initialize() somewhere in this call chain, though no
        // exception ever reached the log because of the bug PerihelionApiServer.Start() fixes).
        public override Task Initialize() {
            try {
                apiServer.Start();
            } catch (Exception ex) {
                Logger.Error($"Perihelion: Initialize() failed unexpectedly: {ex}");
            }
            return base.Initialize();
        }

        public override Task Teardown() {
            try {
                apiServer.Stop();
            } catch (Exception ex) {
                Logger.Error($"Perihelion: Teardown() failed: {ex}");
            }
            return base.Teardown();
        }
    }
}
