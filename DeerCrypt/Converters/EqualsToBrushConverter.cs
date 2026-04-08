using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeerCrypt.Converters
{
    /// <summary>
    /// IMultiValueConverter used for breadcrumb drag-drop highlighting.
    /// Binding[0] = DirectoryId of the breadcrumb segment.
    /// Binding[1] = VaultBrowserViewModel.BreadcrumbDragTargetId (currently dragged-over segment).
    /// Returns a highlight brush when equal, transparent otherwise.
    /// </summary>
    public class EqualsToBrushConverter : IMultiValueConverter
    {
        public static readonly EqualsToBrushConverter Instance = new();

        public object? Convert( IList<object?> values, Type targetType, object? parameter, CultureInfo culture )
        {
            if( values.Count >= 2 && values [ 0 ] is string a && values [ 1 ] is string b && a == b )
                return new SolidColorBrush( Color.FromArgb( 60, 91, 155, 213 ) );

            return Brushes.Transparent;
        }
    }
}
