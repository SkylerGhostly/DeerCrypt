using CommunityToolkit.Mvvm.ComponentModel;

namespace DeerCrypt.ViewModels.Dialogs
{
    public partial class ConfirmDialogViewModel : ObservableObject
    {
        public string Title { get; init; } = "Confirm";
        public string Message { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string ConfirmText { get; init; } = "OK";
        public string CancelText { get; init; } = "Cancel";
    }
}