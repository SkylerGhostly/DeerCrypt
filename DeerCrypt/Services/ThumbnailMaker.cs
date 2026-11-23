using SkiaSharp;
using System;
using System.IO;
using System.Linq;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Makes small WebP thumbnails in memory: from image bytes, or from the cover art embedded in
    /// an audio file. Nothing touches the disk. Returns null when there's nothing usable.
    /// </summary>
    public static class ThumbnailMaker
    {
        /// <summary>Longest edge of a stored thumbnail, in pixels (crisp at 2× in tiles and the details panel).</summary>
        public const int MaxEdge = 256;

        /// <summary>Don't decode anything larger than this many pixels (after any decoder downscaling).</summary>
        private const long MaxDecodePixels = 64L * 1024 * 1024;

        public static byte [ ]? FromImage( byte [ ] data )
        {
            try
            {
                using var codec = SKCodec.Create( new MemoryStream( data, writable: false ) );
                if( codec == null ) return null;

                var info = codec.Info;
                if( info.Width <= 0 || info.Height <= 0 ) return null;

                float scale = Math.Min( 1f, (float)MaxEdge / Math.Max( info.Width, info.Height ) );
                int targetW = Math.Max( 1, (int)Math.Round( info.Width * scale ) );
                int targetH = Math.Max( 1, (int)Math.Round( info.Height * scale ) );

                // JPEG (and some others) can decode straight to 1/2, 1/4 or 1/8 size: much faster
                // and lighter on memory. Use it only if the result is still at least the target size.
                var decodeSize = codec.GetScaledDimensions( scale );
                if( decodeSize.Width < targetW || decodeSize.Height < targetH ) decodeSize = new SKSizeI( info.Width, info.Height );
                if( (long)decodeSize.Width * decodeSize.Height > MaxDecodePixels ) return null;

                var decodeInfo = new SKImageInfo( decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul );
                using var decoded = new SKBitmap( decodeInfo );
                var result = codec.GetPixels( decodeInfo, decoded.GetPixels( ) );
                if( result is not ( SKCodecResult.Success or SKCodecResult.IncompleteInput ) ) return null;

                using var resized = decoded.Resize( new SKImageInfo( targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul ), SKFilterQuality.High );
                if( resized == null ) return null;

                using var oriented = ApplyOrigin( resized, codec.EncodedOrigin );
                using var image = SKImage.FromBitmap( oriented ?? resized );
                using var encoded = image.Encode( SKEncodedImageFormat.Webp, 80 );
                return encoded?.ToArray( );
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The embedded cover art of an audio file, as a thumbnail.</summary>
        public static byte [ ]? FromAudio( Stream stream, string fileName )
        {
            try
            {
                using var file = TagLib.File.Create( new StreamAbstraction( fileName, stream ) );
                var picture = file.Tag.Pictures
                    .OrderByDescending( p => p.Type == TagLib.PictureType.FrontCover )
                    .FirstOrDefault( );
                return picture == null ? null : FromImage( picture.Data.Data );
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Rotates/flips per the photo's EXIF orientation. Null when it's already upright.</summary>
        private static SKBitmap? ApplyOrigin( SKBitmap src, SKEncodedOrigin origin )
        {
            if( origin == SKEncodedOrigin.TopLeft ) return null;

            int w = src.Width, h = src.Height;
            bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var dst = new SKBitmap( swap ? h : w, swap ? w : h, src.ColorType, src.AlphaType );
            using var canvas = new SKCanvas( dst );
            canvas.Clear( SKColors.Transparent );

            switch( origin )
            {
                case SKEncodedOrigin.TopRight:    canvas.Translate( w, 0 ); canvas.Scale( -1, 1 ); break;
                case SKEncodedOrigin.BottomRight: canvas.Translate( w, h ); canvas.RotateDegrees( 180 ); break;
                case SKEncodedOrigin.BottomLeft:  canvas.Translate( 0, h ); canvas.Scale( 1, -1 ); break;
                case SKEncodedOrigin.LeftTop:     canvas.RotateDegrees( 90 ); canvas.Scale( 1, -1 ); break;
                case SKEncodedOrigin.RightTop:    canvas.Translate( h, 0 ); canvas.RotateDegrees( 90 ); break;
                case SKEncodedOrigin.RightBottom: canvas.Translate( h, w ); canvas.RotateDegrees( -90 ); canvas.Scale( 1, -1 ); break;
                case SKEncodedOrigin.LeftBottom:  canvas.Translate( 0, w ); canvas.RotateDegrees( -90 ); break;
            }
            canvas.DrawBitmap( src, 0, 0 );
            return dst;
        }

        /// <summary>TagLib wrapper over a stream that never closes it.</summary>
        private sealed class StreamAbstraction( string name, Stream stream ) : TagLib.File.IFileAbstraction
        {
            public string Name { get; } = name;
            public Stream ReadStream  => stream;
            public Stream WriteStream => stream;
            public void CloseStream( Stream s ) { }
        }
    }
}
