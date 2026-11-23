using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeerCryptLib.Vault
{
    public sealed partial class VaultFile
    {
        /// <summary>
        /// Returns aggregate vault statistics - file count, directory count, total size,
        /// physical file size on disk, and creation date.
        /// </summary>
        public async Task<VaultInfo> GetVaultInfoAsync( CancellationToken cancellationToken = default )
        {
            using var lease = await EnterReadAsync( cancellationToken );
            var db = lease.Db;

            const string sql = """
                SELECT
                    (SELECT COUNT(*)                        FROM vault_files)       AS total_files,
                    (SELECT COUNT(*) - 1                    FROM vault_directories)  AS total_directories,
                    (SELECT COALESCE(SUM(original_size), 0) FROM vault_files)       AS total_size
                """;

            await using SqliteCommand    cmd    = new(sql, db);
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync( cancellationToken );

            int  totalFiles       = (int)(long)reader["total_files"];
            int  totalDirectories = (int)(long)reader["total_directories"];
            long totalSize        = (long)reader["total_size"];
            // While open in WAL mode, recent changes live in the -wal file until checkpointed
            long vaultFileSize    = new FileInfo(_vaultPath).Length
                                  + ( File.Exists( _vaultPath + "-wal" ) ? new FileInfo( _vaultPath + "-wal" ).Length : 0 );

            byte[]?  createdAtBytes = await ReadMetaBlobAsync(db, MetaCreatedAt, cancellationToken);
            DateTime createdAt      = createdAtBytes != null
                ? DateTime.Parse(
                    System.Text.Encoding.UTF8.GetString(createdAtBytes),
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind)
                : DateTime.MinValue;

            return new VaultInfo(
                TotalFiles: totalFiles,
                TotalDirectories: totalDirectories,
                TotalOriginalSize: totalSize,
                VaultFileSize: vaultFileSize,
                CreatedAt: createdAt );
        }

        /// <summary>
        /// Reclaims freed disk space in small incremental steps.
        /// Yields between steps so the UI stays responsive.
        /// </summary>
        /// <param name="maxPagesPerStep">
        ///    SQLite pages to vacuum per step. At the default 4096-byte page size,
        ///    256 pages = 1 MB per step. Tune this to control pause length vs. progress speed.
        /// </param>
        public async Task CompactAsync(
            int maxPagesPerStep = 256,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            // How much work is there to do?
            long freePages;
            using( var lease = await EnterWriteAsync( cancellationToken ) )
                freePages = await GetFreePageCountAsync( lease.Db, cancellationToken );

            if( freePages == 0 )
            {
                progress?.Report( 1.0 );
                return;
            }

            long totalToReclaim = freePages;
            while( true )
            {
                cancellationToken.ThrowIfCancellationRequested( );

                // One step per gate acquisition so other work can interleave
                long remaining;
                using( var lease = await EnterWriteAsync( cancellationToken ) )
                {
                    var db = lease.Db;
                    await ExecuteNonQueryAsync(
                        db,
                        $"PRAGMA incremental_vacuum({maxPagesPerStep})",
                        cancellationToken );

                    // Check how many free pages remain
                    remaining = await GetFreePageCountAsync( db, cancellationToken );
                }

                long reclaimed = totalToReclaim - remaining;
                progress?.Report( Math.Min( 1.0, (double)reclaimed / totalToReclaim ) );

                if( remaining == 0 )
                    break;

                // Yield to the thread pool between steps so other operations can proceed
                await Task.Yield( );
            }

            progress?.Report( 1.0 );
        }

        private async Task<long> GetFreePageCountAsync( SqliteConnection db, CancellationToken cancellationToken )
        {
            await using SqliteCommand cmd = new("PRAGMA freelist_count", db);
            object? result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is long count ? count : 0;
        }

        /// <summary>
        /// Returns the number of bytes that would be reclaimed by CompactAsync.
        /// Calculated as free_pages * page_size.
        /// </summary>
        public async Task<long> GetReclaimableBytesAsync( CancellationToken cancellationToken = default )
        {
            const string sql = """
            SELECT freelist_count * page_size
            FROM   pragma_freelist_count(), pragma_page_size()
            """;

            using var lease = await EnterReadAsync( cancellationToken );
            var db = lease.Db;

            await using SqliteCommand cmd = new(sql, db);
            object? result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is long bytes ? bytes : 0;
        }

        /// <summary>
        /// Checks the vault for internal consistency issues -
        /// orphaned chunks, files with wrong chunk counts, etc.
        /// Returns a list of issues found, empty if the vault is healthy.
        /// </summary>
        public Task<IReadOnlyList<string>> CheckIntegrityAsync(
            CancellationToken cancellationToken = default )
            => CheckIntegrityAsync( null, cancellationToken );

        /// <summary>
        /// Checks the vault for internal consistency issues - files whose stored chunk count
        /// doesn't match the chunks present, files with no chunks, and orphaned chunks.
        /// Works through files in batches on a reader connection, so it never blocks browsing
        /// or writes, and reports 0.0-1.0 progress. Returns the issues found (empty if healthy).
        /// </summary>
        public async Task<IReadOnlyList<string>> CheckIntegrityAsync(
            IProgress<double>? progress,
            CancellationToken cancellationToken = default )
        {
            const int BatchSize = 2000;
            List<string> issues = [];

            long totalFiles;
            using( var lease = await EnterReadAsync( cancellationToken ) )
            {
                await using SqliteCommand count = new( "SELECT COUNT(*) FROM vault_files", lease.Db );
                totalFiles = (long)( await count.ExecuteScalarAsync( cancellationToken ) ?? 0L );
            }

            // 1. Per-file chunk counts, a batch at a time (90% of the progress bar)
            const string batchSql = """
                SELECT f.rowid AS rid, f.id, f.name, f.chunk_count,
                       (SELECT COUNT(*) FROM vault_chunks c WHERE c.file_id = f.id) AS actual_count
                FROM   vault_files f
                WHERE  f.rowid > @after
                ORDER  BY f.rowid
                LIMIT  @limit
                """;

            long after = long.MinValue;
            long checkedFiles = 0;
            while( true )
            {
                cancellationToken.ThrowIfCancellationRequested( );
                int rows = 0;

                using( var lease = await EnterReadAsync( cancellationToken ) )
                {
                    await using SqliteCommand cmd = new( batchSql, lease.Db );
                    cmd.Parameters.AddWithValue( "@after", after );
                    cmd.Parameters.AddWithValue( "@limit", BatchSize );

                    await using SqliteDataReader r = await cmd.ExecuteReaderAsync( cancellationToken );
                    while( await r.ReadAsync( cancellationToken ) )
                    {
                        rows++;
                        after = (long)r [ "rid" ];
                        long declared = (long)r [ "chunk_count" ];
                        long actual   = (long)r [ "actual_count" ];

                        if( actual == 0 )
                            issues.Add( $"File '{r [ "name" ]}' (ID: {r [ "id" ]}) has no chunks." );
                        else if( declared != actual )
                            issues.Add( $"File '{r [ "name" ]}' (ID: {r [ "id" ]}) declares {declared} chunks but has {actual}." );
                    }
                }

                checkedFiles += rows;
                progress?.Report( totalFiles == 0 ? 0.9 : 0.9 * Math.Min( 1.0, (double)checkedFiles / totalFiles ) );
                if( rows < BatchSize ) break;
            }

            // 2. Orphaned chunks with no parent file (one index scan, last 10%)
            const string orphanSql = """
                SELECT c.file_id, COUNT(*) AS orphan_count
                FROM   vault_chunks c
                LEFT   JOIN vault_files f ON f.id = c.file_id
                WHERE  f.id IS NULL
                GROUP  BY c.file_id
                """;

            using( var lease = await EnterReadAsync( cancellationToken ) )
            {
                await using SqliteCommand cmd = new( orphanSql, lease.Db );
                await using SqliteDataReader r = await cmd.ExecuteReaderAsync( cancellationToken );
                while( await r.ReadAsync( cancellationToken ) )
                {
                    issues.Add(
                        $"Found {r [ "orphan_count" ]} orphaned chunk(s) for " +
                        $"missing file ID '{r [ "file_id" ]}'." );
                }
            }

            progress?.Report( 1.0 );
            return issues;
        }
    }
}
