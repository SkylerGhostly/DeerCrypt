using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCrypt.Converters;
using DeerCrypt.Services;
using DeerCryptLib.Vault;
using FluentIcons.Common;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels
{
    public enum InspectorMode { Summary, File, Folder, Multiple }

    /// <summary>One label/value line in the inspector's details list.</summary>
    public sealed record InspectorRow( string Label, string Value, Symbol Icon, IBrush? IconBrush = null )
    {
        public bool HasIconBrush => IconBrush != null;
    }

    /// <summary>
    /// The inspector panel: details for the current selection (one file, one folder, several
    /// items) or, with nothing selected, a summary of what's on screen. Read-only.
    /// </summary>
    public partial class VaultBrowserViewModel
    {
        [ObservableProperty]
        public partial bool IsInspectorOpen { get; set; } = App.SettingsService.Settings.InspectorOpen;

        partial void OnIsInspectorOpenChanged( bool value )
        {
            App.SettingsService.Settings.InspectorOpen = value;
            App.SettingsService.Save( );
            if( value ) ScheduleInspectorUpdate( );
        }

        [RelayCommand]
        private void ToggleInspector( ) => IsInspectorOpen = !IsInspectorOpen;

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsInspectingFile ) )]
        [NotifyPropertyChangedFor( nameof( IsInspectingFolder ) )]
        [NotifyPropertyChangedFor( nameof( IsInspectingMultiple ) )]
        [NotifyPropertyChangedFor( nameof( IsInspectingSummary ) )]
        [NotifyPropertyChangedFor( nameof( HasInspectorActions ) )]
        public partial InspectorMode InspectorMode { get; set; }

        public bool IsInspectingFile     => InspectorMode == InspectorMode.File;
        public bool IsInspectingFolder   => InspectorMode == InspectorMode.Folder;
        public bool IsInspectingMultiple => InspectorMode == InspectorMode.Multiple;
        public bool IsInspectingSummary  => InspectorMode == InspectorMode.Summary;
        public bool HasInspectorActions  => InspectorMode != InspectorMode.Summary;

        [ObservableProperty] public partial VaultItemViewModel? InspectedItem { get; set; }
        [ObservableProperty] public partial string InspectorTitle { get; set; } = string.Empty;
        [ObservableProperty] public partial string InspectorSubtitle { get; set; } = string.Empty;
        [ObservableProperty] public partial Symbol InspectorIcon { get; set; } = Symbol.Folder;
        [ObservableProperty] public partial IBrush InspectorIconBrush { get; set; } = FileKindVisuals.GetBrush( FileKind.Folder );

        /// <summary>Full SHA-256 of the inspected file's content (hex), once loaded.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( HasInspectorHash ) )]
        [NotifyPropertyChangedFor( nameof( InspectorHashShort ) )]
        public partial string InspectorHash { get; set; } = string.Empty;

        public bool   HasInspectorHash   => InspectorHash.Length > 0;
        public string InspectorHashShort => InspectorHash.Length > 24
            ? $"{InspectorHash [ ..12 ]}…{InspectorHash [ ^8.. ]}".ToLowerInvariant( )
            : InspectorHash.ToLowerInvariant( );

        /// <summary>True when the inspected item lives somewhere other than the folder on screen.</summary>
        [ObservableProperty]
        public partial bool CanRevealInspected { get; set; }

        public ObservableCollection<InspectorRow> InspectorRows { get; } = [ ];

        private CancellationTokenSource? _inspectorCts;

        /// <summary>Debounced so dragging a selection across many rows doesn't hammer the vault.</summary>
        private void ScheduleInspectorUpdate( )
        {
            if( !IsInspectorOpen ) return;

            _inspectorCts?.Cancel( );
            _inspectorCts = new CancellationTokenSource( );
            var ct = _inspectorCts.Token;

            _ = Task.Run( async ( ) =>
            {
                try
                {
                    await Task.Delay( 90, ct );
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync( ( ) => UpdateInspectorAsync( ct ) );
                }
                catch( OperationCanceledException ) { }
            }, ct );
        }

        private async Task UpdateInspectorAsync( CancellationToken ct )
        {
            InspectorRows.Clear( );
            InspectorHash      = string.Empty;
            CanRevealInspected = false;

            var selected = SelectedItems.Where( i => !i.IsParentDir ).ToList( );

            try
            {
                if( selected.Count == 1 && selected [ 0 ].AsFile is { } file )
                    await InspectFileAsync( selected [ 0 ], file, ct );
                else if( selected.Count == 1 && selected [ 0 ].AsDir is { } dir )
                    await InspectFolderAsync( selected [ 0 ], dir, ct );
                else if( selected.Count > 1 )
                    InspectMultiple( selected );
                else
                    await InspectSummaryAsync( ct );
            }
            catch( OperationCanceledException ) { }
            catch( ObjectDisposedException ) { } // vault locked mid-update
            catch( Exception ex )
            {
                InspectorRows.Add( new InspectorRow( "Couldn't load details", ex.Message, Symbol.ErrorCircle ) );
            }
        }

        private async Task InspectFileAsync( VaultItemViewModel item, VaultEntry file, CancellationToken ct )
        {
            InspectorMode      = InspectorMode.File;
            InspectedItem      = item;
            InspectorTitle     = item.DisplayName;
            InspectorSubtitle  = item.TypeText;
            InspectorIcon      = item.Icon;
            InspectorIconBrush = item.IconBrush;

            var paths = BuildDirPathMap( );
            string location = paths.TryGetValue( file.DirectoryId, out string? p ) ? p : "Vault";
            CanRevealInspected = !IsFolderView || IsSearchActive || file.DirectoryId != CurrentDirectoryId;

            InspectorRows.Add( new InspectorRow( "Size", $"{FileSizeConverter.Format( file.OriginalSize )} ({file.OriginalSize:N0} bytes)", Symbol.DataUsage ) );
            InspectorRows.Add( new InspectorRow( "Location", location, Symbol.Folder ) );
            InspectorRows.Add( new InspectorRow( "Added", FormatFullDate( file.AddedAt ), Symbol.Calendar ) );
            InspectorRows.Add( new InspectorRow( "Stored as",
                file.ChunkCount == 1 ? "1 encrypted chunk" : $"{file.ChunkCount:N0} encrypted chunks", Symbol.ShieldLock ) );
            if( item.IsCheckedOut )
                InspectorRows.Add( new InspectorRow( "Status", "Open in another app (temporary copy on disk)",
                    Symbol.LockClosed, FileKindVisuals.GetBrush( FileKind.Archive ) ) );

            byte[] hash = await Vault.GetFileSha256Async( file.Id, ct );
            ct.ThrowIfCancellationRequested( );
            InspectorHash = Convert.ToHexString( hash );
        }

        private async Task InspectFolderAsync( VaultItemViewModel item, VaultDirectoryEntry dir, CancellationToken ct )
        {
            InspectorMode      = InspectorMode.Folder;
            InspectedItem      = item;
            InspectorTitle     = item.DisplayName;
            InspectorSubtitle  = "Folder";
            InspectorIcon      = Symbol.Folder;
            InspectorIconBrush = FileKindVisuals.GetBrush( FileKind.Folder );

            var paths = BuildDirPathMap( );
            if( dir.ParentId != null && paths.TryGetValue( dir.ParentId, out string? parent ) )
                InspectorRows.Add( new InspectorRow( "Location", parent, Symbol.Folder ) );
            InspectorRows.Add( new InspectorRow( "Created", FormatFullDate( dir.CreatedAt ), Symbol.Calendar ) );
            CanRevealInspected = !IsFolderView || IsSearchActive;

            DirectoryStats stats = await Vault.GetDirectoryStatsAsync( dir.Id, ct );
            ct.ThrowIfCancellationRequested( );
            InspectorRows.Insert( 0, new InspectorRow( "Contains", DescribeCounts( stats ), Symbol.DocumentMultiple ) );
            InspectorRows.Insert( 1, new InspectorRow( "Total size", FileSizeConverter.Format( stats.TotalSize ), Symbol.DataUsage ) );
        }

        private void InspectMultiple( System.Collections.Generic.List<VaultItemViewModel> selected )
        {
            InspectorMode      = InspectorMode.Multiple;
            InspectedItem      = null;
            InspectorTitle     = $"{selected.Count} items selected";
            long bytes         = selected.Sum( i => i.AsFile?.OriginalSize ?? 0 );
            int folders        = selected.Count( i => i.IsDir );
            InspectorSubtitle  = folders > 0
                ? $"{FileSizeConverter.Format( bytes )} in files, plus {folders} folder{( folders == 1 ? "" : "s" )}"
                : FileSizeConverter.Format( bytes );
            InspectorIcon      = Symbol.DocumentMultiple;
            InspectorIconBrush = FileKindVisuals.GetBrush( FileKind.Other );

            foreach( var group in selected.GroupBy( i => i.Kind ).OrderByDescending( g => g.Count( ) ) )
            {
                string label = group.Key switch
                {
                    FileKind.Folder   => "Folders",
                    FileKind.Image    => "Images",
                    FileKind.Video    => "Videos",
                    FileKind.Audio    => "Audio",
                    FileKind.Pdf      => "PDFs",
                    FileKind.Document => "Documents",
                    FileKind.Code     => "Code and data",
                    FileKind.Archive  => "Archives",
                    _                 => "Other files"
                };
                long size = group.Sum( i => i.AsFile?.OriginalSize ?? 0 );
                string value = size > 0 ? $"{group.Count( )} · {FileSizeConverter.Format( size )}" : $"{group.Count( )}";
                InspectorRows.Add( new InspectorRow( label, value, FileKindVisuals.GetIcon( group.Key ), FileKindVisuals.GetBrush( group.Key ) ) );
            }
        }

        private async Task InspectSummaryAsync( CancellationToken ct )
        {
            InspectorMode      = InspectorMode.Summary;
            InspectedItem      = null;
            InspectorTitle     = ViewTitle;
            InspectorIconBrush = FileKindVisuals.GetBrush( FileKind.Folder );

            if( IsFolderView && !IsSearchActive )
            {
                InspectorIcon     = _currentDirectory?.IsRoot == true ? Symbol.ShieldLock : Symbol.Folder;
                InspectorSubtitle = _currentDirectory?.IsRoot == true ? "Whole vault" : "Folder";

                DirectoryStats stats = await Vault.GetDirectoryStatsAsync( CurrentDirectoryId, ct );
                ct.ThrowIfCancellationRequested( );
                InspectorRows.Add( new InspectorRow( "Contains", DescribeCounts( stats ), Symbol.DocumentMultiple ) );
                InspectorRows.Add( new InspectorRow( "Total size", FileSizeConverter.Format( stats.TotalSize ), Symbol.DataUsage ) );
            }
            else
            {
                InspectorIcon     = IsSearchActive ? Symbol.Search
                                  : PrimaryNav.Concat( LibraryNav ).FirstOrDefault( i => i.Kind == ActiveSmartView )?.Icon ?? Symbol.Folder;
                InspectorSubtitle = IsSearchActive ? $"Matching \"{SearchText.Trim( )}\"" : "Library view";

                long bytes = CurrentItems.Sum( i => i.AsFile?.OriginalSize ?? 0 );
                InspectorRows.Add( new InspectorRow( "Showing", $"{CurrentItems.Count:N0} item{( CurrentItems.Count == 1 ? "" : "s" )}", Symbol.DocumentMultiple ) );
                InspectorRows.Add( new InspectorRow( "Total size", FileSizeConverter.Format( bytes ), Symbol.DataUsage ) );
            }
        }

        [RelayCommand]
        private async Task CopyInspectorHashAsync( )
        {
            if( !HasInspectorHash || OwnerWindow?.Clipboard is not { } clipboard ) return;
            await clipboard.SetTextAsync( InspectorHash.ToLowerInvariant( ) );
            StatusMessage = "SHA-256 copied to the clipboard";
        }

        /// <summary>Goes to the folder that holds the inspected item and selects it there.</summary>
        [RelayCommand]
        private async Task RevealInspectedAsync( )
        {
            if( InspectedItem is not { } item ) return;

            string? folderId = item.AsFile?.DirectoryId ?? item.AsDir?.ParentId;
            if( folderId == null ) return;

            await NavigateToDirectoryAsync( folderId );
            var match = CurrentItems.FirstOrDefault( i => i.Id == item.Id );
            if( match != null ) SelectedFile = match;
        }

        private static string DescribeCounts( DirectoryStats s ) =>
            $"{s.FileCount:N0} file{( s.FileCount == 1 ? "" : "s" )}, {s.FolderCount:N0} folder{( s.FolderCount == 1 ? "" : "s" )}";

        private static string FormatFullDate( DateTime utc ) =>
            utc.ToLocalTime( ).ToString( "MMM d, yyyy 'at' HH:mm" );
    }
}
