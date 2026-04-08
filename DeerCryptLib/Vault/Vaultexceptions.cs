namespace DeerCryptLib.Vault
{
    /// <summary>
    /// Base class for all DeerCrypt vault exceptions.
    /// Catch this to handle any vault error in a single clause,
    /// or catch a specific subtype for targeted handling.
    /// </summary>
    public class VaultException : Exception
    {
        public VaultException( ) { }
        public VaultException( string message ) : base( message ) { }
        public VaultException( string message, Exception innerException ) : base( message, innerException ) { }
    }

    /// <summary>
    /// The vault file does not exist at the specified path.
    /// </summary>
    public sealed class VaultNotFoundException : VaultException
    {
        public string VaultPath { get; }

        public VaultNotFoundException( string vaultPath )
            : base( $"Vault file not found: {vaultPath}" )
        {
            VaultPath = vaultPath;
        }

        public VaultNotFoundException( string vaultPath, Exception innerException )
            : base( $"Vault file not found: {vaultPath}", innerException )
        {
            VaultPath = vaultPath;
        }
    }

    /// <summary>
    /// The vault file exists but its contents are unreadable, incomplete, or
    /// from an unsupported version. This may indicate a corrupt or truncated file.
    /// </summary>
    public sealed class VaultCorruptException : VaultException
    {
        public VaultCorruptException( string message ) : base( message ) { }
        public VaultCorruptException( string message, Exception innerException ) : base( message, innerException ) { }
    }

    /// <summary>
    /// The supplied password is wrong or empty.
    /// </summary>
    public sealed class VaultPasswordException : VaultException
    {
        public VaultPasswordException( string message ) : base( message ) { }
        public VaultPasswordException( string message, Exception innerException ) : base( message, innerException ) { }
    }

    /// <summary>
    /// The SHA-256 hash of an extracted file does not match the hash stored at add time.
    /// The output file has been deleted. The vault chunk data may be corrupt or tampered with.
    /// </summary>
    public sealed class VaultIntegrityException : VaultException
    {
        /// <summary>The display name of the file that failed verification.</summary>
        public string FileName { get; }

        /// <summary>The GUID of the file that failed verification.</summary>
        public string FileId { get; }

        public VaultIntegrityException( string fileName, string fileId )
            : base( $"Integrity check failed for '{fileName}' (ID: {fileId}). " +
                   "The extracted file did not match the stored hash and has been deleted." )
        {
            FileName = fileName;
            FileId = fileId;
        }

        public VaultIntegrityException( string fileName, string fileId, Exception innerException )
            : base( $"Integrity check failed for '{fileName}' (ID: {fileId}). " +
                   "The extracted file did not match the stored hash and has been deleted.",
                   innerException )
        {
            FileName = fileName;
            FileId = fileId;
        }
    }

    /// <summary>
    /// No file or directory with the specified GUID exists in the vault.
    /// </summary>
    public sealed class VaultItemNotFoundException : VaultException
    {
        /// <summary>The GUID that was not found.</summary>
        public string ItemId { get; }

        public VaultItemNotFoundException( string itemId )
            : base( $"No file or directory with ID '{itemId}' exists in this vault." )
        {
            ItemId = itemId;
        }

        public VaultItemNotFoundException( string itemId, string context )
            : base( $"No {context} with ID '{itemId}' exists in this vault." )
        {
            ItemId = itemId;
        }
    }

    /// <summary>
    /// A vault operation was attempted that is not permitted.
    /// Examples: removing the root directory, moving a directory into itself,
    /// creating a directory with an empty name, or providing an invalid target path.
    /// </summary>
    public sealed class VaultOperationException : VaultException
    {
        public VaultOperationException( string message ) : base( message ) { }
        public VaultOperationException( string message, Exception innerException ) : base( message, innerException ) { }
    }
}