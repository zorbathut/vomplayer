using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Vomplayer.Converters;

public sealed class PlayPauseLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool paused)
        {
            return paused ? "Play" : "Pause";
        }
        return BindingOperations.DoNothing;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
