using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace QxBgmManager.Converters;

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>状态圆点：通过绿、未过红、未知灰。</summary>
public class StatusDotConverter : IValueConverter
{
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x7A, 0x7A)));
    private static readonly Brush Gray = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x80)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? (b ? Green : Red) : Gray;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>枚举值 → 文字。</summary>
public class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // parameter: "真值|假值"
        var parts = (parameter as string ?? "|").Split('|');
        var t = parts.Length > 0 ? parts[0] : "";
        var f = parts.Length > 1 ? parts[1] : "";
        return value is true ? t : f;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>枚举值 → Visibility：value.ToString() 与 parameter 相等时可见。</summary>
public class EnumMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>环境检查级别 ●/○。</summary>
public class EnvCheckLevelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == "Critical" ? "●" : "○";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔 → 画刷：真值用警示红，假值用三级文字色（如目标已补丁提示）。</summary>
public class BoolToWarnBrushConverter : IValueConverter
{
    private static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x7A, 0x7A)));
    private static readonly Brush Normal = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x90, 0xA0)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Warn : Normal;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
