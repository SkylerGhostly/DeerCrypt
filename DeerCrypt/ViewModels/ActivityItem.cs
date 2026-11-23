using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using System;
using System.Collections.Generic;
using System.Threading;

namespace DeerCrypt.ViewModels
{
    public enum ActivityState { Running, Completed, Cancelled, Failed }

    /// <summary>
    /// One background operation (adding, extracting, deleting, compacting, ...) shown in the
    /// activity bar. Workers update it from the UI thread; it never blocks the window.
    /// </summary>
    public partial class ActivityItem( string title, Symbol icon, bool canCancel = true ) : ObservableObject
    {
        public Symbol   Icon      { get; } = icon;
        public DateTime StartedAt { get; } = DateTime.Now;

        [ObservableProperty]
        public partial string Title { get; set; } = title;

        /// <summary>The file currently being worked on.</summary>
        [ObservableProperty]
        public partial string CurrentFileName { get; set; } = string.Empty;

        /// <summary>Secondary progress text, e.g. "3 of 12".</summary>
        [ObservableProperty]
        public partial string StatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsIndeterminate { get; set; }

        /// <summary>0.0 - 1.0</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( PercentText ) )]
        public partial double Progress { get; set; }

        public string PercentText => IsIndeterminate ? string.Empty : $"{Math.Round( Math.Clamp( Progress, 0, 1 ) * 100 )}%";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor( nameof( CancelCommand ) )]
        public partial bool CanCancel { get; set; } = canCancel;

        [ObservableProperty]
        [NotifyPropertyChangedFor( nameof( IsRunning ) )]
        [NotifyPropertyChangedFor( nameof( IsFinished ) )]
        [NotifyPropertyChangedFor( nameof( StateIcon ) )]
        [NotifyPropertyChangedFor( nameof( IsSucceeded ) )]
        [NotifyPropertyChangedFor( nameof( IsFailedState ) )]
        [NotifyPropertyChangedFor( nameof( IsCancelledState ) )]
        [NotifyCanExecuteChangedFor( nameof( CancelCommand ) )]
        public partial ActivityState State { get; set; } = ActivityState.Running;

        /// <summary>One-line outcome shown once the operation ends.</summary>
        [ObservableProperty]
        public partial string ResultText { get; set; } = string.Empty;

        public bool IsRunning  => State == ActivityState.Running;
        public bool IsFinished => State != ActivityState.Running;
        public bool IsSucceeded      => State == ActivityState.Completed;
        public bool IsFailedState    => State == ActivityState.Failed;
        public bool IsCancelledState => State == ActivityState.Cancelled;

        public string StartedText => StartedAt.ToString( "HH:mm" );

        public Symbol StateIcon => State switch
        {
            ActivityState.Completed => Symbol.CheckmarkCircle,
            ActivityState.Cancelled => Symbol.DismissCircle,
            ActivityState.Failed    => Symbol.ErrorCircle,
            _                       => Icon
        };

        /// <summary>Per-file problems that didn't stop the whole operation.</summary>
        public List<string> Failures { get; } = [ ];

        public void AddFailure( string name, string message ) => Failures.Add( $"{name}: {message}" );

        private readonly CancellationTokenSource _cts = new( );
        public CancellationToken CancellationToken => _cts.Token;
        public bool IsCancelled => _cts.IsCancellationRequested;

        public void RequestCancel( )
        {
            if( !_cts.IsCancellationRequested ) _cts.Cancel( );
        }

        private bool CanCancelNow => CanCancel && IsRunning;

        [RelayCommand( CanExecute = nameof( CanCancelNow ) )]
        private void Cancel( )
        {
            RequestCancel( );
            StatusText = "Cancelling…";
        }
    }
}
