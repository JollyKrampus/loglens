using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace LogLens.Core;

/// <summary>
/// Null becomes UnsetValue rather than null, so a line with no highlight rule
/// inherits the theme foreground instead of snapping to WPF's default black.
/// </summary>
public sealed class InheritIfNullConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v ?? DependencyProperty.UnsetValue;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class NullToTransparentConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v as Brush ?? Brushes.Transparent;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class BoolToWrapConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v is true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Word wrap on means no horizontal scrollbar, and vice versa.</summary>
public sealed class WrapToHScrollConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v is true ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Set Invert=True in XAML to hide when true.</summary>
    public bool Invert { get; set; }

    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        bool b = v is true;
        if (string.Equals(p as string, "invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => v is Visibility.Visible;
}

public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => string.IsNullOrWhiteSpace(v as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class PositiveToVisibilityConverter : IValueConverter
{
    /// <summary>Set Invert=True to show only when the count is zero (empty states).</summary>
    public bool Invert { get; set; }

    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        bool positive = v is int i && i > 0;
        if (Invert) positive = !positive;
        return positive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Hex string to brush, for the swatches in the rules editor.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var s = v as string;
        if (string.IsNullOrWhiteSpace(s)) return Brushes.Transparent;
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)!);
            b.Freeze();
            return b;
        }
        catch { return Brushes.Transparent; }
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>"Cascadia Mono, Consolas" to a FontFamily with its fallback chain intact.</summary>
public sealed class StringToFontFamilyConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var s = v as string;
        if (string.IsNullOrWhiteSpace(s)) return new FontFamily("Consolas");
        try { return new FontFamily(s); }
        catch { return new FontFamily("Consolas"); }
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => (v as FontFamily)?.Source ?? "Consolas";
}

/// <summary>Line-number gutter width — collapses the column when numbers are off.</summary>
public sealed class LineNumberWidthConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v is true ? new GridLength(62) : new GridLength(0);

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}
