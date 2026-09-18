using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using Perihelion.Astrometry;
using Perihelion.SequenceItems;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Perihelion.Api {

    /// <summary>
    /// Owns Quick Track's ongoing background behavior: the optional "auto re-apply every N
    /// seconds" timer, and an always-on meridian safety cutoff (see CheckMeridian). State is
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
        private static CancellationTokenSource? sessionCts;
        private static Task? inFlightReapply;

        // Fixed, not user-configurable -- this is a safety check, not a preference (unlike the
        // reapply interval above). 60s is frequent enough to catch the threshold promptly
        // without hammering the mount driver with property reads.
        private static readonly TimeSpan MeridianCheckInterval = TimeSpan.FromSeconds(60);

        public static void Start(ITelescopeMediator telescopeMediator, IGuiderMediator? guiderMediator, IProfileService profileService, OrbitalObjectType objectType, string targetName, bool guiding, int? reapplyIntervalSeconds) {
            lock (Gate) {
                StopLocked();
                var cts = new CancellationTokenSource();
                sessionCts = cts;

                if (reapplyIntervalSeconds is > 0) {
                    var interval = TimeSpan.FromSeconds(Math.Max(PerihelionPlugin.MinReapplyIntervalSeconds, reapplyIntervalSeconds.Value));
                    reapplyTimer = new Timer(
                        _ => Reapply(telescopeMediator, guiderMediator, profileService, objectType, targetName, guiding, cts.Token),
                        null,
                        interval,
                        interval);
                    Logger.Info($"Perihelion: auto re-apply enabled for {targetName} every {interval.TotalSeconds:0} sec");
                }

                // Unconditional -- runs whether or not auto re-apply is on, since Quick Track has
                // no sequence and no MeridianFlipTrigger of its own regardless of that setting.
                meridianGuardTimer = new Timer(
                    _ => CheckMeridian(telescopeMediator, guiderMediator, profileService, targetName),
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
            // Signals any reapply tick already in flight to bail out instead of applying a rate
            // -- CheckMeridian additionally awaits inFlightReapply itself (see its own comment)
            // so a tick that's already past this check can still finish before the actual stop
            // commands go out, rather than racing them.
            sessionCts?.Cancel();
            sessionCts = null;
        }

        /// <summary>
        /// Hardware safety concern, not a hypothetical: on a German Equatorial Mount,
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
        /// only inside a sequence. Reimplementing that whole orchestration independently here
        /// would mean duplicating safety-critical logic outside the one place it's actually
        /// tested, for a feature explicitly scoped to manual/visual use, not unattended automation
        /// -- so this stops tracking and tells the user to flip manually, the same way a plain
        /// "I've been analog-tracking and forgot the time" situation would require anyway.
        ///
        /// Deliberately does NOT use ITelescopeInfo.TimeToMeridianFlip (NINA.Astrometry.
        /// MeridianFlip.TimeToMeridianFlip) despite it looking like the obvious fit -- confirmed
        /// by reading that method's own source that it can never return a negative number: the
        /// moment its internal (RA - LST) difference would go negative, it unconditionally adds
        /// 12 hours back, specifically so the value stays meaningful as a forward-looking "time
        /// until the next flip point" for the UI. That means a poll checking "<= 0" has, in the
        /// literal sense, nothing to ever observe -- the value jumps straight from a small
        /// positive number to roughly 12 hours with no dwell time in between, so a periodic timer
        /// will almost always sample on the wrong side of that jump and never see zero. Confirmed
        /// on live hardware: a Quick Track session run 30+ minutes past its target's actual
        /// meridian crossing never stopped, and the NINA log showed no meridian-related line at
        /// all -- consistent with every single 60-second poll landing on the wrapped side.
        ///
        /// Hour angle computed directly from ITelescopeInfo.SiderealTime/RightAscension (both
        /// populated by NINA itself from site longitude and the mount's own reported coordinates,
        /// not dependent on driver-specific capability flags the way TimeToMeridianFlip partly
        /// is) sidesteps this: normalized into (-12, 12], HA increases monotonically THROUGH the
        /// meridian crossing and keeps increasing for a full 12 hours afterward before it wraps,
        /// so "has HA exceeded the configured grace period" stays true and observable for hours,
        /// not a single unobservable instant.
        ///
        /// The threshold itself still comes from the user's own MeridianFlipSettings -- not just
        /// MaxMinutesAfterMeridian, but PauseTimeBeforeMeridian too: MeridianFlipTrigger.
        /// ShouldTrigger treats a configured PauseTimeBeforeMeridian as a hard equipment-clearance
        /// limit that pulls the required stop BEFORE the meridian rather than after it (a rig
        /// that physically cannot approach the meridian at all needs this, not just a grace period
        /// past it) -- confirmed directly from its own source, which substracts MinutesAfterMeridian
        /// and PauseTimeBeforeMeridian from the raw time-to-meridian when PauseTimeBeforeMeridian is
        /// non-zero. Perihelion respects the same override so a user who configured that limit for
        /// their own rig gets the same protection here, not just the after-meridian one.
        /// </summary>
        private static async void CheckMeridian(ITelescopeMediator telescopeMediator, IGuiderMediator? guiderMediator, IProfileService profileService, string targetName) {
            try {
                var info = telescopeMediator.GetInfo();
                if (!info.Connected) return;

                // Hours, normalized into (-12, 12]: negative means still approaching the
                // meridian, 0 is the crossing itself, positive and increasing means further past
                // it -- unlike TimeToMeridianFlip this never wraps back to a large value until a
                // full 12 hours after the crossing, giving a 60-second poll a wide, easily-hit
                // window instead of a single instant.
                var hourAngle = info.SiderealTime - info.RightAscension;
                hourAngle %= 24.0;
                if (hourAngle > 12.0) hourAngle -= 24.0;
                if (hourAngle <= -12.0) hourAngle += 24.0;

                var settings = profileService.ActiveProfile.MeridianFlipSettings;
                // A configured PauseTimeBeforeMeridian moves the limit to that many minutes
                // BEFORE the meridian (negative HA) instead of MaxMinutesAfterMeridian minutes
                // after it -- same override MeridianFlipTrigger itself applies for a rig that
                // needs to stop clear of the meridian entirely, not just flip promptly past it.
                var thresholdHours = settings.PauseTimeBeforeMeridian > 0
                    ? -settings.PauseTimeBeforeMeridian / 60.0
                    : settings.MaxMinutesAfterMeridian / 60.0;
                if (hourAngle < thresholdHours) return;

                Task? pending;
                lock (Gate) {
                    StopLocked();
                    pending = inFlightReapply;
                }
                if (pending != null) {
                    // A tick already past its own cancellation check can still be mid-flight,
                    // and Timer.Dispose() above doesn't cancel or wait for it -- awaiting it here
                    // means the stop commands below are always the last word sent to the mount,
                    // instead of racing a reapply that could otherwise silently re-enable
                    // tracking (NINA's ASCOM SetCustomTrackingRate does this as a side effect).
                    await pending.ConfigureAwait(false);
                }

                telescopeMediator.SetTrackingEnabled(false);
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

        private static async void Reapply(ITelescopeMediator telescopeMediator, IGuiderMediator? guiderMediator, IProfileService profileService, OrbitalObjectType objectType, string targetName, bool guiding, CancellationToken ct) {
            // Registered before awaiting so CheckMeridian can find and await this exact tick if
            // it fires mid-flight (see CheckMeridian's own comment) -- cleared again once done,
            // whether it completed, failed, or was cancelled.
            var work = ReapplyCore(telescopeMediator, guiderMediator, profileService, objectType, targetName, guiding, ct);
            lock (Gate) { inFlightReapply = work; }
            try {
                await work.ConfigureAwait(false);
            } finally {
                lock (Gate) {
                    if (inFlightReapply == work) inFlightReapply = null;
                }
            }
        }

        private static async Task ReapplyCore(ITelescopeMediator telescopeMediator, IGuiderMediator? guiderMediator, IProfileService profileService, OrbitalObjectType objectType, string targetName, bool guiding, CancellationToken ct) {
            if (ct.IsCancellationRequested) return;

            // Same capability check as Track()'s own -- see its doc comment for the driver
            // (ASCOM OnStep) that surfaced this. Checked once per tick since a driver's own
            // capability doesn't change mid-session, but re-fetching info.CanSet* rather than
            // caching the original Track() call's result means a mount reconnected with a fixed
            // driver mid-session would self-correct on the very next tick.
            var telescopeInfo = telescopeMediator.GetInfo();
            bool canSetBaseRate = telescopeInfo.CanSetRightAscensionRate && telescopeInfo.CanSetDeclinationRate;

            if (canSetBaseRate) {
                try {
                    var trackingItem = new SetPerihelionTrackingRate(telescopeMediator, profileService) { ObjectType = objectType, TargetName = targetName };
                    await trackingItem.Execute(new Progress<ApplicationStatus>(), ct);

                    if (trackingItem.LastAppliedRate is OrbitalRate rate) {
                        // Reported immediately, before attempting guiding below -- a guiding
                        // hiccup this tick shouldn't leave the UI showing a stale rate/timestamp
                        // when the mount's own rate genuinely was just refreshed successfully.
                        // Reporting only after both steps succeed would let a guiding failure
                        // silently suppress a correct tracking-rate update from reaching the panel.
                        QuickTrackStatus.Applied(rate);
                        // Info, not Debug -- this was originally Debug and, on a default PINS log
                        // level, never showed up at all, making a 15-minute re-apply session
                        // look indistinguishable from a silently-dead timer purely because of
                        // log-level filtering. This is the one line that proves the timer is
                        // actually still firing.
                        Logger.Info($"Perihelion: auto re-applied tracking rate for {targetName} -- RA {rate.RaArcsecPerSec:F4} arcsec/s, Dec {rate.DecArcsecPerSec:F4} arcsec/s");
                    }
                } catch (Exception ex) {
                    // Swallowed -- this runs unattended on a background timer with no HTTP caller
                    // to report to, and the next scheduled tick retries regardless. Logged so a
                    // genuine recurring failure (mount disconnected, object dropped out of the
                    // feed) is still visible in the NINA log rather than silently going nowhere.
                    QuickTrackStatus.Failed(ex.Message);
                    Logger.Error($"Perihelion: auto re-apply failed for {targetName}: {ex.Message}");
                    return;
                }
            }

            // Deliberately its own try/catch, separate from the tracking-rate application above
            // -- see Track()'s own identical split for why a guiding hiccup shouldn't be
            // conflated with the mount's own (already-succeeded) tracking-rate refresh.
            bool guidingOnlyFallback = false;
            if (guiding && guiderMediator != null) {
                try {
                    var guiderItem = new SetPerihelionGuiderShiftRate(telescopeMediator, guiderMediator, profileService) { ObjectType = objectType, TargetName = targetName };
                    await guiderItem.Execute(new Progress<ApplicationStatus>(), ct);
                    QuickTrackStatus.GuidingSucceeded();
                    // Same guiding-only fallback as Track() -- see its own doc comment. The
                    // mount never got a base rate this tick (or any tick), so the guider's own
                    // refreshed shift rate is the only thing actually keeping this on target.
                    if (!canSetBaseRate) {
                        guidingOnlyFallback = true;
                        if (guiderItem.LastAppliedRate is OrbitalRate guiderRate) {
                            QuickTrackStatus.Applied(guiderRate);
                            Logger.Info($"Perihelion: auto re-applied guiding-only shift rate for {targetName} -- RA {guiderRate.RaArcsecPerSec:F4} arcsec/s, Dec {guiderRate.DecArcsecPerSec:F4} arcsec/s");
                        }
                    }
                } catch (Exception ex) {
                    QuickTrackStatus.GuidingFailed(ex.Message);
                    Logger.Warning($"Perihelion: auto re-apply's guider shift failed for {targetName}: {ex.Message}");
                }
            }

            if (!canSetBaseRate && !guidingOnlyFallback) {
                // Nothing actually tracked this tick -- the mount can't take a base rate and
                // guiding-only didn't work either (disabled, or its own attempt above failed).
                QuickTrackStatus.Failed($"{telescopeInfo.Name} does not support a custom tracking rate, and guiding-only fallback is unavailable this tick");
                Logger.Error($"Perihelion: auto re-apply for {targetName} tracked nothing this tick -- {telescopeInfo.Name} has no base rate support and guiding-only fallback failed or is disabled");
                return;
            }

            QuickTrackStatus.SetGuidingOnlyFallback(guidingOnlyFallback);
        }
    }
}
