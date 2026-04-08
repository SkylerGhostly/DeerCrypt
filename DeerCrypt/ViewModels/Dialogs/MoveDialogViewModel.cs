using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCryptLib.Vault;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Dialogs
{
    public partial class MoveDialogViewModel( VaultFile vault, IEnumerable<string> movingIds ) : ObservableObject
    {
        private readonly HashSet<string> _movingIds = [ .. movingIds ];

        public ObservableCollection<VaultItemViewModel> CurrentDirs { get; } = [ ];
        public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = [ ];

        /// <summary>Set by ConfirmCommand. Null means the dialog was cancelled.</summary>
        public string? ConfirmedTargetId { get; private set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( NavigateToParentCommand ) )]
        public partial bool CanNavigateBack { get; set; }

        /// <summary>Raised when the dialog should close. The bool payload is the dialog result.</summary>
        public event EventHandler<bool>? DialogCloseRequested;

        /// <summary>Called from code-behind after the window opens.</summary>
        public async Task InitializeAsync( CancellationToken ct = default )
        {
            Breadcrumb.Add( new BreadcrumbSegment( vault.RootDirectoryId, "Vault" ) );
            await LoadDirsAsync( vault.RootDirectoryId, ct );
        }

        private async Task LoadDirsAsync( string dirId, CancellationToken ct = default )
        {
            VaultListing listing = await vault.ListAsync(dirId, ct);
            CurrentDirs.Clear( );

            foreach( VaultDirectoryEntry dir in listing.Directories.OrderBy( d => d.Name ) )
            {
                CurrentDirs.Add( new VaultItemViewModel( dir )
                {
                    IsDisabled = _movingIds.Contains( dir.Id )
                } );
            }
        }

        [RelayCommand]
        private async Task NavigateInto( VaultItemViewModel item, CancellationToken ct )
        {
            if( item.AsDir is not { } dir || item.IsDisabled ) return;
            Breadcrumb.Add( new BreadcrumbSegment( dir.Id, dir.Name ) );
            CanNavigateBack = Breadcrumb.Count > 1;
            await LoadDirsAsync( dir.Id, ct );
        }

        [RelayCommand( CanExecute = nameof( CanNavigateBack ) )]
        private async Task NavigateToParent( CancellationToken ct )
        {
            if( Breadcrumb.Count <= 1 ) return;
            Breadcrumb.RemoveAt( Breadcrumb.Count - 1 );
            CanNavigateBack = Breadcrumb.Count > 1;
            await LoadDirsAsync( Breadcrumb [ ^1 ].DirectoryId, ct );
        }

        [RelayCommand]
        private async Task NavigateToBreadcrumb( BreadcrumbSegment seg, CancellationToken ct )
        {
            int idx = Breadcrumb.IndexOf(seg);
            if( idx < 0 ) return;
            while( Breadcrumb.Count > idx + 1 )
                Breadcrumb.RemoveAt( Breadcrumb.Count - 1 );
            CanNavigateBack = Breadcrumb.Count > 1;
            await LoadDirsAsync( seg.DirectoryId, ct );
        }

        [RelayCommand]
        private void Confirm( )
        {
            ConfirmedTargetId = Breadcrumb [ ^1 ].DirectoryId;
            DialogCloseRequested?.Invoke( this, true );
        }
    }
}
