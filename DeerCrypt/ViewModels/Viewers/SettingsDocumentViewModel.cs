using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Converters;
using DeerCrypt.Services;
using DeerCrypt.ViewModels.Dialogs;
using DeerCrypt.Views.Dialogs;
using DeerCryptLib.Vault;
using FluentIcons.Common;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Viewers
{
    /// <summary>
    /// The Settings tab: app-wide preferences (saved to settings.json) and this vault's own
    /// settings (stored inside the vault). Only the "This vault" section ever writes to the vault.
    /// </summary>
    public sealed partial class SettingsDocumentViewModel : DocumentViewModel
    {
        private static readonly long [ ] CacheLimitsMb = [ 64, 128, 256, 512, 1024, 2048 ];
        private static readonly string [ ] ModeValues  = [ "Off", "Blurred", "On" ];

        private bool _loading;
        private bool _refreshQueued;

        public VaultBrowserViewModel Browser { get; }

        public SettingsDocumentViewModel( VaultBrowserViewModel browser )
        {
            Browser = browser;
            Title   = "Settings";

            _loading = true;
            var s = App.SettingsService.Settings;
            ThemeIndex            = s.Theme switch { "Dark" => 1, "Light" => 2, _ => 0 };
            DensityIndex          = s.Density == "Compact" ? 1 : 0;
            ViewerPlacementIndex  = s.OpenViewersIn == "Windows" ? 1 : 0;
            AutoLockIndex         = Math.Max( 0, Array.IndexOf( AutoLockMinutes, s.AutoLockMinutes ) );
            DefaultThumbnailIndex = Math.Max( 0, Array.IndexOf( ModeValues, s.DefaultThumbnails ) );
            VaultThumbnailIndex   = browser.VaultThumbnailSetting is { } v ? Array.IndexOf( ModeValues, v ) + 1 : 0;
            CacheLimitIndex       = Math.Max( 0, Array.IndexOf( CacheLimitsMb, browser.ThumbnailCacheLimitBytes / ( 1024 * 1024 ) ) );
            _loading = false;

            browser.ThumbnailCacheChanged += OnCacheChanged;
            browser.PropertyChanged += OnBrowserPropertyChanged;
            _ = RefreshAsync( );
        }

        public override Symbol Icon      => Symbol.Settings;
        public override IBrush IconBrush => FileKindVisuals.GetBrush( FileKind.Other );

        // Appearance (app-wide)

        [ObservableProperty] public partial int ThemeIndex { get; set; }
        [ObservableProperty] public partial int DensityIndex { get; set; }

        partial void OnThemeIndexChanged( int value )
        {
            if( _loading ) return;
            string theme = value switch { 1 => "Dark", 2 => "Light", _ => "System" };
            App.SettingsService.Settings.Theme = theme;
            App.SettingsService.Save( );
            ThemeService.ApplyTheme( theme );
        }

        partial void OnDensityIndexChanged( int value )
        {
            if( _loading ) return;
            string density = value == 1 ? "Compact" : "Comfortable";
            App.SettingsService.Settings.Density = density;
            App.SettingsService.Save( );
            ThemeService.ApplyDensity( density );
        }

        // Security (app-wide)

        private static readonly int [ ] AutoLockMinutes = [ 0, 1, 5, 10, 15, 30, 60 ];

        /// <summary>Index into Never / 1 / 5 / 10 / 15 / 30 / 60 minutes.</summary>
        [ObservableProperty] public partial int AutoLockIndex { get; set; }

        partial void OnAutoLockIndexChanged( int value )
        {
            if( _loading ) return;
            App.SettingsService.Settings.AutoLockMinutes = AutoLockMinutes [ Math.Clamp( value, 0, AutoLockMinutes.Length - 1 ) ];
            App.SettingsService.Save( );
        }

        // Opening files (app-wide)

        /// <summary>0 = tabs in the main window, 1 = a window per file.</summary>
        [ObservableProperty] public partial int ViewerPlacementIndex { get; set; }

        partial void OnViewerPlacementIndexChanged( int value )
        {
            if( _loading ) return;
            App.SettingsService.Settings.OpenViewersIn = value == 1 ? "Windows" : "Tabs";
            App.SettingsService.Save( );
        }

        // Thumbnails

        /// <summary>0 = follow the app default, then Off / Blurred / On.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( FollowDefaultText ) )]
        public partial int VaultThumbnailIndex { get; set; }

        /// <summary>App-wide default for vaults that haven't chosen: Off / Blurred / On.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( FollowDefaultText ) )]
        public partial int DefaultThumbnailIndex { get; set; }

        [ObservableProperty] public partial int CacheLimitIndex { get; set; }

        public string FollowDefaultText => Browser.ThumbnailsUndecided
            ? "Not chosen yet (off)"
            : $"Use the default ({ModeLabel( DefaultThumbnailIndex )})";

        private static string ModeLabel( int index ) => index switch { 1 => "blurred until hover", 2 => "on", _ => "off" };

        public bool ThumbnailsHidden
        {
            get => Browser.ThumbnailsHidden;
            set => Browser.ThumbnailsHidden = value;
        }

        async partial void OnVaultThumbnailIndexChanged( int value )
        {
            if( _loading ) return;
            await RunVaultWriteAsync( ( ) => Browser.SetVaultThumbnailSettingAsync( value == 0 ? null : ModeValues [ value - 1 ] ) );
        }

        partial void OnDefaultThumbnailIndexChanged( int value )
        {
            if( _loading ) return;
            App.SettingsService.Settings.DefaultThumbnails = ModeValues [ Math.Clamp( value, 0, 2 ) ];
            App.SettingsService.Save( );
            Browser.ApplyThumbnailMode( );
        }

        async partial void OnCacheLimitIndexChanged( int value )
        {
            if( _loading ) return;
            long bytes = CacheLimitsMb [ Math.Clamp( value, 0, CacheLimitsMb.Length - 1 ) ] * 1024 * 1024;
            await RunVaultWriteAsync( ( ) => Browser.SetThumbnailCacheLimitAsync( bytes ) );
            await RefreshAsync( );
        }

        [ObservableProperty] public partial string CacheUsageText { get; set; } = "…";
        [ObservableProperty] public partial double CacheUsagePercent { get; set; }
        [ObservableProperty] public partial bool HasCache { get; set; }
        [ObservableProperty] public partial string ErrorText { get; set; } = string.Empty;

        [RelayCommand]
        private async Task ClearCacheAsync( )
        {
            if( Browser.OwnerWindow is { } owner )
            {
                var confirm = new ConfirmDialogViewModel
                {
                    Title       = "Clear thumbnails",
                    Message     = "Delete every thumbnail stored in this vault?",
                    Detail      = "Your files aren't affected. If thumbnails are on, they're made again as you browse. " +
                                  "Use Compact afterwards to shrink the vault file.",
                    ConfirmText = "Clear",
                    Tone        = DialogTone.Warning
                };
                if( await new ConfirmDialog { DataContext = confirm }.ShowDialog<bool?>( owner ) != true ) return;
            }

            await RunVaultWriteAsync( Browser.ClearThumbnailCacheAsync );
            await RefreshAsync( );
        }

        // This vault (read-only details)

        [ObservableProperty] public partial string VaultName { get; set; } = string.Empty;
        [ObservableProperty] public partial string VaultPath { get; set; } = string.Empty;
        [ObservableProperty] public partial string VaultSizeText { get; set; } = string.Empty;
        [ObservableProperty] public partial string VaultContentsText { get; set; } = string.Empty;
        [ObservableProperty] public partial string VaultCreatedText { get; set; } = string.Empty;

        public string AppVersion { get; } =
            Assembly.GetExecutingAssembly( ).GetName( ).Version is { } v ? $"DeerCrypt {v.Major}.{v.Minor}.{v.Build}" : string.Empty;

        // Loading and saving

        private async Task RunVaultWriteAsync( Func<Task> write )
        {
            ErrorText = string.Empty;
            try { await write( ); }
            catch( ObjectDisposedException ) { }
            catch( Exception ex ) { ErrorText = $"Couldn't save to the vault: {ex.Message}"; }
        }

        private void OnCacheChanged( )
        {
            // Raised per stored thumbnail on a worker thread: refresh at most twice a second
            if( _refreshQueued ) return;
            _refreshQueued = true;
            DispatcherTimer.RunOnce( ( ) => { _refreshQueued = false; _ = RefreshAsync( ); }, TimeSpan.FromMilliseconds( 500 ) );
        }

        private void OnBrowserPropertyChanged( object? sender, System.ComponentModel.PropertyChangedEventArgs e )
        {
            if( e.PropertyName == nameof( VaultBrowserViewModel.ThumbnailsHidden ) )
                OnPropertyChanged( nameof( ThumbnailsHidden ) );
        }

        private async Task RefreshAsync( )
        {
            try
            {
                var cache = await Browser.GetThumbnailCacheInfoAsync( );
                long limit = Browser.ThumbnailCacheLimitBytes;
                HasCache = cache.Count > 0;
                CacheUsagePercent = limit > 0 ? Math.Clamp( 100.0 * cache.Bytes / limit, 0, 100 ) : 0;
                CacheUsageText = cache.Count == 0
                    ? $"Empty · limit {FileSizeConverter.Format( limit )}"
                    : $"{cache.Count:N0} thumbnail{( cache.Count == 1 ? "" : "s" )} · {FileSizeConverter.Format( cache.Bytes )} of {FileSizeConverter.Format( limit )}";

                VaultInfo info = await Browser.GetVaultInfoAsync( );
                VaultPath         = Browser.VaultFilePath;
                VaultName         = Path.GetFileNameWithoutExtension( VaultPath );
                VaultSizeText     = FileSizeConverter.Format( info.VaultFileSize );
                VaultContentsText = $"{info.TotalFiles:N0} file{( info.TotalFiles == 1 ? "" : "s" )} in {info.TotalDirectories:N0} folder{( info.TotalDirectories == 1 ? "" : "s" )} · {FileSizeConverter.Format( info.TotalOriginalSize )}";
                VaultCreatedText  = info.CreatedAt == DateTime.MinValue ? "Unknown" : info.CreatedAt.ToLocalTime( ).ToString( "D" );
            }
            catch( ObjectDisposedException ) { }
            catch( Exception ex ) { ErrorText = $"Couldn't read the vault's details: {ex.Message}"; }
        }

        public override void Dispose( )
        {
            Browser.ThumbnailCacheChanged -= OnCacheChanged;
            Browser.PropertyChanged -= OnBrowserPropertyChanged;
        }
    }
}
