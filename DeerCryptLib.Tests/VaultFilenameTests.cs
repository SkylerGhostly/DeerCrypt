using DeerCryptLib.Tests.Helpers;
using DeerCryptLib.Vault;
using FluentAssertions;

namespace DeerCryptLib.Tests
{
    public class VaultFilenameTests : VaultTestFixture
    {
        [Fact]
        public async Task AddAsync_FileWithSpacesInName_PreservesName( )
        {
            string fileId = await Vault!.AddAsync( CreateTempFile( name: "File With Spaces", extension: ".bin" ), Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultItem? item = await Vault!.GetEntryAsync( fileId, TestContext.Current.CancellationToken );
            item.Should( ).NotBeNull( );
            item.Name.Should( ).Be( "File With Spaces.bin" );
        }

        [Fact]
        public async Task AddAsync_FileWithUnicodeInName_PreservesName( )
        {
            string fileId = await Vault!.AddAsync( CreateTempFile( name: "ファイル", extension: ".bin" ), Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultItem? item = await Vault!.GetEntryAsync( fileId, TestContext.Current.CancellationToken );
            item.Should( ).NotBeNull( );
            item.Name.Should( ).Be( "ファイル.bin" );
        }

        [Fact]
        // Apostrophes, parentheses, brackets - these can break SQL if unparameterized
        public async Task AddAsync_FileWithSpecialCharacters_PreservesName( )
        {
            string fileId = await Vault!.AddAsync( CreateTempFile( name: "`Special` $ 'Chars' % (Test) [Example]", extension: ".bin" ), Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultItem? item = await Vault!.GetEntryAsync( fileId, TestContext.Current.CancellationToken );
            item.Should( ).NotBeNull( );
            item.Name.Should( ).Be( "`Special` $ 'Chars' % (Test) [Example].bin" );
        }

        [Fact]
        // 200+ character filename - no Windows path limit issues since
        // names are stored as TEXT, not used as filesystem paths
        public async Task AddAsync_VeryLongFilename_PreservesName( )
        {
            string name = new( 'A', 250 );
            string fileId = await Vault!.AddAsync( CreateTempFile( name: name, extension: ".bin" ), Vault.RootDirectoryId, cancellationToken: TestContext.Current.CancellationToken );
            VaultItem? item = await Vault!.GetEntryAsync( fileId, TestContext.Current.CancellationToken );
            item.Should( ).NotBeNull( );
            item.Name.Should( ).Be( name + ".bin" );
        }
    }
}
