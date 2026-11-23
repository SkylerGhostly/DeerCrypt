using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DeerCrypt.Views.Dialogs
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog( )
        {
            InitializeComponent( );
        }

        private void OnConfirmClicked( object? sender, RoutedEventArgs e ) => Close( true );
        private void OnCancelClicked( object? sender, RoutedEventArgs e ) =>
            Close( DataContext is DeerCrypt.ViewModels.Dialogs.ConfirmDialogViewModel { HasDiscard: true } ? null : false );

        private void OnDiscardClicked( object? sender, RoutedEventArgs e ) => Close( false );
    }
}