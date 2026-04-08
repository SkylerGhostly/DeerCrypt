using DeerCryptLib.Core;
using DeerCryptLib.Exceptions;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace DeerCryptLib.Vault
{
    /// <summary>
    /// Represents an open DeerCrypt vault (.dcv) file.
    ///
    /// Always obtain an instance via <see cref="CreateAsync"/> or <see cref="OpenAsync"/>.
    /// Dispose when done - this closes the SQLite connection and zeroes key material.
    /// </summary>
    public sealed partial class VaultFile : IAsyncDisposable
    {
        #region Constants

        public  const string FileExtension    = ".dcv";
        private const string RootDirId        = "00000000-0000-0000-0000-000000000000";
        private const int    CurrentVersion   = 3;

        // vault_meta keys
        private const string MetaVersion          = "version";
        private const string MetaKdfSalt          = "kdf_salt";
        private const string MetaWrappedMasterKey = "wrapped_master_key";
        private const string MetaCreatedAt        = "created_at";

        #endregion

        #region Fields

        private readonly SqliteConnection _connection;
        private readonly byte[]           _encKey;    // master key (DEK) - random, never changes
        private readonly string           _vaultPath; // stored so GetVaultInfoAsync doesn't need it passed in

        #endregion

        #region Constructor (private)

        private VaultFile( SqliteConnection connection, byte [ ] masterKey, string vaultPath )
        {
            _connection = connection;
            _encKey     = masterKey;
            _vaultPath  = vaultPath;
        }

        #endregion

        #region Factory Methods

        /// <summary>
        /// Creates a new vault at <paramref name="path"/>.
        /// Throws <see cref="InvalidOperationException"/> if a file already exists there.
        /// </summary>
        public static async Task<VaultFile> CreateAsync(
            string path,
            string password,
            CancellationToken cancellationToken = default )
        {
            if( File.Exists( path ) )
                throw new VaultOperationException( $"A vault already exists at: {path}" );

            if( !path.EndsWith( FileExtension, StringComparison.OrdinalIgnoreCase ) )
                throw new VaultOperationException( $"Vault files must use the {FileExtension} extension." );

            string? directory = Path.GetDirectoryName(path);
            if( !string.IsNullOrEmpty( directory ) )
                Directory.CreateDirectory( directory );

            // Derive all key material before touching the file.
            byte[]? masterKey = null;
            byte[]? kek       = null;
            try
            {
                byte[] kdfSalt       = RandomNumberGenerator.GetBytes( Crypt.SaltSize );
                masterKey            = RandomNumberGenerator.GetBytes( Crypt.AesKeySizeBytes );
                kek                  = Crypt.DeriveKek( password, kdfSalt );
                byte[] wrappedMaster = Crypt.WrapKey( kek, masterKey );
                CryptographicOperations.ZeroMemory( kek );
                kek = null;

                SqliteConnection connection = OpenConnection( path, password );

                try
                {
                    await InitializePragmasAsync( connection, cancellationToken );
                    await CreateSchemaAsync( connection, cancellationToken );

                    await InsertMetaAsync( connection, MetaVersion,          BitConverter.GetBytes( CurrentVersion ), cancellationToken );
                    await InsertMetaAsync( connection, MetaKdfSalt,          kdfSalt,       cancellationToken );
                    await InsertMetaAsync( connection, MetaWrappedMasterKey, wrappedMaster, cancellationToken );
                    await InsertMetaAsync( connection, MetaCreatedAt,        SerializeUtcNow( ),                      cancellationToken );

                    await CreateRootDirectoryAsync( connection, cancellationToken );

                    return new VaultFile( connection, masterKey, path );
                }
                catch
                {
                    await connection.DisposeAsync( );
                    if( File.Exists( path ) )
                        File.Delete( path );
                    throw;
                }
            }
            catch
            {
                if( masterKey != null ) CryptographicOperations.ZeroMemory( masterKey );
                if( kek       != null ) CryptographicOperations.ZeroMemory( kek );
                throw;
            }
        }

        /// <summary>
        /// Opens an existing vault at <paramref name="path"/> and authenticates with
        /// <paramref name="password"/>.
        /// Throws <see cref="FileNotFoundException"/> if the vault does not exist.
        /// Throws <see cref="CryptDataException"/> if the vault metadata is corrupt or the version is unsupported.
        /// </summary>
        public static async Task<VaultFile> OpenAsync(
            string path,
            string password,
            CancellationToken cancellationToken = default )
        {
            if( !File.Exists( path ) )
                throw new VaultNotFoundException( path );

            // Step 1: Open the encrypted database 
            SqliteConnection connection = OpenConnection( path, password, SqliteOpenMode.ReadWrite );

            byte[]? masterKey = null;
            try
            {
                await InitializePragmasAsync( connection, cancellationToken );

                // Step 2: Version check 
                byte[]? versionBytes = await ReadMetaBlobAsync( connection, MetaVersion, cancellationToken );
                int version = versionBytes != null ? BitConverter.ToInt32( versionBytes ) : -1;
                if( version != CurrentVersion )
                    throw new VaultCorruptException(
                        $"This vault uses schema version {( version == -1 ? "unknown" : version )} " +
                        $"and is not compatible with this version of DeerCrypt." );

                // Step 3: Derive master key from vault metadata 
                byte[] kdfSalt = await ReadMetaBlobAsync( connection, MetaKdfSalt, cancellationToken )
                    ?? throw new VaultCorruptException( "Vault metadata is missing the KDF salt." );

                byte[] wrappedMasterKey = await ReadMetaBlobAsync( connection, MetaWrappedMasterKey, cancellationToken )
                    ?? throw new VaultCorruptException( "Vault metadata is missing the wrapped master key." );

                byte[] kek = Crypt.DeriveKek( password, kdfSalt );
                try
                {
                    masterKey = Crypt.UnwrapKey( kek, wrappedMasterKey );
                }
                catch( CryptographicException ex )
                {
                    throw new VaultPasswordException( "The password is incorrect.", ex );
                }
                finally
                {
                    CryptographicOperations.ZeroMemory( kek );
                }

                return new VaultFile( connection, masterKey, path );
            }
            catch
            {
                if( masterKey != null ) CryptographicOperations.ZeroMemory( masterKey );
                await connection.DisposeAsync( );
                throw;
            }
        }

        #endregion

        #region Internals - Utility

        private static int GetChunkSize( long fileSize )
        {
            return fileSize switch
            {
                <= 1 * 1024 * 1024 => (int)fileSize,  // ≤ 1MB:  one chunk, exact size
                <= 16 * 1024 * 1024 => 1 * 1024 * 1024, // ≤ 16MB: 1MB chunks
                <= 256 * 1024 * 1024 => 4 * 1024 * 1024, // ≤ 256MB: 4MB chunks
                _ => 16 * 1024 * 1024  // > 256MB: 16MB chunks
            };
        }
        private static byte [ ] SerializeUtcNow( ) =>
            System.Text.Encoding.UTF8.GetBytes( DateTime.UtcNow.ToString( "O" ) );

        #endregion

        #region IAsyncDisposable

        public async ValueTask DisposeAsync( )
        {
            CryptographicOperations.ZeroMemory( _encKey );
            await _connection.DisposeAsync( );
        }

        #endregion
    }
}