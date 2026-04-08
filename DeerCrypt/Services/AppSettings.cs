namespace DeerCrypt.Services
{
    /// <summary>
    /// Persisted application settings.
    /// Saved to %AppData%\DeerCrypt\settings.json by <see cref="SettingsService"/>.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// When <c>true</c> (the default), compatible media files open in the built-in
        /// player with on-the-fly decryption. When <c>false</c>, all files are decrypted
        /// to a temp directory and opened in the system's default application.
        /// </summary>
        public bool UseInternalViewer { get; set; } = true;
    }
}
