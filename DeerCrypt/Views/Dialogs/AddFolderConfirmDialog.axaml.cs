using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DeerCrypt.Views.Dialogs
{
    public partial class AddFolderConfirmDialog : Window
    {
        public AddFolderConfirmDialog( )
        {
            InitializeComponent( );
        }

        private void OnConfirmClicked( object? sender, RoutedEventArgs e ) => Close( true );
        private void OnCancelClicked( object? sender, RoutedEventArgs e ) => Close( false );
    }
}