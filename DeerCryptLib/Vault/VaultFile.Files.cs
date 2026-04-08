using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace DeerCryptLib.Vault
{
    public sealed partial class VaultFile
    {
        /// <summary>
        /// Returns the stored SHA-256 hash of a file's original plaintext content.
        /// Used by external callers to detect whether a file has been modified.
        /// </summary>
        public async Task<byte [ ]> GetFileSha256Async(
            string fileId,
            CancellationToken cancellationToken = default )
        {
            return await ReadFileSha256Async( fileId, cancellationToken );
        }

        /// <summary>
        /// Adds a file from disk into the vault under the specified directory.
        /// Files are split into 16 MB chunks, each independently encrypted.
        /// The entire operation is a single transaction - all chunks commit or none do.
        /// </summary>
        /// <param name="sourcePath">Absolute path to the source file on disk.</param>
        /// <param name="directoryId">
        ///    Target directory GUID. Pass <see cref="RootDirectoryId"/> for the vault root.
        /// </param>
        /// <param name="progress">Reports 0.0–1.0 as chunks are encrypted and written.</param>
        /// <returns>The GUID assigned to the file - required for extract, remove, rename, and move.</returns>
        public async Task<string> AddAsync(
            string sourcePath,
            string directoryId,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            if( !File.Exists( sourcePath ) )
                throw new VaultItemNotFoundException( sourcePath, "source file on disk" );

            if( !await DirectoryExistsAsync( directoryId, cancellationToken ) )
                throw new VaultItemNotFoundException( directoryId, "target directory" );

            string fileId   = Guid.NewGuid().ToString();
            string fileName = Path.GetFileName(sourcePath);

            await using FileStream fileStream = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            long fileSize   = fileStream.Length;
            int  chunkSize  = GetChunkSize(fileSize);
            int  chunkCount = (int)Math.Ceiling((double)fileSize / chunkSize);
            if( chunkCount == 0 ) chunkCount = 1; // empty file still produces one chunk

            byte[] sha256 = await ComputeSha256Async(fileStream, cancellationToken);
            fileStream.Seek( 0, SeekOrigin.Begin );

            await using SqliteTransaction transaction =
                await _connection.BeginTransactionAsync(cancellationToken) as SqliteTransaction
                ?? throw new InvalidOperationException("Failed to begin SQLite transaction.");

            try
            {
                await InsertVaultFileAsync(
                    transaction, fileId, fileName, directoryId,
                    fileSize, chunkSize, chunkCount, sha256, cancellationToken );

                byte[] readBuffer = new byte[chunkSize];
                int    chunkIndex = 0;

                await using SqliteCommand insertCmd = CreatePreparedChunkInsertCommand( transaction );

                while( true )
                {
                    int bytesRead = await fileStream.ReadAsync(readBuffer.AsMemory(0, chunkSize), cancellationToken);
                    if( bytesRead == 0 ) break;

                    byte[] encryptedChunk = await EncryptChunkAsync(readBuffer.AsMemory(0, bytesRead), cancellationToken);

                    insertCmd.Parameters [ "@fileId"     ].Value = fileId;
                    insertCmd.Parameters [ "@chunkIndex" ].Value = chunkIndex;
                    insertCmd.Parameters [ "@data"       ].Value = encryptedChunk;
                    await insertCmd.ExecuteNonQueryAsync( cancellationToken );

                    chunkIndex++;
                    progress?.Report( (double)chunkIndex / chunkCount );
                }

                // Empty file - the loop never executed but we promised chunk_count = 1
                if( chunkIndex == 0 )
                {
                    byte[] emptyChunk = await EncryptChunkAsync(ReadOnlyMemory<byte>.Empty, cancellationToken);

                    insertCmd.Parameters [ "@fileId"     ].Value = fileId;
                    insertCmd.Parameters [ "@chunkIndex" ].Value = 0;
                    insertCmd.Parameters [ "@data"       ].Value = emptyChunk;
                    await insertCmd.ExecuteNonQueryAsync( cancellationToken );
                }

                await transaction.CommitAsync( cancellationToken );
                progress?.Report( 1.0 );
                return fileId;
            }
            catch
            {
                await transaction.RollbackAsync( cancellationToken );
                throw;
            }
        }

        /// <summary>
        /// Replaces the encrypted contents of an existing vault file with new data
        /// from <paramref name="sourcePath"/>, preserving the file's ID, name,
        /// directory, and creation date.
        ///
        /// The entire operation is a single transaction - the file record never
        /// disappears and is never left in a partially-updated state.
        /// </summary>
        /// <param name="fileId">The GUID of the file to update.</param>
        /// <param name="sourcePath">Path to the new version of the file on disk.</param>
        /// <param name="progress">Reports 0.0–1.0 as chunks are encrypted and written.</param>
        public async Task UpdateAsync(
            string fileId,
            string sourcePath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            if( !File.Exists( sourcePath ) )
                throw new VaultItemNotFoundException( fileId, "source file on disk" );

            // Verify the file exists in the vault
            VaultEntry? _ = await ReadVaultEntryAsync(fileId, cancellationToken)
                ?? throw new VaultItemNotFoundException(fileId, "file");

            await using FileStream fileStream = new(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            long fileSize   = fileStream.Length;
            int  chunkSize  = GetChunkSize(fileSize);
            int  chunkCount = (int)Math.Ceiling((double)fileSize / chunkSize);
            if( chunkCount == 0 ) chunkCount = 1;

            byte[] sha256 = await ComputeSha256Async(fileStream, cancellationToken);
            fileStream.Seek( 0, SeekOrigin.Begin );

            await using SqliteTransaction transaction =
            await _connection.BeginTransactionAsync(cancellationToken) as SqliteTransaction
                ?? throw new InvalidOperationException("Failed to begin transaction.");

            try
            {
                // 1. Delete existing chunks 
                await using SqliteCommand deleteChunks = new(
                    "DELETE FROM vault_chunks WHERE file_id = @id",
                    _connection, transaction);
                deleteChunks.Parameters.AddWithValue( "@id", fileId );
                await deleteChunks.ExecuteNonQueryAsync( cancellationToken );

                // 2. Encrypt and insert new chunks 
                byte[] readBuffer = new byte[chunkSize];
                int    chunkIndex = 0;

                await using SqliteCommand insertCmd = CreatePreparedChunkInsertCommand( transaction );

                while( true )
                {
                    int bytesRead = await fileStream.ReadAsync(
                    readBuffer.AsMemory(0, chunkSize), cancellationToken);
                    if( bytesRead == 0 ) break;

                    byte[] encryptedChunk = await EncryptChunkAsync(
                    readBuffer.AsMemory(0, bytesRead), cancellationToken);

                    insertCmd.Parameters [ "@fileId"     ].Value = fileId;
                    insertCmd.Parameters [ "@chunkIndex" ].Value = chunkIndex;
                    insertCmd.Parameters [ "@data"       ].Value = encryptedChunk;
                    await insertCmd.ExecuteNonQueryAsync( cancellationToken );

                    chunkIndex++;
                    progress?.Report( (double)chunkIndex / chunkCount );
                }

                // Handle empty file edge case
                if( chunkIndex == 0 )
                {
                    byte[] emptyChunk = await EncryptChunkAsync(
                    ReadOnlyMemory<byte>.Empty, cancellationToken);

                    insertCmd.Parameters [ "@fileId"     ].Value = fileId;
                    insertCmd.Parameters [ "@chunkIndex" ].Value = 0;
                    insertCmd.Parameters [ "@data"       ].Value = emptyChunk;
                    await insertCmd.ExecuteNonQueryAsync( cancellationToken );
                }

                // 3. Update file metadata 
                const string updateSql = """
                    UPDATE vault_files
                    SET    original_size = @size,
                           chunk_size    = @chunkSize,
                           chunk_count   = @chunkCount,
                           sha256        = @sha256
                    WHERE  id = @id
                    """;

                await using SqliteCommand updateFile = new(updateSql, _connection, transaction);
                updateFile.Parameters.AddWithValue( "@size", fileSize );
                updateFile.Parameters.AddWithValue( "@chunkSize", chunkSize );
                updateFile.Parameters.AddWithValue( "@chunkCount", chunkCount );
                updateFile.Parameters.AddWithValue( "@sha256", sha256 );
                updateFile.Parameters.AddWithValue( "@id", fileId );
                await updateFile.ExecuteNonQueryAsync( cancellationToken );

                await transaction.CommitAsync( cancellationToken );
                progress?.Report( 1.0 );
            }
            catch
            {
                await transaction.RollbackAsync( cancellationToken );
                throw;
            }
        }

        /// <summary>
        /// Replaces the encrypted content of an existing vault file with data from a stream.
        /// If the stream is not seekable it is buffered into memory first.
        /// The operation is fully transactional - the file record is never left partially updated.
        /// </summary>
        /// <param name="fileId">The GUID of the file to update.</param>
        /// <param name="sourceStream">Stream containing the new file content.</param>
        /// <param name="progress">Reports 0.0–1.0 as chunks are encrypted and written.</param>
        public async Task UpdateAsync(
            string fileId,
            Stream sourceStream,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            // Verify the file exists in the vault
            VaultEntry? _ = await ReadVaultEntryAsync( fileId, cancellationToken )
                ?? throw new VaultItemNotFoundException( fileId, "file" );

            // Ensure we have a seekable stream
            Stream stream;
            bool   ownsStream;
            if( sourceStream.CanSeek )
            {
                stream = sourceStream;
                ownsStream = false;
            }
            else
            {
                var ms = new MemoryStream( );
                await sourceStream.CopyToAsync( ms, cancellationToken );
                ms.Seek( 0, SeekOrigin.Begin );
                stream = ms;
                ownsStream = true;
            }

            try
            {
                long fileSize   = stream.Length;
                int  chunkSize  = GetChunkSize( fileSize );
                int  chunkCount = (int)Math.Ceiling( (double)fileSize / chunkSize );
                if( chunkCount == 0 ) chunkCount = 1;

                byte[] sha256 = await ComputeSha256Async( stream, cancellationToken );
                stream.Seek( 0, SeekOrigin.Begin );

                await using SqliteTransaction transaction =
                    await _connection.BeginTransactionAsync( cancellationToken ) as SqliteTransaction
                    ?? throw new InvalidOperationException( "Failed to begin transaction." );

                try
                {
                    // 1. Delete existing chunks 
                    await using SqliteCommand deleteChunks = new(
                        "DELETE FROM vault_chunks WHERE file_id = @id",
                        _connection, transaction );
                    deleteChunks.Parameters.AddWithValue( "@id", fileId );
                    await deleteChunks.ExecuteNonQueryAsync( cancellationToken );

                    // 2. Encrypt and insert new chunks 
                    byte[] readBuffer = new byte[chunkSize];
                    int    chunkIndex = 0;

                    await using SqliteCommand insertCmd = CreatePreparedChunkInsertCommand( transaction );

                    while( true )
                    {
                        int bytesRead = await stream.ReadAsync(
                            readBuffer.AsMemory( 0, chunkSize ), cancellationToken );
                        if( bytesRead == 0 ) break;

                        byte[] encryptedChunk = await EncryptChunkAsync(
                            readBuffer.AsMemory( 0, bytesRead ), cancellationToken );

                        insertCmd.Parameters [ "@fileId"     ].Value = fileId;
                        insertCmd.Parameters [ "@chunkIndex" ].Value = chunkIndex;
                        insertCmd.Parameters [ "@data"       ].Value = encryptedChunk;
                        await insertCmd.ExecuteNonQueryAsync( cancellationToken );

                        chunkIndex++;
                        progress?.Report( (double)chunkIndex / chunkCount );
                    }

                    // Handle empty file edge case
                    if( chunkIndex == 0 )
                    {
                        byte[] emptyChunk = await EncryptChunkAsync(
                            ReadOnlyMemory<byte>.Empty, cancellationToken );

                        insertCmd.Parameters [ "@fileId"     ].Value = fileId;
                        insertCmd.Parameters [ "@chunkIndex" ].Value = 0;
                        insertCmd.Parameters [ "@data"       ].Value = emptyChunk;
                        await insertCmd.ExecuteNonQueryAsync( cancellationToken );
                    }

                    // 3. Update file metadata 
                    const string updateSql = """
                        UPDATE vault_files
                        SET    original_size = @size,
                               chunk_size    = @chunkSize,
                               chunk_count   = @chunkCount,
                               sha256        = @sha256
                        WHERE  id = @id
                        """;

                    await using SqliteCommand updateFile = new( updateSql, _connection, transaction );
                    updateFile.Parameters.AddWithValue( "@size", fileSize );
                    updateFile.Parameters.AddWithValue( "@chunkSize", chunkSize );
                    updateFile.Parameters.AddWithValue( "@chunkCount", chunkCount );
                    updateFile.Parameters.AddWithValue( "@sha256", sha256 );
                    updateFile.Parameters.AddWithValue( "@id", fileId );
                    await updateFile.ExecuteNonQueryAsync( cancellationToken );

                    await transaction.CommitAsync( cancellationToken );
                    progress?.Report( 1.0 );
                }
                catch
                {
                    await transaction.RollbackAsync( cancellationToken );
                    throw;
                }
            }
            finally
            {
                if( ownsStream ) await stream.DisposeAsync( );
            }
        }

        /// <summary>
        /// Extracts a file from the vault to disk by its GUID.
        /// Decrypts all chunks in order, then verifies the assembled SHA-256 against
        /// the hash stored at add time. Deletes the output file if verification fails.
        /// </summary>
        /// <param name="fileId">The GUID returned by <see cref="AddAsync"/>.</param>
        /// <param name="outputPath">Full destination path including filename.</param>
        /// <param name="progress">Reports 0.0–1.0 as chunks are decrypted and written.</param>
        public async Task ExtractAsync(
            string fileId,
            string outputPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            VaultEntry record = await ReadVaultEntryAsync( fileId, cancellationToken )
        ?? throw new VaultItemNotFoundException( fileId, "file" );

            string? outputDir = Path.GetDirectoryName( outputPath );
            if( !string.IsNullOrEmpty( outputDir ) )
                Directory.CreateDirectory( outputDir );

            // Manual lifetime, not `await using` because we must guarantee the file
            // is closed BEFORE we attempt to delete it on integrity failure.
            // `await using` would only dispose on scope exit (after the throw),
            // which is too late on platforms that require a file to be closed before deletion.
            FileStream outputStream = new(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            bool integrityPassed    = false;

            try
            {
                for( int chunkIndex = 0; chunkIndex < record.ChunkCount; chunkIndex++ )
                {
                    byte[] encryptedChunk = await ReadVaultChunkAsync( fileId, chunkIndex, cancellationToken )
                ?? throw new VaultCorruptException( $"Chunk {chunkIndex} of file '{fileId}' is missing from the vault." );

                    await DecryptChunkToStreamAsync( encryptedChunk, outputStream, cancellationToken );

                    progress?.Report( (double)( chunkIndex + 1 ) / record.ChunkCount );
                }

                await outputStream.FlushAsync( cancellationToken );

                // Verify assembled file hash - catches any tampering even if per-chunk HMACs passed
                outputStream.Seek( 0, SeekOrigin.Begin );
                byte[] actualSha256 = await ComputeSha256Async( outputStream, cancellationToken );
                byte[] storedSha256 = await ReadFileSha256Async( fileId, cancellationToken );
                integrityPassed = CryptographicOperations.FixedTimeEquals( actualSha256, storedSha256 );
            }
            finally
            {
                // Always close the stream first - file must not be open when we delete it
                await outputStream.DisposeAsync( );

                // Delete the partial or corrupt output if anything went wrong,
                // including exceptions thrown during decryption or the integrity check
                if( !integrityPassed )
                    File.Delete( outputPath );
            }

            // Throw after finally so the file is already closed and deleted before the
            // caller receives the exception
            if( !integrityPassed )
                throw new VaultIntegrityException( record.Name, fileId );

            progress?.Report( 1.0 );
        }

        /// <summary>
        /// Removes a file and all its chunks from the vault.
        /// Chunks are deleted in batches to allow progress reporting.
        /// The vault file does not shrink until <see cref="CompactAsync"/> is called.
        /// </summary>
        public async Task RemoveAsync(
            string fileId,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            int chunkCount = await GetChunkCountAsync(fileId, cancellationToken);
            if( chunkCount == 0 )
                throw new VaultItemNotFoundException( fileId, "file" );

            await DeleteChunksInBatchesAsync(
                [ fileId ],
                new Dictionary<string, int> { [ fileId ] = chunkCount },
                chunkCount,
                progress,
                afterChunks: async ( transaction, ct ) =>
                {
                    await using SqliteCommand cmd = new(
                        "DELETE FROM vault_files WHERE id = @id",
                        _connection, transaction);
                    cmd.Parameters.AddWithValue( "@id", fileId );
                    await cmd.ExecuteNonQueryAsync( ct );
                },
                cancellationToken );
        }

        /// <summary>
        /// Opens a read-only, seekable <see cref="VaultReadStream"/> over the specified vault
        /// file. Chunks are decrypted on demand as the stream is read, with an LRU cache of
        /// the last <see cref="VaultReadStream.CacheCapacity"/> chunks to avoid redundant
        /// decrypt operations during seeks and buffered reads.
        /// </summary>
        /// <param name="fileId">The GUID of the vault file to stream.</param>
        /// <returns>
        /// A <see cref="VaultReadStream"/> positioned at byte 0. Dispose when done.
        /// </returns>
        /// <exception cref="VaultItemNotFoundException">
        /// Thrown if <paramref name="fileId"/> does not exist in the vault.
        /// </exception>
        public async Task<VaultReadStream> OpenReadStreamAsync(
            string fileId,
            CancellationToken cancellationToken = default )
        {
            (long originalSize, int chunkSize, int chunkCount) =
                await ReadStreamMetaAsync( fileId, cancellationToken );

            return new VaultReadStream(
                fileId: fileId,
                length: originalSize,
                chunkSize: chunkSize,
                chunkCount: chunkCount,
                fetchChunk: ( index, ct ) => FetchDecryptedChunkAsync( fileId, index, ct ) );
        }
    }
}
