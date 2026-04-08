using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Services;
using DeerCryptLib.Vault;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Dialogs
{
    public partial class CreateVaultViewModel( VaultService vaultService ) : ObservableObject
    {
        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( CreateCommand ) )]
        public partial string VaultPath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Password { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ConfirmPassword { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ErrorMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial bool ShowPassword { get; set; }

        private string? _createdVaultPath;
        public string? CreatedVaultPath
        {
            get => _createdVaultPath;
            private set => SetProperty( ref _createdVaultPath, value );
        }

        public IAsyncRelayCommand? BrowseCommand { get; set; }

        // Password requirement checks 
        public bool PasswordHasLength  => Password.Length >= 8;
        public bool PasswordHasUpper   => Password.Any( char.IsUpper );
        public bool PasswordHasDigit   => Password.Any( char.IsDigit );
        public bool PasswordHasSpecial => Password.Any( c => !char.IsLetterOrDigit( c ) );
        public bool PasswordsMatch     => Password == ConfirmPassword;
        public bool ConfirmNotEmpty    => ConfirmPassword.Length > 0;

        public int PasswordStrength =>
            ( PasswordHasLength  ? 1 : 0 ) +
            ( PasswordHasUpper   ? 1 : 0 ) +
            ( PasswordHasDigit   ? 1 : 0 ) +
            ( PasswordHasSpecial ? 1 : 0 );

        public double PasswordStrengthPercent => PasswordStrength * 25.0;

        public string PasswordStrengthLabel => PasswordStrength switch
        {
            1 => "Weak",
            2 => "Fair",
            3 => "Good",
            4 => "Strong",
            _ => ""
        };

        public IBrush PasswordStrengthBrush => PasswordStrength switch
        {
            1 => new SolidColorBrush( Color.Parse( "#E05252" ) ),
            2 => new SolidColorBrush( Color.Parse( "#FF9800" ) ),
            3 => new SolidColorBrush( Color.Parse( "#8BC34A" ) ),
            4 => new SolidColorBrush( Color.Parse( "#4CAF50" ) ),
            _ => Brushes.Transparent
        };

        public IBrush PasswordMatchBrush =>
            PasswordsMatch
                ? new SolidColorBrush( Color.Parse( "#4CAF50" ) )
                : new SolidColorBrush( Color.Parse( "#E05252" ) );

        public string PasswordMatchText =>
            ConfirmNotEmpty
                ? ( PasswordsMatch ? "Passwords match" : "Passwords do not match" )
                : string.Empty;

        // Partial hooks 
        partial void OnPasswordChanged( string value )
        {
            OnPropertyChanged( nameof( PasswordHasLength ) );
            OnPropertyChanged( nameof( PasswordHasUpper ) );
            OnPropertyChanged( nameof( PasswordHasDigit ) );
            OnPropertyChanged( nameof( PasswordHasSpecial ) );
            OnPropertyChanged( nameof( PasswordStrength ) );
            OnPropertyChanged( nameof( PasswordStrengthPercent ) );
            OnPropertyChanged( nameof( PasswordStrengthLabel ) );
            OnPropertyChanged( nameof( PasswordStrengthBrush ) );
            OnPropertyChanged( nameof( PasswordsMatch ) );
            OnPropertyChanged( nameof( PasswordMatchBrush ) );
            OnPropertyChanged( nameof( PasswordMatchText ) );
            CreateCommand.NotifyCanExecuteChanged( );
        }

        partial void OnConfirmPasswordChanged( string value )
        {
            OnPropertyChanged( nameof( PasswordsMatch ) );
            OnPropertyChanged( nameof( PasswordMatchBrush ) );
            OnPropertyChanged( nameof( PasswordMatchText ) );
            OnPropertyChanged( nameof( ConfirmNotEmpty ) );
            CreateCommand.NotifyCanExecuteChanged( );
        }

        // Command 
        private bool CanCreate =>
            !string.IsNullOrWhiteSpace( VaultPath ) &&
            PasswordStrength == 4 &&
            PasswordsMatch &&
            !IsBusy;

        [RelayCommand( CanExecute = nameof( CanCreate ) )]
        private async Task CreateAsync( CancellationToken cancellationToken )
        {
            ErrorMessage = string.Empty;

            try
            {
                IsBusy = true;
                await vaultService.CreateAsync( VaultPath, Password, cancellationToken );
                CreatedVaultPath = VaultPath;
            }
            catch( VaultOperationException ex )
            {
                ErrorMessage = ex.Message;
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
