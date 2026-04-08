using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Rainbow-column CSV colorizer.  Each comma-separated field is colored with
    /// a different muted hue (cycling every 6 columns) so columns are instantly
    /// distinguishable.  Quoted fields and escaped quotes ("") are handled correctly.
    /// Registered as an AvaloniaEdit line transformer - independent of TextMate.
    /// </summary>
    internal sealed class CsvColorizingTransformer : DocumentColorizingTransformer
    {
        // Palette 
        // Six cycling column colours + one for the comma glyph itself.
        // Dark palette mirrors the muted hues used in DeerCryptTheme.
        // Light palette uses the same hues but darkened for a light background.

        private static readonly Color[] DarkColumns =
        [
            Color.Parse( "#84B4D4" ),   // steel-blue
            Color.Parse( "#D4AC70" ),   // amber
            Color.Parse( "#A0C0A0" ),   // mint
            Color.Parse( "#CC8866" ),   // terracotta
            Color.Parse( "#B0A0D8" ),   // lavender
            Color.Parse( "#7AAEC8" ),   // teal
        ];

        private static readonly Color[] LightColumns =
        [
            Color.Parse( "#2070A0" ),   // blue
            Color.Parse( "#9A6020" ),   // amber-brown
            Color.Parse( "#406040" ),   // olive
            Color.Parse( "#A04020" ),   // rust
            Color.Parse( "#7060B0" ),   // purple
            Color.Parse( "#2060A0" ),   // slate-blue
        ];

        private static readonly Color DarkSeparator  = Color.Parse( "#555C6B" );
        private static readonly Color LightSeparator = Color.Parse( "#A0A0B0" );

        // Fields 

        private readonly SolidColorBrush[]  _cols;
        private readonly SolidColorBrush    _sep;

        // Construction 

        public CsvColorizingTransformer( bool isDark )
        {
            var palette = isDark ? DarkColumns : LightColumns;
            _cols = new SolidColorBrush[ palette.Length ];
            for( int i = 0; i < palette.Length; i++ )
                _cols[ i ] = new SolidColorBrush( palette[ i ] );

            _sep = new SolidColorBrush( isDark ? DarkSeparator : LightSeparator );
        }

        // Coloring 

        protected override void ColorizeLine( DocumentLine line )
        {
            var text       = CurrentContext.Document.GetText( line );
            int col        = 0;
            int fieldStart = 0;
            bool inQuotes  = false;

            for( int i = 0; i <= text.Length; i++ )
            {
                // Inside a quoted field 
                if( inQuotes )
                {
                    if( i < text.Length && text[ i ] == '"' )
                    {
                        if( i + 1 < text.Length && text[ i + 1 ] == '"' )
                            i++;              // "" → escaped quote, skip both
                        else
                            inQuotes = false; // closing quote
                    }
                    continue;
                }

                // End of line - flush last field 
                if( i == text.Length )
                {
                    if( i > fieldStart )
                        Paint( line.Offset + fieldStart, line.Offset + i,
                               _cols[ col % _cols.Length ] );
                    break;
                }

                char c = text[ i ];

                if( c == '"' )
                {
                    inQuotes = true;
                }
                else if( c == ',' )
                {
                    // Color everything before the comma as the current column
                    if( i > fieldStart )
                        Paint( line.Offset + fieldStart, line.Offset + i,
                               _cols[ col % _cols.Length ] );

                    // Dim the comma glyph
                    Paint( line.Offset + i, line.Offset + i + 1, _sep );

                    col++;
                    fieldStart = i + 1;
                }
            }
        }

        private void Paint( int start, int end, IBrush brush )
        {
            if( start >= end ) return;
            ChangeLinePart( start, end, el => el.TextRunProperties.SetForegroundBrush( brush ) );
        }
    }
}
