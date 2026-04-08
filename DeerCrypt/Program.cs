using Avalonia;
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
                .With( new SkiaOptions
                {
                    // Bump Skia's internal resource cache to ~512 MB
                    MaxGpuResourceSizeBytes = 536870912
                } )
                .LogToTrace( );
    }
}
