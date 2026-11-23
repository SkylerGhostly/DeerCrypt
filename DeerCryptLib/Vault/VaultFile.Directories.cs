using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeerCryptLib.Vault
{
    public sealed partial class VaultFile
    {
        /// <summary>Returns the well-known GUID of the vault root directory.</summary>
        public string RootDirectoryId => RootDirId;

        /// <summary>
        /// Creates a new virtual directory inside the vault.
        /// </summary>
        /// <param name="name">Single name segment - not a path. e.g. "Documents"</param>
        /// <param name="parentId">Parent GUID. Pass <see cref="RootDirectoryId"/> for top-level.</param>
        /// <returns>The new directory's GUID.</returns>
        public async Task<string> CreateDirectoryAsync(
            string name,
            string parentId,
            CancellationToken cancellationToken = default )
        {
            if( string.IsNullOrWhiteSpace( name ) )
                throw new VaultOperationException( "Directory name cannot be empty." );

            using var lease = await EnterWriteAsync( cancellationToken );
            var db = lease.Db;

            if( !await DirectoryExistsAsync( db, parentId, cancellationToken ) )
                throw new VaultItemNotFoundException( parentId, "parent directory" );

            string id = Guid.NewGuid().ToString();

            const string sql = """
                INSERT INTO vault_directories (id, name, parent_id, created_at)
                VALUES (@id, @name, @parentId, @createdAt)
                """;

            await using SqliteCommand cmd = new(sql, db);
            cmd.Parameters.AddWithValue( "@id", id );
            cmd.Parameters.AddWithValue( "@name", name );
            cmd.Parameters.AddWithValue( "@parentId", parentId );
            cmd.Parameters.AddWithValue( "@createdAt", DateTime.UtcNow.ToString( "O" ) );
            await cmd.ExecuteNonQueryAsync( cancellationToken );

            return id;
        }

        /// <summary>
        /// Removes a directory and all its contents recursively.
        /// Chunks are deleted in batches with progress reporting.
        /// The root directory cannot be removed.
        /// </summary>
        public async Task RemoveDirectoryAsync(
            string directoryId,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            if( directoryId == RootDirId )
                throw new VaultOperationException( "The root directory cannot be removed." );

            using var lease = await EnterWriteAsync( cancellationToken );
            var db = lease.Db;

            if( !await DirectoryExistsAsync( db, directoryId, cancellationToken ) )
                throw new VaultItemNotFoundException( directoryId, "directory" );

            List<string> allFileIds = await GetAllFileIdsRecursiveAsync( db, directoryId, cancellationToken);

            if( allFileIds.Count == 0 )
            {
                await using SqliteCommand cmd = new(
                    "DELETE FROM vault_directories WHERE id = @id",
                    db);
                cmd.Parameters.AddWithValue( "@id", directoryId );
                await cmd.ExecuteNonQueryAsync( cancellationToken );
                progress?.Report( 1.0 );

                return;
            }

            int totalChunks = await GetTotalChunkCountAsync( db, allFileIds, cancellationToken);

            Dictionary<string, int> chunkCounts = [];
            foreach( string id in allFileIds )
                chunkCounts [ id ] = await GetChunkCountAsync( db, id, cancellationToken );

            await DeleteChunksInBatchesAsync(
                db,
                allFileIds,
                chunkCounts,
                totalChunks,
                progress,
                afterChunks: async ( transaction, ct ) =>
                {
                    // Deleting the directory row cascades to vault_files and any remaining refs
                    await using SqliteCommand cmd = new(
                        "DELETE FROM vault_directories WHERE id = @id",
                        db, transaction);
                    cmd.Parameters.AddWithValue( "@id", directoryId );
                    await cmd.ExecuteNonQueryAsync( ct );
                },
                cancellationToken );
        }

        /// <summary>
        /// Extracts an entire directory tree from the vault to disk,
        /// recreating the original folder structure.
        /// </summary>
        /// <param name="directoryId">The GUID of the directory to extract.</param>
        /// <param name="outputPath">
        ///    The folder on disk to extract into. The directory's own name
        ///    will be created as a subfolder here.
        ///    e.g. outputPath = @"C:\Output" → files land in @"C:\Output\FolderName\"
        /// </param>
        /// <param name="progress">
        ///    Reports 0.0–1.0 across all files in the tree.
        /// </param>
        public async Task ExtractDirectoryAsync(
            string directoryId,
            string outputPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            VaultDirectoryEntry dirEntry;
            List<(VaultEntry File, string RelativePath)> allFiles;

            // Only the listing is gated here; each file's extraction gates itself chunk by chunk
            using( var lease = await EnterReadAsync( cancellationToken ) )
            {
                var db = lease.Db;
                dirEntry = await GetDirectoryEntryAsync( db, directoryId, cancellationToken )
                    ?? throw new VaultItemNotFoundException( directoryId, "directory" );

                // Every file in the tree along with its vault path relative to this directory
                allFiles = await GetFilesWithRelativePathsAsync( db, directoryId, cancellationToken );
            }

            if( allFiles.Count == 0 )
            {
                // Empty directory - just create the folder on disk
                Directory.CreateDirectory( Path.Combine( outputPath, dirEntry.Name ) );
                progress?.Report( 1.0 );
                return;
            }

            int   total     = allFiles.Count;
            int   completed = 0;

            foreach( (VaultEntry file, string relativePath) in allFiles )
            {
                cancellationToken.ThrowIfCancellationRequested( );

                // Reconstruct the full output path preserving the directory structure
                string fullOutputPath = Path.Combine(outputPath, dirEntry.Name, relativePath);

                // Ensure the subdirectory exists
                string? fileDir = Path.GetDirectoryName(fullOutputPath);
                if( !string.IsNullOrEmpty( fileDir ) )
                    Directory.CreateDirectory( fileDir );

                // Per-file progress is not surfaced here - overall progress is file count based
                await ExtractAsync( file.Id, fullOutputPath, null, cancellationToken );

                completed++;
                progress?.Report( (double)completed / total );
            }

            progress?.Report( 1.0 );
        }

        /// <summary>
        /// Counts everything beneath a directory (recursively): files, subfolders, and the
        /// total original size of the files. Read-only.
        /// </summary>
        public async Task<DirectoryStats> GetDirectoryStatsAsync(
            string directoryId,
            CancellationToken cancellationToken = default )
        {
            using var lease = await EnterReadAsync( cancellationToken );
            var db = lease.Db;

            if( !await DirectoryExistsAsync( db, directoryId, cancellationToken ) )
                throw new VaultItemNotFoundException( directoryId, "directory" );

            const string sql = """
                WITH RECURSIVE dir_tree(id) AS (
                    SELECT id FROM vault_directories WHERE id = @rootId
                    UNION ALL
                    SELECT d.id FROM vault_directories d
                    INNER JOIN dir_tree t ON d.parent_id = t.id
                )
                SELECT
                    (SELECT COUNT(*) - 1 FROM dir_tree)                                           AS folders,
                    (SELECT COUNT(*) FROM vault_files f JOIN dir_tree t ON f.directory_id = t.id)  AS files,
                    (SELECT COALESCE(SUM(f.original_size), 0)
                       FROM vault_files f JOIN dir_tree t ON f.directory_id = t.id)                AS total_size
                """;

            await using SqliteCommand cmd = new( sql, db );
            cmd.Parameters.AddWithValue( "@rootId", directoryId );

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync( cancellationToken );
            await reader.ReadAsync( cancellationToken );

            return new DirectoryStats(
                FileCount:   (int)(long)reader [ "files" ],
                FolderCount: (int)(long)reader [ "folders" ],
                TotalSize:   (long)reader [ "total_size" ] );
        }

        /// <summary>
        /// Lists the immediate contents of a directory - subdirectories and files, one level deep.
        /// Pass <see cref="RootDirectoryId"/> to list the vault root.
        /// Both lists are sorted case-insensitively by name.
        /// </summary>
        public async Task<VaultListing> ListAsync(
            string directoryId,
            CancellationToken cancellationToken = default )
        {
            using var lease = await EnterReadAsync( cancellationToken );
            var db = lease.Db;

            if( !await DirectoryExistsAsync( db, directoryId, cancellationToken ) )
                throw new VaultItemNotFoundException( directoryId, "directory" );

            List<VaultDirectoryEntry> directories = await ListDirectoriesAsync( db, directoryId, cancellationToken);
            List<VaultEntry>          files       = await ListFilesAsync( db, directoryId, cancellationToken);

            return new VaultListing( directories, files );
        }
    }
}
