using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;

namespace DeerCryptLib.Tests
{
    public class VaultMaintenanceTests : VaultTestFixture
    {
        [Fact]
        public async Task CompactAsync_EmptyVault_Succeeds( )
        {
            Func<Task> act = async ( ) => await Vault!.CompactAsync( cancellationToken: TestContext.Current.CancellationToken );
            await act.Should( ).NotThrowAsync( );
        }

        [Fact]
        public async Task CompactAsync_AfterDeletions_VaultStillFunctional( )
        {
            string fileId = await Vault!.AddAsync( CreateTempFile( ), Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.RemoveAsync( fileId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.CompactAsync( cancellationToken: TestContext.Current.CancellationToken );
            // After compaction, we should still be able to add new files and list the vault
            string newFileId = await Vault.AddAsync( CreateTempFile( ), Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Files.Should( ).ContainSingle( f => f.Id == newFileId );
        }

        [Fact]
        public async Task GetVaultInfoAsync_VaultFileSizeIsPositive( )
        {
            VaultInfo info = await Vault!.GetVaultInfoAsync( cancellationToken: TestContext.Current.CancellationToken );
            info.VaultFileSize.Should( ).BeGreaterThan( 0 );
        }

        [Fact]
        public async Task CheckIntegrityAsync_CleanVault_ReturnsNoIssues( )
        {
            string sourcePath = CreateTempFile(1024);
            await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId,
                cancellationToken: TestContext.Current.CancellationToken );

            IReadOnlyList<string> issues = await Vault.CheckIntegrityAsync(
            TestContext.Current.CancellationToken);

            issues.Should( ).BeEmpty( );
        }
    }
}
