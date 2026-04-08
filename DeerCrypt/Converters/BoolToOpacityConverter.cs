using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace DeerCrypt.Converters
{
    /// <summary>
    /// Returns 0.35 opacity when the value is true (disabled), 1.0 otherwise.
    /// Used by MoveDialog to visually grey out items that cannot be selected as move targets.
    /// </summary>
    public class BoolToOpacityConverter : IValueConverter
    {
        public static readonly BoolToOpacityConverter Instance = new();

        public object Convert( object? value, Type targetType, object? parameter, CultureInfo culture )
            => value is true ? 0.35 : 1.0;

        public object ConvertBack( object? value, Type targetType, object? parameter, CultureInfo culture )
            => throw new NotSupportedException();
    }
}
