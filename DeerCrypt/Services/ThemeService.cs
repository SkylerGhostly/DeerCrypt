using Avalonia;
using Avalonia.Styling;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Applies appearance settings (theme variant and density) to the running app.
    /// Purely visual: nothing here touches vault data.
    /// </summary>
    public static class ThemeService
    {
        public static void Apply( AppSettings settings )
        {
            ApplyTheme( settings.Theme );
            ApplyDensity( settings.Density );
        }

        public static void ApplyTheme( string theme )
        {
            if( Application.Current is not { } app ) return;

            app.RequestedThemeVariant = theme switch
            {
                "Dark"  => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                _       => ThemeVariant.Default // follow the OS
            };
        }

        private static string _density = "Comfortable";
        private static bool   _gridThumbnails;

        public static void ApplyDensity( string density )
        {
            if( Application.Current is not { } app ) return;
            _density = density;

            bool compact = density == "Compact";
            app.Resources [ "ListRowPadding" ] = compact ? new Thickness( 10, 3 ) : new Thickness( 10, 7 );
            app.Resources [ "NavItemPadding" ] = compact ? new Thickness( 10, 4 ) : new Thickness( 10, 6 );
            ApplyGridTiles( app, compact );
        }

        /// <summary>Grid tiles get a larger picture area while thumbnails are on.</summary>
        public static void SetGridThumbnails( bool on )
        {
            if( _gridThumbnails == on || Application.Current is not { } app ) return;
            _gridThumbnails = on;
            ApplyGridTiles( app, _density == "Compact" );
        }

        private static void ApplyGridTiles( Application app, bool compact )
        {
            if( _gridThumbnails )
            {
                app.Resources [ "GridTileWidth"   ] = compact ? 112.0 : 136.0;
                app.Resources [ "GridTileHeight"  ] = compact ? 124.0 : 152.0;
                app.Resources [ "GridThumbHeight" ] = compact ? 60.0  : 80.0;
            }
            else
            {
                app.Resources [ "GridTileWidth"   ] = compact ? 100.0 : 118.0;
                app.Resources [ "GridTileHeight"  ] = compact ? 106.0 : 124.0;
                app.Resources [ "GridThumbHeight" ] = 44.0;
            }
        }
    }
}
