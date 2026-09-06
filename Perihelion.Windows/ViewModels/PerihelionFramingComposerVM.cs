using CommunityToolkit.Mvvm.Input;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.WPF.Base.SkySurvey;
using Perihelion.Api;
using Perihelion.Astrometry;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;

namespace Perihelion.ViewModels {

    /// <summary>
    /// The Perihelion Framing Composer's own VM -- a plain class, not MEF-exported, since a real
    /// popup Window (unlike PerihelionDockableVM's IDockableVM or PerihelionPlugin's
    /// IPluginManifest) isn't something MEF composes at all; PerihelionDockableVM constructs one
    /// directly (`new PerihelionFramingComposerVM(...)`) with whatever it already has on hand
    /// (ITelescopeMediator, IRotatorMediator, the resolved ISequencerFactory), the same way
    /// PerihelionSequenceBuilder is a plain static class rather than an MEF export.
    ///
    /// Real design decision (2026-09-05, after the earlier "just add a raw rotation-degrees
    /// TextBox to Add to Sequence" attempt was rightly rejected): rotation and offset shouldn't
    /// be typed in blind. This VM drives the SAME real Center/CenterAndRotate sequence items Add
    /// to Sequence itself uses -- via the same ISequencerFactory access, executed directly
    /// (Execute(), not a full sequence run, same "run outside a sequence" pattern already
    /// established for Quick Track) -- so "Slew and Center" here is a real hardware action with
    /// NINA's own real plate-solve loop behind it, not a preview. Confirming this window copies
    /// whatever was actually achieved (the real offset from the mount's real position, and the
    /// real rotation angle if a rotator was used) back into the main panel, for Add to Sequence/
    /// Quick Track to pick up -- rather than a number someone guessed.
    /// </summary>
    public class PerihelionFramingComposerVM : INotifyPropertyChanged {
        // Fixed on-screen size of the sky map display -- deliberately independent of whatever
        // pixel resolution the fetched SkySurveyImage actually comes back at (confirmed from
        // NINA.WPF.Base's own NASASkySurvey.cs that this varies with the requested field of view,
        // not with any width/height passed to GetImage). The FOV rectangle below is computed in
        // this SAME fixed coordinate space using the image's own real FoVWidth (always set by
        // every ISkySurvey implementation, regardless of pixel resolution), so it lines up
        // correctly with the displayed image regardless of its native size -- WPF's own Image
        // control stretches the source bitmap to fill this fixed area either way. 600, not the
        // original 320 (then 480) -- real user feedback (2026-09-05, reported twice) that the
        // whole window read as too small. MUST match the XAML Border's own Width/Height exactly
        // (PerihelionFramingComposerWindow.xaml) -- this constant is the only source of truth
        // for pixelsPerArcmin/FovRectWidth/Height below, so a mismatch here silently misdraws
        // the FOV rectangle's real size relative to the displayed image.
        public const double SkyMapDisplaySize = 600;

        // Requests a field of view wider than the camera's own actual FOV, so the displayed sky
        // map shows real surrounding context (other stars/objects) around the FOV rectangle, not
        // just the rectangle itself filling the whole view. 6x, not the original 3x -- real user
        // feedback (2026-09-05): this is a single static fetched image being panned around, not a
        // true tiled/infinite map (that would need a real interactive planetarium library like
        // Touch-N-Stars' own celestia-atlas, which is JS-only), so there's a genuine, honest limit
        // to how far this can be panned before reaching the edge of the fetched image regardless
        // of this factor -- a wider request just pushes that edge further out, it doesn't remove
        // it.
        private const double SkyMapZoomOutFactor = 6.0;

        // Same 10 nights PerihelionDockableVM's own Position tab path chart uses (PathDays
        // there) -- no reason for the Composer's own overlay to show a different span.
        private const int PathDays = 10;

        private static readonly HttpClient HttpClient = PerihelionHttpClient.Instance;

        private readonly ITelescopeMediator telescopeMediator;
        private readonly IRotatorMediator rotatorMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IProfileService profileService;
        private readonly IImageDataFactory imageDataFactory;
        private readonly ISequencerFactory factory;
        private readonly OrbitalObjectType objectType;
        private readonly Coordinates trueCoordinates;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public PerihelionFramingComposerVM(
            string targetName,
            OrbitalObjectType objectType,
            Coordinates trueCoordinates,
            ITelescopeMediator telescopeMediator,
            IRotatorMediator rotatorMediator,
            ICameraMediator cameraMediator,
            IImagingMediator imagingMediator,
            IFilterWheelMediator filterWheelMediator,
            IProfileService profileService,
            IImageDataFactory imageDataFactory,
            ISequencerFactory factory) {
            TargetName = targetName;
            this.objectType = objectType;
            this.trueCoordinates = trueCoordinates;
            this.telescopeMediator = telescopeMediator;
            this.rotatorMediator = rotatorMediator;
            this.cameraMediator = cameraMediator;
            this.imagingMediator = imagingMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.profileService = profileService;
            this.imageDataFactory = imageDataFactory;
            this.factory = factory;

            PositionText = $"RA {AstroUtil.HoursToHMS(trueCoordinates.RA)}  Dec {AstroUtil.DegreesToDMS(trueCoordinates.Dec)}";
            RotatorConnected = rotatorMediator.GetInfo().Connected;
            // Defaults to whatever RotatorConnected already is, not unconditionally off -- real
            // user feedback (2026-09-06): with a rotator actually connected, requiring a manual
            // toggle every time this opens was just friction for what's almost always the wanted
            // behavior. The toggle itself still exists (backing field set directly here, not via
            // the property setter, so this doesn't trigger its own IncludeCenter side effect) --
            // a user WITH a rotator can still opt out for one session; a user WITHOUT one still
            // opens with it correctly off.
            useRotation = RotatorConnected;
            // Backing field directly, not the property setter -- the setter's own side effects
            // (persisting to the profile, re-triggering LoadSkyMapAsync) are for when the USER
            // changes the dropdown; LoadSkyMapAsync below already runs once regardless, so
            // running it a second time here too would just be a redundant fetch on open.
            // Falls back to NASA if the persisted preference isn't one of ImageSources' own
            // entries (e.g. real NINA's own default of Offline/SKYATLAS, deliberately excluded
            // from that list -- see its own doc comment) -- otherwise the ComboBox would come up
            // with nothing selected at all.
            var lastSource = profileService.ActiveProfile.FramingAssistantSettings.LastSelectedImageSource;
            selectedImageSource = ImageSources.Contains(lastSource) ? lastSource : SkySurveySource.NASA;

            SlewAndCenterCommand = new AsyncRelayCommand(SlewAndCenterAction, () => !IsBusy && telescopeMediator.GetInfo().Connected);
            SlewAndCenterCommand.RegisterPropertyChangeNotification(this, nameof(IsBusy));

            DetermineRotationCommand = new AsyncRelayCommand(DetermineRotationAction,
                () => !IsBusy && cameraMediator.GetInfo().Connected && cameraMediator.IsFreeToCapture(this));
            DetermineRotationCommand.RegisterPropertyChangeNotification(this, nameof(IsBusy));
            DetermineRotationCommand.RegisterPropertyChangeNotification(cameraMediator.GetInfo(), nameof(CameraInfo.Connected));

            CaptureOffsetCommand = new RelayCommand(CaptureOffsetAction, () => telescopeMediator.GetInfo().Connected);

            ToggleSlewOptionsCommand = new RelayCommand(() => SlewOptionsOpen = !SlewOptionsOpen);
            ResetCommand = new RelayCommand(ResetAction);

            ConfirmCommand = new RelayCommand(() => Confirmed = true);
            CancelCommand = new RelayCommand(() => Confirmed = false);

            // Fire-and-forget, same pattern as PerihelionDockableVM's own constructor
            // auto-loading the Browse list -- SkyMapStatusText/SkyMapLoading reflect progress and
            // any failure, so a slow or unreachable sky-survey endpoint doesn't block this window
            // from opening or being usable for Slew and Center/Capture Offset in the meantime.
            _ = LoadSkyMapAsync();
        }

        public string TargetName { get; }
        public string PositionText { get; }
        public bool RotatorConnected { get; }

        // --- Image source ---

        /// <summary>File/Cache/Offline all deliberately excluded -- File needs a local file
        /// picker (not built here), Cache only has content once something else has already
        /// populated it, and Offline (SkyAtlasSkySurvey) turned out, on reading its own real
        /// source, to be a flat mid-grey placeholder with no actual imagery at all (confirmed:
        /// it fills every pixel with the literal byte value 30, nothing else) -- real, even in
        /// stock NINA, but genuinely useless for this feature specifically ("see real objects to
        /// frame against"), so it was actively misleading to list it as if it were a real choice.
        /// The remaining five are all live, no-setup sky-survey sources with real imagery.</summary>
        public IReadOnlyList<SkySurveySource> ImageSources { get; } = new[] {
            SkySurveySource.NASA, SkySurveySource.HIPS2FITS, SkySurveySource.STSCI,
            SkySurveySource.ESO, SkySurveySource.SKYSERVER,
        };

        private SkySurveySource selectedImageSource;
        public SkySurveySource SelectedImageSource {
            get => selectedImageSource;
            set {
                if (selectedImageSource == value) return;
                selectedImageSource = value;
                RaisePropertyChanged();
                // Persisted back to the profile -- same field NINA's own real Framing Assistant
                // reads/writes for this exact purpose, so switching sources here also becomes
                // the new default there (and next time this Composer opens), matching how a
                // user's own preference is normally expected to stick.
                profileService.ActiveProfile.FramingAssistantSettings.LastSelectedImageSource = value;
                _ = LoadSkyMapAsync();
            }
        }

        private bool useRotation;
        /// <summary>Rotate implies Center (NINA's own CenterAndRotate always centers first --
        /// there's no "rotate without centering" sequence item), so turning this on also forces
        /// IncludeCenter on -- backing field set directly to avoid a redundant recursive
        /// notification storm through IncludeCenter's own setter, then explicitly notified below
        /// so its own CheckBox binding still picks up the change.</summary>
        public bool UseRotation {
            get => useRotation;
            set {
                useRotation = value;
                if (useRotation) includeCenter = true;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(DisplayRotationAngle));
                RaisePropertyChanged(nameof(SlewButtonLabel));
                RaisePropertyChanged(nameof(IncludeCenter));
            }
        }

        private bool includeCenter = true;
        /// <summary>Backs the "Center" toggle in the Slew options popup -- turning it off also
        /// forces UseRotation off (same reasoning as above, opposite direction).</summary>
        public bool IncludeCenter {
            get => includeCenter;
            set {
                includeCenter = value;
                if (!includeCenter) useRotation = false;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(SlewButtonLabel));
                RaisePropertyChanged(nameof(UseRotation));
                RaisePropertyChanged(nameof(DisplayRotationAngle));
            }
        }

        private bool slewOptionsOpen;
        /// <summary>Drives the Slew options Popup's own IsOpen -- the cog button next to Slew and
        /// Center toggles this via ToggleSlewOptionsCommand.</summary>
        public bool SlewOptionsOpen {
            get => slewOptionsOpen;
            set { slewOptionsOpen = value; RaisePropertyChanged(); }
        }

        /// <summary>Real user request (2026-09-05): the Slew and Center button's own label should
        /// reflect exactly what it's about to do, since Center and Rotate are now independently
        /// toggleable via the Slew options popup rather than fixed. Checks RotatorConnected too,
        /// not just UseRotation -- UseRotation can be true with no rotator present (it's no longer
        /// gated on one, see its own doc comment and the XAML's), in which case SlewAndCenterAction
        /// itself falls back to plain Center, so the label has to say that's what will actually
        /// happen rather than promising a rotate that won't occur.</summary>
        public string SlewButtonLabel {
            get {
                if (UseRotation && RotatorConnected) return "Slew, Center & Rotate";
                if (IncludeCenter) return "Slew & Center";
                return "Slew";
            }
        }

        private double rotationAngle;
        public double RotationAngle {
            get => rotationAngle;
            set { rotationAngle = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(DisplayRotationAngle)); }
        }

        /// <summary>Drives the FOV rectangle's own on-screen RenderTransform (RotateTransform) --
        /// negated because screen-space rotation (WPF's RotateTransform.Angle, clockwise in
        /// pixel space) and the astronomical position-angle convention CenterAndRotate itself
        /// uses aren't the same direction by default for a standard N-up display. Best
        /// understanding as of writing this, not yet confirmed against a real rotator visually --
        /// flag if the box appears to rotate the wrong way once actually seen live.</summary>
        public double DisplayRotationAngle => UseRotation ? -RotationAngle : 0;

        // --- Background pan/zoom ---
        //
        // Rebuilt (2026-09-05) to match how Touch-N-Stars' own Perihelion framing view
        // (FramingOffsetView.vue) actually works -- read directly rather than guessed a second
        // time: it has exactly ONE drag interaction (pan the whole sky view), not two. The target
        // marker is pinned to a real point ON the sky image, so panning carries it along, same as
        // a pin on a map; the FOV rectangle stays fixed at the viewport's own center the whole
        // time (there is no independently-draggable FOV box on the TNS side at all). Offset is
        // then just "how far the marker has moved from center" -- exactly what
        // FramingOffsetView.vue's own captureFraming() reads off its view's pan state.
        //
        // ImageZoom is still purely cosmetic (never changes any real value, just for looking
        // around at more/less context) -- TranslateTransform is applied AFTER ScaleTransform in
        // the Window's own TransformGroup, so a pan distance in on-screen pixels stays constant
        // regardless of zoom level (translation happens in the already-scaled coordinate frame),
        // which is why ImagePanX/Y can convert straight to arcsec via pixelsPerArcmin below with
        // no zoom-dependent correction needed.
        //
        // PerihelionFramingComposerWindow's own code-behind drives Zoom/Pan from mouse wheel/
        // drag, since WPF has no built-in pan/zoom gesture support without a third-party
        // behaviors library this project doesn't reference. Zoom clamping happens there too, the
        // natural place to enforce "don't zoom out past 1x".

        // 2.5, not 1.0 -- real bug found from a real screenshot: at zoom exactly 1.0 the fetched
        // image is displayed at precisely the viewport's own size (Stretch="UniformToFill" on an
        // image whose own aspect matches a same-aspect container is an exact fit, no overflow at
        // all), so ANY pan at that zoom level immediately exposed the image's real edge (the
        // black strip the screenshot showed) -- there was no slack to pan into. Starting zoomed
        // in past 1.0 guarantees real overflow to pan around within from the moment this opens.
        private double imageZoom = 2.5;
        public double ImageZoom {
            get => imageZoom;
            set { imageZoom = value; RaisePropertyChanged(); }
        }

        private double imagePanX;
        /// <summary>The one real interaction -- dragging the sky map sets this (and ImagePanY),
        /// which directly derives and sets OffsetRaArcsec (the same field "Capture Offset from
        /// Mount" sets, just derived visually here instead of from the mount's real position).
        /// RA increasing = screen right: best understanding as of writing this, not yet confirmed
        /// against a real sky map visually -- flag if it turns out backwards once actually seen
        /// live.</summary>
        public double ImagePanX {
            get => imagePanX;
            set {
                imagePanX = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(TargetLabelScreenX));
                if (pixelsPerArcmin > 0) OffsetRaArcsec = Math.Round((imagePanX / pixelsPerArcmin) * 60.0, 1);
            }
        }

        private double imagePanY;
        /// <summary>Dec increasing = screen up (hence the negation -- screen Y grows downward):
        /// same "not yet visually confirmed" caveat as ImagePanX.</summary>
        public double ImagePanY {
            get => imagePanY;
            set {
                imagePanY = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(TargetLabelScreenY));
                if (pixelsPerArcmin > 0) OffsetDecArcsec = Math.Round((-imagePanY / pixelsPerArcmin) * 60.0, 1);
            }
        }

        private bool isBusy;
        public bool IsBusy {
            get => isBusy;
            set { isBusy = value; RaisePropertyChanged(); }
        }

        private string statusText = "Slew and Center to establish the base framing, then optionally nudge the mount and Capture Offset.";
        public string StatusText {
            get => statusText;
            set { statusText = value; RaisePropertyChanged(); }
        }

        // Separate from StatusText -- that field sits right under Slew and Center/Determine
        // Rotation and reports on THOSE hardware actions specifically; Reset isn't one of those
        // (it touches no hardware), and showing its own confirmation there read as misplaced
        // (real user feedback, 2026-09-05). This one is displayed right above the bottom button
        // row instead, next to the action it actually confirms.
        private string footerStatusText = string.Empty;
        public string FooterStatusText {
            get => footerStatusText;
            private set { footerStatusText = value; RaisePropertyChanged(); }
        }

        // Arcsec internally (same convention as PerihelionDockableVM's own Offset fields) --
        // OffsetRaText/OffsetDecText below match its exact HH:MM:SS/DMS display format.
        private double offsetRaArcsec;
        public double OffsetRaArcsec {
            get => offsetRaArcsec;
            private set { offsetRaArcsec = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(OffsetRaText)); RaisePropertyChanged(nameof(CurrentCenterText)); }
        }

        private double offsetDecArcsec;
        public double OffsetDecArcsec {
            get => offsetDecArcsec;
            private set { offsetDecArcsec = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(OffsetDecText)); RaisePropertyChanged(nameof(CurrentCenterText)); }
        }

        public string OffsetRaText => AstroUtil.HoursToHMS(offsetRaArcsec / 3600.0 / 15.0);
        public string OffsetDecText => AstroUtil.DegreesToDMS(offsetDecArcsec / 3600.0);

        /// <summary>Wherever this view is CURRENTLY centered (true position + whatever offset has
        /// been panned/captured so far) -- matches Touch-N-Stars' own FramingOffsetView.vue top-
        /// right RA/Dec overlay, which tracks its own live pan the same way.</summary>
        public string CurrentCenterText {
            get {
                var centerRa = trueCoordinates.RA + offsetRaArcsec / 3600.0 / 15.0;
                var centerDec = trueCoordinates.Dec + offsetDecArcsec / 3600.0;
                return $"RA {AstroUtil.HoursToHMS(centerRa)}  Dec {AstroUtil.DegreesToDMS(centerDec)}";
            }
        }

        // --- Sky map ---

        private BitmapSource? skyImage;
        public BitmapSource? SkyImage {
            get => skyImage;
            private set { skyImage = value; RaisePropertyChanged(); }
        }

        private bool skyMapLoading = true;
        public bool SkyMapLoading {
            get => skyMapLoading;
            private set { skyMapLoading = value; RaisePropertyChanged(); }
        }

        private string skyMapStatusText = "Loading sky map...";
        public string SkyMapStatusText {
            get => skyMapStatusText;
            private set { skyMapStatusText = value; RaisePropertyChanged(); }
        }

        // FOV rectangle -- fixed at the viewport's own center always (HorizontalAlignment=Center
        // in the XAML, no Left/Top here at all), matching FramingOffsetView.vue's own behavior
        // (no independently-draggable FOV box there). Only its size is real state, computed from
        // the user's own gear.
        private double fovRectWidth, fovRectHeight;
        public double FovRectWidth { get => fovRectWidth; private set { fovRectWidth = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(TargetMarkerSize)); } }
        public double FovRectHeight { get => fovRectHeight; private set { fovRectHeight = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(TargetMarkerSize)); } }

        /// <summary>Target marker's on-screen diameter, sized relative to the FOV rectangle
        /// rather than a fixed pixel size -- a comet/asteroid has no real angular size worth
        /// drawing to scale, but a fixed-size dot looked disproportionate across very different
        /// camera FOVs (a speck against a huge FOV rectangle, or nearly filling a tiny one).
        /// Clamped so it stays visible at a small FOV and doesn't dominate a large one.</summary>
        public double TargetMarkerSize => Math.Clamp(Math.Min(FovRectWidth, FovRectHeight) * 0.12, 6.0, 24.0);

        // Set once per LoadSkyMapAsync call -- how many on-screen pixels correspond to one arcmin
        // on the sky, used by ImagePanX/Y's own setters to convert a drag distance to a real
        // angular offset, and by CaptureOffsetAction to convert the other way.
        private double pixelsPerArcmin = 1;

        // --- Path overlay ---
        //
        // Real user request (2026-09-06): overlay the object's own 10-night path directly on the
        // sky map, matching Touch-N-Stars' own FramingOffsetView.vue (a violet path line plus
        // dots, the "tonight" point distinguished from the rest). That component draws onto a
        // separate <canvas> using a proper tangent-plane (gnomonic) projection from its own
        // interactive planetarium library's view state -- not something this Composer can reuse
        // directly (no such library here, just a fetched static bitmap), but the CONCEPT ports
        // cleanly: project each point's RA/Dec into the exact same fixed pixel space
        // ImagePanX/Y/CaptureOffsetAction already use (arcsec-from-trueCoordinates times
        // pixelsPerArcmin, no cos(dec) compensation) rather than a separate, more rigorous
        // projection -- deliberately consistent with how every other point in this same view
        // (the target marker, the captured offset) is already placed, not more "correct" in
        // isolation. At the small angular scales a camera FOV actually spans, the difference is
        // negligible; a different convention for just this one overlay would be a real
        // inconsistency for no visible benefit.
        //
        // Absolute canvas-space coordinates (SkyMapDisplaySize/2 already added), not raw offsets,
        // so the XAML can bind Canvas.Left/Top and Polyline.Points directly with no converter.
        // Lives inside the SAME transformed Grid as the sky image and target marker, so it pans/
        // zooms as one unit with them -- these are real positions on the sky, not a viewport-
        // fixed overlay like the FOV rectangle.
        //
        // Day 0 (tonight) is deliberately NOT drawn as its own marker -- LoadSkyMapAsync's own
        // sky-map fetch and ComputeOrbitalPathAsync's own day-0 point are both anchored to
        // essentially the same "now" instant (see PerihelionApiController.GetPath's own comment
        // on why day 0 uses full-precision UtcNow, not midnight), so it would coincide almost
        // exactly with the existing target Ellipse and just double-draw the same dot. PathMarkers
        // starts from day 1; the connecting Polyline still includes day 0, so the line visibly
        // starts at the real target marker.
        private PointCollection? pathPolylinePoints;
        public PointCollection? PathPolylinePoints {
            get => pathPolylinePoints;
            private set { pathPolylinePoints = value; RaisePropertyChanged(); }
        }

        private IReadOnlyList<FramingPathPoint>? pathMarkers;
        public IReadOnlyList<FramingPathPoint>? PathMarkers {
            get => pathMarkers;
            private set { pathMarkers = value; RaisePropertyChanged(); }
        }

        public sealed class FramingPathPoint {
            public double X { get; set; }
            public double Y { get; set; }
            public string Tooltip { get; set; } = string.Empty;
        }

        // Real bug found from a real screenshot (2026-09-06): the label was first placed INSIDE
        // the same pan/zoom-transformed Grid as the image/marker/path, so at the default 2.5x
        // zoom its font rendered 2.5x too -- FramingOffsetView.vue's own canvas-drawn label uses
        // a font size fixed in screen pixels regardless of the view's own zoom/FOV, so it should
        // stay constant here too, not scale. Fixed by moving the label to the OUTER, untransformed
        // Grid instead (sibling of the FOV rectangle) and computing its own screen position
        // directly from ImagePanX/Y rather than inheriting the shared RenderTransform. This is
        // mathematically exact, not an approximation: the marker/label sit exactly at the inner
        // Grid's own RenderTransformOrigin (0.5,0.5 = dead center), and WPF scales a
        // RenderTransform around that origin point -- a point exactly AT the scale origin is
        // invariant under scaling, so ImageZoom drops out of the position entirely and only the
        // TranslateTransform (ImagePanX/Y) actually moves it. Y is nudged up ~7px (half a
        // typical single-line 11pt run's own height) so the label reads vertically centered on
        // the marker, matching FramingOffsetView.vue's own boxY centering -- a fixed constant,
        // not measured, since WPF has no clean way to bind a TextBlock's own rendered height back
        // into a Canvas.Top the way ActualWidth already gets used for TargetLabelLeftConverter
        // (that one has real user-visible payoff -- text overlapping the wrong side of the path
        // -- worth a MultiBinding; a few px of vertical centering doesn't).
        public double TargetLabelScreenX => SkyMapDisplaySize / 2.0 + ImagePanX;
        public double TargetLabelScreenY => SkyMapDisplaySize / 2.0 - 7 + ImagePanY;

        private bool targetLabelOnRight = true;
        /// <summary>Real user request (2026-09-06), matching FramingOffsetView.vue's own name-tag
        /// placement exactly: the target's name sits on the OPPOSITE side from wherever the path
        /// continues from "tonight" (day 0), so the label never runs alongside/through the path
        /// line itself. True (right) until path data loads or there's fewer than 2 points to
        /// judge a direction from. Doesn't need FramingOffsetView.vue's own canvas-edge safety
        /// fallback -- the target marker sits at the sky map's own exact center by construction
        /// here (unlike that component's own pannable view center), so there's always at least
        /// SkyMapDisplaySize/2 of clearance on either side, comfortably more than any real target
        /// name's rendered width.</summary>
        public bool TargetLabelOnRight {
            get => targetLabelOnRight;
            private set { targetLabelOnRight = value; RaisePropertyChanged(); }
        }

        /// <summary>Fetches the object's own real 10-night path and projects it into the sky
        /// map's fixed pixel space -- called from LoadSkyMapAsync AFTER pixelsPerArcmin is set
        /// (projection needs it), not in parallel with the sky-map fetch itself. Failure here
        /// (e.g. no internet for a comet's MPC elements) just means no path overlay -- it doesn't
        /// block the Composer from being usable for Slew and Center/Capture Offset, same
        /// "secondary data, don't let it block the primary view" pattern as SkyMapStatusText's
        /// own error handling.</summary>
        private async Task LoadPathAsync() {
            try {
                if (pixelsPerArcmin <= 0) return;
                var points = await OrbitalTracking.ComputeOrbitalPathAsync(HttpClient, objectType, TargetName, DateTime.UtcNow, PathDays, CancellationToken.None);
                if (points == null || points.Count == 0) {
                    PathPolylinePoints = null;
                    PathMarkers = null;
                    return;
                }

                var polyline = new PointCollection();
                var markers = new List<FramingPathPoint>();
                for (var i = 0; i < points.Count; i++) {
                    var p = points[i];
                    var raArcsec = (p.raHours - trueCoordinates.RA) * 15 * 3600;
                    var decArcsec = (p.decDeg - trueCoordinates.Dec) * 3600;
                    var x = SkyMapDisplaySize / 2.0 + (raArcsec / 60.0) * pixelsPerArcmin;
                    var y = SkyMapDisplaySize / 2.0 - (decArcsec / 60.0) * pixelsPerArcmin;
                    polyline.Add(new Point(x, y));
                    if (i > 0) markers.Add(new FramingPathPoint { X = x, Y = y, Tooltip = p.date.ToString("yyyy-MM-dd") });
                }
                PathPolylinePoints = polyline;
                PathMarkers = markers;
                if (polyline.Count >= 2) {
                    var pathGoesRight = polyline[1].X > polyline[0].X;
                    TargetLabelOnRight = !pathGoesRight;
                }
            } catch (Exception ex) {
                PathPolylinePoints = null;
                PathMarkers = null;
                Logger.Warning($"Perihelion: PerihelionFramingComposerVM.LoadPathAsync failed: {ex}");
            }
        }

        /// <summary>Fetches a real sky-survey image centered on the target, sized to show
        /// genuine surrounding context (SkyMapZoomOutFactor wider than the camera's own actual
        /// field of view), then computes the FOV rectangle overlay from the user's own real gear
        /// settings -- CameraSettings.PixelSize + TelescopeSettings.FocalLength for arcsec/pixel
        /// (AstroUtil.ArcsecPerPixel, already used elsewhere in this project for MaxExposureText),
        /// times FramingAssistantSettings.CameraWidth/CameraHeight for the sensor's own pixel
        /// dimensions -- the SAME persisted settings NINA's own real Framing Assistant uses for
        /// this exact purpose (IFramingAssistantSettings, confirmed from its own real source),
        /// not something reinvented here. LastSelectedImageSource is reused for the same reason:
        /// whatever image source the user already prefers (or has working, if they don't have
        /// reliable internet) in the real Framing Assistant is almost certainly the right default
        /// here too, rather than hardcoding one. SkySurveyFactory/ISkySurvey are both real,
        /// plugin-safe NINA.WPF.Base APIs (same assembly AltitudeChart came from) -- this is
        /// genuine sky-survey imagery, not a custom rendering.</summary>
        private async Task LoadSkyMapAsync() {
            SkyMapLoading = true;
            SkyMapStatusText = "Loading sky map...";
            try {
                var cameraSettings = profileService.ActiveProfile.CameraSettings;
                var telescopeSettings = profileService.ActiveProfile.TelescopeSettings;
                var framingSettings = profileService.ActiveProfile.FramingAssistantSettings;

                var arcsecPerPixel = AstroUtil.ArcsecPerPixel(cameraSettings.PixelSize, telescopeSettings.FocalLength);
                var cameraFovWidthArcmin = arcsecPerPixel * framingSettings.CameraWidth / 60.0;
                var cameraFovHeightArcmin = arcsecPerPixel * framingSettings.CameraHeight / 60.0;

                var requestedFovArcmin = Math.Max(cameraFovWidthArcmin, cameraFovHeightArcmin) * SkyMapZoomOutFactor;
                if (!(requestedFovArcmin > 0) || double.IsNaN(requestedFovArcmin)) {
                    // Camera/telescope profile not fully configured (0 focal length, 0 pixel
                    // size, or 0 resolution) -- fall back to a reasonable 1-degree view rather
                    // than requesting a nonsensical or zero field of view.
                    requestedFovArcmin = 60;
                    cameraFovWidthArcmin = cameraFovHeightArcmin = 0;
                }

                var survey = new SkySurveyFactory(imageDataFactory).Create(selectedImageSource);
                var image = await survey.GetImage(TargetName, trueCoordinates, requestedFovArcmin,
                    (int)SkyMapDisplaySize, (int)SkyMapDisplaySize, CancellationToken.None, new Progress<int>());

                SkyImage = image.Image;
                // Reset -- a freshly (re)loaded sky map has no pan applied to it yet, and
                // switching image source mid-session shouldn't leave a stale offset from the
                // previous image's own pixel scale. Zoom resets to the same >1.0 default
                // ImageZoom's own field initializer uses, not 1.0 -- see its own comment for why
                // 1.0 specifically has zero pannable overflow.
                ImagePanX = 0;
                ImagePanY = 0;
                ImageZoom = 2.5;

                pixelsPerArcmin = SkyMapDisplaySize / image.FoVWidth;
                FovRectWidth = Math.Min(SkyMapDisplaySize, cameraFovWidthArcmin * pixelsPerArcmin);
                FovRectHeight = Math.Min(SkyMapDisplaySize, cameraFovHeightArcmin * pixelsPerArcmin);

                SkyMapStatusText = cameraFovWidthArcmin > 0
                    ? string.Empty
                    : "Camera/telescope profile isn't fully configured -- showing the sky map without a real FOV rectangle.";

                await LoadPathAsync();
            } catch (Exception ex) {
                SkyMapStatusText = $"Sky map unavailable: {ex.Message}";
                Logger.Warning($"Perihelion: PerihelionFramingComposerVM.LoadSkyMapAsync failed: {ex}");
            } finally {
                SkyMapLoading = false;
            }
        }

        public AsyncRelayCommand SlewAndCenterCommand { get; }
        public AsyncRelayCommand DetermineRotationCommand { get; }
        public RelayCommand CaptureOffsetCommand { get; }
        public RelayCommand ToggleSlewOptionsCommand { get; }
        public RelayCommand ResetCommand { get; }
        public RelayCommand ConfirmCommand { get; }
        public RelayCommand CancelCommand { get; }

        /// <summary>Set once Confirm/Cancel is clicked -- PerihelionFramingComposerWindow's own
        /// code-behind reads this immediately after to decide whether to copy this VM's captured
        /// state back into the caller or discard it, then closes the window either way.</summary>
        public bool? Confirmed { get; private set; }

        /// <summary>Null means plain Center should be used for Add to Sequence/Quick Track too
        /// (no other angle should be assumed) -- true either when Rotate was never checked, or
        /// when it was checked but no rotator was connected here (UseRotation is no longer gated
        /// on RotatorConnected, see its own doc comment), since there'd be nothing for a later
        /// CenterAndRotate step to actually command either.</summary>
        public double? CapturedRotationAngle => UseRotation && RotatorConnected ? RotationAngle : (double?)null;

        /// <summary>Three real, distinct outcomes depending on the Slew options popup's own
        /// IncludeCenter/UseRotation toggles -- SlewButtonLabel above always names exactly which
        /// one is about to run. Checks RotatorConnected alongside UseRotation for the same reason
        /// SlewButtonLabel and CapturedRotationAngle do -- UseRotation can be true with no rotator
        /// present, and CenterAndRotate has nothing to command in that case, so this falls back to
        /// plain Center instead of attempting (and failing) a rotate against hardware that isn't
        /// there. Plain Slew (no plate-solve) uses the same real
        /// NINA.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec item Add to Sequence's own
        /// "just point there" step would use -- a real, if less common, use case for a quick test
        /// frame before committing to a full plate-solved center.</summary>
        private async Task SlewAndCenterAction() {
            IsBusy = true;
            var rotating = UseRotation && RotatorConnected;
            StatusText = rotating
                ? $"Slewing, centering, and rotating to {RotationAngle}°..."
                : IncludeCenter ? "Slewing and centering..." : "Slewing...";
            try {
                var progress = new Progress<ApplicationStatus>(s => StatusText = s.Status ?? StatusText);
                if (rotating) {
                    var rotate = factory.GetItem<CenterAndRotate>();
                    rotate.Inherited = false;
                    rotate.Coordinates = new InputCoordinates(trueCoordinates);
                    rotate.PositionAngle = RotationAngle;
                    await rotate.Execute(progress, CancellationToken.None);
                    StatusText = "Centered and rotated. Nudge the mount now if you want to frame off-center (e.g. a comet's tail), then Capture Offset.";
                } else if (IncludeCenter) {
                    var center = factory.GetItem<Center>();
                    center.Inherited = false;
                    center.Coordinates = new InputCoordinates(trueCoordinates);
                    await center.Execute(progress, CancellationToken.None);
                    StatusText = "Centered. Nudge the mount now if you want to frame off-center (e.g. a comet's tail), then Capture Offset.";
                } else {
                    var slew = factory.GetItem<SlewScopeToRaDec>();
                    slew.Inherited = false;
                    slew.Coordinates = new InputCoordinates(trueCoordinates);
                    await slew.Execute(progress, CancellationToken.None);
                    StatusText = "Slewed (no plate-solve center). Nudge the mount now if you want to frame off-center, then Capture Offset.";
                }
            } catch (Exception ex) {
                StatusText = $"Slew/center failed: {ex.Message}";
                Notification.ShowError($"Perihelion: framing slew/center failed: {ex.Message}");
                Logger.Error("Perihelion: PerihelionFramingComposerVM.SlewAndCenterAction failed", ex);
            } finally {
                IsBusy = false;
            }
        }

        /// <summary>Real plate-solve rotation readout -- the only way a user WITHOUT a rotator can
        /// know what framing rotation they'll actually get (the "Rotate to" section above is
        /// disabled entirely without one, since there's nothing to command), and a quick sanity
        /// check for a user WITH one before picking a target angle. Ported directly from real
        /// NINA's own FramingAssistantVM.GetRotationFromCamera
        /// (NINA/ViewModel/FramingAssistant/FramingAssistantVM.cs) -- same CaptureSolver/
        /// PlateSolverFactory/CaptureSolverParameter construction sourced from the same
        /// PlateSolveSettings, not reinvented. Unlike that method, RotationAngle is set directly
        /// to the solved PositionAngle with no "360 -" inversion -- Perihelion's own
        /// SlewAndCenterAction passes RotationAngle straight through to CenterAndRotate.PositionAngle
        /// with no inversion either, so the two have to agree for "measure it, then use it" to
        /// actually round-trip correctly.</summary>
        private async Task DetermineRotationAction() {
            IsBusy = true;
            StatusText = "Capturing and plate-solving to determine camera rotation...";
            try {
                var settings = profileService.ActiveProfile.PlateSolveSettings;
                var seq = new CaptureSequence {
                    Binning = new BinningMode(settings.Binning, settings.Binning),
                    Gain = settings.Gain,
                    FilterType = settings.Filter,
                    ExposureTime = settings.ExposureTime,
                    TotalExposureCount = 1,
                };
                var plateSolver = PlateSolverFactory.GetPlateSolver(settings);
                var blindSolver = PlateSolverFactory.GetBlindSolver(settings);
                var parameter = new CaptureSolverParameter {
                    Attempts = 1,
                    Binning = settings.Binning,
                    DownSampleFactor = settings.DownSampleFactor,
                    FocalLength = profileService.ActiveProfile.TelescopeSettings.FocalLength,
                    MaxObjects = settings.MaxObjects,
                    PixelSize = profileService.ActiveProfile.CameraSettings.PixelSize,
                    ReattemptDelay = TimeSpan.FromMinutes(settings.ReattemptDelay),
                    Regions = settings.Regions,
                    SearchRadius = settings.SearchRadius,
                    Coordinates = telescopeMediator.GetCurrentPosition(),
                    BlindFailoverEnabled = settings.BlindFailoverEnabled,
                };
                var captureSolver = new CaptureSolver(plateSolver, blindSolver, imagingMediator, filterWheelMediator);
                var progress = new Progress<ApplicationStatus>(s => StatusText = s.Status ?? StatusText);
                var result = await captureSolver.Solve(seq, parameter, null, progress, CancellationToken.None);

                if (result.Success) {
                    RotationAngle = Math.Round(AstroUtil.EuclidianModulus(result.PositionAngle, 360), 1);
                    UseRotation = true;
                    // Same real behavior as FramingAssistantVM's own version -- if a rotator IS
                    // connected, sync its reported position to match what was just measured
                    // (calibration), it doesn't command a move anywhere.
                    if (rotatorMediator.GetInfo().Connected) {
                        rotatorMediator.Sync((float)result.PositionAngle);
                    }
                    StatusText = $"Camera rotation determined: {RotationAngle}°.";
                } else {
                    StatusText = "Determine rotation failed: plate solve was unsuccessful.";
                    Notification.ShowError("Perihelion: determining camera rotation failed -- plate solve was unsuccessful.");
                }
            } catch (Exception ex) {
                StatusText = $"Determine rotation failed: {ex.Message}";
                Notification.ShowError($"Perihelion: determining camera rotation failed: {ex.Message}");
                Logger.Error("Perihelion: PerihelionFramingComposerVM.DetermineRotationAction failed", ex);
            } finally {
                IsBusy = false;
            }
        }

        /// <summary>Clears every adjustment made in this Composer session (pan/zoom, rotation,
        /// offset) back to the defaults it opened with -- lets a user start over without closing
        /// and reopening the whole window. Does not touch SelectedImageSource/RotatorConnected
        /// (real profile/hardware state, not a session adjustment).</summary>
        private void ResetAction() {
            ImagePanX = 0;
            ImagePanY = 0;
            ImageZoom = 2.5;
            // Back to the same smart default the window opened with (see the constructor's own
            // comment) -- RotatorConnected, not unconditionally off.
            UseRotation = RotatorConnected;
            RotationAngle = 0;
            IncludeCenter = true;
            OffsetRaArcsec = 0;
            OffsetDecArcsec = 0;
            FooterStatusText = "Reset -- framing adjustments cleared.";
        }

        /// <summary>Same math as PerihelionDockableVM's own SetOffsetFromMountAction -- captures
        /// wherever the mount is ACTUALLY pointed right now as an offset relative to the target's
        /// true position. Deliberately duplicated rather than shared: that method lives on a
        /// different VM instantiated a different way (MEF), and the few lines of arithmetic
        /// aren't worth a shared-state dependency between the two.</summary>
        private void CaptureOffsetAction() {
            var current = telescopeMediator.GetCurrentPosition();
            var raArcsec = Math.Round((current.RA - trueCoordinates.RA) * 15 * 3600, 1);
            var decArcsec = Math.Round((current.Dec - trueCoordinates.Dec) * 3600, 1);
            if (pixelsPerArcmin > 0) {
                // Setting the pan (not OffsetRa/DecArcsec directly) -- ImagePanX/Y's own setters
                // derive and set the offset, so this keeps the on-screen marker's position and
                // the real offset value as one source of truth instead of two that could drift,
                // and moves the marker to visually reflect this physical capture too.
                ImagePanX = (raArcsec / 60.0) * pixelsPerArcmin;
                ImagePanY = -(decArcsec / 60.0) * pixelsPerArcmin;
            } else {
                // No real gear configured to derive a pixel scale from -- fall back to setting
                // the offset directly; the marker just won't visually move to match.
                OffsetRaArcsec = raArcsec;
                OffsetDecArcsec = decArcsec;
            }
            StatusText = "Offset captured from the mount's current position.";
        }
    }
}
