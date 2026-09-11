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
using NINA.WPF.Base.SkySurvey;
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
    /// Perihelion's own native dockable panel for Windows NINA -- the Windows-only
    /// counterpart to the Touch-N-Stars web panel, but running in-process rather than as an HTTP
    /// client of Perihelion's own API server (Quick Track start/stop calls QuickTrackEngine
    /// directly, same call NINA's own advanced sequencer path would end up making). A dockable
    /// panel (standard IDockableVM export shape) with a Browse/Load list, a details area, and a
    /// Frame/Set Tracking Rate/Clear Offset action set, plus live-brightness browsing, a
    /// 10-night path preview, and Quick Track's auto-reapply.
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

        /// <summary>Owned here at the panel's own whole-session lifetime and handed to every
        /// PerihelionFramingComposerVM the Frame button creates, rather than constructed fresh
        /// per window: a second SkyMapAnnotator/DatabaseInteraction pairing in the same NINA
        /// process silently fails to render its Offline Sky Map (no exception, the tile
        /// compositing pass just never fires again) -- matching NINA's own Framing Assistant,
        /// which keeps one instance alive for the whole session and re-Initializes it instead
        /// of recreating it each time.</summary>
        private readonly SkyMapAnnotator skyMapAnnotator;

        // ISequencerFactory/ISequenceMediator are NOT imported here directly -- doing so
        // silently breaks this VM's own MEF composition (the whole panel vanishes from the
        // Imaging tab, no error). See PerihelionPlugin's own static SequencerFactory/
        // SequenceMediator fields for the working alternative. IRotatorMediator/
        // IImageDataFactory/ICameraMediator/IImagingMediator/IFilterWheelMediator are all
        // safely importable standard interfaces, unlike the two above.
        // IFramingAssistantVM/IApplicationMediator are deliberately not imported -- "Frame"
        // opens Perihelion's own popup Framing Composer, not NINA's own Framing Assistant tab.

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

            // Same persisted FramingAssistantSettings the Composer itself used to read --
            // see the SkyMapAnnotator field's own doc comment for why this is constructed once
            // here rather than fresh per Composer window.
            var framingDefaults = profileService.ActiveProfile.FramingAssistantSettings;
            skyMapAnnotator = new SkyMapAnnotator(telescopeMediator, profileService) {
                AnnotateConstellations = framingDefaults.AnnotateConstellations,
                AnnotateConstellationBoundaries = framingDefaults.AnnotateConstellationBoundaries,
                AnnotateGrid = framingDefaults.AnnotateGrid,
                AnnotateDSO = framingDefaults.AnnotateDSO,
            };

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
            UpdateAsteroidsCommand = new AsyncRelayCommand(UpdateAsteroidsAction);
            ImportCometsCommand = new AsyncRelayCommand(ImportCometsAction);
            ExportCometsCommand = new AsyncRelayCommand(ExportCometsAction);
            ClearCometsCommand = new AsyncRelayCommand(ClearCometsAction);
            ImportAsteroidsCommand = new AsyncRelayCommand(ImportAsteroidsAction);
            ExportAsteroidsCommand = new AsyncRelayCommand(ExportAsteroidsAction);
            ClearAsteroidsCommand = new AsyncRelayCommand(ClearAsteroidsAction);
            RefreshLastUpdatedText();

            PathPoints = new PointCollection();
            // ReapplyIntervalSeconds itself always reads the live value with no caching, but a
            // plain property read alone doesn't refresh anything already bound in the UI -- WPF
            // only re-reads a binding when it's told to. Subscribing here means an Options-page
            // change is reflected on this already-open panel immediately, no NINA restart needed.
            if (PerihelionPlugin.Instance != null) {
                PerihelionPlugin.Instance.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(PerihelionPlugin.QuickTrackReapplyIntervalSeconds)) {
                        RaisePropertyChanged(nameof(ReapplyIntervalSeconds));
                    }
                };
            }
            StatusText = "Loading live comet and asteroid data...";

            // "(Don't switch)" first, then every filter actually configured on this profile --
            // matches SwitchFilter's own convention (an empty/null ComboBoxText means leave the
            // wheel alone, not a filter position).
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

            SetTrackingRateCommand = new AsyncRelayCommand(SetTrackingRateAction, CanSetTrackingRate);
            SetTrackingRateCommand.RegisterPropertyChangeNotification(this, nameof(Loaded));
            SetTrackingRateCommand.RegisterPropertyChangeNotification(telescopeMediator.GetInfo(), nameof(TelescopeInfo.Connected), nameof(TelescopeInfo.CanSetRightAscensionRate), nameof(TelescopeInfo.CanSetDeclinationRate));

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

            // Auto-populate on open rather than waiting for an explicit Refresh click, so a
            // warm cache shows results immediately. Fire-and-forget is safe here:
            // RefreshBrowseListAction handles its own IsBusy/StatusText/error reporting.
            _ = RefreshBrowseListAction();
        }

        private bool CanSetTrackingRate() {
            var info = telescopeMediator.GetInfo();
            return Loaded != null && info.Connected && info.CanSetRightAscensionRate && info.CanSetDeclinationRate;
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
            set { selectedObjectType = value; RaisePropertyChanged(); browseObjectsView?.Refresh(); UpdateStatusTextFromCurrentFilter(); }
        }

        public ObservableCollection<BrowseObject> BrowseObjects { get; }

        // CollectionViewSource.GetDefaultView returns the exact same ICollectionView WPF already
        // uses internally for the ListView's own ItemsSource="{Binding BrowseObjects}" binding --
        // setting Filter here affects what that ListView shows with no XAML change needed at all.
        private readonly ICollectionView browseObjectsView;

        private string searchText = string.Empty;
        public string SearchText {
            get => searchText;
            set { searchText = value; RaisePropertyChanged(); browseObjectsView.Refresh(); UpdateStatusTextFromCurrentFilter(); }
        }

        // Switching the object type or typing a search term re-filters the already-fetched
        // BrowseObjects instantly (both setters above), but StatusText's own count/wording was
        // only ever set once, by the fetch that last populated the list -- switching from Comets
        // to Asteroids left it showing the stale comet count until Refresh was clicked, which
        // looked like the switch itself hadn't done anything. Keeps StatusText in sync with
        // whatever the list is actually showing right now, without a network round-trip.
        private void UpdateStatusTextFromCurrentFilter() {
            if (browseObjectsView == null || IsBusy) return;
            StatusText = $"{VisibleBrowseObjectCount} {SelectedObjectType.ToString().ToLowerInvariant()}(s) loaded, brightest first.";
        }

        /// <summary>Number of rows the Browse list is actually showing right now (current object
        /// type + search text applied) -- used for status messages instead of BrowseObjects.Count,
        /// which now holds both object types combined.</summary>
        private int VisibleBrowseObjectCount => browseObjectsView.Cast<object>().Count();

        public RelayCommand SearchCommand { get; }

        // --- Update Databases ---
        //
        // User concern: does this cache actually persist across NINA restarts/reboots, or
        // could it silently clear out? Verified empirically, not assumed: both
        // %LocalAppData%\NINA\PerihelionData\comet-elements-cache.json and
        // \comet-activity-cache.json (COBS) already exist on the deployed machine from
        // earlier in this same session, surviving every restart and redeploy since -- this is
        // the same stable, persistent NINA.Core.Utility.CoreUtil.APPLICATIONTEMPPATH root that
        // hosts the Plugins folder itself (confirmed from NINA.Plugin/Constants.cs using the same
        // constant for its own UserExtensionsFolder), not a volatile OS temp directory despite
        // the confusing name. CometOrbits' own comet-elements cache has a 6-hour TTL and is used
        // automatically by Load/Refresh; these two buttons are the explicit "do it now, bypass
        // the TTL" actions, matching this project's own existing PINS-side /objects/refresh-cobs
        // route (mirrored exactly, not reinvented) for the same reason -- a full COBS sweep
        // across every comet takes several seconds to tens of seconds, so it stays a deliberate,
        // explicit action rather
        // than something that runs silently on every Load.
        private string cometsLastUpdatedText = "Never";
        public string CometsLastUpdatedText => cometsLastUpdatedText;
        private string cobsLastUpdatedText = "Never";
        public string CobsLastUpdatedText => cobsLastUpdatedText;
        private string asteroidsLastUpdatedText = "Never";
        public string AsteroidsLastUpdatedText => asteroidsLastUpdatedText;

        /// <summary>"Comets (4108)"/"Asteroids (6761)"/"COBS (37)" -- per-category count labels.
        /// All three *Orbits/*Activity CachedCount properties are cheap synchronous reads of
        /// whatever's already in memory/on disk, never a live fetch. COBS counts comets with an
        /// actual cached observation, not every comet ever checked -- see
        /// CometActivity.CachedCount's own doc comment for why those aren't the same
        /// number.</summary>
        public string CometsCountText => $"Comets ({CometOrbits.CachedCount})";
        public string AsteroidsCountText => $"Asteroids ({AsteroidOrbits.CachedCount})";
        public string CobsCountText => $"COBS ({CometActivity.CachedCount})";

        public AsyncRelayCommand UpdateCometsCommand { get; }
        public AsyncRelayCommand UpdateCobsCommand { get; }
        public AsyncRelayCommand UpdateAsteroidsCommand { get; }

        /// <summary>Import/Export/Clear -- for an observatory behind a shared/high-density
        /// network egress that gets IP-blocked by MPC/JPL for looking "spammy": fetching data
        /// once from an unblocked network and distributing the file locally is the workaround.
        /// Comets accept/produce MPC's own CometEls.txt format directly (so anyone, not
        /// just another Perihelion install, can produce a compatible file); asteroids use
        /// Perihelion's own plain JSON list instead, since there's no external universal bulk
        /// format for this data -- see CometOrbits/AsteroidOrbits' own ImportFromFileAsync doc
        /// comments for the full reasoning. COBS deliberately has none of these three: it's a live per-comet
        /// observation cache, not an elements dataset, and doesn't fit the same import/export
        /// story.</summary>
        public AsyncRelayCommand ImportCometsCommand { get; }
        public AsyncRelayCommand ExportCometsCommand { get; }
        public AsyncRelayCommand ClearCometsCommand { get; }
        public AsyncRelayCommand ImportAsteroidsCommand { get; }
        public AsyncRelayCommand ExportAsteroidsCommand { get; }
        public AsyncRelayCommand ClearAsteroidsCommand { get; }

        /// <summary>Status for the Update Sources actions specifically (Update Comets/Update
        /// COBS) -- kept separate from StatusText, which is Browse/Load-only, so an Update
        /// Sources message doesn't appear next to the Browse combo box as if it were a
        /// Browse-list status.</summary>
        private string updateSourcesStatusText = string.Empty;
        public string UpdateSourcesStatusText {
            get => updateSourcesStatusText;
            set { updateSourcesStatusText = value; RaisePropertyChanged(); }
        }

        private void RefreshLastUpdatedText() {
            cometsLastUpdatedText = CometOrbits.LastSyncedUtc is DateTime c ? c.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Never";
            cobsLastUpdatedText = CometActivity.LastFullRefreshUtc is DateTime o ? o.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Never";
            asteroidsLastUpdatedText = AsteroidOrbits.LastSyncedUtc is DateTime a ? a.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Never";
            RaisePropertyChanged(nameof(CometsLastUpdatedText));
            RaisePropertyChanged(nameof(CobsLastUpdatedText));
            RaisePropertyChanged(nameof(CometsCountText));
            RaisePropertyChanged(nameof(AsteroidsLastUpdatedText));
            RaisePropertyChanged(nameof(AsteroidsCountText));
            RaisePropertyChanged(nameof(CobsCountText));
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

        private async Task UpdateAsteroidsAction() {
            IsBusy = true;
            UpdateSourcesStatusText = "Updating asteroid elements from JPL...";
            try {
                var ok = await AsteroidOrbits.SyncNowAsync(HttpClient, CancellationToken.None);
                UpdateSourcesStatusText = ok ? "Asteroid elements updated." : "Asteroid elements update failed -- see log.";
            } catch (Exception ex) {
                UpdateSourcesStatusText = $"Asteroid elements update failed: {ex.Message}";
                Notification.ShowError($"Perihelion: asteroid elements update failed: {ex.Message}");
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

        private async Task ImportCometsAction() {
            var dialog = new Microsoft.Win32.OpenFileDialog {
                Title = "Import Comet Elements",
                Filter = "MPC comet elements (*.txt)|*.txt|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            UpdateSourcesStatusText = "Importing comet elements...";
            try {
                var count = await CometOrbits.ImportFromFileAsync(dialog.FileName, CancellationToken.None);
                UpdateSourcesStatusText = $"Imported {count} comet(s) from {System.IO.Path.GetFileName(dialog.FileName)}.";
            } catch (Exception ex) {
                UpdateSourcesStatusText = $"Comet import failed: {ex.Message}";
                Notification.ShowError($"Perihelion: comet import failed: {ex.Message}");
            } finally {
                RefreshLastUpdatedText();
                IsBusy = false;
            }
        }

        private async Task ExportCometsAction() {
            var dialog = new Microsoft.Win32.SaveFileDialog {
                Title = "Export Comet Elements",
                FileName = "CometEls.txt",
                Filter = "MPC comet elements (*.txt)|*.txt|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            UpdateSourcesStatusText = "Exporting comet elements...";
            try {
                var ok = await CometOrbits.ExportToFileAsync(dialog.FileName, CancellationToken.None);
                UpdateSourcesStatusText = ok
                    ? $"Exported comet elements to {System.IO.Path.GetFileName(dialog.FileName)}."
                    : "Nothing to export -- comet elements have never been synced on this install.";
            } catch (Exception ex) {
                UpdateSourcesStatusText = $"Comet export failed: {ex.Message}";
                Notification.ShowError($"Perihelion: comet export failed: {ex.Message}");
            } finally {
                IsBusy = false;
            }
        }

        private async Task ClearCometsAction() {
            var confirm = MessageBox.Show(
                "This removes all cached comet elements. The Browse list's comets will be empty until the next Update or a fresh Load.",
                "Clear Comet Cache", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            IsBusy = true;
            try {
                await CometOrbits.ClearAsync(CancellationToken.None);
                foreach (var b in BrowseObjects.Where(o => o.ObjectType == OrbitalObjectType.Comet).ToList()) {
                    BrowseObjects.Remove(b);
                }
                UpdateSourcesStatusText = "Comet cache cleared.";
            } finally {
                RefreshLastUpdatedText();
                IsBusy = false;
            }
        }

        private async Task ImportAsteroidsAction() {
            var dialog = new Microsoft.Win32.OpenFileDialog {
                Title = "Import Asteroid Elements",
                Filter = "Perihelion asteroid elements (*.json)|*.json|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            UpdateSourcesStatusText = "Importing asteroid elements...";
            try {
                var count = await AsteroidOrbits.ImportFromFileAsync(dialog.FileName, CancellationToken.None);
                UpdateSourcesStatusText = $"Imported {count} asteroid(s) from {System.IO.Path.GetFileName(dialog.FileName)}.";
            } catch (Exception ex) {
                UpdateSourcesStatusText = $"Asteroid import failed: {ex.Message}";
                Notification.ShowError($"Perihelion: asteroid import failed: {ex.Message}");
            } finally {
                RefreshLastUpdatedText();
                IsBusy = false;
            }
        }

        private async Task ExportAsteroidsAction() {
            var dialog = new Microsoft.Win32.SaveFileDialog {
                Title = "Export Asteroid Elements",
                FileName = "perihelion-asteroid-elements.json",
                Filter = "Perihelion asteroid elements (*.json)|*.json|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            UpdateSourcesStatusText = "Exporting asteroid elements...";
            try {
                var ok = await AsteroidOrbits.ExportToFileAsync(dialog.FileName, CancellationToken.None);
                UpdateSourcesStatusText = ok
                    ? $"Exported asteroid elements to {System.IO.Path.GetFileName(dialog.FileName)}."
                    : "Nothing to export -- asteroid elements have never been synced on this install.";
            } catch (Exception ex) {
                UpdateSourcesStatusText = $"Asteroid export failed: {ex.Message}";
                Notification.ShowError($"Perihelion: asteroid export failed: {ex.Message}");
            } finally {
                IsBusy = false;
            }
        }

        private async Task ClearAsteroidsAction() {
            var confirm = MessageBox.Show(
                "This removes all cached asteroid elements. The Browse list's asteroids will be empty until the next Update or a fresh Load.",
                "Clear Asteroid Cache", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            IsBusy = true;
            try {
                await AsteroidOrbits.ClearAsync(CancellationToken.None);
                foreach (var b in BrowseObjects.Where(o => o.ObjectType == OrbitalObjectType.Asteroid).ToList()) {
                    BrowseObjects.Remove(b);
                }
                UpdateSourcesStatusText = "Asteroid cache cleared.";
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
        /// the full "why": even a warm COBS cache measurably slowed down the initial render.
        /// Reuses ListBrowseObjectsAsync's own parallel COBS-fetch logic rather
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
        // predicted (H/G model) value can be badly wrong during an outburst, and that's only
        // useful to notice when the observed value sits right next to it. Diff-colored to
        // match Touch-N-Stars' own magDiffTier convention exactly (same thresholds, ported here
        // rather than reinvented) -- the two values are colored independently since the most
        // recent report and the 5-observation average can genuinely disagree with each other,
        // not just with the prediction.
        private double? observedMagnitude, observedAverageMagnitude;

        public string ObservedMagnitudeMainText => observedMagnitude is double m ? m.ToString("F2") : "n/a";
        public Brush ObservedMagnitudeMainBrush => MagnitudeDiffBrush(magnitudeNow, observedMagnitude);

        public string ObservedMagnitudeAverageText =>
            observedAverageMagnitude is double avg ? $" (5-obs avg {avg:F2})" : "";
        public Brush ObservedMagnitudeAverageBrush => MagnitudeDiffBrush(magnitudeNow, observedAverageMagnitude);

        // Same tiers/thresholds as PerihelionView.vue's own magDiffTier: negative diff (observed
        // brighter than predicted, e.g. an outburst) is the "ok"/worth-noticing-positively case,
        // not danger -- a comet significantly dimmer than predicted is the tier that actually
        // warrants a warning color. NINA's own theme only defines Warning/Error notification
        // brushes (confirmed from NINA.WPF.Base's own Brushes.xaml -- no "success" brush exists
        // at all), so "ok" falls back to a plain literal green rather than a theme resource that
        // doesn't exist; PrimaryBrush covers the neutral "close to predicted" case.
        // Internal, not private -- MagnitudeDiffBrushConverter (Browse list's own per-row
        // coloring, a separate XAML location from this VM's Position/Elements card) reuses this
        // exact same threshold logic rather than duplicating it a second time.
        internal static Brush MagnitudeDiffBrush(double? predicted, double? observed) {
            if (predicted is not double p || observed is not double o) {
                return LookupBrush("PrimaryBrush", Brushes.Gray);
            }
            var diff = o - p;
            if (diff <= -1) return LookupBrush(null, new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
            if (diff >= 3) return LookupBrush("NotificationErrorBrush", Brushes.OrangeRed);
            if (diff >= 1) return LookupBrush("NotificationWarningBrush", Brushes.Orange);
            return LookupBrush("PrimaryBrush", Brushes.Gray);
        }

        private static Brush LookupBrush(string? resourceKey, Brush fallback) {
            if (resourceKey != null && System.Windows.Application.Current?.TryFindResource(resourceKey) is Brush b) {
                return b;
            }
            return fallback;
        }

        // Elements card -- Epoch and Periapsis are shown side by side, not one-or-the-other: a
        // comet's own perihelion passage time T is a separate quantity from "Epoch" (the
        // reference date its Mean Anomaly at Epoch is computed for -- today's date at 00:00 UTC,
        // a convention cross-checked against another tool's displayed value for the same
        // comet on the same day), not a substitute for it.
        private double? eccentricity, inclinationDeg, argPeriDeg, nodeDeg, perihelionDistanceAu, semiMajorAxisAu;
        private double? meanAnomalyAtEpochDeg, meanAnomalyNowDeg, eccentricAnomalyNowDeg, trueAnomalyNowDeg, distanceNowAu;
        private DateTime? epochUtc, periapsisUtc;
        private string sourceText = "--";
        private double? epochAgeDays;
        private bool epochIsStale;

        public string EccentricityText => eccentricity is double e ? e.ToString("F4") : "--";
        public string InclinationText => inclinationDeg is double i ? $"{i:F4}°" : "--";
        public string ArgPeriText => argPeriDeg is double a ? $"{a:F4}°" : "--";
        public string NodeText => nodeDeg is double n ? $"{n:F4}°" : "--";
        public string PerihelionDistanceText => perihelionDistanceAu is double q ? $"{q:F4} au" : "--";
        public string SemiMajorAxisText => semiMajorAxisAu is double a ? $"{a:F4} au" : "n/a (non-elliptical)";
        // Wrapped to (-180, 180] rather than the raw [0, 360) range -- values near the
        // wraparound point (e.g. 356.25° vs. -3.75°) otherwise look "dramatically different"
        // at a glance despite being the same angle.
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

        // Epoch-staleness warning -- see CometOrbits/AsteroidOrbits' own EpochAgeDays doc
        // comment for why this matters (propagating pure two-body elements far from their own
        // reference epoch with no perturbation model). Empty string when not stale, same "drives
        // its own Visibility via NullOrEmptyToVisibility" convention already used for the Quick
        // Track status lines below, rather than a separate bool + Visibility property pair.
        public string EpochStaleWarningText => epochIsStale && epochAgeDays is double d
            ? $"This object's orbital elements are {d:F0} days from their own reference epoch -- positions may be less accurate than usual for a target this far from its data's own anchor date."
            : string.Empty;

        // Wraps to (-180, 180] -- the "+540" shifts any double-precision value (whatever sign or
        // magnitude the underlying %360 in AsteroidOrbits/CometOrbits' own ComputeAnomalies left
        // it in) into a single positive range before the final %360 and re-centering.
        private static double WrapSigned(double degrees) => ((degrees % 360) + 540) % 360 - 180;

        // Arcsec remains the internal storage (what LoadedCoordinatesWithOffset actually adds),
        // but displayed as HH:MM:SS for RA and DMS for Dec instead of a raw arcsec number
        // (AstroUtil.HoursToHMS/DegreesToDMS correctly handle negative offsets with a leading
        // "-", unlike a plain position which never goes negative). Read-only display, not an
        // editable sexagesimal text box -- Set Offset (capture from the mount) and Clear Offset
        // are the only ways to change this, avoiding a bespoke bidirectional parser.
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
        /// vector chart like this one) shows something, not just an anonymous dot.</summary>
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
        // other way). A user-reported bug: the previous Start=Left/End=Right assumption put
        // the wrong date under each dot whenever a comet's own path happened to run right-to-left.
        // Touch-N-Stars' own web chart (OrbitalPathChart.vue) already gets this right by anchoring
        // each label to that point's own actual x-coordinate; this mirrors the same fix.
        private string pathLeftLabel = string.Empty, pathRightLabel = string.Empty;
        public string PathLeftLabel => pathLeftLabel;
        public string PathRightLabel => pathRightLabel;

        /// <summary>A light interior line, purely a proportion aid (is the curve steep? does it
        /// cross a third of the box?) -- matches Touch-N-Stars' own OrbitalPathChart.vue gridX/
        /// gridY exactly (quarter divisions of the plot box, not a labeled coordinate grid).</summary>
        public sealed class PathGridLine {
            public required double X1 { get; init; }
            public required double Y1 { get; init; }
            public required double X2 { get; init; }
            public required double Y2 { get; init; }
        }
        public ObservableCollection<PathGridLine> PathGridLines { get; } = new();

        /// <summary>"0.98° total drift over 9 nights (~0.11°/night)" -- cos(dec)-compensated true
        /// angular separation between the path's own first/last points (same convention as
        /// OrbitalTracking.cs's own tracking-rate math), unlike the chart's own plot, which
        /// independently stretches RA and Dec to fill the box and so isn't a true angular shape.
        /// Ported directly from OrbitalPathChart.vue's own endpointDriftDeg/driftSummary.</summary>
        private string pathDriftSummaryText = string.Empty;
        public string PathDriftSummaryText => pathDriftSummaryText;

        /// <summary>An angular reference for the path line's own length -- not a coordinate
        /// grid. X1/X2/Y are already in the same canvas pixel space as PathPoints/PathMarkers;
        /// LabelText sits just above the bar's own start tick. Null when there's no meaningful
        /// path (fewer than 2 nights, or the two endpoints plot on top of each other). Ported
        /// from OrbitalPathChart.vue's own scaleBar/scaleBarSide/NICE_DEG_STEPS -- same "nice"
        /// round-value selection and same whichever-side-has-more-clearance-from-the-whole-path
        /// logic, simplified only in that the label is always left-anchored at the bar's own
        /// start tick rather than also flipping its own text-anchor per side (WPF has no
        /// equivalent to SVG's text-anchor without pre-measuring the string's rendered width).</summary>
        public sealed class PathScaleBar {
            public required double X1 { get; init; }
            public required double X2 { get; init; }
            public required double Y { get; init; }
            // Precomputed here, not via XAML arithmetic (no built-in +/- binding converter in
            // this project, and one property per tick is simpler than adding one) -- the
            // two end-tick verticals and the label's own row, all a few px off the bar's own Y.
            public required double TickTopY { get; init; }
            public required double TickBottomY { get; init; }
            public required double LabelY { get; init; }
            public required string Label { get; init; }
        }
        private PathScaleBar? pathScaleBar;
        public PathScaleBar? PathScaleBarInfo {
            get => pathScaleBar;
            private set { pathScaleBar = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(PathScaleBarVisibility)); }
        }
        // Plain Visibility, not a bool + BooleanToVisibilityConverter -- this project has no
        // converters declared as resources anywhere yet, and an enum property here is one
        // less thing to wire up for a single binding.
        public System.Windows.Visibility PathScaleBarVisibility => PathScaleBarInfo != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        // --- Tonight's altitude ---
        //
        // Uses NINA's own AltitudeChart control (NINA.WPF.Base.View.AltitudeChart) rather than
        // a hand-rolled one, for the same twilight shading/"Now" line/transit annotation/moon
        // position every other NINA panel gets for free. The control binds its own DataContext
        // (a NINA.Astrometry.DeepSkyObject -- SkyObjectBase.Altitudes/Horizon/MaxAltitude
        // compute themselves once SetDateAndPosition is called) plus NighttimeData for the
        // twilight/moon background, the same pattern FramingAssistantView.xaml and
        // SkyAtlasView.xaml use. NighttimeCalculator's ReferenceDate (not DateTime.Now
        // directly) keeps the target curve and twilight background on the same night window.
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

        // This VM never imports ISequencerFactory/ISequenceMediator directly -- see
        // PerihelionPlugin's own comment on SequenceMediator and
        // PerihelionSequenceBuilder.ResolveFactory's doc comment for why. It reads
        // PerihelionPlugin's own static SequenceMediator field instead and reflects the
        // ISequencerFactory out of it, the same way ninaAPI's own Sequence.cs route does.
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
                // EnsureGlobalMeridianFlipTrigger's own doc comment for why. Best-effort: if the
                // root can't be reached, the target itself was still added successfully above,
                // so this only logs rather than rolling back or erroring the whole action.
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
        public AsyncRelayCommand SetTrackingRateCommand { get; }
        public RelayCommand ResetOffsetCommand { get; }

        private void RaiseLoadedDataChanged() {
            foreach (var name in new[] {
                nameof(PositionText), nameof(RateText), nameof(MaxExposureText), nameof(MagnitudeText),
                nameof(ObservedMagnitudeMainText), nameof(ObservedMagnitudeMainBrush),
                nameof(ObservedMagnitudeAverageText), nameof(ObservedMagnitudeAverageBrush),
                nameof(EccentricityText), nameof(InclinationText), nameof(ArgPeriText), nameof(NodeText),
                nameof(PerihelionDistanceText), nameof(SemiMajorAxisText), nameof(MeanAnomalyAtEpochText),
                nameof(MeanAnomalyNowText), nameof(EccentricAnomalyNowText), nameof(TrueAnomalyNowText),
                nameof(DistanceNowText), nameof(EpochText), nameof(EpochJulianText), nameof(PeriapsisText), nameof(PeriapsisJulianText), nameof(SourceText),
                nameof(EpochStaleWarningText),
                nameof(PathPoints), nameof(PathMarkers), nameof(PathLeftLabel), nameof(PathRightLabel),
                nameof(PathGridLines), nameof(PathDriftSummaryText), nameof(PathScaleBarInfo), nameof(PathScaleBarVisibility),
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

                epochAgeDays = null;
                epochIsStale = false;

                if (target.ObjectType == OrbitalObjectType.Comet) {
                    var comet = await CometOrbits.FindByNameAsync(HttpClient, target.Name, ct);
                    if (comet != null) {
                        eccentricity = comet.Eccentricity;
                        inclinationDeg = comet.InclinationDeg;
                        argPeriDeg = comet.ArgPeriDeg;
                        nodeDeg = comet.NodeDeg;
                        perihelionDistanceAu = comet.Q;
                        semiMajorAxisAu = comet.Eccentricity < 1 ? comet.Q / (1 - comet.Eccentricity) : (double?)null;
                        epochAgeDays = CometOrbits.EpochAgeDays(comet, now);
                        epochIsStale = CometOrbits.IsEpochStale(comet, now);
                        // A comet has no stored epoch the way an asteroid does -- MPC's own comet
                        // elements are parameterized by perihelion passage time T instead. "Epoch"
                        // here is today's date at 00:00 UTC, a reference-date convention
                        // cross-checked against another tool's displayed Epoch for this same
                        // comet on the same day (matched exactly), used purely so Mean Anomaly at
                        // Epoch has a concrete instant to be computed for.
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

                        // COBS (observed brightness) -- fetched only for the loaded object,
                        // not the whole browse list, unlike the Touch-N-Stars panel's own
                        // includeCobs path: that one needs a non-blocking per-row background
                        // sweep specifically because it covers the WHOLE list; loading a single
                        // object doesn't have that problem, so a direct await here is simplest
                        // and correct. Value: predicted (H/G model) magnitude can be badly
                        // wrong during an outburst -- 10P/Tempel and 220P/McNaught are verified
                        // cases several magnitudes off.
                        var activity = await CometActivity.FetchAsync(HttpClient, target.Name, ct);
                        observedMagnitude = activity?.MostRecent.Magnitude;
                        observedAverageMagnitude = activity?.RecentAverageMagnitude;
                    }
                } else {
                    observedMagnitude = observedAverageMagnitude = null; // COBS is comet-only
                    var asteroid = await AsteroidOrbits.FindByNameAsync(HttpClient, target.Name, ct);
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
                        sourceText = "JPL SBDB (live)";
                        epochAgeDays = AsteroidOrbits.EpochAgeDays(asteroid, now);
                        epochIsStale = AsteroidOrbits.IsEpochStale(asteroid, now);
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

        // "Nice" round angular values for the scale bar, smallest first -- ported directly from
        // OrbitalPathChart.vue's own NICE_DEG_STEPS.
        private static readonly (double Deg, string Label)[] PathNiceDegSteps = {
            (1.0 / 3600, "1\""), (2.0 / 3600, "2\""), (5.0 / 3600, "5\""), (10.0 / 3600, "10\""), (30.0 / 3600, "30\""),
            (1.0 / 60, "1'"), (2.0 / 60, "2'"), (5.0 / 60, "5'"), (10.0 / 60, "10'"), (30.0 / 60, "30'"),
            (1, "1°"), (2, "2°"), (5, "5°"), (10, "10°"), (20, "20°"), (30, "30°"),
        };

        // Ported from OrbitalPathChart.vue's own formatDeg -- picks whichever unit (deg/arcmin/
        // arcsec) reads as a sensible number rather than always showing a tiny or huge degree
        // value.
        private static string FormatDeg(double deg) {
            if (deg >= 1) return $"{deg.ToString(deg < 10 ? "F2" : "F1")}°";
            var arcmin = deg * 60;
            if (arcmin >= 1) return $"{arcmin.ToString(arcmin < 10 ? "F1" : "F0")}′";
            return $"{(deg * 3600).ToString("F0")}″";
        }

        private void BuildPathPolyline(IReadOnlyList<(DateTime date, double raHours, double decDeg)>? path) {
            var points = new PointCollection();
            var markers = new List<PathMarker>();
            pathLeftLabel = pathRightLabel = string.Empty;
            pathDriftSummaryText = string.Empty;
            PathGridLines.Clear();
            PathScaleBarInfo = null;
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

            // Light interior grid -- quarter divisions of the plot box, matching
            // OrbitalPathChart.vue's own gridX/gridY exactly.
            for (var i = 1; i <= 3; i++) {
                var gx = PathMarkerInset + i / 4.0 * plotWidth;
                PathGridLines.Add(new PathGridLine { X1 = gx, Y1 = PathMarkerInset, X2 = gx, Y2 = PathMarkerInset + plotHeight });
                var gy = PathMarkerInset + i / 4.0 * plotHeight;
                PathGridLines.Add(new PathGridLine { X1 = PathMarkerInset, Y1 = gy, X2 = PathMarkerInset + plotWidth, Y2 = gy });
            }

            // True angular separation between the path's own endpoints -- cos(dec)-compensated
            // (same convention as OrbitalTracking.cs's own tracking-rate math), unlike the plot
            // above, which independently stretches RA and Dec and so isn't a true angular shape.
            // Ported from OrbitalPathChart.vue's own endpointDriftDeg/driftSummary.
            if (path.Count >= 2) {
                var first = path[0];
                var last = path[^1];
                var dRaHours = last.raHours - first.raHours;
                while (dRaHours > 12) dRaHours -= 24;
                while (dRaHours < -12) dRaHours += 24;
                var dRaDeg = dRaHours * 15;
                var dDecDeg = last.decDeg - first.decDeg;
                var avgDecRad = (first.decDeg + last.decDeg) / 2 * Math.PI / 180;
                var totalDeg = Math.Sqrt(Math.Pow(dRaDeg * Math.Cos(avgDecRad), 2) + dDecDeg * dDecDeg);
                var nights = path.Count - 1;
                var perNightDeg = totalDeg / nights;
                pathDriftSummaryText = $"{FormatDeg(totalDeg)} total drift over {nights} nights (~{FormatDeg(perNightDeg)}/night)";

                // Angular scale bar, sized from the endpoints' own actual pixel distance
                // (so it reflects the plot's own effective scale) and placed on whichever
                // horizontal side has the most clearance from the WHOLE path, not just the start
                // point -- a curved path can swing close to either bottom corner somewhere other
                // than its very first point. Ported from OrbitalPathChart.vue's own scaleBar/
                // scaleBarSide.
                var firstPoint = points[0];
                var lastPoint = points[^1];
                var pxDistance = Math.Sqrt(Math.Pow(lastPoint.X - firstPoint.X, 2) + Math.Pow(lastPoint.Y - firstPoint.Y, 2));
                if (pxDistance >= 1 && totalDeg > 0) {
                    var degPerPx = totalDeg / pxDistance;
                    var maxBarPx = plotWidth * 0.4;
                    var chosen = PathNiceDegSteps[0];
                    foreach (var step in PathNiceDegSteps) {
                        var px = step.Deg / degPerPx;
                        if (px > maxBarPx) break;
                        chosen = step;
                    }
                    var barPx = chosen.Deg / degPerPx;
                    if (barPx >= 4) {
                        var barY = PathMarkerInset + plotHeight - 6;
                        double MinDistanceToBox(double boxX1, double boxX2) => points.Min(p => {
                            var dx = p.X < boxX1 ? boxX1 - p.X : p.X > boxX2 ? p.X - boxX2 : 0;
                            return Math.Sqrt(dx * dx + Math.Pow(p.Y - barY, 2));
                        });
                        var leftX1 = PathMarkerInset;
                        var rightX1 = PathMarkerInset + plotWidth - barPx;
                        var useLeft = MinDistanceToBox(leftX1, leftX1 + barPx) >= MinDistanceToBox(rightX1, rightX1 + barPx);
                        var barX1 = useLeft ? leftX1 : rightX1;
                        PathScaleBarInfo = new PathScaleBar {
                            X1 = barX1, X2 = barX1 + barPx, Y = barY,
                            TickTopY = barY - 3, TickBottomY = barY + 3, LabelY = barY - 14,
                            Label = chosen.Label,
                        };
                    }
                }
            }

            // Labels sit in their own row below the plot (see the view), not overlaid on the
            // canvas -- an overlaid label can collide with the line and dots whenever the
            // path's first/last point lands near a top corner.
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

        // --- Actions that drive hardware ---

        private Coordinates LoadedCoordinatesWithOffset() {
            var ra = raHours + offsetRaArcsec / 3600.0 / 15.0; // arcsec -> hours, cos(dec)-compensated rate already; offset is a plain positional nudge
            var dec = decDeg + offsetDecArcsec / 3600.0;
            return new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Hours);
        }

        /// <summary>Opens Perihelion's own popup Framing Composer -- a sky/FOV view plus
        /// Slew and Center, rotation (if a rotator is connected), and offset capture, all
        /// carried into Add to Sequence/Quick Track on confirm. Deliberately Perihelion's own
        /// popup, not a jump to NINA's own Framing Assistant tab (considered and explicitly
        /// rejected -- Perihelion needs its own window here, not a redirect). Same factory-
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
            var composerVm = new PerihelionFramingComposerVM(Loaded.Name, Loaded.ObjectType, trueCoordinates, telescopeMediator, rotatorMediator,
                cameraMediator, imagingMediator, filterWheelMediator, profileService, imageDataFactory, factory, skyMapAnnotator);
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

        /// <summary>The Options-page value (QuickTrackReapplyIntervalSeconds, default 900) --
        /// read fresh each time rather than cached, so it reflects a mid-session Options change.
        /// The same value both Quick Track's own reapply timer and PerihelionReapplyTrigger
        /// (Add to Sequence's own reapply) use directly -- there's no separate minutes-only
        /// variant anymore, since QuickTrackReapply's own timer now works in seconds too.</summary>
        public int ReapplyIntervalSeconds => PerihelionPlugin.Instance?.QuickTrackReapplyIntervalSeconds ?? 900;

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

        // The five fields below mirror Touch-N-Stars' own Track tab "Live Status" card
        // (PerihelionView.vue, backed by the same GET /perihelion/api/status shape as
        // QuickTrackStatus.Current here) -- same underlying data on both sides, this panel just
        // hadn't been reading the rest of the snapshot beyond target/rate/fallback. Each is null
        // (not empty string) when there's nothing to show, so the view's own visibility bindings
        // can collapse the line entirely rather than rendering blank space.

        private string? quickTrackElapsedText;
        public string? QuickTrackElapsedText {
            get => quickTrackElapsedText;
            private set { quickTrackElapsedText = value; RaisePropertyChanged(); }
        }

        private string? quickTrackAppliedAgoText;
        public string? QuickTrackAppliedAgoText {
            get => quickTrackAppliedAgoText;
            private set { quickTrackAppliedAgoText = value; RaisePropertyChanged(); }
        }

        private string? quickTrackNextReapplyText;
        public string? QuickTrackNextReapplyText {
            get => quickTrackNextReapplyText;
            private set { quickTrackNextReapplyText = value; RaisePropertyChanged(); }
        }

        private string? quickTrackLastErrorText;
        public string? QuickTrackLastErrorText {
            get => quickTrackLastErrorText;
            private set { quickTrackLastErrorText = value; RaisePropertyChanged(); }
        }

        private string? quickTrackGuidingErrorText;
        public string? QuickTrackGuidingErrorText {
            get => quickTrackGuidingErrorText;
            private set { quickTrackGuidingErrorText = value; RaisePropertyChanged(); }
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
                    AutoReapply ? ReapplyIntervalSeconds : null,
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

                QuickTrackElapsedText = s.StartedUtc is DateTime started
                    ? $"Tracking for {FormatDuration(DateTime.UtcNow - started)}"
                    : null;
                QuickTrackAppliedAgoText = s.LastAppliedUtc is DateTime lastApplied
                    ? $"Applied {FormatRelativeTime(lastApplied)}"
                    : null;
                QuickTrackNextReapplyText = s.AutoReapplySeconds is int secs && s.LastAppliedUtc is DateTime lastAppliedForReapply
                    ? FormatNextReapply(lastAppliedForReapply, secs)
                    : null;
                // Deliberately independent of each other, same as the Touch-N-Stars card --
                // LastError describes only the mount's own tracking-rate application, GuidingError
                // only the guider shift, and a Quick Track session where the mount succeeded but
                // guiding hiccuped should show the guiding line without implying tracking failed.
                QuickTrackLastErrorText = !s.LastApplySucceeded && s.LastError != null
                    ? $"Last attempt failed: {s.LastError}"
                    : null;
                QuickTrackGuidingErrorText = s.GuidingError != null
                    ? $"Guiding failed: {s.GuidingError}"
                    : null;
            } else {
                if (!string.IsNullOrEmpty(quickTrackStatusText) && s.StopReason != null) {
                    QuickTrackStatusText = $"Stopped: {s.StopReason}";
                }
                QuickTrackElapsedText = null;
                QuickTrackAppliedAgoText = null;
                QuickTrackNextReapplyText = null;
                QuickTrackLastErrorText = null;
                QuickTrackGuidingErrorText = null;
            }
        }

        /// <summary>Mirrors Touch-N-Stars' own formatDuration (PerihelionView.vue) exactly, so a
        /// given elapsed time reads the same on both frontends.</summary>
        private static string FormatDuration(TimeSpan span) {
            var seconds = Math.Max(0, span.TotalSeconds);
            if (seconds < 60) return $"{(int)Math.Round(seconds)}s";
            if (seconds < 3600) {
                var m = (int)(seconds / 60);
                var s = (int)Math.Round(seconds % 60);
                return s > 0 ? $"{m}m {s}s" : $"{m}m";
            }
            var hours = (int)(seconds / 3600);
            var minutes = (int)(seconds % 3600 / 60);
            return $"{hours}h {minutes:00}m";
        }

        /// <summary>Mirrors Touch-N-Stars' own relativeTime (PerihelionView.vue) exactly.</summary>
        private static string FormatRelativeTime(DateTime utc) {
            var seconds = Math.Max(0, (DateTime.UtcNow - utc).TotalSeconds);
            if (seconds < 60) return "just now";
            if (seconds < 3600) return $"{(int)(seconds / 60)}m ago";
            if (seconds < 86400) return $"{(int)(seconds / 3600)}h ago";
            return $"{(int)(seconds / 86400)}d ago";
        }

        private static string? FormatNextReapply(DateTime lastAppliedUtc, int autoReapplySeconds) {
            var nextAt = lastAppliedUtc.AddSeconds(autoReapplySeconds);
            var remaining = nextAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            // Always raw seconds, not FormatDuration's minutes/hours breakdown -- this counts
            // down within a bound the reapply interval itself sets, and that interval is always
            // seconds now (no /60 conversion anywhere else in this feature), so the countdown
            // shouldn't introduce one either. Matches Touch-N-Stars' own nextReapplyIn exactly.
            return $"Next re-apply in {Math.Max(0, (int)Math.Round(remaining.TotalSeconds))}s";
        }

        public override void Hide(object o) {
            base.Hide(o);
        }
    }
}
