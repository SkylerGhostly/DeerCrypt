using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Services;
using DeerCryptLib.Vault;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Dialogs
{
    public partial class OpenVaultViewModel( VaultService vaultService, string initialPath = "" ) : ObservableObject
    {
        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( OpenCommand ) )]
        public partial string VaultPath { get; set; } = initialPath;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( OpenCommand ) )]
        public partial string Password { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ErrorMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        /// <summary>
        /// Set to the vault path on successful open,
        /// the View closes when this is non-null.
        /// </summary>

        private string? _openedVaultPath;
        public string? OpenedVaultPath
        {
            get => _openedVaultPath;
            private set => SetProperty( ref _openedVaultPath, value );
        }

        [ObservableProperty]
        public partial bool ShowPassword { get; set; }
        public IAsyncRelayCommand? BrowseCommand { get; set; }

        private bool CanOpen =>
            !string.IsNullOrWhiteSpace( VaultPath ) &&
            !string.IsNullOrWhiteSpace( Password ) &&
            !IsBusy;

        [RelayCommand( CanExecute = nameof( CanOpen ) )]
        private async Task OpenAsync( CancellationToken cancellationToken )
        {
            ErrorMessage = string.Empty;

            try
            {
                IsBusy = true;
                await vaultService.OpenAsync( VaultPath, Password, cancellationToken );
                OpenedVaultPath = VaultPath;
            }
            catch( VaultNotFoundException )
            {
                ErrorMessage = "Vault file not found. Please check the path.";
            }
            catch( VaultPasswordException )
            {
                ErrorMessage = "Incorrect password.";
            }
            catch( VaultCorruptException )
            {
                ErrorMessage = "The vault file appears to be corrupt or is an unsupported version.";
            }
            catch( Exception ex )
            {
                ErrorMessage = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}