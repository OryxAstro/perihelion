using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

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

        /// <summary>The machine's own outbound LAN IPv4 address -- same technique nitr57/
        /// ninaAPI's own Utility/CoreUtility.cs uses for its Options page's "IP Address" row
        /// (GetIPv4Address there): opening a UDP socket and "connecting" it to a public address
        /// never actually sends a packet, it just asks the OS to pick the real local interface/
        /// address it would route through, which is exactly the LAN-reachable address another
        /// device (a phone running Touch-N-Stars) would need. Falls back to loopback if that
        /// fails for any reason (no network, etc.) rather than throwing.</summary>
        public static string GetLocalIPv4Address() {
            try {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
            } catch (Exception) {
                return "127.0.0.1";
            }
        }
    }
}
