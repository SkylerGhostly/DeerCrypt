using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using System;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Viewers
{
    /// <summary>
    /// Something open in a tab (or a popped-out window): an image set, a media player, or a
    /// text file. Everything is decrypted in memory; nothing here writes plaintext to disk.
    /// </summary>
    public abstract partial class DocumentViewModel : ObservableObject, IDisposable
    {
        /// <summary>Vault ID of the file currently shown (changes as an image set or playlist moves).</summary>
        [ObservableProperty]
        public partial string FileId { get; protected set; } = string.Empty;

        [ObservableProperty]
        public partial string Title { get; protected set; } = string.Empty;

        public abstract Symbol  Icon      { get; }
        public abstract IBrush  IconBrush { get; }

        /// <summary>Unsaved edits (text editor only).</summary>
        [ObservableProperty]
        public partial bool IsDirty { get; set; }

        /// <summary>This document's tab is the one on screen (always true while popped out).</summary>
        [ObservableProperty]
        public partial bool IsActive { get; set; }

        [ObservableProperty]
        public partial bool IsPoppedOut { get; set; }

        /// <summary>Shown on the tab while the document is busy in the background (e.g. audio playing).</summary>
        [ObservableProperty]
        public partial bool HasBackgroundActivity { get; protected set; }

        partial void OnIsActiveChanged( bool value )
        {
            if( value ) OnActivated( ); else OnDeactivated( );
        }

        partial void OnIsDirtyChanged( bool value ) => OnDirtyChanged( value );

        protected virtual void OnActivated( ) { }
        protected virtual void OnDeactivated( ) { }
        protected virtual void OnDirtyChanged( bool value ) { }

        /// <summary>Called before the view is torn down to move between the tabs and a window.</summary>
        public virtual void BeforeMove( ) { }

        /// <summary>Called once the view has been rebuilt in its new place.</summary>
        public virtual void AfterMove( ) { }

        /// <summary>
        /// Asks about anything that would be lost (unsaved text). Returns false to keep it open.
        /// </summary>
        public virtual Task<bool> ConfirmCloseAsync( Window owner ) => Task.FromResult( true );

        /// <summary>The document wants to be closed (e.g. its last image was deleted).</summary>
        public event Action<DocumentViewModel>? CloseRequested;

        /// <summary>The document changed vault contents (saved, deleted) - the browser should refresh.</summary>
        public event Action<DocumentViewModel>? VaultChanged;

        /// <summary>The document wants full screen (pops out if needed).</summary>
        public event Action<DocumentViewModel>? FullscreenRequested;

        protected void RequestClose( )      => CloseRequested?.Invoke( this );
        protected void RaiseVaultChanged( ) => VaultChanged?.Invoke( this );
        public    void RequestFullscreen( ) => FullscreenRequested?.Invoke( this );

        public abstract void Dispose( );
    }
}
