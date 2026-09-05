using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using Perihelion.Api;
using Perihelion.Utility;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Perihelion {

    [Export(typeof(IPluginManifest))]
    public class PerihelionPlugin : PluginBase, INotifyPropertyChanged {
        private readonly PerihelionApiServer apiServer;
        private readonly IPluginOptionsAccessor pluginSettings;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Corrected finding, 2026-09-05: ISequenceMediator IS safely importable here
        /// after all. The earlier "No exports were found that match the constraint ...
        /// IPluginManifest" CompositionException was real, but mis-attributed -- that attempt
        /// imported BOTH ISequencerFactory AND ISequenceMediator together, and ISequencerFactory
        /// is the one that isn't MEF-exported at all (it's a plain DI-registered class,
        /// confirmed from NINA.Sequencer/SequencerFactory.cs's own constructor -- MEF has no
        /// [Export] for it anywhere to satisfy the import, hence the hard composition failure).
        /// ISequenceMediator alone is a completely different case: nitr57/ninaAPI's own
        /// AdvancedAPI.cs lists it directly in its [ImportingConstructor] and that plugin loads
        /// and runs correctly on this exact PINS build today -- real, working proof the MEF
        /// bridge for this specific interface does exist. Re-tried with ISequenceMediator alone
        /// this time: builds clean, and (see AddToSequenceAction's own comment in
        /// PerihelionDockableVM) reflecting the same private fields ninaAPI's own Sequence.cs
        /// route reflects (SequenceMediator.sequenceNavigation, then
        /// ISequenceNavigationVM.factory) is how the actual ISequencerFactory instance is reached
        /// from here, since that one genuinely has no direct import path.</summary>
        public static ISequenceMediator? SequenceMediator { get; private set; }

        [ImportingConstructor]
        public PerihelionPlugin(
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            IProfileService profileService,
            ISequenceMediator sequenceMediator) {

            SequenceMediator = sequenceMediator;

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
            ActualPort = port;
            apiServer = new PerihelionApiServer(telescopeMediator, guiderMediator, profileService, port);

            RegisterWindowsResources();
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

        /// <summary>Whether the standalone HTTP server should start at all -- real user request
        /// (2026-09-05): someone using only the native Windows panel has no need for Touch-N-
        /// Stars/Quick Track's remote API and the open port that comes with it. Defaults true so
        /// existing installs keep working unchanged. Same "takes effect on next restart"
        /// convention as Port -- doesn't attempt to stop/start the already-running server live.</summary>
        public bool ApiEnabled {
            get => pluginSettings.GetValueBoolean("ApiEnabled", true);
            set {
                pluginSettings.SetValueBoolean("ApiEnabled", value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ApiEnabled)));
            }
        }

        /// <summary>The port actually bound this session -- distinct from the Port setting above,
        /// which is only what's configured for the *next* restart and may not match if
        /// GetNearestAvailablePort had to shift away from a conflict. The three address
        /// properties below are for PerihelionOptionsView.xaml's own "Network Addresses" section,
        /// same idea as nitr57/ninaAPI's own Options page, so a user pointing Touch-N-Stars at
        /// this server knows the real, currently-listening address rather than guessing from the
        /// configured port alone.</summary>
        public int ActualPort { get; }

        public string LocalAddress => $"http://localhost:{ActualPort}/perihelion/api";
        public string IpAddress => $"http://{CoreUtility.GetLocalIPv4Address()}:{ActualPort}/perihelion/api";
        public string HostAddress => $"http://{Dns.GetHostName()}:{ActualPort}/perihelion/api";

        /// <summary>Three Windows-only WPF resources this plugin registers itself, since PINS
        /// renders no WPF UI shell at all and never looks any of them up (PINS' own
        /// System.Windows.Compat stub gives a harmless, always-non-null Application.Current, so
        /// this guard is about not adding meaningless entries to that stub's own
        /// MergedDictionaries, not about avoiding a null-reference crash):
        /// - PerihelionOptionsView.xaml -- NINA's Options > Plugins page looks up a DataTemplate
        ///   keyed "{plugin.Name}_Options" in Application.Current.Resources (confirmed directly
        ///   from NINA.ViewModel.Plugins.PluginOptionsDataTemplateSelector's own source).
        /// - PerihelionIcon.xaml -- the GeometryGroup PerihelionDockableVM's own constructor
        ///   looks up by name for its dock-tab icon, in place of DockableVM's default
        ///   PuzzlePieceSVG. Deliberately merged first: PerihelionDockableVM falls back
        ///   gracefully if this hasn't landed yet (MEF composition order between two independent
        ///   exports isn't guaranteed), but there's no reason not to give it the best chance.
        /// - PerihelionDockableView.xaml -- the implicit (DataType-only) DataTemplate NINA's own
        ///   imaging-tab dock host resolves automatically for a PerihelionDockableVM instance.
        /// All three XAML files are Windows-build-only -- see Perihelion.Windows.csproj's own
        /// comment on UseWPF.</summary>
        private void RegisterWindowsResources() {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            foreach (var path in new[] { "Resources/PerihelionIcon.xaml", "Views/PerihelionDockableView.xaml", "Views/PerihelionOptionsView.xaml" }) {
                try {
                    System.Windows.Application.Current.Resources.MergedDictionaries.Add(
                        new System.Windows.ResourceDictionary {
                            Source = new Uri($"pack://application:,,,/Perihelion;component/{path}"),
                        });
                } catch (Exception ex) {
                    // Never worth failing plugin Initialize() over a missing WPF resource -- the
                    // server itself doesn't depend on any of these, only the affected UI surface
                    // (settings page, dock icon, or dock panel content) would look wrong.
                    Logger.Error($"Perihelion: could not register {path}: {ex}");
                }
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
                if (ApiEnabled) {
                    apiServer.Start();
                } else {
                    Logger.Info("Perihelion: API server disabled via plugin options -- not starting");
                }
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
