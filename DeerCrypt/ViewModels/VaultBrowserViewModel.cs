using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Models;
using DeerCrypt.Services;
using DeerCrypt.ViewModels.Dialogs;
using DeerCrypt.Views.Dialogs;
using DeerCryptLib.Vault;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels
{
    public partial class VaultBrowserViewModel : ObservableObject
    {
        private readonly VaultService _vaultService;
        private VaultFile Vault => _vaultService.Vault!;

        // Directory tree 
        public ObservableCollection<DirectoryNodeViewModel> RootNodes { get; } = [ ];

        [ObservableProperty]
        public partial DirectoryNodeViewModel? SelectedDirectory { get; set; }

        // File listing 
        public ObservableCollection<VaultEntry> CurrentFiles { get; } = [ ];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( ExtractCommand ) )]
        [NotifyCanExecuteChangedFor( nameof( DeleteCommand ) )]
        [NotifyCanExecuteChangedFor( nameof( RenameCommand ) )]
        [NotifyCanExecuteChangedFor( nameof( OpenCommand ) )]
        [NotifyCanExecuteChangedFor( nameof( MoveCommand ) )]
        public partial VaultItemViewModel? SelectedFile { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;
        public Window? OwnerWindow { get; set; }

        public ObservableCollection<VaultItemViewModel> CurrentItems { get; } = [ ];

        /// <summary>All items currently selected in the file list (synced from code-behind).</summary>
        public ObservableCollection<VaultItemViewModel> SelectedItems { get; } = [ ];

        private readonly Stack<DirectoryNodeViewModel> _backStack    = new();
        private readonly Stack<DirectoryNodeViewModel> _forwardStack = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( NavigateBackCommand ) )]
        [NotifyCanExecuteChangedFor( nameof( NavigateForwardCommand ) )]
        public partial bool CanNavigateBack { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( NavigateBackCommand ) )]
        [NotifyCanExecuteChangedFor( nameof( NavigateForwardCommand ) )]
        public partial bool CanNavigateForward { get; set; }

        // Breadcrumb - list of (DirectoryId, DisplayName) from root to current
        public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = [ ];

        [ObservableProperty]
        public partial VaultItemViewModel? DragOverItem { get; set; }

        /// <summary>DirectoryId of the breadcrumb segment currently being hovered during a drag.</summary>
        [ObservableProperty]
        public partial string? BreadcrumbDragTargetId { get; set; }

        public string CurrentDirectoryId =>
            _currentDirectory?.DirectoryId ?? Vault.RootDirectoryId;

        // Search 

        private CancellationTokenSource? _searchCts;

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsSearchActive ) )]
        public partial string SearchText { get; set; } = string.Empty;

        public bool IsSearchActive => !string.IsNullOrWhiteSpace( SearchText );

        [RelayCommand]
        private void ClearSearch( ) => SearchText = string.Empty;

        partial void OnSearchTextChanged( string value )
        {
            _searchCts?.Cancel( );
            _searchCts = new CancellationTokenSource( );
            var ct = _searchCts.Token;
            if( !string.IsNullOrWhiteSpace( value ) ) ActiveDocument = null; // results show in the Files tab
            RaiseListStateChanged( );
            _ = RunSearchDebouncedAsync( value, ct );
        }

        private async Task RunSearchDebouncedAsync( string query, CancellationToken ct )
        {
            try
            {
                await Task.Delay( 300, ct );
                await RunSearchAsync( query, ct );
            }
            catch( OperationCanceledException ) { }
        }

        private async Task RunSearchAsync( string query, CancellationToken ct )
        {
            if( ct.IsCancellationRequested ) return;

            if( string.IsNullOrWhiteSpace( query ) )
            {
                await ReloadCurrentViewAsync( ct );
                return;
            }

            StatusMessage = "Searching…";
            RequestScroll( ListScroll.Top );
            int version = BeginLoad( );
            try
            {
                var match    = SearchQuery.Parse( query );
                var scopeDir = SearchInFolder ? _currentDirectory : null;

                // Plain text over the whole vault can use the database's own LIKE filter;
                // wildcards and folder scope filter the full listing in memory.
                IReadOnlyList<VaultEntry> files = !match.IsPattern && scopeDir == null
                    ? await Vault.ListRecursiveAsync( query.Trim( ), ct )
                    : await Vault.ListRecursiveAsync( null, ct );
                if( ct.IsCancellationRequested || !IsCurrentLoad( version ) ) return;

                HashSet<string>? inScope = scopeDir == null ? null : CollectSubtreeIds( scopeDir );
                var pathMap = BuildDirPathMap( );
                var items   = new List<VaultItemViewModel>( );

                // Folders whose names match (from the in-memory tree)
                foreach( var (node, parentId) in WalkFolders( ) )
                {
                    if( node.IsRoot || !match.IsMatch( node.Name ) ) continue;
                    if( inScope != null && ( parentId == null || !inScope.Contains( parentId ) ) ) continue;
                    var dir = new VaultDirectoryEntry( node.DirectoryId, node.Name, parentId, DateTime.MinValue );
                    var vm  = new VaultItemViewModel( dir );
                    if( parentId != null && pathMap.TryGetValue( parentId, out string? p ) ) vm.SubText = p;
                    WireItemNotifications( vm );
                    items.Add( vm );
                }

                foreach( var entry in files )
                {
                    if( inScope != null && !inScope.Contains( entry.DirectoryId ) ) continue;
                    if( ( match.IsPattern || scopeDir != null ) && !match.IsMatch( entry.Name ) ) continue;

                    var vm = new VaultItemViewModel( entry );
                    if( pathMap.TryGetValue( entry.DirectoryId, out string? path ) )
                        vm.SubText = path;
                    if( _checkedOutFiles.ContainsKey( entry.Id ) )
                        vm.IsCheckedOut = true;
                    WireItemNotifications( vm );
                    items.Add( vm );
                }

                ReplaceItems( GetSortedItems( items ) );

                int count = items.Count;
                string where = scopeDir == null ? "" : $" in \"{scopeDir.Name}\"";
                StatusMessage = count == 0
                    ? $"No results for \"{query}\"{where}"
                    : $"{count:N0} result{( count == 1 ? "" : "s" )} for \"{query}\"{where}";
            }
            catch( OperationCanceledException ) { }
            catch( Exception ex )
            {
                if( IsCurrentLoad( version ) ) StatusMessage = $"Search failed: {ex.Message}";
            }
            finally
            {
                EndLoad( version );
            }
        }

        private Dictionary<string, string> BuildDirPathMap( )
        {
            var map = new Dictionary<string, string>( );
            foreach( var root in RootNodes )
                WalkDirNode( root, string.Empty, map );
            return map;
        }

        private static void WalkDirNode(
            DirectoryNodeViewModel node,
            string parentPath,
            Dictionary<string, string> map )
        {
            string path = parentPath.Length == 0 ? node.Name : $"{parentPath} / {node.Name}";
            map [ node.DirectoryId ] = path;
            foreach( var child in node.Children )
                WalkDirNode( child, path, map );
        }

        // Sort state 

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( NameHeaderText ) )]
        [NotifyPropertyChangedFor( nameof( TypeHeaderText ) )]
        [NotifyPropertyChangedFor( nameof( SizeHeaderText ) )]
        [NotifyPropertyChangedFor( nameof( DateHeaderText ) )]
        public partial SortColumn SortColumn { get; set; } = SortColumn.Name;

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( NameHeaderText ) )]
        [NotifyPropertyChangedFor( nameof( TypeHeaderText ) )]
        [NotifyPropertyChangedFor( nameof( SizeHeaderText ) )]
        [NotifyPropertyChangedFor( nameof( DateHeaderText ) )]
        public partial bool SortDescending { get; set; }

        private string FormatHeader( string label, SortColumn col ) =>
            SortColumn == col ? $"{label} {( SortDescending ? "↓" : "↑" )}" : label;

        public string NameHeaderText => FormatHeader( "Name", SortColumn.Name );
        public string TypeHeaderText => FormatHeader( "Type", SortColumn.Type );
        public string SizeHeaderText => FormatHeader( "Size", SortColumn.Size );
        public string DateHeaderText => FormatHeader( "Date", SortColumn.Date );

        // Vault info 
        [ObservableProperty]
        public partial string VaultInfoText { get; set; } = string.Empty;

        public VaultBrowserViewModel( VaultService vaultService )
        {
            _vaultService = vaultService;
            InitializeShell( );

            // Notify commands when multi-selection changes
            SelectedItems.CollectionChanged += ( _, _ ) =>
            {
                if( _selectionBatch ) return;
                RenameCommand.NotifyCanExecuteChanged();
                DeleteCommand.NotifyCanExecuteChanged();
                ExtractCommand.NotifyCanExecuteChanged();
                MoveCommand.NotifyCanExecuteChanged();
            };
        }

        private void WireItemNotifications( VaultItemViewModel item )
        {
            item.PropertyChanged += ( _, args ) =>
            {
                if( args.PropertyName == nameof( VaultItemViewModel.IsCheckedOut ) )
                {
                    DeleteCommand.NotifyCanExecuteChanged( );
                    RenameCommand.NotifyCanExecuteChanged( );
                }
            };
        }

        /// <summary>
        /// Called by MainViewModel after a vault is opened.
        /// Loads the directory tree and selects the root.
        /// </summary>
        public async Task LoadAsync( bool isNewVault = false, CancellationToken cancellationToken = default )
        {
            RootNodes.Clear( );
            CurrentItems.Clear( );
            SelectedItems.Clear( );
            _backStack.Clear( );
            _forwardStack.Clear( );

            var rootNode = new DirectoryNodeViewModel(
                Vault.RootDirectoryId,
                "Vault",
                isRoot: true);

            await LoadThumbnailSettingsAsync( isNewVault, cancellationToken );
            await rootNode.LoadChildrenAsync( Vault, cancellationToken );
            RootNodes.Add( rootNode );
            OnPropertyChanged( nameof( FolderNodes ) );

            await NavigateToNodeAsync( rootNode, pushToBack: false, cancellationToken: cancellationToken );
            await RefreshVaultInfoAsync( cancellationToken );
        }

        public async Task OnDirectorySelectedAsync( DirectoryNodeViewModel node )
        {
            await NavigateToNodeAsync( node, pushToBack: true );
        }

        public async Task HandleDropAsync(
            List<string> filePaths,
            List<string> folderPaths,
            string targetDirectoryId )
        {
            int totalItems = filePaths.Count + folderPaths.Count;
            if( totalItems == 0 ) return;

            string noun = $"{totalItems:N0} item{( totalItems == 1 ? "" : "s" )}";
            var activity = new ActivityItem( $"Adding {noun}", FluentIcons.Common.Symbol.ArrowUpload );

            await Activities.RunAsync( activity,
                a => AddDroppedItemsAsync( filePaths, folderPaths, targetDirectoryId, a, a.CancellationToken ),
                _ => $"Added {noun}" );

            await AfterVaultChangedAsync( );
        }

        private async Task AddDroppedItemsAsync(
            List<string> filePaths,
            List<string> folderPaths,
            string targetDirectoryId,
            ActivityItem activity,
            CancellationToken cancellationToken )
        {
            if( filePaths.Count > 0 )
                await AddFilePathsAsync( filePaths, targetDirectoryId, activity, cancellationToken );

            foreach( string folderPath in folderPaths )
            {
                if( cancellationToken.IsCancellationRequested ) break;

                activity.CurrentFileName = Path.GetFileName(
                    folderPath.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) );

                await AddFolderPathAsync( folderPath, targetDirectoryId, activity, cancellationToken );
            }
        }

        partial void OnDragOverItemChanged( VaultItemViewModel? oldValue, VaultItemViewModel? newValue )
        {
            if( oldValue != null ) oldValue.IsDragTarget = false;
            if( newValue != null ) newValue.IsDragTarget = true;
        }

        partial void OnSelectedFileChanged( VaultItemViewModel? oldValue, VaultItemViewModel? newValue )
        {
            if( oldValue?.IsRenaming == true )
                oldValue.CancelRename( );
        }

        // Sort helpers 

        [RelayCommand]
        private void SortBy( string columnName )
        {
            var col = columnName switch
            {
                "Name" => SortColumn.Name,
                "Type" => SortColumn.Type,
                "Size" => SortColumn.Size,
                "Date" => SortColumn.Date,
                _      => SortColumn.Name
            };

            if( SortColumn == col )
                SortDescending = !SortDescending;
            else
            {
                SortColumn     = col;
                SortDescending = false;
            }

            ApplySortToCurrentItems( );
        }

        private void ApplySortToCurrentItems( )
        {
            var sorted = GetSortedItems([ .. CurrentItems ]).ToList();
            CurrentItems.Clear( );
            foreach( var item in sorted )
                CurrentItems.Add( item );
        }

        private IEnumerable<VaultItemViewModel> GetSortedItems( IList<VaultItemViewModel> items )
        {
            var parentDirs = items.Where(i => i.IsParentDir);
            var dirs       = SortGroup(items.Where(i => !i.IsParentDir && i.IsDir));
            var files      = SortGroup(items.Where(i => !i.IsParentDir && i.IsFile));
            return parentDirs.Concat( dirs ).Concat( files );
        }

        private IOrderedEnumerable<VaultItemViewModel> SortGroup( IEnumerable<VaultItemViewModel> group )
        {
            Func<VaultItemViewModel, IComparable> key = SortColumn switch
            {
                SortColumn.Type => v => v.TypeText,
                SortColumn.Size => v => (IComparable)( v.AsFile?.OriginalSize ?? 0L ),
                SortColumn.Date => v => (IComparable)( v.AsFile?.AddedAt
                                                        ?? ( v.AsDir?.CreatedAt
                                                        ?? DateTime.MinValue ) ),
                _ => v => v.DisplayName
            };

            return SortDescending
                ? group.OrderByDescending( key )
                : group.OrderBy( key );
        }

        // Item loading 

        private async Task LoadItemsAsync(
            string directoryId,
            CancellationToken cancellationToken = default )
        {
            int version = BeginLoad( );
            try
            {
                VaultListing listing = await Vault.ListAsync(directoryId, cancellationToken);
                if( !IsCurrentLoad( version ) ) return; // the user has moved on

                // No ".." row: the breadcrumb, Up button, and Backspace cover going up.
                var allItems = new List<VaultItemViewModel>();

                foreach( VaultDirectoryEntry dir in listing.Directories )
                {
                    var vm = new VaultItemViewModel(dir);
                    WireItemNotifications( vm );
                    allItems.Add( vm );
                }

                foreach( VaultEntry file in listing.Files )
                {
                    var vm = new VaultItemViewModel(file);

                    if( _checkedOutFiles.ContainsKey( file.Id ) )
                        vm.IsCheckedOut = true;

                    WireItemNotifications( vm );
                    allItems.Add( vm );
                }

                ReplaceItems( GetSortedItems( allItems ) );
                StatusMessage = string.Empty; // item count is shown on the right of the status bar
            }
            catch( OperationCanceledException ) { }
            catch( Exception ex )
            {
                if( IsCurrentLoad( version ) ) StatusMessage = $"Couldn't load this folder: {ex.Message}";
            }
            finally
            {
                EndLoad( version );
            }
        }

        public async Task NavigateToDirectoryAsync( string directoryId )
        {
            DirectoryNodeViewModel? node = FindNode(RootNodes, directoryId);
            if( node == null ) return;
            await NavigateToNodeAsync( node, pushToBack: true );
        }

        // Commands 

        [RelayCommand( CanExecute = nameof( CanOpen ) )]
        private async Task OpenAsync( CancellationToken cancellationToken )
        {
            if( SelectedFile == null ) return;

            if( SelectedFile.IsDir && SelectedFile.AsDir is { } dir )
                await NavigateToDirectoryAsync( dir.Id );
            else if( SelectedFile.IsFile )
                await OpenFileAsync( SelectedFile, cancellationToken );
        }

        private bool CanOpen => IsBrowserActive && SelectedFile != null && !SelectedFile.IsParentDir;

        public Action? FocusRenameBox { get; set; }

        [RelayCommand( CanExecute = nameof( CanRename ) )]
        private async Task RenameAsync( CancellationToken cancellationToken )
        {
            if( SelectedFile == null ) return;
            SelectedFile.BeginRename( );
            FocusRenameBox?.Invoke( );
        }

        private bool CanRename =>
            IsBrowserActive &&
            SelectedItems.Count == 1 &&
            !SelectedItems [ 0 ].IsParentDir &&
            !SelectedItems [ 0 ].IsCheckedOut;

        private bool CanExtract =>
            IsBrowserActive &&
            SelectedItems.Count > 0 &&
            SelectedItems.All( i => !i.IsParentDir );

        private bool CanDelete =>
            IsBrowserActive &&
            SelectedItems.Count > 0 &&
            SelectedItems.All( i => !i.IsParentDir && !i.IsCheckedOut );

        private bool CanMove =>
            IsBrowserActive &&
            SelectedItems.Count > 0 &&
            SelectedItems.All( i => !i.IsParentDir );

        private static DirectoryNodeViewModel? FindNode(
            IEnumerable<DirectoryNodeViewModel> nodes,
            string directoryId )
        {
            foreach( DirectoryNodeViewModel node in nodes )
            {
                if( node.DirectoryId == directoryId )
                    return node;

                DirectoryNodeViewModel? found = FindNode(node.Children, directoryId);
                if( found != null )
                    return found;
            }
            return null;
        }

        private async Task RefreshVaultInfoAsync( CancellationToken cancellationToken = default )
        {
            try
            {
                VaultInfo info = await Vault.GetVaultInfoAsync(cancellationToken);
                long reclaimable = await Vault.GetReclaimableBytesAsync( cancellationToken );
                UpdateStorageSummary( info, reclaimable );
                VaultInfoText = $"{info.TotalFiles} file{( info.TotalFiles == 1 ? "" : "s" )}  •  " +
                                $"{FormatSize( info.TotalOriginalSize )}  •  " +
                                $"Vault: {FormatSize( info.VaultFileSize )}";
            }
            catch { /* non-critical, swallow */ }
        }

        private DirectoryNodeViewModel? _currentDirectory;

        private async Task NavigateToNodeAsync(
            DirectoryNodeViewModel node,
            bool pushToBack = true,
            CancellationToken cancellationToken = default )
        {
            // New place starts at the top; going up/back brings the folder you came from into view
            if( ActiveSmartView == null && _currentDirectory != null && _currentDirectory.DirectoryId == node.DirectoryId )
                RequestScroll( ListScroll.Keep );
            else if( ActiveSmartView == null && ChildOnPathTowards( _currentDirectory, node ) is { } childId )
                RequestScroll( ListScroll.Item, childId );
            else
                RequestScroll( ListScroll.Top );

            // Cancel any in-flight search or library view and reset the search box silently
            ResetSearchSilently( );
            _viewCts?.Cancel( );
            ActiveDocument = null; // browsing always brings the Files tab forward
            bool leavingSmartView = ActiveSmartView != null;
            ActiveSmartView = null;
            if( leavingSmartView && SortColumn == SortColumn.Date && SortDescending )
            {
                SortColumn     = SortColumn.Name;
                SortDescending = false;
            }

            if( pushToBack && _currentDirectory != null )
            {
                _backStack.Push( _currentDirectory );
                _forwardStack.Clear( );
            }

            _currentDirectory  = node;
            SelectedDirectory  = node;
            CanNavigateBack    = _backStack.Count > 0;
            CanNavigateForward = _forwardStack.Count > 0;

            // Breadcrumb MUST be updated before LoadItemsAsync so the ".." entry
            // can read Breadcrumb[^2] for the parent directory ID.
            UpdateBreadcrumb( node );
            SyncSidebarState( );
            await LoadItemsAsync( node.DirectoryId, cancellationToken );
        }

        private void UpdateBreadcrumb( DirectoryNodeViewModel node )
        {
            Breadcrumb.Clear( );

            List<BreadcrumbSegment> segments = [];
            DirectoryNodeViewModel? current  = node;

            while( current != null )
            {
                segments.Insert( 0, new BreadcrumbSegment( current.DirectoryId, current.Name ) );
                current = FindParentNode( RootNodes, current.DirectoryId );
            }

            for( int i = 0; i < segments.Count; i++ )
                Breadcrumb.Add( segments [ i ] with { IsFirst = i == 0 } );
        }

        private static DirectoryNodeViewModel? FindParentNode(
            IEnumerable<DirectoryNodeViewModel> nodes,
            string childId,
            DirectoryNodeViewModel? _ = null )
        {
            foreach( DirectoryNodeViewModel node in nodes )
            {
                if( node.Children.Any( c => c.DirectoryId == childId ) )
                    return node;

                DirectoryNodeViewModel? found = FindParentNode(node.Children, childId, node);
                if( found != null ) return found;
            }
            return null;
        }

        [RelayCommand]
        private async Task CheckIntegrityAsync( CancellationToken cancellationToken )
        {
            if( OwnerWindow == null ) return;

            IReadOnlyList<string> issues = [ ];
            var activity = new ActivityItem( "Checking vault integrity", FluentIcons.Common.Symbol.ShieldCheckmark )
            {
                CurrentFileName = "Checking every file's chunks",
                StatusText      = "Runs on its own connection, so you can keep browsing"
            };

            await Activities.RunAsync( activity,
                async a => issues = await Task.Run( ( ) => Vault.CheckIntegrityAsync(
                    new ThrottledProgress( p => a.Progress = p ), a.CancellationToken ), a.CancellationToken ),
                _ => issues.Count == 0 ? "No problems found" : $"{issues.Count} problem{( issues.Count == 1 ? "" : "s" )} found" );

            if( activity.State != ActivityState.Completed || OwnerWindow == null ) return;

            var vm = issues.Count == 0
                ? new ConfirmDialogViewModel
                {
                    Title       = "Integrity check",
                    Message     = "Your vault is healthy",
                    Detail      = "Every file has the chunks it expects, and nothing is orphaned.",
                    ConfirmText = "Done",
                    CancelText  = "",
                    Tone        = DialogTone.Success
                }
                : new ConfirmDialogViewModel
                {
                    Title       = "Integrity check",
                    Message     = $"{issues.Count} problem{( issues.Count == 1 ? "" : "s" )} found",
                    Detail      = string.Join( "\n", issues.Select( i => $"• {i}" ) ),
                    ConfirmText = "Close",
                    CancelText  = "",
                    Tone        = DialogTone.Warning
                };

            await new ConfirmDialog { DataContext = vm }.ShowDialog( OwnerWindow );
            await AfterVaultChangedAsync( );
        }

        [RelayCommand( CanExecute = nameof( IsFolderView ), AllowConcurrentExecutions = true )]
        private async Task AddFilesAsync( CancellationToken cancellationToken )
        {
            if( _currentDirectory == null || OwnerWindow == null ) return;

            var files = await OwnerWindow.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title         = "Add files to the vault",
                    AllowMultiple = true
                });

            if( files.Count == 0 ) return;

            List<string> paths = [ .. files.Select(f => f.Path.LocalPath) ];
            string targetId    = _currentDirectory.DirectoryId;
            string noun        = $"{paths.Count} file{( paths.Count == 1 ? "" : "s" )}";

            var activity = new ActivityItem( $"Adding {noun}", FluentIcons.Common.Symbol.ArrowUpload );
            await Activities.RunAsync( activity,
                a => AddFilePathsAsync( paths, targetId, a, a.CancellationToken ),
                _ => $"Added {noun}" );

            await ReloadCurrentViewAsync( );
            await RefreshVaultInfoAsync( );
        }

        [RelayCommand( CanExecute = nameof( IsFolderView ), AllowConcurrentExecutions = true )]
        private async Task AddFolderAsync( CancellationToken cancellationToken )
        {
            if( _currentDirectory == null || OwnerWindow == null ) return;

            var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title         = "Choose a folder to add",
                    AllowMultiple = false
                });

            if( folders.Count == 0 ) return;

            string sourcePath = folders[0].Path.LocalPath
                .TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );

            FolderManifest manifest;
            try
            {
                manifest = FolderScanner.Scan( sourcePath );
            }
            catch( Exception ex )
            {
                StatusMessage = $"Couldn't read that folder: {ex.Message}";
                return;
            }

            var confirmVm  = new AddFolderConfirmViewModel(manifest);
            bool? confirmed = await new AddFolderConfirmDialog { DataContext = confirmVm }
                .ShowDialog<bool?>( OwnerWindow );

            if( confirmed != true ) return;

            string targetId = _currentDirectory.DirectoryId;
            var activity = new ActivityItem( $"Adding \"{manifest.RootName}\"", FluentIcons.Common.Symbol.FolderArrowUp );
            await Activities.RunAsync( activity,
                a => AddFolderToVaultAsync( manifest, targetId, a, a.CancellationToken ),
                _ => $"Added \"{manifest.RootName}\"" );

            await AfterVaultChangedAsync( );
        }

        // Shared core add methods 

        private async Task AddFilePathsAsync(
            IEnumerable<string> filePaths,
            string targetDirectoryId,
            ActivityItem progressVm,
            CancellationToken cancellationToken )
        {
            List<string> paths     = [ .. filePaths ];
            int          total     = paths.Count;
            int          completed = 0;
            long         lastTick  = 0;
            const long   minTicks  = 8_000_000L; // 80 ms

            foreach( string path in paths )
            {
                if( cancellationToken.IsCancellationRequested ) break;

                string name = Path.GetFileName(path);
                int    snap = completed;

                // Throttle per-file UI updates - avoid flooding the dispatcher
                long now = DateTime.UtcNow.Ticks;
                if( now - lastTick >= minTicks )
                {
                    lastTick = now;
                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                    {
                        progressVm.CurrentFileName = name;
                        progressVm.StatusText      = $"{snap + 1} of {total}";
                        progressVm.Progress        = (double)snap / total;
                    } );
                }

                try
                {
                    // ThrottledProgress fires at most every 80 ms at the call site,
                    // so no redundant Post calls accumulate on the dispatcher queue.
                    IProgress<double> fileProgress = new ThrottledProgress(
                        p => progressVm.Progress = ( snap + p ) / total );

                    await Vault.AddAsync(
                        path,
                        targetDirectoryId,
                        fileProgress,
                        cancellationToken );

                    completed++;
                }
                catch( OperationCanceledException )
                {
                    break;
                }
                catch( ObjectDisposedException ) { throw; }
                catch( Exception ex )
                {
                    progressVm.AddFailure( name, ex.Message );
                }
            }
        }

        private async Task AddFolderPathAsync(
            string folderPath,
            string targetDirectoryId,
            ActivityItem progressVm,
            CancellationToken cancellationToken )
        {
            string trimmed = folderPath
                .TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );

            FolderManifest manifest = FolderScanner.Scan(trimmed);

            await AddFolderToVaultAsync(
                manifest,
                targetDirectoryId,
                progressVm,
                cancellationToken );
        }

        private async Task AddFolderToVaultAsync(
            FolderManifest manifest,
            string parentDirectoryId,
            ActivityItem progressVm,
            CancellationToken cancellationToken )
        {
            Dictionary<string, string> dirIdMap = new()
            {
                [ string.Empty ] = await GetOrCreateDirectoryAsync(
                    manifest.RootName, parentDirectoryId, cancellationToken)
            };

            var sortedFolders = manifest.Folders
                .OrderBy( f => f.RelativePath.Count( c => c == Path.DirectorySeparatorChar ) );

            foreach( FolderEntry folder in sortedFolders )
            {
                if( cancellationToken.IsCancellationRequested ) break;

                string parentPath    = folder.ParentPath;
                string parentVaultId = dirIdMap.TryGetValue( parentPath, out string? pid )
                    ? pid
                    : dirIdMap [ string.Empty ];

                string newDirId = await GetOrCreateDirectoryAsync(
                    folder.Name, parentVaultId, cancellationToken);

                dirIdMap [ folder.RelativePath ] = newDirId;
            }

            int  total    = manifest.Files.Count;
            int  completed = 0;
            long lastTick  = 0;
            const long minTicks = 8_000_000L; // 80 ms

            foreach( FileEntry file in manifest.Files )
            {
                if( cancellationToken.IsCancellationRequested ) break;

                string folderVaultId = dirIdMap.TryGetValue( file.FolderPath, out string? fid )
                    ? fid
                    : dirIdMap [ string.Empty ];

                string name = file.Name;
                int    snap = completed;

                long now = DateTime.UtcNow.Ticks;
                if( now - lastTick >= minTicks )
                {
                    lastTick = now;
                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                    {
                        progressVm.CurrentFileName = name;
                        progressVm.StatusText      = $"{snap + 1} of {total}";
                        progressVm.Progress        = (double)snap / total;
                    } );
                }

                IProgress<double> fileProgress = new ThrottledProgress(
                    p => progressVm.Progress = ( snap + p ) / total );

                await Vault.AddAsync(
                    file.FullPath,
                    folderVaultId,
                    fileProgress,
                    cancellationToken );

                completed++;
            }
        }

        private async Task<string> GetOrCreateDirectoryAsync(
            string name,
            string parentId,
            CancellationToken cancellationToken )
        {
            VaultListing listing = await Vault.ListAsync(parentId, cancellationToken);
            VaultDirectoryEntry? existing = listing.Directories
                .FirstOrDefault( d => string.Equals( d.Name, name,
                    StringComparison.OrdinalIgnoreCase ) );

            if( existing != null )
                return existing.Id;

            return await Vault.CreateDirectoryAsync( name, parentId, cancellationToken );
        }

        private async Task ReloadTreeAsync( CancellationToken cancellationToken = default )
        {
            string currentId = _currentDirectory?.DirectoryId ?? Vault.RootDirectoryId;

            RootNodes.Clear( );
            var rootNode = new DirectoryNodeViewModel(
                Vault.RootDirectoryId, "Vault", isRoot: true);
            await rootNode.LoadChildrenAsync( Vault, cancellationToken );
            RootNodes.Add( rootNode );
            OnPropertyChanged( nameof( FolderNodes ) );

            DirectoryNodeViewModel? restored = FindNode(RootNodes, currentId)
                ?? rootNode;

            if( ActiveSmartView != null || IsSearchActive )
            {
                // Stay in the library view or search; just re-point the folder state at the new tree
                _currentDirectory = restored;
                SelectedDirectory = restored;
                UpdateBreadcrumb( restored );
                SyncSidebarState( );
                if( IsSearchActive )
                {
                    _searchCts?.Cancel( );
                    _searchCts = new CancellationTokenSource( );
                    await RunSearchAsync( SearchText, _searchCts.Token );
                }
                else
                {
                    await ReloadCurrentViewAsync( cancellationToken );
                }
                return;
            }

            await NavigateToNodeAsync( restored, pushToBack: false, cancellationToken: cancellationToken );
        }

        // Extract 

        [RelayCommand( CanExecute = nameof( CanExtract ), AllowConcurrentExecutions = true )]
        private async Task ExtractAsync( CancellationToken cancellationToken )
        {
            if( OwnerWindow == null ) return;

            var items = SelectedItems.Where(i => !i.IsParentDir).ToList();
            if( items.Count == 0 ) return;

            var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title         = "Extract to",
                    AllowMultiple = false
                });

            if( folders.Count == 0 ) return;

            string outputPath = folders[0].Path.LocalPath;
            string title = items.Count == 1
                ? $"Extracting \"{items[0].DisplayName}\""
                : $"Extracting {items.Count} items";

            var activity = new ActivityItem( title, FluentIcons.Common.Symbol.ArrowDownload );
            await Activities.RunAsync( activity,
                a => Task.Run( ( ) => ExtractItemsAsync( items, outputPath, a ) ),
                _ => items.Count == 1 ? $"Extracted \"{items[0].DisplayName}\"" : $"Extracted {items.Count} items" );

            if( activity.State == ActivityState.Completed )
                StatusMessage = $"Extracted to {outputPath}";
        }

        /// <summary>Runs on a worker thread; UI updates are posted back to the dispatcher.</summary>
        private async Task ExtractItemsAsync( List<VaultItemViewModel> items, string outputPath, ActivityItem activity )
        {
            int total     = items.Count;
            int completed = 0;
            CancellationToken ct = activity.CancellationToken;

            foreach( var item in items )
            {
                if( ct.IsCancellationRequested ) break;

                int snap = completed;
                Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                {
                    activity.CurrentFileName = item.DisplayName;
                    activity.StatusText      = total > 1 ? $"{snap + 1} of {total}" : string.Empty;
                } );

                IProgress<double> progress = new ThrottledProgress( p => activity.Progress = ( snap + p ) / total );

                try
                {
                    if( item.AsFile is VaultEntry file )
                        await Vault.ExtractAsync( file.Id, Path.Combine( outputPath, file.Name ), progress, ct );
                    else if( item.AsDir is VaultDirectoryEntry dir )
                        await Vault.ExtractDirectoryAsync( dir.Id, outputPath, progress, ct );
                }
                catch( OperationCanceledException ) { break; }
                catch( ObjectDisposedException ) { throw; }
                catch( Exception ex )
                {
                    string msg = ex.Message;
                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) => activity.AddFailure( item.DisplayName, msg ) );
                }

                completed++;
                int done = completed;
                Avalonia.Threading.Dispatcher.UIThread.Post( ( ) => activity.Progress = (double)done / total );
            }

            // Let the posted failure/progress updates land before the activity is finalized
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync( ( ) => { } );
        }

        // Delete 

        [RelayCommand( CanExecute = nameof( CanDelete ), AllowConcurrentExecutions = true )]
        private async Task DeleteAsync( CancellationToken cancellationToken )
        {
            if( OwnerWindow == null ) return;

            var items = SelectedItems.Where(i => !i.IsParentDir && !i.IsCheckedOut).ToList();
            if( items.Count == 0 ) return;

            string message = items.Count == 1
                ? $"Delete \"{items [ 0 ].DisplayName}\"?"
                : $"Delete {items.Count} items?";

            string detail = items.Count == 1
                ? items [ 0 ].IsDir
                    ? "The folder and everything in it will be permanently deleted. This can't be undone."
                    : "The file will be permanently deleted. This can't be undone."
                : "These will be permanently deleted. This can't be undone.\n\n" +
                  string.Join( "\n", items.Take( 8 ).Select( i => $"• {i.DisplayName}" ) ) +
                  ( items.Count > 8 ? $"\n• and {items.Count - 8} more" : string.Empty );

            var confirmVm = new ConfirmDialogViewModel
            {
                Title       = "Delete",
                Message     = message,
                Detail      = detail,
                ConfirmText = "Delete",
                CancelText  = "Cancel",
                Tone        = DialogTone.Danger
            };

            bool? confirmed = await new ConfirmDialog { DataContext = confirmVm }
                .ShowDialog<bool?>( OwnerWindow );

            if( confirmed != true ) return;

            string title = items.Count == 1 ? $"Deleting \"{items[0].DisplayName}\"" : $"Deleting {items.Count} items";
            var activity = new ActivityItem( title, FluentIcons.Common.Symbol.Delete );

            await Activities.RunAsync( activity,
                a => Task.Run( ( ) => DeleteItemsAsync( items, a ) ),
                _ => items.Count == 1 ? $"Deleted \"{items[0].DisplayName}\"" : $"Deleted {items.Count} items" );

            await RefreshVaultInfoAsync( );
        }

        /// <summary>Runs on a worker thread. Each item is its own transaction; cancelling stops between items.</summary>
        private async Task DeleteItemsAsync( List<VaultItemViewModel> items, ActivityItem activity )
        {
            int total     = items.Count;
            int completed = 0;
            CancellationToken ct = activity.CancellationToken;

            foreach( var item in items )
            {
                if( ct.IsCancellationRequested ) break;

                int snap = completed;
                Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                {
                    activity.CurrentFileName = item.DisplayName;
                    activity.StatusText      = total > 1 ? $"{snap + 1} of {total}" : string.Empty;
                } );

                IProgress<double> progress = new ThrottledProgress( p => activity.Progress = ( snap + p ) / total );

                try
                {
                    // Not cancellable mid-item: each item is one transaction
                    if( item.AsFile is VaultEntry file )
                        await Vault.RemoveAsync( file.Id, progress, CancellationToken.None );
                    else if( item.AsDir is VaultDirectoryEntry dir )
                        await Vault.RemoveDirectoryAsync( dir.Id, progress, CancellationToken.None );

                    completed++;
                    int done = completed;
                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                    {
                        CurrentItems.Remove( item );
                        SelectedItems.Remove( item );
                        if( ReferenceEquals( SelectedFile, item ) ) SelectedFile = null;
                        if( item.AsDir is VaultDirectoryEntry dirEntry )
                            RemoveNodeFromTree( RootNodes, dirEntry.Id );
                        activity.Progress = (double)done / total;
                    } );
                }
                catch( ObjectDisposedException ) { throw; }
                catch( Exception ex )
                {
                    string msg = ex.Message;
                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) => activity.AddFailure( item.DisplayName, msg ) );
                }
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync( ( ) => { } );
        }

        private static bool RemoveNodeFromTree(
            ObservableCollection<DirectoryNodeViewModel> nodes,
            string directoryId )
        {
            for( int i = 0; i < nodes.Count; i++ )
            {
                if( nodes [ i ].DirectoryId == directoryId )
                {
                    nodes.RemoveAt( i );
                    return true;
                }
                if( RemoveNodeFromTree( nodes [ i ].Children, directoryId ) )
                    return true;
            }
            return false;
        }

        // Move 

        [RelayCommand( CanExecute = nameof( CanMove ) )]
        private async Task MoveAsync( CancellationToken ct )
        {
            if( OwnerWindow == null ) return;

            var ids = SelectedItems
                .Where(i => !i.IsParentDir)
                .Select(i => i.Item.Id)
                .ToList();

            if( ids.Count == 0 ) return;

            var vm     = new MoveDialogViewModel(Vault, ids);
            var dialog = new MoveDialog { DataContext = vm };
            bool? result = await dialog.ShowDialog<bool?>( OwnerWindow );

            if( result != true || vm.ConfirmedTargetId == null ) return;

            await MoveItemsAsync( ids, vm.ConfirmedTargetId, ct );
        }

        /// <summary>
        /// Moves the specified vault items to the target directory.
        /// Used both by the Move dialog and by internal drag-drop.
        /// </summary>
        public async Task MoveItemsAsync(
            IEnumerable<string> itemIds,
            string targetDirId,
            CancellationToken ct = default )
        {
            var ids    = itemIds.ToList();
            var errors = new List<string>();

            foreach( string id in ids )
            {
                if( ct.IsCancellationRequested ) break;
                try
                {
                    await Vault.MoveAsync( id, targetDirId, ct );
                }
                catch( VaultOperationException ex )
                {
                    errors.Add( ex.Message );
                }
            }

            if( errors.Count > 0 && OwnerWindow != null )
            {
                var errVm = new ConfirmDialogViewModel
                {
                    Title       = "Move - Partial Failure",
                    Message     = $"{errors.Count} item{( errors.Count == 1 ? "" : "s" )} could not be moved.",
                    Detail      = string.Join( "\n", errors ),
                    ConfirmText = "OK",
                    CancelText  = ""
                };
                await new ConfirmDialog { DataContext = errVm }.ShowDialog( OwnerWindow );
            }

            SelectedItems.Clear( );
            await ReloadTreeAsync( ct );
            await RefreshVaultInfoAsync( ct );
        }

        // New Folder 

        [RelayCommand( CanExecute = nameof( IsFolderView ) )]
        private async Task NewFolderAsync( CancellationToken cancellationToken )
        {
            if( SelectedDirectory == null || OwnerWindow == null ) return;

            var vm = new InputDialogViewModel
            {
                Title       = "New folder",
                Prompt      = "It's created inside the folder you're viewing.",
                Placeholder = "Folder name",
                ConfirmText = "Create",
                Icon        = FluentIcons.Common.Symbol.FolderAdd,
                Validator   = ValidateItemName
            };

            var dialog = new InputDialog { DataContext = vm };
            string? folderName = await dialog.ShowDialog<string?>( OwnerWindow );

            if( folderName == null ) return;

            try
            {
                IsBusy = true;
                string newDirId = await Vault.CreateDirectoryAsync(
                    folderName,
                    SelectedDirectory.DirectoryId,
                    cancellationToken);

                var newNode = new DirectoryNodeViewModel(newDirId, folderName);
                SelectedDirectory.Children.Add( newNode );
                SelectedDirectory.IsExpanded = true;

                await LoadItemsAsync( SelectedDirectory.DirectoryId, cancellationToken );
                await RefreshVaultInfoAsync( cancellationToken );

                StatusMessage = $"Folder '{folderName}' created.";
            }
            catch( VaultOperationException ex )
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            catch( Exception ex )
            {
                StatusMessage = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Rename 

        public async Task CommitRenameAsync(
            VaultItemViewModel item,
            CancellationToken cancellationToken = default )
        {
            string newName = item.EditingName.Trim();

            string? error = ValidateItemName(newName);
            if( error != null || newName == item.DisplayName )
            {
                item.CancelRename( );
                return;
            }

            try
            {
                await Vault.RenameAsync( item.Id, newName, cancellationToken );
                item.DisplayName = newName;
                item.CancelRename( );

                if( item.IsDir )
                {
                    DirectoryNodeViewModel? node = FindNode(RootNodes, item.Id);
                    node?.Name = newName;
                }

                StatusMessage = $"Renamed to \"{newName}\".";
            }
            catch( Exception ex )
            {
                item.CancelRename( );
                StatusMessage = $"Error renaming: {ex.Message}";
            }
        }

        // Compact 

        [RelayCommand]
        private async Task CompactAsync( CancellationToken cancellationToken )
        {
            if( OwnerWindow == null ) return;

            long reclaimable = await Vault.GetReclaimableBytesAsync( cancellationToken );

            var vm = new CompactDialogViewModel( reclaimable );
            bool? result = await new CompactDialog { DataContext = vm }.ShowDialog<bool?>( OwnerWindow );
            if( result != true ) return;

            var activity = new ActivityItem( "Compacting vault", FluentIcons.Common.Symbol.Broom );
            await Activities.RunAsync( activity,
                a => Task.Run( ( ) => Vault.CompactAsync(
                    progress: new ThrottledProgress( p => a.Progress = p ),
                    cancellationToken: a.CancellationToken ) ),
                _ => $"Reclaimed about {Converters.FileSizeConverter.Format( reclaimable )}" );

            await RefreshVaultInfoAsync( );
        }

        // Navigation 

        [RelayCommand( CanExecute = nameof( CanNavigateBack ) )]
        private async Task NavigateBackAsync( CancellationToken cancellationToken )
        {
            // From a library view, Back returns to the folder you were in
            if( ActiveSmartView != null && _currentDirectory != null )
            {
                await NavigateToNodeAsync( _currentDirectory, pushToBack: false, cancellationToken: cancellationToken );
                return;
            }

            if( _backStack.Count == 0 ) return;

            DirectoryNodeViewModel current  = _currentDirectory!;
            _forwardStack.Push( current );

            DirectoryNodeViewModel previous = _backStack.Pop();
            await NavigateToNodeAsync( previous, pushToBack: false, cancellationToken: cancellationToken );
        }

        [RelayCommand( CanExecute = nameof( CanNavigateForward ) )]
        private async Task NavigateForwardAsync( CancellationToken cancellationToken )
        {
            if( _forwardStack.Count == 0 ) return;

            DirectoryNodeViewModel current = _currentDirectory!;
            _backStack.Push( current );

            DirectoryNodeViewModel next = _forwardStack.Pop();
            await NavigateToNodeAsync( next, pushToBack: false, cancellationToken: cancellationToken );
        }

        [RelayCommand]
        private async Task NavigateToBreadcrumbAsync(
            BreadcrumbSegment segment,
            CancellationToken cancellationToken = default )
        {
            if( ActiveSmartView == null && segment.DirectoryId == _currentDirectory?.DirectoryId ) return;

            DirectoryNodeViewModel? node = FindNode(RootNodes, segment.DirectoryId);
            if( node == null ) return;

            await NavigateToNodeAsync( node, pushToBack: true, cancellationToken: cancellationToken );
        }

        // Helpers 

        public static string? ValidateItemName( string name )
        {
            if( string.IsNullOrWhiteSpace( name ) )
                return "Name cannot be empty.";

            char[] invalidChars = [ .. Path.GetInvalidFileNameChars(), '/', '\\' ];
            char[] found        = [ .. name.Where( c => invalidChars.Contains( c ) ).Distinct() ];
            if( found.Length > 0 )
                return $"Name contains invalid characters: {string.Join( " ", found )}";

            string[] reserved =
            [
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT8", "LPT9"
            ];
            if( reserved.Contains( name.Trim( ).ToUpperInvariant( ) ) )
                return $"\"{name}\" is a reserved name and cannot be used.";

            return null;
        }

        private static string FormatSize( long bytes ) => bytes switch
        {
            < 1024             => $"{bytes} B",
            < 1024 * 1024      => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / ( 1024.0 * 1024 ):F1} MB",
            _                  => $"{bytes / ( 1024.0 * 1024 * 1024 ):F2} GB"
        };

        // Internal viewer toggle 

        /// <summary>
        /// When <c>true</c> (the default), compatible media files open in the built-in
        /// LibVLC player with on-the-fly decryption. When <c>false</c>, all files are
        /// decrypted to a temp directory and opened with the system default app.
        /// Persisted via <see cref="App.SettingsService"/> on change.
        /// </summary>
        [ObservableProperty]
        public partial bool UseInternalViewer { get; set; } = App.SettingsService.Settings.UseInternalViewer;

        partial void OnUseInternalViewerChanged( bool value )
        {
            App.SettingsService.Settings.UseInternalViewer = value;
            App.SettingsService.Save( );
        }

        // Checked-out file tracking 

        private readonly Dictionary<string, CheckedOutFile> _checkedOutFiles = [ ];
        /// <summary>Guards against double-open races while extraction is in progress.</summary>
        private readonly HashSet<string> _openingFiles = [ ];

        public async Task OpenFileAsync(
            VaultItemViewModel item,
            CancellationToken cancellationToken = default )
        {
            if( item.AsFile == null || OwnerWindow == null ) return;

            // Route to the appropriate viewer 
            var viewerType = FileTypeHelper.Classify( item.DisplayName );

            if( UseInternalViewer )
            {
                switch( viewerType )
                {
                    case FileViewerType.Media:
                        await OpenInMediaPlayerAsync( item );
                        return;

                    case FileViewerType.Image:
                        await OpenInImageViewerAsync( item );
                        return;

                    case FileViewerType.Text:
                        await OpenInTextEditorAsync( item );
                        return;

                    case FileViewerType.External:
                        // Unknown type - let the user pick a viewer
                        var choice = await new Views.Dialogs.OpenWithDialog( item.DisplayName )
                            .ShowDialog<OpenWithChoice>( OwnerWindow );

                        switch( choice )
                        {
                            case OpenWithChoice.MediaPlayer:
                                await OpenInMediaPlayerAsync( item );
                                return;
                            case OpenWithChoice.ImageViewer:
                                await OpenInImageViewerAsync( item );
                                return;
                            case OpenWithChoice.TextEditor:
                                await OpenInTextEditorAsync( item );
                                return;
                            case OpenWithChoice.External:
                                break;   // fall through to the external open path below
                            default:
                                return;  // cancelled
                        }
                        break;
                }
            }

            // External / extract-to-temp path 

            string fileId = item.Id;

            // Re-focus an already-open file without re-extracting
            if( _checkedOutFiles.TryGetValue( fileId, out CheckedOutFile? existing ) )
            {
                try
                {
                    Process.Start( new ProcessStartInfo( existing.TempPath )
                    {
                        UseShellExecute = true
                    } );
                }
                catch( Exception ex )
                {
                    StatusMessage = $"Failed to open file: {ex.Message}";
                }
                return;
            }

            // Prevent a double-click race from starting two concurrent extractions
            if( !_openingFiles.Add( fileId ) ) return;

            // Use a per-session GUID directory so a stuck/orphaned temp file from a
            // previous failed cleanup never blocks a fresh extraction of the same vault file.
            string tempDir  = Path.Combine(App.TempDirectory, Guid.NewGuid().ToString());
            string tempPath = Path.Combine(tempDir, item.DisplayName);

            Directory.CreateDirectory( tempDir );

            var activity = new ActivityItem( $"Opening \"{item.DisplayName}\"", FluentIcons.Common.Symbol.Open )
            {
                CurrentFileName = item.DisplayName,
                StatusText      = "Decrypting to a temporary copy"
            };

            string fileToOpen = item.AsFile.Id;
            await Activities.RunAsync( activity,
                a => ExtractToTempAsync( fileToOpen, tempPath, a, a.CancellationToken ),
                _ => $"Opened \"{item.DisplayName}\"" );

            bool success = activity.State == ActivityState.Completed;

            if( !success )
            {
                _openingFiles.Remove( fileId );
                // Clean up any partial extraction
                try
                {
                    if( Directory.Exists( tempDir ) )
                        Directory.Delete( tempDir, recursive: true );
                }
                catch { }
                return;
            }

            byte[] storedSha256 = await Vault.GetFileSha256Async( item.AsFile.Id, cancellationToken );
            string sha256Hex    = Convert.ToHexString( storedSha256 );

            var cts        = new CancellationTokenSource();
            var checkedOut = new CheckedOutFile(fileId, tempPath, sha256Hex, cts);
            _checkedOutFiles [ fileId ] = checkedOut;
            item.IsCheckedOut = true;
            // Remove AFTER _checkedOutFiles registration so a re-click hits the re-focus path
            _openingFiles.Remove( fileId );

            try
            {
                Process.Start( new ProcessStartInfo( tempPath )
                {
                    UseShellExecute = true
                } );
            }
            catch( Exception ex )
            {
                StatusMessage = $"Failed to open file: {ex.Message}";
                _checkedOutFiles.Remove( fileId );
                item.IsCheckedOut = false;
                cts.Dispose( );
                return;
            }

            _ = WatchForReleaseAsync( item, checkedOut, cts.Token );
        }




        private async Task ExtractToTempAsync(
            string fileId,
            string tempPath,
            ActivityItem progressVm,
            CancellationToken cancellationToken )
        {
            IProgress<double> progress = new Progress<double>( p => progressVm.Progress = p );
            await Vault.ExtractAsync( fileId, tempPath, progress, cancellationToken );
        }

        private VaultItemViewModel? FindCurrentItem( string fileId ) =>
            CurrentItems.FirstOrDefault( i => i.Id == fileId );

        private async Task WatchForReleaseAsync(
            VaultItemViewModel item,
            CheckedOutFile checkedOut,
            CancellationToken cancellationToken )
        {
            try
            {
                while( !cancellationToken.IsCancellationRequested )
                {
                    await Task.Delay( 2000, cancellationToken ).ConfigureAwait( false );

                    if( !File.Exists( checkedOut.TempPath ) )
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync( ( ) =>
                        {
                            var current = FindCurrentItem( checkedOut.FileId ) ?? item;
                            CleanupCheckedOutFile( current, checkedOut, deleted: true );
                        } );
                        return; // file was deleted externally - nothing more to do
                    }

                    if( !OperatingSystem.IsWindows( ) )
                    {
                        await CheckForSavedChangesAsync( item, checkedOut, cancellationToken ).ConfigureAwait( false );
                        continue;
                    }

                    FileStream? fs = null;
                    try
                    {
                        fs = new FileStream(
                            checkedOut.TempPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.None );

                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync( async ( ) =>
                        {
                            var current = FindCurrentItem( checkedOut.FileId ) ?? item;
                            await HandleFileReleasedAsync( current, checkedOut, fs );
                        } );
                    }
                    catch( IOException )
                    {
                        // File still in use - try again next poll cycle
                        fs?.Dispose( );
                    }
                }
            }
            catch( OperationCanceledException ) { }
        }

        private async Task HandleFileReleasedAsync(
            VaultItemViewModel item,
            CheckedOutFile checkedOut,
            FileStream stream )
        {
            bool doCleanup = true;
            try
            {
                byte[] currentSha256 = await ComputeSha256Async(stream, CancellationToken.None);
                stream.Dispose( );

                string currentHex = Convert.ToHexString(currentSha256);

                if( currentHex != checkedOut.Sha256Hex )
                {
                    // File was modified - show save dialog, then fall through to cleanup.
                    await PromptSaveChangesAsync( item, checkedOut );
                }
                else
                {
                    // File not modified - confirm the release is genuine by attempting to
                    // delete the temp file. Media players (e.g. VLC) may briefly release
                    // the OS handle between read cycles while still "using" the file, making
                    // FileShare.None succeed as a false positive. If deletion fails, the
                    // external process has re-acquired the file; bail without clearing the
                    // lock so the watcher retries on the next cycle.
                    try
                    {
                        File.Delete( checkedOut.TempPath );
                    }
                    catch( IOException )
                    {
                        doCleanup = false;
                        return;
                    }
                }
            }
            catch( Exception ex )
            {
                stream.Dispose( );
                StatusMessage = $"Error checking for changes: {ex.Message}";
            }
            finally
            {
                if( doCleanup )
                    CleanupCheckedOutFile( item, checkedOut, deleted: false );
            }
        }

        /// <summary>
        /// Non-Windows change detection. File locks there are advisory, so FileShare.None
        /// never fails and there is no reliable "the external app let go" signal - the
        /// Windows release check would fire on the first poll and delete the temp file out
        /// from under the app. Instead, watch for saves: once the temp file's write time has
        /// changed and stayed put for a full poll cycle, offer to save it back to the vault.
        /// The temp copy stays checked out until it is deleted or the vault is closed.
        /// </summary>
        private async Task CheckForSavedChangesAsync(
            VaultItemViewModel item,
            CheckedOutFile checkedOut,
            CancellationToken cancellationToken )
        {
            try
            {
                DateTime writeTime = File.GetLastWriteTimeUtc( checkedOut.TempPath );

                if( writeTime == checkedOut.LastWriteUtc )
                {
                    checkedOut.PendingWriteUtc = null;
                    return;
                }

                // Wait one more cycle so we don't hash a file that is still being written
                if( checkedOut.PendingWriteUtc != writeTime )
                {
                    checkedOut.PendingWriteUtc = writeTime;
                    return;
                }

                checkedOut.LastWriteUtc    = writeTime;
                checkedOut.PendingWriteUtc = null;

                string currentHex;
                await using( FileStream fs = new(
                    checkedOut.TempPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete ) )
                {
                    currentHex = Convert.ToHexString( await ComputeSha256Async( fs, cancellationToken ) );
                }

                if( currentHex == checkedOut.Sha256Hex )
                    return;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync( async ( ) =>
                {
                    var current = FindCurrentItem( checkedOut.FileId ) ?? item;
                    await PromptSaveChangesAsync( current, checkedOut );
                } );

                // Saved or discarded, don't prompt again for this same content
                checkedOut.Sha256Hex = currentHex;
            }
            catch( IOException )
            {
                // Mid-save rename or similar - try again next poll cycle
            }
        }

        private async Task PromptSaveChangesAsync(
            VaultItemViewModel item,
            CheckedOutFile checkedOut )
        {
            var confirmVm = new ConfirmDialogViewModel
            {
                Title       = "Save Changes",
                Message     = $"\"{item.DisplayName}\" was modified while open.",
                Detail      = "Save changes back to the vault?",
                ConfirmText = "Save to Vault",
                CancelText  = "Discard"
            };

            bool? result = await new ConfirmDialog { DataContext = confirmVm }
                .ShowDialog<bool?>( OwnerWindow ?? throw new InvalidOperationException( "No owner window" ) );

            if( result == true )
            {
                try
                {
                    var activity = new ActivityItem( $"Saving \"{item.DisplayName}\"", FluentIcons.Common.Symbol.Save, canCancel: false )
                    {
                        IsIndeterminate = true
                    };
                    await Activities.RunAsync( activity,
                        _ => Vault.UpdateAsync( checkedOut.FileId, checkedOut.TempPath, cancellationToken: CancellationToken.None ),
                        _ => $"Saved \"{item.DisplayName}\" to the vault" );
                    if( activity.State != ActivityState.Completed )
                        throw new InvalidOperationException( activity.ResultText );

                    await ReloadCurrentViewAsync( );

                    await RefreshVaultInfoAsync( );
                    StatusMessage = $"\"{item.DisplayName}\" saved to vault.";
                }
                catch( Exception ex )
                {
                    StatusMessage = $"Failed to save changes: {ex.Message}";
                }
            }
        }

        private void CleanupCheckedOutFile(
            VaultItemViewModel item,
            CheckedOutFile checkedOut,
            bool deleted )
        {
            checkedOut.WatcherCts.Cancel( );
            checkedOut.WatcherCts.Dispose( );

            _checkedOutFiles.Remove( checkedOut.FileId );
            item.IsCheckedOut = false;

            if( !deleted )
            {
                try
                {
                    if( File.Exists( checkedOut.TempPath ) )
                        File.Delete( checkedOut.TempPath );

                    string? tempDir = Path.GetDirectoryName(checkedOut.TempPath);
                    if( tempDir != null && Directory.Exists( tempDir ) )
                        Directory.Delete( tempDir, recursive: true );
                }
                catch
                {
                    // Non-critical
                }
            }
        }

        public async Task CloseAllCheckedOutFilesAsync( )
        {
            foreach( var (_, checkedOut) in _checkedOutFiles.ToList( ) )
            {
                checkedOut.WatcherCts.Cancel( );
                checkedOut.WatcherCts.Dispose( );

                try
                {
                    if( File.Exists( checkedOut.TempPath ) )
                        File.Delete( checkedOut.TempPath );

                    string? tempDir = Path.GetDirectoryName(checkedOut.TempPath);
                    if( tempDir != null && Directory.Exists( tempDir ) )
                        Directory.Delete( tempDir, recursive: true );
                }
                catch { }
            }

            _checkedOutFiles.Clear( );
            await Task.CompletedTask;
        }

        private static async Task<byte[]> ComputeSha256Async(
            Stream stream,
            CancellationToken cancellationToken )
        {
            using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer             = new byte[81920];
            int    bytesRead;

            while( ( bytesRead = await stream.ReadAsync( buffer, cancellationToken ) ) > 0 )
                sha.AppendData( buffer.AsSpan( 0, bytesRead ) );

            return sha.GetHashAndReset( );
        }

        public bool HasCheckedOutFiles => _checkedOutFiles.Count > 0;

        private sealed class CheckedOutFile(
            string FileId,
            string TempPath,
            string Sha256Hex,
            CancellationTokenSource WatcherCts )
        {
            public string                  FileId     { get; } = FileId;
            public string                  TempPath   { get; } = TempPath;
            public CancellationTokenSource WatcherCts { get; } = WatcherCts;

            /// <summary>Hash of the content the vault currently holds for this checkout.</summary>
            public string Sha256Hex { get; set; } = Sha256Hex;

            // Non-Windows save detection (see CheckForSavedChangesAsync)
            public DateTime  LastWriteUtc    { get; set; } = File.GetLastWriteTimeUtc( TempPath );
            public DateTime? PendingWriteUtc { get; set; }
        }

        /// <summary>
        /// IProgress&lt;double&gt; that fires at most every <see cref="MinTicks"/> (≈80 ms).
        /// The throttle check runs on the calling (background) thread so no
        /// unnecessary <c>Post</c> calls are ever queued on the UI dispatcher.
        /// </summary>
        private sealed class ThrottledProgress( Action<double> onReport ) : IProgress<double>
        {
            private long _lastTick = 0;
            private const long MinTicks = 8_000_000L; // 80 ms in 100-ns Ticks

            public void Report( double value )
            {
                long now = DateTime.UtcNow.Ticks;
                if( now - Volatile.Read( ref _lastTick ) < MinTicks ) return;
                Volatile.Write( ref _lastTick, now );
                Avalonia.Threading.Dispatcher.UIThread.Post( () => onReport( value ) );
            }
        }
    }

    public enum SortColumn { Name, Type, Size, Date }
}
