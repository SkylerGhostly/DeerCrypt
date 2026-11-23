using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Search;
using AvaloniaEdit.TextMate;
using DeerCrypt.Services;
using DeerCrypt.ViewModels.Dialogs;
using DeerCrypt.ViewModels.Viewers;
using DeerCrypt.Views.Dialogs;
using Markdown.Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TextMateSharp.Grammars;

namespace DeerCrypt.Views.Viewers
{
    /// <summary>
    /// AvaloniaEdit host for <see cref="TextDocumentViewModel"/>: syntax highlighting (TextMate,
    /// or a column colorizer for CSV), find/replace, go to line, Markdown preview, and live
    /// dark/light theme switching. The document object lives in the view model, so a rebuilt
    /// view (pop out / dock) picks up the same text and undo history.
    /// </summary>
    public partial class TextEditorView : UserControl
    {
        private TextDocumentViewModel? _vm;
        private readonly TextEditor           _editor;
        private readonly MarkdownScrollViewer _mdPreview;
        private readonly Grid                 _splitGrid;
        private SearchPanel?                  _searchPanel;

        private RegistryOptions?          _registryOptions;
        private TextMate.Installation?    _textMate;
        private CsvColorizingTransformer? _csvTransformer;

        public TextEditorView( )
        {
            InitializeComponent( );
            _editor    = this.FindControl<TextEditor>( "Editor" )!;
            _mdPreview = this.FindControl<MarkdownScrollViewer>( "MarkdownPreview" )!;
            _splitGrid = this.FindControl<Grid>( "SplitGrid" )!;

            _editor.ShowLineNumbers               = true;
            _editor.Options.IndentationSize       = 4;
            _editor.Options.ConvertTabsToSpaces   = false;
            _editor.Options.EnableHyperlinks      = false;
            _editor.Options.EnableEmailHyperlinks = false;
            _editor.Options.HighlightCurrentLine  = true;
            _editor.TextArea.TextView.Margin      = new Thickness( 8, 0, 0, 0 );

            _searchPanel = SearchPanel.Install( _editor );
            SetupContextMenu( );

            _editor.AddHandler( PointerWheelChangedEvent, OnEditorWheel, RoutingStrategies.Tunnel );
            _editor.TextArea.Caret.PositionChanged     += ( _, _ ) => SyncCaret( );
            _editor.TextArea.SelectionChanged          += ( _, _ ) => SyncCaret( );

            this.FindControl<Button>( "UndoBtn" )!.Click += ( _, _ ) => { if( _editor.CanUndo ) _editor.Undo( ); };
            this.FindControl<Button>( "RedoBtn" )!.Click += ( _, _ ) => { if( _editor.CanRedo ) _editor.Redo( ); };

            // Privacy: never fetch anything for the preview (a remote image in a vault document
            // would tell its host the file was opened). Links still only open http(s) on click.
            if( _mdPreview.Engine is IMarkdownEngine engine )
                engine.HyperlinkCommand = new SafeHyperlinkCommand( );
            _mdPreview.Plugins = new MdAvPlugins { PathResolver = new NoImagesPathResolver( ) };

            ActualThemeVariantChanged += ( _, _ ) => ApplyTheme( );
        }

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );
            if( _vm != null )
            {
                _vm.PropertyChanged   -= OnVmPropertyChanged;
                _vm.GoToLineRequested -= OnGoToLineRequested;
                _vm.FindRequested     -= OnFindRequested;
                _vm.ViewDetaching     -= OnViewDetaching;
            }

            _vm = DataContext as TextDocumentViewModel;
            if( _vm == null ) return;

            _vm.PropertyChanged   += OnVmPropertyChanged;
            _vm.GoToLineRequested += OnGoToLineRequested;
            _vm.FindRequested     += OnFindRequested;
            _vm.ViewDetaching     += OnViewDetaching;
            ApplyEditorState( );
        }

        protected override void OnAttachedToVisualTree( VisualTreeAttachmentEventArgs e )
        {
            base.OnAttachedToVisualTree( e );
            ApplyTheme( );
            ApplyLanguage( );
            Dispatcher.UIThread.Post( ( ) => _editor.TextArea.Focus( ), DispatcherPriority.Loaded );
        }

        protected override void OnDetachedFromVisualTree( VisualTreeAttachmentEventArgs e )
        {
            base.OnDetachedFromVisualTree( e );
            _textMate?.Dispose( );
            _textMate = null;
        }

        // View model → editor

        private void ApplyEditorState( )
        {
            if( _vm == null ) return;
            _editor.Document   = _vm.Document;
            _editor.IsReadOnly = _vm.IsReadOnly;
            _editor.WordWrap   = _vm.WordWrap;
            _editor.FontSize   = _vm.FontSize;
            if( _vm.IsMarkdown ) SetMarkdownSafe( _vm.MarkdownSource );
            UpdateSplitLayout( );
        }

        private void OnVmPropertyChanged( object? sender, System.ComponentModel.PropertyChangedEventArgs e )
        {
            if( _vm == null ) return;
            switch( e.PropertyName )
            {
                case nameof( TextDocumentViewModel.Document ):
                    _editor.Document = _vm.Document;
                    ApplyLanguage( );
                    break;
                case nameof( TextDocumentViewModel.IsReadOnly ):     _editor.IsReadOnly = _vm.IsReadOnly; break;
                case nameof( TextDocumentViewModel.WordWrap ):       _editor.WordWrap   = _vm.WordWrap;   break;
                case nameof( TextDocumentViewModel.FontSize ):       _editor.FontSize   = _vm.FontSize;   break;
                case nameof( TextDocumentViewModel.MarkdownSource ): SetMarkdownSafe( _vm.MarkdownSource ); break;
                case nameof( TextDocumentViewModel.MarkdownMode ):   UpdateSplitLayout( ); break;
            }
        }

        private void SyncCaret( ) =>
            _vm?.UpdateCaret( _editor.TextArea.Caret.Line, _editor.TextArea.Caret.Column, _editor.SelectionLength );

        private void UpdateSplitLayout( )
        {
            if( _vm == null ) return;
            var cols = _splitGrid.ColumnDefinitions;
            ( cols [ 0 ].Width, cols [ 1 ].Width, cols [ 2 ].Width ) = _vm.IsMarkdown
                ? _vm.MarkdownMode switch
                {
                    MarkdownMode.Edit    => ( GridLength.Star, new GridLength( 0 ), new GridLength( 0 ) ),
                    MarkdownMode.Preview => ( new GridLength( 0 ), new GridLength( 0 ), GridLength.Star ),
                    _                    => ( GridLength.Star, new GridLength( 5 ), GridLength.Star ),
                }
                : ( GridLength.Star, new GridLength( 0 ), new GridLength( 0 ) );
            _editor.IsVisible = _vm.ShowEditor;
        }

        /// <summary>Empty the preview before removal: Markdown.Avalonia's text elements crash on theme loss.</summary>
        private void OnViewDetaching( )
        {
            try { _mdPreview.Markdown = string.Empty; } catch { }
        }

        private void SetMarkdownSafe( string source )
        {
            try { _mdPreview.Markdown = source; }
            catch( Exception ex ) { _mdPreview.Markdown = $"*Couldn't render: {ex.Message}*\n\n```\n{source}\n```"; }
        }

        // Theme and syntax highlighting

        private void ApplyTheme( )
        {
            bool isDark = ActualThemeVariant == ThemeVariant.Dark;

            _textMate?.Dispose( );
            _registryOptions = new RegistryOptions( isDark ? ThemeName.DarkPlus : ThemeName.LightPlus );
            _textMate        = _editor.InstallTextMate( _registryOptions );

            var raw = isDark ? DeerCryptTheme.TryLoadDark( ) : DeerCryptTheme.TryLoadLight( );
            if( raw != null ) { try { _textMate.SetTheme( raw ); } catch { } }

            // Editor chrome follows the app's own surfaces
            IBrush Res( string key, string fallback ) =>
                this.TryFindResource( key, ActualThemeVariant, out object? v ) && v is IBrush b ? b : SolidColorBrush.Parse( fallback );

            var bg = Res( "AppBackgroundBrush", isDark ? "#1B1B1E" : "#FBFBFC" );
            _editor.Background            = bg;
            _editor.TextArea.Background   = bg;
            _editor.Foreground            = Res( "TextPrimaryBrush", isDark ? "#ECECEF" : "#1B1B1F" );
            _editor.LineNumbersForeground = Res( "TextMutedBrush",   isDark ? "#6F6F78" : "#8C8C95" );
            _editor.TextArea.TextView.CurrentLineBackground = Res( "HoverBrush", isDark ? "#0DFFFFFF" : "#0B000000" );
            _editor.TextArea.TextView.CurrentLineBorder     = new Pen( Brushes.Transparent );
            _editor.TextArea.SelectionBrush = Res( "SelectedBrush", "#553F9BDB" );

            ApplyLanguage( );
        }

        private void ApplyLanguage( )
        {
            if( _vm == null ) return;
            string ext = _vm.FileExtension;

            if( _csvTransformer != null )
            {
                _editor.TextArea.TextView.LineTransformers.Remove( _csvTransformer );
                _csvTransformer = null;
            }

            if( ext == ".csv" )
            {
                _csvTransformer = new CsvColorizingTransformer( ActualThemeVariant == ThemeVariant.Dark );
                _editor.TextArea.TextView.LineTransformers.Add( _csvTransformer );
                return;
            }

            if( _textMate == null || _registryOptions == null ) return;
            try
            {
                string? scope = null;
                if( !string.IsNullOrEmpty( ext ) && _registryOptions.GetLanguageByExtension( ext ) is { } lang )
                    scope = _registryOptions.GetScopeByLanguageId( lang.Id );

                scope ??= ( ext switch { ".h" => "cpp", ".conf" or ".cfg" => "ini", _ => null } ) is { } alt
                    ? _registryOptions.GetScopeByLanguageId( alt )
                    : null;

                if( scope != null ) _textMate.SetGrammar( scope );
            }
            catch { /* unsupported: no highlighting */ }
        }

        // Find / go to line

        private void OnFindRequested( bool replace )
        {
            if( _searchPanel == null ) return;
            _searchPanel.IsReplaceMode = replace && !_editor.IsReadOnly;
            _searchPanel.Open( );
            if( !string.IsNullOrEmpty( _editor.SelectedText ) && !_editor.SelectedText.Contains( '\n' ) )
                _searchPanel.SearchPattern = _editor.SelectedText;
            _searchPanel.Reactivate( );
        }

        private async void OnGoToLineRequested( )
        {
            if( _vm == null || TopLevel.GetTopLevel( this ) is not Window owner ) return;

            var input = new InputDialogViewModel
            {
                Title       = "Go to line",
                Prompt      = $"Enter a line number from 1 to {_vm.LineCount:N0}.",
                Icon        = FluentIcons.Common.Symbol.TextNumberList,
                Placeholder = _vm.CaretLine.ToString( ),
                ConfirmText = "Go",
                Validator   = s => int.TryParse( s, out int n ) && n >= 1 && n <= _vm.LineCount
                    ? null
                    : $"Enter a number between 1 and {_vm.LineCount:N0}."
            };

            await new InputDialog { DataContext = input }.ShowDialog( owner );

            if( input.ConfirmedValue != null && int.TryParse( input.ConfirmedValue, out int line ) )
            {
                _editor.TextArea.Caret.Line   = line;
                _editor.TextArea.Caret.Column = 1;
                _editor.ScrollToLine( line );
                _editor.TextArea.Focus( );
            }
        }

        // Input

        private void OnEditorWheel( object? sender, PointerWheelEventArgs e )
        {
            if( ( e.KeyModifiers & KeyModifiers.Control ) == 0 || _vm == null ) return;
            if( e.Delta.Y > 0 ) _vm.IncreaseFontSizeCommand.Execute( null );
            else if( e.Delta.Y < 0 ) _vm.DecreaseFontSizeCommand.Execute( null );
            e.Handled = true;
        }

        private void SetupContextMenu( )
        {
            var cm = new ContextMenu( );

            MenuItem Item( string header, string gesture, FluentIcons.Common.Symbol icon, Func<Task> run )
            {
                var mi = new MenuItem
                {
                    Header       = header,
                    InputGesture = KeyGesture.Parse( gesture ),
                    Icon         = new FluentIcons.Avalonia.SymbolIcon { Symbol = icon }
                };
                mi.Click += ( _, _ ) => _ = run( );
                return mi;
            }

            IClipboard? Clipboard( ) => TopLevel.GetTopLevel( this )?.Clipboard;

            cm.Items.Add( Item( "Cut", "Ctrl+X", FluentIcons.Common.Symbol.Cut, async ( ) =>
            {
                if( _editor.IsReadOnly || string.IsNullOrEmpty( _editor.SelectedText ) || Clipboard( ) is not { } cb ) return;
                await cb.SetTextAsync( _editor.SelectedText );
                _editor.Document.Replace( _editor.SelectionStart, _editor.SelectionLength, "" );
            } ) );
            cm.Items.Add( Item( "Copy", "Ctrl+C", FluentIcons.Common.Symbol.Copy, async ( ) =>
            {
                if( !string.IsNullOrEmpty( _editor.SelectedText ) && Clipboard( ) is { } cb )
                    await cb.SetTextAsync( _editor.SelectedText );
            } ) );
            cm.Items.Add( Item( "Paste", "Ctrl+V", FluentIcons.Common.Symbol.ClipboardPaste, async ( ) =>
            {
                if( _editor.IsReadOnly || Clipboard( ) is not { } cb ) return;
                string? text = await ClipboardExtensions.TryGetTextAsync( cb );
                if( text == null ) return;
                _editor.Document.Replace( _editor.SelectionStart, _editor.SelectionLength, text );
                _editor.TextArea.Caret.Offset = _editor.SelectionStart + text.Length;
            } ) );
            cm.Items.Add( new Separator( ) );
            cm.Items.Add( Item( "Select all", "Ctrl+A", FluentIcons.Common.Symbol.SelectAllOn, ( ) =>
            {
                _editor.TextArea.Selection = Selection.Create( _editor.TextArea, 0, _editor.Document.TextLength );
                return Task.CompletedTask;
            } ) );
            cm.Items.Add( Item( "Find", "Ctrl+F", FluentIcons.Common.Symbol.Search, ( ) =>
            {
                OnFindRequested( false );
                return Task.CompletedTask;
            } ) );

            _editor.ContextMenu = cm;
        }

        /// <summary>Resolves no images at all: the preview never reads files or the network.</summary>
        private sealed class NoImagesPathResolver : Markdown.Avalonia.Utils.IPathResolver
        {
            public string? AssetPathRoot { set { } }
            public System.Collections.Generic.IEnumerable<string>? CallerAssemblyNames { set { } }
            public Task<Stream?>? ResolveImageResource( string relativeOrAbsolutePath ) => Task.FromResult<Stream?>( null );
        }

        /// <summary>Opens only absolute http(s) links, and only when clicked.</summary>
        private sealed class SafeHyperlinkCommand : System.Windows.Input.ICommand
        {
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute( object? parameter ) => true;
            public void Execute( object? parameter )
            {
                string? url = parameter switch
                {
                    Uri u    => u.IsAbsoluteUri ? u.AbsoluteUri : u.OriginalString,
                    string s => s,
                    _        => null
                };
                if( string.IsNullOrEmpty( url ) || url.StartsWith( '#' ) ) return;
                if( !url.StartsWith( "http://", StringComparison.OrdinalIgnoreCase )
                 && !url.StartsWith( "https://", StringComparison.OrdinalIgnoreCase ) ) return;
                try { Process.Start( new ProcessStartInfo( url ) { UseShellExecute = true } ); } catch { }
            }
        }
    }
}
