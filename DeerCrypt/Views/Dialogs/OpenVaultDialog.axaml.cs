using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.ViewModels.Dialogs;
using System;

namespace DeerCrypt.Views.Dialogs
{
    public partial class OpenVaultDialog : Window
    {
        public OpenVaultDialog( )
        {
            InitializeComponent( );
        }

        protected override void OnOpened( EventArgs e )
        {
            base.OnOpened( e );
            // Path already known (recent vault or dropped file): go straight to the password
            if( DataContext is OpenVaultViewModel { VaultPath.Length: > 0 } )
                this.FindControl<TextBox>( "PasswordBox" )?.Focus( );
        }

        private void OnCancelClicked( object? sender, RoutedEventArgs e )
        {
            Close( );
        }

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );

            if( DataContext is OpenVaultViewModel vm )
            {
                vm.PropertyChanged += ( _, args ) =>
                {
                    if( args.PropertyName == nameof( OpenVaultViewModel.OpenedVaultPath )
                        && vm.OpenedVaultPath != null )
                        Close( vm.OpenedVaultPath );
                };

                vm.BrowseCommand = new AsyncRelayCommand( async ( ) =>
                {
                    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title         = "Choose a vault file",
                        AllowMultiple = false,
                        FileTypeFilter =
                        [
                            new FilePickerFileType("DeerCrypt Vault") { Patterns = ["*.dcv"] }
                        ]
                    });

                    if( files.Count > 0 )
                        vm.VaultPath = files [ 0 ].Path.LocalPath;
                } );
            }
        }
    }
}