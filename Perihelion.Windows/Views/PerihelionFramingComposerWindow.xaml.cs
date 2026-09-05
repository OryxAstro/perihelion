using Perihelion.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;

namespace Perihelion.Views {

    /// <summary>
    /// Code-behind exists here for two real reasons, both things a plain MVVM command has no way
    /// to do on its own: (1) a real popup Window needs somewhere to actually close the dialog
    /// (Confirm/Cancel below), and (2) WPF has no built-in pan/zoom/drag gesture support without
    /// a third-party behaviors library this project doesn't reference, so the sky map's own
    /// mouse wheel/drag handling lives here too, calling straight into the VM's own state
    /// (ImagePanX/Y/ImageZoom for the purely-cosmetic background pan/zoom, MoveFovRectBy for the
    /// FOV box drag, which is a real interaction -- see PerihelionFramingComposerWindow.xaml's
    /// own comment on the distinction between the two).
    /// </summary>
    public partial class PerihelionFramingComposerWindow : Window {
        private const double MinZoom = 1.0;
        private const double MaxZoom = 6.0;

        private PerihelionFramingComposerVM ViewModel => (PerihelionFramingComposerVM)DataContext;

        private bool draggingImage;
        private bool draggingFovRect;
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

        // --- Background pan/zoom (purely cosmetic) ---

        private void SkyMap_MouseWheel(object sender, MouseWheelEventArgs e) {
            var vm = ViewModel;
            var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            vm.ImageZoom = System.Math.Clamp(vm.ImageZoom * factor, MinZoom, MaxZoom);
            e.Handled = true;
        }

        private void SkyMapImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            draggingImage = true;
            lastMousePosition = e.GetPosition(this);
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void SkyMapImage_MouseMove(object sender, MouseEventArgs e) {
            if (!draggingImage) return;
            var position = e.GetPosition(this);
            var vm = ViewModel;
            vm.ImagePanX += position.X - lastMousePosition.X;
            vm.ImagePanY += position.Y - lastMousePosition.Y;
            lastMousePosition = position;
        }

        private void SkyMapImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            draggingImage = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        // --- FOV rectangle drag (a real interaction -- sets the real offset) ---

        private void FovRect_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            draggingFovRect = true;
            lastMousePosition = e.GetPosition(this);
            ((Rectangle)sender).CaptureMouse();
            // Stops this reaching the Image underneath, which would otherwise also start a
            // background pan for the same mouse-down (WPF hit-testing gives the topmost element
            // -- this Rectangle -- the event first, but a routed event still bubbles/tunnels past
            // it unless explicitly marked handled).
            e.Handled = true;
        }

        private void FovRect_MouseMove(object sender, MouseEventArgs e) {
            if (!draggingFovRect) return;
            var position = e.GetPosition(this);
            ViewModel.MoveFovRectBy(position.X - lastMousePosition.X, position.Y - lastMousePosition.Y);
            lastMousePosition = position;
            e.Handled = true;
        }

        private void FovRect_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            draggingFovRect = false;
            ((Rectangle)sender).ReleaseMouseCapture();
            e.Handled = true;
        }
    }
}
