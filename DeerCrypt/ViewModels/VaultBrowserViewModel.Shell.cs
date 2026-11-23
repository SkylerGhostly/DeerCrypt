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
            ResetSearchSilently( );

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
            CurrentItems.Clear( );

            try
            {
                IReadOnlyList<VaultEntry> all = await Vault.ListRecursiveAsync( null, ct );
                ct.ThrowIfCancellationRequested( );

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

                foreach( var item in GetSortedItems( items ) )
                    CurrentItems.Add( item );

                StatusMessage = string.Empty;
            }
            catch( OperationCanceledException ) { throw; }
            catch( Exception ex )
            {
                StatusMessage = $"Error loading items: {ex.Message}";
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
                    return $"{count} item{( count == 1 ? "" : "s" )}";
                }

                long bytes = SelectedItems.Sum( i => i.AsFile?.OriginalSize ?? 0 );
                string size = bytes > 0 ? $" · {FileSizeConverter.Format( bytes )}" : string.Empty;
                return $"{n} selected{size}";
            }
        }

        // Empty states

        public bool IsCurrentViewEmpty => CurrentItems.Count == 0;

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

        private void InitializeShell( )
        {
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
            };
            SelectedItems.CollectionChanged += ( _, _ ) =>
            {
                OnPropertyChanged( nameof( HasSelection ) );
                OnPropertyChanged( nameof( SelectionText ) );
            };
        }
    }
}
