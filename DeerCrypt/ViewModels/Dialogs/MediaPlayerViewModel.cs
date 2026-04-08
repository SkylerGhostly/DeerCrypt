using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Dialogs
{
    /// <summary>
    /// ViewModel for <see cref="DeerCrypt.Views.Dialogs.MediaPlayerWindow"/>.
    ///
    /// Owns the LibVLC object chain: <see cref="StreamMediaInput"/> →
    /// <see cref="LibVLCSharp.Shared.Media"/> → <see cref="LibVLCSharp.Shared.MediaPlayer"/>.
    /// Also owns the underlying <see cref="System.IO.Stream"/> that backs the stream input.
    ///
    /// Dispose via <see cref="Dispose"/> when the window closes.
    /// </summary>
    public sealed partial class MediaPlayerViewModel : ObservableObject, IDisposable
    {
        #region Fields

        private readonly System.IO.Stream _vaultStream;
        private readonly StreamMediaInput _mediaInput;
        private readonly Media            _media;

        // Set by the View when the user starts dragging the seek slider, so that
        // the position timer doesn't fight the thumb position.
        private volatile bool _isSeeking;
        private bool          _disposed;

        // Cached media duration in ms.  Populated from both LengthChanged (for
        // fast initial display) and by the position timer on each tick.
        private long _lengthMs;

        // DispatcherTimer that polls MediaPlayer.Time / .Position every 200 ms
        // while the player is in Playing state.  We poll rather than relying on
        // TimeChanged / PositionChanged events because those event args can carry
        // incorrect values when the MP4 container's timescale metadata is wrong
        // (e.g. mvhd reports 4 s but actual media is 9 s) - the native property
        // getters (libvlc_media_player_get_time / _get_position) are always accurate.
        private DispatcherTimer? _positionTimer;

        // Store delegates so we can unsubscribe properly in Dispose.
        private readonly EventHandler<MediaPlayerLengthChangedEventArgs> _onLengthChanged;
        private readonly EventHandler<EventArgs>                          _onPlaying;
        private readonly EventHandler<EventArgs>                          _onPaused;
        private readonly EventHandler<EventArgs>                          _onStopped;
        private readonly EventHandler<EventArgs>                          _onEndReached;


        // Guard so we only inspect VideoTrack once (it may not be set yet on the
        // first Playing event for some container formats).
        private bool _audioOnlyChecked;

        #endregion

        #region Observable Properties

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( PlayPauseGlyph ) )]
        public partial bool IsPlaying { get; set; }

        /// <summary>Seek-bar position 0.0–1.0. Updated by the timer; set externally via <see cref="EndSeek"/>.</summary>
        [ObservableProperty]
        public partial double SeekPosition { get; set; }

        [ObservableProperty]
        public partial string CurrentTimeText { get; set; } = "0:00";

        [ObservableProperty]
        public partial string TotalTimeText { get; set; } = "--:--";

        /// <summary>Volume 0–150. 100 = 100 %, values above 100 amplify.</summary>
        [ObservableProperty]
        public partial int Volume { get; set; } = 100;

        [ObservableProperty]
        public partial bool IsRepeat { get; set; }

        /// <summary>
        /// True when VLC confirms the media has no video track (audio-only file).
        /// Controls the audio placeholder panel's visibility via XAML binding.
        /// </summary>
        [ObservableProperty]
        public partial bool IsAudioOnly { get; set; }

        /// <summary>
        /// Album art extracted from the media's embedded tags, or null if none is present.
        /// Populated asynchronously shortly after construction via <see cref="StartAlbumArtParse"/>.
        /// </summary>
        [ObservableProperty]
        public partial Bitmap? AlbumArt { get; set; }

        #endregion

        #region Public Read-Only

        /// <summary>Exposed to the View so it can be assigned to VideoView.MediaPlayer.</summary>
        public MediaPlayer MediaPlayer { get; }

        public string Title { get; }

        /// <summary>Play or pause glyph, updates when <see cref="IsPlaying"/> changes.</summary>
        public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";

        #endregion

        #region Constructor

        public MediaPlayerViewModel( LibVLC libVLC, System.IO.Stream stream, string title )
        {
            _vaultStream = stream;

            // Read embedded artwork via TagLib before VLC touches the stream.
            // TagLib's StreamFileAbstraction does not close the stream when done.
            AlbumArt = TryExtractAlbumArt( stream, title );
            stream.Seek( 0, SeekOrigin.Begin );   // reset for VLC
            TagLib.Tag? tag = TryExtractTag( stream, title );
            if( tag != null && !string.IsNullOrWhiteSpace( tag.Title ) )
            {
                Title = $"{tag.FirstAlbumArtist} - {tag.Title}";
                if( !string.IsNullOrWhiteSpace( tag.Album ) )
                    Title = tag.Album + $" • {Title}";
            }
            else
                Title = title;

            _mediaInput = new StreamMediaInput( stream );
            _media       = new Media( libVLC, _mediaInput );
            MediaPlayer  = new MediaPlayer( _media );

            MediaPlayer.Volume = Volume;

            // LengthChanged: fire-and-forget update of TotalTimeText so the
            // duration label appears as soon as VLC determines it, without waiting
            // for the first timer tick.  Also cache _lengthMs for EndSeek.
            _onLengthChanged = ( _, e ) =>
            {
                _lengthMs = e.Length;                       // VLC thread - set atomically
                Post( ( ) => TotalTimeText = FormatMs( e.Length ) );
            };

            // Playing: transition UI state and start the position timer.
            // The timer is the sole driver of SeekPosition and CurrentTimeText
            // during playback - it polls MediaPlayer.Time / .Position / .Length
            // directly so it is immune to any event-arg marshaling inaccuracies.
            _onPlaying = ( _, _ ) => Post( ( ) =>
            {
                IsPlaying = true;
                EnsureTimerRunning( );

                // Sync duration immediately in case LengthChanged already fired.
                long len = MediaPlayer.Length;
                if( len > 0 && _lengthMs == 0 )
                {
                    _lengthMs = len;
                    TotalTimeText = FormatMs( len );
                }

                // Detect audio-only files on the first Playing event.
                // VideoTrack is -1 when there is no video track.
                if( !_audioOnlyChecked )
                {
                    _audioOnlyChecked = true;
                    IsAudioOnly = MediaPlayer.VideoTrack < 0;
                }
            } );

            _onPaused = ( _, _ ) => Post( ( ) =>
            {
                IsPlaying = false;
                _positionTimer?.Stop( );
                SyncPosition( );        // one last sync so UI reflects the paused position
            } );

            _onStopped = ( _, _ ) => Post( ( ) =>
            {
                IsPlaying = false;
                _positionTimer?.Stop( );
            } );

            _onEndReached = ( _, _ ) =>
            {
                Post( ( ) =>
                {
                    IsPlaying = false;
                    _positionTimer?.Stop( );

                    // Pin the seek bar and time display at the end.
                    if( _lengthMs > 0 )
                    {
                        SeekPosition    = 1.0;
                        CurrentTimeText = TotalTimeText;
                    }
                } );

                if( IsRepeat )
                {
                    // Must not call Stop/Play from the EndReached callback thread.
                    // Stop() is required before Play() - calling Play() alone from
                    // VLC's Ended state does not restart the media in this version.
                    Task.Run( ( ) =>
                    {
                        MediaPlayer.Stop( );
                        MediaPlayer.Play( );
                    } );
                }
            };

            MediaPlayer.LengthChanged += _onLengthChanged;
            MediaPlayer.Playing       += _onPlaying;
            MediaPlayer.Paused        += _onPaused;
            MediaPlayer.Stopped       += _onStopped;
            MediaPlayer.EndReached    += _onEndReached;
        }

        #endregion

        #region Commands

        [RelayCommand]
        private void PlayPause( )
        {
            if( MediaPlayer.IsPlaying )
            {
                MediaPlayer.Pause( );
            }
            else if( MediaPlayer.State == VLCState.Ended )
            {
                // After the video has finished, Play() alone does not restart it -
                // VLC requires Stop() first to reset from Ended back to Stopped state.
                Task.Run( ( ) =>
                {
                    MediaPlayer.Stop( );
                    MediaPlayer.Play( );
                } );
            }
            else
            {
                MediaPlayer.Play( );
            }
        }

        #endregion

        #region Seek Bar Interaction (called by code-behind)

        /// <summary>Called when the user starts dragging the seek slider.</summary>
        public void BeginSeek( ) => _isSeeking = true;

        /// <summary>
        /// Called when the user releases the seek slider.
        /// Seeks the player to <paramref name="position"/> (0.0–1.0) and resumes
        /// timer-driven updates.
        /// </summary>
        public void EndSeek( double position )
        {
            double clamped = Math.Clamp( position, 0.0, 1.0 );
            if( _lengthMs > 0 )
                MediaPlayer.Time = (long)( clamped * _lengthMs );
            else
                MediaPlayer.Position = (float)clamped;
            _isSeeking = false;
        }

        #endregion

        #region Property Hooks

        partial void OnVolumeChanged( int value )
        {
            if( !_disposed )
                MediaPlayer.Volume = value;
        }

        #endregion

        #region Timer

        private void EnsureTimerRunning( )
        {
            // Must be called from the UI thread (inside a Post() callback).
            if( _disposed ) return;
            if( _positionTimer == null )
            {
                _positionTimer = new DispatcherTimer( DispatcherPriority.Normal )
                {
                    Interval = TimeSpan.FromMilliseconds( 200 )
                };
                _positionTimer.Tick += OnPositionTimerTick;
            }
            _positionTimer.Start( );
        }

        private void OnPositionTimerTick( object? sender, EventArgs e )
        {
            if( _disposed ) return;
            SyncPosition( );
        }

        /// <summary>
        /// Reads the current playback position directly from native VLC properties and
        /// updates <see cref="SeekPosition"/> and <see cref="CurrentTimeText"/>.
        ///
        /// Using property getters rather than event args makes this immune to container
        /// timescale metadata errors that can cause TimeChanged / PositionChanged events
        /// to carry incorrect values.
        /// </summary>
        private void SyncPosition( )
        {
            // Re-check length on each tick - VLC can update it as it parses the media.
            long len = MediaPlayer.Length;
            if( len > 0 && len != _lengthMs )
            {
                _lengthMs     = len;
                TotalTimeText = FormatMs( len );
            }

            if( _isSeeking ) return;

            long  t   = MediaPlayer.Time;       // ms; -1 if unavailable
            float pos = MediaPlayer.Position;   // 0–1; -1 if unavailable

            if( t >= 0 )
                CurrentTimeText = FormatMs( t );

            if( _lengthMs > 0 && t >= 0 )
                SeekPosition = Math.Clamp( (double)t / _lengthMs, 0.0, 1.0 );
            else if( pos >= 0 )
                SeekPosition = Math.Clamp( pos, 0.0, 1.0 );
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Subscribes to <see cref="Media.MetaChanged"/> so that album art is loaded
        /// the moment VLC updates the ArtworkURL metadata field during normal demuxing.
        /// This works for stream-based inputs (MemoryStream / VaultReadStream) where
        /// <see cref="Media.Parse"/> with ParseLocal is skipped by VLC.
        /// </summary>
        /// <summary>
        /// Uses TagLib to extract the first embedded picture from the stream.
        /// Seeks back to 0 automatically via the caller.  Returns null if no
        /// artwork is found or the stream/format is not supported.
        /// </summary>
        private static Bitmap? TryExtractAlbumArt( System.IO.Stream stream, string title )
        {
            try
            {   
                stream.Seek( 0, SeekOrigin.Begin );
                using var file = TagLib.File.Create( new StreamAbstraction( title, stream ) );
                var pic = file.Tag.Pictures.FirstOrDefault( );
                if( pic == null ) return null;
                using var ms = new MemoryStream( pic.Data.Data );
                return new Bitmap( ms );
            }
            catch { return null; }
        }

        private static TagLib.Tag? TryExtractTag( System.IO.Stream stream, string title )
        {
            stream.Seek( 0, SeekOrigin.Begin );
            try
            {
                using var file = TagLib.File.Create( new StreamAbstraction( title, stream ) );
                return file.Tag;
            }
            catch( TagLib.UnsupportedFormatException ) { }

            return null;
        }

        /// <summary>
        /// Minimal TagLib IFileAbstraction wrapper around an existing stream.
        /// CloseStream is intentionally a no-op so TagLib never closes our stream.
        /// </summary>
        private sealed class StreamAbstraction( string name, System.IO.Stream stream ) : TagLib.File.IFileAbstraction
        {
            public string Name { get; } = name;
            public System.IO.Stream   ReadStream  => stream;
            public System.IO.Stream   WriteStream => stream;
            public void CloseStream( System.IO.Stream stream ) { }
        }

        private static string FormatMs( long ms )
        {
            if( ms < 0 ) return "--:--";
            var ts = TimeSpan.FromMilliseconds( ms );
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private static void Post( Action action ) => Dispatcher.UIThread.Post( action );

        #endregion

        #region IDisposable

        public void Dispose( )
        {
            if( _disposed ) return;
            _disposed = true;

            // Stop the timer before unsubscribing so no tick fires during teardown.
            _positionTimer?.Stop( );

            // Release the album art bitmap immediately (safe on any thread).
            AlbumArt?.Dispose( );
            AlbumArt = null;

            // Unsubscribe before Stop to prevent callbacks firing during teardown.
            MediaPlayer.LengthChanged -= _onLengthChanged;
            MediaPlayer.Playing       -= _onPlaying;
            MediaPlayer.Paused        -= _onPaused;
            MediaPlayer.Stopped       -= _onStopped;
            MediaPlayer.EndReached    -= _onEndReached;

            // VLC's native teardown must not run on the UI thread - doing so while
            // VLC's internal threads still hold references causes ExecutionEngineException.
            var mp    = MediaPlayer;
            var media = _media;
            var input = _mediaInput;
            var vs    = _vaultStream;
            Task.Run( ( ) =>
            {
                try { mp.Stop( );       } catch { }
                try { mp.Dispose( );    } catch { }
                try { media.Dispose( ); } catch { }
                try { input.Dispose( ); } catch { }
                try { vs.Dispose( );    } catch { }
            } );
        }

        #endregion
    }
}
