using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeerCryptLib.Vault
{
    public sealed partial class VaultFile
    {
        /// <summary>
        /// Renames a file or directory. Does not change its location in the tree.
        /// </summary>
        public async Task RenameAsync(
            string id,
            string newName,
            CancellationToken cancellationToken = default )
        {
            if( string.IsNullOrWhiteSpace( newName ) )
                throw new VaultOperationException( "Name cannot be empty." );

            const string renameFile = "UPDATE vault_files SET name = @name WHERE id = @id";
            await using SqliteCommand fileCmd = new(renameFile, _connection);
            fileCmd.Parameters.AddWithValue( "@name", newName );
            fileCmd.Parameters.AddWithValue( "@id", id );

            if( await fileCmd.ExecuteNonQueryAsync( cancellationToken ) > 0 )
                return;

            const string renameDir = "UPDATE vault_directories SET name = @name WHERE id = @id";
            await using SqliteCommand dirCmd = new(renameDir, _connection);
            dirCmd.Parameters.AddWithValue( "@name", newName );
            dirCmd.Parameters.AddWithValue( "@id", id );

            if( await dirCmd.ExecuteNonQueryAsync( cancellationToken ) == 0 )
                throw new VaultItemNotFoundException( id );
        }

        /// <summary>
        /// Moves a file or directory to a different parent directory.
        /// Does not rename it - use <see cref="RenameAsync"/> for that.
        /// </summary>
        public async Task MoveAsync(
            string id,
            string targetDirectoryId,
            CancellationToken cancellationToken = default )
        {
            // Root guard first - before any DB writes
            if( id == RootDirId )
                throw new VaultOperationException( "The root directory cannot be moved." );

            if( !await DirectoryExistsAsync( targetDirectoryId, cancellationToken ) )
                throw new VaultItemNotFoundException( targetDirectoryId, "target directory" );

            if( await IsDescendantOrSelfAsync( id, targetDirectoryId, cancellationToken ) )
                throw new VaultOperationException( "Cannot move a directory into itself or one of its subdirectories." );

            const string moveFile = "UPDATE vault_files SET directory_id = @targetId WHERE id = @id";
            await using SqliteCommand fileCmd = new(moveFile, _connection);
            fileCmd.Parameters.AddWithValue( "@targetId", targetDirectoryId );
            fileCmd.Parameters.AddWithValue( "@id", id );

            if( await fileCmd.ExecuteNonQueryAsync( cancellationToken ) > 0 )
                return;

            const string moveDir = "UPDATE vault_directories SET parent_id = @targetId WHERE id = @id";
            await using SqliteCommand dirCmd = new(moveDir, _connection);
            dirCmd.Parameters.AddWithValue( "@targetId", targetDirectoryId );
            dirCmd.Parameters.AddWithValue( "@id", id );

            if( await dirCmd.ExecuteNonQueryAsync( cancellationToken ) == 0 )
                throw new VaultItemNotFoundException( id );
        }

        /// <summary>
        /// Returns a flat list of every file in the vault, optionally filtered by a
        /// case-insensitive contains search on the filename. Results are sorted by name.
        /// Useful for vault-wide search in the UI regardless of directory structure.
        /// </summary>
        public async Task<IReadOnlyList<VaultEntry>> ListRecursiveAsync(
            string? searchTerm = null,
            CancellationToken cancellationToken = default )
        {
            bool hasSearch = !string.IsNullOrWhiteSpace(searchTerm);

            string sql = hasSearch
                ? """
                  SELECT id, name, directory_id, original_size, chunk_count, added_at
                  FROM   vault_files
                  WHERE  name LIKE @search ESCAPE '\'
                  ORDER  BY name COLLATE NOCASE
                  """
                : """
                  SELECT id, name, directory_id, original_size, chunk_count, added_at
                  FROM   vault_files
                  ORDER  BY name COLLATE NOCASE
                  """;

            await using SqliteCommand cmd = new(sql, _connection);

            if( hasSearch )
            {
                string escaped = searchTerm!
                    .Replace(@"\", @"\\")
                    .Replace("%",  @"\%")
                    .Replace("_",  @"\_");
                cmd.Parameters.AddWithValue( "@search", $"%{escaped}%" );
            }

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

            List<VaultEntry> results = [];
            while( await reader.ReadAsync( cancellationToken ) )
                results.Add( ReadVaultEntry( reader ) );

            return results;
        }

        /// <summary>
        /// Looks up a single file or directory by its GUID. Returns null if not found.
        /// Use pattern matching on the result to distinguish files from directories.
        /// </summary>
        public async Task<VaultItem?> GetEntryAsync(
            string id,
            CancellationToken cancellationToken = default )
        {
            // Check files first
            const string fileSql = """
                SELECT id, name, directory_id, original_size, chunk_count, added_at
                FROM   vault_files
                WHERE  id = @id
                """;

            await using SqliteCommand fileCmd = new(fileSql, _connection);
            fileCmd.Parameters.AddWithValue( "@id", id );

            await using SqliteDataReader fileReader = await fileCmd.ExecuteReaderAsync(cancellationToken);
            if( await fileReader.ReadAsync( cancellationToken ) )
                return ReadVaultEntry( fileReader );

            // Then directories
            const string dirSql = """
                SELECT id, name, parent_id, created_at
                FROM   vault_directories
                WHERE  id = @id
                """;

            await using SqliteCommand dirCmd = new(dirSql, _connection);
            dirCmd.Parameters.AddWithValue( "@id", id );

            await using SqliteDataReader dirReader = await dirCmd.ExecuteReaderAsync(cancellationToken);
            if( await dirReader.ReadAsync( cancellationToken ) )
                return ReadVaultDirectoryEntry( dirReader );

            return null;
        }
    }
}
