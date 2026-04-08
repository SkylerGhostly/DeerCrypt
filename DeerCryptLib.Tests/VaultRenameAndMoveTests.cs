using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;

namespace DeerCryptLib.Tests
{
    public class VaultRenameAndMoveTests : VaultTestFixture
    {
        #region Rename Tests
        [Fact]
        public async Task RenameAsync_File_UpdatesNameInListing( )
        {
            string fileId = await Vault!.AddAsync( CreateTempFile( ), Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.RenameAsync( fileId, "RenamedFile.txt", cancellationToken: TestContext.Current.CancellationToken );
            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Files.Should().ContainSingle( f => f.Id == fileId && f.Name == "RenamedFile.txt" );
        }

        [Fact]
        public async Task RenameAsync_Directory_UpdatesNameInListing( )
        {
            string dirId = await Vault!.CreateDirectoryAsync( "DirToRename", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.RenameAsync( dirId, "RenamedDir", cancellationToken: TestContext.Current.CancellationToken );
            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Directories.Should().ContainSingle( d => d.Id == dirId && d.Name == "RenamedDir" );
        }

        [Fact]
        public async Task RenameAsync_WithEmptyName_ThrowsVaultOperationException( )
        {
            string fileId = await Vault!.AddAsync( CreateTempFile( ), Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            Func<Task> act = async ( ) => await Vault.RenameAsync( fileId, string.Empty, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultOperationException>();
        }

        [Fact]
        public async Task RenameAsync_WithUnknownId_ThrowsVaultItemNotFoundException( )
        {
            Func<Task> act = async ( ) => await Vault!.RenameAsync( "NonExistentId", "NewName", cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }

        [Fact]
        // File should still be in same directory after rename
        public async Task RenameAsync_DoesNotChangeLocation( )
        {
            string dirId = await Vault!.CreateDirectoryAsync( "Dir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string fileId = await Vault.AddAsync( CreateTempFile( ), dirId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.RenameAsync( fileId, "RenamedFile.txt", cancellationToken: TestContext.Current.CancellationToken );
            VaultListing listing = await Vault.ListAsync( dirId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Files.Should().ContainSingle( f => f.Id == fileId && f.Name == "RenamedFile.txt" );
        }
        #endregion

        #region Move Tests
        [Fact]
        public async Task MoveAsync_File_AppearsInTargetDirectory( )
        {
            string sourceDirId = await Vault!.CreateDirectoryAsync( "SourceDir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string fileId = await Vault.AddAsync( CreateTempFile( ), sourceDirId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.MoveAsync( fileId, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Files.Should().ContainSingle( f => f.Id == fileId );
        }

        [Fact]
        public async Task MoveAsync_File_AbsentFromSourceDirectory( )
        {
            string sourceDirId = await Vault!.CreateDirectoryAsync( "SourceDir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string fileId = await Vault.AddAsync( CreateTempFile( ), sourceDirId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.MoveAsync( fileId, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultListing listing = await Vault.ListAsync( sourceDirId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Files.Should( ).NotContain( f => f.Id == fileId );
        }

        [Fact]
        public async Task MoveAsync_Directory_AppearsInTargetDirectory( )
        {
            string sourceDirId = await Vault!.CreateDirectoryAsync( "SourceDir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string childDirId = await Vault.CreateDirectoryAsync( "ChildDir", sourceDirId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.MoveAsync( childDirId, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Directories.Should().ContainSingle( d => d.Id == childDirId );
        }

        [Fact]
        public async Task MoveAsync_RootDirectory_ThrowsVaultOperationException( )
        {
            Func<Task> act = async ( ) => await Vault!.MoveAsync( Vault.RootDirectoryId, "SomeOtherDirId", cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultOperationException>();
        }

        [Fact]
        public async Task MoveAsync_DirectoryIntoItself_ThrowsVaultOperationException( )
        {
            string dirId = await Vault!.CreateDirectoryAsync( "Dir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            Func<Task> act = async ( ) => await Vault.MoveAsync( dirId, dirId, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultOperationException>();
        }

        [Fact]
        public async Task MoveAsync_DirectoryIntoOwnDescendant_ThrowsVaultOperationException( )
        {
            string parentDirId = await Vault!.CreateDirectoryAsync( "ParentDir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string childDirId = await Vault.CreateDirectoryAsync( "ChildDir", parentDirId, cancellationToken: TestContext.Current.CancellationToken );
            Func<Task> act = async ( ) => await Vault.MoveAsync( parentDirId, childDirId, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultOperationException>();
        }

        [Fact]
        public async Task MoveAsync_WithUnknownTargetDirectory_ThrowsVaultItemNotFoundException( )
        {
            string fileId = await Vault!.AddAsync( CreateTempFile( ), Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            Func<Task> act = async ( ) => await Vault.MoveAsync( fileId, "NonExistentDirId", cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }

        [Fact]
        public async Task MoveAsync_WithUnknownId_ThrowsVaultItemNotFoundException( )
        {
            Func<Task> act = async ( ) => await Vault!.MoveAsync( "NonExistentId", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }

        [Fact]
        public async Task MoveAsync_DoesNotChangeName( )
        {
            string sourceDirId = await Vault!.CreateDirectoryAsync( "SourceDir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string fileId = await Vault.AddAsync( CreateTempFile( ), sourceDirId, cancellationToken: TestContext.Current.CancellationToken );
            VaultListing sourceListing = await Vault.ListAsync( sourceDirId, cancellationToken: TestContext.Current.CancellationToken );
            string originalName = sourceListing.Files.Single( f => f.Id == fileId ).Name;
            await Vault.MoveAsync( fileId, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultListing targetListing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            targetListing.Files.Should().ContainSingle( f => f.Id == fileId && f.Name == originalName );
        }
        #endregion
    }
}
