using Avalonia.Controls;
using Avalonia.Interactivity;
using DeerCrypt.ViewModels.Dialogs;
using System;

namespace DeerCrypt.Views.Dialogs
{
    public partial class CompactDialog : Window
    {
        public CompactDialog( )
        {
            InitializeComponent( );
        }

        private void OnCancelClicked( object? sender, RoutedEventArgs e )
        {
            Close( false );
        }

        protected override void OnClosing( WindowClosingEventArgs e )
        {
            if( DataContext is CompactDialogViewModel { IsCompacting: true } )
                e.Cancel = true;

            base.OnClosing( e );
        }

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );

            if( DataContext is CompactDialogViewModel vm )
            {
                vm.PropertyChanged += ( _, args ) =>
                {
                    if( args.PropertyName == nameof( CompactDialogViewModel.IsComplete )
                        && vm.IsComplete )
                        Close( true );
                };
            }
        }
    }
}