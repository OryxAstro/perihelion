using CosineKitty;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using Perihelion.Api;
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
        // One shared HttpClient across the whole plugin (PerihelionHttpClient.cs) -- HttpClient
        // is designed to be reused, not created per request or per class.
        private static readonly HttpClient HttpClient = PerihelionHttpClient.Instance;

        private readonly ITelescopeMediator telescopeMediator;
        private readonly IProfileService profileService;

        [ImportingConstructor]
        public SetPerihelionTrackingRate(ITelescopeMediator telescopeMediator, IProfileService profileService) {
            this.telescopeMediator = telescopeMediator;
            this.profileService = profileService;
        }

        private SetPerihelionTrackingRate(SetPerihelionTrackingRate cloneMe) : this(cloneMe.telescopeMediator, cloneMe.profileService) {
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

        /// <summary>
        /// The exact rate this instance last successfully computed and sent, in Perihelion's own
        /// arcsec/sec units -- exposed so callers (the Quick Track API controller, the auto-reapply
        /// timer) can report the true applied value rather than a separately-recomputed
        /// approximation, which could disagree from a few seconds' worth of real orbital motion or
        /// simply obscure whether the call actually reached the mount.
        /// </summary>
        public OrbitalRate? LastAppliedRate { get; private set; }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // A parked mount silently ignores a custom tracking rate -- SetCustomTrackingRate
            // returns true regardless (TelescopeVM only checks Connected, not AtPark; see
            // CLAUDE.md/session notes), so this is the only place that can catch it. Real NINA
            // sequences always run an explicit UnparkScope item first; Quick Track has no
            // equivalent step of its own, so it needs to do this itself rather than silently
            // reporting success while the mount stays parked.
            if (telescopeMediator.GetInfo().AtPark) {
                if (!await telescopeMediator.UnparkTelescope(progress, token)) {
                    throw new SequenceEntityFailedException("Mount is parked and could not be unparked");
                }
            }

            var rate = await OrbitalTracking.ComputeOrbitalRateAsync(HttpClient, ObjectType, TargetName, DateTime.UtcNow, token, CurrentObserver());
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
            LastAppliedRate = rate.Value;
        }

        private Observer CurrentObserver() {
            var site = profileService.ActiveProfile.AstrometrySettings;
            return new Observer(site.Latitude, site.Longitude, site.Elevation);
        }

        // --- Live coordinate refresh ---
        //
        // Add to Sequence places this item next to a plain CenterAndRotate in a generic
        // DeepSkyObjectContainer, not a dedicated container of its own -- CenterAndRotate slews
        // to whatever Target.InputCoordinates it's handed, and without something keeping that
        // current, it's frozen at whatever position was computed when the sequence was built.
        // For a comet moving on the order of an arcminute or more per hour, a target sitting
        // queued behind other sequence steps for even 30-60 minutes can drift meaningfully
        // before its own turn comes up. Running from AfterParentChanged (fires once this item
        // is actually attached into a sequence tree, i.e. as soon as the sequence is loaded, not
        // just when it starts executing) rather than from Execute keeps it live the whole time
        // the sequence is loaded, matching what a user would actually expect "current position"
        // to mean.
        private const int CoordinateRefreshSeconds = 30;
        private CancellationTokenSource? coordinateUpdateCts;
        private Task? coordinateUpdateTask;

        private async Task CoordinateUpdateLoop(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                await RefreshTargetCoordinates(ct);
                try {
                    await Task.Delay(TimeSpan.FromSeconds(CoordinateRefreshSeconds), ct);
                } catch (OperationCanceledException) {
                    return;
                }
            }
        }

        private async Task RefreshTargetCoordinates(CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(TargetName)) return;
            if (Parent is not IDeepSkyObjectContainer container || container.Target?.InputCoordinates == null) return;

            try {
                var position = await OrbitalTracking.ComputeApparentPositionAsync(HttpClient, ObjectType, TargetName, DateTime.UtcNow, CurrentObserver(), ct);
                if (position is (double raHours, double decDeg)) {
                    container.Target.InputCoordinates.Coordinates = new Coordinates(raHours, decDeg, Epoch.J2000, Coordinates.RAType.Hours);
                }
            } catch (Exception ex) {
                // Logged, not thrown -- this runs unattended on a background loop with nothing
                // waiting on its result. A transient failure (feed briefly unreachable) shouldn't
                // tear down the loop; the next tick tries again on its own.
                Logger.Warning($"Perihelion: could not refresh live coordinates for {TargetName}: {ex.Message}");
            }
        }

        public override void AfterParentChanged() {
            if (Parent != null) {
                if (coordinateUpdateTask == null) {
                    coordinateUpdateCts = new CancellationTokenSource();
                    coordinateUpdateTask = Task.Run(() => CoordinateUpdateLoop(coordinateUpdateCts.Token));
                }
            } else {
                StopCoordinateUpdateLoop();
            }
            base.AfterParentChanged();
        }

        public override void Teardown() {
            StopCoordinateUpdateLoop();
            base.Teardown();
        }

        private void StopCoordinateUpdateLoop() {
            try {
                coordinateUpdateCts?.Cancel();
            } finally {
                coordinateUpdateCts = null;
                coordinateUpdateTask = null;
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
