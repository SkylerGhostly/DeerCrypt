using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeerCrypt.Services;
using DeerCrypt.ViewModels;
using DeerCrypt.Views;
using LibVLCSharp.Shared;
using SkiaSharp;
using System.IO;
using System.Threading.Tasks;

namespace DeerCrypt
{
    public partial class App : Application
    {
        public static VaultService        VaultService        { get; } = new( );
        public static RecentVaultsService RecentVaultsService { get; } = new( );
        public static SettingsService     SettingsService     { get; } = new( );

        // Lazy singleton - initialised on first media open, never disposed (app lifetime).
        private static LibVLC? _libVLC;
        public  static LibVLC  LibVLC => _libVLC ??= new LibVLC( enableDebugLogs: false );

        public override void Initialize( )
        {
            AvaloniaXamlLoader.Load( this );
        }

        public override async void OnFrameworkInitializationCompleted( )
        {
            SettingsService.Load( );

            // Clean up any temp files left from a previous session
            CleanupTempDirectory( );

            // Pre-warm LibVLC on a background thread so the first media open
            // does not block the UI thread while native VLC DLLs are loaded.
            _ = Task.Run( ( ) => { try { _ = LibVLC; } catch { } } );

            if( ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop )
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainViewModel( VaultService, RecentVaultsService )
                };

                desktop.Exit += async ( _, _ ) => await VaultService.DisposeAsync( );
            }

            base.OnFrameworkInitializationCompleted( );

            // Set AFTER base init so any Avalonia-internal SKGraphics calls can't override ours.
            // Skia's default is 2 MB; color emoji at 72 pt on a 2× DPI screen occupy ~83 KB each,
            // so the default exhausts quickly and causes subsequent emoji to fall back to monochrome.
            SKGraphics.SetFontCacheLimit( 64 * 1024 * 1024 );
        }

        private static void CleanupTempDirectory( )
        {
            try
            {
                if( Directory.Exists( TempDirectory ) )
                    Directory.Delete( TempDirectory, recursive: true );
            }
            catch { }
        }

        public static string TempDirectory =>
            Path.Combine( Path.GetTempPath( ), "DeerCrypt" );
    }
}
