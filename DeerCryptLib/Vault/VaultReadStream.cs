using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCryptLib.Vault
{
    /// <summary>
    /// A read-only, seekable <see cref="Stream"/> over an encrypted vault file that
    /// decrypts chunks on demand - no full extraction to disk required.
    ///
    /// <para><b>Chunk model:</b> vault files are stored as a sequence of independently
    /// encrypted fixed-size chunks.  <see cref="VaultReadStream"/> maps any byte-range
    /// read or seek to the minimum set of chunks needed, decrypts each one exactly once
    /// (per cache lifetime), and splices the results into the caller's buffer.</para>
    ///
    /// <para><b>LRU cache:</b> the last <see cref="CacheCapacity"/> decrypted chunks are
    /// retained in memory.  A media player buffering a few seconds ahead or seeking back
    /// a short distance will typically hit the cache and pay no decrypt cost.</para>
    ///
    /// <para><b>Thread-safety:</b> this class is <em>not</em> thread-safe.  The underlying
    /// <see cref="VaultFile"/> connection is shared; only one reader should be active on
    /// a connection at a time.  If you need concurrent playback of multiple files, open
    /// separate <see cref="VaultFile"/> instances.</para>
    ///
    /// <para>Obtain an instance via <see cref="VaultFile.OpenReadStreamAsync"/>.</para>
    /// </summary>
    public sealed class VaultReadStream : Stream
    {
        #region Constants

        /// <summary>Number of decrypted chunks kept in the LRU cache.</summary>
        public const int CacheCapacity = 3;

        #endregion

        #region Fields

        private readonly string                                      _fileId;
        private readonly long                                        _length;
        private readonly int                                         _chunkSize;  // plaintext bytes per full chunk
        private readonly int                                         _chunkCount;
        private readonly Func<int, CancellationToken, Task<byte[]>> _fetchChunk; // returns decrypted plaintext
        private readonly ChunkLruCache                               _cache;

        private long _position;
        private bool _disposed;

        #endregion

        #region Constructor (internal)

        /// <summary>
        /// Created exclusively by <see cref="VaultFile.OpenReadStreamAsync"/>.
        /// </summary>
        internal VaultReadStream(
            string                                      fileId,
            long                                        length,
            int                                         chunkSize,
            int                                         chunkCount,
            Func<int, CancellationToken, Task<byte[]>> fetchChunk )
        {
            _fileId     = fileId;
            _length     = length;
            _chunkSize  = chunkSize > 0 ? chunkSize : throw new ArgumentOutOfRangeException( nameof( chunkSize ) );
            _chunkCount = chunkCount;
            _fetchChunk = fetchChunk ?? throw new ArgumentNullException( nameof( fetchChunk ) );
            _cache      = new ChunkLruCache( CacheCapacity );
        }

        #endregion

        #region Stream Properties

        /// <inheritdoc/>
        public override bool CanRead  => !_disposed;

        /// <inheritdoc/>
        public override bool CanSeek  => !_disposed;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <summary>The original (plaintext) byte length of the vault file.</summary>
        public override long Length => _length;

        /// <summary>
        /// Current read position in the plaintext byte stream.
        /// Setting this property is equivalent to <c>Seek(value, SeekOrigin.Begin)</c>.
        /// </summary>
        public override long Position
        {
            get => _position;
            set => Seek( value, SeekOrigin.Begin );
        }

        #endregion

        #region Seek

        /// <inheritdoc/>
        public override long Seek( long offset, SeekOrigin origin )
        {
            ThrowIfDisposed( );

            long newPos = origin switch
            {
                SeekOrigin.Begin   => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End     => _length + offset,
                _                  => throw new ArgumentException( $"Unknown SeekOrigin: {origin}", nameof( origin ) )
            };

            if( newPos < 0 )
                throw new IOException( "An attempt was made to seek before the beginning of the stream." );

            // Seeking past the end is allowed by Stream contract; Read will just return 0.
            _position = newPos;
            return _position;
        }

        #endregion

        #region Read

        /// <inheritdoc/>
        /// <remarks>
        /// Internally dispatches to <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>
        /// via <see cref="Task.Run"/> to avoid blocking the caller's
        /// <see cref="System.Threading.SynchronizationContext"/> on async SQLite + crypto work.
        /// Prefer the async overloads from async callers.
        /// </remarks>
        public override int Read( byte [ ] buffer, int offset, int count )
        {
            ThrowIfDisposed( );
            return Task.Run( ( ) => ReadAsync( buffer.AsMemory( offset, count ) ).AsTask( ) )
                       .GetAwaiter( ).GetResult( );
        }

        /// <inheritdoc/>
        public override Task<int> ReadAsync(
            byte [ ]          buffer,
            int               offset,
            int               count,
            CancellationToken cancellationToken )
            => ReadAsync( buffer.AsMemory( offset, count ), cancellationToken ).AsTask( );

        /// <summary>
        /// Asynchronously reads up to <paramref name="buffer"/>.Length bytes from the
        /// current position, decrypting vault chunks as needed.
        /// </summary>
        /// <returns>
        /// The number of bytes written into <paramref name="buffer"/>, or 0 at end-of-stream.
        /// </returns>
        public override async ValueTask<int> ReadAsync(
            Memory<byte>      buffer,
            CancellationToken cancellationToken = default )
        {
            ThrowIfDisposed( );

            if( _position >= _length || buffer.IsEmpty )
                return 0;

            long available = _length - _position;
            int  toRead    = (int)Math.Min( buffer.Length, available );
            int  totalRead = 0;

            while( toRead > 0 )
            {
                int chunkIndex  = (int)( _position / _chunkSize );
                int chunkOffset = (int)( _position % _chunkSize );

                byte [ ] chunkData = await GetChunkAsync( chunkIndex, cancellationToken )
                    .ConfigureAwait( false );

                // The last chunk is usually shorter than _chunkSize
                int availableInChunk = chunkData.Length - chunkOffset;
                if( availableInChunk <= 0 ) break; // defensive - shouldn't happen on a valid vault

                int take = Math.Min( toRead, availableInChunk );
                chunkData.AsMemory( chunkOffset, take ).CopyTo( buffer[ totalRead.. ] );

                _position += take;
                totalRead += take;
                toRead    -= take;
            }

            return totalRead;
        }

        #endregion

        #region Write / Flush / SetLength (unsupported)

        /// <inheritdoc/>
        public override void Write( byte [ ] buffer, int offset, int count )
            => throw new NotSupportedException( $"{nameof( VaultReadStream )} is read-only." );

        /// <inheritdoc/>
        public override void Flush( ) { /* read-only - nothing to flush */ }

        /// <inheritdoc/>
        public override Task FlushAsync( CancellationToken cancellationToken ) => Task.CompletedTask;

        /// <inheritdoc/>
        public override void SetLength( long value )
            => throw new NotSupportedException( $"{nameof( VaultReadStream )} is read-only." );

        #endregion

        #region Dispose

        /// <inheritdoc/>
        protected override void Dispose( bool disposing )
        {
            if( !_disposed )
                _disposed = true;

            base.Dispose( disposing );
            // Note: VaultFile owns the connection - we must not close it here.
        }

        #endregion

        #region Private Helpers

        private async Task<byte [ ]> GetChunkAsync( int chunkIndex, CancellationToken cancellationToken )
        {
            if( _cache.TryGet( chunkIndex, out byte [ ]? cached ) )
                return cached!;

            byte [ ] data = await _fetchChunk( chunkIndex, cancellationToken ).ConfigureAwait( false );
            _cache.Put( chunkIndex, data );
            return data;
        }

        private void ThrowIfDisposed( )
        {
            ObjectDisposedException.ThrowIf( _disposed, this );
        }

        #endregion

        #region LRU Cache

        /// <summary>
        /// Fixed-capacity LRU cache for decrypted vault chunks.
        /// Most-recently-used entries are promoted to the front of a doubly-linked list;
        /// the entry at the tail is evicted when capacity is exceeded.
        /// </summary>
        private sealed class ChunkLruCache( int capacity )
        {
            private readonly Dictionary<int, LinkedListNode<CacheEntry>> _index = new( capacity + 1 );
            private readonly LinkedList<CacheEntry> _order = new( );

            /// <summary>
            /// Returns <c>true</c> and the cached data if <paramref name="chunkIndex"/>
            /// is present, promoting it to most-recently-used.
            /// </summary>
            public bool TryGet( int chunkIndex, out byte [ ]? data )
            {
                if( !_index.TryGetValue( chunkIndex, out LinkedListNode<CacheEntry>? node ) )
                {
                    data = null;
                    return false;
                }

                // Promote to front
                _order.Remove( node );
                _order.AddFirst( node );

                data = node.Value.Data;
                return true;
            }

            /// <summary>
            /// Inserts or refreshes an entry, evicting the LRU tail when at capacity.
            /// </summary>
            public void Put( int chunkIndex, byte [ ] data )
            {
                if( _index.TryGetValue( chunkIndex, out LinkedListNode<CacheEntry>? existing ) )
                {
                    _order.Remove( existing );
                    _index.Remove( chunkIndex );
                }

                LinkedListNode<CacheEntry> node = _order.AddFirst( new CacheEntry( chunkIndex, data ) );
                _index [ chunkIndex ] = node;

                if( _index.Count > capacity )
                {
                    LinkedListNode<CacheEntry> lru = _order.Last!;
                    _order.RemoveLast( );
                    _index.Remove( lru.Value.Index );
                }
            }

            private readonly record struct CacheEntry( int Index, byte [ ] Data );
        }

        #endregion
    }
}
