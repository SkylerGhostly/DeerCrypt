using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Converters;
using DeerCrypt.Services;
using DeerCrypt.ViewModels.Dialogs;
using DeerCrypt.Views.Dialogs;
using DeerCryptLib.Vault;
using FluentIcons.Common;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Viewers
{
    public enum TextLoadMode { FullEdit, ReadOnly, StreamPartial }

    /// <summary>How a Markdown file is shown: source only, side by side, or rendered only.</summary>
    public enum MarkdownMode { Edit, Split, Preview }

    /// <summary>
    /// Text editor tab. The file is decrypted into memory; saving re-encrypts it straight back
    /// into the vault (nothing is written to disk in plain form).
    /// </summary>
    public sealed partial class TextDocumentViewModel : DocumentViewModel
    {
        private const long ReadOnlyThreshold = 10L * 1024 * 1024;   // 10 MB
        private const long StreamThreshold   = 50L * 1024 * 1024;   // 50 MB
        private const int  MaxStreamLines    = 50_000;

        private readonly VaultFile _vault;
        private readonly string    _fileName;
        private bool     _disposed;
        private bool     _isInitialized;
        private Encoding _detectedEncoding   = Encoding.UTF8;
        private string   _detectedLineEnding = "\r\n";

        public TextDocumentViewModel( VaultFile vault, string fileId, string fileName )
        {
            _vault    = vault;
            _fileName = fileName;
            FileId    = fileId;
            Title     = fileName;
            FileExtension = Path.GetExtension( fileName ).ToLowerInvariant( );

            // Wrap prose by default, not code or logs
            WordWrap = FileExtension is ".txt" or ".md" or ".markdown" or ".csv";
            MarkdownMode = IsMarkdown ? MarkdownMode.Split : MarkdownMode.Edit;
        }

        public override Symbol Icon      => FileKindVisuals.GetIcon( FileTypeHelper.GetKind( _fileName ) );
        public override IBrush IconBrush => FileKindVisuals.GetBrush( FileTypeHelper.GetKind( _fileName ) );

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( SaveCommand ) )]
        public partial bool IsReadOnly { get; set; }

        [ObservableProperty] public partial bool WordWrap { get; set; }
        [ObservableProperty] public partial double FontSize { get; set; } = 13.0;
        [ObservableProperty] public partial TextLoadMode LoadMode { get; set; } = TextLoadMode.FullEdit;
        [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
        [ObservableProperty] public partial string EncodingText { get; set; } = "UTF-8";
        [ObservableProperty] public partial string LineEndingText { get; set; } = "CRLF";
        [ObservableProperty] public partial int LineCount { get; set; }
        [ObservableProperty] public partial int WordCount { get; set; }
        [ObservableProperty] public partial int CaretLine { get; set; } = 1;
        [ObservableProperty] public partial int CaretColumn { get; set; } = 1;
        [ObservableProperty] public partial int SelectionLength { get; set; }
        [ObservableProperty] public partial bool IsLoading { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsMarkdown ) )]
        public partial string FileExtension { get; set; } = string.Empty;

        [ObservableProperty] public partial string MarkdownSource { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( ShowEditor ) )]
        [NotifyPropertyChangedFor( nameof( ShowPreview ) )]
        [NotifyPropertyChangedFor( nameof( IsEditMode ) )]
        [NotifyPropertyChangedFor( nameof( IsSplitMode ) )]
        [NotifyPropertyChangedFor( nameof( IsPreviewMode ) )]
        public partial MarkdownMode MarkdownMode { get; set; }

        public bool IsEditMode    => MarkdownMode == MarkdownMode.Edit;
        public bool IsSplitMode   => IsMarkdown && MarkdownMode == MarkdownMode.Split;
        public bool IsPreviewMode => MarkdownMode == MarkdownMode.Preview;

        public bool ShowEditor  => !IsMarkdown || MarkdownMode != MarkdownMode.Preview;
        public bool ShowPreview => IsMarkdown && MarkdownMode != MarkdownMode.Edit;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( SaveCommand ) )]
        public partial bool IsSaving { get; set; }

        public bool HasSelection  => SelectionLength > 0;
        partial void OnSelectionLengthChanged( int value ) => OnPropertyChanged( nameof( HasSelection ) );

        public bool IsMarkdown => FileExtension is ".md" or ".markdown";

        public string LanguageLabel => FileExtension switch
        {
            ".cs" => "C#", ".js" => "JavaScript", ".ts" => "TypeScript", ".html" or ".htm" => "HTML",
            ".xml" => "XML", ".json" => "JSON", ".css" => "CSS", ".py" => "Python", ".java" => "Java",
            ".cpp" or ".cc" or ".cxx" => "C++", ".c" => "C", ".h" => "C/C++", ".sql" => "SQL",
            ".ps1" => "PowerShell", ".vb" => "VB.NET", ".fs" => "F#", ".php" => "PHP", ".rs" => "Rust",
            ".go" => "Go", ".yaml" or ".yml" => "YAML", ".toml" => "TOML", ".sh" or ".bash" or ".zsh" => "Shell",
            ".lua" => "Lua", ".rb" => "Ruby", ".ini" or ".cfg" or ".conf" => "INI",
            ".md" or ".markdown" => "Markdown", ".csv" => "CSV", "" or null => "Plain text",
            var x => x.TrimStart( '.' ).ToUpperInvariant( )
        };

        // Document

        private TextDocument _document = new( );
        public TextDocument Document
        {
            get => _document;
            private set
            {
                if( ReferenceEquals( _document, value ) ) return;
                _document.TextChanged -= OnDocumentTextChanged;
                _document = value;
                _document.TextChanged += OnDocumentTextChanged;
                OnPropertyChanged( );
            }
        }

        /// <summary>Asks the view to show Go to line (it owns the editor and the dialog's owner window).</summary>
        public event Action? GoToLineRequested;
        /// <summary>Asks the view to open the find (or find and replace) panel.</summary>
        public event Action<bool>? FindRequested;

        /// <summary>
        /// The view is about to be removed (pop out, dock, close). The Markdown preview library
        /// crashes if its rendered text loses its window's theme, so the view empties it first.
        /// </summary>
        public event Action? ViewDetaching;

        public override void BeforeMove( ) => ViewDetaching?.Invoke( );

        // Loading

        public async Task LoadAsync( CancellationToken ct = default )
        {
            IsLoading = true;
            StatusMessage = "Decrypting…";
            try
            {
                var stream = await _vault.OpenReadStreamAsync( FileId, ct );
                long size  = stream.Length;

                if( size > StreamThreshold )
                {
                    LoadMode   = TextLoadMode.StreamPartial;
                    IsReadOnly = true;

                    var sb = new StringBuilder( );
                    using var reader = new StreamReader( stream, detectEncodingFromByteOrderMarks: true, leaveOpen: false );
                    _detectedEncoding = reader.CurrentEncoding;
                    int lines = 0;
                    string? line;
                    while( lines < MaxStreamLines && ( line = await reader.ReadLineAsync( ct ) ) != null )
                    {
                        sb.AppendLine( line );
                        lines++;
                    }
                    Document = new TextDocument( sb.ToString( ) );
                    StatusMessage = $"Showing the first {lines:N0} lines, read-only ({FileSizeConverter.Format( size )} file)";
                }
                else
                {
                    byte[] data;
                    using( var ms = new MemoryStream( (int)size ) )
                    {
                        await stream.CopyToAsync( ms, ct );
                        data = ms.ToArray( );
                    }
                    await stream.DisposeAsync( );

                    _detectedEncoding = TextEncodingDetector.Detect( data );
                    string text = _detectedEncoding.GetString( data );

                    _detectedLineEnding = text.Contains( "\r\n" ) ? "\r\n" : text.Contains( '\n' ) ? "\n" : "\r\n";

                    if( size > ReadOnlyThreshold )
                    {
                        LoadMode      = TextLoadMode.ReadOnly;
                        IsReadOnly    = true;
                        StatusMessage = $"Read-only ({FileSizeConverter.Format( size )} file)";
                    }
                    else
                    {
                        LoadMode      = TextLoadMode.FullEdit;
                        IsReadOnly    = false;
                        StatusMessage = string.Empty;
                    }

                    Document = new TextDocument( text );
                }

                RefreshEncodingText( );
                LineCount = Document.LineCount;
                UpdateWordCount( Document.Text );
                if( IsMarkdown ) MarkdownSource = Document.Text;
                _isInitialized = true;
            }
            catch( OperationCanceledException ) { }
            catch( ObjectDisposedException ) { }
            catch( Exception ex )
            {
                StatusMessage = $"Couldn't open the file: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void OnDocumentTextChanged( object? sender, EventArgs e )
        {
            if( !_isInitialized ) return;
            IsDirty   = true;
            LineCount = Document.LineCount;
            if( IsMarkdown ) MarkdownSource = Document.Text;
            if( Document.TextLength < 2_000_000 ) UpdateWordCount( Document.Text );
        }

        protected override void OnDirtyChanged( bool value ) => SaveCommand.NotifyCanExecuteChanged( );

        // Commands

        [RelayCommand( CanExecute = nameof( CanSave ) )]
        private async Task SaveAsync( CancellationToken ct )
        {
            if( IsReadOnly || IsSaving ) return;
            IsSaving = true;
            try
            {
                string text = Document.Text;
                text = _detectedLineEnding == "\r\n"
                    ? text.Replace( "\r\n", "\n" ).Replace( "\n", "\r\n" )
                    : text.Replace( "\r\n", "\n" );

                byte[] data = _detectedEncoding.GetBytes( text );
                await using var ms = new MemoryStream( data );
                await _vault.UpdateAsync( FileId, ms, cancellationToken: ct );

                IsDirty = false;
                StatusMessage = $"Saved to the vault at {DateTime.Now:HH:mm}";
                RaiseVaultChanged( );
            }
            catch( OperationCanceledException ) { }
            catch( Exception ex )
            {
                StatusMessage = $"Couldn't save: {ex.Message}";
            }
            finally
            {
                IsSaving = false;
            }
        }

        private bool CanSave => IsDirty && !IsReadOnly && !IsSaving;

        [RelayCommand] private void GoToLine( ) => GoToLineRequested?.Invoke( );
        [RelayCommand] private void Find( )     => FindRequested?.Invoke( false );
        [RelayCommand] private void Replace( )  => FindRequested?.Invoke( true );
        [RelayCommand] private void IncreaseFontSize( ) => FontSize = Math.Min( FontSize + 1, 48 );
        [RelayCommand] private void DecreaseFontSize( ) => FontSize = Math.Max( FontSize - 1, 8 );
        [RelayCommand] private void ResetFontSize( )    => FontSize = 13;
        [RelayCommand] private void ToggleWordWrap( )   => WordWrap = !WordWrap;

        [RelayCommand]
        private void SetMarkdownMode( string mode )
        {
            if( Enum.TryParse( mode, out MarkdownMode m ) ) MarkdownMode = m;
        }

        public void UpdateCaret( int line, int column, int selectionLength )
        {
            CaretLine       = line;
            CaretColumn     = column;
            SelectionLength = selectionLength;
        }

        // Closing with unsaved edits

        public override async Task<bool> ConfirmCloseAsync( Window owner )
        {
            if( !IsDirty || IsReadOnly ) return true;

            var vm = new ConfirmDialogViewModel
            {
                Title       = "Unsaved changes",
                Message     = $"Save your changes to \"{_fileName}\"?",
                Detail      = "If you don't, your edits since the last save are lost.",
                ConfirmText = "Save",
                DiscardText = "Don't save",
                CancelText  = "Cancel",
                Tone        = DialogTone.Warning
            };

            bool? result = await new ConfirmDialog { DataContext = vm }.ShowDialog<bool?>( owner );
            if( result == null ) return false;          // cancel: keep it open
            if( result == false ) { IsDirty = false; return true; }

            await SaveCommand.ExecuteAsync( null );
            return !IsDirty;                            // a failed save keeps it open
        }

        // Helpers

        private void RefreshEncodingText( )
        {
            bool hasBom = _detectedEncoding.GetPreamble( ).Length > 0;
            EncodingText = _detectedEncoding.WebName.ToUpperInvariant( ) switch
            {
                "UTF-8"        => hasBom ? "UTF-8 with BOM" : "UTF-8",
                "UTF-16"       => "UTF-16 LE",
                "UTF-16BE"     => "UTF-16 BE",
                "UTF-32"       => "UTF-32",
                "WINDOWS-1252" => "Windows-1252",
                "ISO-8859-1"   => "Latin-1",
                var x          => x
            };
            LineEndingText = _detectedLineEnding == "\r\n" ? "CRLF" : _detectedLineEnding == "\n" ? "LF" : "CR";
        }

        private void UpdateWordCount( string text )
        {
            int count = 0;
            bool inWord = false;
            foreach( char c in text )
            {
                if( char.IsWhiteSpace( c ) ) inWord = false;
                else if( !inWord ) { inWord = true; count++; }
            }
            WordCount = count;
        }

        public override void Dispose( )
        {
            if( _disposed ) return;
            _disposed = true;
            _document.TextChanged -= OnDocumentTextChanged;
        }
    }
}
