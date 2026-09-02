using System.Net.NetworkInformation;

namespace Perihelion.Utility {

    /// <summary>
    /// Port-availability check, ported from nitr57/ninaAPI's own Utility/CoreUtility.cs (also
    /// duplicated verbatim in the Touch-N-Stars PINS plugin, NINA.Plugins/Touch-N-Stars/Touch-N-
    /// Stars/Utility/CoreUtility.cs) -- the proven conflict-avoidance pattern this exact PINS
    /// build already uses for two other plugins picking their own listener port, so Perihelion
    /// follows the same one rather than inventing a separate strategy. Checks the OS's own
    /// active TCP listeners directly, so it catches a collision with any process on the box, not
    /// just other known PINS plugins.
    /// </summary>
    internal static class CoreUtility {
        public static bool IsPortAvailable(int port) {
            var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var endpoint in ipGlobalProperties.GetActiveTcpListeners()) {
                if (endpoint.Port == port) return false;
            }
            return true;
        }

        public static int GetNearestAvailablePort(int startPort) {
            var port = startPort;
            while (!IsPortAvailable(port)) port++;
            return port;
        }
    }
}
