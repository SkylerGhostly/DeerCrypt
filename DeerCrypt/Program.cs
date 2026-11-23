using Avalonia;
using Avalonia.Media;
using System;

namespace DeerCrypt
{
    internal class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main( string [ ] args ) => BuildAvaloniaApp( )
            .StartWithClassicDesktopLifetime( args );

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp( )
            => AppBuilder.Configure<App>( )
                .UsePlatformDetect( )
                .WithInterFont( )
                .With( new FontManagerOptions
                {
                    // Inter everywhere (bundled, so it looks the same on every OS).
                    // Glyphs Inter lacks (CJK, emoji, ...) fall back to system fonts.
                    DefaultFamilyName = "fonts:Inter#Inter"
                } )
                .With( new SkiaOptions
                {
                    // Bump Skia's internal resource cache to ~512 MB
                    MaxGpuResourceSizeBytes = 536870912
                } )
                .LogToTrace( );
    }
}
