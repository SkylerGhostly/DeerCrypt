using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Viewers
{
    /// <summary>
    /// One track's playback: owns the LibVLC chain (stream input → Media → MediaPlayer) and the
    /// decrypted stream behind it. Position is polled from VLC every 200 ms rather than taken from
    /// event args, which can be wrong for MP4s with bad timescale metadata.
    /// </summary>
    public sealed partial class MediaSession : ObservableObject, IDisposable
    {
        private readonly Stream           _vaultStream;
        private readonly StreamMediaInput _mediaInput;
        private readonly Media            _media;

        private volatile bool _isSeeking;
        private bool          _disposed;
        private long          _lengthMs;
        private bool          _audioOnlyChecked;
        private DispatcherTimer? _positionTimer;

        private readonly EventHandler<MediaPlayerLengthChangedEventArgs> _onLengthChanged;
        private readonly EventHandler<EventArgs> _onPlaying;
        private readonly EventHandler<EventArgs> _onPaused;
        private readonly EventHandler<EventArgs> _onStopped;
        private readonly EventHandler<EventArgs> _onEndReached;
        private readonly EventHandler<EventArgs> _onError;

        public MediaPlayer MediaPlayer { get; }

        /// <summary>The file name, or "Artist - Title" when the file has tags.</summary>
        public string DisplayTitle { get; }
        public string TrackTitle   { get; }
        public string Artist       { get; } = string.Empty;
        public string Album        { get; } = string.Empty;
        public bool   HasArtist    => Artist.Length > 0;
        public bool   HasAlbum     => Album.Length > 0;

        [ObservableProperty] public partial bool IsPlaying { get; set; }
        [ObservableProperty] public partial double SeekPosition { get; set; }
        [ObservableProperty] public partial string CurrentTimeText { get; set; } = "0:00";
        [ObservableProperty] public partial string TotalTimeText { get; set; } = "--:--";
        [ObservableProperty] public partial bool IsAudioOnly { get; set; }
        [ObservableProperty] public partial Bitmap? AlbumArt { get; set; }

        /// <summary>The track finished (raised on the UI thread).</summary>
        public event Action? Ended;

        /// <summary>VLC couldn't play the file at all (raised on the UI thread). Ended is not raised.</summary>
        public event Action? Failed;

        // A file that "ends" without ever having a length or a playback position never played
        private bool _everPlayed;

        public MediaSession( LibVLC libVLC, Stream stream, string fileName, int volume, bool muted, float rate )
        {
            _vaultStream = stream;

            // Tags and artwork via TagLib before VLC touches the stream
            AlbumArt = TryExtractAlbumArt( stream, fileName );
            TagLib.Tag? tag = TryExtractTag( stream, fileName );
            stream.Seek( 0, SeekOrigin.Begin );

            TrackTitle = !string.IsNullOrWhiteSpace( tag?.Title ) ? tag!.Title : Path.GetFileNameWithoutExtension( fileName );
            Artist     = tag?.FirstPerformer ?? tag?.FirstAlbumArtist ?? string.Empty;
            Album      = tag?.Album ?? string.Empty;
            DisplayTitle = HasArtist ? $"{Artist} - {TrackTitle}" : fileName;

            _mediaInput = new StreamMediaInput( stream );
            _media      = new Media( libVLC, _mediaInput );

            // Linux: decode in software. VLC otherwise picks NVIDIA VDPAU/VA-API, whose GL interop
            // corrupts frames once a second player exists. This must be a per-media option: VLC
            // 3.0.24 ignores --avcodec-hw when it is passed to the LibVLC instance (verified).
            if( OperatingSystem.IsLinux( ) )
                _media.AddOption( ":avcodec-hw=none" );
            // VLC must not take keyboard or mouse input on its video surface: on X11, key presses go
            // to the window under the pointer, so with the cursor over the video VLC swallowed
            // shortcuts like Ctrl+W and Space. With input off they reach DeerCrypt instead.
            MediaPlayer = new MediaPlayer( _media ) { Volume = volume, Mute = muted, EnableKeyInput = false, EnableMouseInput = false };
            if( Math.Abs( rate - 1f ) > 0.001f ) MediaPlayer.SetRate( rate );

            _onLengthChanged = ( _, e ) =>
            {
                _lengthMs = e.Length;
                Post( ( ) => TotalTimeText = FormatMs( e.Length ) );
            };
            _onPlaying = ( _, _ ) => Post( ( ) =>
            {
                IsPlaying = true;
                EnsureTimerRunning( );
                long len = MediaPlayer.Length;
                if( len > 0 && _lengthMs == 0 ) { _lengthMs = len; TotalTimeText = FormatMs( len ); }
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
                SyncPosition( );
            } );
            _onStopped = ( _, _ ) => Post( ( ) =>
            {
                IsPlaying = false;
                _positionTimer?.Stop( );
            } );
            _onEndReached = ( _, _ ) => Post( ( ) =>
            {
                IsPlaying = false;
                _positionTimer?.Stop( );
                if( _disposed ) return;
                if( !_everPlayed && _lengthMs <= 0 && MediaPlayer.Length <= 0 ) { Failed?.Invoke( ); return; }
                if( _lengthMs > 0 ) { SeekPosition = 1.0; CurrentTimeText = TotalTimeText; }
                Ended?.Invoke( );
            } );
            _onError = ( _, _ ) => Post( ( ) =>
            {
                IsPlaying = false;
                _positionTimer?.Stop( );
                if( !_disposed ) Failed?.Invoke( );
            } );

            MediaPlayer.LengthChanged += _onLengthChanged;
            MediaPlayer.Playing       += _onPlaying;
            MediaPlayer.Paused        += _onPaused;
            MediaPlayer.Stopped       += _onStopped;
            MediaPlayer.EndReached    += _onEndReached;
            MediaPlayer.EncounteredError += _onError;
        }

        // Transport

        [RelayCommand]
        public void PlayPause( )
        {
            if( _disposed ) return;
            if( MediaPlayer.IsPlaying ) MediaPlayer.Pause( );
            else if( MediaPlayer.State == VLCState.Ended ) Restart( );
            else MediaPlayer.Play( );
        }

        public void Play( )  { if( !_disposed && !MediaPlayer.IsPlaying ) MediaPlayer.Play( ); }
        public void Pause( ) { if( !_disposed && MediaPlayer.IsPlaying ) MediaPlayer.Pause( ); }

        /// <summary>From the Ended state VLC needs Stop() before Play(); never call those on VLC's own thread.</summary>
        public void Restart( ) => Task.Run( ( ) =>
        {
            if( _disposed ) return;
            MediaPlayer.Stop( );
            MediaPlayer.Play( );
        } );

        public void SkipBy( long deltaMs )
        {
            if( _disposed ) return;
            long length = MediaPlayer.Length;
            MediaPlayer.Time = Math.Clamp( MediaPlayer.Time + deltaMs, 0L, length > 0 ? length : 0L );
            SyncPosition( );
        }

        public void SetVolume( int volume ) { if( !_disposed ) MediaPlayer.Volume = volume; }
        public void SetMute( bool muted )   { if( !_disposed ) MediaPlayer.Mute = muted; }
        public void SetRate( float rate )   { if( !_disposed ) MediaPlayer.SetRate( rate ); }

        public void BeginSeek( ) => _isSeeking = true;

        public void EndSeek( double position )
        {
            double p = Math.Clamp( position, 0.0, 1.0 );
            if( _lengthMs > 0 ) MediaPlayer.Time = (long)( p * _lengthMs );
            else MediaPlayer.Position = (float)p;
            _isSeeking = false;
        }

        // Position polling

        private void EnsureTimerRunning( )
        {
            if( _disposed ) return;
            if( _positionTimer == null )
            {
                _positionTimer = new DispatcherTimer( DispatcherPriority.Normal ) { Interval = TimeSpan.FromMilliseconds( 200 ) };
                _positionTimer.Tick += ( _, _ ) => { if( !_disposed ) SyncPosition( ); };
            }
            _positionTimer.Start( );
        }

        private void SyncPosition( )
        {
            long len = MediaPlayer.Length;
            if( len > 0 && len != _lengthMs ) { _lengthMs = len; TotalTimeText = FormatMs( len ); }
            if( _isSeeking ) return;

            long  t   = MediaPlayer.Time;
            float pos = MediaPlayer.Position;
            if( t > 0 || _lengthMs > 0 ) _everPlayed = true;
            if( t >= 0 ) CurrentTimeText = FormatMs( t );
            if( _lengthMs > 0 && t >= 0 ) SeekPosition = Math.Clamp( (double)t / _lengthMs, 0.0, 1.0 );
            else if( pos >= 0 ) SeekPosition = Math.Clamp( pos, 0.0, 1.0 );
        }

        // Tags

        private static Bitmap? TryExtractAlbumArt( Stream stream, string name )
        {
            try
            {
                stream.Seek( 0, SeekOrigin.Begin );
                using var file = TagLib.File.Create( new StreamAbstraction( name, stream ) );
                var pic = file.Tag.Pictures.FirstOrDefault( );
                if( pic == null ) return null;
                using var ms = new MemoryStream( pic.Data.Data );
                return new Bitmap( ms );
            }
            catch { return null; }
        }

        private static TagLib.Tag? TryExtractTag( Stream stream, string name )
        {
            try
            {
                stream.Seek( 0, SeekOrigin.Begin );
                using var file = TagLib.File.Create( new StreamAbstraction( name, stream ) );
                return file.Tag;
            }
            catch { return null; }
        }

        /// <summary>TagLib wrapper over our stream that never closes it.</summary>
        private sealed class StreamAbstraction( string name, Stream stream ) : TagLib.File.IFileAbstraction
        {
            public string Name { get; } = name;
            public Stream ReadStream  => stream;
            public Stream WriteStream => stream;
            public void CloseStream( Stream s ) { }
        }

        public static string FormatMs( long ms )
        {
            if( ms < 0 ) return "--:--";
            var ts = TimeSpan.FromMilliseconds( ms );
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private static void Post( Action action ) => Dispatcher.UIThread.Post( action );

        /// <summary>Stops playback off the UI thread (VLC's Stop blocks). Safe to await before swapping tracks.</summary>
        public Task StopAsync( ) => _disposed ? Task.CompletedTask : Task.Run( ( ) => { try { MediaPlayer.Stop( ); } catch { } } );

        public void Dispose( )
        {
            if( _disposed ) return;
            _disposed = true;
            _positionTimer?.Stop( );
            AlbumArt?.Dispose( );
            AlbumArt = null;

            MediaPlayer.LengthChanged -= _onLengthChanged;
            MediaPlayer.Playing       -= _onPlaying;
            MediaPlayer.Paused        -= _onPaused;
            MediaPlayer.Stopped       -= _onStopped;
            MediaPlayer.EndReached    -= _onEndReached;
            MediaPlayer.EncounteredError -= _onError;

            // VLC's native teardown must not run on the UI thread
            var mp = MediaPlayer; var media = _media; var input = _mediaInput; var vs = _vaultStream;
            Task.Run( ( ) =>
            {
                try { mp.Stop( );       } catch { }
                try { mp.Dispose( );    } catch { }
                try { media.Dispose( ); } catch { }
                try { input.Dispose( ); } catch { }
                try { vs.Dispose( );    } catch { }
            } );
        }
    }
}
