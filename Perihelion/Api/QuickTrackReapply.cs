using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using Perihelion.Astrometry;
using Perihelion.SequenceItems;
using System;
using System.Threading;

namespace Perihelion.Api {

    /// <summary>
    /// Owns the optional "auto re-apply every N minutes" background timer for Quick Track.
    /// State is static/module-level, not instance state, because EmbedIO constructs a new
    /// PerihelionApiController instance per request -- same reason TelescopeMediator/
    /// GuiderMediator on that controller are static fields rather than constructor-injected.
    ///
    /// Re-applying means fully recomputing the rate from the object's current position each
    /// tick (a fresh SetPerihelionTrackingRate/SetPerihelionGuiderShiftRate, not re-sending a
    /// cached rate) -- a comet's true angular rate drifts gradually over the course of a night,
    /// so recomputing is what actually keeps the mount's custom rate accurate through a long,
    /// unattended Quick Track session. Runs entirely in this plugin process, independent of
    /// whether the Touch-N-Stars browser tab that started it is still open.
    /// </summary>
    internal static class QuickTrackReapply {
        private static readonly object Gate = new();
        private static Timer? timer;

        public static void Start(ITelescopeMediator telescopeMediator, IGuiderMediator? guiderMediator, OrbitalObjectType objectType, string targetName, bool guiding, int intervalMinutes) {
            lock (Gate) {
                StopLocked();
                var interval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
                timer = new Timer(
                    _ => Reapply(telescopeMediator, guiderMediator, objectType, targetName, guiding),
                    null,
                    interval,
                    interval);
                Logger.Info($"Perihelion: auto re-apply enabled for {targetName} every {interval.TotalMinutes:0} min");
            }
        }

        public static void Stop() {
            lock (Gate) {
                StopLocked();
            }
        }

        private static void StopLocked() {
            timer?.Dispose();
            timer = null;
        }

        private static async void Reapply(ITelescopeMediator telescopeMediator, IGuiderMediator? guiderMediator, OrbitalObjectType objectType, string targetName, bool guiding) {
            try {
                var trackingItem = new SetPerihelionTrackingRate(telescopeMediator) { ObjectType = objectType, TargetName = targetName };
                await trackingItem.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

                if (guiding && guiderMediator != null) {
                    var guiderItem = new SetPerihelionGuiderShiftRate(guiderMediator) { ObjectType = objectType, TargetName = targetName };
                    await guiderItem.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
                }

                if (trackingItem.LastAppliedRate is OrbitalRate rate) {
                    QuickTrackStatus.Applied(rate);
                }
                // Info, not Debug -- this was originally Debug and, on a default PINS log level,
                // never showed up at all, making a real 15-minute re-apply session look
                // indistinguishable from a silently-dead timer purely because of log-level
                // filtering. This is the one line that proves the timer is actually still firing.
                Logger.Info($"Perihelion: auto re-applied tracking rate for {targetName} -- RA {trackingItem.LastAppliedRate?.RaArcsecPerSec:F4} arcsec/s, Dec {trackingItem.LastAppliedRate?.DecArcsecPerSec:F4} arcsec/s");
            } catch (Exception ex) {
                // Swallowed -- this runs unattended on a background timer with no HTTP caller to
                // report to, and the next scheduled tick retries regardless. Logged so a genuine
                // recurring failure (mount disconnected, object dropped out of the feed) is still
                // visible in the NINA log rather than silently going nowhere.
                QuickTrackStatus.Failed(ex.Message);
                Logger.Error($"Perihelion: auto re-apply failed for {targetName}: {ex.Message}");
            }
        }
    }
}
