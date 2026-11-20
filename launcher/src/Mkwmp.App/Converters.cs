// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Mkwmp.App;

/// <summary>
/// <c>Conv.Visible</c>: Visible when the value is "something" (true, a non-empty string, a non-zero
/// number, any other non-null object), else Collapsed. ConverterParameter <c>!</c> inverts;
/// <c>hide</c> gives Hidden instead of Collapsed; <c>=text</c> compares the value's text.
/// </summary>
public sealed class VisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var option = parameter as string ?? "";
        bool truthy;
        if (option.StartsWith('=') || option.StartsWith("!=", StringComparison.Ordinal))
        {
            var invert = option[0] == '!';
            var expected = option[(invert ? 2 : 1)..];
            truthy = string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), expected, StringComparison.Ordinal) != invert;
        }
        else
        {
            truthy = value switch
            {
                null => false,
                bool b => b,
                string s => s.Length > 0,
                int i => i != 0,
                double d => !double.IsNaN(d) && d != 0,
                _ => true,
            };
            if (option.Contains('!', StringComparison.Ordinal))
            {
                truthy = !truthy;
            }
        }
        return truthy ? Visibility.Visible : option.Contains("hide", StringComparison.Ordinal) ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary><c>Conv.Icon</c>: a resource key ("Icon.Mic") to its Geometry, so a view model can name its icon.</summary>
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && key.Length > 0 ? Application.Current?.TryFindResource(key) as Geometry : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary><c>Conv.Percent</c>: 0..1 to "40%".</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && !double.IsNaN(d) ? Math.Round(Math.Clamp(d, 0, 1) * 100).ToString(CultureInfo.InvariantCulture) + "%" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary><c>Conv.Not</c>: the opposite bool.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary><c>Conv.Fraction</c>: (value 0..1, available width) to a width, for hand-made bars.</summary>
public sealed class FractionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Length == 2 && values[0] is double fraction && values[1] is double width && !double.IsNaN(fraction)
            ? Math.Max(0, Math.Clamp(fraction, 0, 1) * width)
            : 0.0;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary><c>Conv.Art</c>: a seed (a name, or "#0" … "#7") to its generated gradient brush.</summary>
public sealed class ArtConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Controls.GeneratedArt.For(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
