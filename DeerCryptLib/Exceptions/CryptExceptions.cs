using System;
using System.Collections.Generic;
using System.Text;

namespace DeerCryptLib.Exceptions
{
    internal class CryptException : Exception
    {
        public CryptException( ) { }
        public CryptException( string message ) : base( message ) { }
        public CryptException( Exception innerException ) : base( innerException.Message, innerException ) { }
        public CryptException( string message, Exception innerException ) : base( message, innerException ) { }
    }

    internal class CryptKeyException : CryptException
    {
        public CryptKeyException( ) { }
        public CryptKeyException( string message ) : base( message ) { }
        public CryptKeyException( Exception innerException ) : base( innerException.Message, innerException ) { }
        public CryptKeyException( string message, Exception innerException ) : base( message, innerException ) { }
    }

    internal class CryptDataException : CryptException
    {
        public CryptDataException( ) { }
        public CryptDataException( string message ) : base( message ) { }
        public CryptDataException( Exception innerException ) : base( innerException.Message, innerException ) { }
        public CryptDataException( string message, Exception innerException ) : base( message, innerException ) { }
    }
}
