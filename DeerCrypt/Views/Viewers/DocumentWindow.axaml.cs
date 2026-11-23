using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeerCrypt.ViewModels.Viewers;
using System;

namespace DeerCrypt.Views.Viewers
{
    /// <summary>
    /// A document popped out of the tab strip into its own window. Closing the window closes the
    /// document (asking about unsaved edits); "Back to tabs" hands it back to the main window.
    /// </summary>
    public partial class DocumentWindow : Window
    {
        private readonly DocumentViewModel?          _doc;
        private readonly Action<DocumentViewModel>?  _onDock;
        private readonly Action<DocumentViewModel>?  _onClosed;
        private bool _ownerClosing;

        /// <summary>Popped out only to go full screen: leaving full screen puts it back in its tab.</summary>
        public bool DockWhenLeavingFullscreen { get; set; }
        private bool _confirmed;

        public DocumentWindow( )
        {
            InitializeComponent( );
        }

        public DocumentWindow( DocumentViewModel doc, Action<DocumentViewModel> onDock, Action<DocumentViewModel> onClosed )
        {
            _doc      = doc;
            _onDock   = onDock;
            _onClosed = onClosed;
            DataContext = doc;
            InitializeComponent( );

            // Ctrl+W closes the window, like a tab (asks first about unsaved edits). Tunnel, so a
            // focused control inside can't swallow it.
            AddHandler( KeyDownEvent, ( _, e ) =>
            {
                if( e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control )
                {
                    e.Handled = true;
                    Close( );
                }
            }, RoutingStrategies.Tunnel );
            PropertyChanged += ( _, e ) =>
            {
                if( e.Property != WindowStateProperty ) return;
                this.FindControl<Border>( "Header" )!.IsVisible = WindowState != WindowState.FullScreen;
                if( DockWhenLeavingFullscreen && e.OldValue is WindowState.FullScreen && WindowState != WindowState.FullScreen && _doc != null )
                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) => _onDock?.Invoke( _doc ) );
            };
        }

        /// <summary>Closed by the main window (docking, or the vault locking): no questions asked.</summary>
        public void CloseWithoutPrompt( )
        {
            _ownerClosing = true;
            Close( );
        }

        public void ToggleFullscreen( ) =>
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

        protected override void OnKeyDown( KeyEventArgs e )
        {
            base.OnKeyDown( e );
            if( e.Handled ) return;

            if( e.Key == Key.F11 )
            {
                ToggleFullscreen( );
                e.Handled = true;
            }
            else if( e.Key == Key.Escape && WindowState == WindowState.FullScreen )
            {
                WindowState = WindowState.Normal;
                e.Handled = true;
            }
        }

        private void OnFullscreenClicked( object? sender, RoutedEventArgs e ) => ToggleFullscreen( );

        private void OnDockClicked( object? sender, RoutedEventArgs e )
        {
            if( _doc != null ) _onDock?.Invoke( _doc );
        }

        protected override async void OnClosing( WindowClosingEventArgs e )
        {
            if( _ownerClosing || _confirmed || _doc == null )
            {
                base.OnClosing( e );
                return;
            }

            e.Cancel = true;
            if( await _doc.ConfirmCloseAsync( this ) )
            {
                _confirmed = true;
                Close( );
            }
        }

        protected override void OnClosed( EventArgs e )
        {
            base.OnClosed( e );
            if( !_ownerClosing && _doc != null ) _onClosed?.Invoke( _doc );
        }
    }
}
