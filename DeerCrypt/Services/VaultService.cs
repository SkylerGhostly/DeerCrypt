using DeerCryptLib.Vault;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Owns the open VaultFile instance for the application lifetime.
    /// A single instance of this class is created at startup and passed
    /// to ViewModels that need vault access.
    /// </summary>
    public sealed class VaultService : IAsyncDisposable
    {
        private VaultFile? _vault;

        /// <summary>
        /// The currently open vault. Null if no vault is open.
        /// </summary>
        public VaultFile? Vault => _vault;

        /// <summary>
        /// True if a vault is currently open.
        /// </summary>
        public bool IsOpen => _vault != null;

        /// <summary>
        /// Creates a new vault and opens it.
        /// </summary>
        public async Task CreateAsync( string path, string password, CancellationToken cancellationToken = default )
        {
            if( _vault != null )
                await CloseAsync( );

            _vault = await VaultFile.CreateAsync( path, password, cancellationToken );
        }

        /// <summary>
        /// Opens an existing vault.
        /// </summary>
        public async Task OpenAsync( string path, string password, CancellationToken cancellationToken = default )
        {
            if( _vault != null )
                await CloseAsync( );

            _vault = await VaultFile.OpenAsync( path, password, cancellationToken );
        }

        /// <summary>
        /// Closes the currently open vault.
        /// </summary>
        public async Task CloseAsync( )
        {
            if( _vault != null )
            {
                await _vault.DisposeAsync( );
                _vault = null;
            }
        }

        public async ValueTask DisposeAsync( )
        {
            await CloseAsync( );
        }
    }
}