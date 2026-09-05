using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using Perihelion.Api;
using Perihelion.Utility;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Perihelion {

    [Export(typeof(IPluginManifest))]
    public class PerihelionPlugin : PluginBase, INotifyPropertyChanged {
        private readonly PerihelionApiServer apiServer;
        private readonly IPluginOptionsAccessor pluginSettings;

        public event PropertyChangedEventHandler? PropertyChanged;

        [ImportingConstructor]
        public PerihelionPlugin(ITelescopeMediator telescopeMediator, IGuiderMediator guiderMediator, IProfileService profileService) {
            // Same PluginOptionsAccessor mechanism nitr57/ninaAPI and the Touch-N-Stars PINS
            // plugin both use for their own configurable port. On PINS there's still no settings
            // UI to expose this through at all (no WPF shell renders, the same root cause
            // documented in CLAUDE.md) -- Port there stays hand-edit-the-profile-XML-only, same
            // as before. On real Windows NINA, PerihelionOptionsView.xaml (Windows-only, see
            // RegisterOptionsTemplate below) binds directly to the Port property below, so this
            // now has a real settings page there. What actually matters day to day either way is
            // GetNearestAvailablePort(): the same real conflict-avoidance ninaAPI/Touch-N-Stars
            // already rely on, so an unconfigured collision with another plugin (or anything
            // else on the box) self-resolves at startup instead of the server silently failing
            // to bind.
            pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(Identifier));
            var configuredPort = pluginSettings.GetValueInt32("Port", PerihelionApiServer.DefaultPort);
            var port = CoreUtility.GetNearestAvailablePort(configuredPort);
            if (port != configuredPort) {
                Logger.Info($"Perihelion: port {configuredPort} unavailable, using {port} instead");
            }
            apiServer = new PerihelionApiServer(telescopeMediator, guiderMediator, profileService, port);

            RegisterOptionsTemplate();
        }

        /// <summary>The configured port, persisted per-profile via PluginOptionsAccessor -- the
        /// same value PerihelionApiServer.DefaultPort falls back to and GetNearestAvailablePort
        /// resolves conflicts from at startup (see the constructor's own comment). Bound directly
        /// by PerihelionOptionsView.xaml on the Windows build; changing it here only takes effect
        /// on the next NINA/PINS restart, same as hand-editing the profile XML already did --
        /// this doesn't attempt to stop and rebind the already-running server live.</summary>
        public int Port {
            get => pluginSettings.GetValueInt32("Port", PerihelionApiServer.DefaultPort);
            set {
                pluginSettings.SetValueInt32("Port", value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Port)));
            }
        }

        /// <summary>NINA's own Options > Plugins page looks up a DataTemplate keyed
        /// "{plugin.Name}_Options" in Application.Current.Resources (confirmed directly from
        /// NINA.ViewModel.Plugins.PluginOptionsDataTemplateSelector's own source) -- registering
        /// one is what turns "no options UI at all" into a real settings page there. Guarded to
        /// only ever run on Windows: PINS renders no WPF UI shell at all, so nothing there would
        /// ever look this key up in the first place -- PINS' own System.Windows.Compat stub
        /// actually provides a harmless, always-non-null Application.Current (checked directly
        /// against its source), so this guard is about not registering a meaningless entry into
        /// that stub's own MergedDictionaries list, not about avoiding a null-reference crash.
        /// The actual XAML (PerihelionOptionsView.xaml) is Windows-build-only for the same
        /// reason -- see Perihelion.Windows.csproj's own comment on UseWPF.</summary>
        private void RegisterOptionsTemplate() {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            try {
                System.Windows.Application.Current.Resources.MergedDictionaries.Add(
                    new System.Windows.ResourceDictionary {
                        Source = new Uri("pack://application:,,,/Perihelion;component/Views/PerihelionOptionsView.xaml"),
                    });
            } catch (Exception ex) {
                // Never worth failing plugin Initialize() over a missing options page -- the
                // server itself doesn't depend on this at all, only the Settings UI would show
                // nothing where a real options page should be.
                Logger.Error($"Perihelion: could not register options page: {ex}");
            }
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
