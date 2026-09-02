using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using Perihelion.Api;
using Perihelion.Utility;
using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;

namespace Perihelion {

    [Export(typeof(IPluginManifest))]
    public class PerihelionPlugin : PluginBase {
        private readonly PerihelionApiServer apiServer;

        [ImportingConstructor]
        public PerihelionPlugin(ITelescopeMediator telescopeMediator, IGuiderMediator guiderMediator, IProfileService profileService) {
            // Same PluginOptionsAccessor mechanism nitr57/ninaAPI and the Touch-N-Stars PINS
            // plugin both use for their own configurable port -- there's no settings UI to
            // expose this through on PINS (no WPF shell renders at all, the same root cause
            // documented in CLAUDE.md), so today the only way to override "Port" is hand-editing
            // the active profile's plugin-settings XML under this plugin's own GUID. What
            // actually matters day to day is GetNearestAvailablePort(): the same real
            // conflict-avoidance ninaAPI/Touch-N-Stars already rely on, so an unconfigured
            // collision with another plugin (or anything else on the box) self-resolves at
            // startup instead of the server silently failing to bind.
            var pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(Identifier));
            var configuredPort = pluginSettings.GetValueInt32("Port", PerihelionApiServer.DefaultPort);
            var port = CoreUtility.GetNearestAvailablePort(configuredPort);
            if (port != configuredPort) {
                Logger.Info($"Perihelion: port {configuredPort} unavailable, using {port} instead");
            }
            apiServer = new PerihelionApiServer(telescopeMediator, guiderMediator, profileService, port);
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
