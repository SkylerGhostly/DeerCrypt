using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Reads files and folders copied in a file manager from the clipboard, so Ctrl+V can add
    /// them to the vault (drag and drop from other apps isn't available on Linux with Avalonia 11).
    /// Tries Avalonia's file list first, then the raw formats Linux file managers publish:
    /// text/uri-list (Dolphin, most apps) and x-special/gnome-copied-files (GNOME Files).
    /// </summary>
    public static class ClipboardFiles
    {
        private static readonly string [ ] RawFormats = [ "text/uri-list", "x-special/gnome-copied-files" ];

        public static async Task<(List<string> Files, List<string> Folders)> ReadAsync( IClipboard? clipboard )
        {
            List<string> paths = [ ];
            if( clipboard == null ) return Split( paths );

            try
            {
                var items = await clipboard.TryGetFilesAsync( );
                foreach( var item in items ?? [ ] )
                    if( item.TryGetLocalPath( ) is { } p ) paths.Add( p );
            }
            catch { }

            foreach( string mime in RawFormats )
            {
                if( paths.Count > 0 ) break;
                try
                {
                    string? text = await clipboard.TryGetValueAsync( DataFormat.CreateStringPlatformFormat( mime ) );
                    paths.AddRange( ParseUriList( text ) );
                }
                catch { }
            }

            return Split( paths );
        }

        /// <summary>Local paths from a URI list (one per line; "#" comments and GNOME's copy/cut header skipped).</summary>
        public static IEnumerable<string> ParseUriList( string? text )
        {
            if( string.IsNullOrWhiteSpace( text ) ) yield break;
            foreach( string raw in text.Split( [ '\r', '\n', '\0' ], StringSplitOptions.RemoveEmptyEntries ) )
            {
                string line = raw.Trim( );
                if( line.Length == 0 || line.StartsWith( '#' ) || line is "copy" or "cut" ) continue;
                if( Uri.TryCreate( line, UriKind.Absolute, out var uri ) && uri.IsFile )
                    yield return uri.LocalPath;
            }
        }

        private static (List<string> Files, List<string> Folders) Split( IEnumerable<string> paths )
        {
            List<string> files = [ ], folders = [ ];
            foreach( string p in paths.Select( p => p.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) ).Distinct( ) )
            {
                if( Directory.Exists( p ) ) folders.Add( p );
                else if( File.Exists( p ) ) files.Add( p );
            }
            return (files, folders);
        }
    }
}
