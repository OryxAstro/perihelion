using System;
using System.Globalization;
using System.Windows.Data;

namespace Perihelion.Converters {

    /// <summary>Computes a Canvas.Left for the Framing Composer's target-name label so its LEFT
    /// edge sits a fixed gap to the right of the target marker, or its RIGHT edge sits the same
    /// gap to the left of it -- depending on which side of the path's own continuation the label
    /// should sit on (see PerihelionFramingComposerVM.TargetLabelOnRight's own doc comment).
    /// WPF has no text-anchor equivalent to SVG's (which is how FramingOffsetView.vue solves the
    /// exact same problem, `ctx.textAlign`/measured box width): the label's own real rendered
    /// width is only known once it's actually measured, hence a MultiBinding pulling in the
    /// label's own ActualWidth via a self-reference, not something precomputed in the VM.</summary>
    public class TargetLabelLeftConverter : IMultiValueConverter {
        private const double Gap = 8;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            if (values.Length < 3 || values[0] is not double anchorX || values[1] is not bool onRight || values[2] is not double width) {
                return 0.0;
            }
            return onRight ? anchorX + Gap : anchorX - Gap - width;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
