using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace DeerCryptLib.Tests
{
    /// <summary>
    /// While open, a vault runs in WAL mode with separate reader connections. Locked, it must be
    /// a single file in rollback mode again. A vault left in WAL mode by a crash must still open.
    /// </summary>
    public class VaultWalTests : VaultTestFixture
    {
        private static string JournalModeOnDisk( string path, string password )
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Password = password, Pooling = false, Mode = SqliteOpenMode.ReadOnly };
            using var c = new SqliteConnection( cs.ToString( ) );
            c.Open( );
            using var cmd = new SqliteCommand( "PRAGMA journal_mode", c );
            return (string)cmd.ExecuteScalar( )!;
        }

        [Fact]
        public async Task OpenVault_UsesWal_AndLockedVaultIsSingleFileAgain( )
        {
            var ct = TestContext.Current.CancellationToken;
            Vault!.HasConcurrentReads.Should( ).BeTrue( );
            await Vault.AddAsync( CreateTempFile( 200_000 ), Vault.RootDirectoryId, cancellationToken: ct );
            File.Exists( VaultPath + "-wal" ).Should( ).BeTrue( "the vault is open in WAL mode" );

            await Vault.DisposeAsync( );

            File.Exists( VaultPath + "-wal" ).Should( ).BeFalse( );
            File.Exists( VaultPath + "-shm" ).Should( ).BeFalse( );
            JournalModeOnDisk( VaultPath, TestPassword ).Should( ).Be( "delete" );

            await using VaultFile reopened = await VaultFile.OpenAsync( VaultPath, TestPassword, ct );
            ( await reopened.ListAsync( reopened.RootDirectoryId, ct ) ).Files.Should( ).HaveCount( 1 );
        }

        [Fact]
        public async Task Reads_FinishWhileALargeWriteIsStillRunning( )
        {
            var ct = TestContext.Current.CancellationToken;
            string big = CreateTempFile( 160 * 1024 * 1024 );

            var inTransaction = new TaskCompletionSource( );
            var progress = new SyncProgress( p => { if( p > 0 ) inTransaction.TrySetResult( ); } );
            Task add = Vault!.AddAsync( big, Vault.RootDirectoryId, progress, ct );

            await inTransaction.Task.WaitAsync( TimeSpan.FromSeconds( 60 ), ct );

            var sw = Stopwatch.StartNew( );
            await Vault.ListAsync( Vault.RootDirectoryId, ct );
            await Vault.GetVaultInfoAsync( ct );
            sw.Stop( );

            add.IsCompleted.Should( ).BeFalse( "the reads should not have waited for the add's transaction" );
            await add;
        }

        [Fact]
        public async Task IntegrityCheck_RunsAlongsideWrites_AndReportsProgress( )
        {
            var ct = TestContext.Current.CancellationToken;
            for( int i = 0; i < 30; i++ )
                await Vault!.AddAsync( CreateTempFile( 50_000 ), Vault.RootDirectoryId, cancellationToken: ct );

            double last = 0;
            var progress = new SyncProgress( p => last = p );

            Task<IReadOnlyList<string>> check = Vault!.CheckIntegrityAsync( progress, ct );
            Task<string> add = Vault.AddAsync( CreateTempFile( 3 * 1024 * 1024 ), Vault.RootDirectoryId, cancellationToken: ct );

            ( await check ).Should( ).BeEmpty( );
            await add;
            last.Should( ).Be( 1.0 );
        }

        [Fact]
        public async Task VaultLeftInWalModeByACrash_StillOpensWithItsData( )
        {
            var ct = TestContext.Current.CancellationToken;
            byte[] content = new byte[ 300_000 ];
            Random.Shared.NextBytes( content );
            string id = await Vault!.AddAsync( CreateTempFileWithContent( content ), Vault.RootDirectoryId, cancellationToken: ct );

            // Snapshot the files as a crash would leave them: committed data still in the -wal
            string crashDir = Path.Combine( TempDir, "crash" );
            Directory.CreateDirectory( crashDir );
            string crashed = Path.Combine( crashDir, "crashed.dcv" );
            File.Copy( VaultPath, crashed );
            File.Copy( VaultPath + "-wal", crashed + "-wal" );

            // Wrong password is still rejected in this state
            Func<Task> wrong = ( ) => VaultFile.OpenAsync( crashed, "not-the-password", ct );
            await wrong.Should( ).ThrowAsync<VaultPasswordException>( );

            await using VaultFile recovered = await VaultFile.OpenAsync( crashed, TestPassword, ct );
            string outPath = Path.Combine( crashDir, "out.bin" );
            await recovered.ExtractAsync( id, outPath, cancellationToken: ct );
            File.ReadAllBytes( outPath ).Should( ).Equal( content );
        }

        /// <summary>Progress that reports synchronously (Progress&lt;T&gt; would post to a context).</summary>
        private sealed class SyncProgress( Action<double> report ) : IProgress<double>
        {
            public void Report( double value ) => report( value );
        }
    }
}
