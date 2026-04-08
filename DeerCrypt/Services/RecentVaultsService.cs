using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeerCrypt.Services
{
    public record RecentVaultEntry( string Path, string Name, DateTime LastOpened );

    public class RecentVaultsService
    {
        private const int MaxEntries = 10;

        private static readonly string SettingsDir =
            System.IO.Path.Combine(
                Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ),
                "DeerCrypt" );

        private static readonly string FilePath =
            System.IO.Path.Combine( SettingsDir, "recent.json" );

        private static readonly JsonSerializerOptions _jsonOptions =
            new( ) { WriteIndented = true };

        private List<RecentVaultEntry> _entries = [ ];

        public IReadOnlyList<RecentVaultEntry> Entries => _entries;

        public void Load( )
        {
            try
            {
                if( !File.Exists( FilePath ) ) return;
                var json = File.ReadAllText( FilePath );
                _entries = JsonSerializer.Deserialize<List<RecentVaultEntry>>( json ) ?? [ ];
            }
            catch
            {
                _entries = [ ];
            }
        }

        public void Add( string vaultPath )
        {
            _entries.RemoveAll( e =>
                string.Equals( e.Path, vaultPath, StringComparison.OrdinalIgnoreCase ) );

            _entries.Insert( 0, new RecentVaultEntry(
                vaultPath,
                System.IO.Path.GetFileNameWithoutExtension( vaultPath ),
                DateTime.Now ) );

            if( _entries.Count > MaxEntries )
                _entries.RemoveRange( MaxEntries, _entries.Count - MaxEntries );

            Save( );
        }

        public void Remove( string vaultPath )
        {
            _entries.RemoveAll( e =>
                string.Equals( e.Path, vaultPath, StringComparison.OrdinalIgnoreCase ) );
            Save( );
        }

        private void Save( )
        {
            try
            {
                Directory.CreateDirectory( SettingsDir );
                File.WriteAllText( FilePath, JsonSerializer.Serialize( _entries, _jsonOptions ) );
            }
            catch { }
        }
    }
}
