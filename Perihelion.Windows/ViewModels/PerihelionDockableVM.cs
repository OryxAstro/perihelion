using CommunityToolkit.Mvvm.Input;
using CosineKitty;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using Perihelion.Api;
using Perihelion.Astrometry;
using Perihelion.SequenceItems;
using Perihelion.Sequencing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
        private const double PathViewWidth = 280;
        private const double PathViewHeight = 140;

        private readonly IProfileService profileService;
        private readonly IGuiderMediator guiderMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IFramingAssistantVM framingAssistantVM;
        private readonly IApplicationMediator applicationMediator;
        private readonly ISequencerFactory sequencerFactory;
        private readonly ISequenceMediator sequenceMediator;
        private readonly DispatcherTimer statusTimer;
        private CancellationTokenSource? loadCts;

        [ImportingConstructor]
        public PerihelionDockableVM(
            IProfileService profileService,
            IGuiderMediator guiderMediator,
            ITelescopeMediator telescopeMediator,
            IFramingAssistantVM framingAssistantVM,
            IApplicationMediator applicationMediator,
            ISequencerFactory sequencerFactory,
            ISequenceMediator sequenceMediator) : base(profileService) {
            this.profileService = profileService;
            this.guiderMediator = guiderMediator;
            this.telescopeMediator = telescopeMediator;
            this.framingAssistantVM = framingAssistantVM;
            this.applicationMediator = applicationMediator;
            this.sequencerFactory = sequencerFactory;
            this.sequenceMediator = sequenceMediator;

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
            PathPoints = new PointCollection();
            AutoReapplyMinutes = 15;
            StatusText = "Click Refresh to browse live comets and asteroids.";

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

            FrameCommand = new AsyncRelayCommand(FrameAction, () => Loaded != null);
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
            set { selectedObjectType = value; RaisePropertyChanged(); }
        }

        public ObservableCollection<BrowseObject> BrowseObjects { get; }

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

        private async Task RefreshBrowseListAction() {
            IsBusy = true;
            StatusText = "Fetching live comet and asteroid data...";
            try {
                var objects = await OrbitalTracking.ListBrowseObjectsAsync(HttpClient, DateTime.UtcNow, CancellationToken.None);
                BrowseObjects.Clear();
                foreach (var o in objects.Where(o => o.ObjectType == SelectedObjectType)) {
                    BrowseObjects.Add(o);
                }
                StatusText = $"{BrowseObjects.Count} {SelectedObjectType.ToString().ToLowerInvariant()}(s) loaded, brightest first.";
            } catch (Exception ex) {
                StatusText = $"Failed to fetch: {ex.Message}";
                Notification.ShowError($"Perihelion: failed to fetch browse list: {ex.Message}");
            } finally {
                IsBusy = false;
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

        // Elements card -- fields present depend on ObjectType (comets have no fixed epoch /
        // mean-anomaly-at-epoch; asteroids have no perihelion-passage date in these elements).
        private double? eccentricity, inclinationDeg, argPeriDeg, nodeDeg, perihelionDistanceAu, semiMajorAxisAu;
        private double? meanAnomalyAtEpochDeg, meanAnomalyNowDeg, eccentricAnomalyNowDeg, trueAnomalyNowDeg, distanceNowAu;
        private DateTime? epochOrPerihelionUtc;
        private string epochOrPerihelionLabel = "Epoch";

        public string EccentricityText => eccentricity is double e ? e.ToString("F4") : "--";
        public string InclinationText => inclinationDeg is double i ? $"{i:F4}°" : "--";
        public string ArgPeriText => argPeriDeg is double a ? $"{a:F4}°" : "--";
        public string NodeText => nodeDeg is double n ? $"{n:F4}°" : "--";
        public string PerihelionDistanceText => perihelionDistanceAu is double q ? $"{q:F4} au" : "--";
        public string SemiMajorAxisText => semiMajorAxisAu is double a ? $"{a:F4} au" : "n/a (non-elliptical)";
        public string MeanAnomalyAtEpochText => meanAnomalyAtEpochDeg is double m ? $"{m:F4}°" : "n/a";
        public string MeanAnomalyNowText => meanAnomalyNowDeg is double m ? $"{m:F4}°" : "n/a";
        public string EccentricAnomalyNowText => eccentricAnomalyNowDeg is double e ? $"{e:F4}°" : "n/a";
        public string TrueAnomalyNowText => trueAnomalyNowDeg is double t ? $"{t:F4}°" : "n/a";
        public string DistanceNowText => distanceNowAu is double d ? $"{d:F4} au" : "--";
        public string EpochOrPerihelionLabel => epochOrPerihelionLabel;
        public string EpochOrPerihelionText => epochOrPerihelionUtc is DateTime d ? d.ToString("yyyy-MM-dd HH:mm") + " UTC" : "--";

        private double offsetRaArcsec, offsetDecArcsec;
        public double OffsetRaArcsec {
            get => offsetRaArcsec;
            set { offsetRaArcsec = value; RaisePropertyChanged(); ResetOffsetCommand.NotifyCanExecuteChanged(); }
        }
        public double OffsetDecArcsec {
            get => offsetDecArcsec;
            set { offsetDecArcsec = value; RaisePropertyChanged(); ResetOffsetCommand.NotifyCanExecuteChanged(); }
        }

        /// <summary>One marker per night on the path, positioned in the same coordinate space
        /// as PathPoints (Left/Top already centered, not top-left-anchored) -- IsTonight flags
        /// day 0 (the same "now" instant Load's own position/rate came from) so the chart can
        /// highlight it distinctly from the other nine nights.</summary>
        public sealed class PathMarker {
            public required double Left { get; init; }
            public required double Top { get; init; }
            public required double Size { get; init; }
            public required bool IsTonight { get; init; }
        }

        public PointCollection PathPoints { get; private set; }
        public ObservableCollection<PathMarker> PathMarkers { get; } = new();
        public double PathViewBoxWidth => PathViewWidth;
        public double PathViewBoxHeight => PathViewHeight;
        private string pathStartLabel = string.Empty, pathEndLabel = string.Empty;
        public string PathStartLabel => pathStartLabel;
        public string PathEndLabel => pathEndLabel;

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

        private void AddToSequenceAction() {
            var target = Loaded;
            if (target == null) return;
            if (!sequenceMediator.Initialized) {
                Notification.ShowError("The Advanced Sequencer isn't ready yet -- open the Sequence tab once, then try again.");
                return;
            }

            try {
                var trueCoordinates = new Coordinates(raHours, decDeg, Epoch.J2000, Coordinates.RAType.Hours);
                var filter = SelectedFilterName == NoFilterChangeOption
                    ? null
                    : profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(f => f.Name == SelectedFilterName);
                var exposure = new PerihelionSequenceBuilder.ExposureSettings(filter, ExposureSeconds, FrameCount);

                var dso = PerihelionSequenceBuilder.BuildTargetContainer(
                    sequencerFactory, target.ObjectType, target.Name,
                    trueCoordinates, LoadedCoordinatesWithOffset(),
                    Guiding, AutofocusEnabled ? AutofocusMinutes : null, exposure);

                sequenceMediator.AddAdvancedTarget(dso);

                if (MeridianFlip) {
                    // MeridianFlipTrigger belongs on the sequence's own global triggers (applies
                    // mount-wide, not per-target) -- AddAdvancedTarget only adds the one target
                    // container, so this needs its own path via the already-loaded root.
                    var root = dso.GetRootContainer(dso);
                    if (root != null && !root.Triggers.Any(t => t is NINA.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger)) {
                        root.Add(sequencerFactory.GetTrigger<NINA.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger>());
                    }
                }

                sequenceMediator.SwitchToAdvancedView();
                Notification.ShowSuccess($"Added {target.Name} to the sequence");
            } catch (Exception ex) {
                Notification.ShowError($"Perihelion: failed to add to sequence: {ex.Message}");
                Logger.Error("Perihelion: failed to add to sequence", ex);
            }
        }

        public AsyncRelayCommand LoadCommand { get; }
        public AsyncRelayCommand FrameCommand { get; }
        public AsyncRelayCommand SlewAndTrackCommand { get; }
        public AsyncRelayCommand SetTrackingRateCommand { get; }
        public AsyncRelayCommand SetGuiderShiftRateCommand { get; }
        public RelayCommand ResetOffsetCommand { get; }

        private void RaiseLoadedDataChanged() {
            foreach (var name in new[] {
                nameof(PositionText), nameof(RateText), nameof(MaxExposureText), nameof(MagnitudeText),
                nameof(EccentricityText), nameof(InclinationText), nameof(ArgPeriText), nameof(NodeText),
                nameof(PerihelionDistanceText), nameof(SemiMajorAxisText), nameof(MeanAnomalyAtEpochText),
                nameof(MeanAnomalyNowText), nameof(EccentricAnomalyNowText), nameof(TrueAnomalyNowText),
                nameof(DistanceNowText), nameof(EpochOrPerihelionLabel), nameof(EpochOrPerihelionText),
                nameof(PathPoints), nameof(PathMarkers), nameof(PathStartLabel), nameof(PathEndLabel),
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
                        meanAnomalyAtEpochDeg = meanAnomalyNowDeg = eccentricAnomalyNowDeg = trueAnomalyNowDeg = null;
                        epochOrPerihelionLabel = "Perihelion Passage";
                        epochOrPerihelionUtc = comet.PerihelionDate;
                        magnitudeNow = CometOrbits.PredictedMagnitude(comet, now, t);
                        var helio = CometOrbits.HeliocentricEcliptic(comet, now);
                        distanceNowAu = Math.Sqrt(helio.X * helio.X + helio.Y * helio.Y + helio.Z * helio.Z);
                    }
                } else {
                    var asteroid = AsteroidOrbits.FindByName(target.Name);
                    if (asteroid != null) {
                        eccentricity = asteroid.Eccentricity;
                        inclinationDeg = asteroid.InclinationDeg;
                        argPeriDeg = asteroid.ArgPeriDeg;
                        nodeDeg = asteroid.NodeDeg;
                        semiMajorAxisAu = asteroid.A;
                        perihelionDistanceAu = asteroid.A * (1 - asteroid.Eccentricity);
                        meanAnomalyAtEpochDeg = asteroid.MeanAnomalyDeg;
                        epochOrPerihelionLabel = "Epoch";
                        epochOrPerihelionUtc = JulianDateToUtc(asteroid.EpochJd);
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
            pathStartLabel = pathEndLabel = string.Empty;
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
                var size = isTonight ? 9.0 : 5.0;
                markers.Add(new PathMarker { Left = x - size / 2, Top = y - size / 2, Size = size, IsTonight = isTonight });
            }

            PathMarkers.Clear();
            foreach (var m in markers) PathMarkers.Add(m);

            // Labels sit in their own row below the plot (see the view), not overlaid on the
            // canvas -- a real-hardware test found the overlaid version colliding with the line
            // and dots whenever the path's first/last point happened to land near a top corner.
            pathStartLabel = path[0].date.ToString("MMM d");
            pathEndLabel = path[^1].date.ToString("MMM d");
            PathPoints = points;
        }

        // --- Actions that drive real hardware ---

        private Coordinates LoadedCoordinatesWithOffset() {
            var ra = raHours + offsetRaArcsec / 3600.0 / 15.0; // arcsec -> hours, cos(dec)-compensated rate already; offset is a plain positional nudge
            var dec = decDeg + offsetDecArcsec / 3600.0;
            return new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Hours);
        }

        private async Task FrameAction() {
            if (Loaded == null) return;
            try {
                var dso = new NINA.Astrometry.DeepSkyObject(
                    Loaded.Name,
                    LoadedCoordinatesWithOffset(),
                    profileService.ActiveProfile.ApplicationSettings.SkyAtlasImageRepository,
                    profileService.ActiveProfile.AstrometrySettings.Horizon);
                applicationMediator.ChangeTab(ApplicationTab.FRAMINGASSISTANT);
                await framingAssistantVM.SetCoordinates(dso);
            } catch (Exception ex) {
                Notification.ShowError($"Perihelion: failed to send to framing wizard: {ex.Message}");
                Logger.Error("Perihelion: failed to send to framing wizard", ex);
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
