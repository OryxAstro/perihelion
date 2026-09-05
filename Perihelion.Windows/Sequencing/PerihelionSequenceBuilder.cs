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
    /// Builds the same real-sequence shape Touch-N-Stars' own buildPerihelionSequence.js does
    /// (Center honoring a captured offset -> SetPerihelionTrackingRate [+ StartGuiding
    /// + SetPerihelionGuiderShiftRate] -> a filter-switch + exposure loop, plus an optional
    /// autofocus trigger) -- but built directly as real NINA.Sequencer C# objects rather than
    /// hand-rolled JSON matching NINA's own serialization contract. Running in-process (the
    /// native dockable panel, not an HTTP route) makes this the natural approach: every item
    /// comes from ISequencerFactory, which resolves the exact same MEF-composed instance NINA's
    /// own sequencer editor would hand you for a drag-dropped item, dependencies and all -- no
    /// need to know or supply any of Center/TakeExposure/SwitchFilter's own (quite long)
    /// constructor parameter lists.
    /// </summary>
    public static class PerihelionSequenceBuilder {
        /// <summary>
        /// ISequencerFactory has no direct MEF import path at all -- confirmed from its own real
        /// source (NINA.Sequencer/SequencerFactory.cs): it's a plain class registered in NINA's
        /// separate Microsoft.Extensions.DependencyInjection container, constructed there from
        /// MEF-aggregated item/condition/trigger lists, but never itself exported via [Export]
        /// for MEF to hand back out. The only way in is through SequenceMediator's own private
        /// `sequenceNavigation` field (confirmed against its real source, SequenceMediator.cs) --
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
        /// own real source that this matters: Add(ISequenceItem) (the only overload
        /// ISequenceContainer/ISequenceRootContainer expose) just appends to Items unconditionally,
        /// with no runtime type check at all, while Add(ISequenceTrigger) (only reachable when
        /// the call site's declared type is the concrete SequenceContainer or a subclass, since
        /// C# overload resolution is based on static type) correctly appends to Triggers instead.
        /// Calling root.Add(trigger) through the interface would have silently misfiled the
        /// trigger into Items instead of Triggers -- caught by reading this source before
        /// shipping, not by a second round of real-hardware trial and error.</summary>
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
        /// there automatically, same mechanism as any per-container Add) instead of nesting it
        /// inside the target container BuildTargetContainer just built. Real user feedback
        /// (2026-09-05): a per-target trigger showed up under that one target's own local
        /// "Triggers" section in the sequencer UI, not the sequence-wide Global Triggers area a
        /// meridian flip protection is conventionally set once for, covering every target that
        /// runs afterward -- exactly matching how a user manually adding this trigger themselves
        /// via the sequencer's own UI would normally do it. Skips adding a second one if a
        /// MeridianFlipTrigger already exists globally (e.g. from an earlier Add to Sequence
        /// call, or one the user added themselves) -- redundant duplicates would just mean the
        /// same check runs twice for no benefit, and silently piling one up per "Add to Sequence"
        /// click across several targets in one sequence would be real clutter.</summary>
        public static void EnsureGlobalMeridianFlipTrigger(ISequencerFactory factory, SequenceRootContainer root) {
            if (root.Triggers.Any(t => t is MeridianFlipTrigger)) return;
            root.Add(factory.GetTrigger<MeridianFlipTrigger>());
        }

        /// <summary>Filter null means "leave the wheel alone" -- the installed NINA.Sequencer
        /// (3.2.0.9001) SwitchFilter takes a real FilterInfo via its settable Filter property,
        /// not the string-based ComboBoxText/ Xfilter expression system added in a later,
        /// currently-unshipped version (confirmed by inspecting the actual installed DLL, not
        /// assumed from upstream source -- a real, previously-hit version-drift trap in this
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
            dso.Target.InputCoordinates = new InputCoordinates(trueCoordinates);

            // rotationAngle null means plain Center (no rotator involved at all) -- real user
            // feedback (2026-09-05): with no rotator connected, CenterAndRotate fails validation
            // ("rotator not connected") and blocks the whole sequence, so this can no longer be
            // unconditional. A real angle means CenterAndRotate instead, for users who do have a
            // rotator and want real framing control over it. Each branch sets Inherited/
            // Coordinates on its own concrete type rather than through a shared base variable --
            // deliberate: this project has already hit a real version-drift trap once
            // (SwitchFilter's Filter property vs. a newer, currently-unshipped ComboBoxText
            // system, confirmed only by inspecting the actual installed DLL) from assuming
            // upstream NINA.Sequencer source matches what's actually installed here, and Center/
            // CenterAndRotate's own common base type was one such assumption that turned out not
            // to resolve against this exact installed package version. Not worth the same risk
            // twice for two lines of duplication.
            if (rotationAngle is double angle) {
                var rotate = factory.GetItem<CenterAndRotate>();
                rotate.PositionAngle = angle;
                // Explicitly false, not the JSON export's "Inherited: true" -- Inherited mode
                // resyncs Coordinates from the parent container's own Target the moment this
                // item's Parent is set, which would silently discard the offset-adjusted
                // slewCoordinates below the instant Add() attaches it. Setting Coordinates
                // directly, with Inherited off, is the only way to guarantee this item slews to
                // exactly what was asked for.
                rotate.Inherited = false;
                rotate.Coordinates = new InputCoordinates(slewCoordinates);
                dso.Add(rotate);
            } else {
                var center = factory.GetItem<Center>();
                center.Inherited = false;
                center.Coordinates = new InputCoordinates(slewCoordinates);
                dso.Add(center);
            }

            var trackingRate = factory.GetItem<SetPerihelionTrackingRate>();
            trackingRate.ObjectType = objectType;
            trackingRate.TargetName = targetName;
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
