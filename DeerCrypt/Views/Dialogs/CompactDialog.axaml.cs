using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DeerCrypt.Views.Dialogs
{
    /// <summary>Asks before compacting. Returns true to start; the work itself runs as a background activity.</summary>
    public partial class CompactDialog : Window
    {
        public CompactDialog( )
        {
            InitializeComponent( );
        }

        private void OnConfirmClicked( object? sender, RoutedEventArgs e ) => Close( true );
        private void OnCancelClicked( object? sender, RoutedEventArgs e ) => Close( false );
    }
}
