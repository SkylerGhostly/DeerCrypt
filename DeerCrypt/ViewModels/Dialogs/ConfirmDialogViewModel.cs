using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;

namespace DeerCrypt.ViewModels.Dialogs
{
    /// <summary>Visual intent of a confirmation dialog: drives its icon, icon color, and confirm button.</summary>
    public enum DialogTone { Neutral, Info, Success, Warning, Danger }

    public partial class ConfirmDialogViewModel : ObservableObject
    {
        public string Title { get; init; } = "Confirm";
        public string Message { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string ConfirmText { get; init; } = "OK";
        public string CancelText { get; init; } = "Cancel";
        public DialogTone Tone { get; init; } = DialogTone.Neutral;

        public Symbol Icon => Tone switch
        {
            DialogTone.Success => Symbol.CheckmarkCircle,
            DialogTone.Warning => Symbol.Warning,
            DialogTone.Danger  => Symbol.Delete,
            DialogTone.Info    => Symbol.Info,
            _                  => Symbol.QuestionCircle
        };

        public bool IsDanger  => Tone == DialogTone.Danger;
        public bool IsSuccess => Tone == DialogTone.Success;
        public bool IsWarning => Tone == DialogTone.Warning;
        public bool IsNeutral => Tone is DialogTone.Neutral or DialogTone.Info;
        public bool HasCancel => !string.IsNullOrEmpty( CancelText );
        public bool HasDetail => !string.IsNullOrEmpty( Detail );
    }
}
