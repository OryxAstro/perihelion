using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using Perihelion.Api;
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

        public override Task Initialize() {
            apiServer.Start();
            return base.Initialize();
        }

        public override Task Teardown() {
            apiServer.Stop();
            return base.Teardown();
        }
    }
}
