using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;

namespace DeerCryptLib.Tests
{
    /// <summary>
    /// The UI runs adds, extracts, and deletes in the background while the user keeps browsing,
    /// all on one VaultFile. These tests hammer one instance from several tasks at once.
    /// </summary>
    public class VaultConcurrencyTests : VaultTestFixture
    {
        [Fact]
        public async Task BrowsingWhileAddingLargeFile_NeverFails( )
        {
            var ct = TestContext.Current.CancellationToken;
            string big = CreateTempFile( 40 * 1024 * 1024 ); // several chunks, one long transaction

            Task<string> add = Vault!.AddAsync( big, Vault.RootDirectoryId, cancellationToken: ct );

            // Reads issued while the add's transaction may be open must wait their turn, not throw
            var reads = Enumerable.Range( 0, 50 ).Select( async _ =>
            {
                await Vault.ListAsync( Vault.RootDirectoryId, ct );
                await Vault.GetVaultInfoAsync( ct );
                await Vault.ListRecursiveAsync( null, ct );
            } );

            await Task.WhenAll( reads.Append( add ) );

            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, ct );
            listing.Files.Should( ).ContainSingle( f => f.Id == add.Result );
        }

        [Fact]
        public async Task ParallelAddsExtractsAndStreams_AllRoundTrip( )
        {
            var ct = TestContext.Current.CancellationToken;
            byte[][] contents = Enumerable.Range( 0, 6 ).Select( i =>
            {
                byte[] b = new byte[ ( i + 1 ) * 700_000 ];
                Random.Shared.NextBytes( b );
                return b;
            } ).ToArray( );

            string[] ids = await Task.WhenAll( contents.Select( c =>
                Vault!.AddAsync( CreateTempFileWithContent( c ), Vault.RootDirectoryId, cancellationToken: ct ) ) );

            // Extract everything and stream everything at the same time
            var extracts = ids.Select( async ( id, i ) =>
            {
                string outPath = Path.Combine( TempDir, $"out-{i}.bin" );
                await Vault!.ExtractAsync( id, outPath, cancellationToken: ct );
                File.ReadAllBytes( outPath ).Should( ).Equal( contents [ i ] );
            } );

            var streams = ids.Select( async ( id, i ) =>
            {
                await using VaultReadStream s = await Vault!.OpenReadStreamAsync( id, ct );
                using var ms = new MemoryStream( );
                await s.CopyToAsync( ms, ct );
                ms.ToArray( ).Should( ).Equal( contents [ i ] );
            } );

            await Task.WhenAll( extracts.Concat( streams ) );
        }

        [Fact]
        public async Task Dispose_WaitsForInFlightWork_ThenRejectsNewWork( )
        {
            var ct = TestContext.Current.CancellationToken;
            string big = CreateTempFile( 24 * 1024 * 1024 );

            Task add = Vault!.AddAsync( big, Vault.RootDirectoryId, cancellationToken: ct );
            await Task.Delay( 20, ct );
            ValueTask dispose = Vault.DisposeAsync( );

            // The add either finished before dispose took the gate, or never started its transaction
            // and was refused; it must never run against a closed connection.
            Exception? addError = await Record.ExceptionAsync( ( ) => add );
            if( addError != null )
                addError.Should( ).BeOfType<ObjectDisposedException>( );

            await dispose;

            Func<Task> listAfter = ( ) => Vault.ListAsync( Vault.RootDirectoryId, ct );
            await listAfter.Should( ).ThrowAsync<ObjectDisposedException>( );
        }

        [Fact]
        public async Task DirectoryStats_CountsRecursively( )
        {
            var ct = TestContext.Current.CancellationToken;
            string a = await Vault!.CreateDirectoryAsync( "A", Vault.RootDirectoryId, ct );
            string b = await Vault.CreateDirectoryAsync( "B", a, ct );
            await Vault.CreateDirectoryAsync( "C", b, ct );
            await Vault.AddAsync( CreateTempFile( 1000 ), a, cancellationToken: ct );
            await Vault.AddAsync( CreateTempFile( 2500 ), b, cancellationToken: ct );

            DirectoryStats stats = await Vault.GetDirectoryStatsAsync( a, ct );
            stats.FolderCount.Should( ).Be( 2 );
            stats.FileCount.Should( ).Be( 2 );
            stats.TotalSize.Should( ).Be( 3500 );

            DirectoryStats root = await Vault.GetDirectoryStatsAsync( Vault.RootDirectoryId, ct );
            root.FolderCount.Should( ).Be( 3 );
            root.FileCount.Should( ).Be( 2 );
        }
    }
}
