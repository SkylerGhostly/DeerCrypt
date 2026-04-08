using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Threading;

namespace DeerCrypt.ViewModels.Dialogs
{
    public partial class ProgressDialogViewModel : ObservableObject
    {
        [ObservableProperty]
        public partial string Title { get; set; } = "Please wait...";

        [ObservableProperty]
        public partial string CurrentFileName { get; set; } = string.Empty;
        [ObservableProperty]
        public partial bool IsIndeterminate { get; set; } = false;

        [ObservableProperty]
        public partial double Progress { get; set; }

        [ObservableProperty]
        public partial string StatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool CanCancel { get; set; } = true;

        [ObservableProperty]
        public partial bool IsComplete { get; set; }

        public bool IsCancelled => _cts.IsCancellationRequested;

        private readonly CancellationTokenSource _cts = new();
        public CancellationToken CancellationToken => _cts.Token;

        public void Cancel( ) => _cts.Cancel( );

        public void Complete( )
        {
            IsComplete = true;
            Progress = 1.0;
            StatusText = "Done.";
        }
    }
}