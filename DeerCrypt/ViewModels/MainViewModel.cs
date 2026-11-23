using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Services;
using DeerCrypt.ViewModels.Dialogs;
using DeerCrypt.Views.Dialogs;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly VaultService        _vaultService;
        private readonly RecentVaultsService _recentVaultsService;

        [ObservableProperty]
        public partial bool IsVaultOpen { get; set; }

        [ObservableProperty]
        public partial string WindowTitle { get; set; } = "DeerCrypt";

        [ObservableProperty]
        public partial VaultBrowserViewModel? VaultBrowser { get; set; }

        /// <summary>Display name of the open vault (file name without extension).</summary>
        [ObservableProperty]
        public partial string VaultName { get; set; } = string.Empty;
        public ObservableCollection<RecentVaultEntry> RecentVaults { get; } = [ ];

        public bool HasRecentVaults => RecentVaults.Count > 0;

        public MainViewModel( VaultService vaultService, RecentVaultsService recentVaultsService )
        {
            _vaultService        = vaultService;
            _recentVaultsService = recentVaultsService;

            _recentVaultsService.Load( );
            SyncRecentVaults( );

            RecentVaults.CollectionChanged += ( _, _ ) => OnPropertyChanged( nameof( HasRecentVaults ) );
        }

        // Design-time constructor
        public MainViewModel( ) : this( new VaultService( ), new RecentVaultsService( ) ) { }

        public async void OnVaultOpened( string vaultPath ) => await OpenedAsync( vaultPath, isNewVault: false );

        public async Task OpenedAsync( string vaultPath, bool isNewVault )
        {
            IsVaultOpen = true;
            VaultName   = Path.GetFileNameWithoutExtension( vaultPath );
            WindowTitle = $"{VaultName} - DeerCrypt";
            VaultBrowser = new VaultBrowserViewModel( _vaultService );

            _recentVaultsService.Add( vaultPath );
            SyncRecentVaults( );

            var browser = VaultBrowser;
            await browser.LoadAsync( isNewVault );

            // A vault from before thumbnails: ask before creating a cache in it
            if( browser.NeedsThumbnailChoice && ( browser.OwnerWindow ?? GetMainWindow( ) ) is { } owner && VaultBrowser == browser )
                await browser.AskThumbnailChoiceAsync( owner );
        }

        private void SyncRecentVaults( )
        {
            RecentVaults.Clear( );
            foreach( var entry in _recentVaultsService.Entries )
                RecentVaults.Add( entry );
        }

        [RelayCommand]
        private async Task NewVaultAsync( )
        {
            // Opening another vault closes this one; stop its work first (or back out)
            if( IsVaultOpen && !await CloseCurrentVaultAsync( ) ) return;

            var dialog = new CreateVaultDialog
            {
                DataContext = new CreateVaultViewModel( _vaultService )
            };

            var result = await dialog.ShowDialog<string?>( GetMainWindow( ) ?? throw new InvalidOperationException( "No main window" ) );
            if( result != null )
                await OpenedAsync( result, isNewVault: true );
        }

        [RelayCommand]
        private async Task OpenVaultAsync( )
        {
            // Opening another vault closes this one; stop its work first (or back out)
            if( IsVaultOpen && !await CloseCurrentVaultAsync( ) ) return;

            var dialog = new OpenVaultDialog
            {
                DataContext = new OpenVaultViewModel( _vaultService )
            };

            var result = await dialog.ShowDialog<string?>( GetMainWindow( ) ?? throw new InvalidOperationException( "No main window" ) );
            if( result != null )
                OnVaultOpened( result );
        }

        [RelayCommand]
        private async Task OpenRecentAsync( string path )
        {
            // Opening another vault closes this one; stop its work first (or back out)
            if( IsVaultOpen && !await CloseCurrentVaultAsync( ) ) return;

            var dialog = new OpenVaultDialog
            {
                DataContext = new OpenVaultViewModel( _vaultService, path )
            };

            var result = await dialog.ShowDialog<string?>( GetMainWindow( ) ?? throw new InvalidOperationException( "No main window" ) );
            if( result != null )
                OnVaultOpened( result );
        }

        [RelayCommand]
        private void RemoveRecent( string path )
        {
            _recentVaultsService.Remove( path );
            SyncRecentVaults( );
        }


        /// <summary>
        /// Locks the vault immediately: closes it, zeroes the key, and closes viewers.
        /// Only asks first when files are open in external apps, since those edits would be lost.
        /// </summary>
        /// <summary>
        /// Locks the vault immediately: cancels background work, closes files open in other apps,
        /// zeroes the key, and closes viewers. Only asks first when something would be lost.
        /// </summary>
        [RelayCommand]
        private async Task LockVaultAsync( ) => await CloseCurrentVaultAsync( );

        /// <summary>Used when the window is closing. Returns false if the user chose to keep the app open.</summary>
        public Task<bool> CloseForExitAsync( ) => CloseCurrentVaultAsync( );

        /// <summary>
        /// Stops everything tied to the open vault and closes it. Asks first only when background
        /// work would be cancelled or edits in other apps would be lost. Returns false if the user backs out.
        /// </summary>
        private async Task<bool> CloseCurrentVaultAsync( )
        {
            if( !IsVaultOpen ) return true;

            var browser    = VaultBrowser;
            int running    = browser?.Activities.RunningCount ?? 0;
            bool checkedOut = browser?.HasCheckedOutFiles == true;
            bool unsaved    = browser?.HasUnsavedDocuments == true;

            if( running > 0 || checkedOut || unsaved )
            {
                var lines = new System.Collections.Generic.List<string>( );
                if( running > 0 )
                    lines.Add( $"{running} background task{( running == 1 ? "" : "s" )} will be cancelled. " +
                               "Files that were already finished stay in the vault; the one in progress is rolled back." );
                if( checkedOut )
                    lines.Add( "Files open in other apps will be closed, and changes you haven't saved back will be lost." );
                if( unsaved )
                    lines.Add( "You have unsaved edits in the text editor. They'll be lost." );

                var confirmVm = new ConfirmDialogViewModel
                {
                    Title       = "Lock vault",
                    Message     = "Lock the vault now?",
                    Detail      = string.Join( "\n\n", lines ),
                    ConfirmText = "Lock",
                    CancelText  = "Keep working",
                    Tone        = DialogTone.Warning
                };

                bool? result = await new ConfirmDialog { DataContext = confirmVm }
                    .ShowDialog<bool?>( GetMainWindow( ) ?? throw new InvalidOperationException( "No main window" ) );
                if( result != true ) return false;
            }

            if( browser != null )
            {
                // Close tabs and pop-out windows first: they hold decrypted content and vault streams
                browser.DiscardAllDocuments( );
                browser.ShutdownThumbnails( );
                await browser.Activities.CancelAllAndWaitAsync( );
                await browser.CloseAllCheckedOutFilesAsync( );
            }

            await _vaultService.CloseAsync( );
            IsVaultOpen  = false;
            WindowTitle  = "DeerCrypt";
            VaultName    = string.Empty;
            VaultBrowser = null;
            return true;
        }

        /// <summary>Opens a vault file dropped onto the welcome screen (asks for its password).</summary>
        public Task OpenDroppedVaultAsync( string path ) => OpenRecentAsync( path );

        private static Window? GetMainWindow( ) =>
            ( Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime )
                ?.MainWindow;
    }
}
