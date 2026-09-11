using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Perihelion.SequenceItems {

    /// <summary>
    /// Periodically re-executes a sibling Set Perihelion Tracking Rate (and Set Perihelion
    /// Guider Shift Rate, if present) within the same container -- the sequence-side counterpart
    /// to Quick Track's own QuickTrackReapply timer. Without this, Add to Sequence applies the
    /// tracking rate exactly once, when the sequencer first reaches SetPerihelionTrackingRate's
    /// own Execute(), and never again for the rest of a potentially many-hour unattended run --
    /// unlike Quick Track, which has re-applied on a timer since it was first built (see
    /// QuickTrackReapply.cs). SetPerihelionTrackingRate's own AfterParentChanged background loop
    /// solves a different problem (keeping Target.InputCoordinates fresh for a still-queued
    /// slew) and never touches the applied rate itself once tracking has actually started -- see
    /// that file's own "Live coordinate refresh" region.
    ///
    /// Deliberately stateless with respect to ObjectType/TargetName: rather than duplicating
    /// them as its own properties (which could drift from the tracking item if a user edits its
    /// target after Add to Sequence builds it), this trigger looks up its sibling items in
    /// Parent.Items and calls their own Execute() directly -- same sibling-lookup pattern
    /// SetPerihelionTrackingRate.Validate() already uses to find an optional guider-shift
    /// fallback. Reuses the exact same instances already in the sequence tree (not fresh copies,
    /// unlike QuickTrackReapply's standalone construction, which has no sequence tree to borrow
    /// from) so LastAppliedRate on those items stays the true source of truth for anything
    /// inspecting them.
    /// </summary>
    [ExportMetadata("Name", "Perihelion Reapply Tracking Rate")]
    [ExportMetadata("Description", "Periodically recomputes and resends a sibling tracking/guider-shift rate so a long unattended sequence stays accurate as the object's true rate drifts.")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Telescope")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class PerihelionReapplyTrigger : SequenceTrigger, IValidatable {

        public PerihelionReapplyTrigger() : base() {
        }

        private PerihelionReapplyTrigger(PerihelionReapplyTrigger cloneMe) : this() {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new PerihelionReapplyTrigger(this);
        }

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        // Same shared setting Quick Track's own reapply timer reads (PerihelionPlugin's
        // QuickTrackReapplyIntervalSeconds) -- not renamed/duplicated here even though its name
        // predates this trigger, since it's also the wire-level JSON field name Touch-N-Stars'
        // frontend already depends on; renaming it needs a matching frontend change.
        private DateTime lastAppliedUtc = DateTime.MinValue;

        public override void SequenceBlockInitialize() {
            lastAppliedUtc = DateTime.UtcNow;
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            var trackingItem = FindTrackingItem();
            if (trackingItem == null) return false;

            // LastAppliedRate is only ever set at the end of a successful
            // SetPerihelionTrackingRate.Execute() -- staying null until then means the sequence
            // hasn't reached its own natural first application of the rate yet (still unparking,
            // still slewing/centering/settling). Without this gate, a short user-configured
            // interval (the Options page explicitly invites shortening it "for an object moving
            // unusually fast") could let ShouldTrigger fire purely on elapsed-since-block-start
            // time, reapplying a rate before the mount has even finished its first center --
            // this is that guard.
            if (trackingItem.LastAppliedRate == null) return false;

            var intervalSeconds = Math.Max(PerihelionPlugin.MinReapplyIntervalSeconds, PerihelionPlugin.Instance?.QuickTrackReapplyIntervalSeconds ?? 900);
            return (DateTime.UtcNow - lastAppliedUtc) >= TimeSpan.FromSeconds(intervalSeconds);
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var trackingItem = FindTrackingItem();
            if (trackingItem == null) return;

            try {
                await trackingItem.Execute(progress, token);

                var guiderItem = Parent?.Items?.OfType<SetPerihelionGuiderShiftRate>().FirstOrDefault();
                if (guiderItem != null) {
                    await guiderItem.Execute(progress, token);
                }
            } finally {
                // Reset regardless of success/failure -- a transient failure (feed briefly
                // unreachable, mount momentarily busy) shouldn't make the next tick fire
                // immediately on top of it; the interval is the retry cadence too, same as
                // QuickTrackReapply's own swallow-and-log-and-wait-for-the-next-tick behavior.
                lastAppliedUtc = DateTime.UtcNow;
            }
        }

        private SetPerihelionTrackingRate? FindTrackingItem() {
            return Parent?.Items?.OfType<SetPerihelionTrackingRate>().FirstOrDefault();
        }

        public bool Validate() {
            var i = new List<string>();
            if (FindTrackingItem() == null) {
                i.Add("No \"Set Perihelion Tracking Rate\" item found in this container to reapply.");
            }
            Issues = i;
            return i.Count == 0;
        }

        public override string ToString() {
            return $"Trigger: {nameof(PerihelionReapplyTrigger)}";
        }
    }
}
