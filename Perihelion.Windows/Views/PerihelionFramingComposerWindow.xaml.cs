using Perihelion.ViewModels;
using System.Windows;

namespace Perihelion.Views {

    /// <summary>
    /// Code-behind exists only because a real popup Window (unlike this project's other
    /// Windows-only views, which are all merged DataTemplates) needs somewhere to actually close
    /// the dialog -- a plain MVVM command has no reference to the Window it lives in to call
    /// Close() on. Confirm/Cancel here just delegate the real state change to the VM's own
    /// commands (so PerihelionFramingComposerVM.Confirmed reflects which one was actually
    /// clicked), then close.
    /// </summary>
    public partial class PerihelionFramingComposerWindow : Window {
        public PerihelionFramingComposerWindow(PerihelionFramingComposerVM vm) {
            InitializeComponent();
            DataContext = vm;
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) {
            ((PerihelionFramingComposerVM)DataContext).ConfirmCommand.Execute(null);
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) {
            ((PerihelionFramingComposerVM)DataContext).CancelCommand.Execute(null);
            DialogResult = false;
            Close();
        }
    }
}
