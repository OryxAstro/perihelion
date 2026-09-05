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
using Perihelion.Astrometry;
using Perihelion.SequenceItems;
using System.Reflection;

namespace Perihelion.Sequencing {

    /// <summary>
    /// Builds the same real-sequence shape Touch-N-Stars' own buildPerihelionSequence.js does
    /// (CenterAndRotate honoring a captured offset -> SetPerihelionTrackingRate [+ StartGuiding
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
        /// for MEF to hand back out. nitr57/ninaAPI's own Sequence.cs hits this same wall and
        /// works around it with exactly this reflection: SequenceMediator (the concrete class
        /// behind ISequenceMediator, which unlike ISequencerFactory IS safely MEF-importable --
        /// see PerihelionPlugin's own updated comment) keeps a private `sequenceNavigation`
        /// field, and that object in turn keeps a private `factory` field that's the actual
        /// ISequencerFactory instance NINA's own sequencer editor uses. Both field names
        /// confirmed directly against NINA's real source (SequenceMediator.cs,
        /// SequenceNavigationVM.cs), not guessed. Returns null if the sequencer hasn't finished
        /// starting up yet, or if either field is missing (e.g. renamed in a future NINA version)
        /// -- callers must treat null as "not available right now", not throw.
        /// </summary>
        public static ISequencerFactory? ResolveFactory(ISequenceMediator mediator) {
            if (!mediator.Initialized || mediator is not SequenceMediator concrete) return null;
            var nav = typeof(SequenceMediator)
                .GetField("sequenceNavigation", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(concrete);
            if (nav == null) return null;
            return nav.GetType()
                .GetField("factory", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(nav) as ISequencerFactory;
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
            bool meridianFlip,
            double? autofocusMinutes,
            ExposureSettings exposure) {
            var dso = factory.GetContainer<DeepSkyObjectContainer>();
            dso.Name = targetName;
            dso.Target.TargetName = targetName;
            dso.Target.InputCoordinates = new InputCoordinates(trueCoordinates);

            var center = factory.GetItem<CenterAndRotate>();
            // Explicitly false, not the JSON export's "Inherited: true" -- Inherited mode
            // resyncs Coordinates from the parent container's own Target the moment this item's
            // Parent is set, which would silently discard the offset-adjusted slewCoordinates
            // below the instant Add() attaches it. Setting Coordinates directly, with Inherited
            // off, is the only way to guarantee this item slews to exactly what was asked for.
            center.Inherited = false;
            center.PositionAngle = 0;
            center.Coordinates = new InputCoordinates(slewCoordinates);
            dso.Add(center);

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

            if (meridianFlip) {
                dso.Add(factory.GetTrigger<MeridianFlipTrigger>());
            }

            if (autofocusMinutes is double minutes && minutes > 0) {
                var afTrigger = factory.GetTrigger<AutofocusAfterTimeTrigger>();
                afTrigger.Amount = minutes;
                dso.Add(afTrigger);
            }

            return dso;
        }
    }
}
