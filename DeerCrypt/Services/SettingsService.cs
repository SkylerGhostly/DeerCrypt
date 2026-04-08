using System;
using System.IO;
using System.Text.Json;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> to
    /// <c>%AppData%\DeerCrypt\settings.json</c>.
    /// </summary>
    public sealed class SettingsService
    {
        private static readonly string _settingsDir =
            Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ), "DeerCrypt" );

        private static readonly string _filePath =
            Path.Combine( _settingsDir, "settings.json" );

        private static readonly JsonSerializerOptions _json =
            new( ) { WriteIndented = true };

        public AppSettings Settings { get; private set; } = new( );

        public void Load( )
        {
            try
            {
                if( !File.Exists( _filePath ) ) return;
                string raw = File.ReadAllText( _filePath );
                Settings = JsonSerializer.Deserialize<AppSettings>( raw ) ?? new( );
            }
            catch
            {
                Settings = new( );
            }
        }

        public void Save( )
        {
            try
            {
                Directory.CreateDirectory( _settingsDir );
                File.WriteAllText( _filePath, JsonSerializer.Serialize( Settings, _json ) );
            }
            catch { }
        }
    }
}
