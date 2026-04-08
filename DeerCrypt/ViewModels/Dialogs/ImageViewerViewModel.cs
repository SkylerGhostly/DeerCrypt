using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCryptLib.Vault;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Dialogs
{
    public sealed partial class ImageViewerViewModel(
        VaultFile vault,
        IReadOnlyList<VaultItemViewModel> images,
        int startIndex ) : ObservableObject, IDisposable
    {
        // Constants 
        private const double ZoomMin = 0.05;  // 5 %
        private const double ZoomMax = 8.0;   // 800 %
        private readonly List<VaultItemViewModel> _images = [ .. images ];
        private int  _index  = Math.Clamp( startIndex, 0, Math.Max( 0, images.Count - 1 ) );
        private bool _disposed;

        // Observable 

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( HasContent ) )]
        public partial Bitmap? Bitmap { get; set; }

        /// <summary>
        /// The live stream backing the current animated GIF - kept alive while
        /// GifImage is rendering.  Null when the current image is static.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsAnimated ) )]
        [NotifyPropertyChangedFor( nameof( HasContent ) )]
        public partial Stream? AnimatedStream { get; set; }

        [ObservableProperty]
        public partial string Title { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsLoading { get; set; }
        [ObservableProperty]
        public partial string ErrorText { get; set; } = string.Empty;

        // Zoom: 1.0 = 100% (1 image pixel = 1 logical pixel)
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( ZoomPercent ) )]
        [NotifyPropertyChangedFor( nameof( ZoomSliderValue ) )]
        public partial double Zoom { get; set; } = 1.0;

        // Derived properties 

        /// <summary>True when the current image is an animated GIF.</summary>
        public bool IsAnimated => AnimatedStream != null;

        /// <summary>True when there is something to display (static or animated).</summary>
        public bool HasContent => Bitmap != null || AnimatedStream != null;

        /// <summary>
        /// Natural pixel dimensions of the currently loaded image.
        /// Set by <see cref="LoadIndexAsync"/> before raising Bitmap/AnimatedStream
        /// so that code-behind layout helpers can use it synchronously.
        /// </summary>
        public PixelSize NaturalPixelSize { get; private set; }

        public string ZoomPercent => $"{(int)Math.Round( Zoom * 100 )}%";

        /// <summary>
        /// Logarithmic slider position 0–100, where 0 = 5 %, ~59 = 100 %, 100 = 800 %.
        /// TwoWay binding via a plain property - raises PropertyChanged through Zoom.
        /// </summary>
        public double ZoomSliderValue
        {
            get => ZoomToSlider( Zoom );
            set => Zoom = Math.Clamp( SliderToZoom( value ), ZoomMin, ZoomMax );
        }

        public bool CanGoPrevious( ) => _index > 0;
        public bool CanGoNext( )     => _index < _images.Count - 1;

        public string CountText => _images.Count == 0
            ? ""
            : $"{_index + 1} / {_images.Count}";

        // Events 

        /// <summary>Raised when deletion empties the image list - window should close.</summary>
        public event Action? CloseRequested;

        /// <summary>Raised after a successful vault deletion so the browser can remove the item.</summary>
        public event Action<VaultItemViewModel>? ItemDeleted;

        public Task LoadCurrentAsync( CancellationToken ct = default ) =>
            _images.Count > 0 ? LoadIndexAsync( _index, ct ) : Task.CompletedTask;

        // Image loading 

        private async Task LoadIndexAsync( int index, CancellationToken ct )
        {
            _index = Math.Clamp( index, 0, _images.Count - 1 );

            OnPropertyChanged( nameof( CanGoPrevious ) );
            OnPropertyChanged( nameof( CanGoNext ) );
            OnPropertyChanged( nameof( CountText ) );
            PreviousCommand.NotifyCanExecuteChanged( );
            NextCommand.NotifyCanExecuteChanged( );

            var item = _images[ _index ];
            Title        = item.DisplayName;
            ErrorText    = string.Empty;
            IsLoading    = true;

            // Clear previous content 
            var oldBitmap = Bitmap;
            Bitmap = null;
            oldBitmap?.Dispose( );

            // Stop animation and release stream before disposal
            var oldStream = AnimatedStream;
            AnimatedStream = null;
            OnPropertyChanged( nameof( AnimatedStream ) );
            OnPropertyChanged( nameof( IsAnimated ) );
            OnPropertyChanged( nameof( HasContent ) );
            oldStream?.Dispose( );

            NaturalPixelSize = default;

            try
            {
                var stream = await vault.OpenReadStreamAsync( item.AsFile!.Id, ct );
                using( stream )
                {
                    var ms = new MemoryStream( );
                    await stream.CopyToAsync( ms, ct );
                    ms.Position = 0;

                    if( IsGifStream( ms ) )
                    {
                        // Animated GIF path 
                        // Keep the MemoryStream alive - GifImage reads it on demand.
                        NaturalPixelSize = ReadGifPixelSize( ms );
                        ms.Position      = 0;
                        AnimatedStream = ms;        // take ownership; do NOT dispose here
                        OnPropertyChanged( nameof( AnimatedStream ) );
                        OnPropertyChanged( nameof( IsAnimated ) );
                        OnPropertyChanged( nameof( HasContent ) );
                    }
                    else
                    {
                        // Static image path 
                        // Decode off the UI thread.  Large images produce very large GPU
                        // textures that crowd out Skia's glyph atlas, causing color emoji
                        // in the main window to fall back to monochrome outline rendering.
                        // Capping at 4096 px on the longest side keeps the texture ≤ 64 MB
                        // while remaining above any reasonable display resolution.
                        // NaturalPixelSize is set to the *original* dimensions so that
                        // zoom=1.0 means one original pixel per logical pixel.
                        ms.Position = 0;
                        (Bitmap? bitmap, PixelSize naturalSize) = await Task.Run<(Bitmap?, PixelSize)>( ( ) =>
                        {
                            const int MaxSide = 4096;

                            Bitmap full;

                            try
                            {
                                full = new Bitmap( ms );
                            }
                            catch( ArgumentException )
                            {
                                // Catch it here so the debugger leaves you alone.
                                // Return null to tell the UI thread it failed.
                                return (null, default);
                            }

                            var ps   = full.PixelSize;
                            if( ps.Width <= MaxSide && ps.Height <= MaxSide )
                                return (full, ps);
                            double scale  = Math.Min( (double)MaxSide / ps.Width,
                                                      (double)MaxSide / ps.Height );
                            var scaled = full.CreateScaledBitmap(
                                new PixelSize( (int)( ps.Width  * scale ),
                                               (int)( ps.Height * scale ) ),
                                BitmapInterpolationMode.HighQuality );
                            full.Dispose( );
                            return (scaled, ps);   // ps = original size for zoom math
                        }, ct );
                        ms.Dispose( );

                        if( bitmap == null )
                        {
                            ErrorText = $"File type is not supported: {Path.GetExtension( item.DisplayName )}";
                        }
                        else
                        {
                            NaturalPixelSize = naturalSize;
                            Bitmap = bitmap;
                        }
                    }
                }
            }
            catch( OperationCanceledException ) { }
            catch( Exception ex )
            {
                ErrorText = $"Cannot display image: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        // Commands 

        [RelayCommand( CanExecute = nameof( CanGoPrevious ) )]
        private Task Previous( CancellationToken ct ) => LoadIndexAsync( _index - 1, ct );

        [RelayCommand( CanExecute = nameof( CanGoNext ) )]
        private Task Next( CancellationToken ct ) => LoadIndexAsync( _index + 1, ct );

        [RelayCommand]
        private async Task Delete( CancellationToken ct )
        {
            if( _images.Count == 0 ) return;
            var item = _images[ _index ];

            try
            {
                await vault.RemoveAsync( item.Id, new Progress<double>( _ => { } ), ct );
            }
            catch( Exception ex )
            {
                ErrorText = $"Delete failed: {ex.Message}";
                return;
            }

            ItemDeleted?.Invoke( item );
            _images.RemoveAt( _index );

            if( _images.Count == 0 )
            {
                CloseRequested?.Invoke( );
                return;
            }

            await LoadIndexAsync( Math.Min( _index, _images.Count - 1 ), ct );
        }

        // GIF helpers 

        // GIF signature: bytes 0-5 are "GIF87a" or "GIF89a"
        private static bool IsGifStream( Stream s )
        {
            if( s.Length < 6 ) return false;
            Span<byte> header = stackalloc byte[ 6 ];
            int read = s.Read( header );
            s.Position = 0;
            return read == 6
                && header[ 0 ] == 'G' && header[ 1 ] == 'I' && header[ 2 ] == 'F'
                && header[ 3 ] == '8' && ( header[ 4 ] == '7' || header[ 4 ] == '9' )
                && header[ 5 ] == 'a';
        }

        // GIF logical screen descriptor: bytes 6-7 = width, bytes 8-9 = height (little-endian)
        private static PixelSize ReadGifPixelSize( Stream s )
        {
            if( s.Length < 10 ) return new PixelSize( 1, 1 );
            Span<byte> buf = stackalloc byte[ 10 ];
            s.ReadExactly( buf );
            s.Position = 0;
            int w = buf[ 6 ] | ( buf[ 7 ] << 8 );
            int h = buf[ 8 ] | ( buf[ 9 ] << 8 );
            return new PixelSize( Math.Max( 1, w ), Math.Max( 1, h ) );
        }

        // Zoom helpers 

        // Logarithmic scale: slider 0→5%, slider 100→800%, slider ~59→100%
        private static double SliderToZoom( double v )  => ZoomMin * Math.Pow( ZoomMax / ZoomMin, v / 100.0 );
        private static double ZoomToSlider( double z )  => 100.0 * Math.Log( z / ZoomMin ) / Math.Log( ZoomMax / ZoomMin );

        // IDisposable 

        public void Dispose( )
        {
            if( _disposed ) return;
            _disposed = true;

            Bitmap?.Dispose( );
            Bitmap = null;
            AnimatedStream?.Dispose( );
            AnimatedStream = null;
        }
    }
}
