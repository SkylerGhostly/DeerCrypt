using Avalonia.Controls;
using Avalonia.Interactivity;
using DeerCrypt.ViewModels.Dialogs;
using System;

namespace DeerCrypt.Views.Dialogs
{
    public partial class InputDialog : Window
    {
        public InputDialog( )
        {
            InitializeComponent( );
        }

        protected override void OnOpened( EventArgs e )
        {
            base.OnOpened( e );

            // Focus the input box immediately so the user can type right away
            this.FindControl<TextBox>( "InputBox" )?.Focus( );
        }

        private void OnCancelClicked( object? sender, RoutedEventArgs e )
        {
            Close( null );
        }

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );

            if( DataContext is InputDialogViewModel vm )
            {
                vm.PropertyChanged += ( _, args ) =>
                {
                    if( args.PropertyName == nameof( InputDialogViewModel.ConfirmedValue )
                        && vm.ConfirmedValue != null )
                        Close( vm.ConfirmedValue );
                };
            }
        }
    }
}