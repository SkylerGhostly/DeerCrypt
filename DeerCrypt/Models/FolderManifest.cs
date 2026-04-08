using System.Collections.Generic;

namespace DeerCrypt.Models
{
    /// <summary>
    /// Represents the result of scanning a folder on disk before adding it to the vault.
    /// </summary>
    public sealed class FolderManifest
    {
        public string RootPath { get; init; } = string.Empty;
        public string RootName { get; init; } = string.Empty;
        public int TotalFiles { get; init; }
        public int TotalFolders { get; init; }
        public long TotalSize { get; init; }
        public List<FolderEntry> Folders { get; init; } = [ ];
        public List<FileEntry> Files { get; init; } = [ ];
    }

    /// <summary>
    /// Represents a single folder in the manifest with its relative path from the root.
    /// </summary>
    public sealed class FolderEntry
    {
        public string RelativePath { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string ParentPath { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents a single file in the manifest with its location.
    /// </summary>
    public sealed class FileEntry
    {
        public string FullPath { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty; // relative to manifest root
        public string FolderPath { get; init; } = string.Empty; // relative folder path
        public long Size { get; init; }
    }
}