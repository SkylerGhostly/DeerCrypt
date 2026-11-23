using CommunityToolkit.Mvvm.ComponentModel;
using DeerCrypt.Converters;

namespace DeerCrypt.ViewModels.Dialogs
{
    /// <summary>
    /// Confirmation for compacting. The compaction itself runs in the background as an activity,
    /// so this dialog only explains what will happen and asks.
    /// </summary>
    public partial class CompactDialogViewModel( long reclaimableBytes ) : ObservableObject
    {
        public bool HasSpaceToReclaim { get; } = reclaimableBytes > 0;

        public string ReclaimableText { get; } = FileSizeConverter.Format( reclaimableBytes );

        public string InfoText { get; } = reclaimableBytes > 0
            ? "Compacting rewrites free space left behind by deleted files so the vault file gets smaller."
            : "There's no free space to reclaim. The vault is already compact.";
    }
}
