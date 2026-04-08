using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeerCryptLib.Vault;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels.Dialogs
{
    public partial class CompactDialogViewModel( VaultFile vault, long reclaimableBytes ) : ObservableObject
    {

        [ObservableProperty]
        public partial string InfoText { get; set; } = reclaimableBytes > 0
                ? $"Compacting will reclaim approximately {FormatSize( reclaimableBytes )} of disk space."
                : "No disk space to reclaim. The vault is already compact.";

        [ObservableProperty]
        public partial bool IsCompacting { get; set; }

        [ObservableProperty]
        public partial double Progress { get; set; }

        private bool _isComplete;
        public bool IsComplete
        {
            get => _isComplete;
            private set => SetProperty( ref _isComplete, value );
        }

        [RelayCommand]
        private async Task CompactAsync( CancellationToken cancellationToken )
        {
            IsCompacting = true;
            Progress = 0;

            try
            {
                IProgress<double> progress = new Progress<double>(p =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = p));

                await Task.Run( async ( ) =>
                    await vault.CompactAsync(
                        progress: progress,
                        cancellationToken: cancellationToken ),
                    cancellationToken );
            }
            finally
            {
                IsCompacting = false;
                IsComplete = true;
            }
        }

        private static string FormatSize( long bytes ) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / ( 1024.0 * 1024 ):F1} MB",
            _ => $"{bytes / ( 1024.0 * 1024 * 1024 ):F2} GB"
        };
    }
}