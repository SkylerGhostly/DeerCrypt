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

        /// <summary>"System", "Dark", or "Light".</summary>
        public string Theme { get; set; } = "System";

        /// <summary>"Comfortable" or "Compact".</summary>
        public string Density { get; set; } = "Comfortable";

        /// <summary>"List" or "Grid".</summary>
        public string ViewMode { get; set; } = "List";

        /// <summary>Width of the sidebar in pixels, remembered between sessions.</summary>
        public double SidebarWidth { get; set; } = 232;
    }
}
