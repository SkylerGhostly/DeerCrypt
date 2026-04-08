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
            const string sql = """
                SELECT
                    (SELECT COUNT(*)                        FROM vault_files)       AS total_files,
                    (SELECT COUNT(*) - 1                    FROM vault_directories)  AS total_directories,
                    (SELECT COALESCE(SUM(original_size), 0) FROM vault_files)       AS total_size
                """;

            await using SqliteCommand    cmd    = new(sql, _connection);
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync( cancellationToken );

            int  totalFiles       = (int)(long)reader["total_files"];
            int  totalDirectories = (int)(long)reader["total_directories"];
            long totalSize        = (long)reader["total_size"];
            long vaultFileSize    = new FileInfo(_vaultPath).Length;

            byte[]?  createdAtBytes = await ReadMetaBlobAsync(_connection, MetaCreatedAt, cancellationToken);
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
            long freePages = await GetFreePageCountAsync(cancellationToken);

            if( freePages == 0 )
            {
                progress?.Report( 1.0 );
                return;
            }

            long totalToReclaim = freePages;
            while( true )
            {
                cancellationToken.ThrowIfCancellationRequested( );

                await ExecuteNonQueryAsync(
                    _connection,
                    $"PRAGMA incremental_vacuum({maxPagesPerStep})",
                    cancellationToken );

                // Check how many free pages remain
                long remaining = await GetFreePageCountAsync(cancellationToken);

                long reclaimed = totalToReclaim - remaining;
                progress?.Report( Math.Min( 1.0, (double)reclaimed / totalToReclaim ) );

                if( remaining == 0 )
                    break;

                // Yield to the thread pool between steps so other operations can proceed
                await Task.Yield( );
            }

            progress?.Report( 1.0 );
        }

        private async Task<long> GetFreePageCountAsync( CancellationToken cancellationToken )
        {
            await using SqliteCommand cmd = new("PRAGMA freelist_count", _connection);
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

            await using SqliteCommand cmd = new(sql, _connection);
            object? result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is long bytes ? bytes : 0;
        }

        /// <summary>
        /// Checks the vault for internal consistency issues -
        /// orphaned chunks, files with wrong chunk counts, etc.
        /// Returns a list of issues found, empty if the vault is healthy.
        /// </summary>
        public async Task<IReadOnlyList<string>> CheckIntegrityAsync(
            CancellationToken cancellationToken = default )
        {
            List<string> issues = [];

            // Check for files where actual chunk count doesn't match stored chunk_count
            const string chunkCountSql = """
                SELECT f.id, f.name, f.chunk_count, COUNT(c.chunk_index) AS actual_count
                FROM   vault_files f
                LEFT   JOIN vault_chunks c ON c.file_id = f.id
                GROUP  BY f.id
                HAVING f.chunk_count != actual_count
                """;

            await using SqliteCommand cmd1 = new(chunkCountSql, _connection);
            await using SqliteDataReader r1 = await cmd1.ExecuteReaderAsync(cancellationToken);
            while( await r1.ReadAsync( cancellationToken ) )
            {
                issues.Add(
                    $"File '{r1 [ "name" ]}' (ID: {r1 [ "id" ]}) declares " +
                    $"{r1 [ "chunk_count" ]} chunks but has {r1 [ "actual_count" ]}." );
            }

            // Check for orphaned chunks with no parent file
            const string orphanSql = """
                SELECT c.file_id, COUNT(*) AS orphan_count
                FROM   vault_chunks c
                LEFT   JOIN vault_files f ON f.id = c.file_id
                WHERE  f.id IS NULL
                GROUP  BY c.file_id
                """;

            await using SqliteCommand cmd2 = new(orphanSql, _connection);
            await using SqliteDataReader r2 = await cmd2.ExecuteReaderAsync(cancellationToken);
            while( await r2.ReadAsync( cancellationToken ) )
            {
                issues.Add(
                    $"Found {r2 [ "orphan_count" ]} orphaned chunk(s) for " +
                    $"missing file ID '{r2 [ "file_id" ]}'." );
            }

            // Check for files with no chunks at all
            const string noChunksSql = """
                SELECT f.id, f.name
                FROM   vault_files f
                WHERE  NOT EXISTS (
                    SELECT 1 FROM vault_chunks c WHERE c.file_id = f.id
                )
                """;

            await using SqliteCommand cmd3 = new(noChunksSql, _connection);
            await using SqliteDataReader r3 = await cmd3.ExecuteReaderAsync(cancellationToken);
            while( await r3.ReadAsync( cancellationToken ) )
            {
                issues.Add(
                    $"File '{r3 [ "name" ]}' (ID: {r3 [ "id" ]}) has no chunks." );
            }

            return issues;
        }
    }
}
