using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;

namespace DeerCryptLib.Tests
{
    public class VaultCreationTests : VaultTestFixture
    {
        [Fact]
        public async Task CreateAsync_CreatesVaultFileOnDisk( )
        {
            File.Exists( VaultPath ).Should( ).BeTrue( );
        }

        [Fact]
        public async Task CreateAsync_VaultFileHasDcvExtension( )
        {
            Path.GetExtension( VaultPath ).Should( ).Be( ".dcv" );
        }

        [Fact]
        public async Task CreateAsync_WhenVaultAlreadyExists_ThrowsVaultOperationException( )
        {
            Func<Task> act = async () =>
                await VaultFile.CreateAsync(VaultPath, TestPassword);

            await act.Should( ).ThrowAsync<VaultOperationException>( );
        }

        [Fact]
        public async Task CreateAsync_WithWrongExtension_ThrowsVaultOperationException( )
        {
            string badPath = Path.Combine(TempDir, "test.wrongext");

            Func<Task> act = async () =>
                await VaultFile.CreateAsync(badPath, TestPassword);

            await act.Should( ).ThrowAsync<VaultOperationException>( );
        }

        [Fact]
        public async Task OpenAsync_WithCorrectPassword_Succeeds( )
        {
            await Vault!.DisposeAsync( );

            Func<Task> act = async () =>
            {
                await using VaultFile vault = await VaultFile.OpenAsync(VaultPath, TestPassword);
            };

            await act.Should( ).NotThrowAsync( );
        }

        [Fact]
        public async Task OpenAsync_WithWrongPassword_ThrowsVaultPasswordException( )
        {
            await Vault!.DisposeAsync( );

            Func<Task> act = async () =>
            await VaultFile.OpenAsync(VaultPath, "wrongpassword",TestContext.Current.CancellationToken);

            await act.Should( ).ThrowAsync<VaultPasswordException>( );
        }

        [Fact]
        public async Task OpenAsync_WithNonExistentVault_ThrowsVaultNotFoundException( )
        {
            Func<Task> act = async () =>
                await VaultFile.OpenAsync(Path.Combine(TempDir, "nonexistent.dcv"), TestPassword);

            await act.Should( ).ThrowAsync<VaultNotFoundException>( );
        }

        [Fact]
        public async Task OpenAsync_NewVault_HasRootDirectory( )
        {
            VaultListing listing = await Vault!.ListAsync( Vault.RootDirectoryId, TestContext.Current.CancellationToken );

            listing.Directories.Should( ).BeEmpty( );
            listing.Files.Should( ).BeEmpty( );
        }
    }
}