using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Perihelion.Converters {

    /// <summary>Collapses a TextBlock when its bound string is null/empty -- used for the Quick
    /// Track status lines (elapsed/last-applied/next-reapply/errors), each of which is null when
    /// there's nothing to show rather than an empty string, so a stale blank line doesn't sit in
    /// the panel taking up space between sessions.</summary>
    public class NullOrEmptyToVisibilityConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotSupportedException();
        }
    }
}
