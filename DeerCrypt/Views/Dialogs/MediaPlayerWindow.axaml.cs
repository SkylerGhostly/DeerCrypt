using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeerCrypt.ViewModels.Dialogs;
using LibVLCSharp.Avalonia;
using System;

namespace DeerCrypt.Views.Dialogs
{
    public partial class MediaPlayerWindow : Window
    {
        private MediaPlayerViewModel? _vm;
        private VideoView?            _videoView;
        private Slider?               _seekSlider;

        public MediaPlayerWindow( )
        {
            InitializeComponent( );
        }

        // Lifecycle 

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );
            _vm = DataContext as MediaPlayerViewModel;
        }

        protected override void OnOpened( EventArgs e )
        {
            base.OnOpened( e );

            _videoView  = this.FindControl<VideoView>( "VideoView" );
            _seekSlider = this.FindControl<Slider>( "SeekSlider" );

            if( _vm == null ) return;

            // Attach MediaPlayer to the VideoView so VLC renders into this window.
            _videoView?.MediaPlayer = _vm.MediaPlayer;

            // Wire seek-bar pointer events for drag detection.
            if( _seekSlider != null )
            {
                _seekSlider.AddHandler(
                    PointerPressedEvent,
                    OnSeekSliderPointerPressed,
                    RoutingStrategies.Tunnel );

                // Tunnel so we receive the release before the Slider thumb handles it.
                _seekSlider.AddHandler(
                    PointerReleasedEvent,
                    OnSeekSliderPointerReleased,
                    RoutingStrategies.Tunnel );
            }

            // Start playback.  IsAudioOnly is detected in MediaPlayerViewModel._onPlaying.
            _vm.MediaPlayer.Play( );
        }

        protected override void OnClosed( EventArgs e )
        {
            // Detach the native VideoView HWND from the MediaPlayer before VLC teardown.
            // If we don't do this, VLC may attempt to render into a destroying HWND,
            // causing an access violation that surfaces as ExecutionEngineException.
            _videoView?.MediaPlayer = null;

            _vm?.Dispose( );
            base.OnClosed( e );
        }

        // Keyboard shortcuts 

        protected override void OnKeyDown( KeyEventArgs e )
        {
            base.OnKeyDown( e );
            if( _vm == null ) return;

            switch( e.Key )
            {
                case Key.Space:
                    _vm.PlayPauseCommand.Execute( null );
                    e.Handled = true;
                    break;

                case Key.Left:
                    SeekByDelta( -5_000 );
                    e.Handled = true;
                    break;

                case Key.Right:
                    SeekByDelta( +5_000 );
                    e.Handled = true;
                    break;

                case Key.Up:
                    _vm.Volume = Math.Min( 150, _vm.Volume + 5 );
                    e.Handled = true;
                    break;

                case Key.Down:
                    _vm.Volume = Math.Max( 0, _vm.Volume - 5 );
                    e.Handled = true;
                    break;
            }
        }

        // Seek-bar drag handling 

        private void OnSeekSliderPointerPressed( object? sender, PointerPressedEventArgs e )
        {
            _vm?.BeginSeek( );
        }

        private void OnSeekSliderPointerReleased( object? sender, PointerReleasedEventArgs e )
        {
            if( _vm != null && _seekSlider != null )
                _vm.EndSeek( _seekSlider.Value );
        }

        // Helpers 

        private void SeekByDelta( long deltaMs )
        {
            if( _vm == null ) return;
            long current = _vm.MediaPlayer.Time;
            long length  = _vm.MediaPlayer.Length;
            _vm.MediaPlayer.Time = Math.Clamp( current + deltaMs, 0L, length > 0 ? length : 0L );
        }
    }
}
