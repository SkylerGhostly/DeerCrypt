using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DeerCryptLib.Vault
{
    /// <summary>Size of a vault's thumbnail cache.</summary>
    public sealed record ThumbnailCacheInfo( int Count, long Bytes );

    public sealed partial class VaultFile
    {
        // Thumbnail cache and per-vault settings.
        //
        // Format safety: both are purely additive. The vault_thumbnails table is created the first
        // time a thumbnail is stored (never on open), and the vault version is NOT changed, so
        // earlier DeerCrypt versions open the vault exactly as before and simply ignore the table.
        // Rows reference vault_files with ON DELETE CASCADE, so deleting a file in any version
        // removes its thumbnail too. Each thumbnail is encrypted with the vault's master key
        // (AES-GCM, like file chunks) and remembers the SHA-256 of the file it was made from, so a
        // file whose contents change (even by an older version) never shows a stale thumbnail.

        private const string ThumbnailTable = "vault_thumbnails";
        private const string SettingPrefix  = "app.";

        private volatile int _thumbTableState; // 0 = unknown, 1 = exists, 2 = missing
        private readonly HashSet<string> _pendingThumbTouches = [ ];

        private static long Today => DateTime.UtcNow.Ticks / TimeSpan.TicksPerDay;

        private async Task<bool> ThumbnailTableExistsAsync( SqliteConnection db, CancellationToken cancellationToken )
        {
            if( _thumbTableState != 0 ) return _thumbTableState == 1;

            await using SqliteCommand cmd = new( "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name", db );
            cmd.Parameters.AddWithValue( "@name", ThumbnailTable );
            bool exists = await cmd.ExecuteScalarAsync( cancellationToken ) != null;
            _thumbTableState = exists ? 1 : 2;
            return exists;
        }

        private async Task EnsureThumbnailTableAsync( SqliteConnection writer, CancellationToken cancellationToken )
        {
            if( await ThumbnailTableExistsAsync( writer, cancellationToken ) ) return;

            const string sql = """
                CREATE TABLE IF NOT EXISTS vault_thumbnails (
                    file_id        TEXT    PRIMARY KEY REFERENCES vault_files(id) ON DELETE CASCADE,
                    source_sha256  BLOB    NOT NULL,
                    data           BLOB    NOT NULL,
                    size           INTEGER NOT NULL,
                    last_used      INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_thumbnails_last_used ON vault_thumbnails(last_used);
                """;
            await ExecuteNonQueryAsync( writer, sql, cancellationToken );
            _thumbTableState = 1;
        }

        /// <summary>
        /// The cached thumbnail for a file: null when there is none (or it was made from older
        /// contents of the file), an empty array when the file is known to have no thumbnail
        /// (e.g. an image that couldn't be decoded), otherwise the decrypted image bytes.
        /// </summary>
        public async Task<byte [ ]?> GetThumbnailAsync( string fileId, CancellationToken cancellationToken = default )
        {
            using var lease = await EnterReadAsync( cancellationToken );
            var db = lease.Db;
            if( !await ThumbnailTableExistsAsync( db, cancellationToken ) ) return null;

            const string sql = """
                SELECT t.data, t.last_used
                FROM   vault_thumbnails t
                JOIN   vault_files f ON f.id = t.file_id
                WHERE  t.file_id = @id AND t.source_sha256 = f.sha256
                """;
            await using SqliteCommand cmd = new( sql, db );
            cmd.Parameters.AddWithValue( "@id", fileId );
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync( cancellationToken );
            if( !await reader.ReadAsync( cancellationToken ) ) return null;

            byte [ ] encrypted = (byte [ ])reader [ "data" ];
            if( (long)reader [ "last_used" ] < Today )
                lock( _pendingThumbTouches ) _pendingThumbTouches.Add( fileId );

            if( encrypted.Length == 0 ) return [ ];
            using MemoryStream output = new( );
            await DecryptChunkToStreamAsync( encrypted, output, cancellationToken );
            return output.ToArray( );
        }

        /// <summary>
        /// Stores (or replaces) a file's thumbnail. Pass an empty image to remember that the file
        /// has no thumbnail, so it isn't attempted again until its contents change.
        /// Does nothing if the file no longer exists.
        /// </summary>
        public async Task PutThumbnailAsync( string fileId, ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default )
        {
            using var lease = await EnterWriteAsync( cancellationToken );
            var db = lease.Db;

            byte [ ]? sha;
            await using( SqliteCommand shaCmd = new( "SELECT sha256 FROM vault_files WHERE id = @id", db ) )
            {
                shaCmd.Parameters.AddWithValue( "@id", fileId );
                sha = await shaCmd.ExecuteScalarAsync( cancellationToken ) as byte [ ];
            }
            if( sha == null ) return; // deleted meanwhile

            await EnsureThumbnailTableAsync( db, cancellationToken );
            byte [ ] encrypted = image.IsEmpty ? [ ] : await EncryptChunkAsync( image, cancellationToken );

            const string sql = """
                INSERT OR REPLACE INTO vault_thumbnails (file_id, source_sha256, data, size, last_used)
                VALUES (@id, @sha, @data, @size, @today)
                """;
            await using SqliteCommand cmd = new( sql, db );
            cmd.Parameters.AddWithValue( "@id", fileId );
            cmd.Parameters.AddWithValue( "@sha", sha );
            cmd.Parameters.AddWithValue( "@data", encrypted );
            cmd.Parameters.AddWithValue( "@size", encrypted.LongLength );
            cmd.Parameters.AddWithValue( "@today", Today );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        /// <summary>
        /// Writes the "last used" day of thumbnails read since the last flush, in one transaction.
        /// Each thumbnail's day is written at most once per day, so browsing causes few writes.
        /// </summary>
        public async Task FlushThumbnailUsageAsync( CancellationToken cancellationToken = default )
        {
            string [ ] ids;
            lock( _pendingThumbTouches )
            {
                if( _pendingThumbTouches.Count == 0 ) return;
                ids = [ .. _pendingThumbTouches ];
                _pendingThumbTouches.Clear( );
            }

            using var lease = await EnterWriteAsync( cancellationToken );
            var db = lease.Db;
            if( !await ThumbnailTableExistsAsync( db, cancellationToken ) ) return;

            await using SqliteTransaction tx = (SqliteTransaction)await db.BeginTransactionAsync( cancellationToken );
            await using SqliteCommand cmd = new( "UPDATE vault_thumbnails SET last_used = @today WHERE file_id = @id", db, tx );
            var idParam = cmd.Parameters.Add( "@id", SqliteType.Text );
            cmd.Parameters.AddWithValue( "@today", Today );
            foreach( string id in ids )
            {
                idParam.Value = id;
                await cmd.ExecuteNonQueryAsync( cancellationToken );
            }
            await tx.CommitAsync( cancellationToken );
        }

        public async Task<ThumbnailCacheInfo> GetThumbnailCacheInfoAsync( CancellationToken cancellationToken = default )
        {
            using var lease = await EnterReadAsync( cancellationToken );
            var db = lease.Db;
            if( !await ThumbnailTableExistsAsync( db, cancellationToken ) ) return new ThumbnailCacheInfo( 0, 0 );

            await using SqliteCommand cmd = new( "SELECT COUNT(*), COALESCE(SUM(size), 0) FROM vault_thumbnails", db );
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync( cancellationToken );
            await r.ReadAsync( cancellationToken );
            return new ThumbnailCacheInfo( (int)r.GetInt64( 0 ), r.GetInt64( 1 ) );
        }

        /// <summary>
        /// Keeps the cache under <paramref name="maxBytes"/>: when it's over, the least recently
        /// used thumbnails are removed until it's at 90% of the limit. Returns how many were removed.
        /// </summary>
        public async Task<int> TrimThumbnailCacheAsync( long maxBytes, CancellationToken cancellationToken = default )
        {
            using var lease = await EnterWriteAsync( cancellationToken );
            var db = lease.Db;
            if( !await ThumbnailTableExistsAsync( db, cancellationToken ) ) return 0;

            long total;
            await using( SqliteCommand sum = new( "SELECT COALESCE(SUM(size), 0) FROM vault_thumbnails", db ) )
                total = (long)( await sum.ExecuteScalarAsync( cancellationToken ) ?? 0L );
            if( total <= maxBytes ) return 0;

            long target = maxBytes * 9 / 10;
            List<string> victims = [ ];
            await using( SqliteCommand oldest = new( "SELECT file_id, size FROM vault_thumbnails ORDER BY last_used, rowid", db ) )
            await using( SqliteDataReader r = await oldest.ExecuteReaderAsync( cancellationToken ) )
            {
                while( total > target && await r.ReadAsync( cancellationToken ) )
                {
                    victims.Add( r.GetString( 0 ) );
                    total -= r.GetInt64( 1 );
                }
            }

            await using SqliteTransaction tx = (SqliteTransaction)await db.BeginTransactionAsync( cancellationToken );
            await using SqliteCommand del = new( "DELETE FROM vault_thumbnails WHERE file_id = @id", db, tx );
            var idParam = del.Parameters.Add( "@id", SqliteType.Text );
            foreach( string id in victims )
            {
                idParam.Value = id;
                await del.ExecuteNonQueryAsync( cancellationToken );
            }
            await tx.CommitAsync( cancellationToken );
            return victims.Count;
        }

        /// <summary>
        /// Deletes every thumbnail and the cache table itself, returning the vault's schema to
        /// exactly what it was before thumbnails were used. The freed space is reclaimed by
        /// <see cref="CompactAsync"/>.
        /// </summary>
        public async Task ClearThumbnailCacheAsync( CancellationToken cancellationToken = default )
        {
            using var lease = await EnterWriteAsync( cancellationToken );
            lock( _pendingThumbTouches ) _pendingThumbTouches.Clear( );
            await ExecuteNonQueryAsync( lease.Db, "DROP TABLE IF EXISTS vault_thumbnails", cancellationToken );
            _thumbTableState = 2;
        }

        // Per-vault settings (stored in vault_meta under an "app." prefix so they can never
        // collide with the vault's own keys)

        public async Task<string?> GetSettingAsync( string name, CancellationToken cancellationToken = default )
        {
            using var lease = await EnterReadAsync( cancellationToken );
            byte [ ]? value = await ReadMetaBlobAsync( lease.Db, SettingPrefix + name, cancellationToken );
            return value == null ? null : System.Text.Encoding.UTF8.GetString( value );
        }

        /// <summary>Stores a per-vault setting; null removes it.</summary>
        public async Task SetSettingAsync( string name, string? value, CancellationToken cancellationToken = default )
        {
            using var lease = await EnterWriteAsync( cancellationToken );
            var db = lease.Db;

            if( value == null )
            {
                await using SqliteCommand del = new( "DELETE FROM vault_meta WHERE key = @key", db );
                del.Parameters.AddWithValue( "@key", SettingPrefix + name );
                await del.ExecuteNonQueryAsync( cancellationToken );
                return;
            }

            await using SqliteCommand cmd = new( "INSERT OR REPLACE INTO vault_meta (key, value) VALUES (@key, @value)", db );
            cmd.Parameters.AddWithValue( "@key", SettingPrefix + name );
            cmd.Parameters.AddWithValue( "@value", System.Text.Encoding.UTF8.GetBytes( value ) );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }
    }
}
