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

        /// <summary>
        /// Where the built-in viewers open: "Tabs" (inside the main window) or "Windows" (each in
        /// its own window). Ctrl+click / Ctrl+Enter always opens a new window.
        /// </summary>
        public string OpenViewersIn { get; set; } = "Tabs";

        /// <summary>"System", "Dark", or "Light".</summary>
        public string Theme { get; set; } = "System";

        /// <summary>"Comfortable" or "Compact".</summary>
        public string Density { get; set; } = "Comfortable";

        /// <summary>"List" or "Grid".</summary>
        public string ViewMode { get; set; } = "List";

        /// <summary>Width of the sidebar in pixels, remembered between sessions.</summary>
        public double SidebarWidth { get; set; } = 232;

        /// <summary>Whether the details panel on the right is shown.</summary>
        public bool InspectorOpen { get; set; } = true;

        /// <summary>List view column widths (the Name column takes the remaining space).</summary>
        public double TypeColumnWidth { get; set; } = 150;
        public double SizeColumnWidth { get; set; } = 90;
        public double DateColumnWidth { get; set; } = 170;

        /// <summary>
        /// Thumbnails for vaults that haven't chosen their own: "Off", "Blurred" (until hovered),
        /// or "On". New vaults follow it; a vault from before thumbnails existed asks first,
        /// since turning them on writes a cache into it.
        /// </summary>
        public string DefaultThumbnails { get; set; } = "On";
    }
}
