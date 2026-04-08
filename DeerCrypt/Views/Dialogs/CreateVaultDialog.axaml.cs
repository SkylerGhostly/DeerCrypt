using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.ViewModels.Dialogs;
using System;

namespace DeerCrypt.Views.Dialogs
{
    public partial class CreateVaultDialog : Window
    {
        public CreateVaultDialog( )
        {
            InitializeComponent( );
        }

        private void OnCancelClicked( object? sender, RoutedEventArgs e )
        {
            Close( );
        }

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );

            if( DataContext is CreateVaultViewModel vm )
            {
                // Watch for successful creation and close the dialog
                vm.PropertyChanged += ( _, args ) =>
                {
                    if( args.PropertyName == nameof( CreateVaultViewModel.CreatedVaultPath )
                        && vm.CreatedVaultPath != null )
                        Close( vm.CreatedVaultPath );
                };

                // Wire up the browse command here since it needs the window reference
                vm.BrowseCommand = new AsyncRelayCommand( async ( ) =>
                {
                    var files = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title           = "Create Vault",
                        SuggestedFileName = "MyVault",
                        FileTypeChoices =
                        [
                            new FilePickerFileType("DeerCrypt Vault") { Patterns = ["*.dcv"] }
                        ]
                    });

                    if( files?.Path.LocalPath is string path )
                        vm.VaultPath = path.EndsWith( ".dcv" ) ? path : path + ".dcv";
                } );
            }
        }
    }
}