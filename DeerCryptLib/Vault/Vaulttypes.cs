namespace DeerCryptLib.Vault
{
    /// <summary>
    /// Base type for anything that can live in a vault - either a file or a directory.
    /// Use pattern matching to distinguish:
    /// <code>
    /// VaultItem? item = await vault.GetEntryAsync(id);
    /// if (item is VaultEntry      file) { /* file      */ }
    /// if (item is VaultDirectoryEntry dir)  { /* directory */ }
    /// </code>
    /// </summary>
    public abstract record VaultItem( string Id, string Name );

    /// <summary>
    /// Represents a file stored in the vault.
    /// </summary>
    public sealed record VaultEntry(
        string Id,
        string Name,
        string DirectoryId,
        long OriginalSize,
        int ChunkCount,
        DateTime AddedAt ) : VaultItem( Id, Name );

    /// <summary>
    /// Represents a virtual directory inside the vault.
    /// <see cref="ParentId"/> is null only for the root directory.
    /// </summary>
    public sealed record VaultDirectoryEntry(
        string Id,
        string Name,
        string? ParentId,
        DateTime CreatedAt ) : VaultItem( Id, Name );

    /// <summary>
    /// The result of a <see cref="VaultFile.ListAsync"/> call -
    /// immediate subdirectories and files of a single directory.
    /// </summary>
    public sealed record VaultListing(
        IReadOnlyList<VaultDirectoryEntry> Directories,
        IReadOnlyList<VaultEntry> Files );

    /// <summary>
    /// Aggregate vault statistics for display in a status bar or info panel.
    /// </summary>
    public sealed record VaultInfo(
        int TotalFiles,
        int TotalDirectories,
        long TotalOriginalSize,
        long VaultFileSize,
        DateTime CreatedAt );
}