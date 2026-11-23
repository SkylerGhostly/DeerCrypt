using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Converters;
using DeerCrypt.Services;
using DeerCryptLib.Vault;
using FluentIcons.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Viewers
{
    /// <summary>
    /// Image viewer tab: one image at a time from a set (the folder or view it was opened from),
    /// decoded in memory. Zoom and pan math lives in the view; this holds the image, the set,
    /// rotation, and the slideshow.
    /// </summary>
    public sealed partial class ImageDocumentViewModel : DocumentViewModel
    {
        public const double ZoomMin = 0.05;  // 5 %
        public const double ZoomMax = 8.0;   // 800 %
        private const int    MaxDecodeSide = 4096;
        private static readonly TimeSpan SlideshowInterval = TimeSpan.FromSeconds( 4 );

        private readonly VaultFile                _vault;
        private readonly List<VaultItemViewModel> _images;
        private int                               _index;
        private bool                              _disposed;
        private CancellationTokenSource?          _loadCts;
        private DispatcherTimer?                  _slideshowTimer;

        public ImageDocumentViewModel( VaultFile vault, IReadOnlyList<VaultItemViewModel> images, int startIndex )
        {
            _vault  = vault;
            _images = [ .. images ];
            _index  = Math.Clamp( startIndex, 0, Math.Max( 0, _images.Count - 1 ) );
        }

        public override Symbol Icon      => Symbol.Image;
        public override IBrush IconBrush => FileKindVisuals.GetBrush( FileKind.Image );

        // Content

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( HasContent ) )]
        public partial Bitmap? Bitmap { get; set; }

        /// <summary>Backing stream for an animated GIF (kept alive while it renders); null for static images.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsAnimated ) )]
        [NotifyPropertyChangedFor( nameof( HasContent ) )]
        public partial Stream? AnimatedStream { get; set; }

        [ObservableProperty]
        public partial bool IsLoading { get; set; }

        [ObservableProperty]
        public partial string ErrorText { get; set; } = string.Empty;

        public bool IsAnimated => AnimatedStream != null;
        public bool HasContent => Bitmap != null || AnimatedStream != null;

        /// <summary>Original pixel size (before the decode cap), so 100% means one image pixel per screen pixel.</summary>
        public PixelSize NaturalPixelSize { get; private set; }

        [ObservableProperty]
        public partial string InfoText { get; set; } = string.Empty;

        // Zoom (1.0 = 100%)

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( ZoomPercent ) )]
        [NotifyPropertyChangedFor( nameof( ZoomSliderValue ) )]
        public partial double Zoom { get; set; } = 1.0;

        public string ZoomPercent => $"{(int)Math.Round( Zoom * 100 )}%";

        /// <summary>Logarithmic slider 0-100 (0 = 5%, ~59 = 100%, 100 = 800%).</summary>
        public double ZoomSliderValue
        {
            get => 100.0 * Math.Log( Zoom / ZoomMin ) / Math.Log( ZoomMax / ZoomMin );
            set => Zoom = Math.Clamp( ZoomMin * Math.Pow( ZoomMax / ZoomMin, value / 100.0 ), ZoomMin, ZoomMax );
        }

        // Rotation (display only - the file is never modified)

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsSideways ) )]
        public partial int Rotation { get; set; }

        /// <summary>Rotated 90° or 270°: width and height swap on screen.</summary>
        public bool IsSideways => Rotation % 180 != 0;

        [RelayCommand] private void RotateLeft( )  => Rotation = ( Rotation + 270 ) % 360;
        [RelayCommand] private void RotateRight( ) => Rotation = ( Rotation + 90 ) % 360;

        // Set navigation

        public int  Count       => _images.Count;
        public bool HasSet      => _images.Count > 1;
        public string CountText => _images.Count == 0 ? "" : $"{_index + 1} of {_images.Count}";

        private bool CanGoPrevious => _index > 0;
        private bool CanGoNext     => _index < _images.Count - 1;

        [RelayCommand( CanExecute = nameof( CanGoPrevious ) )]
        private Task Previous( ) => LoadIndexAsync( _index - 1 );

        [RelayCommand( CanExecute = nameof( CanGoNext ) )]
        private Task Next( ) => LoadIndexAsync( _index + 1 );

        [RelayCommand] private Task First( ) => LoadIndexAsync( 0 );
        [RelayCommand] private Task Last( )  => LoadIndexAsync( _images.Count - 1 );

        public VaultItemViewModel? CurrentItem => _images.Count > 0 ? _images [ _index ] : null;

        /// <summary>Shows a specific image from the set (used when the same file is opened again).</summary>
        public Task ShowAsync( string fileId )
        {
            int i = _images.FindIndex( x => x.Id == fileId );
            return i >= 0 ? LoadIndexAsync( i ) : Task.CompletedTask;
        }

        public bool Contains( string fileId ) => _images.Exists( x => x.Id == fileId );

        // Slideshow

        [ObservableProperty]
        public partial bool IsSlideshowRunning { get; set; }

        [RelayCommand]
        private void ToggleSlideshow( )
        {
            if( IsSlideshowRunning ) { StopSlideshow( ); return; }
            if( !HasSet ) return;

            _slideshowTimer ??= new DispatcherTimer { Interval = SlideshowInterval };
            _slideshowTimer.Tick -= OnSlideshowTick;
            _slideshowTimer.Tick += OnSlideshowTick;
            _slideshowTimer.Start( );
            IsSlideshowRunning = true;
        }

        private async void OnSlideshowTick( object? sender, EventArgs e )
        {
            if( _disposed ) { StopSlideshow( ); return; }
            await LoadIndexAsync( CanGoNext ? _index + 1 : 0 ); // loops back to the start
        }

        private void StopSlideshow( )
        {
            _slideshowTimer?.Stop( );
            IsSlideshowRunning = false;
        }

        protected override void OnDeactivated( ) => StopSlideshow( );

        // Loading

        public Task LoadCurrentAsync( ) => _images.Count > 0 ? LoadIndexAsync( _index ) : Task.CompletedTask;

        private async Task LoadIndexAsync( int index )
        {
            if( _disposed || _images.Count == 0 ) return;

            _loadCts?.Cancel( );
            _loadCts = new CancellationTokenSource( );
            var ct = _loadCts.Token;

            _index = Math.Clamp( index, 0, _images.Count - 1 );
            var item = _images [ _index ];

            OnPropertyChanged( nameof( CountText ) );
            OnPropertyChanged( nameof( CurrentItem ) );
            PreviousCommand.NotifyCanExecuteChanged( );
            NextCommand.NotifyCanExecuteChanged( );

            FileId    = item.Id;
            Title     = item.DisplayName;
            ErrorText = string.Empty;
            Rotation  = 0;
            IsLoading = true;

            // Release the previous image first
            var oldBitmap = Bitmap;
            Bitmap = null;
            oldBitmap?.Dispose( );
            var oldStream = AnimatedStream;
            AnimatedStream = null;
            oldStream?.Dispose( );
            NaturalPixelSize = default;

            try
            {
                var ms = new MemoryStream( );
                await using( var stream = await _vault.OpenReadStreamAsync( item.AsFile!.Id, ct ) )
                    await stream.CopyToAsync( ms, ct );
                ct.ThrowIfCancellationRequested( );
                ms.Position = 0;

                if( IsGifStream( ms ) )
                {
                    // GifImage reads the stream on demand: keep it alive (owned until replaced)
                    NaturalPixelSize = ReadGifPixelSize( ms );
                    ms.Position      = 0;
                    AnimatedStream   = ms;
                }
                else
                {
                    // Decode off the UI thread, capping huge images at 4096 px on the long side so the
                    // GPU texture stays reasonable. NaturalPixelSize keeps the original size for zoom math.
                    (Bitmap? bitmap, PixelSize natural) = await Task.Run<(Bitmap?, PixelSize)>( ( ) =>
                    {
                        Bitmap full;
                        try { full = new Bitmap( ms ); }
                        catch( Exception ) { return (null, default); }

                        var ps = full.PixelSize;
                        if( ps.Width <= MaxDecodeSide && ps.Height <= MaxDecodeSide )
                            return (full, ps);

                        double scale = Math.Min( (double)MaxDecodeSide / ps.Width, (double)MaxDecodeSide / ps.Height );
                        var scaled = full.CreateScaledBitmap(
                            new PixelSize( (int)( ps.Width * scale ), (int)( ps.Height * scale ) ),
                            BitmapInterpolationMode.HighQuality );
                        full.Dispose( );
                        return (scaled, ps);
                    }, ct );
                    ms.Dispose( );

                    if( ct.IsCancellationRequested ) { bitmap?.Dispose( ); return; }

                    if( bitmap == null )
                    {
                        ErrorText = $"This image format can't be shown here ({Path.GetExtension( item.DisplayName )}). " +
                                    "Extract it or open it in another app instead.";
                    }
                    else
                    {
                        NaturalPixelSize = natural;
                        Bitmap = bitmap;
                    }
                }

                string ext = Path.GetExtension( item.DisplayName ).TrimStart( '.' ).ToUpperInvariant( );
                long size  = item.AsFile?.OriginalSize ?? 0;
                InfoText = NaturalPixelSize.Width > 0
                    ? $"{NaturalPixelSize.Width:N0} × {NaturalPixelSize.Height:N0}  ·  {ext}  ·  {FileSizeConverter.Format( size )}" +
                      ( IsAnimated ? "  ·  animated" : string.Empty )
                    : $"{ext}  ·  {FileSizeConverter.Format( size )}";
            }
            catch( OperationCanceledException ) { }
            catch( ObjectDisposedException ) { }
            catch( Exception ex )
            {
                ErrorText = $"Couldn't show this image: {ex.Message}";
            }
            finally
            {
                if( !ct.IsCancellationRequested ) IsLoading = false;
            }
        }

        // Delete (the view confirms first)

        /// <summary>Raised after a successful delete so the browser can drop the item from its list.</summary>
        public event Action<VaultItemViewModel>? ItemDeleted;

        public async Task<string?> DeleteCurrentAsync( )
        {
            if( _images.Count == 0 ) return null;
            var item = _images [ _index ];

            try
            {
                await _vault.RemoveAsync( item.Id );
            }
            catch( Exception ex )
            {
                return ex.Message;
            }

            ItemDeleted?.Invoke( item );
            RaiseVaultChanged( );
            _images.RemoveAt( _index );
            OnPropertyChanged( nameof( Count ) );
            OnPropertyChanged( nameof( HasSet ) );

            if( _images.Count == 0 )
            {
                RequestClose( );
                return null;
            }

            await LoadIndexAsync( Math.Min( _index, _images.Count - 1 ) );
            return null;
        }

        // GIF helpers

        private static bool IsGifStream( Stream s )
        {
            if( s.Length < 6 ) return false;
            Span<byte> h = stackalloc byte [ 6 ];
            int read = s.Read( h );
            s.Position = 0;
            return read == 6 && h [ 0 ] == 'G' && h [ 1 ] == 'I' && h [ 2 ] == 'F'
                && h [ 3 ] == '8' && ( h [ 4 ] == '7' || h [ 4 ] == '9' ) && h [ 5 ] == 'a';
        }

        private static PixelSize ReadGifPixelSize( Stream s )
        {
            if( s.Length < 10 ) return new PixelSize( 1, 1 );
            Span<byte> buf = stackalloc byte [ 10 ];
            s.ReadExactly( buf );
            s.Position = 0;
            int w = buf [ 6 ] | ( buf [ 7 ] << 8 );
            int h = buf [ 8 ] | ( buf [ 9 ] << 8 );
            return new PixelSize( Math.Max( 1, w ), Math.Max( 1, h ) );
        }

        public override void Dispose( )
        {
            if( _disposed ) return;
            _disposed = true;
            _loadCts?.Cancel( );
            StopSlideshow( );
            Bitmap?.Dispose( );
            Bitmap = null;
            AnimatedStream?.Dispose( );
            AnimatedStream = null;
            App.RefreshGlyphCache( );
        }
    }
}
