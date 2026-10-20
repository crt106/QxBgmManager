using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QxBgmManager.Services;

/// <summary>内层列表能滚时先滚列表，到边界后继续滚动页面。</summary>
public static class ScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollBehavior), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not UIElement element) return;
        if ((bool)e.NewValue) element.PreviewMouseWheel += OnWheel;
        else element.PreviewMouseWheel -= OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || SystemParameters.WheelScrollLines == 0) return;
        var current = e.OriginalSource as DependencyObject;
        while (current != null)
        {
            if (current is ScrollViewer viewer && viewer.IsEnabled && viewer.ScrollableHeight > 0 &&
                (e.Delta < 0 ? viewer.VerticalOffset < viewer.ScrollableHeight - 0.1 : viewer.VerticalOffset > 0.1))
            {
                if (SystemParameters.WheelScrollLines < 0)
                {
                    if (e.Delta < 0) viewer.PageDown(); else viewer.PageUp();
                }
                else
                {
                    int lines = Math.Max(1, Math.Abs(e.Delta) / 120) * SystemParameters.WheelScrollLines;
                    for (int i = 0; i < lines; i++)
                        if (e.Delta < 0) viewer.LineDown(); else viewer.LineUp();
                }
                e.Handled = true;
                return;
            }
            if (current == sender) break;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current) ?? (current as FrameworkContentElement)?.Parent;
        }
    }
}
