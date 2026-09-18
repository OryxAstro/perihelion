using NINA.Astrometry;
using NINA.Core.Model.Equipment;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.Mediator;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Trigger.Autofocus;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.ViewModel.Sequencer;
using Perihelion.Astrometry;
using Perihelion.SequenceItems;
using System.Linq;
using System.Reflection;

namespace Perihelion.Sequencing {

    /// <summary>
    /// Builds the same sequence shape Touch-N-Stars' own buildPerihelionSequence.js does
    /// (Center honoring a captured offset -> SetPerihelionTrackingRate [+ StartGuiding
    /// + SetPerihelionGuiderShiftRate] -> a filter-switch + exposure loop, plus an optional
    /// autofocus trigger) -- but built directly as NINA.Sequencer C# objects rather than
    /// hand-rolled JSON matching NINA's own serialization contract. Running in-process (the
    /// native dockable panel, not an HTTP route) makes this the natural approach: every item
    /// comes from ISequencerFactory, which resolves the exact same MEF-composed instance NINA's
    /// own sequencer editor would hand you for a drag-dropped item, dependencies and all -- no
    /// need to know or supply any of Center/TakeExposure/SwitchFilter's own (quite long)
    /// constructor parameter lists.
    /// </summary>
    public static class PerihelionSequenceBuilder {
        /// <summary>
        /// ISequencerFactory has no direct MEF import path at all -- confirmed from its own
        /// source (NINA.Sequencer/SequencerFactory.cs): it's a plain class registered in NINA's
        /// separate Microsoft.Extensions.DependencyInjection container, constructed there from
        /// MEF-aggregated item/condition/trigger lists, but never itself exported via [Export]
        /// for MEF to hand back out. The only way in is through SequenceMediator's own private
        /// `sequenceNavigation` field (confirmed against its source, SequenceMediator.cs) --
        /// one reflection hop, not two: nitr57/ninaAPI's own Sequence.cs reflects a SECOND
        /// private field (`factory`) on the nav object itself, but ISequenceNavigationVM already
        /// exposes the exact same instance publicly via Sequence2VM.SequencerFactory (confirmed
        /// from ISequence2VM.cs) -- ninaAPI's own CoreUtility.GetSequenceRoot extension uses this
        /// same one-hop-then-public-property pattern for the sequence root (see
        /// ResolveSequenceRoot below), just not for the factory specifically. Fewer private
        /// fields relied on means less that can silently break on a future NINA version. Returns
        /// null if the sequencer hasn't finished starting up yet, or if the one reflected field
        /// is missing -- callers must treat null as "not available right now", not throw.
        /// </summary>
        public static ISequencerFactory? ResolveFactory(ISequenceMediator mediator) {
            return ResolveSequenceNavigation(mediator)?.Sequence2VM.SequencerFactory;
        }

        /// <summary>The actual root of the currently loaded Advanced Sequence -- same one-hop
        /// reflection as ResolveFactory above, then the same public property chain
        /// nitr57/ninaAPI's own CoreUtility.GetSequenceRoot extension uses
        /// (Sequence2VM.Sequencer.MainContainer). Used for adding a trigger to the sequence's own
        /// Global Triggers rather than a specific target's local ones -- see
        /// EnsureGlobalMeridianFlipTrigger's own doc comment for why that distinction matters.
        /// Returns the CONCRETE SequenceRootContainer, not the ISequenceRootContainer interface
        /// it's declared as on ISequencer.MainContainer -- confirmed from SequenceContainer.cs's
        /// own source that this matters: Add(ISequenceItem) (the only overload
        /// ISequenceContainer/ISequenceRootContainer expose) just appends to Items unconditionally,
        /// with no runtime type check at all, while Add(ISequenceTrigger) (only reachable when
        /// the call site's declared type is the concrete SequenceContainer or a subclass, since
        /// C# overload resolution is based on static type) correctly appends to Triggers instead.
        /// Calling root.Add(trigger) through the interface would silently misfile the trigger
        /// into Items instead of Triggers.</summary>
        public static SequenceRootContainer? ResolveSequenceRoot(ISequenceMediator mediator) {
            return ResolveSequenceNavigation(mediator)?.Sequence2VM.Sequencer.MainContainer as SequenceRootContainer;
        }

        private static ISequenceNavigationVM? ResolveSequenceNavigation(ISequenceMediator mediator) {
            if (!mediator.Initialized || mediator is not SequenceMediator concrete) return null;
            return typeof(SequenceMediator)
                .GetField("sequenceNavigation", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(concrete) as ISequenceNavigationVM;
        }

        /// <summary>Adds a MeridianFlipTrigger to the sequence's own Global Triggers (the root
        /// container's own Triggers list -- SequenceContainer.Add() routes an ISequenceTrigger
        /// there automatically) instead of nesting it inside the target container
        /// BuildTargetContainer just built -- a meridian flip protection is conventionally set
        /// once, covering every target that runs afterward, matching how a user would add this
        /// trigger themselves via the sequencer's own UI. Skips adding a second one if a
        /// MeridianFlipTrigger already exists globally, so multiple Add to Sequence calls across
        /// several targets in one sequence don't pile up duplicates.</summary>
        public static void EnsureGlobalMeridianFlipTrigger(ISequencerFactory factory, SequenceRootContainer root) {
            if (root.Triggers.Any(t => t is MeridianFlipTrigger)) return;
            root.Add(factory.GetTrigger<MeridianFlipTrigger>());
        }

        /// <summary>Filter null means "leave the wheel alone" -- the installed NINA.Sequencer
        /// (3.2.0.9001) SwitchFilter takes a FilterInfo via its settable Filter property,
        /// not the string-based ComboBoxText/ Xfilter expression system added in a later,
        /// currently-unshipped version (confirmed by inspecting the actual installed DLL, not
        /// assumed from upstream source -- a previously-hit version-drift trap in this
        /// project). Resolving the name to a FilterInfo is the caller's job, since that needs
        /// IProfileService, which this builder deliberately doesn't depend on.</summary>
        public sealed record ExposureSettings(NINA.Core.Model.Equipment.FilterInfo? Filter, double ExposureSeconds, int FrameCount);

        public static DeepSkyObjectContainer BuildTargetContainer(
            ISequencerFactory factory,
            OrbitalObjectType objectType,
            string targetName,
            Coordinates trueCoordinates,
            Coordinates slewCoordinates,
            bool guiding,
            double? rotationAngle,
            double? autofocusMinutes,
            ExposureSettings exposure) {
            var dso = factory.GetContainer<DeepSkyObjectContainer>();
            dso.Name = targetName;
            dso.Target.TargetName = targetName;
            // The container's own Target holds the FRAMED position/rotation directly (isbeorn's
            // own suggestion, confirmed against NINA.Sequencer 3.2.0.9001's own source: both
            // DeepSkyObjectContainer.Target_OnCoordinatesChanged and AddAdvancedTarget's own
            // attach path cascade AfterParentChanged down to every child, and CenterAndRotate's
            // own AfterParentChanged unconditionally copies Coordinates/PositionAngle straight
            // from this same Target -- so setting the framed values here means every future
            // cascade re-derives the SAME correct values instead of fighting to survive one.
            // This replaces the earlier approach (setting Inherited=false directly on Center/
            // CenterAndRotate) which the cascade discarded on every AddAdvancedTarget attach --
            // see PerihelionSequenceBuilder's own git history / perihelion_isbeorn_review's
            // finding #8 for why that needed a second, separate ApplyFraming() call after attach
            // and a self-healing loop for PositionAngle specifically. Neither is needed now.
            dso.Target.InputCoordinates = new InputCoordinates(slewCoordinates);
            dso.Target.PositionAngle = rotationAngle ?? 0;

            // rotationAngle null means plain Center (no rotator involved) -- with no rotator
            // connected, CenterAndRotate fails validation ("rotator not connected") and blocks
            // the whole sequence, so this can't be unconditional. Left at its Inherited=true
            // default so it just reads Target's own Coordinates/PositionAngle above -- no manual
            // assignment needed on the item itself at all.
            if (rotationAngle is double) {
                dso.Add(factory.GetItem<CenterAndRotate>());
            } else {
                dso.Add(factory.GetItem<Center>());
            }

            var trackingRate = factory.GetItem<SetPerihelionTrackingRate>();
            trackingRate.ObjectType = objectType;
            trackingRate.TargetName = targetName;
            // The offset this target was framed with, in the same units the live-refresh loop
            // works in -- kept as a fixed delta from the comet's own true position (not the
            // framed position itself), since the true position is what moves tick to tick and
            // the offset is the one thing that should stay constant while it does. See
            // SetPerihelionTrackingRate's own RefreshTargetCoordinates for how this gets
            // reapplied to Target on every tick.
            trackingRate.OffsetRaHours = slewCoordinates.RA - trueCoordinates.RA;
            trackingRate.OffsetDecDeg = slewCoordinates.Dec - trueCoordinates.Dec;
            trackingRate.FramingPositionAngle = rotationAngle;
            dso.Add(trackingRate);

            if (guiding) {
                var startGuiding = factory.GetItem<StartGuiding>();
                startGuiding.ForceCalibration = false;
                dso.Add(startGuiding);

                var guiderShift = factory.GetItem<SetPerihelionGuiderShiftRate>();
                guiderShift.ObjectType = objectType;
                guiderShift.TargetName = targetName;
                dso.Add(guiderShift);
            }

            // Unlike autofocus/meridian flip, this isn't a session-shaping choice with
            // tradeoffs (extra exposure time, an interruption) -- it's a background correction
            // with no cost to the imaging run, so it's unconditional here, matching
            // SetPerihelionTrackingRate's own always-on coordinate-refresh loop rather than the
            // opt-in toggles above it. See PerihelionReapplyTrigger's own doc comment for why
            // Add to Sequence needed this at all (it didn't re-apply the rate after the initial
            // Execute(), unlike Quick Track).
            dso.Add(factory.GetTrigger<Perihelion.SequenceItems.PerihelionReapplyTrigger>());

            var imagingInstructions = factory.GetContainer<SequentialContainer>();
            imagingInstructions.Name = "Target Imaging Instructions";

            var filterLoop = factory.GetContainer<SequentialContainer>();
            filterLoop.Name = exposure.Filter != null
                ? $"{exposure.Filter.Name} x {exposure.ExposureSeconds}s"
                : $"Exposure Loop - {targetName}";

            var loopCondition = factory.GetCondition<LoopCondition>();
            loopCondition.Iterations = exposure.FrameCount;
            filterLoop.Add(loopCondition);

            // Null Filter means "leave the wheel alone" -- same convention as the
            // Touch-N-Stars builder, not a magic filter position.
            if (exposure.Filter != null) {
                var switchFilter = factory.GetItem<SwitchFilter>();
                switchFilter.Filter = exposure.Filter;
                filterLoop.Add(switchFilter);
            }

            var takeExposure = factory.GetItem<TakeExposure>();
            takeExposure.ExposureTime = exposure.ExposureSeconds;
            takeExposure.Gain = -1;
            takeExposure.Offset = -1;
            takeExposure.Binning = new BinningMode(1, 1);
            takeExposure.ImageType = "LIGHT";
            takeExposure.ExposureCount = 0;
            filterLoop.Add(takeExposure);

            imagingInstructions.Add(filterLoop);
            dso.Add(imagingInstructions);

            if (autofocusMinutes is double minutes && minutes > 0) {
                var afTrigger = factory.GetTrigger<AutofocusAfterTimeTrigger>();
                afTrigger.Amount = minutes;
                dso.Add(afTrigger);
            }

            return dso;
        }

    }
}
