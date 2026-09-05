using CommunityToolkit.Mvvm.Input;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer;
using NINA.Sequencer.SequenceItem.Platesolving;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IRotatorMediator rotatorMediator;
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
            ISequencerFactory factory) {
            TargetName = targetName;
            this.trueCoordinates = trueCoordinates;
            this.telescopeMediator = telescopeMediator;
            this.rotatorMediator = rotatorMediator;
            this.factory = factory;

            PositionText = $"RA {AstroUtil.HoursToHMS(trueCoordinates.RA)}  Dec {AstroUtil.DegreesToDMS(trueCoordinates.Dec)}";
            RotatorConnected = rotatorMediator.GetInfo().Connected;

            SlewAndCenterCommand = new AsyncRelayCommand(SlewAndCenterAction, () => !IsBusy && telescopeMediator.GetInfo().Connected);
            SlewAndCenterCommand.RegisterPropertyChangeNotification(this, nameof(IsBusy));

            CaptureOffsetCommand = new RelayCommand(CaptureOffsetAction, () => telescopeMediator.GetInfo().Connected);

            ConfirmCommand = new RelayCommand(() => Confirmed = true);
            CancelCommand = new RelayCommand(() => Confirmed = false);
        }

        public string TargetName { get; }
        public string PositionText { get; }
        public bool RotatorConnected { get; }

        private bool useRotation;
        public bool UseRotation {
            get => useRotation;
            set { useRotation = value; RaisePropertyChanged(); }
        }

        private double rotationAngle;
        public double RotationAngle {
            get => rotationAngle;
            set { rotationAngle = value; RaisePropertyChanged(); }
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
            OffsetRaArcsec = Math.Round((current.RA - trueCoordinates.RA) * 15 * 3600, 1);
            OffsetDecArcsec = Math.Round((current.Dec - trueCoordinates.Dec) * 3600, 1);
            StatusText = "Offset captured from the mount's current position.";
        }
    }
}
