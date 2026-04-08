using Avalonia.Controls;
using Avalonia.Interactivity;
using DeerCrypt.Services;

namespace DeerCrypt.Views.Dialogs
{
    public partial class OpenWithDialog : Window
    {
        public OpenWithDialog( )
        {
            InitializeComponent( );
        }
        
        public OpenWithDialog( string fileName )
        {
            InitializeComponent( );
            var tb = this.FindControl<TextBlock>( "FileNameText" );
            tb?.Text = $"\"{fileName}\"";
        }

        private void OnMediaPlayerClicked( object? sender, RoutedEventArgs e ) => Close( OpenWithChoice.MediaPlayer );
        private void OnImageViewerClicked( object? sender, RoutedEventArgs e ) => Close( OpenWithChoice.ImageViewer );
        private void OnTextEditorClicked(  object? sender, RoutedEventArgs e ) => Close( OpenWithChoice.TextEditor );
        private void OnExternalClicked(    object? sender, RoutedEventArgs e ) => Close( OpenWithChoice.External );
        private void OnCancelClicked(      object? sender, RoutedEventArgs e ) => Close( OpenWithChoice.None );
    }
}
