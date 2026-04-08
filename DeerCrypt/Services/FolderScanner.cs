using DeerCrypt.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Scans a folder on disk and builds a FolderManifest describing its contents.
    /// </summary>
    public static class FolderScanner
    {
        public static FolderManifest Scan( string rootPath )
        {
            if( !Directory.Exists( rootPath ) )
                throw new DirectoryNotFoundException( $"Folder not found: {rootPath}" );

            string rootName = Path.GetFileName(
                rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            List<FolderEntry> folders = [];
            List<FileEntry>   files   = [];

            ScanRecursive( rootPath, rootPath, folders, files );

            return new FolderManifest
            {
                RootPath = rootPath,
                RootName = rootName,
                TotalFiles = files.Count,
                TotalFolders = folders.Count,
                TotalSize = files.Sum( f => f.Size ),
                Folders = folders,
                Files = files
            };
        }

        private static void ScanRecursive(
            string rootPath,
            string currentPath,
            List<FolderEntry> folders,
            List<FileEntry> files )
        {
            // Add subfolders
            foreach( string dir in Directory.GetDirectories( currentPath ) )
            {
                string relativePath = Path.GetRelativePath(rootPath, dir);
                string parentPath   = Path.GetRelativePath(rootPath,
                    Path.GetDirectoryName(dir) ?? rootPath);

                // Normalize - root-level folders have parent ""
                if( parentPath == "." )
                    parentPath = string.Empty;

                folders.Add( new FolderEntry
                {
                    RelativePath = relativePath,
                    Name = Path.GetFileName( dir ),
                    ParentPath = parentPath
                } );

                ScanRecursive( rootPath, dir, folders, files );
            }

            // Add files in current directory
            foreach( string file in Directory.GetFiles( currentPath ) )
            {
                string relativePath = Path.GetRelativePath(rootPath, file);
                string folderPath   = Path.GetRelativePath(rootPath,
                    Path.GetDirectoryName(file) ?? rootPath);

                if( folderPath == "." )
                    folderPath = string.Empty;

                files.Add( new FileEntry
                {
                    FullPath = file,
                    Name = Path.GetFileName( file ),
                    RelativePath = relativePath,
                    FolderPath = folderPath,
                    Size = new FileInfo( file ).Length
                } );
            }
        }
    }
}