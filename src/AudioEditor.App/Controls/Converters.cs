using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AudioEditor.Core;

namespace AudioEditor.App.Controls;

/// <summary>Seconds as m:ss.fff. Invalid input leaves the value unchanged.</summary>
public sealed class TimeTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double seconds ? TimeFormat.Format(seconds) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        TimeFormat.TryParse(value as string, out double seconds) ? seconds : Binding.DoNothing;
}

/// <summary>Seconds as a plain number, for example "0.50". Accepts "," as decimal separator.</summary>
public sealed class SecondsTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double seconds ? seconds.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = (value as string ?? string.Empty).Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds)
            ? seconds
            : Binding.DoNothing;
    }
}

/// <summary>A number with no more decimals than needed, for example "120" or "92.5". Accepts "," as decimal separator.</summary>
public sealed class NumberTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        double number => number.ToString("0.##", CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = (value as string ?? string.Empty).Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double number))
            return Binding.DoNothing;
        return targetType == typeof(int) ? (int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue) : number;
    }
}

/// <summary>True is Collapsed and false is Visible, the opposite of <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not Visibility.Visible;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
