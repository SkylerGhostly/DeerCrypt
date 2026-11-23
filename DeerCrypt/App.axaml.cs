using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeerCrypt.Services;
using DeerCrypt.ViewModels;
using DeerCrypt.Views;
using LibVLCSharp.Shared;
using SkiaSharp;
using System;
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
        public  static LibVLC  LibVLC => _libVLC ??= OperatingSystem.IsLinux( )
            ? new LibVLC( enableDebugLogs: false, "--no-video-title-show" ) // hardware decoding is switched off per media (see MediaSession)
            : new LibVLC( enableDebugLogs: false );

        public override void Initialize( )
        {
            AvaloniaXamlLoader.Load( this );
        }

        public override async void OnFrameworkInitializationCompleted( )
        {
            SettingsService.Load( );
            ThemeService.Apply( SettingsService.Settings );

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

                desktop.Exit += ( _, e ) =>
                {
                    // The window's closing flow normally locks the vault first; this is the
                    // backstop for any other exit path. Run off the UI thread with a timeout
                    // so a stuck background step can't hang shutdown.
                    try { Task.Run( ( ) => VaultService.DisposeAsync( ).AsTask( ) ).Wait( TimeSpan.FromSeconds( 15 ) ); }
                    catch { }

                    // End the process here, before Avalonia tears down its D-Bus connection.
                    // On Linux that teardown reports through the already-stopped UI dispatcher
                    // and crashes with an unhandled TaskCanceledException (happens even in a
                    // blank Avalonia 11.3 app). Nothing after this point matters to DeerCrypt.
                    Environment.Exit( e.ApplicationExitCode );
                };
            }

            base.OnFrameworkInitializationCompleted( );

            // Set AFTER base init so any Avalonia-internal SKGraphics calls can't override ours.
            // Skia's default is 2 MB, which glyph-heavy views (and any color emoji in file names)
            // exhaust quickly.
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

        /// <summary>
        /// Large image textures can crowd glyphs out of Skia's cache; after a big image is
        /// released, purge the font cache and redraw the main window so text renders cleanly.
        /// </summary>
        public static void RefreshGlyphCache( )
        {
            SKGraphics.PurgeFontCache( );
            Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
            {
                if( Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime dt
                    && dt.MainWindow is { } main )
                    Invalidate( main );
            }, Avalonia.Threading.DispatcherPriority.Render );

            static void Invalidate( Avalonia.Visual v )
            {
                v.InvalidateVisual( );
                foreach( var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren( v ) )
                    Invalidate( child );
            }
        }

        public static string TempDirectory =>
            Path.Combine( Path.GetTempPath( ), "DeerCrypt" );
    }
}
