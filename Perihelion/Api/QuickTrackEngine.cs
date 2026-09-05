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
    /// The actual "start/stop Quick Track" logic, shared by every frontend that can trigger it --
    /// originally just PerihelionApiController (the Touch-N-Stars panel's HTTP entry point),
    /// now also PerihelionDockableVM (the native NINA panel, which runs in the same process and
    /// so calls this directly with no HTTP round-trip at all). Extracted rather than duplicated
    /// so the capability-check-first / guiding-only-fallback / meridian-safety reasoning lives in
    /// exactly one place -- see PerihelionApiController's own git history for the original,
    /// single-frontend version of this logic if the reasoning behind a given branch isn't obvious
    /// from its comment here.
    /// </summary>
    public static class QuickTrackEngine {
        public sealed record Result(bool Success, string Message, bool GuidingOnlyFallback);

        public static async Task<Result> StartAsync(
            ITelescopeMediator telescopeMediator,
            IGuiderMediator? guiderMediator,
            IProfileService profileService,
            OrbitalObjectType objectType,
            string targetName,
            bool guiding,
            int? autoReapplyMinutes,
            CancellationToken ct) {
            QuickTrackStatus.Started(objectType, targetName, guiding, autoReapplyMinutes is > 0 ? autoReapplyMinutes : null);

            // Some real mount drivers don't support a custom base tracking rate at all --
            // confirmed real case: an ASCOM OnStep driver build reporting
            // CanSetRightAscensionRate/CanSetDeclinationRate both false (NINA's own
            // TelescopeVM.SetCustomTrackingRate has no fallback for this itself, it just throws
            // NotSupportedException straight up). Checking the capability first, rather than
            // catching that exception, means a driver that genuinely can't do this reads as a
            // deliberate, expected branch here instead of a raw unhandled error every time.
            var telescopeInfo = telescopeMediator.GetInfo();
            bool canSetBaseRate = telescopeInfo.CanSetRightAscensionRate && telescopeInfo.CanSetDeclinationRate;

            try {
                if (canSetBaseRate) {
                    var trackingItem = new SetPerihelionTrackingRate(telescopeMediator, profileService) {
                        ObjectType = objectType,
                        TargetName = targetName,
                    };
                    await trackingItem.Execute(new Progress<ApplicationStatus>(), ct);
                    if (trackingItem.LastAppliedRate is OrbitalRate appliedRate) {
                        QuickTrackStatus.Applied(appliedRate);
                        Logger.Info($"Perihelion: Quick Track applied for {targetName} -- RA {appliedRate.RaArcsecPerSec:F4} arcsec/s, Dec {appliedRate.DecArcsecPerSec:F4} arcsec/s");
                    }
                } else {
                    Logger.Info($"Perihelion: {telescopeInfo.Name} does not support a custom base tracking rate (CanSetRightAscensionRate/CanSetDeclinationRate both false) for {targetName} -- will rely on guiding-only fallback if guiding is enabled");
                }

                // Deliberately its own try/catch, separate from the tracking-rate application
                // above: when canSetBaseRate is true, the mount is already correctly tracking at
                // this point regardless of what happens here, so a guiding hiccup (no PHD2, no
                // lock star within its own retry budget) shouldn't make an otherwise-successful
                // Quick Track attempt read as an outright failure.
                string? guidingError = null;
                bool guidingOnlyFallback = false;
                if (guiding && guiderMediator != null) {
                    try {
                        var guiderItem = new SetPerihelionGuiderShiftRate(guiderMediator, profileService) {
                            ObjectType = objectType,
                            TargetName = targetName,
                        };
                        await guiderItem.Execute(new Progress<ApplicationStatus>(), ct);
                        QuickTrackStatus.GuidingSucceeded();
                        // If the base rate couldn't be set at all, the guider shift IS the whole
                        // tracking mechanism for this session -- PHD2's own native "Comet
                        // Tracking" feature is this exact same mechanism, so this isn't a novel
                        // approach, just driving it programmatically instead of through PHD2's own
                        // dialog. The mount itself stays on plain sidereal; PHD2's active
                        // guide-correction loop, driven by a continuously shifting lock position,
                        // does the real tracking.
                        if (!canSetBaseRate) {
                            guidingOnlyFallback = true;
                            if (guiderItem.LastAppliedRate is OrbitalRate guiderRate) {
                                QuickTrackStatus.Applied(guiderRate);
                                Logger.Info($"Perihelion: Quick Track applied via guiding-only fallback for {targetName} -- RA {guiderRate.RaArcsecPerSec:F4} arcsec/s, Dec {guiderRate.DecArcsecPerSec:F4} arcsec/s");
                            }
                        }
                    } catch (Exception ex) {
                        guidingError = ex.Message;
                        QuickTrackStatus.GuidingFailed(ex.Message);
                        Logger.Warning($"Perihelion: Quick Track guider shift failed for {targetName}: {ex.Message}");
                    }
                }

                if (!canSetBaseRate && !guidingOnlyFallback) {
                    // Neither the mount's own base rate nor a guiding-only fallback worked --
                    // nothing is actually tracking this target, so this is the one combination
                    // that should fail the whole attempt outright.
                    var reason = guiding
                        ? $"{telescopeInfo.Name} does not support a custom tracking rate, and the guiding-only fallback failed: {guidingError}"
                        : $"{telescopeInfo.Name} does not support a custom tracking rate. Enable \"Include guider shift rate\" with an actively guiding, shift-rate-capable guider (e.g. PHD2) to track via guiding only instead.";
                    throw new SequenceEntityFailedException(reason);
                }

                QuickTrackStatus.SetGuidingOnlyFallback(guidingOnlyFallback);

                // Unconditional now, not just when autoReapplyMinutes is set -- QuickTrackReapply
                // always runs its own meridian safety cutoff regardless of that setting (Quick
                // Track has no sequence/trigger infrastructure either way), and only starts the
                // optional reapply sub-timer when a positive interval is actually given.
                QuickTrackReapply.Start(telescopeMediator, guiderMediator, profileService, objectType, targetName, guiding, autoReapplyMinutes is > 0 ? autoReapplyMinutes : null);

                var message = guidingOnlyFallback
                    ? "Quick Track started via guiding only (mount does not support a custom tracking rate)"
                    : guidingError != null
                        ? $"Quick Track started, but guiding could not be started: {guidingError}"
                        : autoReapplyMinutes is > 0
                            ? $"Quick Track started, re-applying every {autoReapplyMinutes} min"
                            : "Quick Track started";
                return new Result(true, message, guidingOnlyFallback);
            } catch (SequenceEntityFailedException ex) {
                QuickTrackStatus.Failed(ex.Message);
                return new Result(false, ex.Message, false);
            } catch (Exception ex) {
                QuickTrackStatus.Failed(ex.Message);
                return new Result(false, $"Unexpected error: {ex.Message}", false);
            }
        }

        public static async Task<Result> StopAsync(ITelescopeMediator? telescopeMediator, IGuiderMediator? guiderMediator, CancellationToken ct) {
            QuickTrackReapply.Stop();
            QuickTrackStatus.Stopped();
            try {
                if (telescopeMediator == null) {
                    return new Result(false, "Perihelion was started before the telescope mediator was available", false);
                }
                var success = telescopeMediator.SetTrackingMode(TrackingMode.Sidereal);
                if (guiderMediator != null) {
                    await guiderMediator.StopShifting(ct);
                }
                return new Result(success, success ? "Back to sidereal tracking" : "Setting tracking mode failed", false);
            } catch (Exception ex) {
                return new Result(false, $"Unexpected error: {ex.Message}", false);
            }
        }
    }
}
