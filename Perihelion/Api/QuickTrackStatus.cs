using Perihelion.Astrometry;
using System;

namespace Perihelion.Api {

    /// <summary>
    /// Live state for whatever Quick Track session is currently running, backing
    /// GET /perihelion/api/status -- exists specifically so the Touch-N-Stars panel (and anyone
    /// debugging against the INDI/ASCOM control panel directly) can see the *actual* rate that
    /// was last computed and sent, rather than trusting that "the toggle was on when I clicked
    /// the button" still reflects reality. An immutable snapshot replaced by reference on every
    /// update -- simpler and just as safe as locking a bag of individual static fields for this
    /// low-concurrency use (one background timer, occasional HTTP calls), since a reference swap
    /// is atomic and a reader always sees one fully-formed snapshot, never a torn one.
    /// </summary>
    internal static class QuickTrackStatus {
        public sealed record Snapshot(
            bool Active,
            string? ObjectType,
            string? TargetName,
            bool Guiding,
            int? AutoReapplyMinutes,
            DateTime? StartedUtc,
            DateTime? LastAppliedUtc,
            double? LastRaArcsecPerSec,
            double? LastDecArcsecPerSec,
            bool LastApplySucceeded,
            string? LastError,
            string? StopReason,
            string? GuidingError,
            bool GuidingOnlyFallback);

        private static Snapshot current = new(false, null, null, false, null, null, null, null, null, false, null, null, null, false);

        public static Snapshot Current => current;

        public static void Started(OrbitalObjectType objectType, string targetName, bool guiding, int? autoReapplyMinutes) {
            current = current with {
                Active = true,
                ObjectType = objectType.ToString(),
                TargetName = targetName,
                Guiding = guiding,
                AutoReapplyMinutes = autoReapplyMinutes,
                StartedUtc = DateTime.UtcNow,
                // A previous session's automatic-stop reason (e.g. the meridian safety cutoff)
                // means nothing for this new one -- without clearing it here, `with` semantics
                // elsewhere would otherwise carry it forward indefinitely. Same for a previous
                // session's guiding error.
                StopReason = null,
                GuidingError = null,
                GuidingOnlyFallback = false,
            };
        }

        /// <summary>
        /// The mount's own tracking-rate application succeeded -- deliberately independent of
        /// whatever the guider half of the same attempt does (see GuidingFailed/GuidingSucceeded
        /// below). LastApplySucceeded/LastError describe ONLY this: whether the mount actually
        /// received a correct custom tracking rate. Conflating a guiding hiccup into this value
        /// (as an earlier version did) meant a Quick Track attempt where the mount was already
        /// correctly tracking could still read as an outright failure just because PHD2 couldn't
        /// find a star -- misleading, since the thing Quick Track exists to do had already
        /// succeeded.
        /// </summary>
        public static void Applied(OrbitalRate rate) {
            current = current with {
                LastAppliedUtc = DateTime.UtcNow,
                LastRaArcsecPerSec = rate.RaArcsecPerSec,
                LastDecArcsecPerSec = rate.DecArcsecPerSec,
                LastApplySucceeded = true,
                LastError = null,
            };
        }

        public static void Failed(string error) {
            current = current with {
                LastAppliedUtc = DateTime.UtcNow,
                LastApplySucceeded = false,
                LastError = error,
            };
        }

        /// <summary>The guider-shift half of a Quick Track attempt failed -- tracked separately
        /// from LastApplySucceeded/LastError (which describe only the mount's own tracking rate)
        /// so a guiding hiccup doesn't make an otherwise-successful tracking rate application
        /// read as an outright failure. See Track()/Reapply()'s own call sites for why this is
        /// caught and reported independently rather than propagated as a generic Failed().</summary>
        public static void GuidingFailed(string error) {
            current = current with { GuidingError = error };
        }

        public static void GuidingSucceeded() {
            current = current with { GuidingError = null };
        }

        /// <summary>Set once per Track() attempt (not toggled independently like GuidingFailed/
        /// GuidingSucceeded above) -- true means the mount itself never received a custom
        /// tracking rate at all (its driver doesn't support one; confirmed real case: at least
        /// one ASCOM OnStep driver build reports CanSetRightAscensionRate/CanSetDeclinationRate
        /// both false) and the guider's own shift rate is the *entire* tracking mechanism for
        /// this session, not a companion to a base-rate change. The panel needs this to avoid
        /// implying the mount is tracking off-sidereal when it's actually still on plain
        /// sidereal the whole time, with PHD2's own active guide-correction loop (driven by a
        /// continuously-shifting lock position) doing the real work instead.</summary>
        public static void SetGuidingOnlyFallback(bool guidingOnlyFallback) {
            current = current with { GuidingOnlyFallback = guidingOnlyFallback };
        }

        /// <param name="reason">Null for a plain manual stop (the user already knows why --
        /// they just pressed Stop). Set for an automatic stop the user didn't initiate -- in
        /// particular the meridian safety cutoff (see QuickTrackReapply's own CheckMeridian) --
        /// so the panel can show a real, unmissable explanation rather than the session just
        /// silently ending.</param>
        public static void Stopped(string? reason = null) {
            current = new Snapshot(false, null, null, false, null, null, null, null, null, false, null, reason, null, false);
        }
    }
}
