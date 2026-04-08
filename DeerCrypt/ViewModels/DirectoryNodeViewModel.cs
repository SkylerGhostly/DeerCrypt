using CommunityToolkit.Mvvm.ComponentModel;
using DeerCryptLib.Vault;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels
{
    public partial class DirectoryNodeViewModel( string directoryId, string name, bool isRoot = false ) : ObservableObject
    {
        public string DirectoryId { get; } = directoryId;

        [ObservableProperty]
        public partial string Name { get; set; } = name;

        [ObservableProperty]
        public partial bool IsExpanded { get; set; }
        public bool IsRoot { get; } = isRoot;

        public ObservableCollection<DirectoryNodeViewModel> Children { get; } = [ ];

        /// <summary>
        /// Loads immediate subdirectories from the vault into Children.
        /// Recursively loads their children too so the tree is fully populated on open.
        /// </summary>
        public async Task LoadChildrenAsync( VaultFile vault, CancellationToken cancellationToken = default )
        {
            Children.Clear( );

            VaultListing listing = await vault.ListAsync(DirectoryId, cancellationToken);

            foreach( VaultDirectoryEntry dir in listing.Directories )
            {
                var child = new DirectoryNodeViewModel(dir.Id, dir.Name);
                await child.LoadChildrenAsync( vault, cancellationToken );
                Children.Add( child );
            }

            IsExpanded = IsRoot;
        }
    }
}