using Avalonia.Media;
using FluentIcons.Common;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Icon and tint for each <see cref="FileKind"/>. Tints are mid-tones chosen to read
    /// on both the dark and light themes.
    /// </summary>
    public static class FileKindVisuals
    {
        private static readonly IBrush _folder   = new SolidColorBrush( Color.Parse( "#E0A93B" ) );
        private static readonly IBrush _image    = new SolidColorBrush( Color.Parse( "#3F9BDB" ) );
        private static readonly IBrush _video    = new SolidColorBrush( Color.Parse( "#A06BE0" ) );
        private static readonly IBrush _audio    = new SolidColorBrush( Color.Parse( "#E0648A" ) );
        private static readonly IBrush _pdf      = new SolidColorBrush( Color.Parse( "#E0574A" ) );
        private static readonly IBrush _document = new SolidColorBrush( Color.Parse( "#6F8FB5" ) );
        private static readonly IBrush _code     = new SolidColorBrush( Color.Parse( "#3FAE7C" ) );
        private static readonly IBrush _archive  = new SolidColorBrush( Color.Parse( "#B98A4E" ) );
        private static readonly IBrush _other    = new SolidColorBrush( Color.Parse( "#8C8C96" ) );

        public static Symbol GetIcon( FileKind kind ) => kind switch
        {
            FileKind.Folder   => Symbol.Folder,
            FileKind.Image    => Symbol.Image,
            FileKind.Video    => Symbol.Video,
            FileKind.Audio    => Symbol.MusicNote2,
            FileKind.Pdf      => Symbol.DocumentPdf,
            FileKind.Document => Symbol.DocumentText,
            FileKind.Code     => Symbol.Code,
            FileKind.Archive  => Symbol.FolderZip,
            _                 => Symbol.Document
        };

        public static IBrush GetBrush( FileKind kind ) => kind switch
        {
            FileKind.Folder   => _folder,
            FileKind.Image    => _image,
            FileKind.Video    => _video,
            FileKind.Audio    => _audio,
            FileKind.Pdf      => _pdf,
            FileKind.Document => _document,
            FileKind.Code     => _code,
            FileKind.Archive  => _archive,
            _                 => _other
        };
    }
}
