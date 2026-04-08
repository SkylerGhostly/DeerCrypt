using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Search;
using AvaloniaEdit.TextMate;
using DeerCrypt.Services;
using DeerCrypt.ViewModels.Dialogs;
using Markdown.Avalonia;
using TextMateSharp.Grammars;

namespace DeerCrypt.Views.Dialogs
{
    public partial class TextEditorWindow : Window
    {
        private TextEditorViewModel?  _vm;
        private TextEditor?           _editor;
        private MarkdownScrollViewer? _mdPreview;
        private Grid?                 _editorSplitGrid;

        private RegistryOptions?              _registryOptions;
        private TextMate.Installation?        _textMateInstallation;
        private CsvColorizingTransformer?     _csvTransformer;

        public TextEditorWindow( )
        {
            InitializeComponent( );
        }

        // Lifecycle 

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );
            _vm = DataContext as TextEditorViewModel;
        }

        protected override async void OnOpened( EventArgs e )
        {
            base.OnOpened( e );

            _editor          = this.FindControl<TextEditor>( "Editor" );
            _mdPreview       = this.FindControl<MarkdownScrollViewer>( "MarkdownPreview" );
            _editorSplitGrid = this.FindControl<Grid>( "EditorSplitGrid" );

            if( _vm == null || _editor == null ) return;

            // AvaloniaEdit setup 
            _editor.ShowLineNumbers                     = true;
            _editor.Options.IndentationSize             = 4;
            _editor.Options.ConvertTabsToSpaces   = false;
            _editor.Options.EnableHyperlinks      = false;
            _editor.Options.EnableEmailHyperlinks = false;

            // Left padding between line-number gutter and code text
            _editor.TextArea.TextView.Margin = new Thickness( 6, 0, 0, 0 );

            // TextMate syntax highlighting 
            bool isDark = ActualThemeVariant == ThemeVariant.Dark;

            _registryOptions      = new RegistryOptions( isDark ? ThemeName.DarkPlus : ThemeName.LightPlus );
            _textMateInstallation = _editor.InstallTextMate( _registryOptions );

            // Override with DeerCrypt's hand-tuned palette
            var rawTheme = isDark ? DeerCryptTheme.TryLoadDark( ) : DeerCryptTheme.TryLoadLight( );
            if( rawTheme != null )
            {
                try   { _textMateInstallation.SetTheme( rawTheme ); }
                catch { /* keep the bundled fallback theme */ }
            }

            // Explicit Avalonia colours - belt-and-suspenders in case the TextMate
            // theme colours haven't propagated to the AvaloniaEdit properties yet.
            if( isDark )
            {
                var panelBg = new SolidColorBrush( Color.Parse( "#1A1A1A" ) );
                _editor.Background            = panelBg;
                _editor.TextArea.Background   = panelBg;
                _editor.Foreground            = new SolidColorBrush( Color.Parse( "#D0D0D0" ) );
                _editor.LineNumbersForeground  = new SolidColorBrush( Color.Parse( "#555C6B" ) );
                _editor.TextArea.TextView.CurrentLineBackground =
                    new SolidColorBrush( Color.Parse( "#18FFFFFF" ) );
            }

            // Install find/replace panel (Ctrl+F)
            SearchPanel.Install( _editor );

            // Context menu
            SetupContextMenu( );

            // Subscribe to ViewModel 
            _vm.PropertyChanged   += OnVmPropertyChanged;
            _vm.GoToLineRequested += OnGoToLineRequested;

            // Suppress Markdown.Avalonia default hyperlink handler
            // (it calls Process.Start which crashes on #anchor links)
            if( _mdPreview?.Engine is IMarkdownEngine mdEngine )
                mdEngine.HyperlinkCommand = new SafeHyperlinkCommand( );

            // Ctrl+scroll for font size
            _editor.AddHandler(
                PointerWheelChangedEvent,
                OnEditorWheel,
                RoutingStrategies.Tunnel );

            // Load and apply 
            await _vm.LoadAsync( );
            ApplyEditorState( );
            UpdateSplitLayout( );

            _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        }

        protected override async void OnClosing( WindowClosingEventArgs e )
        {
            if( _vm?.IsDirty == true && !_vm.IsReadOnly )
            {
                e.Cancel = true;
                await HandleUnsavedChangesAsync( );
                return;
            }
            base.OnClosing( e );
        }

        protected override void OnClosed( EventArgs e )
        {
            if( _vm != null )
            {
                _vm.PropertyChanged   -= OnVmPropertyChanged;
                _vm.GoToLineRequested -= OnGoToLineRequested;
            }

            _editor?.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;

            _textMateInstallation?.Dispose( );
            _vm?.Dispose( );
            base.OnClosed( e );
        }

        // Editor state sync 

        private void ApplyEditorState( )
        {
            if( _editor == null || _vm == null ) return;

            _editor.Document   = _vm.Document;
            _editor.IsReadOnly = _vm.IsReadOnly;
            _editor.WordWrap   = _vm.WordWrap;
            _editor.FontSize   = _vm.FontSize;

            ApplyLanguage( _vm.FileExtension );

            if( _mdPreview != null && _vm.IsMarkdown )
                SetMarkdownSafe( _vm.MarkdownSource );
        }

        private void OnVmPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs e )
        {
            if( _editor == null || _vm == null ) return;

            switch( e.PropertyName )
            {
                case nameof( TextEditorViewModel.Document ):
                    _editor.Document = _vm.Document;
                    break;
                case nameof( TextEditorViewModel.IsReadOnly ):
                    _editor.IsReadOnly = _vm.IsReadOnly;
                    break;
                case nameof( TextEditorViewModel.WordWrap ):
                    _editor.WordWrap = _vm.WordWrap;
                    break;
                case nameof( TextEditorViewModel.FontSize ):
                    _editor.FontSize = _vm.FontSize;
                    break;
                case nameof( TextEditorViewModel.MarkdownSource ):
                    SetMarkdownSafe( _vm.MarkdownSource );
                    break;
                case nameof( TextEditorViewModel.ShowMarkdownPreview ):
                    UpdateSplitLayout( );
                    break;
            }
        }

        // Language / grammar 

        private void ApplyLanguage( string ext )
        {
            // Remove any active CSV colorizer 
            if( _csvTransformer != null )
            {
                _editor?.TextArea.TextView.LineTransformers.Remove( _csvTransformer );
                _csvTransformer = null;
            }

            // CSV: rainbow-column transformer instead of TextMate 
            if( ext == ".csv" && _editor != null )
            {
                _csvTransformer = new CsvColorizingTransformer( ActualThemeVariant == ThemeVariant.Dark );
                _editor.TextArea.TextView.LineTransformers.Add( _csvTransformer );
                return;
            }

            // All other languages: TextMate grammar 
            if( _textMateInstallation == null || _registryOptions == null ) return;

            try
            {
                string? scope = null;

                if( !string.IsNullOrEmpty( ext ) )
                {
                    var lang = _registryOptions.GetLanguageByExtension( ext );
                    if( lang != null )
                        scope = _registryOptions.GetScopeByLanguageId( lang.Id );
                }

                // Fallback for extensions that need a nudge
                if( scope == null )
                {
                    string? altId = ext switch
                    {
                        ".h"              => "cpp",
                        ".conf" or ".cfg" => "ini",
                        _                 => null
                    };

                    if( altId != null )
                        scope = _registryOptions.GetScopeByLanguageId( altId );
                }

                if( scope != null )
                    _textMateInstallation.SetGrammar( scope );
            }
            catch { /* silently skip - unsupported extension gets no highlighting */ }
        }

        // Split layout 

        private void UpdateSplitLayout( )
        {
            if( _editorSplitGrid == null || _vm == null ) return;
            bool show = _vm.ShowMarkdownPreview;
            _editorSplitGrid.ColumnDefinitions[ 1 ].Width =
                show ? new GridLength( 5 )   : new GridLength( 0 );
            _editorSplitGrid.ColumnDefinitions[ 2 ].Width =
                show ? new GridLength( 300 ) : new GridLength( 0 );
        }

        private void SetMarkdownSafe( string source )
        {
            if( _mdPreview == null ) return;
            try
            {
                _mdPreview.Markdown = source;
            }
            catch( Exception ex )
            {
                _mdPreview.Markdown = $"*(Render error: {ex.Message})*\n\n```\n{source}\n```";
            }
        }

        private void OnCaretPositionChanged( object? sender, EventArgs e )
        {
            if( _vm == null || _editor == null ) return;
            _vm.UpdatePosition(
                _editor.TextArea.Caret.Line,
                _editor.TextArea.Caret.Column );
        }

        // Context menu 

        private void SetupContextMenu( )
        {
            if( _editor == null ) return;

            var cm = new ContextMenu( );

            MenuItem MakeItem( string header, KeyGesture gesture, Func<Task> handler )
            {
                var item = new MenuItem { Header = header, InputGesture = gesture };
                item.Click += ( _, _ ) => _ = handler( );
                return item;
            }

            cm.Items.Add( MakeItem( "Cut",        new KeyGesture( Key.X, KeyModifiers.Control ),
                async ( ) =>
                {
                    if( _editor.IsReadOnly ) return;
                    var text = _editor.SelectedText;
                    if( string.IsNullOrEmpty( text ) ) return;
                    await TopLevel.GetTopLevel( this )!.Clipboard!.SetTextAsync( text );
                    _editor.Document.Replace( _editor.SelectionStart, _editor.SelectionLength, "" );
                } ) );

            cm.Items.Add( MakeItem( "Copy",       new KeyGesture( Key.C, KeyModifiers.Control ),
                async ( ) =>
                {
                    var text = _editor.SelectedText;
                    if( !string.IsNullOrEmpty( text ) )
                        await TopLevel.GetTopLevel( this )!.Clipboard!.SetTextAsync( text );
                } ) );

            cm.Items.Add( MakeItem( "Paste",      new KeyGesture( Key.V, KeyModifiers.Control ),
                async ( ) =>
                {
                    if( _editor.IsReadOnly ) return;
                    if( Clipboard == null ) return;

                    string? text = await ClipboardExtensions.TryGetTextAsync( Clipboard );
                    if( text != null )
                    {
                        _editor.Document.Replace( _editor.SelectionStart, _editor.SelectionLength, text );
                        _editor.TextArea.Caret.Offset = _editor.SelectionStart + text.Length;
                    }
                } ) );

            cm.Items.Add( new Separator( ) );

            cm.Items.Add( MakeItem( "Select All", new KeyGesture( Key.A, KeyModifiers.Control ),
                ( ) =>
                {
                    _editor.TextArea.Selection = Selection.Create(
                        _editor.TextArea, 0, _editor.Document.TextLength );
                    return Task.CompletedTask;
                } ) );

            _editor.ContextMenu = cm;
        }

        // Wheel zoom (Ctrl+scroll) 

        private void OnEditorWheel( object? sender, PointerWheelEventArgs e )
        {
            if( (e.KeyModifiers & KeyModifiers.Control) == 0 ) return;
            if( _vm == null ) return;

            if( e.Delta.Y > 0 ) _vm.IncreaseFontSizeCommand.Execute( null );
            else if( e.Delta.Y < 0 ) _vm.DecreaseFontSizeCommand.Execute( null );

            e.Handled = true;
        }

        // Go to Line 

        private async void OnGoToLineRequested( )
        {
            if( _editor == null || _vm == null ) return;

            var inputVm = new InputDialogViewModel
            {
                Title       = "Go to Line",
                Prompt      = "Line number:",
                Placeholder = $"1 \u2013 {_vm.LineCount}",
                ConfirmText = "Go",
                Validator   = s =>
                {
                    if( !int.TryParse( s, out int n ) || n < 1 || n > _vm.LineCount )
                        return $"Enter a number between 1 and {_vm.LineCount}.";
                    return null;
                }
            };

            var dialog = new InputDialog { DataContext = inputVm };
            await dialog.ShowDialog( this );

            if( inputVm.ConfirmedValue != null
                && int.TryParse( inputVm.ConfirmedValue, out int line ) )
            {
                _editor.TextArea.Caret.Line   = line;
                _editor.TextArea.Caret.Column = 1;
                _editor.ScrollToLine( line );
                _editor.TextArea.Focus( );
            }
        }

        // Unsaved changes dialog 

        private async Task HandleUnsavedChangesAsync( )
        {
            var confirmVm = new ConfirmDialogViewModel
            {
                Title       = "Unsaved Changes",
                Message     = $"\u201c{_vm!.WindowTitle.TrimStart( '\u2022', ' ' )}\u201d has unsaved changes.",
                Detail      = "Save before closing?",
                ConfirmText = "Save",
                CancelText  = "Discard"
            };

            bool? save = await new ConfirmDialog { DataContext = confirmVm }
                .ShowDialog<bool?>( this );

            if( save == true )
            {
                await _vm.SaveCommand.ExecuteAsync( null );
                if( !_vm.IsDirty ) Close( );
            }
            else if( save == false )
            {
                _vm.IsDirty = false;
                Close( );
            }
        }

        // Safe hyperlink command for the Markdown preview 

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

                if( string.IsNullOrEmpty( url ) ) return;
                if( url.StartsWith( '#' ) ) return;

                if( !url.StartsWith( "http://",  StringComparison.OrdinalIgnoreCase )
                 && !url.StartsWith( "https://", StringComparison.OrdinalIgnoreCase ) )
                    return;

                try
                {
                    Process.Start( new ProcessStartInfo( url ) { UseShellExecute = true } );
                }
                catch { }
            }
        }
    }
}
