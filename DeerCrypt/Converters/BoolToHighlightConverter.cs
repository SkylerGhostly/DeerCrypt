using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace DeerCrypt.Converters
{
    public class BoolToHighlightConverter : IValueConverter
    {
        public static readonly BoolToHighlightConverter Instance = new();

        public object? Convert( object? value, Type targetType, object? parameter, CultureInfo culture )
        {
            return value is true
                ? DropTargetBrushProvider.Get( )
                : null;
        }

        public object ConvertBack( object? value, Type targetType, object? parameter, CultureInfo culture )
            => throw new NotImplementedException( );
    }
}