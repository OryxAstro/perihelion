using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Perihelion.ViewModels;

namespace Perihelion.Converters {

    /// <summary>Browse list's own per-row coloring for the "Observed Mag (COBS)" column --
    /// BrowseObject is a shared, cross-platform class (also serialized as the PINS/Touch-N-Stars
    /// API response), so a WPF Brush property can't live on it directly the way it does on
    /// PerihelionDockableVM's own Position/Elements card. A converter keeps the coloring purely
    /// in the WPF layer instead. Reuses PerihelionDockableVM.MagnitudeDiffBrush's exact same
    /// thresholds (ported from Touch-N-Stars' own magDiffTier) rather than a second copy of the
    /// same logic.</summary>
    public class MagnitudeDiffBrushConverter : IMultiValueConverter {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            var predicted = values.Length > 0 && values[0] is double p ? p : (double?)null;
            var observed = values.Length > 1 && values[1] is double o ? o : (double?)null;
            return PerihelionDockableVM.MagnitudeDiffBrush(predicted, observed);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
