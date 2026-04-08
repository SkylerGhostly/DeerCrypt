using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;

namespace DeerCryptLib.Tests
{
    public class VaultFileOperationTests : VaultTestFixture
    {
        #region Add Tests
        [Fact]
        public async Task AddAndExtract_ProducesIdenticalBytes( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );

            string sourcePath = CreateTempFileWithContent(originalContent);
            string outputPath = Path.Combine(TempDir, "extracted.bin");

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.ExtractAsync( fileId, outputPath, cancellationToken: TestContext.Current.CancellationToken );

            byte[] extractedContent = await File.ReadAllBytesAsync( outputPath, TestContext.Current.CancellationToken );
            extractedContent.Should( ).Equal( originalContent );
        }

        [Fact]
        public async Task AddAsync_SameFileTwice_CreatesTwoIndependentEntries( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );

            string sourcePath = CreateTempFileWithContent(originalContent);
            string fileId1 = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            string fileId2 = await Vault.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );

            fileId1.Should( ).NotBe( fileId2 );
        }

        [Fact]
        public async Task AddAndExtract_EmptyFile_ProducesIdenticalBytes( )
        {
            string sourcePath = CreateTempFileWithContent([], ".txt");
            string outputPath = Path.Combine(TempDir, "extracted.txt");

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.ExtractAsync( fileId, outputPath, cancellationToken: TestContext.Current.CancellationToken );

            File.ReadAllBytes( outputPath ).Should( ).BeEmpty( );
        }

        [Fact]
        public async Task AddAndExtract_FileExactlyOneChunkSize_ProducesIdenticalBytes( )
        {
            int chunkSize = 16 * 1024 * 1024; // 16 MB
            byte[] originalContent = new byte[chunkSize];
            Random.Shared.NextBytes( originalContent );

            string sourcePath = CreateTempFileWithContent(originalContent);
            string outputPath = Path.Combine(TempDir, "extracted.bin");

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.ExtractAsync( fileId, outputPath, cancellationToken: TestContext.Current.CancellationToken );

            byte[] extractedContent = await File.ReadAllBytesAsync( outputPath, TestContext.Current.CancellationToken );
            extractedContent.Should( ).Equal( originalContent );
        }

        [Fact]
        public async Task AddAndExtract_FileJustOverOneChunkSize_ProducesIdenticalBytes( )
        {
            int chunkSize = 16 * 1024 * 1024; // 16 MB
            byte[] originalContent = new byte[chunkSize + 1];
            Random.Shared.NextBytes( originalContent );
            
            string sourcePath = CreateTempFileWithContent(originalContent);
            string outputPath = Path.Combine(TempDir, "extracted.bin");

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.ExtractAsync( fileId, outputPath, cancellationToken: TestContext.Current.CancellationToken );

            byte[] extractedContent = await File.ReadAllBytesAsync( outputPath, TestContext.Current.CancellationToken );
            extractedContent.Should( ).Equal( originalContent );
        }

        [Fact]
        public async Task AddAndExtract_LargeFile_ProducesIdenticalBytes( )
        {
            int chunkSize = 1024 * 1024 * 1024; // 1GB
            byte[] originalContent = new byte[chunkSize];
            Random.Shared.NextBytes( originalContent );

            string sourcePath = CreateTempFileWithContent(originalContent);
            string outputPath = Path.Combine(TempDir, "extracted.bin");

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.ExtractAsync( fileId, outputPath, cancellationToken: TestContext.Current.CancellationToken );

            byte[] extractedContent = await File.ReadAllBytesAsync( outputPath, TestContext.Current.CancellationToken );
            extractedContent.Should( ).Equal( originalContent );
        }

        [Fact]
        public async Task AddAsync_FileNamePreservedInListing( )
        {
            int chunkSize = 16 * 1024 * 1024; // 16 MB
            byte[] originalContent = new byte[chunkSize];
            Random.Shared.NextBytes( originalContent );

            string sourcePath = CreateTempFileWithContent(originalContent);

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );

            VaultItem? item = await Vault.GetEntryAsync( fileId, TestContext.Current.CancellationToken );

            item.Should().NotBeNull();
            item!.Name.Should().Be( Path.GetFileName( sourcePath ) );
        }

        [Fact]
        public async Task AddAsync_WithNonExistentSourceFile_ThrowsVaultItemNotFoundException( )
        {
            string sourcePath = Path.Combine(TempDir, "nonexistent.bin");

            Func<Task> act = async () => await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }

        [Fact]
        public async Task AddAsync_WithNonExistentDirectory_ThrowsVaultItemNotFoundException( )
        {
            string sourcePath = CreateTempFileWithContent( new byte[1024] );
            string nonExistentDirId = Guid.NewGuid().ToString();
            Func<Task> act = async () => await Vault!.AddAsync( sourcePath, nonExistentDirId, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }
        #endregion

        #region Extract Tests
        [Fact]
        public async Task ExtractAsync_WithUnknownId_ThrowsVaultItemNotFoundException( )
        {
            string nonExistentId = Guid.NewGuid().ToString();
            string outputPath = Path.Combine(TempDir, "extracted.bin");
            Func<Task> act = async () => await Vault!.ExtractAsync( nonExistentId, outputPath, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }

        [Fact]
        public async Task ExtractAsync_CreatesOutputDirectoryIfNotExists( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );

            string sourcePath = CreateTempFileWithContent(originalContent);
            string outputDir = Path.Combine(TempDir, "nonexistent_dir");
            string outputPath = Path.Combine(outputDir, "extracted.bin");

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.ExtractAsync( fileId, outputPath, cancellationToken: TestContext.Current.CancellationToken );

            File.Exists( outputPath ).Should( ).BeTrue( );
        }

        [Fact]
        public async Task ExtractDirectoryAsync_ReconstructsFolderStructure( )
        {
            // Arrange - create a directory tree with files at different levels
            string dirId    = await Vault!.CreateDirectoryAsync("MyFolder", Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken);
            string subDirId = await Vault.CreateDirectoryAsync("SubFolder", dirId, cancellationToken: TestContext.Current.CancellationToken );

            byte[] file1Content = new byte[512]; Random.Shared.NextBytes( file1Content );
            byte[] file2Content = new byte[512]; Random.Shared.NextBytes( file2Content );
            byte[] file3Content = new byte[512]; Random.Shared.NextBytes( file3Content );

            await Vault.AddAsync( CreateTempFileWithContent( file1Content, extension: ".bin" ), dirId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.AddAsync( CreateTempFileWithContent( file2Content, extension: ".bin" ), subDirId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.AddAsync( CreateTempFileWithContent( file3Content, extension: ".bin" ), subDirId, cancellationToken: TestContext.Current.CancellationToken );

            // Act
            string outputRoot = Path.Combine(TempDir, "extracted");
            await Vault.ExtractDirectoryAsync( dirId, outputRoot, cancellationToken: TestContext.Current.CancellationToken );

            // Assert - folder structure exists on disk
            Directory.Exists( Path.Combine( outputRoot, "MyFolder" ) ).Should( ).BeTrue( );
            Directory.Exists( Path.Combine( outputRoot, "MyFolder", "SubFolder" ) ).Should( ).BeTrue( );

            // Assert - correct number of files at each level
            Directory.GetFiles( Path.Combine( outputRoot, "MyFolder" ) )
                     .Should( ).HaveCount( 1 );
            Directory.GetFiles( Path.Combine( outputRoot, "MyFolder", "SubFolder" ) )
                     .Should( ).HaveCount( 2 );
        }
        #endregion

        #region Remove Tests
        /// <summary>
        /// Verifies that a file is no longer present in the listing after being removed asynchronously.
        /// </summary>
        /// <remarks>This test ensures that the remove operation updates the file listing as expected. Use
        /// this test to validate correct behavior when files are deleted.</remarks>
        /// <returns>A task that represents the asynchronous test operation.</returns>
        [Fact]
        public async Task RemoveAsync_FileNoLongerInListing( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );

            string sourcePath = CreateTempFileWithContent(originalContent);

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            
            VaultEntry? entry = await Vault.GetEntryAsync( fileId, TestContext.Current.CancellationToken ) as VaultEntry;

            entry.Should().NotBeNull();

            await Vault.RemoveAsync( fileId, cancellationToken: TestContext.Current.CancellationToken );

            entry = await Vault.GetEntryAsync( fileId, TestContext.Current.CancellationToken ) as VaultEntry;

            entry.Should( ).BeNull();
        }

        [Fact]
        public async Task RemoveAsync_ExtractAfterRemove_ThrowsVaultItemNotFoundException( )
        {
            byte[] originalContent = new byte[1024 * 1024]; // 1 MB
            Random.Shared.NextBytes( originalContent );

            string sourcePath = CreateTempFileWithContent(originalContent);
            string outputPath = Path.Combine(TempDir, "extracted.bin");

            string fileId = await Vault!.AddAsync( sourcePath, Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            await Vault.RemoveAsync( fileId, cancellationToken: TestContext.Current.CancellationToken );
            Func<Task> act = async () => await Vault.ExtractAsync( fileId, outputPath, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }

        [Fact]
        public async Task RemoveAsync_WithUnknownId_ThrowsVaultItemNotFoundException( )
        {
            string nonExistentId = Guid.NewGuid().ToString();
            Func<Task> act = async () => await Vault!.RemoveAsync( nonExistentId, cancellationToken: TestContext.Current.CancellationToken );
            await act.Should().ThrowAsync<VaultItemNotFoundException>();
        }
        #endregion

        #region Move Tests
        [Fact]
        public async Task MoveAsync_DirectoryIntoItsOwnDescendant_ThrowsVaultOperationException( )
        {
            string parentId = await Vault!.CreateDirectoryAsync( "Parent", Vault.RootDirectoryId, TestContext.Current.CancellationToken );
            string childId  = await Vault.CreateDirectoryAsync( "Child", parentId, TestContext.Current.CancellationToken );

            Func<Task> act = async () => await Vault.MoveAsync( parentId, childId, TestContext.Current.CancellationToken );
            await act.Should( ).ThrowAsync<VaultOperationException>( );
        }
        #endregion

        #region Update Tests
        [Fact]
        public async Task UpdateAsync_ReplacesFileContent( )
        {
            // Arrange - add original file
            byte[] originalContent = new byte[1024];
            Random.Shared.NextBytes( originalContent );
            string sourcePath = CreateTempFileWithContent(originalContent);

            string fileId = await Vault!.AddAsync(
                sourcePath,
                Vault.RootDirectoryId,
                cancellationToken: TestContext.Current.CancellationToken);

            // Act - update with new content
            byte[] updatedContent = new byte[2048];
            Random.Shared.NextBytes( updatedContent );
            string updatedPath = CreateTempFileWithContent(updatedContent);

            await Vault.UpdateAsync(
                fileId,
                updatedPath,
                cancellationToken: TestContext.Current.CancellationToken );

            // Assert - extracting produces the updated content
            string outputPath = Path.Combine(TempDir, "extracted.bin");
            await Vault.ExtractAsync(
                fileId,
                outputPath,
                cancellationToken: TestContext.Current.CancellationToken );

            byte[] extracted = await File.ReadAllBytesAsync( outputPath, TestContext.Current.CancellationToken );
            extracted.Should( ).Equal( updatedContent );
        }

        [Fact]
        public async Task UpdateAsync_PreservesFileId( )
        {
            // The file GUID must not change after an update
            byte[] content = new byte[512];
            string path    = CreateTempFileWithContent(content);

            string fileId = await Vault!.AddAsync(
                path,
                Vault.RootDirectoryId,
                cancellationToken: TestContext.Current.CancellationToken);

            byte[] newContent = new byte[512];
            Random.Shared.NextBytes( newContent );
            string newPath = CreateTempFileWithContent(newContent);

            await Vault.UpdateAsync(
                fileId,
                newPath,
                cancellationToken: TestContext.Current.CancellationToken );

            // File should still be findable by the same ID
            VaultItem? entry = await Vault.GetEntryAsync(
                fileId,
                TestContext.Current.CancellationToken);

            entry.Should( ).NotBeNull( );
            entry!.Id.Should( ).Be( fileId );
        }

        [Fact]
        public async Task UpdateAsync_UpdatesMetadata( )
        {
            // original_size and sha256 should reflect the new file after update
            byte[] originalContent = new byte[1024];
            string sourcePath      = CreateTempFileWithContent(originalContent);

            string fileId = await Vault!.AddAsync(
                sourcePath,
                Vault.RootDirectoryId,
                cancellationToken: TestContext.Current.CancellationToken);

            byte[] updatedContent = new byte[4096]; // different size
            Random.Shared.NextBytes( updatedContent );
            string updatedPath = CreateTempFileWithContent(updatedContent);

            await Vault.UpdateAsync(
                fileId,
                updatedPath,
                cancellationToken: TestContext.Current.CancellationToken );

            VaultItem? item = await Vault.GetEntryAsync(
                fileId,
                TestContext.Current.CancellationToken);

            item.Should( ).BeOfType<VaultEntry>( );
            ( (VaultEntry)item! ).OriginalSize.Should( ).Be( 4096 );
        }

        [Fact]
        public async Task UpdateAsync_WithNonExistentFileId_ThrowsVaultItemNotFoundException( )
        {
            string tempPath = CreateTempFile(512);

            Func<Task> act = async () => await Vault!.UpdateAsync(
                Guid.NewGuid().ToString(),
                tempPath,
                cancellationToken: TestContext.Current.CancellationToken);

            await act.Should( ).ThrowAsync<VaultItemNotFoundException>( );
        }

        [Fact]
        public async Task UpdateAsync_WithEmptyFile_Succeeds( )
        {
            // Verify empty file update works the same as empty file add
            byte[] original = new byte[512];
            string path     = CreateTempFileWithContent(original);

            string fileId = await Vault!.AddAsync(
                path,
                Vault.RootDirectoryId,
                cancellationToken: TestContext.Current.CancellationToken);

            string emptyPath = CreateTempFileWithContent([], ".bin");

            await Vault.UpdateAsync(
                fileId,
                emptyPath,
                cancellationToken: TestContext.Current.CancellationToken );

            string outputPath = Path.Combine(TempDir, "extracted.bin");
            await Vault.ExtractAsync(
                fileId,
                outputPath,
                cancellationToken: TestContext.Current.CancellationToken );

            File.ReadAllBytes( outputPath ).Should( ).BeEmpty( );
        }

        [Fact]
        public async Task UpdateAsync_IntegrityCheckPassesAfterUpdate( )
        {
            byte[] content = new byte[1024];
            string path    = CreateTempFileWithContent(content);

            string fileId = await Vault!.AddAsync(
                path,
                Vault.RootDirectoryId,
                cancellationToken: TestContext.Current.CancellationToken);

            byte[] newContent = new byte[2048];
            Random.Shared.NextBytes( newContent );
            string newPath = CreateTempFileWithContent(newContent);

            await Vault.UpdateAsync(
                fileId,
                newPath,
                cancellationToken: TestContext.Current.CancellationToken );

            IReadOnlyList<string> issues = await Vault.CheckIntegrityAsync(
                TestContext.Current.CancellationToken);

            issues.Should( ).BeEmpty( );
        }
        #endregion
    }
}
