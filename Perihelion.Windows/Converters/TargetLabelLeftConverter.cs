using System;
using System.Globalization;
using System.Windows.Data;

namespace Perihelion.Converters {

    /// <summary>Computes a Canvas.Left for the Framing Composer's target-name label so its LEFT
    /// edge sits a gap to the right of the target marker, or its RIGHT edge sits the same gap to
    /// the left of it -- depending on which side of the path's own continuation the label should
    /// sit on (see PerihelionFramingComposerVM.TargetLabelOnRight's own doc comment). The gap
    /// itself is a bound value (TargetLabelGap), not a constant here -- it has to track the
    /// marker's own on-screen radius, which grows with ImageZoom (see TargetLabelGap's own
    /// doc comment for the collision bug this fixes). WPF has no text-anchor equivalent to
    /// SVG's (which is how FramingOffsetView.vue solves the exact same problem, `ctx.textAlign`/
    /// measured box width): the label's own rendered width is only known once it's actually
    /// measured, hence a MultiBinding pulling in the label's own ActualWidth via a self-reference,
    /// not something precomputed in the VM.</summary>
    public class TargetLabelLeftConverter : IMultiValueConverter {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            if (values.Length < 4 || values[0] is not double anchorX || values[1] is not bool onRight
                || values[2] is not double width || values[3] is not double gap) {
                return 0.0;
            }
            return onRight ? anchorX + gap : anchorX - gap - width;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
