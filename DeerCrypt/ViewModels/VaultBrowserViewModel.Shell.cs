using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Converters;
using DeerCrypt.Services;
using DeerCryptLib.Vault;
using FluentIcons.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels
{
    public enum SidebarKind { Vault, RecentlyAdded, Images, Videos, Audio, Documents }

    /// <summary>A fixed entry in the sidebar (the vault root or a library view).</summary>
    public partial class SidebarItem( SidebarKind kind, string label, Symbol icon ) : ObservableObject
    {
        public SidebarKind Kind  { get; } = kind;
        public string      Label { get; } = label;
        public Symbol      Icon  { get; } = icon;

        [ObservableProperty]
        public partial bool IsActive { get; set; }
    }

    /// <summary>
    /// Shell-level state for the redesigned main window: sidebar views, folder tree
    /// selection, list/grid mode, selection summary, empty states, and storage info.
    /// All of it is read-only with respect to the vault.
    /// </summary>
    public partial class VaultBrowserViewModel
    {
        private const int RecentlyAddedLimit = 200;

        /// <summary>Background operations for this vault (shown in the activity bar).</summary>
        public ActivityCenter Activities { get; } = new( );

        /// <summary>
        /// Refreshes the tree, the visible list, and the storage summary after background work
        /// changed the vault - without yanking the user out of the folder, view, or search they're in.
        /// </summary>
        private async Task AfterVaultChangedAsync( )
        {
            await ReloadTreeAsync( );
            await RefreshVaultInfoAsync( );
        }
        public ObservableCollection<SidebarItem> PrimaryNav { get; } =
        [
            new( SidebarKind.Vault,         "Vault",          Symbol.Home ),
            new( SidebarKind.RecentlyAdded, "Recently added", Symbol.History ),
        ];

        public ObservableCollection<SidebarItem> LibraryNav { get; } =
        [
            new( SidebarKind.Images,    "Images",    Symbol.Image ),
            new( SidebarKind.Videos,    "Videos",    Symbol.Video ),
            new( SidebarKind.Audio,     "Audio",     Symbol.MusicNote2 ),
            new( SidebarKind.Documents, "Documents", Symbol.DocumentText ),
        ];

        /// <summary>Top-level folders shown in the sidebar tree (the root itself is the "Vault" entry).</summary>
        public ObservableCollection<DirectoryNodeViewModel>? FolderNodes => RootNodes.FirstOrDefault( )?.Children;

        // Active view

        /// <summary>The library view being shown, or null when browsing a folder.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsFolderView ) )]
        [NotifyPropertyChangedFor( nameof( ShowBreadcrumb ) )]
        [NotifyPropertyChangedFor( nameof( ViewTitle ) )]
        [NotifyCanExecuteChangedFor( nameof( AddFilesCommand ) )]
        [NotifyCanExecuteChangedFor( nameof( AddFolderCommand ) )]
        [NotifyCanExecuteChangedFor( nameof( NewFolderCommand ) )]
        [NotifyCanExecuteChangedFor( nameof( NavigateUpCommand ) )]
        public partial SidebarKind? ActiveSmartView { get; set; }

        public bool IsFolderView => ActiveSmartView == null;

        /// <summary>Breadcrumb only makes sense while browsing a folder (not search results or library views).</summary>
        public bool ShowBreadcrumb => IsFolderView && !IsSearchActive;

        public string ViewTitle =>
            IsSearchActive ? "Search results"
            : ActiveSmartView is { } kind
                ? PrimaryNav.Concat( LibraryNav ).First( i => i.Kind == kind ).Label
                : _currentDirectory?.Name ?? "Vault";

        private CancellationTokenSource? _viewCts;

        /// <summary>
        /// Folder selected in the sidebar tree. Two-way bound: the user picking a node
        /// navigates there; navigation elsewhere updates it (guarded by <see cref="_syncingTree"/>).
        /// </summary>
        [ObservableProperty]
        public partial DirectoryNodeViewModel? SelectedTreeNode { get; set; }

        private bool _syncingTree;

        partial void OnSelectedTreeNodeChanged( DirectoryNodeViewModel? value )
        {
            if( _syncingTree || value == null ) return;
            if( ActiveSmartView == null && ReferenceEquals( value, _currentDirectory ) ) return;
            _ = NavigateToNodeAsync( value, pushToBack: true );
        }

        [RelayCommand]
        private async Task NavigateSidebarAsync( SidebarItem item )
        {
            if( item.Kind == SidebarKind.Vault )
            {
                if( RootNodes.FirstOrDefault( ) is not { } root ) return;
                if( ActiveSmartView == null && ReferenceEquals( _currentDirectory, root ) ) return;
                await NavigateToNodeAsync( root, pushToBack: ActiveSmartView == null );
                return;
            }

            if( ActiveSmartView == item.Kind ) return;
            await ShowSmartViewAsync( item.Kind );
        }

        private async Task ShowSmartViewAsync( SidebarKind kind )
        {
            RequestScroll( ListScroll.Top );
            ResetSearchSilently( );
            ActiveDocument = null;

            ActiveSmartView = kind;
            CanNavigateBack = true;

            if( kind == SidebarKind.RecentlyAdded )
            {
                SortColumn     = SortColumn.Date;
                SortDescending = true;
            }

            SyncSidebarState( );

            _viewCts?.Cancel( );
            _viewCts = new CancellationTokenSource( );
            try
            {
                await LoadSmartViewItemsAsync( kind, _viewCts.Token );
            }
            catch( OperationCanceledException ) { }
        }

        private async Task LoadSmartViewItemsAsync( SidebarKind kind, CancellationToken ct )
        {
            int version = BeginLoad( );
            try
            {
                IReadOnlyList<VaultEntry> all = await Vault.ListRecursiveAsync( null, ct );
                ct.ThrowIfCancellationRequested( );
                if( !IsCurrentLoad( version ) ) return;

                IEnumerable<VaultEntry> files = kind switch
                {
                    SidebarKind.RecentlyAdded => all.OrderByDescending( f => f.AddedAt ).Take( RecentlyAddedLimit ),
                    SidebarKind.Images        => all.Where( f => FileTypeHelper.GetKind( f.Name ) == FileKind.Image ),
                    SidebarKind.Videos        => all.Where( f => FileTypeHelper.GetKind( f.Name ) == FileKind.Video ),
                    SidebarKind.Audio         => all.Where( f => FileTypeHelper.GetKind( f.Name ) == FileKind.Audio ),
                    SidebarKind.Documents     => all.Where( f => FileTypeHelper.GetKind( f.Name ) is FileKind.Document or FileKind.Pdf ),
                    _                         => []
                };

                var pathMap = BuildDirPathMap( );
                var items   = new List<VaultItemViewModel>( );

                foreach( VaultEntry entry in files )
                {
                    var vm = new VaultItemViewModel( entry );
                    if( pathMap.TryGetValue( entry.DirectoryId, out string? path ) )
                        vm.SubText = path;
                    if( _checkedOutFiles.ContainsKey( entry.Id ) )
                        vm.IsCheckedOut = true;
                    WireItemNotifications( vm );
                    items.Add( vm );
                }

                ReplaceItems( GetSortedItems( items ) );
                StatusMessage = string.Empty;
            }
            catch( OperationCanceledException ) { throw; }
            catch( Exception ex )
            {
                if( IsCurrentLoad( version ) ) StatusMessage = $"Couldn't load items: {ex.Message}";
            }
            finally
            {
                EndLoad( version );
            }
        }

        /// <summary>
        /// Reloads whatever is on screen (a folder or a library view) without changing
        /// navigation state. Used after operations that change vault contents.
        /// </summary>
        private async Task ReloadCurrentViewAsync( CancellationToken cancellationToken = default )
        {
            if( ActiveSmartView is { } kind )
            {
                try { await LoadSmartViewItemsAsync( kind, cancellationToken ); }
                catch( OperationCanceledException ) { }
            }
            else
            {
                await LoadItemsAsync( CurrentDirectoryId, cancellationToken );
            }
        }

        /// <summary>Highlights the sidebar entry and tree node matching the current view.</summary>
        private void SyncSidebarState( )
        {
            bool atRoot = ActiveSmartView == null && _currentDirectory?.IsRoot == true;

            foreach( var item in PrimaryNav.Concat( LibraryNav ) )
                item.IsActive = item.Kind == SidebarKind.Vault
                    ? atRoot
                    : item.Kind == ActiveSmartView;

            _syncingTree = true;
            try
            {
                if( ActiveSmartView == null && _currentDirectory is { IsRoot: false } node )
                {
                    ExpandAncestors( node );
                    SelectedTreeNode = node;
                }
                else
                {
                    SelectedTreeNode = null;
                }
            }
            finally
            {
                _syncingTree = false;
            }

            OnPropertyChanged( nameof( ViewTitle ) );
            OnPropertyChanged( nameof( SearchScopeText ) );
            OnPropertyChanged( nameof( SearchWatermark ) );
            NavigateUpCommand.NotifyCanExecuteChanged( );
        }

        private void ExpandAncestors( DirectoryNodeViewModel node )
        {
            DirectoryNodeViewModel? parent = FindParentNode( RootNodes, node.DirectoryId );
            while( parent != null )
            {
                parent.IsExpanded = true;
                parent = FindParentNode( RootNodes, parent.DirectoryId );
            }
        }

        private void ResetSearchSilently( )
        {
            _searchCts?.Cancel( );
            if( SearchText.Length > 0 )
            {
                SearchText = string.Empty;
                OnPropertyChanged( nameof( SearchText ) );
                OnPropertyChanged( nameof( IsSearchActive ) );
            }
        }

        // Up one level

        [RelayCommand( CanExecute = nameof( CanNavigateUp ) )]
        private async Task NavigateUpAsync( )
        {
            if( _currentDirectory == null ) return;
            DirectoryNodeViewModel? parent = FindParentNode( RootNodes, _currentDirectory.DirectoryId );
            if( parent != null )
                await NavigateToNodeAsync( parent, pushToBack: true );
        }

        private bool CanNavigateUp => IsFolderView && _currentDirectory is { IsRoot: false };

        // List / grid

        [ObservableProperty]
        public partial bool IsGridView { get; set; } = App.SettingsService.Settings.ViewMode == "Grid";

        partial void OnIsGridViewChanged( bool value )
        {
            OnPropertyChanged( nameof( EffectiveGridView ) );
            App.SettingsService.Settings.ViewMode = value ? "Grid" : "List";
            App.SettingsService.Save( );
        }

        [RelayCommand]
        private void SetViewMode( string mode ) => IsGridView = mode == "Grid";

        // Selection summary

        public bool HasSelection => SelectedItems.Count > 0;

        public string SelectionText
        {
            get
            {
                int n = SelectedItems.Count;
                if( n == 0 )
                {
                    int count = CurrentItems.Count;
                    return $"{count:N0} item{( count == 1 ? "" : "s" )}";
                }

                long bytes = SelectedItems.Sum( i => i.AsFile?.OriginalSize ?? 0 );
                string size = bytes > 0 ? $" · {FileSizeConverter.Format( bytes )}" : string.Empty;
                return $"{n:N0} selected{size}";
            }
        }

        // Loading

        // Every list load (folder, library view, search) gets a version number. The list on
        // screen is only replaced once the new results are in, and results from a load that
        // was superseded (the user clicked elsewhere meanwhile) are thrown away.
        private int _loadVersion;

        /// <summary>A list load is in flight.</summary>
        [ObservableProperty]
        public partial bool IsLoading { get; set; }

        /// <summary>Shown only if a load takes long enough to notice, so fast folders don't flicker.</summary>
        [ObservableProperty]
        public partial bool ShowLoadingBar { get; set; }

        private int BeginLoad( )
        {
            int version = ++_loadVersion;
            IsLoading = true;
            _ = RevealLoadingBarAsync( version );
            return version;
        }

        private bool IsCurrentLoad( int version ) => version == _loadVersion;

        private void EndLoad( int version )
        {
            if( version != _loadVersion ) return;
            IsLoading      = false;
            ShowLoadingBar = false;
            RaiseListStateChanged( );
        }

        private async Task RevealLoadingBarAsync( int version )
        {
            await Task.Delay( 150 );
            if( version == _loadVersion && IsLoading ) ShowLoadingBar = true;
        }

        // Scroll position across list reloads 

        /// <summary>What the list's scroll position should do after the next reload.</summary>
        public enum ListScroll { Keep, Top, Item }

        private ListScroll _nextScroll = ListScroll.Keep;
        private string?    _scrollTargetId;

        /// <summary>Raised just before the list is emptied (the view remembers its scroll offset).</summary>
        public event Action? ItemsReplacing;

        /// <summary>Raised after a reload: keep the old offset (refresh), go to the top (new place), or bring an item into view.</summary>
        public event Action<ListScroll, VaultItemViewModel?>? ItemsReplaced;

        private void RequestScroll( ListScroll mode, string? targetId = null )
        {
            _nextScroll     = mode;
            _scrollTargetId = targetId;
        }

        /// <summary>Swaps the visible list for freshly loaded items in one go.</summary>
        private void ReplaceItems( IEnumerable<VaultItemViewModel> items )
        {
            ItemsReplacing?.Invoke( );

            CurrentItems.Clear( );
            foreach( var item in items )
                CurrentItems.Add( item );

            var mode   = _nextScroll;
            var target = _scrollTargetId == null ? null : CurrentItems.FirstOrDefault( i => i.Id == _scrollTargetId );
            _nextScroll     = ListScroll.Keep;
            _scrollTargetId = null;

            if( mode == ListScroll.Item )
            {
                if( target != null ) SelectedFile = target; // highlight the folder you just came out of
                else mode = ListScroll.Top;
            }

            ItemsReplaced?.Invoke( mode, target );
        }

        /// <summary>
        /// Going up (or back) to an ancestor of the folder you were in: the child folder on that path,
        /// so the list can bring it into view. Null when the move isn't upward.
        /// </summary>
        private string? ChildOnPathTowards( DirectoryNodeViewModel? from, DirectoryNodeViewModel to )
        {
            for( var cur = from; cur != null; )
            {
                var parent = FindParentNode( RootNodes, cur.DirectoryId );
                if( parent == null ) return null;
                if( parent.DirectoryId == to.DirectoryId ) return cur.DirectoryId;
                cur = parent;
            }
            return null;
        }

        // Empty states

        /// <summary>Never true while loading: an unfinished load must not look like an empty folder.</summary>
        public bool IsCurrentViewEmpty => !IsLoading && CurrentItems.Count == 0;

        public string EmptyStateTitle =>
            IsSearchActive         ? "No matches"
            : ActiveSmartView is { } kind && kind != SidebarKind.Vault
                                   ? $"No {ViewTitle.ToLowerInvariant( )} yet"
                                   : "This folder is empty";

        public string EmptyStateText =>
            IsSearchActive         ? "Try a different name or part of a name."
            : ActiveSmartView != null
                                   ? "Files you add to the vault show up here automatically."
                                   : "Drop files here, or use Add to bring files into the vault.";

        private void RaiseListStateChanged( )
        {
            OnPropertyChanged( nameof( ShowBreadcrumb ) );
            OnPropertyChanged( nameof( ViewTitle ) );
            OnPropertyChanged( nameof( IsCurrentViewEmpty ) );
            OnPropertyChanged( nameof( EmptyStateTitle ) );
            OnPropertyChanged( nameof( EmptyStateText ) );
            OnPropertyChanged( nameof( SelectionText ) );
            OnPropertyChanged( nameof( EffectiveGridView ) );
        }

        // Storage summary (sidebar footer)

        [ObservableProperty]
        public partial string StorageSummaryText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string StorageDetailText { get; set; } = string.Empty;

        /// <summary>Share of the vault file holding live data (0-100). The rest is reclaimable by compacting.</summary>
        [ObservableProperty]
        public partial double StorageInUsePercent { get; set; }

        [ObservableProperty]
        public partial bool HasReclaimableSpace { get; set; }

        private void UpdateStorageSummary( VaultInfo info, long reclaimable )
        {
            StorageSummaryText = $"{info.TotalFiles:N0} file{( info.TotalFiles == 1 ? "" : "s" )} · " +
                                 $"{FileSizeConverter.Format( info.TotalOriginalSize )}";

            long fileSize = Math.Max( info.VaultFileSize, 1 );
            StorageInUsePercent = Math.Clamp( 100.0 * ( fileSize - reclaimable ) / fileSize, 0, 100 );

            // Only worth surfacing once it's a noticeable amount
            HasReclaimableSpace = reclaimable >= 16L * 1024 * 1024;
            StorageDetailText = HasReclaimableSpace
                ? $"Vault file {FileSizeConverter.Format( info.VaultFileSize )} · {FileSizeConverter.Format( reclaimable )} reclaimable"
                : $"Vault file {FileSizeConverter.Format( info.VaultFileSize )}";
        }

        // Big selections (Ctrl+A on 20k+ items) 

        private bool _selectionBatch;

        /// <summary>
        /// Replaces the selection in one go. Per-item change handling (command refresh, size sum,
        /// inspector) would otherwise run once per item - quadratic on huge folders.
        /// </summary>
        public void ReplaceSelection( IEnumerable<VaultItemViewModel> items, VaultItemViewModel? primary )
        {
            _selectionBatch = true;
            try
            {
                SelectedItems.Clear( );
                foreach( var item in items ) SelectedItems.Add( item );
            }
            finally
            {
                _selectionBatch = false;
            }

            RenameCommand.NotifyCanExecuteChanged( );
            DeleteCommand.NotifyCanExecuteChanged( );
            ExtractCommand.NotifyCanExecuteChanged( );
            MoveCommand.NotifyCanExecuteChanged( );
            OnPropertyChanged( nameof( HasSelection ) );
            OnPropertyChanged( nameof( SelectionText ) );
            ScheduleInspectorUpdate( );
            SelectedFile = primary;
        }

        // Search scope 

        /// <summary>Search only the current folder (and its subfolders) instead of the whole vault.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( SearchScopeText ) )]
        [NotifyPropertyChangedFor( nameof( SearchWatermark ) )]
        public partial bool SearchInFolder { get; set; }

        public string SearchScopeText => SearchInFolder ? ( _currentDirectory?.IsRoot == false ? _currentDirectory.Name : "Vault" ) : "Vault";

        public string SearchWatermark => SearchInFolder && _currentDirectory?.IsRoot == false
            ? $"Search in \"{_currentDirectory.Name}\"  ·  wildcards: *.mp4, photo-??.jpg"
            : "Search the whole vault  ·  wildcards: *.mp4, photo-??.jpg";

        partial void OnSearchInFolderChanged( bool value )
        {
            if( IsSearchActive )
            {
                _searchCts?.Cancel( );
                _searchCts = new CancellationTokenSource( );
                _ = RunSearchAsync( SearchText, _searchCts.Token );
            }
        }

        [RelayCommand]
        private void ToggleSearchScope( ) => SearchInFolder = !SearchInFolder;

        /// <summary>Every folder ID at or below <paramref name="root"/>.</summary>
        private static HashSet<string> CollectSubtreeIds( DirectoryNodeViewModel root )
        {
            var ids   = new HashSet<string>( );
            var stack = new Stack<DirectoryNodeViewModel>( );
            stack.Push( root );
            while( stack.Count > 0 )
            {
                var n = stack.Pop( );
                ids.Add( n.DirectoryId );
                foreach( var c in n.Children ) stack.Push( c );
            }
            return ids;
        }

        /// <summary>All folders in the tree with their parent's ID (null for the root).</summary>
        private IEnumerable<(DirectoryNodeViewModel Node, string? ParentId)> WalkFolders( )
        {
            var stack = new Stack<(DirectoryNodeViewModel, string?)>( );
            foreach( var r in RootNodes ) stack.Push( (r, null) );
            while( stack.Count > 0 )
            {
                var (n, parent) = stack.Pop( );
                yield return (n, parent);
                foreach( var c in n.Children ) stack.Push( (c, n.DirectoryId) );
            }
        }

        // Resizable list columns 

        public const double MinColumnWidth = 56, MaxColumnWidth = 600;

        [ObservableProperty] public partial double TypeColumnWidth { get; set; } = App.SettingsService.Settings.TypeColumnWidth;
        [ObservableProperty] public partial double SizeColumnWidth { get; set; } = App.SettingsService.Settings.SizeColumnWidth;
        [ObservableProperty] public partial double DateColumnWidth { get; set; } = App.SettingsService.Settings.DateColumnWidth;

        /// <summary>Dragging a header handle left widens that column (the Name column gives up the space).</summary>
        public void ResizeColumn( string column, double deltaX )
        {
            double Clamp( double w ) => Math.Clamp( w - deltaX, MinColumnWidth, MaxColumnWidth );
            switch( column )
            {
                case "Type": TypeColumnWidth = Clamp( TypeColumnWidth ); break;
                case "Size": SizeColumnWidth = Clamp( SizeColumnWidth ); break;
                case "Date": DateColumnWidth = Clamp( DateColumnWidth ); break;
            }
        }

        public void SaveColumnWidths( )
        {
            var s = App.SettingsService.Settings;
            s.TypeColumnWidth = TypeColumnWidth;
            s.SizeColumnWidth = SizeColumnWidth;
            s.DateColumnWidth = DateColumnWidth;
            App.SettingsService.Save( );
        }

        // Grid view limit 

        /// <summary>What's shown. The grid is virtualized (VirtualizingWrapPanel), so any folder size works.</summary>
        public bool EffectiveGridView => IsGridView;

        private void InitializeShell( )
        {
            Activities.PropertyChanged += ( _, e ) =>
            {
                if( e.PropertyName == nameof( ActivityCenter.ShowStrip ) ) OnPropertyChanged( nameof( ShowStatusBar ) );
            };
            CurrentItems.CollectionChanged += ( _, e ) =>
            {
                // A reload replaces the whole list: drop any selection from the previous view
                if( e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset
                    && SelectedItems.Count > 0 )
                {
                    SelectedItems.Clear( );
                    SelectedFile = null;
                }
                RaiseListStateChanged( );
                if( e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset || SelectedItems.Count == 0 )
                    ScheduleInspectorUpdate( );
            };
            SelectedItems.CollectionChanged += ( _, _ ) =>
            {
                if( _selectionBatch ) return;
                OnPropertyChanged( nameof( HasSelection ) );
                OnPropertyChanged( nameof( SelectionText ) );
                ScheduleInspectorUpdate( );
            };
        }
    }
}
