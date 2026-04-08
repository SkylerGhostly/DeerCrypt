using Avalonia.Controls;
using Avalonia.Interactivity;
using DeerCrypt.ViewModels;
using DeerCrypt.ViewModels.Dialogs;
using System;
using System.Threading.Tasks;

namespace DeerCrypt.Views.Dialogs
{
    public partial class MoveDialog : Window
    {
        public MoveDialog( )
        {
            InitializeComponent( );
        }

        protected override async void OnOpened( EventArgs e )
        {
            base.OnOpened( e );

            if( DataContext is MoveDialogViewModel vm )
            {
                vm.DialogCloseRequested += ( _, result ) => Close( result );
                await vm.InitializeAsync( );
            }

            // Wire double-click to navigate into the selected folder
            var list = this.FindControl<ListBox>( "DirList" );
            list?.DoubleTapped += async ( _, _ ) =>
            {
                if( list.SelectedItem is VaultItemViewModel item &&
                    DataContext is MoveDialogViewModel vm2 )
                {
                    await vm2.NavigateIntoCommand.ExecuteAsync( item );
                }
            };
        }

        private void OnCancelClicked( object? sender, RoutedEventArgs e ) => Close( false );
    }
}
