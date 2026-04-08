using DeerCryptLib.Core;
using DeerCryptLib.Exceptions;
using Microsoft.Data.Sqlite;
using System;
using System.Security.Cryptography;

namespace DeerCryptLib.Vault
{
    public sealed partial class VaultFile
    {
        private async Task<byte [ ]> EncryptChunkAsync(
            ReadOnlyMemory<byte> plaintext,
            CancellationToken cancellationToken )
        {
            using MemoryStream input  = new(plaintext.ToArray());
            using MemoryStream output = new();
            await Crypt.EncryptGcmKeyed( input, output, _encKey, cancellationToken: cancellationToken );
            return output.ToArray( );
        }

        /// <summary>
        /// Fetches the encrypted blob for <paramref name="chunkIndex"/> from the database,
        /// decrypts it, and returns the plaintext as a new byte array.
        /// Called exclusively by <see cref="VaultReadStream"/> via the fetch delegate.
        /// </summary>
        private async Task<byte [ ]> FetchDecryptedChunkAsync(
            string fileId,
            int chunkIndex,
            CancellationToken cancellationToken )
        {
            byte [ ]? encrypted =  await ReadVaultChunkAsync( fileId, chunkIndex, cancellationToken )
                ?? throw new VaultCorruptException( $"Chunk {chunkIndex} of file '{fileId}' is missing from the vault." );
            using MemoryStream output = new( );
            await DecryptChunkToStreamAsync( encrypted, output, cancellationToken );
            return output.ToArray( );
        }

        private async Task DecryptChunkToStreamAsync(
            byte [ ] encryptedChunk,
            Stream destination,
            CancellationToken cancellationToken )
        {
            using MemoryStream input = new(encryptedChunk);
            try
            {
                await Crypt.DecryptGcmKeyed( input, destination, _encKey, cancellationToken: cancellationToken );
            }
            catch( CryptographicException ex )
            {
                throw new VaultCorruptException(
                    "A vault chunk failed authentication. The data may be corrupt or tampered with.", ex );
            }
        }

        private static async Task<byte [ ]> ComputeSha256Async(
            Stream stream,
            CancellationToken cancellationToken )
        {
            using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[Crypt.LargeBufferSize];
            int    bytesRead;

            while( ( bytesRead = await stream.ReadAsync( buffer, cancellationToken ) ) > 0 )
                sha.AppendData( buffer.AsSpan( 0, bytesRead ) );

            return sha.GetHashAndReset( );
        }

    }
}
