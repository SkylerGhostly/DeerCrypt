using Avalonia;
using Avalonia.Media;

namespace DeerCrypt.Converters
{
    /// <summary>Looks up the theme's drop-target highlight so converters follow dark/light and the OS accent.</summary>
    internal static class DropTargetBrushProvider
    {
        private static readonly IBrush _fallback = new SolidColorBrush( Color.FromArgb( 60, 91, 155, 213 ) );

        public static IBrush Get( )
        {
            if( Application.Current is { } app
                && app.TryGetResource( "DropTargetBrush", app.ActualThemeVariant, out object? res )
                && res is IBrush brush )
                return brush;

            return _fallback;
        }
    }
}
