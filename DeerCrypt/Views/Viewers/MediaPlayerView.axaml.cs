using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeerCrypt.ViewModels.Viewers;
using LibVLCSharp.Avalonia;
using System;

namespace DeerCrypt.Views.Viewers
{
    /// <summary>
    /// Hosts VLC's native video surface for <see cref="MediaDocumentViewModel"/>. The surface is
    /// re-attached whenever the document switches tracks, and released when this view is torn down
    /// (the document pauses video before that happens, so VLC never plays without a surface).
    /// </summary>
    public partial class MediaPlayerView : UserControl
    {
        private MediaDocumentViewModel? _doc;
        private readonly VideoView      _videoView;
        private readonly Slider         _seekSlider;

        public MediaPlayerView( )
        {
            InitializeComponent( );
            _videoView  = this.FindControl<VideoView>( "VideoView" )!;
            _seekSlider = this.FindControl<Slider>( "SeekSlider" )!;

            _seekSlider.AddHandler( PointerPressedEvent,  ( _, _ ) => _doc?.Session?.BeginSeek( ), RoutingStrategies.Tunnel );
            _seekSlider.AddHandler( PointerReleasedEvent, ( _, _ ) => _doc?.Session?.EndSeek( _seekSlider.Value ), RoutingStrategies.Tunnel );

            this.FindControl<Slider>( "VolumeSlider" )!.PointerWheelChanged += ( _, e ) =>
            {
                if( _doc == null ) return;
                _doc.Volume = Math.Clamp( _doc.Volume + ( e.Delta.Y > 0 ? 5 : -5 ), 0, 150 );
                e.Handled = true;
            };

            // Clicking the audio "now playing" area toggles playback
            this.FindControl<Panel>( "Stage" )!.PointerReleased += ( _, e ) =>
            {
                if( _doc?.Session?.IsAudioOnly == true && e.InitialPressMouseButton == MouseButton.Left )
                    _doc.Session.PlayPause( );
            };

            AddHandler( PointerPressedEvent, ( _, _ ) => Focus( ), RoutingStrategies.Tunnel, handledEventsToo: true );

            // Tunnel so Space plays/pauses even when a toolbar button last had focus
            AddHandler( KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel );
        }

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );
            if( _doc != null ) _doc.PropertyChanged -= OnDocPropertyChanged;
            _doc = DataContext as MediaDocumentViewModel;
            if( _doc != null ) _doc.PropertyChanged += OnDocPropertyChanged;
        }

        protected override void OnAttachedToVisualTree( VisualTreeAttachmentEventArgs e )
        {
            base.OnAttachedToVisualTree( e );
            AttachSession( );
            Dispatcher.UIThread.Post( ( ) => Focus( ), DispatcherPriority.Loaded );
        }

        protected override void OnDetachedFromVisualTree( VisualTreeAttachmentEventArgs e )
        {
            // Release the native surface before it is destroyed
            _videoView.MediaPlayer = null;
            base.OnDetachedFromVisualTree( e );
        }

        private void OnDocPropertyChanged( object? sender, System.ComponentModel.PropertyChangedEventArgs e )
        {
            if( e.PropertyName == nameof( MediaDocumentViewModel.Session ) ) AttachSession( );
        }

        private void AttachSession( )
        {
            if( _doc == null || VisualRoot == null ) return;
            _videoView.MediaPlayer = _doc.Session?.MediaPlayer;
            _doc.OnSessionAttached( );
        }

        private void OnTunnelKeyDown( object? sender, KeyEventArgs e )
        {
            if( e.KeyModifiers.HasFlag( KeyModifiers.Control ) || e.KeyModifiers.HasFlag( KeyModifiers.Alt ) ) return;
            if( _doc?.Session is not { } s ) return;

            switch( e.Key )
            {
                case Key.Space or Key.K: s.PlayPause( ); break;
                case Key.Left:           s.SkipBy( -5_000 ); break;
                case Key.Right:          s.SkipBy( +5_000 ); break;
                case Key.J:              s.SkipBy( -10_000 ); break;
                case Key.L:              s.SkipBy( +10_000 ); break;
                case Key.Up:             _doc.Volume = Math.Min( 150, _doc.Volume + 5 ); break;
                case Key.Down:           _doc.Volume = Math.Max( 0, _doc.Volume - 5 ); break;
                case Key.M:              _doc.IsMuted = !_doc.IsMuted; break;
                case Key.F:              _doc.RequestFullscreen( ); break;
                case Key.N:              if( _doc.NextCommand.CanExecute( null ) ) _doc.NextCommand.Execute( null ); break;
                case Key.P:              if( _doc.PreviousCommand.CanExecute( null ) ) _doc.PreviousCommand.Execute( null ); break;
                default: return;
            }
            e.Handled = true;
        }
    }
}
