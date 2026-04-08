using Avalonia.Controls;
using Avalonia.Interactivity;
using DeerCrypt.ViewModels.Dialogs;

namespace DeerCrypt.Views.Dialogs
{
    public partial class ProgressDialog : Window
    {
        public ProgressDialog( )
        {
            InitializeComponent( );
        }

        private void OnCancelClicked( object? sender, RoutedEventArgs e )
        {
            if( DataContext is ProgressDialogViewModel vm )
                vm.Cancel( );
        }

        // Prevent the user from closing the window manually during operation
        protected override void OnClosing( WindowClosingEventArgs e )
        {
            if( DataContext is ProgressDialogViewModel { IsComplete: false } vm && !vm.IsCancelled )
                e.Cancel = true;

            base.OnClosing( e );
        }
    }
}