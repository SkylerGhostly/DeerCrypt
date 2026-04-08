using DeerCryptLib.Vault;
using Microsoft.Data.Sqlite;

namespace DeerCryptLib.Tests.Helpers
{
    /// <summary>
    /// Creates a fresh temporary vault before each test and deletes it after.
    /// Inherit from this in any test class that needs a vault to work with.
    /// </summary>
    public abstract class VaultTestFixture : IAsyncLifetime
    {
        protected const string TestPassword = "test-password-123";

        protected string VaultPath { get; private set; } = string.Empty;
        protected string TempDir { get; private set; } = string.Empty;
        protected VaultFile? Vault { get; private set; }

        public async ValueTask InitializeAsync( )
        {
            TempDir = Path.Combine( Path.GetTempPath( ), Path.GetRandomFileName( ) );
            VaultPath = Path.Combine( TempDir, "test.dcv" );

            Directory.CreateDirectory( TempDir );

            Vault = await VaultFile.CreateAsync( VaultPath, TestPassword );
        }

        public async ValueTask DisposeAsync( )
        {
            if( Vault != null )
                await Vault.DisposeAsync( );

            if( Directory.Exists( TempDir ) )
                Directory.Delete( TempDir, recursive: true );

            GC.SuppressFinalize( this );
        }

        /// <summary>
        /// Creates a temp file with random content of the specified size in bytes.
        /// Returns the path to the created file.
        /// </summary>
        protected string CreateTempFile( int sizeInBytes = 1024, string name = "", string extension = ".bin" )
        {
            string fileName = string.IsNullOrEmpty( name ) ? Path.GetRandomFileName() : name;
            string path = Path.Combine(TempDir, fileName + extension);
            byte[] data = new byte[sizeInBytes];
            Random.Shared.NextBytes( data );
            File.WriteAllBytes( path, data );
            return path;
        }

        /// <summary>
        /// Creates a temp file with specific known content.
        /// Useful for verifying extraction produces identical bytes.
        /// </summary>
        protected string CreateTempFileWithContent( byte [ ] content, string name = "", string extension = ".bin" )
        {
            string fileName = string.IsNullOrEmpty( name ) ? Path.GetRandomFileName() : name;
            string path = Path.Combine(TempDir, fileName + extension);
            File.WriteAllBytes( path, content );
            return path;
        }
    }
}