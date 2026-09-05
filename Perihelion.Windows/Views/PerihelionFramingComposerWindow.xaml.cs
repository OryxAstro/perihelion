using Perihelion.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace Perihelion.Views {

    /// <summary>
    /// Code-behind exists here for two real reasons, both things a plain MVVM command has no way
    /// to do on its own: (1) a real popup Window needs somewhere to actually close the dialog
    /// (Confirm/Cancel below), and (2) WPF has no built-in pan/zoom gesture support without a
    /// third-party behaviors library this project doesn't reference, so the sky map's own mouse
    /// wheel/drag handling lives here too, calling straight into the VM's own ImagePanX/Y/
    /// ImageZoom -- ImagePanX/Y's own setters derive and set the real offset, so there's nothing
    /// else this code-behind needs to compute itself.
    ///
    /// One drag interaction only (2026-09-05, corrected after reading Touch-N-Stars' own
    /// FramingOffsetView.vue directly): panning the whole sky map, same as that component's own
    /// real behavior -- there is no second, independently-draggable FOV rectangle.
    /// </summary>
    public partial class PerihelionFramingComposerWindow : Window {
        private const double MinZoom = 1.0;
        private const double MaxZoom = 6.0;

        private PerihelionFramingComposerVM ViewModel => (PerihelionFramingComposerVM)DataContext;

        private bool dragging;
        private Point lastMousePosition;

        public PerihelionFramingComposerWindow(PerihelionFramingComposerVM vm) {
            InitializeComponent();
            DataContext = vm;
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) {
            ViewModel.ConfirmCommand.Execute(null);
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) {
            ViewModel.CancelCommand.Execute(null);
            DialogResult = false;
            Close();
        }

        private void SkyMap_MouseWheel(object sender, MouseWheelEventArgs e) {
            var vm = ViewModel;
            var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            vm.ImageZoom = System.Math.Clamp(vm.ImageZoom * factor, MinZoom, MaxZoom);
            e.Handled = true;
        }

        private void SkyMap_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            dragging = true;
            lastMousePosition = e.GetPosition(this);
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void SkyMap_MouseMove(object sender, MouseEventArgs e) {
            if (!dragging) return;
            var position = e.GetPosition(this);
            var vm = ViewModel;
            vm.ImagePanX += position.X - lastMousePosition.X;
            vm.ImagePanY += position.Y - lastMousePosition.Y;
            lastMousePosition = position;
        }

        private void SkyMap_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            dragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }
    }
}
