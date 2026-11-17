using Microsoft.UI.Xaml;

namespace BatteryCarePortal;

internal static class LayoutHelper
{
    // XAML has no "start" alignment: Left is the physical left even in a right-to-left layout.
    public static void AlignToStart(FrameworkElement element)
    {
        void Apply() => element.HorizontalAlignment =
            element.FlowDirection == FlowDirection.RightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        element.Loaded += (_, _) => Apply();
        Apply();
    }
}
