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

            CurrentItems.Clear( );

            if( string.IsNullOrWhiteSpace( query ) )
            {
                await LoadItemsAsync( CurrentDirectoryId, ct );
                return;
            }

            StatusMessage = "Searching…";

            var results = await Vault.ListRecursiveAsync( query, ct );
            if( ct.IsCancellationRequested ) return;

            var pathMap = BuildDirPathMap( );

            CurrentItems.Clear( );

            foreach( var entry in results )
            {
                var vm = new VaultItemViewModel( entry );
                if( pathMap.TryGetValue( entry.DirectoryId, out string? path ) )
                    vm.SubText = path;
                WireItemNotifications( vm );
                CurrentItems.Add( vm );
            }

            int count = results.Count;
            StatusMessage = count == 0
                ? $"No results for \"{query}\""
                : $"{count} result{( count == 1 ? "" : "s" )} for \"{query}\"";
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
            SortColumn == col ? $"{label} {( SortDescending ? "▼" : "▲" )}" : label;

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

            // Notify commands when multi-selection changes
            SelectedItems.CollectionChanged += ( _, _ ) =>
            {
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
        public async Task LoadAsync( CancellationToken cancellationToken = default )
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

            await rootNode.LoadChildrenAsync( Vault, cancellationToken );
            RootNodes.Add( rootNode );

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
            if( OwnerWindow == null ) return;

            int totalItems = filePaths.Count + folderPaths.Count;
            if( totalItems == 0 ) return;

            var progressVm = new ProgressDialogViewModel
            {
                Title     = $"Adding {totalItems} item{( totalItems == 1 ? "" : "s" )}...",
                CanCancel = true
            };

            var progressDialog = new ProgressDialog { DataContext = progressVm };

            var addTask = AddDroppedItemsAsync(
                filePaths,
                folderPaths,
                targetDirectoryId,
                progressVm,
                progressVm.CancellationToken);

            var dialogTask = progressDialog.ShowDialog(OwnerWindow);

            await addTask;

            progressVm.Complete( );
            progressVm.CurrentFileName = progressVm.IsCancelled
                ? "Cancelled."
                : $"Added {totalItems} item{( totalItems == 1 ? "" : "s" )}.";

            await Task.Delay( 600 );
            progressDialog.Close( );
            await dialogTask;

            await ReloadTreeAsync( );
            await RefreshVaultInfoAsync( );
        }

        private async Task AddDroppedItemsAsync(
            List<string> filePaths,
            List<string> folderPaths,
            string targetDirectoryId,
            ProgressDialogViewModel progressVm,
            CancellationToken cancellationToken )
        {
            try
            {
                if( filePaths.Count > 0 )
                {
                    await AddFilePathsAsync(
                        filePaths,
                        targetDirectoryId,
                        progressVm,
                        cancellationToken );
                }

                foreach( string folderPath in folderPaths )
                {
                    if( cancellationToken.IsCancellationRequested ) break;

                    string name = Path.GetFileName(
                        folderPath.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar));

                    progressVm.CurrentFileName = name;

                    await AddFolderPathAsync(
                        folderPath,
                        targetDirectoryId,
                        progressVm,
                        cancellationToken );
                }
            }
            catch( Exception ex )
            {
                progressVm.CurrentFileName = $"Error: {ex.Message}";
                progressVm.IsComplete = true;
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
            CurrentItems.Clear( );

            try
            {
                VaultListing listing = await Vault.ListAsync(directoryId, cancellationToken);

                var allItems = new List<VaultItemViewModel>();

                // Prepend ".." when not at root - parent ID from second-to-last breadcrumb
                bool isRoot = directoryId == Vault.RootDirectoryId;
                if( !isRoot && Breadcrumb.Count >= 2 )
                    allItems.Add( VaultItemViewModel.CreateParentDir( Breadcrumb [ ^2 ].DirectoryId ) );

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

                foreach( var item in GetSortedItems( allItems ) )
                    CurrentItems.Add( item );

                int realCount = CurrentItems.Count(i => !i.IsParentDir);
                StatusMessage = $"{realCount} item{( realCount == 1 ? "" : "s" )}";
            }
            catch( Exception ex )
            {
                StatusMessage = $"Error loading items: {ex.Message}";
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

        private bool CanOpen => SelectedFile != null && !SelectedFile.IsParentDir;

        public Action? FocusRenameBox { get; set; }

        [RelayCommand( CanExecute = nameof( CanRename ) )]
        private async Task RenameAsync( CancellationToken cancellationToken )
        {
            if( SelectedFile == null ) return;
            SelectedFile.BeginRename( );
            FocusRenameBox?.Invoke( );
        }

        private bool CanRename =>
            SelectedItems.Count == 1 &&
            !SelectedItems [ 0 ].IsParentDir &&
            !SelectedItems [ 0 ].IsCheckedOut;

        private bool CanExtract =>
            SelectedItems.Count > 0 &&
            SelectedItems.All( i => !i.IsParentDir );

        private bool CanDelete =>
            SelectedItems.Count > 0 &&
            SelectedItems.All( i => !i.IsParentDir && !i.IsCheckedOut );

        private bool CanMove =>
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
            // Cancel any in-flight search and reset the search box silently
            _searchCts?.Cancel( );
            if( SearchText.Length > 0 )
            {
                SearchText = string.Empty;
                OnPropertyChanged( nameof( SearchText ) );
                OnPropertyChanged( nameof( IsSearchActive ) );
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

            foreach( BreadcrumbSegment segment in segments )
                Breadcrumb.Add( segment );
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

            StatusMessage = "Checking vault integrity...";
            IsBusy = true;

            try
            {
                var progressVm = new ProgressDialogViewModel
                {
                    Title     = "Checking vault integrity...",
                    CurrentFileName = "Checking vault integrity...",
                    StatusText = "On large vaults this may take some time.",
                    CanCancel = false,
                    IsIndeterminate = true,
                };

                var progressDialog = new ProgressDialog { DataContext = progressVm };
                progressDialog.Show( OwnerWindow );

                IReadOnlyList<string> issues = await Task.Run(
                    ( ) => Vault.CheckIntegrityAsync( cancellationToken ),
                    cancellationToken );

                progressVm.IsComplete = true;
                progressDialog.Close( );

                if( issues.Count == 0 )
                {
                    var vm = new ConfirmDialogViewModel
                    {
                        Title       = "Integrity Check",
                        Message     = "✓ Vault integrity check passed.",
                        Detail      = "No issues found.",
                        ConfirmText = "OK",
                        CancelText  = ""
                    };

                    await new ConfirmDialog { DataContext = vm }
                        .ShowDialog( OwnerWindow );

                    StatusMessage = "Integrity check passed.";
                }
                else
                {
                    string issueList = string.Join("\n", issues.Select(i => $"• {i}"));

                    var vm = new ConfirmDialogViewModel
                    {
                        Title       = "Integrity Check - Issues Found",
                        Message     = $"{issues.Count} issue{( issues.Count == 1 ? "" : "s" )} found:",
                        Detail      = issueList,
                        ConfirmText = "OK",
                        CancelText  = ""
                    };

                    await new ConfirmDialog { DataContext = vm }
                        .ShowDialog( OwnerWindow );

                    StatusMessage = $"Integrity check found {issues.Count} issue(s).";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task AddFilesAsync( CancellationToken cancellationToken )
        {
            if( _currentDirectory == null || OwnerWindow == null ) return;

            var files = await OwnerWindow.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title         = "Add Files to Vault",
                    AllowMultiple = true
                });

            if( files.Count == 0 ) return;

            List<string> paths = [ .. files.Select(f => f.Path.LocalPath) ];

            var progressVm = new ProgressDialogViewModel
            {
                Title     = $"Adding {paths.Count} file{( paths.Count == 1 ? "" : "s" )}...",
                CanCancel = true
            };

            var progressDialog = new ProgressDialog { DataContext = progressVm };

            var addTask = AddFilePathsAsync(
                paths,
                _currentDirectory.DirectoryId,
                progressVm,
                progressVm.CancellationToken);

            var dialogTask = progressDialog.ShowDialog(OwnerWindow);

            await addTask;

            progressVm.Complete( );
            progressVm.CurrentFileName = progressVm.IsCancelled
                ? "Cancelled."
                : $"Added {paths.Count} file{( paths.Count == 1 ? "" : "s" )}.";
            await Task.Delay( 600, cancellationToken );
            progressDialog.Close( );
            await dialogTask;

            await LoadItemsAsync( _currentDirectory.DirectoryId, cancellationToken );
            await RefreshVaultInfoAsync( cancellationToken );
        }

        [RelayCommand]
        private async Task AddFolderAsync( CancellationToken cancellationToken )
        {
            if( _currentDirectory == null || OwnerWindow == null ) return;

            var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title         = "Select Folder to Add",
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
                StatusMessage = $"Error scanning folder: {ex.Message}";
                return;
            }

            var confirmVm  = new AddFolderConfirmViewModel(manifest);
            bool? confirmed = await new AddFolderConfirmDialog { DataContext = confirmVm }
                .ShowDialog<bool?>( OwnerWindow );

            if( confirmed != true ) return;

            var progressVm = new ProgressDialogViewModel
            {
                Title     = $"Adding \"{manifest.RootName}\"...",
                CanCancel = true
            };

            var progressDialog = new ProgressDialog { DataContext = progressVm };

            var addTask = AddFolderToVaultAsync(
                manifest,
                _currentDirectory.DirectoryId,
                progressVm,
                progressVm.CancellationToken);

            var dialogTask = progressDialog.ShowDialog(OwnerWindow);

            await addTask;

            progressVm.Complete( );
            progressVm.CurrentFileName = progressVm.IsCancelled
                ? "Cancelled."
                : $"Added \"{manifest.RootName}\" successfully.";
            await Task.Delay( 600, cancellationToken );
            progressDialog.Close( );
            await dialogTask;

            await ReloadTreeAsync( cancellationToken );
            await RefreshVaultInfoAsync( cancellationToken );
        }

        // Shared core add methods 

        private async Task AddFilePathsAsync(
            IEnumerable<string> filePaths,
            string targetDirectoryId,
            ProgressDialogViewModel progressVm,
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
                catch( Exception ex )
                {
                    string msg = ex.Message;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync( ( ) =>
                        progressVm.StatusText = $"Failed: {name} - {msg}" );

                    await Task.Delay( 1500, CancellationToken.None );
                }
            }
        }

        private async Task AddFolderPathAsync(
            string folderPath,
            string targetDirectoryId,
            ProgressDialogViewModel progressVm,
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
            ProgressDialogViewModel progressVm,
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

            DirectoryNodeViewModel? restored = FindNode(RootNodes, currentId)
                ?? rootNode;

            await NavigateToNodeAsync( restored, pushToBack: false, cancellationToken: cancellationToken );
        }

        // Extract 

        [RelayCommand( CanExecute = nameof( CanExtract ) )]
        private async Task ExtractAsync( CancellationToken cancellationToken )
        {
            if( OwnerWindow == null ) return;

            var items = SelectedItems.Where(i => !i.IsParentDir).ToList();
            if( items.Count == 0 ) return;

            var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title         = "Extract To...",
                    AllowMultiple = false
                });

            if( folders.Count == 0 ) return;

            string outputPath = folders[0].Path.LocalPath;

            string title = items.Count == 1
                ? $"Extracting \"{items[0].DisplayName}\"..."
                : $"Extracting {items.Count} items...";

            var progressVm = new ProgressDialogViewModel
            {
                Title     = title,
                CanCancel = true
            };

            var progressDialog = new ProgressDialog { DataContext = progressVm };

            var extractTask = Task.Run( async ( ) =>
            {
                int total     = items.Count;
                int completed = 0;

                try
                {
                    foreach( var item in items )
                    {
                        if( progressVm.CancellationToken.IsCancellationRequested ) break;

                        Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                        {
                            progressVm.CurrentFileName = item.DisplayName;
                            progressVm.StatusText      = $"{completed + 1} of {total}";
                        } );

                        IProgress<double> progress = new Progress<double>( p =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                                progressVm.Progress = ( completed + p ) / total );
                        } );

                        if( item.AsFile is VaultEntry file )
                        {
                            string dest = Path.Combine(outputPath, file.Name);
                            await Vault.ExtractAsync( file.Id, dest, progress,
                                progressVm.CancellationToken );
                        }
                        else if( item.AsDir is VaultDirectoryEntry dir )
                        {
                            await Vault.ExtractDirectoryAsync( dir.Id, outputPath, progress,
                                progressVm.CancellationToken );
                        }

                        completed++;
                        Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                            progressVm.Progress = (double)completed / total );
                    }

                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                    {
                        progressVm.Complete( );
                        progressVm.CurrentFileName = progressVm.IsCancelled
                            ? "Cancelled."
                            : "Extraction complete.";
                    } );
                }
                catch( OperationCanceledException )
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                    {
                        progressVm.CurrentFileName = "Cancelled.";
                        progressVm.IsComplete      = true;
                    } );
                }
                catch( Exception ex )
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                    {
                        progressVm.CurrentFileName = $"Error: {ex.Message}";
                        progressVm.IsComplete      = true;
                    } );
                }

                await Task.Delay( 600, CancellationToken.None );
                Avalonia.Threading.Dispatcher.UIThread.Post( ( ) => progressDialog.Close( ) );
            }, cancellationToken );

            await progressDialog.ShowDialog( OwnerWindow );
            await extractTask;

            StatusMessage = "Extraction complete.";
        }

        // Delete 

        [RelayCommand( CanExecute = nameof( CanDelete ) )]
        private async Task DeleteAsync( CancellationToken cancellationToken )
        {
            if( OwnerWindow == null ) return;

            var items = SelectedItems.Where(i => !i.IsParentDir && !i.IsCheckedOut).ToList();
            if( items.Count == 0 ) return;

            // Build confirmation message
            string message;
            string detail;

            if( items.Count == 1 )
            {
                message = $"Are you sure you want to delete \"{items [ 0 ].DisplayName}\"?";
                detail  = items [ 0 ].IsDir
                    ? "This will permanently delete the folder and all of its contents."
                    : string.Empty;
            }
            else
            {
                message = $"Are you sure you want to delete {items.Count} items?";
                detail = "This cannot be undone.";
                detail  = string.Join( "\n", items.Select( i => $"• {i.DisplayName}" ) );
            }

            var confirmVm = new ConfirmDialogViewModel
            {
                Title       = "Confirm Delete",
                Message     = message,
                Detail      = detail,
                ConfirmText = "Delete",
                CancelText  = "Cancel"
            };

            bool? confirmed = await new ConfirmDialog { DataContext = confirmVm }
                .ShowDialog<bool?>( OwnerWindow );

            if( confirmed != true ) return;

            var progressVm = new ProgressDialogViewModel
            {
                Title     = items.Count == 1
                    ? $"Deleting \"{items[0].DisplayName}\"..."
                    : $"Deleting {items.Count} items...",
                CanCancel = false
            };

            var progressDialog = new ProgressDialog { DataContext = progressVm };

            var deleteTask = Task.Run( async ( ) =>
            {
                int total     = items.Count;
                int completed = 0;

                try
                {
                    foreach( var item in items )
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                        {
                            progressVm.CurrentFileName = item.DisplayName;
                            progressVm.StatusText      = $"{completed + 1} of {total}";
                        } );

                        IProgress<double> progress = new Progress<double>( p =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                                progressVm.Progress = ( completed + p ) / total );
                        } );

                        if( item.AsFile is VaultEntry file )
                        {
                            await Vault.RemoveAsync( file.Id, progress, CancellationToken.None );
                        }
                        else if( item.AsDir is VaultDirectoryEntry dir )
                        {
                            await Vault.RemoveDirectoryAsync( dir.Id, progress, CancellationToken.None );
                        }

                        completed++;

                        Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                        {
                            CurrentItems.Remove( item );
                            if( item.AsDir is VaultDirectoryEntry dirEntry )
                                RemoveNodeFromTree( RootNodes, dirEntry.Id );
                            progressVm.Progress = (double)completed / total;
                        } );
                    }

                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                    {
                        SelectedFile = null;
                        SelectedItems.Clear( );
                        progressVm.Complete( );
                    } );
                }
                catch( Exception ex )
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post( ( ) =>
                    {
                        progressVm.StatusText = $"Error: {ex.Message}";
                        progressVm.IsComplete = true;
                    } );
                }

                await Task.Delay( 400, CancellationToken.None );
                Avalonia.Threading.Dispatcher.UIThread.Post( ( ) => progressDialog.Close( ) );
            }, cancellationToken );

            await progressDialog.ShowDialog( OwnerWindow );
            await deleteTask;

            await RefreshVaultInfoAsync( cancellationToken );
            int realCount = CurrentItems.Count(i => !i.IsParentDir);
            StatusMessage = $"{realCount} item{( realCount == 1 ? "" : "s" )}";
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

        [RelayCommand]
        private async Task NewFolderAsync( CancellationToken cancellationToken )
        {
            if( SelectedDirectory == null || OwnerWindow == null ) return;

            var vm = new InputDialogViewModel
            {
                Title       = "New Folder",
                Prompt      = "Enter a name for the new folder:",
                Placeholder = "Folder name",
                ConfirmText = "Create",
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

            long reclaimable = await Vault.GetReclaimableBytesAsync(cancellationToken);

            var vm     = new CompactDialogViewModel(Vault, reclaimable);
            var dialog = new CompactDialog { DataContext = vm };

            bool? result = await dialog.ShowDialog<bool?>( OwnerWindow );

            if( result == true )
            {
                await RefreshVaultInfoAsync( cancellationToken );
                StatusMessage = "Vault compacted.";
            }
        }

        // Navigation 

        [RelayCommand( CanExecute = nameof( CanNavigateBack ) )]
        private async Task NavigateBackAsync( CancellationToken cancellationToken )
        {
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
            if( segment.DirectoryId == _currentDirectory?.DirectoryId ) return;

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
                        await OpenInMediaPlayerAsync( item, cancellationToken );
                        return;

                    case FileViewerType.Image:
                        await OpenInImageViewerAsync( item, cancellationToken );
                        return;

                    case FileViewerType.Text:
                        OpenInTextEditorAsync( item );
                        return;

                    case FileViewerType.External:
                        // Unknown type - let the user pick a viewer
                        var choice = await new Views.Dialogs.OpenWithDialog( item.DisplayName )
                            .ShowDialog<OpenWithChoice>( OwnerWindow );

                        switch( choice )
                        {
                            case OpenWithChoice.MediaPlayer:
                                await OpenInMediaPlayerAsync( item, cancellationToken );
                                return;
                            case OpenWithChoice.ImageViewer:
                                await OpenInImageViewerAsync( item, cancellationToken );
                                return;
                            case OpenWithChoice.TextEditor:
                                OpenInTextEditorAsync( item );
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

            var progressVm = new ProgressDialogViewModel
            {
                Title           = $"Opening \"{item.DisplayName}\"",
                CurrentFileName = item.DisplayName,
                StatusText      = "Decrypting…",
                CanCancel       = true
            };

            var progressDialog = new ProgressDialog { DataContext = progressVm };

            var extractTask = ExtractToTempAsync(
                item.AsFile.Id,
                tempPath,
                progressVm,
                progressVm.CancellationToken );

            var dialogTask = progressDialog.ShowDialog( OwnerWindow );

            bool success = false;
            try
            {
                await extractTask;
                success = true;
            }
            catch( OperationCanceledException )
            {
                // User cancelled - swallow silently
            }
            catch( Exception ex )
            {
                progressVm.StatusText      = string.Empty;
                progressVm.CurrentFileName = $"Error: {ex.Message}";
                progressVm.Progress        = 0;
                await Task.Delay( 1500, cancellationToken );
            }

            // Allow OnClosing to permit programmatic Close() regardless of outcome
            progressVm.IsComplete = true;
            progressDialog.Close( );
            await dialogTask;

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

        /// <summary>
        /// Opens a media file directly from the vault in the built-in LibVLC player.
        ///
        /// Files ≤ 64 MB are fully buffered into a <see cref="System.IO.MemoryStream"/> before
        /// being handed to VLC.  A MemoryStream has zero per-read overhead and is synchronously
        /// seekable, which lets VLC's MP4 demuxer instantly locate the moov atom (even when it
        /// sits at the end of the file) and report an accurate duration.  Without this, VLC's
        /// async-backed stream can mis-time moov discovery on short clips, causing wrong
        /// duration display and a non-moving seekbar.
        ///
        /// Files > 64 MB continue to use the streaming <see cref="DeerCryptLib.Vault.VaultReadStream"/>
        /// directly (also seekable) to avoid holding large allocations in memory.
        /// </summary>
        private async Task OpenInMediaPlayerAsync(
            VaultItemViewModel item,
            CancellationToken  cancellationToken )
        {
            const long BufferThreshold = 64L * 1024 * 1024; // 64 MB

            System.IO.Stream mediaStream;
            try
            {
                var vaultStream = await Vault.OpenReadStreamAsync( item.AsFile!.Id, cancellationToken );

                if( vaultStream.Length <= BufferThreshold )
                {
                    // Buffer the entire decrypted file so VLC gets a native,
                    // zero-overhead MemoryStream with full random-access capability.
                    var ms = new System.IO.MemoryStream( (int)vaultStream.Length );
                    await vaultStream.CopyToAsync( ms, cancellationToken );
                    vaultStream.Dispose( );
                    ms.Position = 0;
                    mediaStream = ms;
                }
                else
                {
                    mediaStream = vaultStream;
                }
            }
            catch( Exception ex )
            {
                StatusMessage = $"Failed to open '{item.DisplayName}': {ex.Message}";
                return;
            }

            var vm     = new MediaPlayerViewModel( App.LibVLC, mediaStream, item.DisplayName );
            var window = new MediaPlayerWindow { DataContext = vm };
            window.Show( );
        }

        /// <summary>
        /// Opens an image file in the built-in image viewer.
        /// Collects all sibling images from the current listing (in display order)
        /// so the user can navigate forward/back without leaving the viewer.
        /// </summary>
        private Task OpenInImageViewerAsync(
            VaultItemViewModel item,
            CancellationToken  cancellationToken )
        {
            // All images in the current directory, in the current sort order
            var images = CurrentItems
                .Where( i => i.IsFile && !i.IsParentDir && FileTypeHelper.IsImage( i.DisplayName ) )
                .ToList( );

            int startIndex = images.IndexOf( item );
            if( startIndex < 0 ) { images.Insert( 0, item ); startIndex = 0; }

            var vm = new ImageViewerViewModel( Vault, images, startIndex );

            // When the viewer deletes an image, remove it from the browser listing too.
            // Store the handler so we can unsubscribe when the window closes, breaking
            // the reference from vm back to this VaultBrowserViewModel.
            void itemDeletedHandler( VaultItemViewModel deletedItem )
            {
                var existing = CurrentItems.FirstOrDefault( i => i.Id == deletedItem.Id );
                if( existing != null ) CurrentItems.Remove( existing );
                _ = RefreshVaultInfoAsync( cancellationToken );
                int realCount = CurrentItems.Count( i => !i.IsParentDir );
                StatusMessage = $"{realCount} item(s)";
            }
            vm.ItemDeleted += itemDeletedHandler;

            var window = new Views.Dialogs.ImageViewerWindow { DataContext = vm };
            window.Closed += ( _, _ ) => vm.ItemDeleted -= itemDeletedHandler;
            window.Show( );
            return Task.CompletedTask;
        }

        /// <summary>
        /// Opens a text / code file in the built-in secure text editor.
        /// The file is decrypted on-the-fly from the vault stream - it never
        /// touches disk until the user explicitly saves.
        /// </summary>
        private void OpenInTextEditorAsync( VaultItemViewModel item )
        {
            var vm     = new TextEditorViewModel( Vault, item.AsFile!.Id, item.DisplayName );
            var window = new TextEditorWindow { DataContext = vm };
            window.Show( );
        }

        private async Task ExtractToTempAsync(
            string fileId,
            string tempPath,
            ProgressDialogViewModel progressVm,
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

                if( currentHex != checkedOut.OriginalSha256Hex )
                {
                    // File was modified - show save dialog, then fall through to cleanup.
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
                            await Vault.UpdateAsync(
                                checkedOut.FileId,
                                checkedOut.TempPath,
                                cancellationToken: CancellationToken.None );

                            if( _currentDirectory != null )
                                await LoadItemsAsync( _currentDirectory.DirectoryId );

                            await RefreshVaultInfoAsync( );
                            StatusMessage = $"\"{item.DisplayName}\" saved to vault.";
                        }
                        catch( Exception ex )
                        {
                            StatusMessage = $"Failed to save changes: {ex.Message}";
                        }
                    }
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

        private sealed record CheckedOutFile(
            string FileId,
            string TempPath,
            string OriginalSha256Hex,
            CancellationTokenSource WatcherCts );

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
