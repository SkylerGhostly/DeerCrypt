using System.Collections.Generic;
using System.IO;

namespace DeerCrypt.Services
{
    public enum OpenWithChoice
    {
        None,
        MediaPlayer,
        ImageViewer,
        TextEditor,
        External
    }

    public enum FileViewerType
    {
        /// <summary>Audio or video - open in the built-in LibVLC media player.</summary>
        Media,

        /// <summary>Plain text or code - reserved for a future text viewer.</summary>
        Text,

        /// <summary>Raster or vector image - reserved for a future image viewer.</summary>
        Image,

        /// <summary>Everything else - decrypt to temp and open with the system default app.</summary>
        External
    }

    /// <summary>
    /// Maps a filename extension to the viewer type that should handle it.
    /// </summary>
    public static class FileTypeHelper
    {
        // Multimedia (VLC-compatible) 
        private static readonly HashSet<string> _media = new( System.StringComparer.OrdinalIgnoreCase )
        {
            // Video containers
            ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv",
            ".ts",  ".m2ts", ".mts", ".vob",  ".mpg", ".mpeg", ".m2v",
            ".divx", ".xvid", ".3gp", ".3g2", ".rmvb", ".rm", ".asf",
            ".mxf", ".f4v",  ".ogv", ".dv",   ".amv",
            // Audio containers
            ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg",  ".oga", ".opus",
            ".wma", ".wv",   ".ape", ".aiff", ".aif", ".ac3", ".dts", ".mka",
            ".ra",  ".mid",  ".midi", ".mod", ".s3m", ".it",  ".xm"
        };

        // Text / code - hook-up for future text viewer 
        private static readonly HashSet<string> _text = new( System.StringComparer.OrdinalIgnoreCase )
        {
            ".txt", ".md",   ".log",  ".csv",  ".json", ".xml", ".yaml", ".yml",
            ".toml", ".ini", ".cfg",  ".conf", ".sh",   ".bat", ".ps1",  ".cmd",
            ".cs",  ".js",   ".ts",   ".py",   ".java", ".cpp", ".c",    ".h",
            ".html", ".htm", ".css",  ".rs",   ".go",   ".rb",  ".php",  ".sql"
        };

        // Images - hook-up for future image viewer 
        private static readonly HashSet<string> _image = new( System.StringComparer.OrdinalIgnoreCase )
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg",
            ".ico", ".tiff", ".tif", ".heic", ".heif", ".avif", ".jxl"
        };

        /// <summary>
        /// Classifies a vault file by its extension to determine which viewer should
        /// open it.
        /// </summary>
        public static FileViewerType Classify( string fileName )
        {
            string ext = Path.GetExtension( fileName );
            if( _media.Contains( ext ) )  return FileViewerType.Media;
            if( _text.Contains( ext ) )   return FileViewerType.Text;
            if( _image.Contains( ext ) )  return FileViewerType.Image;
            return FileViewerType.External;
        }

        public static bool IsMedia( string fileName )  => Classify( fileName ) == FileViewerType.Media;
        public static bool IsText(  string fileName )  => Classify( fileName ) == FileViewerType.Text;
        public static bool IsImage( string fileName )  => Classify( fileName ) == FileViewerType.Image;
    }
}
