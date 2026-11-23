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
    /// Dispose when done - this closes the SQLite connections and zeroes key material.
    ///
    /// <para><b>Connections:</b> one writer plus a small pool of readers. While open, the
    /// database runs in WAL mode so readers never wait for a write in progress (they see the
    /// last committed state) and a long read such as an integrity check never blocks writes.
    /// On dispose the WAL is folded back in and the file returns to rollback-journal mode, so
    /// a locked vault is a single file on disk exactly as before. If WAL can't be enabled
    /// (e.g. a filesystem without shared-memory support), reads fall back to the writer.</para>
    /// </summary>
    public sealed partial class VaultFile : IAsyncDisposable
    {
        #region Constants

        public  const string FileExtension    = ".dcv";
        private const string RootDirId        = "00000000-0000-0000-0000-000000000000";
        private const int    CurrentVersion   = 3;
        private const int    ReaderCount      = 3;

        // vault_meta keys
        private const string MetaVersion          = "version";
        private const string MetaKdfSalt          = "kdf_salt";
        private const string MetaWrappedMasterKey = "wrapped_master_key";
        private const string MetaCreatedAt        = "created_at";

        #endregion

        #region Fields

        private readonly SqliteConnection   _writer;
        private readonly SqliteConnection[] _readers;   // empty when WAL is unavailable
        private readonly Stack<SqliteConnection> _freeReaders;
        private readonly byte[]             _encKey;    // master key (DEK) - random, never changes
        private readonly string             _vaultPath; // stored so GetVaultInfoAsync doesn't need it passed in

        // Each SQLite connection must only be used by one operation at a time. Writes take the
        // single write gate (SQLite allows one writer); reads take one of the reader slots.
        // Long operations take their lease in small steps (per file, per chunk, per vacuum step).
        // Invariant: connections and key material are only touched while holding a lease, and
        // DisposeAsync takes every lease before zeroing the key.
        private readonly SemaphoreSlim _writeGate = new( 1, 1 );
        private readonly SemaphoreSlim _readGate;
        private bool                   _disposed;

        #endregion

        #region Constructor (private)

        private VaultFile( SqliteConnection writer, SqliteConnection [ ] readers, byte [ ] masterKey, string vaultPath )
        {
            _writer      = writer;
            _readers     = readers;
            _freeReaders = new Stack<SqliteConnection>( readers );
            _readGate    = new SemaphoreSlim( readers.Length, Math.Max( readers.Length, 1 ) );
            _encKey      = masterKey;
            _vaultPath   = vaultPath;
        }

        /// <summary>Full path of the vault file.</summary>
        public string FilePath => _vaultPath;

        /// <summary>True when reads run on their own connections (WAL mode is active).</summary>
        public bool HasConcurrentReads => _readers.Length > 0;

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

                SqliteConnection connection = await OpenConnectionAsync( path, password, SqliteOpenMode.ReadWriteCreate, cancellationToken );
                SqliteConnection[] readers = [ ];

                try
                {
                    await InitializePragmasAsync( connection, cancellationToken );
                    await CreateSchemaAsync( connection, cancellationToken );

                    await InsertMetaAsync( connection, MetaVersion,          BitConverter.GetBytes( CurrentVersion ), cancellationToken );
                    await InsertMetaAsync( connection, MetaKdfSalt,          kdfSalt,       cancellationToken );
                    await InsertMetaAsync( connection, MetaWrappedMasterKey, wrappedMaster, cancellationToken );
                    await InsertMetaAsync( connection, MetaCreatedAt,        SerializeUtcNow( ),                      cancellationToken );

                    await CreateRootDirectoryAsync( connection, cancellationToken );

                    readers = await OpenReadersAsync( connection, path, password, cancellationToken );
                    return new VaultFile( connection, readers, masterKey, path );
                }
                catch
                {
                    foreach( var r in readers ) await r.DisposeAsync( );
                    await connection.DisposeAsync( );
                    SqliteConnection.ClearAllPools( );
                    DeleteSidecarFiles( path );
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
            SqliteConnection connection = await OpenConnectionAsync( path, password, SqliteOpenMode.ReadWrite, cancellationToken );
            SqliteConnection[] readers = [ ];

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

                // Step 4: Reader connections (WAL) so browsing never waits on writes
                readers = await OpenReadersAsync( connection, path, password, cancellationToken );

                return new VaultFile( connection, readers, masterKey, path );
            }
            catch
            {
                if( masterKey != null ) CryptographicOperations.ZeroMemory( masterKey );
                foreach( var r in readers ) await r.DisposeAsync( );
                await RestoreRollbackJournalAsync( connection );
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

        /// <summary>
        /// Waits for every in-flight vault step (all readers and the writer) to finish, closes the
        /// readers, folds the WAL back into the vault and returns it to rollback-journal mode, then
        /// zeroes the master key and closes the writer. Operations still waiting for their next step
        /// fail with <see cref="ObjectDisposedException"/> instead of touching a closed vault.
        /// </summary>
        public async ValueTask DisposeAsync( )
        {
            await _writeGate.WaitAsync( );
            for( int i = 0; i < _readers.Length; i++ )
                await _readGate.WaitAsync( );

            try
            {
                if( _disposed ) return;
                _disposed = true;

                foreach( var reader in _readers )
                    await reader.DisposeAsync( );

                await RestoreRollbackJournalAsync( _writer );

                CryptographicOperations.ZeroMemory( _encKey );
                await _writer.DisposeAsync( );
            }
            finally
            {
                if( _readers.Length > 0 ) _readGate.Release( _readers.Length );
                _writeGate.Release( );
            }
        }

        #endregion

        #region Leases

        /// <summary>Exclusive use of one connection. Dispose to give it back.</summary>
        private readonly struct DbLease( VaultFile owner, SqliteConnection db, bool isReader ) : IDisposable
        {
            public SqliteConnection Db => db;
            public void Dispose( ) => owner.Release( db, isReader );
        }

        /// <summary>The writer connection. One writer at a time.</summary>
        private async ValueTask<DbLease> EnterWriteAsync( CancellationToken cancellationToken )
        {
            await _writeGate.WaitAsync( cancellationToken );
            if( _disposed )
            {
                _writeGate.Release( );
                throw new ObjectDisposedException( nameof( VaultFile ), "The vault has been closed." );
            }
            return new DbLease( this, _writer, isReader: false );
        }

        /// <summary>A reader connection (or the writer, when WAL is unavailable).</summary>
        private async ValueTask<DbLease> EnterReadAsync( CancellationToken cancellationToken )
        {
            if( _readers.Length == 0 )
                return await EnterWriteAsync( cancellationToken );

            await _readGate.WaitAsync( cancellationToken );
            if( _disposed )
            {
                _readGate.Release( );
                throw new ObjectDisposedException( nameof( VaultFile ), "The vault has been closed." );
            }

            SqliteConnection reader;
            lock( _freeReaders ) reader = _freeReaders.Pop( );
            return new DbLease( this, reader, isReader: true );
        }

        private void Release( SqliteConnection db, bool isReader )
        {
            if( isReader )
            {
                lock( _freeReaders ) _freeReaders.Push( db );
                _readGate.Release( );
            }
            else
            {
                _writeGate.Release( );
            }
        }

        #endregion
    }
}
