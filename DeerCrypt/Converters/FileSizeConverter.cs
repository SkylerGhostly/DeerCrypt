using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace DeerCrypt.Converters
{
    public class FileSizeConverter : IValueConverter
    {
        public static readonly FileSizeConverter Instance = new();

        public object Convert( object? value, Type targetType, object? parameter, CultureInfo culture )
        {
            if( value is not long bytes ) return string.Empty;
            return bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
                < 1024L * 1024 * 1024 => $"{bytes / ( 1024.0 * 1024 ):F1} MB",
                _ => $"{bytes / ( 1024.0 * 1024 * 1024 ):F2} GB"
            };
        }

        public object ConvertBack( object? value, Type targetType, object? parameter, CultureInfo culture )
            => throw new NotImplementedException( );
    }
}