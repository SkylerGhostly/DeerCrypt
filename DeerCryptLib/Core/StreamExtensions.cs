using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace DeerCryptLib.Core
{
    public static class StreamExtensions
    {
        public static Task CopyToAsync( this Stream source, Stream destination,
            int bufferSize, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            long totalSize = source.CanSeek ? ( source.Length - source.Position ) : 0;
            return CopyToAsync( source, destination, bufferSize, progress, totalSize, cancellationToken );
        }

        public static async Task CopyToAsync( this Stream source, Stream destination,
            int bufferSize, IProgress<double>? progress,    
            long totalSize,
            CancellationToken cancellationToken = default )
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent( bufferSize );
            try
            {
                int bytesRead;
                long totalRead = 0;

                while( ( bytesRead = await source.ReadAsync(
                    buffer.AsMemory( 0, bufferSize ), cancellationToken ) ) > 0 )
                {
                    await destination.WriteAsync(
                        buffer.AsMemory( 0, bytesRead ), cancellationToken );
                    totalRead += bytesRead;
                    if( totalSize > 0 )
                        progress?.Report( (double)totalRead / totalSize );
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return( buffer );
            }
        }
    }
}
