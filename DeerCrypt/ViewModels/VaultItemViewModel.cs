using CommunityToolkit.Mvvm.ComponentModel;
using DeerCryptLib.Vault;
using System;
using System.IO;

namespace DeerCrypt.ViewModels
{
    /// <summary>
    /// Wraps a VaultItem for display in the file listing.
    /// Holds UI-only state like IsRenaming that doesn't belong on the library type.
    /// </summary>
    public partial class VaultItemViewModel( VaultItem item ) : ObservableObject
    {
        public VaultItem Item { get; } = item;

        [ObservableProperty]
        public partial string DisplayName { get; set; } = item.Name;

        [ObservableProperty]
        public partial bool IsRenaming { get; set; }

        [ObservableProperty]
        public partial string EditingName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsDragTarget { get; set; }

        [ObservableProperty]
        public partial bool IsCheckedOut { get; set; }

        /// <summary>True for the virtual ".." parent-directory navigation entry.</summary>
        public bool IsParentDir { get; private init; }

        /// <summary>Used by MoveDialog to grey out items that are being moved.</summary>
        [ObservableProperty]
        public partial bool IsDisabled { get; set; }

        public string Id => Item.Id;
        public bool IsFile => Item is VaultEntry;
        public bool IsDir => Item is VaultDirectoryEntry;

        public VaultEntry? AsFile => Item as VaultEntry;
        public VaultDirectoryEntry? AsDir => Item as VaultDirectoryEntry;

        /// <summary>
        /// Friendly type label for the Type column.
        /// Computed once at construction time - items are replaced on every reload.
        /// </summary>
        public string TypeText { get; } = ComputeTypeText( item );

        /// <summary>
        /// Secondary line shown below the name in search results (e.g. folder path).
        /// Empty string when not in search context.
        /// </summary>
        public string SubText { get; set; } = string.Empty;

        public bool HasSubText => SubText.Length > 0;

        /// <summary>Creates the virtual ".." parent-directory navigation entry.</summary>
        public static VaultItemViewModel CreateParentDir( string parentDirId )
        {
            var fake = new VaultDirectoryEntry( parentDirId, "..", null, System.DateTime.MinValue );
            return new VaultItemViewModel( fake ) { IsParentDir = true };
        }

        private static string ComputeTypeText( VaultItem item )
        {
            if( item is VaultDirectoryEntry ) return "Folder";
            if( item is not VaultEntry entry ) return "";

            string ext = Path.GetExtension( entry.Name ).TrimStart( '.' ).ToLowerInvariant();

            return ext switch
            {
                "png"  => "PNG Image",
                "jpg" or "jpeg" => "JPEG Image",
                "gif"  => "GIF Image",
                "webp" => "WebP Image",
                "bmp"  => "Bitmap Image",
                "tiff" or "tif" => "TIFF Image",
                "ico"  => "Icon Image",
                "svg"  => "SVG Image",
                "heic" or "heif" => "HEIC Image",
                "raw" or "cr2" or "nef" or "arw" => "RAW Image",

                "mp4" or "mkv" or "avi" or "mov" or "wmv" or "webm" or "flv" or "m4v" => "Video",

                "mp3"  => "MP3 Audio",
                "flac" => "FLAC Audio",
                "wav"  => "WAV Audio",
                "aac" or "m4a" => "AAC Audio",
                "ogg"  => "OGG Audio",
                "wma"  => "WMA Audio",

                "pdf"  => "PDF Document",
                "doc" or "docx" => "Word Document",
                "xls" or "xlsx" => "Excel Spreadsheet",
                "ppt" or "pptx" => "PowerPoint Presentation",
                "odt"  => "OpenDocument Text",
                "ods"  => "OpenDocument Spreadsheet",
                "odp"  => "OpenDocument Presentation",

                "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" or "zst" => "Archive",

                "txt"  => "Text File",
                "csv"  => "CSV File",
                "json" => "JSON File",
                "xml"  => "XML File",
                "yaml" or "yml" => "YAML File",
                "toml" => "TOML File",
                "ini" or "cfg" or "conf" => "Config File",
                "log"  => "Log File",
                "md"   => "Markdown File",

                "html" or "htm" => "HTML File",
                "css"  => "CSS File",
                "js"   => "JavaScript File",
                "ts"   => "TypeScript File",
                "jsx" or "tsx" => "React File",
                "cs"   => "C# Source",
                "cpp" or "cc" or "cxx" => "C++ Source",
                "c"    => "C Source",
                "h" or "hpp" => "Header File",
                "py"   => "Python Script",
                "rs"   => "Rust Source",
                "go"   => "Go Source",
                "java" => "Java Source",
                "kt"   => "Kotlin Source",
                "rb"   => "Ruby Script",
                "php"  => "PHP File",
                "swift" => "Swift Source",
                "sh" or "bash" => "Shell Script",
                "ps1"  => "PowerShell Script",
                "bat" or "cmd" => "Batch Script",
                "sql"  => "SQL File",

                "exe" or "msi" => "Application",
                "dll"  => "DLL File",
                "so"   => "Shared Library",
                "dmg" or "pkg" => "macOS Package",
                "deb" or "rpm" => "Linux Package",

                "ttf" or "otf" or "woff" or "woff2" => "Font File",

                "psd"  => "Photoshop Document",
                "ai"   => "Illustrator Document",
                "fig"  => "Figma File",
                "sketch" => "Sketch File",

                "torrent" => "Torrent File",
                "iso"  => "Disk Image",

                "" => "File",
                _  => $"{ext.ToUpperInvariant()} File"
            };
        }

        /// <summary>
        /// Formatted date for the Date column - works for both files and directories.
        /// </summary>
        public string DateText
        {
            get
            {
                if( IsParentDir ) return "";
                DateTime? date = AsFile?.AddedAt ?? AsDir?.CreatedAt;
                return date?.ToString( "yyyy-MM-dd HH:mm" ) ?? "";
            }
        }

        public void BeginRename( )
        {
            EditingName = DisplayName;
            IsRenaming = true;
        }

        public void CancelRename( )
        {
            IsRenaming = false;
            EditingName = string.Empty;
        }
    }
}
