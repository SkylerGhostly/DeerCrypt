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

        public static void ApplyDensity( string density )
        {
            if( Application.Current is not { } app ) return;

            bool compact = density == "Compact";
            app.Resources [ "ListRowPadding" ] = compact ? new Thickness( 10, 3 ) : new Thickness( 10, 7 );
            app.Resources [ "NavItemPadding" ] = compact ? new Thickness( 10, 4 ) : new Thickness( 10, 6 );
            app.Resources [ "GridTileWidth"  ] = compact ? 100.0 : 118.0;
            app.Resources [ "GridTileHeight" ] = compact ? 106.0 : 124.0;
        }
    }
}
