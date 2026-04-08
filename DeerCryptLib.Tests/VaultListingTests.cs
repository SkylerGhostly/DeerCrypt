using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;


namespace DeerCryptLib.Tests
{
    public class VaultListingTests : VaultTestFixture
    {
        #region ListAsync Tests
        [Fact]
        public async Task ListAsync_EmptyVault_ReturnsEmptyListing( )
        {
            VaultListing listing = await Vault!.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Directories.Should().BeEmpty();
            listing.Files.Should().BeEmpty();
        }

        [Fact]
        public async Task ListAsync_AfterAddingFile_FileAppearsInListing( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );

            string sourcePath = CreateTempFileWithContent( originalContent );
            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );

            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Files.Should().ContainSingle( f => f.Id == fileId );
        }

        [Fact]
        public async Task ListAsync_AfterAddingDirectory_DirectoryAppearsInListing( )
        {
            string directoryName = "TestDirectory";
            string directoryId = await Vault!.CreateDirectoryAsync( directoryName, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultListing listing = await Vault.ListAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            listing.Directories.Should().ContainSingle( d => d.Id == directoryId && d.Name == directoryName );
        }

        // [Fact]
        // Add files out of order, verify listing returns them sorted
        // public async Task ListAsync_OnlyShowsImmediateChildren( )

        [Fact]
        public async Task ListAsync_WithUnknownDirectoryId_ThrowsVaultItemNotFoundException( )
        {
            Func<Task> act = async () => await Vault!.ListAsync( "nonexistent-directory-id", cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }
        #endregion

        #region ListRecursiveAsync Tests
        [Fact]
        public async Task ListRecursiveAsync_ReturnsAllFilesAcrossDirectories( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );
            string sourcePath1 = CreateTempFileWithContent( originalContent );
            string sourcePath2 = CreateTempFileWithContent( originalContent );
            string dirId = await Vault!.CreateDirectoryAsync( "TestDir", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string fileId1 = await Vault!.AddAsync( sourcePath1, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string fileId2 = await Vault!.AddAsync( sourcePath2, dirId, cancellationToken: TestContext.Current.CancellationToken );
            IReadOnlyList<VaultEntry> allFiles = await Vault.ListRecursiveAsync( cancellationToken: TestContext.Current.CancellationToken );
            allFiles.Should().ContainSingle( f => f.Id == fileId1 );
            allFiles.Should().ContainSingle( f => f.Id == fileId2 );
        }

        [Fact]
        public async Task ListRecursiveAsync_WithSearchTerm_ReturnsMatchingFiles( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );
            string sourcePath1 = CreateTempFileWithContent( originalContent, "SomeFileName123" );
            string sourcePath2 = CreateTempFileWithContent( originalContent, "TestFileName456" );

            string fileId1 = await Vault!.AddAsync( sourcePath1, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string fileId2 = await Vault!.AddAsync( sourcePath2, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );

            IReadOnlyList<VaultItem> searchResults1 = await Vault.ListRecursiveAsync( searchTerm: "Some", cancellationToken: TestContext.Current.CancellationToken );
            searchResults1.Should().ContainSingle( f => f.Id == fileId1 );
            IReadOnlyList<VaultItem> searchResults2 = await Vault.ListRecursiveAsync( searchTerm: "Test", cancellationToken: TestContext.Current.CancellationToken );
            searchResults2.Should().ContainSingle( f => f.Id == fileId2 );
        }

        [Fact]
        public async Task ListRecursiveAsync_WithSearchTerm_IsCaseInsensitive( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );
            string sourcePath = CreateTempFileWithContent( originalContent, "SomeFileName123" );

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );

            IReadOnlyList<VaultItem> searchResults1 = await Vault.ListRecursiveAsync( searchTerm: "some", cancellationToken: TestContext.Current.CancellationToken );
            searchResults1.Should( ).ContainSingle( f => f.Id == fileId );
        }

        [Fact]
        public async Task ListRecursiveAsync_WithSearchTerm_NoMatches_ReturnsEmpty( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );
            string sourcePath = CreateTempFileWithContent( originalContent, "SomeFileName123" );
            await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            IReadOnlyList<VaultItem> searchResults = await Vault.ListRecursiveAsync( searchTerm: "NonExistentTerm", cancellationToken: TestContext.Current.CancellationToken );
            searchResults.Should().BeEmpty();
        }

        [Fact]
        public async Task ListRecursiveAsync_SearchEscapesWildcards( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );
            string sourcePath = CreateTempFileWithContent( originalContent, "50%_off" );
            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            IReadOnlyList<VaultItem> searchResults = await Vault.ListRecursiveAsync( searchTerm: "50%", cancellationToken: TestContext.Current.CancellationToken );
            searchResults.Should().ContainSingle( f => f.Id == fileId );
        }
        #endregion

        #region GetEntryAsync Tests
        [Fact]
        public async Task GetEntryAsync_FileId_ReturnsVaultEntry( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );
            string sourcePath = CreateTempFileWithContent( originalContent );
            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );

            var result = await Vault.GetEntryAsync( fileId, cancellationToken: TestContext.Current.CancellationToken );
            result.Should( ).NotBeNull( ).And.BeOfType<VaultEntry>( );
        }

        [Fact]
        public async Task GetEntryAsync_DirectoryId_ReturnsVaultDirectoryEntry( )
        {
            string directoryName = "TestDirectory";
            string directoryId = await Vault!.CreateDirectoryAsync( directoryName, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            var result = await Vault.GetEntryAsync( directoryId, cancellationToken: TestContext.Current.CancellationToken );
            result.Should( ).NotBeNull( ).And.BeOfType<VaultDirectoryEntry>( );
        }

        [Fact]
        public async Task GetEntryAsync_UnknownId_ReturnsNull( )
        {
            var result = await Vault!.GetEntryAsync( "nonexistent-id", cancellationToken: TestContext.Current.CancellationToken );
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetEntryAsync_RootDirectoryId_ReturnsVaultDirectoryEntry( )
        {
            var result = await Vault!.GetEntryAsync( Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            result.Should( ).NotBeNull( ).And.BeOfType<VaultDirectoryEntry>( );
        }

        [Fact]
        public async Task GetVaultInfoAsync_CorrectFileCounts( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            for( int i = 0; i < 10; i++ )
            {
                Random.Shared.NextBytes( originalContent );
                string file = CreateTempFileWithContent( originalContent );
                await Vault!.AddAsync( file, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            }

            VaultInfo info = await Vault!.GetVaultInfoAsync( cancellationToken: TestContext.Current.CancellationToken );
            info.TotalFiles.Should().Be( 10 );
        }

        [Fact]
        public async Task GetVaultInfoAsync_CorrectTotalSize( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            for( int i = 0; i < 10; i++ )
            {
                Random.Shared.NextBytes( originalContent );
                string file = CreateTempFileWithContent( originalContent );
                await Vault!.AddAsync( file, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            }

            VaultInfo info = await Vault!.GetVaultInfoAsync( cancellationToken: TestContext.Current.CancellationToken );
            info.TotalOriginalSize.Should().Be( 10 * 1024 * 1024 );
        }

        [Fact]
        public async Task GetVaultInfoAsync_EmptyVault_ReturnsZeros( )
        {
            VaultInfo info = await Vault!.GetVaultInfoAsync( cancellationToken: TestContext.Current.CancellationToken );
            info.TotalFiles.Should().Be( 0 );
            info.TotalDirectories.Should().Be( 0 );
            info.TotalOriginalSize.Should().Be( 0 );
            info.VaultFileSize.Should( ).BeGreaterThanOrEqualTo( 0 );
        }

        [Fact]
        public async Task GetVaultInfoAsync_CreatedAtIsReasonable( )
        {
            VaultInfo info = await Vault!.GetVaultInfoAsync( cancellationToken: TestContext.Current.CancellationToken );
            info.CreatedAt.Should().BeCloseTo( DateTime.UtcNow, TimeSpan.FromMinutes( 1 ) );
        }
        #endregion
    }
}
