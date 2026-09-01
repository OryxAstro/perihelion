using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
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
    /// Sets the mount's custom RA/Dec tracking rate for a comet or asteroid, computed
    /// in-process (see Perihelion.Astrometry.OrbitalTracking) rather than read from a parent
    /// container's coordinates -- unlike NINA.Joko.Plugin.Orbitals' SetTelescopeShiftRate (which
    /// this item is functionally similar to but was independently designed from, not copied
    /// from; see CLAUDE.md's IP hygiene section), this item is self-contained: it takes the
    /// target's identity directly rather than depending on a specific parent container shape.
    /// </summary>
    [ExportMetadata("Name", "Set Perihelion Tracking Rate")]
    [ExportMetadata("Description", "Sets the mount's custom RA/Dec tracking rate for a comet or asteroid, computed live from its current orbital elements.")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Telescope")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SetPerihelionTrackingRate : SequenceItem, IValidatable {
        // Shared across all instances/executions -- HttpClient is designed to be reused, not
        // created per request (per-request instances risk socket exhaustion under load).
        private static readonly HttpClient HttpClient = new();

        private readonly ITelescopeMediator telescopeMediator;

        [ImportingConstructor]
        public SetPerihelionTrackingRate(ITelescopeMediator telescopeMediator) {
            this.telescopeMediator = telescopeMediator;
        }

        private SetPerihelionTrackingRate(SetPerihelionTrackingRate cloneMe) : this(cloneMe.telescopeMediator) {
            CopyMetaData(cloneMe);
            ObjectType = cloneMe.ObjectType;
            TargetName = cloneMe.TargetName;
        }

        public override object Clone() {
            return new SetPerihelionTrackingRate(this);
        }

        // StringEnumConverter makes the JSON contract self-documenting ("Comet"/"Asteroid")
        // rather than a magic integer -- Newtonsoft serializes enums as plain ints by default,
        // and NINA's own sequence-load path (SequenceJsonConverter) doesn't register a global
        // string-enum converter, so without this attribute a saved/loaded sequence would need
        // to know this enum's exact declaration order to get right.
        [JsonProperty]
        [JsonConverter(typeof(StringEnumConverter))]
        public OrbitalObjectType ObjectType { get; set; } = OrbitalObjectType.Comet;

        /// <summary>
        /// Must match a name in the live MPC comet feed (e.g. "1P/Halley") or in
        /// Perihelion.Astrometry.AsteroidOrbits.BrightAsteroids (e.g. "4 Vesta").
        /// </summary>
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
            var rate = await OrbitalTracking.ComputeOrbitalRateAsync(HttpClient, ObjectType, TargetName, DateTime.UtcNow, token);
            if (rate == null) {
                throw new SequenceEntityFailedException($"Could not find current orbital elements for {ObjectType} '{TargetName}'");
            }

            // Numerically, arcsec/sec == degrees/hour (3600 arcsec/deg ÷ 3600 sec/hour == 1) --
            // see OrbitalRate's doc comment -- so these plug straight into Create() with no
            // conversion. SiderealShiftTrackingRate itself then applies the ASCOM RA/sidereal-
            // rate conversion internally when TelescopeVM hands it to the driver.
            var shiftRate = SiderealShiftTrackingRate.Create(rate.Value.RaArcsecPerSec, rate.Value.DecArcsecPerSec);
            if (!telescopeMediator.SetCustomTrackingRate(shiftRate)) {
                throw new SequenceEntityFailedException($"Setting tracking rate to {shiftRate} failed");
            }
        }

        public bool Validate() {
            var i = new List<string>();
            var info = telescopeMediator.GetInfo();
            if (!info.Connected) {
                i.Add("Telescope not connected");
            } else if (!info.CanSetRightAscensionRate) {
                i.Add($"{info.Name} does not support setting the RA rate");
            } else if (!info.CanSetDeclinationRate) {
                i.Add($"{info.Name} does not support setting the Dec rate");
            } else if (string.IsNullOrWhiteSpace(TargetName)) {
                i.Add("No target name set");
            }

            Issues = i;
            return i.Count == 0;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SetPerihelionTrackingRate)}, ObjectType: {ObjectType}, TargetName: {TargetName}";
        }
    }
}
