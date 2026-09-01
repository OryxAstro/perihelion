using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using Perihelion.Astrometry;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Perihelion.SequenceItems {

    /// <summary>
    /// Guider counterpart to SetPerihelionTrackingRate. Needed whenever the mount is guided:
    /// without this, the guider sees the star drift away from its lock position (because
    /// SetPerihelionTrackingRate deliberately made the mount track off-sidereal) and issues
    /// corrective pulses to fight that drift -- cancelling out the whole point of the custom
    /// rate. Telling the guider to shift its own lock position at the same rate avoids that.
    /// Self-contained like its telescope counterpart: computes the rate itself rather than
    /// depending on a parent container's coordinates.
    /// </summary>
    [ExportMetadata("Name", "Set Perihelion Guider Shift Rate")]
    [ExportMetadata("Description", "Shifts the guider's lock position at the same rate as a comet or asteroid's custom tracking rate, so guiding doesn't fight the intentional drift.")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Guider")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SetPerihelionGuiderShiftRate : SequenceItem, IValidatable {
        private static readonly HttpClient HttpClient = new();

        private readonly IGuiderMediator guiderMediator;

        [ImportingConstructor]
        public SetPerihelionGuiderShiftRate(IGuiderMediator guiderMediator) {
            this.guiderMediator = guiderMediator;
        }

        private SetPerihelionGuiderShiftRate(SetPerihelionGuiderShiftRate cloneMe) : this(cloneMe.guiderMediator) {
            CopyMetaData(cloneMe);
            ObjectType = cloneMe.ObjectType;
            TargetName = cloneMe.TargetName;
        }

        public override object Clone() {
            return new SetPerihelionGuiderShiftRate(this);
        }

        [JsonProperty]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public OrbitalObjectType ObjectType { get; set; } = OrbitalObjectType.Comet;

        /// <summary>Same TargetName contract as SetPerihelionTrackingRate -- keep these two items' values in sync when both are placed in the same sequence.</summary>
        [JsonProperty]
        public string TargetName { get; set; } = string.Empty;

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // A shift rate with nothing actively guiding is a no-op at best (PHD2 has no lock
            // position yet to shift) -- calling StartGuiding here rather than just checking some
            // "is guiding" flag is deliberate: NINA/PHD2 doesn't expose one via GuiderInfo at
            // all (Connected is the only state field there), but PHD2Guider.StartGuiding already
            // checks PHD2's own app state itself and returns almost immediately
            // ("Phd2 - App is already guiding. Skipping start guiding") when it's already
            // running, and otherwise does exactly what's needed anyway -- calibrate if needed,
            // wait for a locked star, and wait for the settle the profile's own PHD2 settings
            // define (GuiderSettings.SettlePixels/SettleTime/SettleTimeout) -- before returning.
            // In the "Add to Sequence" path this duplicates that sequence's own explicit
            // StartGuiding item, which is harmless: the second call is just that same fast
            // already-guiding check again.
            if (!await guiderMediator.StartGuiding(false, progress, token)) {
                throw new SequenceEntityFailedException("Could not start guiding");
            }

            var rate = await OrbitalTracking.ComputeOrbitalRateAsync(HttpClient, ObjectType, TargetName, DateTime.UtcNow, token);
            if (rate == null) {
                throw new SequenceEntityFailedException($"Could not find current orbital elements for {ObjectType} '{TargetName}'");
            }

            var shiftRate = SiderealShiftTrackingRate.Create(rate.Value.RaArcsecPerSec, rate.Value.DecArcsecPerSec);
            if (!await guiderMediator.SetShiftRate(shiftRate, token)) {
                throw new SequenceEntityFailedException($"Setting guider shift rate to {shiftRate} failed");
            }
        }

        public bool Validate() {
            var i = new List<string>();
            var info = guiderMediator.GetInfo();
            if (!info.Connected) {
                i.Add("Guider not connected");
            } else if (!info.CanSetShiftRate) {
                i.Add($"{info.Name} does not support shift rates. Try PHD2.");
            } else if (string.IsNullOrWhiteSpace(TargetName)) {
                i.Add("No target name set");
            }

            Issues = i;
            return i.Count == 0;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SetPerihelionGuiderShiftRate)}, ObjectType: {ObjectType}, TargetName: {TargetName}";
        }
    }
}
