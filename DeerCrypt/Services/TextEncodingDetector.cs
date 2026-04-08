using System.Text;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Detects the character encoding of a raw byte array by inspecting
    /// the byte-order mark (BOM) or falling back to a UTF-8 validity heuristic.
    /// </summary>
    public static class TextEncodingDetector
    {
        public static Encoding Detect( byte[] data )
        {
            if( data.Length == 0 ) return Encoding.UTF8;

            // BOM inspection 
            if( data.Length >= 4 )
            {
                if( data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00 )
                    return Encoding.UTF32;                                    // UTF-32 LE

                if( data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFE && data[3] == 0xFF )
                    return new UTF32Encoding( bigEndian: true, byteOrderMark: true );   // UTF-32 BE
            }

            if( data.Length >= 3 )
            {
                if( data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF )
                    return new UTF8Encoding( encoderShouldEmitUTF8Identifier: true );   // UTF-8 BOM
            }

            if( data.Length >= 2 )
            {
                if( data[0] == 0xFF && data[1] == 0xFE ) return Encoding.Unicode;          // UTF-16 LE
                if( data[0] == 0xFE && data[1] == 0xFF ) return Encoding.BigEndianUnicode; // UTF-16 BE
            }

            // Heuristic: validate as UTF-8 without BOM 
            if( IsValidUtf8( data ) )
                return new UTF8Encoding( encoderShouldEmitUTF8Identifier: false );

            // Fallback: Latin-1 accepts all byte values 
            return Encoding.Latin1;
        }

        private static bool IsValidUtf8( byte[] data )
        {
            int i = 0;
            while( i < data.Length )
            {
                byte b = data[ i ];
                int extra;

                if(      b <= 0x7F )                    { i++; continue; }   // ASCII
                else if( b >= 0xC2 && b <= 0xDF ) extra = 1;
                else if( b >= 0xE0 && b <= 0xEF ) extra = 2;
                else if( b >= 0xF0 && b <= 0xF4 ) extra = 3;
                else return false;                          // invalid leading byte

                if( i + extra >= data.Length ) return false;

                for( int j = 1; j <= extra; j++ )
                    if( (data[ i + j ] & 0xC0) != 0x80 ) return false;

                i += extra + 1;
            }
            return true;
        }
    }
}
