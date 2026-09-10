using System;
using System.Globalization;
using System.Windows.Data;

namespace Perihelion.Converters {

    /// <summary>
    /// Computes a flexible width as (bound value - offset), clamped to a sane minimum.
    /// GridViewColumn.Width has no native "*" star-sizing the way a Grid column does -- this is
    /// the standard WPF workaround: bind the last column's Width to the ListView's own
    /// ActualWidth (via ElementName -- GridViewColumn isn't part of the visual tree, but WPF's
    /// NameScope resolution still finds it there) minus everything else that already has a fixed
    /// width, so that column absorbs whatever space is left instead of the ListView just sitting
    /// narrower than its container on a wide panel.
    /// </summary>
    public sealed class RemainingWidthConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is not double actualWidth) return 100.0;
            var offset = parameter != null ? System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture) : 0.0;
            return Math.Max(100.0, actualWidth - offset);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
