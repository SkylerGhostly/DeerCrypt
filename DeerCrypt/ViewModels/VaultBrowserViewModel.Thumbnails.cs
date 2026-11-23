using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Services;
using DeerCryptLib.Vault;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels
{
    /// <summary>
    /// Thumbnails: the mode for this vault (its own choice, or the app default), the privacy
    /// toggle that hides them instantly, and the vault's encrypted cache.
    /// </summary>
    public partial class VaultBrowserViewModel
    {
        private const string SettingThumbnails   = "thumbnails";
        private const string SettingCacheLimitMb = "thumbnail_cache_mb";
        private const string SettingAsked        = "thumbnails_asked";

        /// <summary>
        /// This vault predates thumbnails: it has no choice of its own, no cache, and was never
        /// asked. Thumbnails stay off in it (nothing is written) until its owner decides.
        /// </summary>
        private bool _thumbnailsUndecided;

        public bool ThumbnailsUndecided => _thumbnailsUndecided;

        /// <summary>The vault is undecided and the app default would turn thumbnails on: ask.</summary>
        public bool NeedsThumbnailChoice =>
            _thumbnailsUndecided && ParseThumbnailMode( App.SettingsService.Settings.DefaultThumbnails ) != ThumbnailMode.Off;

        private ThumbnailService? _thumbnails;
        public ThumbnailService Thumbnails => _thumbnails ??= CreateThumbnailService( );

        private ThumbnailService CreateThumbnailService( )
        {
            var service = new ThumbnailService( Vault );
            service.CacheChanged += ( ) => ThumbnailCacheChanged?.Invoke( );
            return service;
        }

        /// <summary>The thumbnail cache grew or shrank (raised on a background thread).</summary>
        public event Action? ThumbnailCacheChanged;

        /// <summary>Thumbnails were switched on (or the cache cleared): the view re-requests what's on screen.</summary>
        public event Action? ThumbnailsReset;

        /// <summary>This vault's own choice ("Off", "Blurred", "On"), or null to follow the app default.</summary>
        [ObservableProperty]
        public partial string? VaultThumbnailSetting { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( ShowThumbnails ) )]
        [NotifyPropertyChangedFor( nameof( BlurThumbnails ) )]
        [NotifyPropertyChangedFor( nameof( ThumbnailsEnabled ) )]
        public partial ThumbnailMode ThumbnailMode { get; private set; }

        public bool ThumbnailsEnabled => ThumbnailMode != ThumbnailMode.Off;

        /// <summary>Privacy toggle (Ctrl+Shift+H): hides every thumbnail and preview at once.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( ShowThumbnails ) )]
        public partial bool ThumbnailsHidden { get; set; }

        public bool ShowThumbnails => ThumbnailMode != ThumbnailMode.Off && !ThumbnailsHidden;
        public bool BlurThumbnails => ThumbnailMode == ThumbnailMode.Blurred;

        [ObservableProperty]
        public partial long ThumbnailCacheLimitBytes { get; private set; } = ThumbnailService.DefaultCacheLimitBytes;

        [RelayCommand]
        private void ToggleThumbnailPrivacy( ) => ThumbnailsHidden = !ThumbnailsHidden;

        public static ThumbnailMode ParseThumbnailMode( string? value ) => value switch
        {
            "On"      => ThumbnailMode.On,
            "Blurred" => ThumbnailMode.Blurred,
            _         => ThumbnailMode.Off
        };

        /// <summary>
        /// Reads this vault's thumbnail settings when it opens. Writes nothing, except to mark a
        /// vault created just now as already settled (it simply follows the app default).
        /// </summary>
        private async Task LoadThumbnailSettingsAsync( bool isNewVault, CancellationToken ct )
        {
            _thumbnailsUndecided = false;
            try
            {
                VaultThumbnailSetting = await Vault.GetSettingAsync( SettingThumbnails, ct );
                string? mb = await Vault.GetSettingAsync( SettingCacheLimitMb, ct );
                if( long.TryParse( mb, out long limitMb ) && limitMb > 0 )
                    ThumbnailCacheLimitBytes = limitMb * 1024 * 1024;

                if( isNewVault )
                {
                    await Vault.SetSettingAsync( SettingAsked, "1", ct );
                }
                else if( VaultThumbnailSetting == null
                         && await Vault.GetSettingAsync( SettingAsked, ct ) == null
                         && ( await Vault.GetThumbnailCacheInfoAsync( ct ) ).Count == 0 )
                {
                    _thumbnailsUndecided = true;
                }
            }
            catch( OperationCanceledException ) { throw; }
            catch { VaultThumbnailSetting = null; }

            Thumbnails.CacheLimitBytes = ThumbnailCacheLimitBytes;
            ApplyThumbnailMode( );
        }

        /// <summary>
        /// Asks whether to use thumbnails in a vault that predates them. "Turn on" and "Keep off"
        /// are saved in the vault; "Not now" saves nothing and asks again next time.
        /// </summary>
        public async Task AskThumbnailChoiceAsync( Avalonia.Controls.Window owner )
        {
            if( !NeedsThumbnailChoice ) return;

            var mode = ParseThumbnailMode( App.SettingsService.Settings.DefaultThumbnails );
            var confirm = new Dialogs.ConfirmDialogViewModel
            {
                Title       = "Thumbnails",
                Message     = "Show thumbnails in this vault?",
                Detail      = "Pictures and album art get small previews in grid view and the details panel" +
                              ( mode == ThumbnailMode.Blurred ? ", blurred until you point at them" : "" ) + ".\n\n" +
                              "To do that, DeerCrypt keeps an encrypted thumbnail cache inside this vault " +
                              $"(up to {Converters.FileSizeConverter.Format( ThumbnailCacheLimitBytes )}). " +
                              "Your files aren't changed, and older versions of DeerCrypt simply ignore the cache.\n\n" +
                              "You can change this, blur thumbnails, or clear the cache any time in Settings.",
                ConfirmText = "Turn on",
                DiscardText = "Keep off",
                CancelText  = "Not now",
                Tone        = Dialogs.DialogTone.Info
            };

            bool? answer = await new Views.Dialogs.ConfirmDialog { DataContext = confirm }.ShowDialog<bool?>( owner );
            if( answer == null ) return; // not now: stays off for this session, asked again next time

            try
            {
                await SetVaultThumbnailSettingAsync( answer == true ? App.SettingsService.Settings.DefaultThumbnails : "Off" );
            }
            catch( ObjectDisposedException ) { }
        }

        /// <summary>Works out the effective mode and switches the service, tile size, and items over to it.</summary>
        public void ApplyThumbnailMode( )
        {
            // Until an older vault's owner has decided, nothing is made (or written)
            var mode = _thumbnailsUndecided
                ? ThumbnailMode.Off
                : ParseThumbnailMode( VaultThumbnailSetting ?? App.SettingsService.Settings.DefaultThumbnails );
            bool wasOn = ThumbnailMode != ThumbnailMode.Off;
            ThumbnailMode = mode;
            bool on = mode != ThumbnailMode.Off;

            Thumbnails.SetEnabled( on );
            ThemeService.SetGridThumbnails( on );

            if( !on )
            {
                foreach( var item in CurrentItems ) item.Thumbnail = null;
                if( InspectedItem != null ) InspectedItem.Thumbnail = null;
            }
            else if( !wasOn )
            {
                RequestVisibleThumbnails( );
            }
        }

        private void RequestVisibleThumbnails( )
        {
            ThumbnailsReset?.Invoke( );
            if( InspectedItem != null ) Thumbnails.Request( InspectedItem );
        }

        partial void OnInspectedItemChanged( VaultItemViewModel? value )
        {
            if( value != null ) Thumbnails.Request( value );
        }

        /// <summary>Stores this vault's thumbnail mode in the vault (null = follow the app default).</summary>
        public async Task SetVaultThumbnailSettingAsync( string? value )
        {
            // Any explicit choice (from the question or from Settings) settles an older vault
            if( _thumbnailsUndecided )
            {
                await Vault.SetSettingAsync( SettingAsked, "1" );
                _thumbnailsUndecided = false;
            }
            else if( value == VaultThumbnailSetting ) return;

            await Vault.SetSettingAsync( SettingThumbnails, value );
            VaultThumbnailSetting = value;
            ApplyThumbnailMode( );
        }

        public async Task SetThumbnailCacheLimitAsync( long bytes )
        {
            if( bytes == ThumbnailCacheLimitBytes ) return;
            await Vault.SetSettingAsync( SettingCacheLimitMb, ( bytes / ( 1024 * 1024 ) ).ToString( ) );
            ThumbnailCacheLimitBytes   = bytes;
            Thumbnails.CacheLimitBytes = bytes;
            await Thumbnails.TrimAsync( );
        }

        public Task<ThumbnailCacheInfo> GetThumbnailCacheInfoAsync( ) => Vault.GetThumbnailCacheInfoAsync( );

        /// <summary>Deletes every stored thumbnail. They're made again as you browse (if thumbnails are on).</summary>
        public async Task ClearThumbnailCacheAsync( )
        {
            Thumbnails.Reset( );
            await Vault.ClearThumbnailCacheAsync( );
            foreach( var item in CurrentItems ) item.Thumbnail = null;
            if( InspectedItem != null ) InspectedItem.Thumbnail = null;
            ThumbnailCacheChanged?.Invoke( );
            _ = RefreshVaultInfoAsync( );
            if( ThumbnailMode != ThumbnailMode.Off ) RequestVisibleThumbnails( );
        }

        public string VaultFilePath => Vault.FilePath;

        public Task<VaultInfo> GetVaultInfoAsync( ) => Vault.GetVaultInfoAsync( );

        /// <summary>Opens the Settings tab (or brings it forward).</summary>
        [RelayCommand]
        private void OpenSettings( )
        {
            foreach( var doc in Documents.Concat( _popouts.Keys ) )
                if( doc is Viewers.SettingsDocumentViewModel ) { ActivateDocument( doc ); return; }
            OpenDocument( new Viewers.SettingsDocumentViewModel( this ) );
        }

        /// <summary>Stops thumbnail work before the vault closes.</summary>
        public void ShutdownThumbnails( ) => _thumbnails?.Dispose( );
    }
}
