using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Services;
using DeerCryptLib.Vault;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Dialogs
{
    public enum TextLoadMode { FullEdit, ReadOnly, StreamPartial }

    public sealed partial class TextEditorViewModel : ObservableObject, IDisposable
    {
        // Constants 
        private const long ReadOnlyThreshold = 10L * 1024 * 1024;   // 10 MB
        private const long StreamThreshold   = 50L * 1024 * 1024;   // 50 MB
        private const int  MaxStreamLines    = 50_000;

        // State 
        private readonly VaultFile _vault;
        private readonly string    _fileId;
        private readonly string    _fileName;

        private bool     _disposed;
        private bool     _isInitialized;
        private Encoding _detectedEncoding   = Encoding.UTF8;
        private string   _detectedLineEnding = "\r\n";

        // Observable 

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( WindowTitle ) )]
        [NotifyCanExecuteChangedFor( nameof( SaveCommand ) )]
        public partial bool IsDirty { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( SaveCommand ) )]
        public partial bool IsReadOnly { get; set; }

        [ObservableProperty]
        public partial bool WordWrap { get; set; }

        [ObservableProperty]
        public partial double FontSize { get; set; } = 13.0;

        [ObservableProperty]
        public partial TextLoadMode LoadMode { get; set; } = TextLoadMode.FullEdit;

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string EncodingText { get; set; } = "UTF-8";

        [ObservableProperty]
        public partial string LineEndingText { get; set; } = "CRLF";

        [ObservableProperty]
        public partial int LineCount { get; set; }

        [ObservableProperty]
        public partial int WordCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsMarkdown ) )]
        public partial string FileExtension { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string MarkdownSource { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool ShowMarkdownPreview { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( SaveCommand ) )]
        public partial bool IsSaving { get; set; }

        // Caret position - updated by code-behind via UpdatePosition()
        [ObservableProperty]
        public partial int CaretLine { get; set; } = 1;

        [ObservableProperty]
        public partial int CaretColumn { get; set; } = 1;

        // Document - not ObservableProperty; replaced once in LoadAsync 
        private TextDocument _document = new();

        public TextDocument Document
        {
            get => _document;
            private set
            {
                if( ReferenceEquals( _document, value ) ) return;
                // Unsubscribe old
                _document.TextChanged -= OnDocumentTextChanged;
                _document = value;
                // Subscribe new
                _document.TextChanged += OnDocumentTextChanged;
                OnPropertyChanged( );
            }
        }

        // Derived 

        public bool IsMarkdown => FileExtension is ".md" or ".markdown";

        public string WindowTitle => IsDirty ? $"\u2022 {_fileName}" : _fileName;

        public string LanguageLabel => FileExtension switch
        {
            ".cs"                          => "C#",
            ".js"                          => "JavaScript",
            ".ts"                          => "TypeScript",
            ".html" or ".htm"              => "HTML",
            ".xml"                         => "XML",
            ".json"                        => "JSON",
            ".css"                         => "CSS",
            ".py"                          => "Python",
            ".java"                        => "Java",
            ".cpp" or ".cc" or ".cxx"      => "C++",
            ".c"                           => "C",
            ".h"                           => "C/C++",
            ".sql"                         => "SQL",
            ".ps1"                         => "PowerShell",
            ".vb"                          => "VB.NET",
            ".fs"                          => "F#",
            ".php"                         => "PHP",
            ".rs"                          => "Rust",
            ".go"                          => "Go",
            ".yaml" or ".yml"              => "YAML",
            ".toml"                        => "TOML",
            ".sh" or ".bash" or ".zsh"     => "Shell",
            ".lua"                         => "Lua",
            ".rb"                          => "Ruby",
            ".ini" or ".cfg" or ".conf"    => "INI",
            ".md" or ".markdown"           => "Markdown",
            "" or null                     => "Plain Text",
            var x                          => x.TrimStart( '.' ).ToUpperInvariant( )
        };

        // Events 

        /// <summary>Raised to ask code-behind to open Go-to-Line UI.</summary>
        public event Action? GoToLineRequested;

        // Construction 

        public TextEditorViewModel( VaultFile vault, string fileId, string fileName )
        {
            _vault    = vault;
            _fileId   = fileId;
            _fileName = fileName;

            FileExtension = Path.GetExtension( fileName ).ToLowerInvariant( );

            // Auto word-wrap: ON for prose/markdown, OFF for code and logs
            WordWrap = FileExtension is ".txt" or ".md" or ".markdown" or ".csv";

            ShowMarkdownPreview = IsMarkdown;
        }

        // Loading 

        /// <summary>
        /// Loads the file content from the vault.  Must be called on the UI thread
        /// (or any SynchronizationContext that can freely access Avalonia collections)
        /// because TextDocument is created here.
        /// </summary>
        public async Task LoadAsync( CancellationToken ct = default )
        {
            StatusMessage = "Loading\u2026";

            try
            {
                var stream = await _vault.OpenReadStreamAsync( _fileId, ct );
                long size   = stream.Length;

                if( size > StreamThreshold )
                {
                    // Streaming mode - read only the first MaxStreamLines lines
                    LoadMode   = TextLoadMode.StreamPartial;
                    IsReadOnly = true;

                    var sb = new StringBuilder( );
                    using var reader = new StreamReader(
                        stream, detectEncodingFromByteOrderMarks: true, leaveOpen: false );
                    _detectedEncoding = reader.CurrentEncoding;

                    int lines = 0;
                    string? line;
                    while( lines < MaxStreamLines && (line = await reader.ReadLineAsync( ct )) != null )
                    {
                        sb.AppendLine( line );
                        lines++;
                    }

                    SetDocument( sb.ToString( ) );
                    StatusMessage = $"[Showing first {lines:N0} lines \u2014 read-only, file is {FormatSize( size )}]";
                }
                else
                {
                    // Buffer entire file
                    byte[] data;
                    using( var ms = new MemoryStream( (int)size ) )
                    {
                        await stream.CopyToAsync( ms, ct );
                        data = ms.ToArray( );
                    }
                    stream.Dispose( );

                    _detectedEncoding = TextEncodingDetector.Detect( data );
                    string text = _detectedEncoding.GetString( data );

                    // Detect dominant line ending
                    if( text.Contains( "\r\n" ) )       _detectedLineEnding = "\r\n";
                    else if( text.Contains( '\n' ) )    _detectedLineEnding = "\n";
                    else                                _detectedLineEnding = "\r\n";

                    if( size > ReadOnlyThreshold )
                    {
                        LoadMode   = TextLoadMode.ReadOnly;
                        IsReadOnly = true;
                        StatusMessage = $"[Read-only \u2014 file is {FormatSize( size )}]";
                    }
                    else
                    {
                        LoadMode   = TextLoadMode.FullEdit;
                        IsReadOnly = false;
                        StatusMessage = string.Empty;
                    }

                    SetDocument( text );
                }

                RefreshEncodingText( );
                LineCount = Document.LineCount;

                if( IsMarkdown )
                    MarkdownSource = Document.Text;

                _isInitialized = true;
            }
            catch( OperationCanceledException ) { }
            catch( Exception ex )
            {
                StatusMessage = $"Error loading file: {ex.Message}";
            }
        }

        private void SetDocument( string text )
        {
            Document = new TextDocument( text );
        }

        private void OnDocumentTextChanged( object? sender, EventArgs e )
        {
            if( !_isInitialized ) return;

            IsDirty   = true;
            LineCount = Document.LineCount;

            if( IsMarkdown )
            {
                MarkdownSource = Document.Text;
                UpdateWordCount( Document.Text );
            }
        }

        // Commands 

        [RelayCommand( CanExecute = nameof( CanSave ) )]
        private async Task Save( CancellationToken ct )
        {
            if( IsReadOnly || IsSaving ) return;

            IsSaving = true;
            try
            {
                string text = Document.Text;

                // Normalise line endings back to what we detected
                if( _detectedLineEnding == "\r\n" )
                    text = text.Replace( "\r\n", "\n" ).Replace( "\n", "\r\n" );
                else
                    text = text.Replace( "\r\n", "\n" );

                byte[] data = _detectedEncoding.GetBytes( text );

                await using var ms = new MemoryStream( data );
                await _vault.UpdateAsync( _fileId, ms, new Progress<double>( _ => { } ), ct );

                IsDirty = false;
                StatusMessage = $"Saved at {DateTime.Now:HH:mm:ss}";
            }
            catch( OperationCanceledException ) { }
            catch( Exception ex )
            {
                StatusMessage = $"Save failed: {ex.Message}";
            }
            finally
            {
                IsSaving = false;
            }
        }

        private bool CanSave => IsDirty && !IsReadOnly && !IsSaving;

        [RelayCommand]
        private void GoToLine( ) => GoToLineRequested?.Invoke( );

        [RelayCommand]
        private void IncreaseFontSize( ) => FontSize = Math.Min( FontSize + 1, 48 );

        [RelayCommand]
        private void DecreaseFontSize( ) => FontSize = Math.Max( FontSize - 1, 8 );

        [RelayCommand]
        private void ToggleWordWrap( ) => WordWrap = !WordWrap;

        [RelayCommand]
        private void ToggleMarkdownPreview( )
        {
            if( !IsMarkdown ) return;
            ShowMarkdownPreview = !ShowMarkdownPreview;
            // Markdown preview is always prose - enforce word wrap while preview is open.
            if( ShowMarkdownPreview ) WordWrap = true;
        }

        // Helpers 

        public void UpdatePosition( int line, int column )
        {
            CaretLine   = line;
            CaretColumn = column;
        }

        private void RefreshEncodingText( )
        {
            bool hasBom = _detectedEncoding.GetPreamble( ).Length > 0;

            EncodingText = _detectedEncoding.WebName.ToUpperInvariant( ) switch
            {
                "UTF-8"        => hasBom ? "UTF-8 BOM" : "UTF-8",
                "UTF-16"       => "UTF-16 LE",
                "UTF-16BE"     => "UTF-16 BE",
                "UTF-32"       => "UTF-32",
                "WINDOWS-1252" => "Windows-1252",
                "ISO-8859-1"   => "Latin-1",
                var x          => x
            };

            LineEndingText = _detectedLineEnding switch
            {
                "\r\n" => "CRLF",
                "\n"   => "LF",
                _      => "CR"
            };
        }

        private void UpdateWordCount( string text )
        {
            int count = 0;
            bool inWord = false;
            foreach( char c in text )
            {
                if( char.IsWhiteSpace( c ) )  { inWord = false; }
                else if( !inWord )             { inWord = true; count++; }
            }
            WordCount = count;
        }

        private static string FormatSize( long bytes )
        {
            if( bytes >= 1_073_741_824 ) return $"{bytes / 1_073_741_824.0:F1} GB";
            if( bytes >= 1_048_576 )     return $"{bytes / 1_048_576.0:F1} MB";
            if( bytes >= 1024 )          return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        // IDisposable 

        public void Dispose( )
        {
            if( _disposed ) return;
            _disposed = true;

            _document.TextChanged -= OnDocumentTextChanged;
        }
    }
}
