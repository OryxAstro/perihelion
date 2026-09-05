using CommunityToolkit.Mvvm.Input;
using CosineKitty;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using Perihelion;
using Perihelion.Api;
using Perihelion.Astrometry;
using Perihelion.SequenceItems;
using Perihelion.Sequencing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;

namespace Perihelion.ViewModels {

    /// <summary>
    /// Perihelion's own native dockable panel for real Windows NINA -- the Windows-only
    /// counterpart to the Touch-N-Stars web panel, but running in-process rather than as an HTTP
    /// client of Perihelion's own API server (Quick Track start/stop calls QuickTrackEngine
    /// directly, same call NINA's own advanced sequencer path would end up making). Modeled after
    /// NINA.Joko.Plugin.Orbitals' own dockable "Orbital Elements" panel (same IDockableVM export
    /// shape, same Frame/Set Tracking Rate/Set Guider Shift Rate/Slew and Track/Offset actions --
    /// independently implemented from reading its panel in screenshots and its public interface
    /// usage, not from its source; see CLAUDE.md's IP hygiene section), plus everything the
    /// Touch-N-Stars panel already does that Orbitals' own panel doesn't: live-brightness
    /// browsing, a 10-night path preview, and Quick Track's auto-reapply.
    /// </summary>
    [Export(typeof(NINA.Equipment.Interfaces.ViewModel.IDockableVM))]
    public class PerihelionDockableVM : DockableVM {
        private static readonly HttpClient HttpClient = PerihelionHttpClient.Instance;
        private const int PathDays = 10;
        private const double PathViewWidth = 260;
        private const double PathViewHeight = 140;

        private readonly IProfileService profileService;
        private readonly IGuiderMediator guiderMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IRotatorMediator rotatorMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IImageDataFactory imageDataFactory;
        private readonly INighttimeCalculator nighttimeCalculator;
        private readonly DispatcherTimer statusTimer;
        private CancellationTokenSource? loadCts;

        // ISequencerFactory/ISequenceMediator are NOT imported here directly -- confirmed the
        // hard way that doing so silently breaks this VM's own MEF composition entirely (the
        // whole panel vanishes from the Imaging tab, no error, no log signal). See
        // PerihelionPlugin's own static SequencerFactory/SequenceMediator fields for the full
        // explanation and the working alternative used instead. INighttimeCalculator below is a
        // different case -- confirmed safe because Orbitals' own real, working dockable VM
        // (OrbitalsVM) imports this exact type directly into its own constructor. IRotatorMediator
        // (added 2026-09-05, for the Framing Composer's own rotator-aware framing),
        // IImageDataFactory (added the same day, for the Composer's own real sky-map fetch via
        // SkySurveyFactory), and ICameraMediator/IImagingMediator/IFilterWheelMediator (added the
        // same day, for the Composer's own real "Determine Rotation from Camera" plate-solve --
        // confirmed safely MEF-importable since nitr57/ninaAPI's own AdvancedAPI.cs imports all
        // three directly too) are the same kind of standard, non-special interfaces as
        // ITelescopeMediator/IGuiderMediator above, not the ISequencerFactory/ISequenceMediator
        // special case.
        // IFramingAssistantVM/IApplicationMediator are deliberately NOT imported -- "Frame" opens
        // Perihelion's own popup Framing Composer, not NINA's own Framing Assistant tab. (An
        // earlier same-day attempt to jump to that tab instead, reasoned from reading
        // NINA.Joko.Plugin.Orbitals' own OrbitalsVM.cs, was explicitly rejected -- Perihelion
        // needs its own real popup with a real sky-map/FOV view, not a redirect elsewhere.)

        [ImportingConstructor]
        public PerihelionDockableVM(
            IProfileService profileService,
            IGuiderMediator guiderMediator,
            ITelescopeMediator telescopeMediator,
            IRotatorMediator rotatorMediator,
            ICameraMediator cameraMediator,
            IImagingMediator imagingMediator,
            IFilterWheelMediator filterWheelMediator,
            IImageDataFactory imageDataFactory,
            INighttimeCalculator nighttimeCalculator) : base(profileService) {
            this.profileService = profileService;
            this.guiderMediator = guiderMediator;
            this.telescopeMediator = telescopeMediator;
            this.rotatorMediator = rotatorMediator;
            this.cameraMediator = cameraMediator;
            this.imagingMediator = imagingMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.imageDataFactory = imageDataFactory;
            this.nighttimeCalculator = nighttimeCalculator;

            Title = "Perihelion";
            // MEF composition order between this VM and PerihelionPlugin's own resource-merging
            // constructor isn't guaranteed -- if PerihelionIcon.xaml hasn't been merged into
            // Application.Current.Resources yet when this runs, fall back to whatever
            // DockableVM's own base constructor already set (its default PuzzlePieceSVG) rather
            // than overwrite it with null.
            if (System.Windows.Application.Current?.Resources["PerihelionOrbitSVG"] is GeometryGroup icon) {
                icon.Freeze();
                ImageGeometry = icon;
            }

            ObjectTypes = new[] { OrbitalObjectType.Comet, OrbitalObjectType.Asteroid };
            SelectedObjectType = OrbitalObjectType.Comet;
            BrowseObjects = new ObservableCollection<BrowseObject>();
            browseObjectsView = CollectionViewSource.GetDefaultView(BrowseObjects);
            browseObjectsView.Filter = o => o is BrowseObject b
                && b.ObjectType == SelectedObjectType
                && (string.IsNullOrWhiteSpace(SearchText) || b.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            SearchCommand = new RelayCommand(() => browseObjectsView.Refresh());

            UpdateCometsCommand = new AsyncRelayCommand(UpdateCometsAction);
            UpdateCobsCommand = new AsyncRelayCommand(UpdateCobsAction);
            RefreshLastUpdatedText();

            PathPoints = new PointCollection();
            AutoReapplyMinutes = 15;
            StatusText = "Loading live comet and asteroid data...";

            // "(Don't switch)" first, then every filter actually configured on this profile --
            // matches SwitchFilter's own convention (an empty/null ComboBoxText means leave the
            // wheel alone, not a real filter position).
            AvailableFilterNames = new[] { NoFilterChangeOption }
                .Concat(profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Select(f => f.Name))
                .ToArray();
            SelectedFilterName = NoFilterChangeOption;
            ExposureSeconds = 60;
            FrameCount = 10;
            AutofocusMinutes = 60;

            RefreshBrowseListCommand = new AsyncRelayCommand(RefreshBrowseListAction);
            LoadCommand = new AsyncRelayCommand(LoadAction, () => SelectedBrowseObject != null && !IsBusy);
            LoadCommand.RegisterPropertyChangeNotification(this, nameof(SelectedBrowseObject));
            LoadCommand.RegisterPropertyChangeNotification(this, nameof(IsBusy));

            FrameCommand = new RelayCommand(FrameAction, () => Loaded != null);
            FrameCommand.RegisterPropertyChangeNotification(this, nameof(Loaded));

            SlewAndTrackCommand = new AsyncRelayCommand(SlewAndTrackAction, () => Loaded != null && telescopeMediator.GetInfo().Connected);
            SlewAndTrackCommand.RegisterPropertyChangeNotification(this, nameof(Loaded));
            SlewAndTrackCommand.RegisterPropertyChangeNotification(telescopeMediator.GetInfo(), nameof(TelescopeInfo.Connected));

            SetTrackingRateCommand = new AsyncRelayCommand(SetTrackingRateAction, CanSetTrackingRate);
            SetTrackingRateCommand.RegisterPropertyChangeNotification(this, nameof(Loaded));
            SetTrackingRateCommand.RegisterPropertyChangeNotification(telescopeMediator.GetInfo(), nameof(TelescopeInfo.Connected), nameof(TelescopeInfo.CanSetRightAscensionRate), nameof(TelescopeInfo.CanSetDeclinationRate));

            SetGuiderShiftRateCommand = new AsyncRelayCommand(SetGuiderShiftRateAction, CanSetGuiderShiftRate);
            SetGuiderShiftRateCommand.RegisterPropertyChangeNotification(this, nameof(Loaded));
            SetGuiderShiftRateCommand.RegisterPropertyChangeNotification(guiderMediator.GetInfo(), nameof(GuiderInfo.Connected), nameof(GuiderInfo.CanSetShiftRate));

            ResetOffsetCommand = new RelayCommand(() => { OffsetRaArcsec = 0; OffsetDecArcsec = 0; }, () => OffsetRaArcsec != 0 || OffsetDecArcsec != 0);

            SetOffsetFromMountCommand = new RelayCommand(SetOffsetFromMountAction, () => Loaded != null && telescopeMediator.GetInfo().Connected);
            SetOffsetFromMountCommand.RegisterPropertyChangeNotification(this, nameof(Loaded));
            SetOffsetFromMountCommand.RegisterPropertyChangeNotification(telescopeMediator.GetInfo(), nameof(TelescopeInfo.Connected));

            AddToSequenceCommand = new RelayCommand(AddToSequenceAction, () => Loaded != null);
            AddToSequenceCommand.RegisterPropertyChangeNotification(this, nameof(Loaded));

            StartQuickTrackCommand = new AsyncRelayCommand(StartQuickTrackAction, () => Loaded != null && !QuickTrackActive);
            StartQuickTrackCommand.RegisterPropertyChangeNotification(this, nameof(Loaded), nameof(QuickTrackActive));

            StopQuickTrackCommand = new AsyncRelayCommand(StopQuickTrackAction, () => QuickTrackActive);
            StopQuickTrackCommand.RegisterPropertyChangeNotification(this, nameof(QuickTrackActive));

            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            statusTimer.Tick += (_, _) => RefreshQuickTrackStatus();
            statusTimer.Start();
            RefreshQuickTrackStatus();

            // Auto-populate on open instead of waiting for an explicit Refresh click -- real user
            // feedback (2026-09-05): "why do I have to click refresh for the asteroids/comets to
            // appear, just populate the list if the data is already cached." Fire-and-forget is
            // safe here: RefreshBrowseListAction handles its own IsBusy/StatusText/error reporting.
            _ = RefreshBrowseListAction();
        }

        private bool CanSetTrackingRate() {
            var info = telescopeMediator.GetInfo();
            return Loaded != null && info.Connected && info.CanSetRightAscensionRate && info.CanSetDeclinationRate;
        }

        private bool CanSetGuiderShiftRate() {
            var info = guiderMediator.GetInfo();
            return Loaded != null && info.Connected && info.CanSetShiftRate;
        }

        // --- Browse ---

        public IReadOnlyList<OrbitalObjectType> ObjectTypes { get; }

        private OrbitalObjectType selectedObjectType;
        public OrbitalObjectType SelectedObjectType {
            get => selectedObjectType;
            // BrowseObjects itself now holds BOTH object types at once (see RefreshBrowseListAction) --
            // switching this combo box just re-filters the existing, already-fetched list instantly
            // instead of requiring a fresh fetch. browseObjectsView is null the one time this setter
            // runs during the constructor's own initial assignment, before the view exists yet.
            set { selectedObjectType = value; RaisePropertyChanged(); browseObjectsView?.Refresh(); }
        }

        public ObservableCollection<BrowseObject> BrowseObjects { get; }

        // CollectionViewSource.GetDefaultView returns the exact same ICollectionView WPF already
        // uses internally for the ListView's own ItemsSource="{Binding BrowseObjects}" binding --
        // setting Filter here affects what that ListView shows with no XAML change needed at all.
        private readonly ICollectionView browseObjectsView;

        private string searchText = string.Empty;
        public string SearchText {
            get => searchText;
            set { searchText = value; RaisePropertyChanged(); browseObjectsView.Refresh(); }
        }

        /// <summary>Number of rows the Browse list is actually showing right now (current object
        /// type + search text applied) -- used for status messages instead of BrowseObjects.Count,
        /// which now holds both object types combined.</summary>
        private int VisibleBrowseObjectCount => browseObjectsView.Cast<object>().Count();

        public RelayCommand SearchCommand { get; }

        // --- Update Databases ---
        //
        // Real user concern: does this cache actually persist across NINA restarts/reboots, or
        // could it silently clear out? Verified empirically, not assumed: both
        // %LocalAppData%\NINA\PerihelionData\comet-elements-cache.json and
        // \comet-activity-cache.json (COBS) already exist on the real deployed machine from
        // earlier in this same session, surviving every restart and redeploy since -- this is
        // the same stable, persistent NINA.Core.Utility.CoreUtil.APPLICATIONTEMPPATH root that
        // hosts the Plugins folder itself (confirmed from NINA.Plugin/Constants.cs using the same
        // constant for its own UserExtensionsFolder), not a volatile OS temp directory despite
        // the confusing name. CometOrbits' own comet-elements cache has a 6-hour TTL and is used
        // automatically by Load/Refresh; these two buttons are the explicit "do it now, bypass
        // the TTL" actions, matching Orbitals' own per-object-type Update button and this
        // project's existing PINS-side /objects/refresh-cobs route (mirrored exactly, not
        // reinvented) for the same real reason -- a full COBS sweep across every comet takes
        // several seconds to tens of seconds, so it stays a deliberate, explicit action rather
        // than something that runs silently on every Load.
        private string cometsLastUpdatedText = "Never";
        public string CometsLastUpdatedText => cometsLastUpdatedText;
        private string cobsLastUpdatedText = "Never";
        public string CobsLastUpdatedText => cobsLastUpdatedText;

        /// <summary>"Comets (4108)" -- same idea as Orbitals' own per-category count label.
        /// CometOrbits.CachedCount is a cheap synchronous read of whatever's already in memory/on
        /// disk, not a live fetch.</summary>
        public string CometsCountText => $"Comets ({CometOrbits.CachedCount})";

        public AsyncRelayCommand UpdateCometsCommand { get; }
        public AsyncRelayCommand UpdateCobsCommand { get; }

        /// <summary>Status for the Update Sources actions specifically (Update Comets/Update
        /// COBS) -- kept separate from StatusText, which is Browse/Load-only, per real user
        /// feedback (2026-09-05): "Updating comet elements from MPC..." was showing next to the
        /// Browse combo box, where it looked like a Browse-list status rather than what it
        /// actually was. Displayed under the Update Sources block in the view instead.</summary>
        private string updateSourcesStatusText = string.Empty;
        public string UpdateSourcesStatusText {
            get => updateSourcesStatusText;
            set { updateSourcesStatusText = value; RaisePropertyChanged(); }
        }

        private void RefreshLastUpdatedText() {
            cometsLastUpdatedText = CometOrbits.LastSyncedUtc is DateTime c ? c.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Never";
            cobsLastUpdatedText = CometActivity.LastFullRefreshUtc is DateTime o ? o.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Never";
            RaisePropertyChanged(nameof(CometsLastUpdatedText));
            RaisePropertyChanged(nameof(CobsLastUpdatedText));
            RaisePropertyChanged(nameof(CometsCountText));
        }

        private async Task UpdateCometsAction() {
            IsBusy = true;
            UpdateSourcesStatusText = "Updating comet elements from MPC...";
            try {
                var ok = await CometOrbits.SyncNowAsync(HttpClient, CancellationToken.None);
                UpdateSourcesStatusText = ok ? "Comet elements updated." : "Comet elements update failed -- see log.";
            } catch (Exception ex) {
                UpdateSourcesStatusText = $"Comet elements update failed: {ex.Message}";
                Notification.ShowError($"Perihelion: comet elements update failed: {ex.Message}");
            } finally {
                RefreshLastUpdatedText();
                IsBusy = false;
            }
        }

        private async Task UpdateCobsAction() {
            IsBusy = true;
            UpdateSourcesStatusText = "Refreshing COBS observed magnitudes (can take a while)...";
            try {
                var objects = await OrbitalTracking.ListBrowseObjectsAsync(HttpClient, DateTime.UtcNow, CancellationToken.None, includeCobs: true, forceRefreshCobs: true);
                BrowseObjects.Clear();
                foreach (var o in objects) {
                    BrowseObjects.Add(o);
                }
                UpdateSourcesStatusText = $"COBS refreshed -- {VisibleBrowseObjectCount} {SelectedObjectType.ToString().ToLowerInvariant()}(s) loaded.";
            } catch (Exception ex) {
                UpdateSourcesStatusText = $"COBS refresh failed: {ex.Message}";
                Notification.ShowError($"Perihelion: COBS refresh failed: {ex.Message}");
            } finally {
                RefreshLastUpdatedText();
                IsBusy = false;
            }
        }

        private BrowseObject? selectedBrowseObject;
        public BrowseObject? SelectedBrowseObject {
            get => selectedBrowseObject;
            set { selectedBrowseObject = value; RaisePropertyChanged(); }
        }

        private bool isBusy;
        public bool IsBusy {
            get => isBusy;
            set { isBusy = value; RaisePropertyChanged(); }
        }

        private string statusText = string.Empty;
        public string StatusText {
            get => statusText;
            set { statusText = value; RaisePropertyChanged(); }
        }

        public AsyncRelayCommand RefreshBrowseListCommand { get; }

        // BrowseObjects holds BOTH object types at once now (see SelectedObjectType's own doc
        // comment) -- fetched without COBS (includeCobs defaults false) so this stays fast even on
        // a cold elements cache; EnrichObservedMagnitudesAsync fills in the Observed Mag column
        // afterward, in the background, without blocking this from returning.
        private async Task RefreshBrowseListAction() {
            IsBusy = true;
            StatusText = "Fetching live comet and asteroid data...";
            try {
                var objects = await OrbitalTracking.ListBrowseObjectsAsync(HttpClient, DateTime.UtcNow, CancellationToken.None);
                BrowseObjects.Clear();
                foreach (var o in objects) {
                    BrowseObjects.Add(o);
                }
                StatusText = $"{VisibleBrowseObjectCount} {SelectedObjectType.ToString().ToLowerInvariant()}(s) loaded, brightest first.";
            } catch (Exception ex) {
                StatusText = $"Failed to fetch: {ex.Message}";
                Notification.ShowError($"Perihelion: failed to fetch browse list: {ex.Message}");
            } finally {
                IsBusy = false;
            }
            _ = EnrichObservedMagnitudesAsync();
        }

        /// <summary>Fills in each comet's ObservedMagnitude/ObservedAverageMagnitude in the
        /// background after the fast, COBS-less list above has already rendered -- same two-phase
        /// pattern as the Touch-N-Stars web panel's own fetchBrowseObjects.js (comment there has
        /// the full "why": even a warm COBS cache measurably slowed down the initial render on
        /// real hardware). Reuses ListBrowseObjectsAsync's own parallel COBS-fetch logic rather
        /// than re-implementing it, matched back onto the already-rendered BrowseObjects by Name.
        /// BrowseObject doesn't implement INotifyPropertyChanged, so a plain field mutation won't
        /// update the ListView on its own -- browseObjectsView.Refresh() forces WPF to regenerate
        /// every row's containers, which re-reads current property values same as a fresh bind.</summary>
        private async Task EnrichObservedMagnitudesAsync() {
            try {
                var enriched = await OrbitalTracking.ListBrowseObjectsAsync(HttpClient, DateTime.UtcNow, CancellationToken.None, includeCobs: true, forceRefreshCobs: false);
                var byName = enriched.Where(o => o.ObjectType == OrbitalObjectType.Comet).ToDictionary(o => o.Name);
                foreach (var comet in BrowseObjects.Where(o => o.ObjectType == OrbitalObjectType.Comet)) {
                    if (byName.TryGetValue(comet.Name, out var match)) {
                        comet.ObservedMagnitude = match.ObservedMagnitude;
                        comet.ObservedAverageMagnitude = match.ObservedAverageMagnitude;
                    }
                }
                browseObjectsView.Refresh();
            } catch {
                // Best-effort background enrichment -- the list above already rendered
                // successfully without this; a COBS hiccup shouldn't surface as a user-facing error.
            }
        }

        // --- Load: current position/rate/elements/path for the selected object ---

        /// <summary>The object Load actually populated the rest of this VM's data for -- distinct
        /// from SelectedBrowseObject so the UI can tell when a newly-selected object hasn't been
        /// loaded yet (every action below gates on this, not on the list selection).</summary>
        private BrowseObject? loaded;
        public BrowseObject? Loaded {
            get => loaded;
            private set { loaded = value; RaisePropertyChanged(); }
        }

        private double raHours, decDeg;
        public string PositionText => Loaded == null ? "--" : $"RA {AstroUtil.HoursToHMS(raHours)}  Dec {AstroUtil.DegreesToDMS(decDeg)}";

        private double? raRateArcsecPerSec, decRateArcsecPerSec;
        public string RateText => raRateArcsecPerSec is double ra && decRateArcsecPerSec is double dec
            ? $"RA {ra:F4} arcsec/sec   Dec {dec:F4} arcsec/sec"
            : "--";

        private double? maxExposureSeconds;
        public string MaxExposureText => maxExposureSeconds is double s ? $"{s:F1} sec" : "--";

        private double? magnitudeNow;
        public string MagnitudeText => magnitudeNow is double m ? m.ToString("F2") : "--";

        // COBS-observed brightness -- comet-only, null for an asteroid or a comet COBS has no
        // reports for. Shown alongside the predicted Magnitude above, not instead of it: the
        // predicted (H/G model) value can be badly wrong during a real outburst, and that's only
        // useful to notice when the real observed value sits right next to it.
        private double? observedMagnitude, observedAverageMagnitude;
        public string ObservedMagnitudeText => observedMagnitude is double m
            ? observedAverageMagnitude is double avg ? $"{m:F2} (5-obs avg {avg:F2})" : m.ToString("F2")
            : "n/a";

        // Elements card -- matches NINA.Joko.Plugin.Orbitals' own layout (Epoch AND Periapsis
        // shown side by side, not one-or-the-other): a comet's own perihelion passage time T is
        // a real, separate quantity from "Epoch" (the reference date its Mean Anomaly at Epoch
        // is computed for -- today's date at 00:00 UTC, same convention confirmed against a real
        // Orbitals screenshot for the same comet on the same day), not a substitute for it.
        private double? eccentricity, inclinationDeg, argPeriDeg, nodeDeg, perihelionDistanceAu, semiMajorAxisAu;
        private double? meanAnomalyAtEpochDeg, meanAnomalyNowDeg, eccentricAnomalyNowDeg, trueAnomalyNowDeg, distanceNowAu;
        private DateTime? epochUtc, periapsisUtc;
        private string sourceText = "--";

        public string EccentricityText => eccentricity is double e ? e.ToString("F4") : "--";
        public string InclinationText => inclinationDeg is double i ? $"{i:F4}°" : "--";
        public string ArgPeriText => argPeriDeg is double a ? $"{a:F4}°" : "--";
        public string NodeText => nodeDeg is double n ? $"{n:F4}°" : "--";
        public string PerihelionDistanceText => perihelionDistanceAu is double q ? $"{q:F4} au" : "--";
        public string SemiMajorAxisText => semiMajorAxisAu is double a ? $"{a:F4} au" : "n/a (non-elliptical)";
        // Wrapped to (-180, 180], matching Orbitals' own sign convention -- real user feedback
        // comparing the two side by side for the same comet found the raw [0, 360) values (this
        // panel's own original convention) confusingly "dramatically different" at a glance
        // (e.g. 356.25° here vs. -3.75° there) when they were actually the same angle.
        public string MeanAnomalyAtEpochText => meanAnomalyAtEpochDeg is double m ? $"{WrapSigned(m):F4}°" : "n/a";
        public string MeanAnomalyNowText => meanAnomalyNowDeg is double m ? $"{WrapSigned(m):F4}°" : "n/a";
        public string EccentricAnomalyNowText => eccentricAnomalyNowDeg is double e ? $"{WrapSigned(e):F4}°" : "n/a";
        public string TrueAnomalyNowText => trueAnomalyNowDeg is double t ? $"{WrapSigned(t):F4}°" : "n/a";
        public string DistanceNowText => distanceNowAu is double d ? $"{d:F4} au" : "--";
        public string EpochText => epochUtc is DateTime d ? d.ToString("yyyy-MM-dd HH:mm") : "--";
        public string EpochJulianText => epochUtc is DateTime d ? OrbitalMechanics.JulianDate(new AstroTime(d)).ToString("F4") : "--";
        public string PeriapsisText => periapsisUtc is DateTime d ? d.ToString("yyyy-MM-dd HH:mm:ss") : "n/a";
        public string PeriapsisJulianText => periapsisUtc is DateTime d ? OrbitalMechanics.JulianDate(new AstroTime(d)).ToString("F4") : "n/a";
        public string SourceText => sourceText;

        // Wraps to (-180, 180] -- the "+540" shifts any double-precision value (whatever sign or
        // magnitude the underlying %360 in AsteroidOrbits/CometOrbits' own ComputeAnomalies left
        // it in) into a single positive range before the final %360 and re-centering.
        private static double WrapSigned(double degrees) => ((degrees % 360) + 540) % 360 - 180;

        // Arcsec remains the internal storage (what LoadedCoordinatesWithOffset actually adds),
        // but real user feedback: don't display it as a raw arcsec number -- show it the same
        // way NINA.Joko.Plugin.Orbitals displays its own RAOffset/DecOffset, HH:MM:SS for RA and
        // DMS for Dec (both AstroUtil.HoursToHMS/DegreesToDMS correctly handle negative offsets
        // with a leading "-", confirmed from NINA's own source, unlike a plain position which
        // never goes negative). Read-only display, not an editable sexagesimal text box -- Set
        // Offset (capture from the mount) and Clear Offset are the only ways to change this,
        // matching Orbitals' own apparent interaction model (no manual offset typing there
        // either), and avoids needing a bespoke bidirectional HH:MM:SS/DMS parser.
        private double offsetRaArcsec, offsetDecArcsec;
        public double OffsetRaArcsec {
            get => offsetRaArcsec;
            set { offsetRaArcsec = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(OffsetRaText)); ResetOffsetCommand.NotifyCanExecuteChanged(); }
        }
        public double OffsetDecArcsec {
            get => offsetDecArcsec;
            set { offsetDecArcsec = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(OffsetDecText)); ResetOffsetCommand.NotifyCanExecuteChanged(); }
        }
        public string OffsetRaText => AstroUtil.HoursToHMS(offsetRaArcsec / 3600.0 / 15.0);
        public string OffsetDecText => AstroUtil.DegreesToDMS(offsetDecArcsec / 3600.0);

        /// <summary>Set only by FrameAction, from whatever the Framing Composer actually
        /// achieved -- null means plain Center (no rotator involved), matching how
        /// PerihelionSequenceBuilder.BuildTargetContainer's own rotationAngle parameter already
        /// works. Read by AddToSequenceAction; Quick Track doesn't use this at all (it has no
        /// centering/framing step of its own, see QuickTrack's own architecture notes).</summary>
        private double? capturedRotationAngle;
        public double? CapturedRotationAngle {
            get => capturedRotationAngle;
            private set { capturedRotationAngle = value; RaisePropertyChanged(); }
        }

        /// <summary>One marker per night on the path, positioned in the same coordinate space
        /// as PathPoints (Left/Top already centered, not top-left-anchored). IsTonight flags day
        /// 0 (the same "now" instant Load's own position/rate came from); IsEnd flags the last
        /// of the 10 nights -- each renders distinctly so the chart reads start-to-end at a
        /// glance, not just as an undifferentiated string of dots. Tooltip carries this point's
        /// own date/RA/Dec so hovering (WPF's native equivalent of "click for info" for a custom
        /// vector chart like this one) shows something real, not just an anonymous dot.</summary>
        public sealed class PathMarker {
            public required double Left { get; init; }
            public required double Top { get; init; }
            public required double Size { get; init; }
            public required bool IsTonight { get; init; }
            public required bool IsEnd { get; init; }
            public required string Tooltip { get; init; }
        }

        public PointCollection PathPoints { get; private set; }
        public ObservableCollection<PathMarker> PathMarkers { get; } = new();
        public double PathViewBoxWidth => PathViewWidth;
        public double PathViewBoxHeight => PathViewHeight;
        // Named by screen position (Left/Right), not chronology (Start/End) -- a comet's path is
        // an RA/Dec trajectory, not a strict left-to-right timeline, so night 0 does not always
        // plot to the left of night 9 (e.g. a comet whose RA decreases night over night runs the
        // other way). A real user-reported bug: the previous Start=Left/End=Right assumption put
        // the wrong date under each dot whenever a comet's own path happened to run right-to-left.
        // Touch-N-Stars' own web chart (OrbitalPathChart.vue) already gets this right by anchoring
        // each label to that point's own actual x-coordinate; this mirrors the same fix.
        private string pathLeftLabel = string.Empty, pathRightLabel = string.Empty;
        public string PathLeftLabel => pathLeftLabel;
        public string PathRightLabel => pathRightLabel;

        // --- Tonight's altitude ---
        //
        // Uses NINA's own real AltitudeChart control (NINA.WPF.Base.View.AltitudeChart) rather
        // than a hand-rolled chart -- real user feedback on the hand-rolled version: it looked
        // inferior to what Orbitals (and every other NINA panel) already gets from this control
        // for free (proper twilight shading, a real "Now" line, transit annotation, moon
        // position). The control binds its own DataContext (a real NINA.Astrometry.DeepSkyObject
        // -- SkyObjectBase.Altitudes/Horizon/MaxAltitude compute themselves once
        // SetDateAndPosition is called, no manual sampling needed) plus a NighttimeData for the
        // twilight/moon background, exactly the same pattern NINA's own FramingAssistantView.xaml
        // and SkyAtlasView.xaml use. NighttimeCalculator's ReferenceDate (not DateTime.Now
        // directly) is what SkyAtlasVM.cs itself passes to SetDateAndPosition, so the target
        // curve and the twilight background always agree on the same night window.
        private NighttimeData? nighttimeData;
        public NighttimeData? NighttimeData {
            get => nighttimeData;
            private set { nighttimeData = value; RaisePropertyChanged(); }
        }

        private NINA.Astrometry.DeepSkyObject? loadedDso;
        public NINA.Astrometry.DeepSkyObject? LoadedDso {
            get => loadedDso;
            private set { loadedDso = value; RaisePropertyChanged(); }
        }

        private void UpdateAltitudeChart(string name, Coordinates coordinates) {
            NighttimeData = nighttimeCalculator.Calculate();
            var site = profileService.ActiveProfile.AstrometrySettings;
            var dso = new NINA.Astrometry.DeepSkyObject(name, coordinates, profileService.ActiveProfile.AstrometrySettings.Horizon);
            dso.SetDateAndPosition(NighttimeData.ReferenceDate, site.Latitude, site.Longitude);
            LoadedDso = dso;
        }

        // --- Add to Sequence ---

        private const string NoFilterChangeOption = "(Don't switch)";
        public IReadOnlyList<string> AvailableFilterNames { get; }

        private string selectedFilterName = NoFilterChangeOption;
        public string SelectedFilterName {
            get => selectedFilterName;
            set { selectedFilterName = value; RaisePropertyChanged(); }
        }

        private double exposureSeconds;
        public double ExposureSeconds {
            get => exposureSeconds;
            set { exposureSeconds = value; RaisePropertyChanged(); }
        }

        private int frameCount;
        public int FrameCount {
            get => frameCount;
            set { frameCount = value; RaisePropertyChanged(); }
        }

        private bool meridianFlip;
        public bool MeridianFlip {
            get => meridianFlip;
            set { meridianFlip = value; RaisePropertyChanged(); }
        }

        private bool autofocusEnabled;
        public bool AutofocusEnabled {
            get => autofocusEnabled;
            set { autofocusEnabled = value; RaisePropertyChanged(); }
        }

        private double autofocusMinutes;
        public double AutofocusMinutes {
            get => autofocusMinutes;
            set { autofocusMinutes = value; RaisePropertyChanged(); }
        }

        public RelayCommand AddToSequenceCommand { get; }

        // Wired up 2026-09-05 -- see PerihelionPlugin's own updated comment on SequenceMediator
        // and PerihelionSequenceBuilder.ResolveFactory's doc comment for the full story: this VM
        // still never imports ISequencerFactory/ISequenceMediator directly (that's still not
        // safe -- unchanged from before), it reads PerihelionPlugin's own static
        // SequenceMediator field instead (populated from a constructor import that turns out
        // to work fine on ITS OWN, now that ISequencerFactory isn't also being imported
        // alongside it) and reflects the real ISequencerFactory out of it the same way
        // nitr57/ninaAPI's own Sequence.cs route does.
        private void AddToSequenceAction() {
            if (Loaded == null) return;

            var sequenceMediator = PerihelionPlugin.SequenceMediator;
            if (sequenceMediator == null) {
                Notification.ShowError("Perihelion: the sequencer isn't available yet -- try again once NINA has fully started.");
                return;
            }
            if (!sequenceMediator.Initialized) {
                Notification.ShowError("Perihelion: the Advanced Sequencer hasn't finished starting up yet.");
                return;
            }
            var factory = PerihelionSequenceBuilder.ResolveFactory(sequenceMediator);
            if (factory == null) {
                Notification.ShowError("Perihelion: could not reach the sequencer's item factory -- see log.");
                Logger.Error("Perihelion: PerihelionSequenceBuilder.ResolveFactory returned null even though Initialized was true");
                return;
            }

            try {
                var filter = SelectedFilterName == NoFilterChangeOption
                    ? null
                    : profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(f => f.Name == SelectedFilterName);

                var container = PerihelionSequenceBuilder.BuildTargetContainer(
                    factory,
                    Loaded.ObjectType,
                    Loaded.Name,
                    new Coordinates(raHours, decDeg, Epoch.J2000, Coordinates.RAType.Hours),
                    LoadedCoordinatesWithOffset(),
                    Guiding,
                    CapturedRotationAngle,
                    AutofocusEnabled ? AutofocusMinutes : (double?)null,
                    new PerihelionSequenceBuilder.ExposureSettings(filter, ExposureSeconds, FrameCount));

                sequenceMediator.AddAdvancedTarget(container);

                // Global Triggers, not this target's own local ones -- see
                // EnsureGlobalMeridianFlipTrigger's own doc comment for why (real user feedback,
                // 2026-09-05). Best-effort: if the root can't be reached for some reason, the
                // target itself was still added successfully above, so this only logs rather
                // than rolling back or erroring the whole action.
                if (MeridianFlip) {
                    var root = PerihelionSequenceBuilder.ResolveSequenceRoot(sequenceMediator);
                    if (root != null) {
                        PerihelionSequenceBuilder.EnsureGlobalMeridianFlipTrigger(factory, root);
                    } else {
                        Logger.Warning("Perihelion: added target to sequence, but could not reach the sequence root to add a global Meridian Flip trigger");
                    }
                }

                StatusText = $"Added {Loaded.Name} to the Advanced Sequencer.";
            } catch (Exception ex) {
                Notification.ShowError($"Perihelion: failed to add to sequence: {ex.Message}");
                Logger.Error("Perihelion: AddToSequenceAction failed", ex);
            }
        }

        public AsyncRelayCommand LoadCommand { get; }
        public RelayCommand FrameCommand { get; }
        public AsyncRelayCommand SlewAndTrackCommand { get; }
        public AsyncRelayCommand SetTrackingRateCommand { get; }
        public AsyncRelayCommand SetGuiderShiftRateCommand { get; }
        public RelayCommand ResetOffsetCommand { get; }
        public RelayCommand SetOffsetFromMountCommand { get; }

        /// <summary>Same semantic as NINA.Joko.Plugin.Orbitals' own SetOffset (confirmed from
        /// its real source): captures wherever the mount is ACTUALLY pointed right now --
        /// telescopeMediator.GetCurrentPosition(), not anything computed -- as a persistent
        /// offset relative to the target's own true position. The real workflow this enables:
        /// manually slew/plate-solve/center wherever you actually want to frame (a comet's tail,
        /// not its nucleus), then capture that exact positioning here instead of typing arcsec
        /// numbers by hand. raHours/decDeg are already the object's true (offset-free) computed
        /// position from the last Load, same fields LoadedCoordinatesWithOffset itself reads.</summary>
        private void SetOffsetFromMountAction() {
            if (Loaded == null) return;
            var current = telescopeMediator.GetCurrentPosition();
            OffsetRaArcsec = Math.Round((current.RA - raHours) * 15 * 3600, 1);
            OffsetDecArcsec = Math.Round((current.Dec - decDeg) * 3600, 1);
        }

        private void RaiseLoadedDataChanged() {
            foreach (var name in new[] {
                nameof(PositionText), nameof(RateText), nameof(MaxExposureText), nameof(MagnitudeText), nameof(ObservedMagnitudeText),
                nameof(EccentricityText), nameof(InclinationText), nameof(ArgPeriText), nameof(NodeText),
                nameof(PerihelionDistanceText), nameof(SemiMajorAxisText), nameof(MeanAnomalyAtEpochText),
                nameof(MeanAnomalyNowText), nameof(EccentricAnomalyNowText), nameof(TrueAnomalyNowText),
                nameof(DistanceNowText), nameof(EpochText), nameof(EpochJulianText), nameof(PeriapsisText), nameof(PeriapsisJulianText), nameof(SourceText),
                nameof(PathPoints), nameof(PathMarkers), nameof(PathLeftLabel), nameof(PathRightLabel),
            }) {
                RaisePropertyChanged(name);
            }
        }

        // JD 2451545.0 == 2000-01-01 12:00 UTC -- same convention OrbitalMechanics.JulianDate
        // uses in the other direction; kept as a small local helper here rather than added to
        // that already physics-audited file, since this is purely a display concern.
        private static DateTime JulianDateToUtc(double jd) => new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(jd - 2451545.0);

        private Observer CurrentObserver() {
            var site = profileService.ActiveProfile.AstrometrySettings;
            return new Observer(site.Latitude, site.Longitude, site.Elevation);
        }

        private async Task LoadAction() {
            var target = SelectedBrowseObject;
            if (target == null) return;

            loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            loadCts = cts;
            var ct = cts.Token;

            IsBusy = true;
            StatusText = $"Loading {target.Name}...";
            try {
                var now = DateTime.UtcNow;
                var t = new AstroTime(now);
                var observer = CurrentObserver();

                var position = await OrbitalTracking.ComputeApparentPositionAsync(HttpClient, target.ObjectType, target.Name, now, observer, ct);
                var rate = await OrbitalTracking.ComputeOrbitalRateAsync(HttpClient, target.ObjectType, target.Name, now, ct, observer);
                var path = await OrbitalTracking.ComputeOrbitalPathAsync(HttpClient, target.ObjectType, target.Name, now, PathDays, ct);
                if (ct.IsCancellationRequested) return;

                if (position is (double raH, double decD)) {
                    raHours = raH;
                    decDeg = decD;
                }
                if (rate is OrbitalRate r) {
                    raRateArcsecPerSec = r.RaArcsecPerSec;
                    decRateArcsecPerSec = r.DecArcsecPerSec;
                    var pixelScale = AstroUtil.ArcsecPerPixel(profileService.ActiveProfile.CameraSettings.PixelSize, profileService.ActiveProfile.TelescopeSettings.FocalLength);
                    var totalRate = Math.Sqrt(r.RaArcsecPerSec * r.RaArcsecPerSec + r.DecArcsecPerSec * r.DecArcsecPerSec);
                    maxExposureSeconds = totalRate > 0 ? pixelScale / totalRate : (double?)null;
                } else {
                    raRateArcsecPerSec = decRateArcsecPerSec = maxExposureSeconds = null;
                }

                if (target.ObjectType == OrbitalObjectType.Comet) {
                    var comet = await CometOrbits.FindByNameAsync(HttpClient, target.Name, ct);
                    if (comet != null) {
                        eccentricity = comet.Eccentricity;
                        inclinationDeg = comet.InclinationDeg;
                        argPeriDeg = comet.ArgPeriDeg;
                        nodeDeg = comet.NodeDeg;
                        perihelionDistanceAu = comet.Q;
                        semiMajorAxisAu = comet.Eccentricity < 1 ? comet.Q / (1 - comet.Eccentricity) : (double?)null;
                        // A comet has no stored epoch the way an asteroid does -- MPC's own comet
                        // elements are parameterized by perihelion passage time T instead. "Epoch"
                        // here is today's date at 00:00 UTC, the same reference-date convention
                        // confirmed against a real Orbitals screenshot for this same comet on the
                        // same day (its own displayed Epoch matched exactly), used purely so Mean
                        // Anomaly at Epoch has a concrete instant to be computed for.
                        var cometEpoch = now.Date;
                        epochUtc = cometEpoch;
                        periapsisUtc = comet.PerihelionDate;
                        sourceText = "MPC";
                        var epochAnomalies = CometOrbits.ComputeAnomalies(comet, cometEpoch);
                        meanAnomalyAtEpochDeg = epochAnomalies?.MeanAnomalyDeg;
                        var cometAnomalies = CometOrbits.ComputeAnomalies(comet, now);
                        meanAnomalyNowDeg = cometAnomalies?.MeanAnomalyDeg;
                        eccentricAnomalyNowDeg = cometAnomalies?.EccentricAnomalyDeg;
                        trueAnomalyNowDeg = cometAnomalies?.TrueAnomalyDeg;
                        magnitudeNow = CometOrbits.PredictedMagnitude(comet, now, t);
                        var helio = CometOrbits.HeliocentricEcliptic(comet, now);
                        distanceNowAu = Math.Sqrt(helio.X * helio.X + helio.Y * helio.Y + helio.Z * helio.Z);

                        // COBS (real observed brightness) -- fetched only for the loaded object,
                        // not the whole browse list, unlike the Touch-N-Stars panel's own
                        // includeCobs path: that one needs a non-blocking per-row background
                        // sweep specifically because it covers the WHOLE list; loading a single
                        // object doesn't have that problem, so a direct await here is simplest
                        // and correct. Real value: predicted (H/G model) magnitude can be badly
                        // wrong during an outburst -- 10P/Tempel and 220P/McNaught are verified
                        // real cases several magnitudes off.
                        var activity = await CometActivity.FetchAsync(HttpClient, target.Name, ct);
                        observedMagnitude = activity?.MostRecent.Magnitude;
                        observedAverageMagnitude = activity?.RecentAverageMagnitude;
                    }
                } else {
                    observedMagnitude = observedAverageMagnitude = null; // COBS is comet-only
                    var asteroid = AsteroidOrbits.FindByName(target.Name);
                    if (asteroid != null) {
                        eccentricity = asteroid.Eccentricity;
                        inclinationDeg = asteroid.InclinationDeg;
                        argPeriDeg = asteroid.ArgPeriDeg;
                        nodeDeg = asteroid.NodeDeg;
                        semiMajorAxisAu = asteroid.A;
                        perihelionDistanceAu = asteroid.A * (1 - asteroid.Eccentricity);
                        meanAnomalyAtEpochDeg = asteroid.MeanAnomalyDeg;
                        epochUtc = JulianDateToUtc(asteroid.EpochJd);
                        periapsisUtc = null; // not natively available from these elements
                        sourceText = "Curated list (JPL)";
                        var anomalies = AsteroidOrbits.ComputeAnomalies(asteroid, t);
                        meanAnomalyNowDeg = anomalies.MeanAnomalyDeg;
                        eccentricAnomalyNowDeg = anomalies.EccentricAnomalyDeg;
                        trueAnomalyNowDeg = anomalies.TrueAnomalyDeg;
                        distanceNowAu = anomalies.DistanceAu;
                        var earth = OrbitalMechanics.EarthHeliocentricEcliptic(t);
                        var helio = AsteroidOrbits.HeliocentricEcliptic(asteroid, t);
                        magnitudeNow = AsteroidOrbits.ApparentMagnitude(asteroid, helio, earth);
                    }
                }

                BuildPathPolyline(path);
                UpdateAltitudeChart(target.Name, new Coordinates(raHours, decDeg, Epoch.J2000, Coordinates.RAType.Hours));

                Loaded = target;
                StatusText = $"Loaded {target.Name}.";
            } catch (Exception ex) {
                StatusText = $"Failed to load {target.Name}: {ex.Message}";
                Notification.ShowError($"Perihelion: failed to load {target.Name}: {ex.Message}");
            } finally {
                RaiseLoadedDataChanged();
                IsBusy = false;
            }
        }

        // Keeps every marker's own radius fully inside the canvas -- without this, a point that
        // lands exactly on an edge (common for the path's own first/last point, since the
        // min/max used to normalize the axes come from the path itself) would render its dot
        // half-clipped.
        private const double PathMarkerInset = 6;

        private void BuildPathPolyline(IReadOnlyList<(DateTime date, double raHours, double decDeg)>? path) {
            var points = new PointCollection();
            var markers = new List<PathMarker>();
            pathLeftLabel = pathRightLabel = string.Empty;
            if (path == null || path.Count == 0) {
                PathPoints = points;
                PathMarkers.Clear();
                return;
            }

            // Unwrap RA across the 24h/0h boundary the same way the Touch-N-Stars chart does --
            // otherwise a path crossing midnight RA plots as a spurious jump across the box.
            var raValues = new List<double>(path.Count) { path[0].raHours };
            for (var i = 1; i < path.Count; i++) {
                var prev = raValues[i - 1];
                var cur = path[i].raHours;
                while (cur - prev > 12) cur -= 24;
                while (cur - prev < -12) cur += 24;
                raValues.Add(cur);
            }
            var decValues = path.Select(p => p.decDeg).ToList();

            var minRa = raValues.Min();
            var maxRa = raValues.Max();
            var minDec = decValues.Min();
            var maxDec = decValues.Max();
            var raSpan = maxRa - minRa;
            var decSpan = maxDec - minDec;
            var plotWidth = PathViewWidth - 2 * PathMarkerInset;
            var plotHeight = PathViewHeight - 2 * PathMarkerInset;

            for (var i = 0; i < path.Count; i++) {
                var x = PathMarkerInset + (raSpan > 1e-9 ? (raValues[i] - minRa) / raSpan * plotWidth : plotWidth / 2);
                var y = PathMarkerInset + (decSpan > 1e-9 ? plotHeight - (decValues[i] - minDec) / decSpan * plotHeight : plotHeight / 2);
                points.Add(new Point(x, y));

                var isTonight = i == 0;
                var isEnd = i == path.Count - 1;
                var size = isTonight || isEnd ? 9.0 : 5.0;
                var tooltip = $"{path[i].date:MMM d}\nRA {AstroUtil.HoursToHMS(path[i].raHours)}  Dec {AstroUtil.DegreesToDMS(path[i].decDeg)}";
                markers.Add(new PathMarker { Left = x - size / 2, Top = y - size / 2, Size = size, IsTonight = isTonight, IsEnd = isEnd, Tooltip = tooltip });
            }

            PathMarkers.Clear();
            foreach (var m in markers) PathMarkers.Add(m);

            // Labels sit in their own row below the plot (see the view), not overlaid on the
            // canvas -- a real-hardware test found the overlaid version colliding with the line
            // and dots whenever the path's first/last point happened to land near a top corner.
            // Which date goes on which SIDE is decided by comparing the two points' own x
            // coordinates, not assumed from chronology -- see PathLeftLabel's own doc comment.
            if (points[0].X <= points[^1].X) {
                pathLeftLabel = path[0].date.ToString("MMM d");
                pathRightLabel = path[^1].date.ToString("MMM d");
            } else {
                pathLeftLabel = path[^1].date.ToString("MMM d");
                pathRightLabel = path[0].date.ToString("MMM d");
            }
            PathPoints = points;
        }

        // --- Actions that drive real hardware ---

        private Coordinates LoadedCoordinatesWithOffset() {
            var ra = raHours + offsetRaArcsec / 3600.0 / 15.0; // arcsec -> hours, cos(dec)-compensated rate already; offset is a plain positional nudge
            var dec = decDeg + offsetDecArcsec / 3600.0;
            return new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Hours);
        }

        /// <summary>Opens Perihelion's own popup Framing Composer -- a real sky/FOV view plus
        /// Slew and Center, rotation (real, if a rotator is connected), and offset capture, all
        /// carried into Add to Sequence/Quick Track on confirm. Deliberately Perihelion's own
        /// popup, not a jump to NINA's own Framing Assistant tab (considered and explicitly
        /// rejected -- Perihelion needs its own real window here, not a redirect). Same factory-
        /// resolution path as AddToSequenceAction (see its own comment): PerihelionPlugin's
        /// static SequenceMediator field, reflected via PerihelionSequenceBuilder.ResolveFactory,
        /// since this VM still never imports ISequencerFactory/ISequenceMediator directly.</summary>
        private void FrameAction() {
            if (Loaded == null) return;

            var sequenceMediator = PerihelionPlugin.SequenceMediator;
            if (sequenceMediator == null || !sequenceMediator.Initialized) {
                Notification.ShowError("Perihelion: the sequencer isn't available yet -- try again once NINA has fully started.");
                return;
            }
            var factory = PerihelionSequenceBuilder.ResolveFactory(sequenceMediator);
            if (factory == null) {
                Notification.ShowError("Perihelion: could not reach the sequencer's item factory -- see log.");
                Logger.Error("Perihelion: PerihelionSequenceBuilder.ResolveFactory returned null even though Initialized was true");
                return;
            }

            var trueCoordinates = new Coordinates(raHours, decDeg, Epoch.J2000, Coordinates.RAType.Hours);
            var composerVm = new PerihelionFramingComposerVM(Loaded.Name, trueCoordinates, telescopeMediator, rotatorMediator,
                cameraMediator, imagingMediator, filterWheelMediator, profileService, imageDataFactory, factory);
            var window = new Perihelion.Views.PerihelionFramingComposerWindow(composerVm) {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            window.ShowDialog();

            if (composerVm.Confirmed == true) {
                OffsetRaArcsec = composerVm.OffsetRaArcsec;
                OffsetDecArcsec = composerVm.OffsetDecArcsec;
                CapturedRotationAngle = composerVm.CapturedRotationAngle;
                StatusText = CapturedRotationAngle is double angle
                    ? $"Framing captured -- rotate to {angle}°, offset RA {OffsetRaText} Dec {OffsetDecText}."
                    : $"Framing captured -- offset RA {OffsetRaText} Dec {OffsetDecText}.";
            }
        }

        private async Task SlewAndTrackAction() {
            if (Loaded == null) return;
            IsBusy = true;
            try {
                var success = await telescopeMediator.SlewToCoordinatesAsync(LoadedCoordinatesWithOffset(), CancellationToken.None);
                if (!success) {
                    Notification.ShowWarning("Slew failed or was rejected by the mount");
                    return;
                }
                await SetTrackingRateAction();
            } catch (Exception ex) {
                Notification.ShowError($"Perihelion: slew and track failed: {ex.Message}");
            } finally {
                IsBusy = false;
            }
        }

        private async Task SetTrackingRateAction() {
            if (Loaded == null) return;
            try {
                var item = new SetPerihelionTrackingRate(telescopeMediator, profileService) {
                    ObjectType = Loaded.ObjectType,
                    TargetName = Loaded.Name,
                };
                await item.Execute(new Progress<NINA.Core.Model.ApplicationStatus>(), CancellationToken.None);
                Notification.ShowSuccess($"Tracking rate set for {Loaded.Name}");
            } catch (Exception ex) {
                Notification.ShowError($"Perihelion: setting tracking rate failed: {ex.Message}");
            }
        }

        private async Task SetGuiderShiftRateAction() {
            if (Loaded == null) return;
            try {
                var item = new SetPerihelionGuiderShiftRate(guiderMediator, profileService) {
                    ObjectType = Loaded.ObjectType,
                    TargetName = Loaded.Name,
                };
                await item.Execute(new Progress<NINA.Core.Model.ApplicationStatus>(), CancellationToken.None);
                Notification.ShowSuccess($"Guider shift rate set for {Loaded.Name}");
            } catch (Exception ex) {
                Notification.ShowError($"Perihelion: setting guider shift rate failed: {ex.Message}");
            }
        }

        // --- Quick Track (in-process -- same QuickTrackEngine the Touch-N-Stars HTTP route uses) ---

        private bool guiding = true;
        public bool Guiding {
            get => guiding;
            set { guiding = value; RaisePropertyChanged(); }
        }

        private bool autoReapply;
        public bool AutoReapply {
            get => autoReapply;
            set { autoReapply = value; RaisePropertyChanged(); }
        }

        public int AutoReapplyMinutes { get; }

        private bool quickTrackActive;
        public bool QuickTrackActive {
            get => quickTrackActive;
            private set { quickTrackActive = value; RaisePropertyChanged(); }
        }

        private string quickTrackStatusText = string.Empty;
        public string QuickTrackStatusText {
            get => quickTrackStatusText;
            private set { quickTrackStatusText = value; RaisePropertyChanged(); }
        }

        public AsyncRelayCommand StartQuickTrackCommand { get; }
        public AsyncRelayCommand StopQuickTrackCommand { get; }

        private async Task StartQuickTrackAction() {
            if (Loaded == null) return;
            IsBusy = true;
            try {
                var result = await QuickTrackEngine.StartAsync(
                    telescopeMediator, guiderMediator, profileService,
                    Loaded.ObjectType, Loaded.Name, Guiding,
                    AutoReapply ? AutoReapplyMinutes : null,
                    CancellationToken.None);
                QuickTrackStatusText = result.Message;
                if (result.Success) {
                    Notification.ShowSuccess(result.Message);
                } else {
                    Notification.ShowError(result.Message);
                }
            } finally {
                IsBusy = false;
                RefreshQuickTrackStatus();
            }
        }

        private async Task StopQuickTrackAction() {
            IsBusy = true;
            try {
                var result = await QuickTrackEngine.StopAsync(telescopeMediator, guiderMediator, CancellationToken.None);
                QuickTrackStatusText = result.Message;
            } finally {
                IsBusy = false;
                RefreshQuickTrackStatus();
            }
        }

        private void RefreshQuickTrackStatus() {
            var s = QuickTrackStatus.Current;
            QuickTrackActive = s.Active;
            if (s.Active) {
                var fallback = s.GuidingOnlyFallback ? " (guiding-only fallback)" : "";
                var applied = s.LastRaArcsecPerSec is double ra && s.LastDecArcsecPerSec is double dec
                    ? $" -- last applied RA {ra:F4}, Dec {dec:F4} arcsec/s"
                    : "";
                QuickTrackStatusText = $"Tracking {s.TargetName}{fallback}{applied}";
            } else if (!string.IsNullOrEmpty(quickTrackStatusText) && s.StopReason != null) {
                QuickTrackStatusText = $"Stopped: {s.StopReason}";
            }
        }

        public override void Hide(object o) {
            base.Hide(o);
        }
    }
}
