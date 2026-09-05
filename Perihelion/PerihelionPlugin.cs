using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Interfaces.Mediator;
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

        /// <summary>Bridges ISequencerFactory/ISequenceMediator to PerihelionDockableVM
        /// (Windows-only, Perihelion.Windows/ViewModels/PerihelionDockableVM.cs), which cannot
        /// import them itself -- confirmed the hard way: adding them directly to that VM's own
        /// [ImportingConstructor] silently broke its MEF composition entirely (the panel
        /// vanished from the Imaging tab, no error anywhere, no obvious log signal at all). Root
        /// cause, confirmed against NINA's own source: ISequenceMediator is registered in NINA's
        /// separate Microsoft.Extensions.DependencyInjection container
        /// (services.AddSingleton&lt;ISequenceMediator, SequenceMediator&gt;() in
        /// NINA/Utility/IoCBindings.cs), not exported to MEF directly -- nitr57/ninaAPI's own
        /// AdvancedAPI (a real, working [Export(typeof(IPluginManifest))] plugin class) proves
        /// the DI/MEF bridge DOES reach a plugin's own main manifest class, but Orbitals' own
        /// real, working dockable VM (OrbitalsVM) tellingly never imports either type despite
        /// clearly needing sequence-building capability elsewhere -- strong evidence the bridge
        /// does not extend to whatever narrower composition scope
        /// pluginProvider.DockableVMs itself composes third-party IDockableVM exports through.
        /// Importing here instead (proven to work, same reasoning as ninaAPI's own AdvancedAPI)
        /// and exposing statically is the same bridge pattern already used for
        /// PerihelionApiController's own TelescopeMediator/GuiderMediator/ProfileService fields.</summary>
        internal static ISequencerFactory? SequencerFactory { get; private set; }
        internal static ISequenceMediator? SequenceMediator { get; private set; }

        [ImportingConstructor]
        public PerihelionPlugin(
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            IProfileService profileService,
            ISequencerFactory sequencerFactory,
            ISequenceMediator sequenceMediator) {
            SequencerFactory = sequencerFactory;
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
