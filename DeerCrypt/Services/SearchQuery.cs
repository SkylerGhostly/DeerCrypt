using System;
using System.Text.RegularExpressions;

namespace DeerCrypt.Services
{
    /// <summary>
    /// What the search box means. Plain text matches anywhere in a name (case-insensitive).
    /// With * (any run of characters) or ? (exactly one character), the pattern must match the
    /// whole name: "*.mp4", "somename*.mp4", "*-export.txt", "IMG_20??.jpg".
    /// </summary>
    public sealed class SearchQuery
    {
        private readonly string? _text;
        private readonly Regex?  _pattern;

        private SearchQuery( string? text, Regex? pattern )
        {
            _text    = text;
            _pattern = pattern;
        }

        public bool IsPattern => _pattern != null;

        public static SearchQuery Parse( string raw )
        {
            string q = raw.Trim( );
            if( q.IndexOfAny( [ '*', '?' ] ) < 0 )
                return new SearchQuery( q, null );

            string regex = "^" + Regex.Escape( q ).Replace( @"\*", ".*" ).Replace( @"\?", "." ) + "$";
            return new SearchQuery( null, new Regex( regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant ) );
        }

        public bool IsMatch( string name ) =>
            _pattern?.IsMatch( name ) ?? name.Contains( _text!, StringComparison.OrdinalIgnoreCase );
    }
}
