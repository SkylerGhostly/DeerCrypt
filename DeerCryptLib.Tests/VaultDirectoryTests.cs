using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;

namespace DeerCryptLib.Tests
{
    public class VaultDirectoryTests : VaultTestFixture
    {
        #region Create Tests
        [Fact]
        public async Task CreateDirectoryAsync_AppearsInListing( )
        {
            await Vault!.CreateDirectoryAsync( "SomeDir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );

            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );

            listing.Directories.Should().ContainSingle( d => d.Name == "SomeDir" );
        }

        [Fact]
        public async Task CreateDirectoryAsync_WithEmptyName_ThrowsVaultOperationException( )
        {
            Func<Task> act = async ( ) => await Vault!.CreateDirectoryAsync( string.Empty, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultOperationException>();
        }

        [Fact]
        public async Task CreateDirectoryAsync_WithNonExistentParent_ThrowsVaultItemNotFoundException( )
        {
            Func<Task> act = async ( ) => await Vault!.CreateDirectoryAsync( "SomeDir", "NonExistentParentId", cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }

        [Fact]
        public async Task CreateDirectoryAsync_NestedDirectories_AllAppearInCorrectListings( )
        {
            string parentDirId = await Vault!.CreateDirectoryAsync( "ParentDir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string childDirId = await Vault.CreateDirectoryAsync( "ChildDir", parentDirId, cancellationToken: TestContext.Current.CancellationToken );
            string childDir2Id = await Vault.CreateDirectoryAsync( "ChildDir2", childDirId, cancellationToken: TestContext.Current.CancellationToken );
            VaultListing rootListing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            rootListing.Directories.Should().ContainSingle( d => d.Id == parentDirId && d.Name == "ParentDir" );
            VaultListing parentListing = await Vault.ListAsync( parentDirId, cancellationToken: TestContext.Current.CancellationToken );
            parentListing.Directories.Should().ContainSingle( d => d.Id == childDirId && d.Name == "ChildDir" );
            VaultListing childListing = await Vault.ListAsync( childDirId, cancellationToken: TestContext.Current.CancellationToken );
            childListing.Directories.Should().ContainSingle( d => d.Id == childDir2Id && d.Name == "ChildDir2" );
        }
        #endregion

        #region Remove Tests
        [Fact]
        public async Task RemoveDirectoryAsync_EmptyDirectory_Succeeds( )
        {
            string dirId = await Vault!.CreateDirectoryAsync( "DirToRemove", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.RemoveDirectoryAsync( dirId, cancellationToken: TestContext.Current.CancellationToken );
            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Directories.Should().NotContain( d => d.Id == dirId );
        }

        [Fact]
        public async Task RemoveDirectoryAsync_WithFiles_RemovesFilesAlso( )
        {
            int chunkSize = 1024 * 1024; // 1MB
            byte[] originalContent = new byte[chunkSize];
            Random.Shared.NextBytes( originalContent );
            string sourcePath1 = CreateTempFileWithContent(originalContent);
            Random.Shared.NextBytes( originalContent );
            string sourcePath2 = CreateTempFileWithContent(originalContent);

            string dirId = await Vault!.CreateDirectoryAsync( "DirToRemove", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            
            string fileId1 = await Vault.AddAsync( sourcePath1, dirId, cancellationToken: TestContext.Current.CancellationToken );
            string fileId2 = await Vault.AddAsync( sourcePath2, dirId, cancellationToken: TestContext.Current.CancellationToken );

            VaultListing listing = await Vault.ListAsync( dirId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Files.Should().ContainSingle( f => f.Id == fileId1 );
            listing.Files.Should().ContainSingle( f => f.Id == fileId2 );

            await Vault.RemoveDirectoryAsync( dirId, cancellationToken: TestContext.Current.CancellationToken );

            VaultItem? item1 = await Vault.GetEntryAsync( fileId1, cancellationToken: TestContext.Current.CancellationToken );
            VaultItem? item2 = await Vault.GetEntryAsync( fileId2, cancellationToken: TestContext.Current.CancellationToken );

            item1.Should().BeNull();
            item2.Should().BeNull();
        }

        [Fact]
        public async Task RemoveDirectoryAsync_Recursive_RemovesAllDescendants( )
        {
            int chunkSize = 1024 * 1024; // 1MB
            byte[] originalContent = new byte[chunkSize];
            Random.Shared.NextBytes( originalContent );
            string sourcePath1 = CreateTempFileWithContent(originalContent);
            Random.Shared.NextBytes( originalContent );
            string sourcePath2 = CreateTempFileWithContent(originalContent);
            Random.Shared.NextBytes( originalContent );
            string sourcePath3 = CreateTempFileWithContent(originalContent);

            string dirMain = await Vault!.CreateDirectoryAsync( "MainDir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string dirSub1 = await Vault.CreateDirectoryAsync( "SubDir1", dirMain, cancellationToken: TestContext.Current.CancellationToken );
            string dirSub2 = await Vault.CreateDirectoryAsync( "SubDir2", dirSub1, cancellationToken: TestContext.Current.CancellationToken );

            string fileId1 = await Vault.AddAsync( sourcePath1, dirMain, cancellationToken: TestContext.Current.CancellationToken );
            string fileId2 = await Vault.AddAsync( sourcePath2, dirSub1, cancellationToken: TestContext.Current.CancellationToken );
            string fileId3 = await Vault.AddAsync( sourcePath3, dirSub2, cancellationToken: TestContext.Current.CancellationToken );

            VaultItem? item1 = await Vault.GetEntryAsync( fileId1, cancellationToken: TestContext.Current.CancellationToken );
            VaultItem? item2 = await Vault.GetEntryAsync( fileId2, cancellationToken: TestContext.Current.CancellationToken );
            VaultItem? item3 = await Vault.GetEntryAsync( fileId3, cancellationToken: TestContext.Current.CancellationToken );

            item1.Should( ).NotBeNull( );
            item2.Should( ).NotBeNull( );
            item3.Should( ).NotBeNull( );

            await Vault.RemoveDirectoryAsync( dirMain, cancellationToken: TestContext.Current.CancellationToken );

            item1 = await Vault.GetEntryAsync( fileId1, cancellationToken: TestContext.Current.CancellationToken );
            item2 = await Vault.GetEntryAsync( fileId2, cancellationToken: TestContext.Current.CancellationToken );
            item3 = await Vault.GetEntryAsync( fileId3, cancellationToken: TestContext.Current.CancellationToken );

            item1.Should( ).BeNull( );
            item2.Should( ).BeNull( );
            item3.Should( ).BeNull( );
        }

        [Fact]
        public async Task RemoveDirectoryAsync_RootDirectory_ThrowsVaultOperationException( )
        {
            Func<Task> act = async ( ) => await Vault!.RemoveDirectoryAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultOperationException>();
        }

        [Fact]
        public async Task RemoveDirectoryAsync_WithUnknownId_ThrowsVaultItemNotFoundException( )
        {
            Func<Task> act = async ( ) => await Vault!.RemoveDirectoryAsync( "NonExistentDirId", cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }
        #endregion
    }
}
