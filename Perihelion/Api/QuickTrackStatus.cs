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
            string? LastError);

        private static Snapshot current = new(false, null, null, false, null, null, null, null, null, false, null);

        public static Snapshot Current => current;

        public static void Started(OrbitalObjectType objectType, string targetName, bool guiding, int? autoReapplyMinutes) {
            current = current with {
                Active = true,
                ObjectType = objectType.ToString(),
                TargetName = targetName,
                Guiding = guiding,
                AutoReapplyMinutes = autoReapplyMinutes,
                StartedUtc = DateTime.UtcNow,
            };
        }

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

        public static void Stopped() {
            current = new Snapshot(false, null, null, false, null, null, null, null, null, false, null);
        }
    }
}
