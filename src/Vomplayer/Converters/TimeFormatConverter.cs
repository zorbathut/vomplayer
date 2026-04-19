using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Vomplayer.Util;

namespace Vomplayer.Converters;

public sealed class TimeFormatConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
        {
            return TimeFormatter.Format(ts.TotalSeconds);
        }
        if (value is double d)
        {
            return TimeFormatter.Format(d);
        }
        return BindingOperations.DoNothing;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
