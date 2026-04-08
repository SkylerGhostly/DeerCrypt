using DeerCryptLib.Exceptions;
using System.Buffers;
using System.Security.Cryptography;

namespace DeerCryptLib.Core
{
    /// <summary>
    /// Core AES-256-CBC encryption engine.
    ///
    /// Security properties:
    ///  - PBKDF2-SHA512 key derivation (600,000 iterations) - resistant to GPU brute-force
    ///  - Random 256-bit salt per encryption - same password never produces the same key
    ///  - Random IV per encryption - same plaintext never produces the same ciphertext
    ///  - HMAC-SHA256 authentication (Encrypt-then-MAC) - detects tampering before decryption
    ///  - Constant-time HMAC comparison - resistant to timing attacks
    ///
    /// Two modes, distinguished by version byte:
    ///
    ///  Version 1 - Password mode (standalone file/string encryption)
    ///    Header: [Version:1][Salt:32][IV:16]  →  49 bytes
    ///    Keys are derived from the password + salt via PBKDF2 on every call.
    ///
    ///  Version 2 - Pre-keyed mode (vault chunk encryption)
    ///    Header: [Version:1][IV:16]  →  17 bytes
    ///    Caller supplies keys directly. No per-chunk PBKDF2 - the vault derives
    ///    keys once at open time and reuses them for every chunk.
    /// </summary>
    internal static class Crypt
    {
        #region Constants

        // Version bytes - stored as the first byte of every encrypted blob
        internal const byte PasswordVersion      = 1; // password mode: header includes salt
        internal const byte KeyedVersion         = 2; // pre-keyed CBC mode: header is IV only
        internal const byte GcmKeyedVersion      = 3; // GCM master-key mode: header is nonce only

        // Header sizes
        internal const int  SaltSize             = 32;                           // 256-bit salt
        internal const int  HmacSize             = 32;                           // 256-bit HMAC-SHA256
        internal const int  AesIvSize            = AesBlockSize / 8;             // 16 bytes
        internal const int  PasswordHeaderSize   = 1 + SaltSize + AesIvSize;    // 49 bytes
        internal const int  KeyedHeaderSize      = 1 + AesIvSize;               // 17 bytes

        // AES configuration
        internal const int  AesKeySize           = 256;                          // bits
        internal const int  AesBlockSize         = 128;                          // bits
        internal const int  AesKeySizeBytes      = AesKeySize / 8;               // 32 bytes
        internal const int  AesBlockSizeInBytes  = AesIvSize;                    // alias

        // Buffer sizing
        internal const int  SmallSizeThreshold   = 10 * 1024 * 1024;            // 10 MB
        internal const int  SmallBufferSize      = 4_096;                        // 4 KB
        internal const int  LargeBufferSize      = 1_048_576;                    // 1 MB

        // GCM configuration (Version 3)
        internal const int  GcmNonceSize          = 12;                          // 96-bit nonce
        internal const int  GcmTagSize            = 16;                          // 128-bit auth tag
        internal const int  GcmKeyedHeaderSize    = 1 + GcmNonceSize;            // 13 bytes
        internal const int  GcmKekDerivedKeyLength = AesKeySizeBytes;            // 32 bytes - KEK only, no HMAC key

        // PBKDF2 - OWASP 2023 recommendation for PBKDF2-SHA512.
        // One derivation produces 64 bytes: 32 for AES key, 32 for HMAC key.
        private const int   Pbkdf2Iterations     = 600_000;
        private const int   DerivedKeyLength     = AesKeySizeBytes + HmacSize;   // 64 bytes

        #endregion

        #region Password-Based API (Version 1 - standalone use)

        /// <summary>
        /// Encrypts <paramref name="inputStream"/> into <paramref name="outputStream"/> using
        /// a password. Derives keys via PBKDF2 with a fresh random salt on every call.
        /// Use this for standalone file or string encryption outside of a vault.
        /// </summary>
        internal static async Task Encrypt(
            Stream inputStream,
            Stream outputStream,
            string password,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] iv   = RandomNumberGenerator.GetBytes(AesIvSize);

            DeriveKeys( password, salt, out byte [ ] encKey, out byte [ ] hmacKey );

            byte[] header = BuildPasswordHeader(salt, iv);
            await EncryptCoreAsync( inputStream, outputStream, encKey, hmacKey, header, iv, progress, cancellationToken );
        }

        /// <summary>
        /// Decrypts a Version 1 (password-mode) blob.
        /// </summary>
        internal static async Task Decrypt(
            Stream inputStream,
            Stream outputStream,
            string password,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            if( !inputStream.CanSeek )
                throw new NotSupportedException( "Decryption requires a seekable stream." );

            long streamStart = inputStream.Position;
            long available   = inputStream.Length - streamStart;

            if( available < PasswordHeaderSize + AesBlockSizeInBytes + HmacSize )
                throw new CryptDataException( "Data is too short to be valid encrypted data." );

            byte[] header = new byte[PasswordHeaderSize];
            await inputStream.ReadExactlyAsync( header, cancellationToken );

            if( header [ 0 ] != PasswordVersion )
                throw new CryptDataException( $"Expected password-mode blob (version {PasswordVersion}), got version {header [ 0 ]}." );

            byte[] salt = header[1..(1 + SaltSize)];
            byte[] iv   = header[(1 + SaltSize)..PasswordHeaderSize];

            DeriveKeys( password, salt, out byte [ ] encKey, out byte [ ] hmacKey );

            long ciphertextStart  = streamStart + PasswordHeaderSize;
            long ciphertextLength = available - PasswordHeaderSize - HmacSize;

            await DecryptCoreAsync( inputStream, outputStream, encKey, hmacKey, header, iv,
                ciphertextStart, ciphertextLength, progress, cancellationToken );
        }

        #endregion

        #region Pre-Keyed API (Version 2 - vault chunk use)

        /// <summary>
        /// Encrypts <paramref name="inputStream"/> into <paramref name="outputStream"/> using
        /// caller-supplied keys. Generates a fresh random IV on every call - no PBKDF2.
        /// Use this for vault chunk encryption where the vault derives keys once at open time.
        /// </summary>
        internal static async Task Encrypt(
            Stream inputStream,
            Stream outputStream,
            byte [ ] encKey,
            byte [ ] hmacKey,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            ValidateKeyBytes( encKey, nameof( encKey ) );
            ValidateKeyBytes( hmacKey, nameof( hmacKey ) );

            byte[] iv     = RandomNumberGenerator.GetBytes(AesIvSize);
            byte[] header = BuildKeyedHeader(iv);

            await EncryptCoreAsync( inputStream, outputStream, encKey, hmacKey, header, iv, progress, cancellationToken );
        }

        /// <summary>
        /// Decrypts a Version 2 (pre-keyed) blob using caller-supplied keys.
        /// </summary>
        internal static async Task Decrypt(
            Stream inputStream,
            Stream outputStream,
            byte [ ] encKey,
            byte [ ] hmacKey,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            if( !inputStream.CanSeek )
                throw new NotSupportedException( "Decryption requires a seekable stream." );

            ValidateKeyBytes( encKey, nameof( encKey ) );
            ValidateKeyBytes( hmacKey, nameof( hmacKey ) );

            long streamStart = inputStream.Position;
            long available   = inputStream.Length - streamStart;

            if( available < KeyedHeaderSize + AesBlockSizeInBytes + HmacSize )
                throw new CryptDataException( "Data is too short to be valid encrypted data." );

            byte[] header = new byte[KeyedHeaderSize];
            await inputStream.ReadExactlyAsync( header, cancellationToken );

            if( header [ 0 ] != KeyedVersion )
                throw new CryptDataException( $"Expected pre-keyed blob (version {KeyedVersion}), got version {header [ 0 ]}." );

            byte[] iv = header[1..KeyedHeaderSize];

            long ciphertextStart  = streamStart + KeyedHeaderSize;
            long ciphertextLength = available - KeyedHeaderSize - HmacSize;

            await DecryptCoreAsync( inputStream, outputStream, encKey, hmacKey, header, iv,
                ciphertextStart, ciphertextLength, progress, cancellationToken );
        }

        #endregion

        #region GCM Master-Key API (Version 3 - vault chunks with KEK/DEK)

        /// <summary>
        /// Encrypts <paramref name="inputStream"/> into <paramref name="outputStream"/> using
        /// AES-256-GCM with a caller-supplied master key. Generates a fresh random 96-bit nonce
        /// per call. Format: [GcmKeyedVersion:1][Nonce:12][Ciphertext:N][Tag:16].
        /// </summary>
        internal static async Task EncryptGcmKeyed(
            Stream inputStream,
            Stream outputStream,
            byte [ ] masterKey,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            ValidateKeyBytes( masterKey, nameof( masterKey ) );

            byte[] nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);

            // AesGcm requires a fully-materialized plaintext buffer
            using MemoryStream ms = new();
            await inputStream.CopyToAsync( ms, GetBufferSize( inputStream.CanSeek ? inputStream.Length : 0 ), cancellationToken );
            byte[] plaintext = ms.ToArray( );

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag        = new byte[GcmTagSize];

            using( AesGcm aesGcm = new(masterKey, GcmTagSize) )
                aesGcm.Encrypt( nonce, plaintext, ciphertext, tag );

            await outputStream.WriteAsync( new byte [ ] { GcmKeyedVersion }, cancellationToken );
            await outputStream.WriteAsync( nonce,      cancellationToken );
            await outputStream.WriteAsync( ciphertext, cancellationToken );
            await outputStream.WriteAsync( tag,        cancellationToken );

            progress?.Report( 1.0 );
        }

        /// <summary>
        /// Decrypts a Version 3 (GCM master-key) blob using a caller-supplied master key.
        /// Throws <see cref="CryptographicException"/> if the authentication tag does not verify -
        /// callers should wrap this as a tamper/corruption error.
        /// </summary>
        internal static async Task DecryptGcmKeyed(
            Stream inputStream,
            Stream outputStream,
            byte [ ] masterKey,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default )
        {
            if( !inputStream.CanSeek )
                throw new NotSupportedException( "GCM decryption requires a seekable stream." );

            ValidateKeyBytes( masterKey, nameof( masterKey ) );

            long available = inputStream.Length - inputStream.Position;

            if( available < GcmKeyedHeaderSize + GcmTagSize )
                throw new CryptDataException( "Data is too short to be a valid GCM-encrypted blob." );

            byte[] versionBuf = new byte[1];
            await inputStream.ReadExactlyAsync( versionBuf, cancellationToken );
            if( versionBuf [ 0 ] != GcmKeyedVersion )
                throw new CryptDataException( $"Expected GCM keyed blob (version {GcmKeyedVersion}), got version {versionBuf [ 0 ]}." );

            byte[] nonce = new byte[GcmNonceSize];
            await inputStream.ReadExactlyAsync( nonce, cancellationToken );

            long   ciphertextLength = available - GcmKeyedHeaderSize - GcmTagSize;
            byte[] ciphertext       = new byte[ciphertextLength];
            await inputStream.ReadExactlyAsync( ciphertext, cancellationToken );

            byte[] tag = new byte[GcmTagSize];
            await inputStream.ReadExactlyAsync( tag, cancellationToken );

            byte[] plaintext = new byte[ciphertextLength];

            using( AesGcm aesGcm = new(masterKey, GcmTagSize) )
                aesGcm.Decrypt( nonce, ciphertext, tag, plaintext ); // throws CryptographicException on tag mismatch

            await outputStream.WriteAsync( plaintext, cancellationToken );

            progress?.Report( 1.0 );
        }

        #endregion

        #region Shared Core

        /// <summary>
        /// Shared encryption path. The caller builds the correct header and supplies keys.
        /// </summary>
        private static async Task EncryptCoreAsync(
            Stream inputStream,
            Stream outputStream,
            byte [ ] encKey,
            byte [ ] hmacKey,
            byte [ ] header,
            byte [ ] iv,
            IProgress<double>? progress,
            CancellationToken cancellationToken )
        {
            await outputStream.WriteAsync( header, cancellationToken );

            using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, hmacKey);
            hmac.AppendData( header );

            using Aes aes = CreateAes(encKey, iv);
            using ICryptoTransform encryptor = aes.CreateEncryptor();

            // HmacInterceptStream feeds every ciphertext byte to the HMAC as it's written -
            // no second pass over the ciphertext needed
            using  HmacInterceptStream hmacStream   = new(outputStream, hmac);
            await using CryptoStream   cryptoStream = new(hmacStream, encryptor, CryptoStreamMode.Write, leaveOpen: true);

            await inputStream.CopyToAsync( cryptoStream, GetBufferSize( inputStream.Length ), progress, cancellationToken );
            await cryptoStream.FlushFinalBlockAsync( cancellationToken );

            await outputStream.WriteAsync( hmac.GetHashAndReset( ), cancellationToken );
        }

        /// <summary>
        /// Shared decryption path. Authenticates first (Encrypt-then-MAC), then decrypts.
        /// All positions are absolute - no relative seek arithmetic.
        /// </summary>
        private static async Task DecryptCoreAsync(
            Stream inputStream,
            Stream outputStream,
            byte [ ] encKey,
            byte [ ] hmacKey,
            byte [ ] header,
            byte [ ] iv,
            long ciphertextStart,
            long ciphertextLength,
            IProgress<double>? progress,
            CancellationToken cancellationToken )
        {
            if( ciphertextLength <= 0 || ciphertextLength % AesBlockSizeInBytes != 0 )
                throw new CryptDataException( "Ciphertext length is invalid." );

            await VerifyHmacAsync( inputStream, header, ciphertextStart, ciphertextLength, hmacKey, cancellationToken );

            inputStream.Seek( ciphertextStart, SeekOrigin.Begin );

            using Aes aes = CreateAes(encKey, iv);
            using ICryptoTransform decryptor = aes.CreateDecryptor();

            using  BoundedReadStream bounded      = new(inputStream, ciphertextLength);
            await using CryptoStream cryptoStream = new(bounded, decryptor, CryptoStreamMode.Read);

            await cryptoStream.CopyToAsync( outputStream, GetBufferSize( ciphertextLength ), progress, cancellationToken );
        }

        #endregion

        #region Internal Utilities

        internal static int GetBufferSize( long streamSize )
        {
            double scale = Math.Min(1.0, (double)streamSize / SmallSizeThreshold);
            return (int)( SmallBufferSize + scale * ( LargeBufferSize - SmallBufferSize ) );
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Derives a 32-byte Key Encryption Key (KEK) via PBKDF2-SHA512.
        /// Used to wrap/unwrap the vault master key. Never used directly to encrypt chunks.
        /// </summary>
        internal static byte [ ] DeriveKek( string password, byte [ ] kdfSalt )
        {
            if( string.IsNullOrWhiteSpace( password ) )
                throw new CryptKeyException( "Password cannot be null or empty." );

            return Rfc2898DeriveBytes.Pbkdf2(
                password,
                kdfSalt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA512,
                GcmKekDerivedKeyLength );
        }

        /// <summary>
        /// Wraps a 32-byte master key using AES-256-GCM with the supplied KEK.
        /// Returns a 60-byte blob: [Nonce:12][EncryptedKey:32][Tag:16].
        /// </summary>
        internal static byte [ ] WrapKey( byte [ ] kek, byte [ ] keyToWrap )
        {
            ValidateKeyBytes( kek, nameof( kek ) );
            ValidateKeyBytes( keyToWrap, nameof( keyToWrap ) );

            byte[] nonce      = RandomNumberGenerator.GetBytes(GcmNonceSize);
            byte[] ciphertext = new byte[keyToWrap.Length];
            byte[] tag        = new byte[GcmTagSize];

            using( AesGcm aesGcm = new(kek, GcmTagSize) )
                aesGcm.Encrypt( nonce, keyToWrap, ciphertext, tag );

            byte[] result = new byte[GcmNonceSize + keyToWrap.Length + GcmTagSize]; // 60 bytes
            Buffer.BlockCopy( nonce,      0, result, 0,                             GcmNonceSize );
            Buffer.BlockCopy( ciphertext, 0, result, GcmNonceSize,                  keyToWrap.Length );
            Buffer.BlockCopy( tag,        0, result, GcmNonceSize + keyToWrap.Length, GcmTagSize );
            return result;
        }

        /// <summary>
        /// Unwraps a 60-byte wrapped key blob using AES-256-GCM with the supplied KEK.
        /// Throws <see cref="CryptographicException"/> if the tag does not verify (wrong KEK / tampered blob).
        /// </summary>
        internal static byte [ ] UnwrapKey( byte [ ] kek, byte [ ] wrappedKey )
        {
            ValidateKeyBytes( kek, nameof( kek ) );

            int expectedLength = GcmNonceSize + AesKeySizeBytes + GcmTagSize; // 60
            if( wrappedKey == null || wrappedKey.Length != expectedLength )
                throw new CryptDataException( $"Wrapped key blob must be exactly {expectedLength} bytes." );

            byte[] nonce        = wrappedKey[..GcmNonceSize];
            byte[] encryptedKey = wrappedKey[GcmNonceSize..(GcmNonceSize + AesKeySizeBytes)];
            byte[] tag          = wrappedKey[(GcmNonceSize + AesKeySizeBytes)..];

            byte[] masterKey = new byte[AesKeySizeBytes];

            using( AesGcm aesGcm = new(kek, GcmTagSize) )
                aesGcm.Decrypt( nonce, encryptedKey, tag, masterKey ); // throws CryptographicException on tag mismatch

            return masterKey;
        }

        /// <summary>
        /// Derives a 64-byte key via PBKDF2-SHA512 and splits it into an AES key and HMAC key.
        /// Only called by the password-based overloads - the vault never calls this per-chunk.
        /// </summary>
        private static void DeriveKeys( string password, byte [ ] salt, out byte [ ] encKey, out byte [ ] hmacKey )
        {
            if( string.IsNullOrWhiteSpace( password ) )
                throw new CryptKeyException( "Password cannot be null or empty." );

            byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA512,
                DerivedKeyLength);

            encKey = derived [ ..AesKeySizeBytes ];
            hmacKey = derived [ AesKeySizeBytes.. ];
        }

        private static void ValidateKeyBytes( byte [ ] key, string paramName )
        {
            if( key is null || key.Length != AesKeySizeBytes )
                throw new CryptKeyException( $"{paramName} must be exactly {AesKeySizeBytes} bytes (256 bits)." );
        }

        private static Aes CreateAes( byte [ ] key, byte [ ] iv )
        {
            Aes aes       = Aes.Create();
            aes.KeySize = AesKeySize;
            aes.BlockSize = AesBlockSize;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;
            return aes;
        }

        // Version 1: [PasswordVersion:1][Salt:32][IV:16]
        private static byte [ ] BuildPasswordHeader( byte [ ] salt, byte [ ] iv )
        {
            byte[] header = new byte[PasswordHeaderSize];
            header [ 0 ] = PasswordVersion;
            Buffer.BlockCopy( salt, 0, header, 1, SaltSize );
            Buffer.BlockCopy( iv, 0, header, 1 + SaltSize, AesIvSize );
            return header;
        }

        // Version 2: [KeyedVersion:1][IV:16]
        private static byte [ ] BuildKeyedHeader( byte [ ] iv )
        {
            byte[] header = new byte[KeyedHeaderSize];
            header [ 0 ] = KeyedVersion;
            Buffer.BlockCopy( iv, 0, header, 1, AesIvSize );
            return header;
        }

        /// <summary>
        /// Reads the HMAC tag from its absolute position, streams through the ciphertext
        /// to compute the expected tag, and compares with a constant-time equality check.
        /// All seeks use SeekOrigin.Begin with pre-calculated absolute positions.
        /// </summary>
        private static async Task VerifyHmacAsync(
            Stream inputStream,
            byte [ ] header,
            long ciphertextStart,
            long ciphertextLength,
            byte [ ] hmacKey,
            CancellationToken cancellationToken )
        {
            long hmacStart = ciphertextStart + ciphertextLength;

            inputStream.Seek( hmacStart, SeekOrigin.Begin );
            byte[] storedHmac = new byte[HmacSize];
            await inputStream.ReadExactlyAsync( storedHmac, cancellationToken );

            inputStream.Seek( ciphertextStart, SeekOrigin.Begin );

            using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, hmacKey);
            hmac.AppendData( header );

            byte[] buffer    = ArrayPool<byte>.Shared.Rent(LargeBufferSize);
            long   remaining = ciphertextLength;
            try
            {
                while( remaining > 0 )
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read   = await inputStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if( read == 0 )
                        throw new CryptDataException( "Unexpected end of stream while verifying authentication tag." );
                    hmac.AppendData( buffer.AsSpan( 0, read ) );
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return( buffer );
            }

            if( !CryptographicOperations.FixedTimeEquals( hmac.GetHashAndReset( ), storedHmac ) )
                throw new CryptDataException( "Authentication failed. The data may be corrupted or the password is wrong." );
        }

        #endregion

        #region Inner Stream Helpers

        /// <summary>
        /// Write-only stream wrapper that feeds every written byte through an
        /// <see cref="IncrementalHash"/> before passing it to the inner stream.
        /// Computes the HMAC over ciphertext in a single pass during encryption.
        /// </summary>
        private sealed class HmacInterceptStream : Stream
        {
            private readonly Stream          _inner;
            private readonly IncrementalHash _hmac;

            internal HmacInterceptStream( Stream inner, IncrementalHash hmac )
            {
                _inner = inner;
                _hmac = hmac;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException( );
            }

            public override void Write( byte [ ] buffer, int offset, int count )
            {
                _hmac.AppendData( buffer, offset, count );
                _inner.Write( buffer, offset, count );
            }

            public override async Task WriteAsync( byte [ ] buffer, int offset, int count, CancellationToken cancellationToken )
            {
                _hmac.AppendData( buffer, offset, count );
                await _inner.WriteAsync( buffer.AsMemory( offset, count ), cancellationToken );
            }

            public override async ValueTask WriteAsync( ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default )
            {
                _hmac.AppendData( buffer.Span );
                await _inner.WriteAsync( buffer, cancellationToken );
            }

            public override void Flush( ) => _inner.Flush( );
            public override Task FlushAsync( CancellationToken ct ) => _inner.FlushAsync( ct );
            public override int Read( byte [ ] b, int o, int c ) => throw new NotSupportedException( );
            public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException( );
            public override void SetLength( long value ) => throw new NotSupportedException( );
        }

        /// <summary>
        /// Read-only stream wrapper that limits how many bytes can be read from the inner stream.
        /// Prevents the AES decryptor from consuming the trailing HMAC tag as ciphertext.
        /// </summary>
        private sealed class BoundedReadStream : Stream
        {
            private readonly Stream _inner;
            private long            _remaining;

            internal BoundedReadStream( Stream inner, long length )
            {
                _inner = inner;
                _remaining = length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException( );
            public override long Position
            {
                get => throw new NotSupportedException( );
                set => throw new NotSupportedException( );
            }

            public override int Read( byte [ ] buffer, int offset, int count )
            {
                if( _remaining <= 0 ) return 0;
                int toRead = (int)Math.Min(count, _remaining);
                int read   = _inner.Read(buffer, offset, toRead);
                _remaining -= read;
                return read;
            }

            public override async ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default )
            {
                if( _remaining <= 0 ) return 0;
                int toRead = (int)Math.Min(buffer.Length, _remaining);
                int read   = await _inner.ReadAsync(buffer[..toRead], cancellationToken);
                _remaining -= read;
                return read;
            }

            public override Task<int> ReadAsync( byte [ ] buffer, int offset, int count, CancellationToken cancellationToken ) =>
                ReadAsync( buffer.AsMemory( offset, count ), cancellationToken ).AsTask( );

            // CryptoStream calls Flush on disposal - safe no-op for a read-only stream
            public override void Flush( ) { }
            public override Task FlushAsync( CancellationToken cancellationToken ) => Task.CompletedTask;
            public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException( );
            public override void SetLength( long value ) => throw new NotSupportedException( );
            public override void Write( byte [ ] buffer, int offset, int count ) => throw new NotSupportedException( );
        }

        #endregion
    }
}