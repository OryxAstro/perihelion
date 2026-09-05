using CommunityToolkit.Mvvm.Input;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.WPF.Base.SkySurvey;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
        // control stretches the source bitmap to fill this fixed area either way. 480, not the
        // original 320 -- real user feedback (2026-09-05): the whole window read as too small.
        public const double SkyMapDisplaySize = 480;

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

        private readonly ITelescopeMediator telescopeMediator;
        private readonly IRotatorMediator rotatorMediator;
        private readonly IProfileService profileService;
        private readonly IImageDataFactory imageDataFactory;
        private readonly ISequencerFactory factory;
        private readonly Coordinates trueCoordinates;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public PerihelionFramingComposerVM(
            string targetName,
            Coordinates trueCoordinates,
            ITelescopeMediator telescopeMediator,
            IRotatorMediator rotatorMediator,
            IProfileService profileService,
            IImageDataFactory imageDataFactory,
            ISequencerFactory factory) {
            TargetName = targetName;
            this.trueCoordinates = trueCoordinates;
            this.telescopeMediator = telescopeMediator;
            this.rotatorMediator = rotatorMediator;
            this.profileService = profileService;
            this.imageDataFactory = imageDataFactory;
            this.factory = factory;

            PositionText = $"RA {AstroUtil.HoursToHMS(trueCoordinates.RA)}  Dec {AstroUtil.DegreesToDMS(trueCoordinates.Dec)}";
            RotatorConnected = rotatorMediator.GetInfo().Connected;
            // Backing field directly, not the property setter -- the setter's own side effects
            // (persisting to the profile, re-triggering LoadSkyMapAsync) are for when the USER
            // changes the dropdown; LoadSkyMapAsync below already runs once regardless, so
            // running it a second time here too would just be a redundant fetch on open.
            selectedImageSource = profileService.ActiveProfile.FramingAssistantSettings.LastSelectedImageSource;

            SlewAndCenterCommand = new AsyncRelayCommand(SlewAndCenterAction, () => !IsBusy && telescopeMediator.GetInfo().Connected);
            SlewAndCenterCommand.RegisterPropertyChangeNotification(this, nameof(IsBusy));

            CaptureOffsetCommand = new RelayCommand(CaptureOffsetAction, () => telescopeMediator.GetInfo().Connected);

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

        /// <summary>File/Cache deliberately excluded -- File needs a local file picker (not
        /// built here) and Cache only has content once something else has already populated it.
        /// The remaining five are all live, no-setup sky-survey sources plus the offline
        /// placeholder, matching what a user picking an image source in real NINA would actually
        /// choose between.</summary>
        public IReadOnlyList<SkySurveySource> ImageSources { get; } = new[] {
            SkySurveySource.NASA, SkySurveySource.HIPS2FITS, SkySurveySource.STSCI,
            SkySurveySource.ESO, SkySurveySource.SKYSERVER, SkySurveySource.SKYATLAS,
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
        public bool UseRotation {
            get => useRotation;
            set { useRotation = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(DisplayRotationAngle)); }
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

        private double imageZoom = 1.0;
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

        // Arcsec internally (same convention as PerihelionDockableVM's own Offset fields) --
        // OffsetRaText/OffsetDecText below match its exact HH:MM:SS/DMS display format.
        private double offsetRaArcsec;
        public double OffsetRaArcsec {
            get => offsetRaArcsec;
            private set { offsetRaArcsec = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(OffsetRaText)); }
        }

        private double offsetDecArcsec;
        public double OffsetDecArcsec {
            get => offsetDecArcsec;
            private set { offsetDecArcsec = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(OffsetDecText)); }
        }

        public string OffsetRaText => AstroUtil.HoursToHMS(offsetRaArcsec / 3600.0 / 15.0);
        public string OffsetDecText => AstroUtil.DegreesToDMS(offsetDecArcsec / 3600.0);

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
        public double FovRectWidth { get => fovRectWidth; private set { fovRectWidth = value; RaisePropertyChanged(); } }
        public double FovRectHeight { get => fovRectHeight; private set { fovRectHeight = value; RaisePropertyChanged(); } }

        // Set once per LoadSkyMapAsync call -- how many on-screen pixels correspond to one arcmin
        // on the sky, used by ImagePanX/Y's own setters to convert a drag distance to a real
        // angular offset, and by CaptureOffsetAction to convert the other way.
        private double pixelsPerArcmin = 1;

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
                // Reset -- a freshly (re)loaded sky map has no pan/zoom applied to it yet, and
                // switching image source mid-session shouldn't leave a stale offset from the
                // previous image's own pixel scale.
                ImagePanX = 0;
                ImagePanY = 0;
                ImageZoom = 1.0;

                pixelsPerArcmin = SkyMapDisplaySize / image.FoVWidth;
                FovRectWidth = Math.Min(SkyMapDisplaySize, cameraFovWidthArcmin * pixelsPerArcmin);
                FovRectHeight = Math.Min(SkyMapDisplaySize, cameraFovHeightArcmin * pixelsPerArcmin);

                SkyMapStatusText = cameraFovWidthArcmin > 0
                    ? string.Empty
                    : "Camera/telescope profile isn't fully configured -- showing the sky map without a real FOV rectangle.";
            } catch (Exception ex) {
                SkyMapStatusText = $"Sky map unavailable: {ex.Message}";
                Logger.Warning($"Perihelion: PerihelionFramingComposerVM.LoadSkyMapAsync failed: {ex}");
            } finally {
                SkyMapLoading = false;
            }
        }

        public AsyncRelayCommand SlewAndCenterCommand { get; }
        public RelayCommand CaptureOffsetCommand { get; }
        public RelayCommand ConfirmCommand { get; }
        public RelayCommand CancelCommand { get; }

        /// <summary>Set once Confirm/Cancel is clicked -- PerihelionFramingComposerWindow's own
        /// code-behind reads this immediately after to decide whether to copy this VM's captured
        /// state back into the caller or discard it, then closes the window either way.</summary>
        public bool? Confirmed { get; private set; }

        /// <summary>Null means plain Center was used (no rotator involved) -- the caller should
        /// treat that as "use plain Center" for Add to Sequence/Quick Track too, not default to
        /// some other angle.</summary>
        public double? CapturedRotationAngle => UseRotation ? RotationAngle : (double?)null;

        private async Task SlewAndCenterAction() {
            IsBusy = true;
            StatusText = UseRotation ? $"Slewing, centering, and rotating to {RotationAngle}°..." : "Slewing and centering...";
            try {
                var progress = new Progress<ApplicationStatus>(s => StatusText = s.Status ?? StatusText);
                if (UseRotation) {
                    var rotate = factory.GetItem<CenterAndRotate>();
                    rotate.Inherited = false;
                    rotate.Coordinates = new InputCoordinates(trueCoordinates);
                    rotate.PositionAngle = RotationAngle;
                    await rotate.Execute(progress, CancellationToken.None);
                } else {
                    var center = factory.GetItem<Center>();
                    center.Inherited = false;
                    center.Coordinates = new InputCoordinates(trueCoordinates);
                    await center.Execute(progress, CancellationToken.None);
                }
                StatusText = "Centered. Nudge the mount now if you want to frame off-center (e.g. a comet's tail), then Capture Offset.";
            } catch (Exception ex) {
                StatusText = $"Slew/center failed: {ex.Message}";
                Notification.ShowError($"Perihelion: framing slew/center failed: {ex.Message}");
                Logger.Error("Perihelion: PerihelionFramingComposerVM.SlewAndCenterAction failed", ex);
            } finally {
                IsBusy = false;
            }
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
