using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Services;
using DeerCrypt.ViewModels.Viewers;
using DeerCrypt.Views.Viewers;
using DeerCryptLib.Vault;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels
{
    /// <summary>
    /// Open documents (viewer tabs). Docked documents live in <see cref="Documents"/> and every
    /// one keeps its view alive while hidden, so switching tabs never loses scroll position,
    /// zoom, or playback. A document can be popped out into its own window and docked back.
    /// </summary>
    public partial class VaultBrowserViewModel
    {
        public ObservableCollection<DocumentViewModel> Documents { get; } = [ ];

        private readonly Dictionary<DocumentViewModel, DocumentWindow> _popouts = [ ];

        /// <summary>The tab on screen; null means the file browser.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsBrowserActive ) )]
        public partial DocumentViewModel? ActiveDocument { get; set; }

        public bool IsBrowserActive => ActiveDocument == null;

        /// <summary>The bottom bar shows in Files, or while an activity is running/just finished.</summary>
        public bool ShowStatusBar => IsBrowserActive || Activities.ShowStrip;
        public bool HasDocuments    => Documents.Count > 0;

        /// <summary>Audio or video is playing in some tab or window.</summary>
        public bool IsMediaPlaying =>
            Documents.Concat( _popouts.Keys ).OfType<MediaDocumentViewModel>( ).Any( m => m.Session?.IsPlaying == true );

        /// <summary>Any open document (tab or window) with unsaved edits.</summary>
        public bool HasUnsavedDocuments => Documents.Concat( _popouts.Keys ).Any( d => d.IsDirty );

        partial void OnActiveDocumentChanged( DocumentViewModel? oldValue, DocumentViewModel? newValue )
        {
            if( oldValue != null && !oldValue.IsPoppedOut ) oldValue.IsActive = false;
            if( newValue != null ) newValue.IsActive = true;
            OnPropertyChanged( nameof( ShowStatusBar ) );

            // File actions (and their shortcuts) only apply while the Files tab is showing
            RenameCommand.NotifyCanExecuteChanged( );
            DeleteCommand.NotifyCanExecuteChanged( );
            ExtractCommand.NotifyCanExecuteChanged( );
            MoveCommand.NotifyCanExecuteChanged( );
            OpenCommand.NotifyCanExecuteChanged( );
            OpenInNewWindowCommand.NotifyCanExecuteChanged( );
            PasteFilesCommand.NotifyCanExecuteChanged( );
        }

        [RelayCommand]
        private void ShowBrowser( ) => ActiveDocument = null;

        [RelayCommand]
        private void ActivateDocument( DocumentViewModel doc )
        {
            if( _popouts.TryGetValue( doc, out var window ) ) { window.Activate( ); return; }
            ActiveDocument = doc;
        }

        /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: cycle Files → tab 1 → tab 2 … → Files.</summary>
        [RelayCommand]
        private void CycleTabs( string direction )
        {
            if( Documents.Count == 0 ) return;
            int count = Documents.Count + 1; // + the Files tab at position 0
            int pos   = ActiveDocument == null ? 0 : Documents.IndexOf( ActiveDocument ) + 1;
            pos = ( pos + ( direction == "Back" ? count - 1 : 1 ) ) % count;
            ActiveDocument = pos == 0 ? null : Documents [ pos - 1 ];
        }

        // Opening

        /// <summary>Viewers open in their own windows instead of tabs (Settings).</summary>
        public bool OpenViewersInWindows => App.SettingsService.Settings.OpenViewersIn == "Windows";

        private void OpenDocument( DocumentViewModel doc, bool inWindow = false )
        {
            doc.CloseRequested      += OnDocumentCloseRequested;
            doc.VaultChanged        += OnDocumentVaultChanged;
            doc.FullscreenRequested += OnDocumentFullscreenRequested;
            doc.PropertyChanged     += OnDocumentPropertyChanged;

            if( inWindow )
            {
                ShowInWindow( doc, fullscreen: false );
                return;
            }

            Documents.Add( doc );
            OnPropertyChanged( nameof( HasDocuments ) );
            ActiveDocument = doc;
        }

        /// <summary>If a document already shows this file, bring it forward instead of opening another.</summary>
        private bool TryActivateExisting( string fileId )
        {
            foreach( var doc in Documents.Concat( _popouts.Keys ) )
            {
                switch( doc )
                {
                    case ImageDocumentViewModel img when img.FileId == fileId:
                        ActivateDocument( doc );
                        return true;
                    case MediaDocumentViewModel media when media.FileId == fileId:
                        ActivateDocument( doc );
                        return true;
                    case TextDocumentViewModel text when text.FileId == fileId:
                        ActivateDocument( doc );
                        return true;
                }
            }
            return false;
        }

        // Closing

        [RelayCommand]
        private async Task CloseDocumentAsync( DocumentViewModel doc )
        {
            Window? owner = _popouts.TryGetValue( doc, out var w ) ? w : OwnerWindow;
            if( owner != null && !await doc.ConfirmCloseAsync( owner ) ) return;
            DiscardDocument( doc );
        }

        [RelayCommand]
        private async Task CloseOtherDocumentsAsync( DocumentViewModel keep )
        {
            foreach( var doc in Documents.Where( d => !ReferenceEquals( d, keep ) ).ToList( ) )
                await CloseDocumentAsync( doc );
            ActiveDocument = keep;
        }

        [RelayCommand]
        private async Task CloseAllDocumentsAsync( )
        {
            foreach( var doc in Documents.ToList( ) )
                await CloseDocumentAsync( doc );
        }

        [RelayCommand]
        private async Task CloseActiveDocumentAsync( )
        {
            if( ActiveDocument is { } doc ) await CloseDocumentAsync( doc );
        }

        /// <summary>Removes and disposes a document without asking (the caller already did).</summary>
        private void DiscardDocument( DocumentViewModel doc )
        {
            doc.BeforeMove( ); // let the view release what it must before it's torn down
            doc.CloseRequested      -= OnDocumentCloseRequested;
            doc.VaultChanged        -= OnDocumentVaultChanged;
            doc.FullscreenRequested -= OnDocumentFullscreenRequested;
            doc.PropertyChanged     -= OnDocumentPropertyChanged;

            if( _popouts.Remove( doc, out var window ) )
                window.CloseWithoutPrompt( );

            int index = Documents.IndexOf( doc );
            if( index >= 0 )
            {
                Documents.RemoveAt( index );
                if( ReferenceEquals( ActiveDocument, doc ) )
                    ActiveDocument = Documents.Count == 0 ? null : Documents [ System.Math.Min( index, Documents.Count - 1 ) ];
            }

            doc.Dispose( );
            OnPropertyChanged( nameof( HasDocuments ) );
            OnPropertyChanged( nameof( HasUnsavedDocuments ) );
        }

        /// <summary>Used when locking: everything goes, no questions (the lock flow already asked).</summary>
        public void DiscardAllDocuments( )
        {
            foreach( var doc in Documents.Concat( _popouts.Keys ).ToList( ) )
                DiscardDocument( doc );
            ActiveDocument = null;
        }

        // Pop out / dock

        [RelayCommand]
        private void PopOutDocument( DocumentViewModel doc ) => PopOut( doc, fullscreen: false );

        private DocumentWindow PopOut( DocumentViewModel doc, bool fullscreen )
        {
            if( _popouts.TryGetValue( doc, out var existing ) ) return existing;

            doc.BeforeMove( );
            int index = Documents.IndexOf( doc );
            Documents.Remove( doc );
            if( ReferenceEquals( ActiveDocument, doc ) )
                ActiveDocument = Documents.Count == 0 ? null : Documents [ System.Math.Clamp( index - 1, 0, Documents.Count - 1 ) ];
            OnPropertyChanged( nameof( HasDocuments ) );

            return ShowInWindow( doc, fullscreen );
        }

        /// <summary>Shows a document (not in the tabs) in its own window.</summary>
        private DocumentWindow ShowInWindow( DocumentViewModel doc, bool fullscreen )
        {
            doc.IsPoppedOut = true;
            doc.IsActive    = true;

            var window = new DocumentWindow( doc,
                onDock:   d => _ = DockDocumentAsync( d ),
                onClosed: d => { if( _popouts.Remove( d ) ) DiscardDocument( d ); } );
            _popouts [ doc ] = window;

            if( fullscreen )
            {
                window.DockWhenLeavingFullscreen = true;
                window.WindowState = WindowState.FullScreen;
            }
            window.Show( );
            Avalonia.Threading.Dispatcher.UIThread.Post( doc.AfterMove, Avalonia.Threading.DispatcherPriority.Loaded );
            return window;
        }

        private async Task DockDocumentAsync( DocumentViewModel doc )
        {
            if( !_popouts.Remove( doc, out var window ) ) return;

            doc.BeforeMove( );
            window.CloseWithoutPrompt( );
            await Task.Yield( );

            doc.IsPoppedOut = false;
            Documents.Add( doc );
            OnPropertyChanged( nameof( HasDocuments ) );
            ActiveDocument = doc;
            doc.IsActive   = true;
            OwnerWindow?.Activate( );
            Avalonia.Threading.Dispatcher.UIThread.Post( doc.AfterMove, Avalonia.Threading.DispatcherPriority.Loaded );
        }

        private void OnDocumentFullscreenRequested( DocumentViewModel doc )
        {
            if( _popouts.TryGetValue( doc, out var window ) )
                window.ToggleFullscreen( );
            else
                PopOut( doc, fullscreen: true );
        }

        // Document → browser

        private void OnDocumentCloseRequested( DocumentViewModel doc ) => DiscardDocument( doc );

        private void OnDocumentVaultChanged( DocumentViewModel doc )
        {
            _ = ReloadCurrentViewAsync( );
            _ = RefreshVaultInfoAsync( );
            ScheduleInspectorUpdate( );
        }

        private void OnDocumentPropertyChanged( object? sender, System.ComponentModel.PropertyChangedEventArgs e )
        {
            if( e.PropertyName == nameof( DocumentViewModel.IsDirty ) )
                OnPropertyChanged( nameof( HasUnsavedDocuments ) );
        }

        /// <summary>Shows a document's current file in the browser and selects it.</summary>
        [RelayCommand]
        private async Task RevealDocumentFileAsync( DocumentViewModel doc )
        {
            if( string.IsNullOrEmpty( doc.FileId ) ) return;
            VaultItem? entry = await Vault.GetEntryAsync( doc.FileId );
            if( entry is not VaultEntry file ) return;

            ActiveDocument = null;
            await NavigateToDirectoryAsync( file.DirectoryId );
            var match = CurrentItems.FirstOrDefault( i => i.Id == file.Id );
            if( match != null ) SelectedFile = match;
        }

        /// <summary>Right-click "Show in folder": jump to the selected item's folder and select it there.</summary>
        [RelayCommand]
        private async Task RevealSelectedAsync( )
        {
            if( SelectedFile is not { } item ) return;
            string? folderId = item.AsFile?.DirectoryId ?? item.AsDir?.ParentId;
            if( folderId == null ) return;
            await NavigateToDirectoryAsync( folderId );
            var match = CurrentItems.FirstOrDefault( i => i.Id == item.Id );
            if( match != null ) SelectedFile = match;
        }

        // Viewers opened from the browser

        /// <summary>Items of one kind from the list on screen, in display order (the viewer's set or playlist).</summary>
        private List<VaultItemViewModel> SiblingsOfKind( VaultItemViewModel item, System.Func<string, bool> isKind )
        {
            var list = CurrentItems.Where( i => i.IsFile && isKind( i.DisplayName ) ).ToList( );
            if( !list.Contains( item ) ) list.Insert( 0, item );
            return list;
        }

        private async Task OpenInImageViewerAsync( VaultItemViewModel item, bool newWindow = false )
        {
            if( !newWindow && TryActivateExisting( item.Id ) ) return;

            var images = SiblingsOfKind( item, FileTypeHelper.IsImage );
            var doc    = new ImageDocumentViewModel( Vault, images, images.IndexOf( item ) );
            doc.ItemDeleted += deleted =>
            {
                var existing = CurrentItems.FirstOrDefault( i => i.Id == deleted.Id );
                if( existing != null ) CurrentItems.Remove( existing );
            };
            OpenDocument( doc, newWindow || OpenViewersInWindows );
            await doc.LoadCurrentAsync( );
        }

        private async Task OpenInMediaPlayerAsync( VaultItemViewModel item, bool newWindow = false )
        {
            if( !newWindow && TryActivateExisting( item.Id ) ) return;

            var playlist = SiblingsOfKind( item, FileTypeHelper.IsMedia );
            var doc      = new MediaDocumentViewModel( Vault, App.LibVLC, playlist, playlist.IndexOf( item ) );
            OpenDocument( doc, newWindow || OpenViewersInWindows );
            await doc.LoadCurrentAsync( );
        }

        private async Task OpenInTextEditorAsync( VaultItemViewModel item, bool newWindow = false )
        {
            if( !newWindow && TryActivateExisting( item.Id ) ) return;

            var doc = new TextDocumentViewModel( Vault, item.AsFile!.Id, item.DisplayName );
            OpenDocument( doc, newWindow || OpenViewersInWindows );
            await doc.LoadAsync( );
        }
    }
}
