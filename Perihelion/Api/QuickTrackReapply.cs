using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using Perihelion.Astrometry;
using Perihelion.SequenceItems;
using System;
using System.Threading;

namespace Perihelion.Api {

    /// <summary>
    /// Owns Quick Track's ongoing background behavior: the optional "auto re-apply every N
    /// minutes" timer, and an always-on meridian safety cutoff (see CheckMeridian). State is
    /// static/module-level, not instance state, because EmbedIO constructs a new
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
        private static Timer? reapplyTimer;
        private static Timer? meridianGuardTimer;

        // Fixed, not user-configurable -- this is a safety check, not a preference (unlike the
        // reapply interval above). 60s is frequent enough to catch the threshold promptly
        // without hammering the mount driver with property reads.
        private static readonly TimeSpan MeridianCheckInterval = TimeSpan.FromSeconds(60);

        public static void Start(ITelescopeMediator telescopeMediator, IGuiderMediator? guiderMediator, IProfileService profileService, OrbitalObjectType objectType, string targetName, bool guiding, int? reapplyIntervalMinutes) {
            lock (Gate) {
                StopLocked();

                if (reapplyIntervalMinutes is > 0) {
                    var interval = TimeSpan.FromMinutes(Math.Max(1, reapplyIntervalMinutes.Value));
                    reapplyTimer = new Timer(
                        _ => Reapply(telescopeMediator, guiderMediator, profileService, objectType, targetName, guiding),
                        null,
                        interval,
                        interval);
                    Logger.Info($"Perihelion: auto re-apply enabled for {targetName} every {interval.TotalMinutes:0} min");
                }

                // Unconditional -- runs whether or not auto re-apply is on, since Quick Track has
                // no sequence and no MeridianFlipTrigger of its own regardless of that setting.
                meridianGuardTimer = new Timer(
                    _ => CheckMeridian(telescopeMediator, guiderMediator, targetName),
                    null,
                    MeridianCheckInterval,
                    MeridianCheckInterval);
            }
        }

        public static void Stop() {
            lock (Gate) {
                StopLocked();
            }
        }

        private static void StopLocked() {
            reapplyTimer?.Dispose();
            reapplyTimer = null;
            meridianGuardTimer?.Dispose();
            meridianGuardTimer = null;
        }

        /// <summary>
        /// Real hardware safety concern, not a hypothetical: on a German Equatorial Mount,
        /// tracking past the meridian without flipping which side of the pier the tube sits on
        /// eventually swings the OTA/counterweight into the tripod, pier, or mount head. NINA's
        /// own Advanced Sequencer handles this via MeridianFlipTrigger -- but Quick Track has no
        /// sequence and no trigger infrastructure at all, so nothing would otherwise stop it.
        ///
        /// Deliberately stops rather than actually performing the flip -- checked
        /// ITelescopeMediator.MeridianFlip() directly (NINA.WPF.Base's TelescopeVM.MeridianFlip):
        /// it's genuinely just the raw pier-flip device command plus a dome-sync wait, NOT a
        /// complete safe sequence. It doesn't stop guiding first, doesn't plate-solve afterward,
        /// and doesn't recenter -- MeridianFlipTrigger orchestrates all of that itself, separately,
        /// only inside a real sequence. Reimplementing that whole orchestration independently here
        /// would mean duplicating safety-critical logic outside the one place it's actually
        /// tested, for a feature explicitly scoped to manual/visual use, not unattended automation
        /// -- so this stops tracking and tells the user to flip manually, the same way a plain
        /// "I've been analog-tracking and forgot the time" situation would require anyway.
        ///
        /// TimeToMeridianFlip (NINA.Astrometry.MeridianFlip.TimeToMeridianFlip, surfaced on
        /// TelescopeInfo) already factors in the user's own configured
        /// MeridianFlipSettings.MaxMinutesAfterMeridian -- reusing it directly means this respects
        /// whatever safety margin the user already set for their own rig's geometry in NINA's own
        /// settings, rather than Perihelion guessing at (or hardcoding) a threshold that varies by
        /// OTA length, dovetail, and mount head clearance.
        /// </summary>
        private static async void CheckMeridian(ITelescopeMediator telescopeMediator, IGuiderMediator? guiderMediator, string targetName) {
            try {
                var info = telescopeMediator.GetInfo();
                if (!info.Connected) return;
                // NaN means this backend/driver doesn't report it at all (not every ASCOM/INDI
                // driver implements TimeToMeridianFlip) -- nothing to act on, and no way to warn
                // reliably, so this mount is simply outside what this guard can cover.
                if (double.IsNaN(info.TimeToMeridianFlip)) return;
                if (info.TimeToMeridianFlip > 0) return;

                StopLocked();
                telescopeMediator.SetTrackingMode(TrackingMode.Sidereal);
                if (guiderMediator != null) {
                    await guiderMediator.StopShifting(CancellationToken.None).ConfigureAwait(false);
                }
                const string reason = "Stopped automatically: reached the meridian flip limit for your mount. Flip it manually, then restart Quick Track.";
                QuickTrackStatus.Stopped(reason);
                Logger.Warning($"Perihelion: Quick Track stopped automatically for {targetName} -- meridian flip limit reached");
            } catch (Exception ex) {
                // Swallowed, like Reapply below -- this runs unattended with no HTTP caller to
                // report to. Logged so a genuine recurring failure (mount disconnected mid-check,
                // e.g.) is still visible rather than silently going nowhere. Deliberately does NOT
                // stop tracking on a failed *check* -- only on a successful one that confirms the
                // limit was actually reached; erring toward "guard didn't run this tick" over
                // "stopped tracking for an unrelated transient error" here.
                Logger.Error($"Perihelion: meridian safety check failed for {targetName}: {ex.Message}");
            }
        }

        private static async void Reapply(ITelescopeMediator telescopeMediator, IGuiderMediator? guiderMediator, IProfileService profileService, OrbitalObjectType objectType, string targetName, bool guiding) {
            try {
                var trackingItem = new SetPerihelionTrackingRate(telescopeMediator, profileService) { ObjectType = objectType, TargetName = targetName };
                await trackingItem.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

                if (guiding && guiderMediator != null) {
                    var guiderItem = new SetPerihelionGuiderShiftRate(guiderMediator, profileService) { ObjectType = objectType, TargetName = targetName };
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
