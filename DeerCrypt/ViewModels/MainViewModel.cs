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

        public async void OnVaultOpened( string vaultPath )
        {
            IsVaultOpen = true;
            VaultName   = Path.GetFileNameWithoutExtension( vaultPath );
            WindowTitle = $"{VaultName} - DeerCrypt";
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
            IsVaultOpen  = false;
            WindowTitle  = "DeerCrypt";
            VaultName    = string.Empty;
            VaultBrowser = null;
        }

        /// <summary>
        /// Locks the vault immediately: closes it, zeroes the key, and closes viewers.
        /// Only asks first when files are open in external apps, since those edits would be lost.
        /// </summary>
        [RelayCommand]
        private async Task LockVaultAsync( )
        {
            if( !IsVaultOpen ) return;

            if( VaultBrowser?.HasCheckedOutFiles == true )
            {
                if( !await ConfirmCloseAsync( ) ) return;
            }

            await _vaultService.CloseAsync( );
            IsVaultOpen  = false;
            WindowTitle  = "DeerCrypt";
            VaultName    = string.Empty;
            VaultBrowser = null;
        }

        /// <summary>Opens a vault file dropped onto the welcome screen (asks for its password).</summary>
        public Task OpenDroppedVaultAsync( string path ) => OpenRecentAsync( path );

        // Appearance 

        public bool IsThemeSystem => App.SettingsService.Settings.Theme is not ( "Dark" or "Light" );
        public bool IsThemeDark   => App.SettingsService.Settings.Theme == "Dark";
        public bool IsThemeLight  => App.SettingsService.Settings.Theme == "Light";
        public bool IsCompact     => App.SettingsService.Settings.Density == "Compact";

        [RelayCommand]
        private void SetTheme( string theme )
        {
            App.SettingsService.Settings.Theme = theme;
            App.SettingsService.Save( );
            ThemeService.ApplyTheme( theme );
            OnPropertyChanged( nameof( IsThemeSystem ) );
            OnPropertyChanged( nameof( IsThemeDark ) );
            OnPropertyChanged( nameof( IsThemeLight ) );
        }

        [RelayCommand]
        private void ToggleCompact( )
        {
            string density = IsCompact ? "Comfortable" : "Compact";
            App.SettingsService.Settings.Density = density;
            App.SettingsService.Save( );
            ThemeService.ApplyDensity( density );
            OnPropertyChanged( nameof( IsCompact ) );
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
