using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Services;
using DeerCryptLib.Vault;
using FluentIcons.Common;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Viewers
{
    /// <summary>
    /// Media player tab with a playlist (the media files of the folder or view it was opened from).
    /// Each track gets its own <see cref="MediaSession"/>; volume, mute, speed, and repeat carry over.
    /// Video pauses when its tab is hidden; audio keeps playing in the background.
    /// </summary>
    public sealed partial class MediaDocumentViewModel : DocumentViewModel
    {
        /// <summary>Files up to this size are buffered fully in memory (instant seeking); bigger ones stream from the vault.</summary>
        private const long BufferThreshold = 64L * 1024 * 1024;

        public static readonly float[] Speeds = [ 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f ];

        private readonly VaultFile                _vault;
        private readonly LibVLC                   _libVLC;
        private readonly List<VaultItemViewModel> _playlist;
        private int                               _index;
        private bool                              _disposed;
        private bool                              _resumeAfterMove;
        private bool                              _resumeOnActivate;

        public MediaDocumentViewModel( VaultFile vault, LibVLC libVLC, IReadOnlyList<VaultItemViewModel> playlist, int startIndex )
        {
            _vault    = vault;
            _libVLC   = libVLC;
            _playlist = [ .. playlist ];
            _index    = Math.Clamp( startIndex, 0, Math.Max( 0, _playlist.Count - 1 ) );
        }

        public override Symbol Icon      => Session?.IsAudioOnly == true ? Symbol.MusicNote2 : Symbol.Video;
        public override IBrush IconBrush => FileKindVisuals.GetBrush( Session?.IsAudioOnly == true ? FileKind.Audio : FileKind.Video );

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( ShowVideoSurface ) )]
        public partial MediaSession? Session { get; private set; }

        [ObservableProperty] public partial bool IsLoading { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( ShowVideoSurface ) )]
        [NotifyPropertyChangedFor( nameof( HasError ) )]
        public partial string ErrorText { get; set; } = string.Empty;

        public bool HasError => ErrorText.Length > 0;

        /// <summary>VLC's native surface draws over everything, so it's hidden for audio and for errors.</summary>
        public bool ShowVideoSurface => Session is { IsAudioOnly: false } && !HasError;

        // Settings that carry across tracks

        [ObservableProperty] public partial int Volume { get; set; } = 100;
        [ObservableProperty] public partial bool IsMuted { get; set; }
        [ObservableProperty] public partial bool IsRepeat { get; set; }

        /// <summary>When a track ends, play the next one in the list.</summary>
        [ObservableProperty] public partial bool AutoAdvance { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( SpeedText ) )]
        public partial float Speed { get; set; } = 1f;

        public string SpeedText => IsSpeedBoosted ? "2× hold" : $"{Speed:0.##}×";

        /// <summary>Space is being held: playing at 2× until it's released.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( SpeedText ) )]
        public partial bool IsSpeedBoosted { get; private set; }

        public void BeginSpeedBoost( )
        {
            if( IsSpeedBoosted || Session is not { IsPlaying: true } s ) return;
            IsSpeedBoosted = true;
            s.SetRate( 2f );
        }

        public void EndSpeedBoost( )
        {
            if( !IsSpeedBoosted ) return;
            IsSpeedBoosted = false;
            Session?.SetRate( Speed );
        }

        partial void OnVolumeChanged( int value )  => Session?.SetVolume( value );
        partial void OnIsMutedChanged( bool value ) => Session?.SetMute( value );
        partial void OnSpeedChanged( float value )  => Session?.SetRate( value );

        [RelayCommand] private void ToggleMute( ) => IsMuted = !IsMuted;
        [RelayCommand] private void SetSpeed( string value ) { if( float.TryParse( value, System.Globalization.CultureInfo.InvariantCulture, out float f ) ) Speed = f; }

        // Playlist

        public bool   HasPlaylist => _playlist.Count > 1;
        public string CountText   => _playlist.Count > 1 ? $"{_index + 1} of {_playlist.Count}" : string.Empty;

        private bool CanGoPrevious => _index > 0;
        private bool CanGoNext     => _index < _playlist.Count - 1;

        [RelayCommand( CanExecute = nameof( CanGoPrevious ) )]
        private Task Previous( )
        {
            // Like most players: early in a track, go back a track; later, restart this one
            if( Session != null && Session.MediaPlayer.Time > 3000 )
            {
                Session.MediaPlayer.Time = 0;
                return Task.CompletedTask;
            }
            return LoadIndexAsync( _index - 1, autoplay: true );
        }

        [RelayCommand( CanExecute = nameof( CanGoNext ) )]
        private Task Next( ) => LoadIndexAsync( _index + 1, autoplay: true );

        [RelayCommand] private void PlayPause( ) => Session?.PlayPause( );
        [RelayCommand] private void SkipBack( )    => Session?.SkipBy( -10_000 );
        [RelayCommand] private void SkipForward( ) => Session?.SkipBy( +10_000 );

        [RelayCommand] private void Fullscreen( ) => RequestFullscreen( );

        public bool Contains( string fileId ) => _playlist.Exists( x => x.Id == fileId );

        public Task ShowAsync( string fileId )
        {
            int i = _playlist.FindIndex( x => x.Id == fileId );
            return i >= 0 && i != _index ? LoadIndexAsync( i, autoplay: true ) : Task.CompletedTask;
        }

        public Task LoadCurrentAsync( ) => LoadIndexAsync( _index, autoplay: true );

        private async Task LoadIndexAsync( int index, bool autoplay )
        {
            if( _disposed || _playlist.Count == 0 ) return;

            _index = Math.Clamp( index, 0, _playlist.Count - 1 );
            var item = _playlist [ _index ];
            OnPropertyChanged( nameof( CountText ) );
            PreviousCommand.NotifyCanExecuteChanged( );
            NextCommand.NotifyCanExecuteChanged( );

            IsSpeedBoosted = false; // the new track starts at the chosen speed
            FileId    = item.Id;
            Title     = item.DisplayName;
            ErrorText = string.Empty;
            IsLoading = true;

            // Stop the old track before its player is detached from the video surface, so VLC
            // never runs without a surface (it would open its own window)
            var old = Session;
            if( old != null ) await old.StopAsync( );

            Stream? stream = null;
            try
            {
                var vaultStream = await _vault.OpenReadStreamAsync( item.AsFile!.Id );
                if( vaultStream.Length <= BufferThreshold )
                {
                    var ms = new MemoryStream( (int)vaultStream.Length );
                    await vaultStream.CopyToAsync( ms );
                    await vaultStream.DisposeAsync( );
                    ms.Position = 0;
                    stream = ms;
                }
                else
                {
                    stream = vaultStream;
                }

                if( _disposed ) { await stream.DisposeAsync( ); return; }

                var session = new MediaSession( _libVLC, stream, item.DisplayName, Volume, IsMuted, Speed );
                stream = null; // owned by the session now
                session.Ended  += OnSessionEnded;
                session.Failed += OnSessionFailed;
                session.PropertyChanged += OnSessionPropertyChanged;

                if( autoplay ) _pendingAutoplay = true; // before assigning: the view starts playback on attach
                Session = session; // the view attaches it to the video surface and starts playback
                if( old != null )
                {
                    old.Ended  -= OnSessionEnded;
                    old.Failed -= OnSessionFailed;
                    old.PropertyChanged -= OnSessionPropertyChanged;
                    old.Dispose( );
                }

                Title = session.DisplayTitle;
            }
            catch( ObjectDisposedException ) { }
            catch( Exception ex )
            {
                ErrorText = $"Couldn't play this file: {ex.Message}";
                if( stream != null ) await stream.DisposeAsync( );
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>Set when a new session should start once the view has attached it.</summary>
        private bool _pendingAutoplay;

        /// <summary>Called by the view after it attached <see cref="Session"/> to its video surface.</summary>
        public void OnSessionAttached( )
        {
            if( _pendingAutoplay )
            {
                _pendingAutoplay = false;
                Session?.Play( );
            }
        }

        /// <summary>Stop here and say so - never skip on through a folder of files that won't play.</summary>
        private void OnSessionFailed( )
        {
            ErrorText = "This file couldn't be played. It may be damaged, encrypted by another app, " +
                        "or in a format VLC doesn't support. Extract it to inspect it, or try the next file.";
        }

        private void OnSessionEnded( )
        {
            if( IsRepeat ) Session?.Restart( );
            else if( AutoAdvance && CanGoNext ) _ = LoadIndexAsync( _index + 1, autoplay: true );
        }

        private void OnSessionPropertyChanged( object? sender, System.ComponentModel.PropertyChangedEventArgs e )
        {
            if( e.PropertyName == nameof( MediaSession.IsAudioOnly ) )
            {
                OnPropertyChanged( nameof( ShowVideoSurface ) );
                OnPropertyChanged( nameof( Icon ) );
                OnPropertyChanged( nameof( IconBrush ) );
            }
            if( e.PropertyName is nameof( MediaSession.IsPlaying ) or nameof( MediaSession.IsAudioOnly ) )
                HasBackgroundActivity = Session?.IsPlaying == true && !IsActive;
        }

        // Tab switching and moving between the tabs and a window

        protected override void OnActivated( )
        {
            HasBackgroundActivity = false;
            if( _resumeOnActivate ) { _resumeOnActivate = false; Session?.Play( ); }
        }

        protected override void OnDeactivated( )
        {
            // Keep playing in the background (the video surface is only hidden)
            HasBackgroundActivity = Session?.IsPlaying == true;
        }

        public override void BeforeMove( )
        {
            // The video surface is rebuilt in the new place; pause so VLC never plays without one
            _resumeAfterMove = Session?.IsPlaying == true;
            if( Session is { IsAudioOnly: false } s ) s.Pause( );
        }

        public override void AfterMove( )
        {
            if( _resumeAfterMove ) Session?.Play( );
            _resumeAfterMove = false;
        }

        public override void Dispose( )
        {
            if( _disposed ) return;
            _disposed = true;
            if( Session is { } s )
            {
                s.Ended  -= OnSessionEnded;
                s.Failed -= OnSessionFailed;
                s.PropertyChanged -= OnSessionPropertyChanged;
                s.Dispose( );
            }
        }
    }
}
