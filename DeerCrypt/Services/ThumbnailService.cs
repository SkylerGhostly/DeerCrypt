using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DeerCrypt.ViewModels;
using DeerCryptLib.Vault;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.Services
{
    /// <summary>How thumbnails are shown for a vault.</summary>
    public enum ThumbnailMode { Off, Blurred, On }

    /// <summary>
    /// Supplies thumbnails for the items on screen. Each is read from the vault's encrypted cache,
    /// or made in memory (images; cover art for audio) and stored there for next time. Work runs on
    /// two background workers, newest request first, so whatever you scrolled to is done first, and
    /// requests for tiles that scrolled away are dropped. Recently shown thumbnails are also kept
    /// in memory. Nothing is decrypted to disk. When thumbnails are off, nothing reads or writes.
    /// </summary>
    public sealed class ThumbnailService : IDisposable
    {
        public const long DefaultCacheLimitBytes = 256L * 1024 * 1024;

        private const int  WorkerCount     = 2;
        private const int  MemoryCapacity  = 400;
        private const int  TrimEveryPuts   = 25;
        private const long MaxImageSource  = 128L * 1024 * 1024; // larger images get no thumbnail

        private readonly VaultFile _vault;
        private readonly object    _lock = new( );

        // Pending requests, most recent first
        private readonly LinkedList<VaultItemViewModel>                           _queue  = [ ];
        private readonly Dictionary<VaultItemViewModel, LinkedListNode<VaultItemViewModel>> _queued = [ ];

        // In-memory cache: file id → thumbnail (null = the file has none)
        private readonly Dictionary<string, (Bitmap? Bitmap, LinkedListNode<string> Node)> _memory = [ ];
        private readonly LinkedList<string> _lru = [ ];

        private CancellationTokenSource _cts = new( );
        private int  _running;
        private int  _putsSinceTrim;
        private bool _disposed;

        public ThumbnailService( VaultFile vault ) => _vault = vault;

        /// <summary>When false, requests are ignored and nothing touches the vault.</summary>
        public bool Enabled { get; private set; }

        public long CacheLimitBytes { get; set; } = DefaultCacheLimitBytes;

        /// <summary>The vault's cache changed size (raised on a worker thread).</summary>
        public event Action? CacheChanged;

        public static bool IsEligible( VaultItemViewModel item ) =>
            item.IsFile && item.Kind is FileKind.Image or FileKind.Audio;

        public void SetEnabled( bool enabled )
        {
            if( Enabled == enabled ) return;
            Enabled = enabled;
            if( !enabled ) Reset( );
        }

        /// <summary>Drops pending work and the in-memory cache (e.g. after clearing the vault's cache).</summary>
        public void Reset( )
        {
            lock( _lock )
            {
                _cts.Cancel( );
                _cts = new CancellationTokenSource( );
                _queue.Clear( );
                _queued.Clear( );
                _memory.Clear( );
                _lru.Clear( );
            }
        }

        /// <summary>Asks for an item's thumbnail; it is set on the item when ready. Call on the UI thread.</summary>
        public void Request( VaultItemViewModel item )
        {
            if( !Enabled || _disposed || item.Thumbnail != null || !IsEligible( item ) ) return;

            lock( _lock )
            {
                if( _memory.TryGetValue( item.Id, out var hit ) )
                {
                    _lru.Remove( hit.Node );
                    _lru.AddFirst( hit.Node );
                    if( hit.Bitmap != null ) item.Thumbnail = hit.Bitmap;
                    return;
                }

                if( _queued.TryGetValue( item, out var node ) ) _queue.Remove( node );
                _queued [ item ] = _queue.AddFirst( item );

                while( _running < WorkerCount && _queue.Count > 0 )
                {
                    _running++;
                    var token = _cts.Token;
                    _ = Task.Run( ( ) => WorkAsync( token ) );
                }
            }
        }

        /// <summary>The item scrolled out of view: don't bother making its thumbnail now.</summary>
        public void Cancel( VaultItemViewModel item )
        {
            lock( _lock )
            {
                if( _queued.Remove( item, out var node ) ) _queue.Remove( node );
            }
        }

        private async Task WorkAsync( CancellationToken ct )
        {
            try
            {
                while( true )
                {
                    VaultItemViewModel item;
                    lock( _lock )
                    {
                        if( ct.IsCancellationRequested || _queue.First is not { } first )
                            break;
                        item = first.Value;
                        _queue.RemoveFirst( );
                        _queued.Remove( item );
                    }

                    Bitmap? bitmap;
                    try
                    {
                        bitmap = await LoadAsync( item, ct );
                    }
                    catch( OperationCanceledException ) { break; }
                    catch( ObjectDisposedException )    { break; } // vault locked
                    catch
                    {
                        continue; // a read error: try again next time it's shown
                    }

                    if( ct.IsCancellationRequested ) break;
                    Remember( item.Id, bitmap );
                    if( bitmap != null )
                        Dispatcher.UIThread.Post( ( ) => { if( Enabled ) item.Thumbnail = bitmap; } );
                }
            }
            finally
            {
                bool last;
                lock( _lock ) last = --_running == 0;

                // Write the batch of "last used" days once the queue drains
                if( last && !_disposed )
                {
                    try { await _vault.FlushThumbnailUsageAsync( CancellationToken.None ); } catch { }
                }
            }
        }

        private async Task<Bitmap?> LoadAsync( VaultItemViewModel item, CancellationToken ct )
        {
            var file = item.AsFile!;

            byte [ ]? cached = await _vault.GetThumbnailAsync( file.Id, ct );
            if( cached != null )
                return cached.Length == 0 ? null : Decode( cached );

            byte [ ]? made = null;
            if( item.Kind == FileKind.Image )
            {
                if( file.OriginalSize <= MaxImageSource )
                {
                    await using var stream = await _vault.OpenReadStreamAsync( file.Id, ct );
                    var buffer = new MemoryStream( (int)Math.Max( 0, file.OriginalSize ) );
                    await stream.CopyToAsync( buffer, ct );
                    made = ThumbnailMaker.FromImage( buffer.GetBuffer( ).AsSpan( 0, (int)buffer.Length ).ToArray( ) );
                }
            }
            else
            {
                // Tags are read straight from the encrypted stream (only the parts TagLib needs)
                await using var stream = await _vault.OpenReadStreamAsync( file.Id, ct );
                made = ThumbnailMaker.FromAudio( stream, file.Name );
            }
            ct.ThrowIfCancellationRequested( );

            // Store the result, or an empty marker so a file without one isn't retried
            await _vault.PutThumbnailAsync( file.Id, made ?? ReadOnlyMemory<byte>.Empty, ct );
            CacheChanged?.Invoke( );

            if( Interlocked.Increment( ref _putsSinceTrim ) >= TrimEveryPuts )
            {
                Interlocked.Exchange( ref _putsSinceTrim, 0 );
                await TrimAsync( ct );
            }

            return made == null ? null : Decode( made );
        }

        public async Task TrimAsync( CancellationToken ct = default )
        {
            if( await _vault.TrimThumbnailCacheAsync( CacheLimitBytes, ct ) > 0 )
                CacheChanged?.Invoke( );
        }

        private static Bitmap? Decode( byte [ ] data )
        {
            try { return new Bitmap( new MemoryStream( data ) ); }
            catch { return null; }
        }

        private void Remember( string id, Bitmap? bitmap )
        {
            lock( _lock )
            {
                if( _memory.TryGetValue( id, out var existing ) ) _lru.Remove( existing.Node );
                _memory [ id ] = (bitmap, _lru.AddFirst( id ));

                // Evicted bitmaps aren't disposed: a tile may still be drawing them (the GC frees them)
                while( _lru.Count > MemoryCapacity && _lru.Last is { } oldest )
                {
                    _lru.RemoveLast( );
                    _memory.Remove( oldest.Value );
                }
            }
        }

        public void Dispose( )
        {
            _disposed = true;
            Enabled   = false;
            Reset( );
        }
    }
}
