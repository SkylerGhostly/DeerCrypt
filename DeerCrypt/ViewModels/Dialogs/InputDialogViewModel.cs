using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace DeerCrypt.ViewModels.Dialogs
{
    /// <summary>
    /// General-purpose single-input dialog ViewModel.
    /// Reusable for folder names, rename, and anything else
    /// that needs one text input with validation.
    /// </summary>
    public partial class InputDialogViewModel : ObservableObject
    {
        [ObservableProperty]
        public partial string Title { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Prompt { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Placeholder { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ConfirmText { get; set; } = "OK";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( ConfirmCommand ) )]
        public partial string Value { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ErrorMessage { get; set; } = string.Empty;

        // Validator function - set by the caller to add custom validation
        public Func<string, string?>? Validator { get; set; }

        private string? _confirmedValue;
        public string? ConfirmedValue
        {
            get => _confirmedValue;
            private set => SetProperty( ref _confirmedValue, value );
        }

        private bool CanConfirm => !string.IsNullOrWhiteSpace( Value );

        [RelayCommand( CanExecute = nameof( CanConfirm ) )]
        private void Confirm( )
        {
            ErrorMessage = string.Empty;

            // Run the validator if one was provided
            if( Validator != null )
            {
                string? error = Validator(Value.Trim());
                if( error != null )
                {
                    ErrorMessage = error;
                    return;
                }
            }

            ConfirmedValue = Value.Trim( );
        }
    }
}