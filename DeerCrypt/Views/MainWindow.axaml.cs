using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeerCrypt.Services;
using DeerCrypt.ViewModels;
using DeerCrypt.ViewModels.Dialogs;
using DeerCrypt.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeerCrypt.Views
{
    public partial class MainWindow : Window
    {
        // Vault browser reference 
        // Updated every time a vault is opened; all WireFileList handlers read
        // this field so they work correctly after open→close→open cycles without
        // needing to re-subscribe to events (which would stack duplicate handlers).
        private VaultBrowserViewModel? _currentBrowser;
        private bool                   _fileListWired;
        private MainViewModel?         _subscribedVm;

        private static readonly DataFormat<string> _vaultItemsFormat =
            DataFormat.CreateStringApplicationFormat( "deercrypt.vault-items" );

        private Point              _pointerPressedAt;
        private bool               _isDragging;
        private bool               _suppressingForDrag;
        private VaultItemViewModel? _dragSuppressedItem;
        private bool               _pendingDragClear;

        public MainWindow( )
        {
            InitializeComponent( );
        }

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );

            // Unsubscribe from any previous DataContext before subscribing to the new one.
#pragma warning disable IDE0031 // Use null propagation
            if( _subscribedVm != null )
            {
                _subscribedVm.PropertyChanged -= OnMainVmPropertyChanged;
                _subscribedVm = null;
            }
#pragma warning restore IDE0031 // Use null propagation

            if( DataContext is MainViewModel vm )
            {
                _subscribedVm = vm;
                vm.PropertyChanged += OnMainVmPropertyChanged;
            }
        }

        private void OnMainVmPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs args )
        {
            if( args.PropertyName != nameof( MainViewModel.VaultBrowser ) ) return;
            if( DataContext is not MainViewModel vm ) return;

            if( vm.VaultBrowser != null )
            {
                vm.VaultBrowser.OwnerWindow = this;
                // Update the reference used by all already-wired handlers, then
                // wire the file list controls exactly once on the first open.
                _currentBrowser = vm.VaultBrowser;
                WireFileList( );

                // FocusRenameBox must be re-assigned for each new VM instance.
                var list = this.FindControl<ListBox>( "FileList" );
                if( list != null ) SetFocusRenameBoxDelegate( list );
            }
            else
            {
                // Vault closed - shut every window that isn't the main window.
                // Iterating desktop.Windows covers all current and future window
                // types without needing any per-type logic here.
                if( Application.Current?.ApplicationLifetime
                        is IClassicDesktopStyleApplicationLifetime desktop )
                {
                    foreach( var w in desktop.Windows.Where( w => w != this ).ToList( ) )
                        w.Close( );
                }
            }
        }

        /// <summary>
        /// Wires all file-list and nav-bar event handlers exactly ONCE.
        /// Every handler reads <see cref="_currentBrowser"/> at call-time so it
        /// automatically works with whichever vault is currently open, without
        /// needing to re-subscribe on every open→close→open cycle (which would
        /// stack duplicate handlers and cause double-opens).
        /// </summary>
        private void WireFileList( )
        {
            if( _fileListWired ) return;
            _fileListWired = true;

            var list = this.FindControl<ListBox>("FileList");
            if( list == null ) return;

            // Double-click to navigate into directories (or ".." to go up)
            list.DoubleTapped += async ( _, _ ) =>
            {
                if( _currentBrowser is not { } browser ) return;
                if( list.SelectedItem is not VaultItemViewModel item ) return;

                if( item.IsParentDir )
                {
                    if( browser.Breadcrumb.Count >= 2 )
                        await browser.NavigateToBreadcrumbCommand.ExecuteAsync( browser.Breadcrumb [ ^2 ] );
                }
                else if( item.IsDir && item.AsDir is { } dir )
                    await browser.NavigateToDirectoryAsync( dir.Id );
                else if( item.IsFile )
                    await browser.OpenFileAsync( item );
            };

            // Enter/Escape for rename
            list.KeyDown += async ( _, e ) =>
            {
                if( _currentBrowser is not { } browser ) return;
                if( browser.SelectedFile?.IsRenaming != true ) return;

                if( e.Key == Key.Enter )
                {
                    await browser.CommitRenameAsync( browser.SelectedFile );
                    e.Handled = true;
                }
                else if( e.Key == Key.Escape )
                {
                    browser.SelectedFile.CancelRename( );
                    e.Handled = true;
                }
            };

            // Sync multi-selection to ViewModel
            list.SelectionChanged += ( _, _ ) =>
            {
                if( _currentBrowser is not { } browser ) return;
                browser.SelectedItems.Clear( );
                foreach( var item in list.SelectedItems!.OfType<VaultItemViewModel>( ) )
                    browser.SelectedItems.Add( item );
                browser.SelectedFile = list.SelectedItem as VaultItemViewModel;
            };

            // Tunnel-phase: intercept before ListBox's OnPointerPressed changes selection.
            list.AddHandler( InputElement.PointerPressedEvent, ( _, e ) =>
            {
                _pointerPressedAt   = e.GetPosition( list );
                _suppressingForDrag = false;
                _dragSuppressedItem = null;

                if( !e.GetCurrentPoint( list ).Properties.IsLeftButtonPressed ) return;
                if( _currentBrowser is not { } browser ) return;

                var hovered = GetHoveredItem( list, e.Source as Control );

                if( hovered == null )
                {
                    list.SelectedItem    = null;
                    browser.SelectedFile = null;
                    e.Handled            = true;
                    return;
                }

                if( e.KeyModifiers == KeyModifiers.None
                    && browser.SelectedItems.Count > 1
                    && browser.SelectedItems.Contains( hovered ) )
                {
                    e.Handled           = true;
                    _suppressingForDrag = true;
                    _dragSuppressedItem = hovered;
                }
            }, RoutingStrategies.Tunnel );

            list.PointerReleased += ( _, _ ) =>
            {
                if( !_suppressingForDrag ) return;
                _suppressingForDrag = false;

                if( !_isDragging && _dragSuppressedItem != null )
                    list.SelectedItem = _dragSuppressedItem;

                _dragSuppressedItem = null;
            };

            // Initiate internal vault drag-drop
            list.PointerMoved += async ( _, e ) =>
            {
                if( _isDragging ) return;
                if( !e.GetCurrentPoint( list ).Properties.IsLeftButtonPressed ) return;
                if( _currentBrowser is not { } browser ) return;

                var draggable = browser.SelectedItems
                    .Where( i => !i.IsParentDir )
                    .ToArray();
                if( draggable.Length == 0 ) return;

                var pos   = e.GetPosition( list );
                var delta = pos - _pointerPressedAt;
                if( Math.Abs( delta.X ) < 5 && Math.Abs( delta.Y ) < 5 ) return;

                _suppressingForDrag = false;
                _isDragging         = true;
                try
                {
                    var item = DataTransferItem.Create( _vaultItemsFormat,
                        string.Join( '\n', draggable.Select( i => i.Item.Id ) ) );
                    var data = new DataTransfer( );
                    data.Add( item );
                    await DragDrop.DoDragDropAsync( e, data, DragDropEffects.Move );
                }
                finally
                {
                    _isDragging = false;
                    if( _currentBrowser is { } b ) b.DragOverItem = null;
                }
            };

            // Focus rename box when rename starts - updated via the field, not captured
            // (FocusRenameBox is re-assigned every time WireFileList is skipped after
            // the guard, but we still need to set it once here for the initial open)
            // NOTE: _currentBrowser changes on each open, so we read it dynamically.
            // The property assignment below runs only on first wire; subsequent opens
            // update _currentBrowser before VaultBrowser.FocusRenameBox is read.

            // Enable drop on the list
            DragDrop.SetAllowDrop( list, true );

            list.AddHandler( DragDrop.DragOverEvent, ( _, e ) =>
            {
                _pendingDragClear = false;

                var element = e.Source as Control;
                var hovered = GetHoveredItem( list, element );
                var browser = _currentBrowser;

                if( e.DataTransfer.Contains( _vaultItemsFormat ) )
                {
                    var ids = ( e.DataTransfer.TryGetValue( _vaultItemsFormat )
                        ?.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
                        ?.ToHashSet( ) ?? [ ];
                    bool valid    = hovered != null && hovered.IsDir
                                    && !ids.Contains( hovered.AsDir!.Id );
                    var newTarget = valid ? hovered : null;
                    e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
                    if( browser != null && !ReferenceEquals( newTarget, browser.DragOverItem ) )
                        browser.DragOverItem = newTarget;
                    e.Handled = true;
                    return;
                }

                if( e.DataTransfer.Contains( DataFormat.File ) )
                {
                    e.DragEffects = DragDropEffects.Copy;
                    var newTarget = hovered?.IsDir == true ? hovered : null;
                    if( browser != null && !ReferenceEquals( newTarget, browser.DragOverItem ) )
                        browser.DragOverItem = newTarget;
                }
                else
                {
                    e.DragEffects = DragDropEffects.None;
                    if( browser?.DragOverItem != null ) browser.DragOverItem = null;
                }

                e.Handled = true;
            } );

            list.AddHandler( DragDrop.DragLeaveEvent, ( _, _ ) =>
            {
                _pendingDragClear = true;
                Dispatcher.UIThread.Post( ( ) =>
                {
                    if( _pendingDragClear )
                    {
                        _pendingDragClear = false;
                        if( _currentBrowser is { } b ) b.DragOverItem = null;
                    }
                } );
            } );

            list.AddHandler( DragDrop.DropEvent, async ( _, e ) =>
            {
                if( _currentBrowser is { } b ) b.DragOverItem = null;

                if( e.DataTransfer.Contains( _vaultItemsFormat ) )
                {
                    var ids     = e.DataTransfer.TryGetValue( _vaultItemsFormat )
                        ?.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) ?? [];
                    var hovered = GetHoveredItem( list, e.Source as Control );
                    if( hovered == null || !hovered.IsDir || _currentBrowser == null )
                    {
                        e.Handled = true;
                        return;
                    }
                    await _currentBrowser.MoveItemsAsync( ids, hovered.AsDir!.Id );
                    e.Handled = true;
                    return;
                }

                if( !e.DataTransfer.Contains( DataFormat.File ) || _currentBrowser == null ) return;

                var files = e.DataTransfer.TryGetFiles( )?.ToList( );
                if( files == null || files.Count == 0 ) return;

                var target = GetHoveredItem( list, e.Source as Control );
                string targetDirectoryId = target?.IsDir == true
                    ? target.AsDir!.Id
                    : _currentBrowser.CurrentDirectoryId;

                List<string> filePaths   = [];
                List<string> folderPaths = [];

                foreach( var item in files )
                {
                    string path = item.Path.LocalPath
                        .TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );

                    if( Directory.Exists( path ) )
                        folderPaths.Add( path );
                    else if( File.Exists( path ) )
                        filePaths.Add( path );
                }

                await _currentBrowser.HandleDropAsync( filePaths, folderPaths, targetDirectoryId );
                e.Handled = true;
            } );

            // Wire NavBar breadcrumb drag-drop (also wired once)
            var navBar = this.FindControl<Border>("NavBar");
            if( navBar != null )
            {
                navBar.AddHandler( DragDrop.DragOverEvent, ( _, e ) =>
                {
                    if( !e.DataTransfer.Contains( _vaultItemsFormat ) || _currentBrowser == null ) return;
                    var seg   = FindBreadcrumbSegment( navBar, e.GetPosition( navBar ) );
                    bool valid = seg != null && seg.DirectoryId != _currentBrowser.CurrentDirectoryId;
                    e.DragEffects                         = valid ? DragDropEffects.Move : DragDropEffects.None;
                    _currentBrowser.BreadcrumbDragTargetId = valid ? seg!.DirectoryId : null;
                    e.Handled                             = true;
                }, RoutingStrategies.Bubble );

                navBar.AddHandler( DragDrop.DragLeaveEvent, ( _, _ ) =>
                {
                    _currentBrowser?.BreadcrumbDragTargetId = null;
                }, RoutingStrategies.Bubble );

                navBar.AddHandler( DragDrop.DropEvent, async ( _, e ) =>
                {
                    if( !e.DataTransfer.Contains( _vaultItemsFormat ) || _currentBrowser == null ) return;
                    var ids = e.DataTransfer.TryGetValue( _vaultItemsFormat )
                        ?.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) ?? [];
                    var seg = FindBreadcrumbSegment( navBar, e.GetPosition( navBar ) );
                    _currentBrowser.BreadcrumbDragTargetId = null;
                    if( seg == null || seg.DirectoryId == _currentBrowser.CurrentDirectoryId ) return;
                    await _currentBrowser.MoveItemsAsync( ids, seg.DirectoryId );
                    e.Handled = true;
                }, RoutingStrategies.Bubble );
            }

            // Wire FocusRenameBox - must be re-assigned every vault open since each
            // VaultBrowserViewModel is a new instance.  Do it here on first wire AND
            // in the PropertyChanged handler above (via the _currentBrowser update).
            // We use a small helper to pick up _currentBrowser at call-time.
            SetFocusRenameBoxDelegate( list );
        }

        /// <summary>
        /// (Re-)assigns VaultBrowserViewModel.FocusRenameBox so it points at the
        /// currently active <see cref="_currentBrowser"/>.  Called once on first wire
        /// and again whenever <see cref="_currentBrowser"/> is updated.
        /// </summary>
        private void SetFocusRenameBoxDelegate( ListBox list )
        {
            if( _currentBrowser == null ) return;
            _currentBrowser.FocusRenameBox = ( ) =>
            {
                var listBoxItem = list.ContainerFromItem( _currentBrowser?.SelectedFile! )
                    as ListBoxItem;
                listBoxItem?.FindDescendantOfType<TextBox>( )?.Focus( );
            };
        }

        private void OnRenameBoxGotFocus( object? sender, GotFocusEventArgs e )
        {
            if( sender is TextBox tb )
                tb.SelectAll( );
        }

        protected override async void OnClosing( WindowClosingEventArgs e )
        {
            if( DataContext is MainViewModel { IsVaultOpen: true } vm )
            {
                // Cancel the close immediately and handle it asynchronously
                e.Cancel = true;

                var confirmVm = new ConfirmDialogViewModel
                {
                    Title       = "Close DeerCrypt",
                    Message     = "A vault is currently open. Close anyway?",
                    Detail      = "The vault will be locked before the application exits.",
                    ConfirmText = "Close",
                    CancelText  = "Cancel"
                };

                bool? result = await new ConfirmDialog { DataContext = confirmVm }
                    .ShowDialog<bool?>(this);

                if( result == true )
                {
                    vm.IsVaultOpen = false; // Update VM state immediately to prevent reentrancy issues
                    await _vaultService.CloseAsync( );
                    Close( );
                }
            }
            else
            {
                base.OnClosing( e );
            }
        }

        private static VaultItemViewModel? GetHoveredItem( ListBox _, Control? element )
        {
            var current = element;
            while( current != null )
            {
                if( current is ListBoxItem { DataContext: VaultItemViewModel vm } )
                    return vm;
                current = current.Parent as Control;
            }
            return null;
        }

        private static BreadcrumbSegment? FindBreadcrumbSegment( Control root, Point pos )
        {
            foreach( var visual in root.GetVisualsAt( pos ) )
                if( visual is Control c && c.DataContext is BreadcrumbSegment seg )
                    return seg;
            return null;
        }

        private async void OnRenameBoxLostFocus( object? sender, RoutedEventArgs e )
        {
            if( DataContext is not MainViewModel { VaultBrowser: { } browser } ) return;
            if( sender is not TextBox { DataContext: VaultItemViewModel item } ) return;
            if( !item.IsRenaming ) return;

            // Commit if the name changed and is valid, otherwise cancel
            string newName = item.EditingName.Trim();
            string? error  = VaultBrowserViewModel.ValidateItemName(newName);

            if( error != null || newName == item.DisplayName )
                item.CancelRename( );
            else
                await browser.CommitRenameAsync( item );
        }

        private readonly VaultService _vaultService = App.VaultService;
    }
}
