using CommunityToolkit.Mvvm.ComponentModel;
using DeerCrypt.Models;

namespace DeerCrypt.ViewModels.Dialogs
{
    public partial class AddFolderConfirmViewModel( FolderManifest manifest ) : ObservableObject
    {
        public string FolderName { get; } = manifest.RootName;
        public string SummaryText { get; } = $"Add \"{manifest.RootName}\" to the vault?";
        public string DetailText { get; } =
                $"{manifest.TotalFiles} file{( manifest.TotalFiles == 1 ? "" : "s" )}  •  " +
                $"{manifest.TotalFolders} folder{( manifest.TotalFolders == 1 ? "" : "s" )}  •  " +
                $"{FormatSize( manifest.TotalSize )}";

        private static string FormatSize( long bytes ) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / ( 1024.0 * 1024 ):F1} MB",
            _ => $"{bytes / ( 1024.0 * 1024 * 1024 ):F2} GB"
        };
    }
}