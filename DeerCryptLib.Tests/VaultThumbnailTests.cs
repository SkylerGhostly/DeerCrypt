using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace DeerCryptLib.Tests
{
    /// <summary>
    /// The thumbnail cache is an additive part of the vault: no table until first use, no version
    /// change, encrypted rows tied to the file's current contents, cascade on delete, LRU trimming.
    /// </summary>
    public class VaultThumbnailTests : VaultTestFixture
    {
        private bool TableExistsOnDisk( )
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = VaultPath, Password = TestPassword, Pooling = false, Mode = SqliteOpenMode.ReadOnly };
            using var c = new SqliteConnection( cs.ToString( ) );
            c.Open( );
            using var cmd = new SqliteCommand( "SELECT 1 FROM sqlite_master WHERE name = 'vault_thumbnails'", c );
            return cmd.ExecuteScalar( ) != null;
        }

        private static byte [ ] Image( int size, byte seed ) => Enumerable.Range( 0, size ).Select( i => (byte)( i * 7 + seed ) ).ToArray( );

        [Fact]
        public async Task NoTable_UntilAThumbnailIsStored_AndReadsWorkWithoutIt( )
        {
            var ct = TestContext.Current.CancellationToken;
            string id = await Vault!.AddAsync( CreateTempFile( 5000 ), Vault.RootDirectoryId, cancellationToken: ct );

            ( await Vault.GetThumbnailAsync( id, ct ) ).Should( ).BeNull( );
            ( await Vault.GetThumbnailCacheInfoAsync( ct ) ).Should( ).Be( new ThumbnailCacheInfo( 0, 0 ) );
            await Vault.FlushThumbnailUsageAsync( ct );
            ( await Vault.TrimThumbnailCacheAsync( 1, ct ) ).Should( ).Be( 0 );
            TableExistsOnDisk( ).Should( ).BeFalse( "reading or trimming must never change the vault" );

            await Vault.PutThumbnailAsync( id, Image( 900, 1 ), ct );
            TableExistsOnDisk( ).Should( ).BeTrue( );
        }

        [Fact]
        public async Task RoundTrips_IsEncrypted_AndKeepsTheVaultOpenable( )
        {
            var ct = TestContext.Current.CancellationToken;
            string id = await Vault!.AddAsync( CreateTempFile( 5000 ), Vault.RootDirectoryId, cancellationToken: ct );
            byte [ ] img = Image( 4000, 3 );

            await Vault.PutThumbnailAsync( id, img, ct );
            ( await Vault.GetThumbnailAsync( id, ct ) ).Should( ).Equal( img );

            var info = await Vault.GetThumbnailCacheInfoAsync( ct );
            info.Count.Should( ).Be( 1 );
            info.Bytes.Should( ).BeGreaterThan( img.Length, "stored with a GCM nonce and tag" );

            await Vault.DisposeAsync( );

            // Stored bytes are not the plaintext
            var cs = new SqliteConnectionStringBuilder { DataSource = VaultPath, Password = TestPassword, Pooling = false, Mode = SqliteOpenMode.ReadOnly };
            using( var c = new SqliteConnection( cs.ToString( ) ) )
            {
                c.Open( );
                using var cmd = new SqliteCommand( "SELECT data FROM vault_thumbnails", c );
                var stored = (byte [ ])cmd.ExecuteScalar( )!;
                stored.AsSpan( ).IndexOf( img.AsSpan( 0, 64 ) ).Should( ).Be( -1 );
            }

            await using var reopened = await VaultFile.OpenAsync( VaultPath, TestPassword, ct );
            ( await reopened.GetThumbnailAsync( id, ct ) ).Should( ).Equal( img );
        }

        [Fact]
        public async Task EmptyMarker_MeansNoThumbnail( )
        {
            var ct = TestContext.Current.CancellationToken;
            string id = await Vault!.AddAsync( CreateTempFile( 5000 ), Vault.RootDirectoryId, cancellationToken: ct );
            await Vault.PutThumbnailAsync( id, ReadOnlyMemory<byte>.Empty, ct );
            ( await Vault.GetThumbnailAsync( id, ct ) ).Should( ).NotBeNull( ).And.BeEmpty( );
        }

        [Fact]
        public async Task ChangedFileContents_InvalidateTheThumbnail( )
        {
            var ct = TestContext.Current.CancellationToken;
            string id = await Vault!.AddAsync( CreateTempFile( 5000 ), Vault.RootDirectoryId, cancellationToken: ct );
            await Vault.PutThumbnailAsync( id, Image( 500, 5 ), ct );

            await Vault.UpdateAsync( id, CreateTempFile( 7000 ), cancellationToken: ct );

            ( await Vault.GetThumbnailAsync( id, ct ) ).Should( ).BeNull( );
        }

        [Fact]
        public async Task DeletingTheFile_DeletesItsThumbnail( )
        {
            var ct = TestContext.Current.CancellationToken;
            string dir = await Vault!.CreateDirectoryAsync( "d", Vault.RootDirectoryId, ct );
            string a = await Vault.AddAsync( CreateTempFile( 5000 ), Vault.RootDirectoryId, cancellationToken: ct );
            string b = await Vault.AddAsync( CreateTempFile( 5000 ), dir, cancellationToken: ct );
            await Vault.PutThumbnailAsync( a, Image( 500, 1 ), ct );
            await Vault.PutThumbnailAsync( b, Image( 500, 2 ), ct );

            await Vault.RemoveAsync( a, cancellationToken: ct );
            await Vault.RemoveDirectoryAsync( dir, cancellationToken: ct );

            ( await Vault.GetThumbnailCacheInfoAsync( ct ) ).Count.Should( ).Be( 0 );
        }

        [Fact]
        public async Task Trim_RemovesLeastRecentlyUsedFirst( )
        {
            var ct = TestContext.Current.CancellationToken;
            var ids = new List<string>( );
            for( int i = 0; i < 10; i++ )
            {
                string id = await Vault!.AddAsync( CreateTempFile( 1000 + i ), Vault.RootDirectoryId, cancellationToken: ct );
                await Vault.PutThumbnailAsync( id, Image( 1000, (byte)i ), ct );
                ids.Add( id );
            }
            long each = ( await Vault!.GetThumbnailCacheInfoAsync( ct ) ).Bytes / 10;

            int removed = await Vault.TrimThumbnailCacheAsync( each * 5, ct );

            removed.Should( ).BeGreaterThanOrEqualTo( 5 );
            var info = await Vault.GetThumbnailCacheInfoAsync( ct );
            info.Bytes.Should( ).BeLessThanOrEqualTo( each * 5 * 9 / 10 );
            // Same day for all: the oldest inserted go first
            ( await Vault.GetThumbnailAsync( ids [ 0 ], ct ) ).Should( ).BeNull( );
            ( await Vault.GetThumbnailAsync( ids [ 9 ], ct ) ).Should( ).NotBeNull( );
        }

        [Fact]
        public async Task Clear_DropsTheTable( )
        {
            var ct = TestContext.Current.CancellationToken;
            string id = await Vault!.AddAsync( CreateTempFile( 5000 ), Vault.RootDirectoryId, cancellationToken: ct );
            await Vault.PutThumbnailAsync( id, Image( 500, 1 ), ct );

            await Vault.ClearThumbnailCacheAsync( ct );

            ( await Vault.GetThumbnailAsync( id, ct ) ).Should( ).BeNull( );
            TableExistsOnDisk( ).Should( ).BeFalse( );

            // And it can be used again afterwards
            await Vault.PutThumbnailAsync( id, Image( 500, 2 ), ct );
            ( await Vault.GetThumbnailAsync( id, ct ) ).Should( ).Equal( Image( 500, 2 ) );
        }

        [Fact]
        public async Task Settings_RoundTrip_AndCanBeRemoved( )
        {
            var ct = TestContext.Current.CancellationToken;
            ( await Vault!.GetSettingAsync( "thumbnails", ct ) ).Should( ).BeNull( );
            await Vault.SetSettingAsync( "thumbnails", "Blurred", ct );
            await Vault.SetSettingAsync( "thumbnails", "On", ct );
            ( await Vault.GetSettingAsync( "thumbnails", ct ) ).Should( ).Be( "On" );
            await Vault.SetSettingAsync( "thumbnails", null, ct );
            ( await Vault.GetSettingAsync( "thumbnails", ct ) ).Should( ).BeNull( );
        }
    }
}
