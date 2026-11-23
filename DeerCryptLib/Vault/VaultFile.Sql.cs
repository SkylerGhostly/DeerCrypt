using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeerCryptLib.Vault
{
    public sealed partial class VaultFile
    {
        #region Internals - Connection & Schema

        /// <summary>
        /// Opens a connection keyed with the vault password through Microsoft.Data.Sqlite's own
        /// <c>Password=</c> support (the same mechanism vaults have always been opened with).
        /// This also works on a vault left in WAL mode by a crash. Note: the library keeps the
        /// password in the connection's connection string while the connection is open.
        /// Throws <see cref="VaultPasswordException"/> if the password doesn't decrypt the file.
        /// </summary>
        private static async Task<SqliteConnection> OpenConnectionAsync(
            string path,
            string password,
            SqliteOpenMode mode,
            CancellationToken cancellationToken )
        {
            SQLitePCL.Batteries_V2.Init( );

            SqliteConnectionStringBuilder builder = new()
            {
                DataSource = path,
                Mode       = mode,
                Password   = password,
                Pooling    = false, // we manage our own connection lifetime and want to ensure proper disposal
            };

            SqliteConnection connection = new(builder.ToString());

            try
            {
                await connection.OpenAsync( cancellationToken );
                return connection;
            }
            catch( SqliteException ex ) when( ex.SqliteErrorCode == 26 )
            {
                await connection.DisposeAsync( );
                throw new VaultPasswordException( "The password is incorrect.", ex );
            }
            catch
            {
                await connection.DisposeAsync( );
                throw;
            }
        }

        /// <summary>
        /// Switches the writer to WAL and opens the reader pool. Returns no readers (and leaves the
        /// vault in rollback mode) if WAL can't be enabled on this filesystem; reads then share the writer.
        /// </summary>
        private static async Task<SqliteConnection [ ]> OpenReadersAsync(
            SqliteConnection writer,
            string path,
            string password,
            CancellationToken cancellationToken )
        {
            string? mode;
            await using( SqliteCommand cmd = new( "PRAGMA journal_mode = WAL;", writer ) )
                mode = await cmd.ExecuteScalarAsync( cancellationToken ) as string;

            if( !string.Equals( mode, "wal", StringComparison.OrdinalIgnoreCase ) )
                return [ ];

            // Durability: sync the WAL on every commit. Keep the WAL file from staying huge after
            // big transactions once they're checkpointed.
            await ExecuteNonQueryAsync( writer, "PRAGMA synchronous = FULL",               cancellationToken );
            await ExecuteNonQueryAsync( writer, "PRAGMA journal_size_limit = 67108864",    cancellationToken );

            List<SqliteConnection> readers = [ ];
            try
            {
                for( int i = 0; i < ReaderCount; i++ )
                {
                    SqliteConnection reader = await OpenConnectionAsync( path, password, SqliteOpenMode.ReadWrite, cancellationToken );
                    readers.Add( reader );
                    await ExecuteNonQueryAsync( reader, "PRAGMA busy_timeout = 30000", cancellationToken );
                    await ExecuteNonQueryAsync( reader, "PRAGMA query_only = ON",      cancellationToken );
                }
                return [ .. readers ];
            }
            catch
            {
                foreach( var r in readers ) await r.DisposeAsync( );
                throw;
            }
        }

        /// <summary>
        /// Folds the WAL back into the vault file and returns it to rollback-journal mode so the
        /// locked vault is a single file again. Best effort: if another process still has the
        /// vault open this fails harmlessly and the vault simply stays in (valid) WAL mode.
        /// </summary>
        private static async Task RestoreRollbackJournalAsync( SqliteConnection writer )
        {
            try
            {
                await ExecuteNonQueryAsync( writer, "PRAGMA wal_checkpoint(TRUNCATE)", CancellationToken.None );
                await ExecuteNonQueryAsync( writer, "PRAGMA journal_mode = DELETE",    CancellationToken.None );
            }
            catch { }
        }

        private static void DeleteSidecarFiles( string path )
        {
            foreach( string suffix in new [ ] { "-wal", "-shm", "-journal" } )
            {
                try { if( File.Exists( path + suffix ) ) File.Delete( path + suffix ); } catch { }
            }
        }

        private static async Task InitializePragmasAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken )
        {
            await ExecuteNonQueryAsync( connection, "PRAGMA busy_timeout = 30000",  cancellationToken );
            await ExecuteNonQueryAsync( connection, "PRAGMA foreign_keys = ON",    cancellationToken );
            await ExecuteNonQueryAsync( connection, "PRAGMA synchronous = NORMAL", cancellationToken );
            // All pages are ciphertext - zeroing freed pages adds write overhead with no security benefit.
            await ExecuteNonQueryAsync( connection, "PRAGMA secure_delete = OFF",  cancellationToken );
        }

        private static async Task CreateSchemaAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken )
        {
            // page_size and auto_vacuum must be set before any tables are created
            await ExecuteNonQueryAsync( connection, "PRAGMA page_size = 65536",          cancellationToken );
            await ExecuteNonQueryAsync( connection, "PRAGMA auto_vacuum = INCREMENTAL",  cancellationToken );

            const string sql = """
                CREATE TABLE IF NOT EXISTS vault_meta (
                    key   TEXT PRIMARY KEY,
                    value BLOB NOT NULL
                );

                CREATE TABLE IF NOT EXISTS vault_directories (
                    id         TEXT PRIMARY KEY,
                    name       TEXT NOT NULL,
                    parent_id  TEXT REFERENCES vault_directories(id) ON DELETE CASCADE,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS vault_files (
                    id             TEXT    PRIMARY KEY,
                    name           TEXT    NOT NULL,
                    directory_id   TEXT    NOT NULL REFERENCES vault_directories(id) ON DELETE CASCADE,
                    original_size  INTEGER NOT NULL,
                    chunk_size     INTEGER NOT NULL,
                    chunk_count    INTEGER NOT NULL,
                    sha256         BLOB    NOT NULL,
                    added_at       TEXT    NOT NULL
                );

                CREATE TABLE IF NOT EXISTS vault_chunks (
                    file_id      TEXT    NOT NULL REFERENCES vault_files(id) ON DELETE CASCADE,
                    chunk_index  INTEGER NOT NULL,
                    data         BLOB    NOT NULL,
                    PRIMARY KEY (file_id, chunk_index)
                );

                CREATE INDEX IF NOT EXISTS idx_files_directory ON vault_files(directory_id);
                CREATE INDEX IF NOT EXISTS idx_chunks_file     ON vault_chunks(file_id, chunk_index);
                """;

            await ExecuteNonQueryAsync( connection, sql, cancellationToken );
        }

        private static async Task CreateRootDirectoryAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken )
        {
            const string sql = """
                INSERT INTO vault_directories (id, name, parent_id, created_at)
                VALUES (@id, @name, NULL, @createdAt)
                """;

            await using SqliteCommand cmd = new(sql, connection);
            cmd.Parameters.AddWithValue( "@id", RootDirId );
            cmd.Parameters.AddWithValue( "@name", "root" );
            cmd.Parameters.AddWithValue( "@createdAt", DateTime.UtcNow.ToString( "O" ) );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        #endregion

        #region Internals - SQL Helpers

        private static async Task InsertMetaAsync(
            SqliteConnection connection,
            string key,
            byte [ ] value,
            CancellationToken cancellationToken )
        {
            const string sql = "INSERT INTO vault_meta (key, value) VALUES (@key, @value)";

            await using SqliteCommand cmd = new(sql, connection);
            cmd.Parameters.AddWithValue( "@key", key );
            cmd.Parameters.AddWithValue( "@value", value );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        private static async Task<byte [ ]?> ReadMetaBlobAsync(
            SqliteConnection connection,
            string key,
            CancellationToken cancellationToken )
        {
            const string sql = "SELECT value FROM vault_meta WHERE key = @key";

            await using SqliteCommand cmd = new(sql, connection);
            cmd.Parameters.AddWithValue( "@key", key );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync( cancellationToken )
                ? (byte [ ])reader [ "value" ]
                : null;
        }

        /// <summary>
        /// Reads a VaultEntry from vault_files by file ID. Returns null if not found.
        /// Used by ExtractAsync - does NOT include sha256, that's fetched separately.
        /// </summary>
        private async Task<VaultEntry?> ReadVaultEntryAsync(
            SqliteConnection db,
            string fileId,
            CancellationToken cancellationToken )
        {
            const string sql = """
                SELECT id, name, directory_id, original_size, chunk_count, added_at
                FROM   vault_files
                WHERE  id = @id
                """;

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@id", fileId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync( cancellationToken )
                ? ReadVaultEntry( reader )
                : null;
        }

        private async Task<byte [ ]> ReadFileSha256Async(
            SqliteConnection db,
            string fileId,
            CancellationToken cancellationToken )
        {
            const string sql = "SELECT sha256 FROM vault_files WHERE id = @id";

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@id", fileId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if( !await reader.ReadAsync( cancellationToken ) )
                throw new VaultCorruptException( $"SHA-256 record missing for file '{fileId}'." );

            return (byte [ ])reader [ "sha256" ];
        }

        private async Task<byte [ ]?> ReadVaultChunkAsync(
            SqliteConnection db,
            string fileId,
            int chunkIndex,
            CancellationToken cancellationToken )
        {
            const string sql = """
                SELECT data FROM vault_chunks
                WHERE file_id = @fileId AND chunk_index = @chunkIndex
                """;

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@fileId", fileId );
            cmd.Parameters.AddWithValue( "@chunkIndex", chunkIndex );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync( cancellationToken )
                ? (byte [ ])reader [ "data" ]
                : null;
        }

        private static async Task InsertVaultFileAsync(
            SqliteTransaction transaction,
            string fileId,
            string fileName,
            string directoryId,
            long originalSize,
            int chunkSize,
            int chunkCount,
            byte [ ] sha256,
            CancellationToken cancellationToken )
        {
            const string sql = """
                INSERT INTO vault_files
                    (id, name, directory_id, original_size, chunk_size, chunk_count, sha256, added_at)
                VALUES
                    (@id, @name, @directoryId, @originalSize, @chunkSize, @chunkCount, @sha256, @addedAt)
                """;

            await using SqliteCommand cmd = new(sql, transaction.Connection, transaction);
            cmd.Parameters.AddWithValue( "@id", fileId );
            cmd.Parameters.AddWithValue( "@name", fileName );
            cmd.Parameters.AddWithValue( "@directoryId", directoryId );
            cmd.Parameters.AddWithValue( "@originalSize", originalSize );
            cmd.Parameters.AddWithValue( "@chunkSize", chunkSize );
            cmd.Parameters.AddWithValue( "@chunkCount", chunkCount );
            cmd.Parameters.AddWithValue( "@sha256", sha256 );
            cmd.Parameters.AddWithValue( "@addedAt", DateTime.UtcNow.ToString( "O" ) );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        private static async Task InsertVaultChunkAsync(
            SqliteTransaction transaction,
            string fileId,
            int chunkIndex,
            byte [ ] data,
            CancellationToken cancellationToken )
        {
            const string sql = """
                INSERT INTO vault_chunks (file_id, chunk_index, data)
                VALUES (@fileId, @chunkIndex, @data)
                """;

            await using SqliteCommand cmd = new(sql, transaction.Connection, transaction);
            cmd.Parameters.AddWithValue( "@fileId", fileId );
            cmd.Parameters.AddWithValue( "@chunkIndex", chunkIndex );
            cmd.Parameters.AddWithValue( "@data", data );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        /// <summary>
        /// Creates a pre-compiled <see cref="SqliteCommand"/> for chunk insertion.
        /// Reuse this command across an entire insert loop - update parameter
        /// <c>Value</c> properties each iteration instead of recreating the command.
        /// Dispose after the loop completes.
        /// </summary>
        private static SqliteCommand CreatePreparedChunkInsertCommand( SqliteTransaction transaction )
        {
            const string sql = """
                INSERT INTO vault_chunks (file_id, chunk_index, data)
                VALUES (@fileId, @chunkIndex, @data)
                """;

            SqliteCommand cmd = new(sql, transaction.Connection, transaction);
            cmd.Parameters.Add( "@fileId",     SqliteType.Text    );
            cmd.Parameters.Add( "@chunkIndex", SqliteType.Integer );
            cmd.Parameters.Add( "@data",       SqliteType.Blob    );
            cmd.Prepare( );
            return cmd;
        }

        private async Task<int> GetChunkCountAsync(
            SqliteConnection db,
            string fileId,
            CancellationToken cancellationToken )
        {
            const string sql = "SELECT chunk_count FROM vault_files WHERE id = @id";

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@id", fileId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync( cancellationToken )
                ? (int)(long)reader [ "chunk_count" ]
                : 0;
        }

        private async Task<int> GetTotalChunkCountAsync(
            SqliteConnection db,
            List<string> fileIds,
            CancellationToken cancellationToken )
        {
            if( fileIds.Count == 0 ) return 0;

            string paramList = string.Join(", ", fileIds.Select((_, i) => $"@id{i}"));
            string sql       = $"SELECT SUM(chunk_count) FROM vault_files WHERE id IN ({paramList})";

            await using SqliteCommand cmd = new(sql, db);
            for( int i = 0; i < fileIds.Count; i++ )
                cmd.Parameters.AddWithValue( $"@id{i}", fileIds [ i ] );

            object? result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is long total ? (int)total : 0;
        }

        private async Task<bool> DirectoryExistsAsync(
            SqliteConnection db,
            string directoryId,
            CancellationToken cancellationToken )
        {
            const string sql = "SELECT COUNT(1) FROM vault_directories WHERE id = @id";

            await using SqliteCommand cmd = new(sql, db);
            cmd.Parameters.AddWithValue( "@id", directoryId );

            object? result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is long count && count > 0;
        }

        private async Task<bool> IsDescendantOrSelfAsync(
            SqliteConnection db,
            string dirId,
            string targetId,
            CancellationToken cancellationToken )
        {
            if( !await DirectoryExistsAsync( db, dirId, cancellationToken ) )
                return false;

            const string sql = """
                WITH RECURSIVE dir_tree(id) AS (
                    SELECT id FROM vault_directories WHERE id = @rootId
                    UNION ALL
                    SELECT d.id FROM vault_directories d
                    INNER JOIN dir_tree t ON d.parent_id = t.id
                )
                SELECT COUNT(1) FROM dir_tree WHERE id = @targetId
                """;

            await using SqliteCommand cmd = new(sql, db);
            cmd.Parameters.AddWithValue( "@rootId", dirId );
            cmd.Parameters.AddWithValue( "@targetId", targetId );

            object? result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is long count && count > 0;
        }

        private async Task<List<string>> GetAllFileIdsRecursiveAsync(
            SqliteConnection db,
            string directoryId,
            CancellationToken cancellationToken )
        {
            const string sql = """
                WITH RECURSIVE dir_tree(id) AS (
                    SELECT id FROM vault_directories WHERE id = @rootId
                    UNION ALL
                    SELECT d.id FROM vault_directories d
                    INNER JOIN dir_tree t ON d.parent_id = t.id
                )
                SELECT f.id FROM vault_files f
                JOIN   dir_tree t ON f.directory_id = t.id
                """;

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@rootId", directoryId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

            List<string> fileIds = [];
            while( await reader.ReadAsync( cancellationToken ) )
                fileIds.Add( (string)reader [ "id" ] );

            return fileIds;
        }

        /// <summary>
        /// Reads a directory entry by ID. Returns null if not found.
        /// </summary>
        private async Task<VaultDirectoryEntry?> GetDirectoryEntryAsync(
            SqliteConnection db,
            string directoryId,
            CancellationToken cancellationToken )
        {
            const string sql = """
        SELECT id, name, parent_id, created_at
        FROM   vault_directories
        WHERE  id = @id
        """;

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@id", directoryId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync( cancellationToken )
                ? ReadVaultDirectoryEntry( reader )
                : null;
        }

        /// <summary>
        /// Returns every file in the directory tree rooted at
        /// <paramref name="rootDirectoryId"/>, each paired with its path
        /// relative to the root directory.
        ///
        /// e.g. for a file at Vault/Photos/2024/img.jpg extracted from Photos:
        ///     relativePath = "2024\img.jpg"
        /// </summary>
        private async Task<List<(VaultEntry File, string RelativePath)>> GetFilesWithRelativePathsAsync(
            SqliteConnection db,
            string rootDirectoryId,
            CancellationToken cancellationToken )
        {
            // This CTE walks the directory tree and builds the relative path
            // for each directory by concatenating name segments from root to leaf.
            const string sql = """
            WITH RECURSIVE dir_tree(id, relative_path) AS (
                -- Root: empty relative path (the root dir name is added by the caller)
                SELECT id, ''
                FROM   vault_directories
                WHERE  id = @rootId

                UNION ALL

                SELECT d.id,
                       CASE
                           WHEN t.relative_path = '' THEN d.name
                           ELSE t.relative_path || '/' || d.name
                       END
                FROM   vault_directories d
                INNER JOIN dir_tree t ON d.parent_id = t.id
            )
            SELECT f.id,
                   f.name,
                   f.directory_id,
                   f.original_size,
                   f.chunk_count,
                   f.added_at,
                   CASE
                       WHEN t.relative_path = '' THEN f.name
                       ELSE t.relative_path || '/' || f.name
                   END AS relative_path
            FROM   vault_files f
            JOIN   dir_tree    t ON f.directory_id = t.id
            ORDER  BY t.relative_path, f.name
            """;

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@rootId", rootDirectoryId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

            List<(VaultEntry, string)> results = [];
            while( await reader.ReadAsync( cancellationToken ) )
            {
                VaultEntry entry = ReadVaultEntry(reader);
                string relativePath = (string)reader["relative_path"];

                // Normalize path separators for the current OS
                relativePath = relativePath.Replace( '/', Path.DirectorySeparatorChar );

                results.Add( (entry, relativePath) );
            }

            return results;
        }

        private async Task<List<VaultDirectoryEntry>> ListDirectoriesAsync(
            SqliteConnection db,
            string parentId,
            CancellationToken cancellationToken )
        {
            const string sql = """
                SELECT id, name, parent_id, created_at
                FROM   vault_directories
                WHERE  parent_id = @parentId
                ORDER  BY name COLLATE NOCASE
                """;

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@parentId", parentId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

            List<VaultDirectoryEntry> results = [];
            while( await reader.ReadAsync( cancellationToken ) )
                results.Add( ReadVaultDirectoryEntry( reader ) );

            return results;
        }

        private async Task<List<VaultEntry>> ListFilesAsync(
            SqliteConnection db,
            string directoryId,
            CancellationToken cancellationToken )
        {
            const string sql = """
                SELECT id, name, directory_id, original_size, chunk_count, added_at
                FROM   vault_files
                WHERE  directory_id = @directoryId
                ORDER  BY name COLLATE NOCASE
                """;

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@directoryId", directoryId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

            List<VaultEntry> results = [];
            while( await reader.ReadAsync( cancellationToken ) )
                results.Add( ReadVaultEntry( reader ) );

            return results;
        }

        private async Task<List<(string FileId, int ChunkIndex)>> GetAllChunkKeysAsync(
            SqliteConnection db,
            CancellationToken cancellationToken )
        {
            const string sql = "SELECT file_id, chunk_index FROM vault_chunks ORDER BY file_id, chunk_index";

            await using SqliteCommand    cmd    = new(sql, db);
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

            List<(string, int)> keys = [];
            while( await reader.ReadAsync( cancellationToken ) )
                keys.Add( ((string)reader["file_id"], (int)(long)reader["chunk_index"]) );

            return keys;
        }

        private static async Task UpdateChunkDataAsync(
            string fileId,
            int chunkIndex,
            byte [ ] data,
            SqliteTransaction transaction,
            CancellationToken cancellationToken )
        {
            const string sql = """
                UPDATE vault_chunks SET data = @data
                WHERE file_id = @fileId AND chunk_index = @chunkIndex
                """;

            await using SqliteCommand cmd = new(sql, transaction.Connection, transaction);
            cmd.Parameters.AddWithValue( "@data", data );
            cmd.Parameters.AddWithValue( "@fileId", fileId );
            cmd.Parameters.AddWithValue( "@chunkIndex", chunkIndex );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        private static async Task ExecuteNonQueryAsync(
            SqliteConnection connection,
            string sql,
            CancellationToken cancellationToken )
        {
            await using SqliteCommand cmd = new(sql, connection);
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        #endregion

        #region Internals - Reader Helpers

        // Centralised record construction from reader columns - single place to update if schema changes
        /// <summary>
        /// Reads the three fields needed to construct a <see cref="VaultReadStream"/>:
        /// original plaintext size, stored chunk size, and chunk count.
        /// Throws <see cref="VaultItemNotFoundException"/> if the file is not present.
        /// </summary>
        private async Task<(long OriginalSize, int ChunkSize, int ChunkCount)> ReadStreamMetaAsync(
            SqliteConnection db,
            string fileId,
            CancellationToken cancellationToken )
        {
            const string sql = """
                SELECT original_size, chunk_size, chunk_count
                FROM   vault_files
                WHERE  id = @id
                """;

            await using SqliteCommand    cmd    = new(sql, db);
            cmd.Parameters.AddWithValue( "@id", fileId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync( cancellationToken );
            if( !await reader.ReadAsync( cancellationToken ) )
                throw new VaultItemNotFoundException( fileId, "file" );

            return (
                OriginalSize: (long)reader [ "original_size" ],
                ChunkSize: (int)(long)reader [ "chunk_size" ],
                ChunkCount: (int)(long)reader [ "chunk_count" ]);
        }

        private static VaultEntry ReadVaultEntry( SqliteDataReader reader ) =>
            new( Id: (string)reader [ "id" ],
                Name: (string)reader [ "name" ],
                DirectoryId: (string)reader [ "directory_id" ],
                OriginalSize: (long)reader [ "original_size" ],
                ChunkCount: (int)(long)reader [ "chunk_count" ],
                AddedAt: DateTime.Parse( (string)reader [ "added_at" ], null, System.Globalization.DateTimeStyles.RoundtripKind ) );

        private static VaultDirectoryEntry ReadVaultDirectoryEntry( SqliteDataReader reader ) =>
            new( Id: (string)reader [ "id" ],
                Name: (string)reader [ "name" ],
                ParentId: reader [ "parent_id" ] as string,
                CreatedAt: DateTime.Parse( (string)reader [ "created_at" ], null, System.Globalization.DateTimeStyles.RoundtripKind ) );

        /// <summary>
        /// Shared batch-delete loop used by both <see cref="RemoveAsync"/> and
        /// <see cref="RemoveDirectoryAsync"/>. Deletes chunks in batches of 10 across
        /// all provided file IDs, reports progress, then calls <paramref name="afterChunks"/>
        /// inside the same transaction for the final cleanup DELETE.
        /// </summary>
        private async Task DeleteChunksInBatchesAsync(
            SqliteConnection db,
            List<string> fileIds,
            Dictionary<string, int> chunkCounts,
            int totalChunks,
            IProgress<double>? progress,
            Func<SqliteTransaction, CancellationToken, Task> afterChunks,
            CancellationToken cancellationToken )
        {
            const int batchSize = 10;

            await using SqliteTransaction transaction =
                await db.BeginTransactionAsync(cancellationToken) as SqliteTransaction
                ?? throw new InvalidOperationException("Failed to begin transaction.");

            try
            {
                int deletedSoFar = 0;

                foreach( string fileId in fileIds )
                {
                    int fileChunkCount    = chunkCounts[fileId];
                    int fileChunksDeleted = 0;

                    while( fileChunksDeleted < fileChunkCount )
                    {
                        // Always take the first remaining batch - rows deleted by earlier
                        // iterations are gone, so an OFFSET would skip live chunks.
                        await using SqliteCommand cmd = new("""
                            DELETE FROM vault_chunks
                            WHERE file_id = @fileId
                              AND chunk_index IN (
                                  SELECT chunk_index FROM vault_chunks
                                  WHERE  file_id = @fileId
                                  ORDER  BY chunk_index
                                  LIMIT  @batchSize
                              )
                            """, db, transaction);

                        cmd.Parameters.AddWithValue( "@fileId", fileId );
                        cmd.Parameters.AddWithValue( "@batchSize", batchSize );
                        int affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                        if( affected == 0 ) break; // fewer chunks stored than declared

                        deletedSoFar += affected;
                        fileChunksDeleted += affected;
                        progress?.Report( (double)deletedSoFar / totalChunks * 0.95 );
                    }
                }

                await afterChunks( transaction, cancellationToken );

                await transaction.CommitAsync( cancellationToken );
                progress?.Report( 1.0 );
            }
            catch
            {
                await transaction.RollbackAsync( cancellationToken );
                throw;
            }
        }
        #endregion
    }
}
