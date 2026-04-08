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

        public async void OnVaultOpened( string vaultPath )
        {
            IsVaultOpen = true;
            WindowTitle = $"DeerCrypt - {Path.GetFileNameWithoutExtension( vaultPath )}";
            VaultBrowser = new VaultBrowserViewModel( _vaultService );

            _recentVaultsService.Add( vaultPath );
            SyncRecentVaults( );

            await VaultBrowser.LoadAsync( );
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
            var dialog = new CreateVaultDialog
            {
                DataContext = new CreateVaultViewModel( _vaultService )
            };

            var result = await dialog.ShowDialog<string?>( GetMainWindow( ) ?? throw new InvalidOperationException( "No main window" ) );
            if( result != null )
                OnVaultOpened( result );
        }

        [RelayCommand]
        private async Task OpenVaultAsync( )
        {
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

        [RelayCommand]
        private async Task CloseVaultAsync( )
        {
            if( !await ConfirmCloseAsync( ) ) return;

            await _vaultService.CloseAsync( );
            IsVaultOpen = false;
            WindowTitle = "DeerCrypt";
            VaultBrowser = null;
        }

        private async Task<bool> ConfirmCloseAsync( )
        {
            if( !IsVaultOpen ) return true;

            string detail = VaultBrowser?.HasCheckedOutFiles == true
                ? "Warning: files are currently open for editing. Unsaved changes will be lost."
                : "The vault will be locked.";

            var confirmVm = new ConfirmDialogViewModel
            {
                Title       = "Close Vault",
                Message     = "Are you sure you want to close the vault?",
                Detail      = detail,
                ConfirmText = "Close",
                CancelText  = "Cancel"
            };

            bool? result = await new ConfirmDialog { DataContext = confirmVm }
                .ShowDialog<bool?>( GetMainWindow( ) ?? throw new InvalidOperationException( "No main window" ) );

            if( result == true && VaultBrowser != null )
                await VaultBrowser.CloseAllCheckedOutFilesAsync( );

            return result == true;
        }

        private static Window? GetMainWindow( ) =>
            ( Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime )
                ?.MainWindow;
    }
}
