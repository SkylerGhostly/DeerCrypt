using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace DeerCrypt.ViewModels
{
    /// <summary>
    /// Tracks background operations for one open vault. Running work is listed first,
    /// followed by a short history of finished work. The vault must not be closed while
    /// anything is running: callers use <see cref="CancelAllAndWaitAsync"/> first.
    /// </summary>
    public partial class ActivityCenter : ObservableObject
    {
        private const int HistoryLimit = 25;
        private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds( 6 );

        private readonly Dictionary<ActivityItem, Task> _running = [ ];
        private readonly DispatcherTimer _recentTimer;

        public ObservableCollection<ActivityItem> Items { get; } = [ ];

        public ActivityCenter( )
        {
            _recentTimer = new DispatcherTimer { Interval = RecentWindow };
            _recentTimer.Tick += ( _, _ ) =>
            {
                _recentTimer.Stop( );
                ShowRecentResult = false;
                Raise( );
            };
        }

        public bool HasRunning   => _running.Count > 0;
        public int  RunningCount => _running.Count;
        public bool HasItems     => Items.Count > 0;

        /// <summary>The operation featured in the status bar: the newest running one, else the one that just finished.</summary>
        public ActivityItem? Current =>
            Items.FirstOrDefault( i => i.IsRunning ) ?? ( ShowRecentResult ? Items.FirstOrDefault( ) : null );

        /// <summary>True while the status bar should show <see cref="Current"/> instead of the plain status message.</summary>
        public bool ShowStrip => Current != null;

        public string BadgeText => RunningCount > 1 ? $"+{RunningCount - 1}" : string.Empty;
        public bool   HasBadge  => RunningCount > 1;

        [ObservableProperty]
        public partial bool ShowRecentResult { get; set; }

        /// <summary>
        /// Starts tracking <paramref name="item"/> and runs <paramref name="work"/>. The returned task
        /// completes when the work ends; exceptions are recorded on the item, never rethrown.
        /// </summary>
        public async Task RunAsync( ActivityItem item, Func<ActivityItem, Task> work, Func<ActivityItem, string>? describeSuccess = null )
        {
            Items.Insert( 0, item );
            item.PropertyChanged += OnItemChanged;
            TrimHistory( );

            Task task = RunCoreAsync( item, work, describeSuccess );
            _running [ item ] = task;
            Raise( );

            try { await task; }
            finally
            {
                _running.Remove( item );
                ShowRecentResult = true;
                _recentTimer.Stop( );
                _recentTimer.Start( );
                Raise( );
            }
        }

        private static async Task RunCoreAsync( ActivityItem item, Func<ActivityItem, Task> work, Func<ActivityItem, string>? describeSuccess )
        {
            try
            {
                await work( item );

                if( item.IsCancelled )
                {
                    item.State      = ActivityState.Cancelled;
                    item.ResultText = "Cancelled";
                }
                else if( item.Failures.Count > 0 )
                {
                    item.State      = ActivityState.Failed;
                    item.ResultText = item.Failures.Count == 1
                        ? $"Finished with 1 problem - {item.Failures [ 0 ]}"
                        : $"Finished with {item.Failures.Count} problems - first: {item.Failures [ 0 ]}";
                }
                else
                {
                    item.State      = ActivityState.Completed;
                    item.ResultText = describeSuccess?.Invoke( item ) ?? "Done";
                }
            }
            catch( OperationCanceledException )
            {
                item.State      = ActivityState.Cancelled;
                item.ResultText = "Cancelled";
            }
            catch( ObjectDisposedException )
            {
                item.State      = ActivityState.Cancelled;
                item.ResultText = "Stopped because the vault was locked";
            }
            catch( Exception ex )
            {
                item.State      = ActivityState.Failed;
                item.ResultText = ex.Message;
            }
            finally
            {
                item.Progress        = item.State == ActivityState.Completed ? 1 : item.Progress;
                item.IsIndeterminate = false;
                item.CurrentFileName = string.Empty;
                item.StatusText      = string.Empty;
            }
        }

        /// <summary>Cancels everything that is running and waits for it to stop.</summary>
        public async Task CancelAllAndWaitAsync( )
        {
            foreach( var item in _running.Keys.ToList( ) )
                item.RequestCancel( );

            // Some steps (e.g. one file's transaction) can't be interrupted midway; wait for them
            await Task.WhenAll( _running.Values.ToList( ) );
        }

        [RelayCommand]
        private void ClearFinished( )
        {
            foreach( var item in Items.Where( i => i.IsFinished ).ToList( ) )
            {
                item.PropertyChanged -= OnItemChanged;
                Items.Remove( item );
            }
            ShowRecentResult = false;
            Raise( );
        }

        private void TrimHistory( )
        {
            while( Items.Count > HistoryLimit && Items.LastOrDefault( i => i.IsFinished ) is { } old )
            {
                old.PropertyChanged -= OnItemChanged;
                Items.Remove( old );
            }
        }

        private void OnItemChanged( object? sender, System.ComponentModel.PropertyChangedEventArgs e )
        {
            if( e.PropertyName == nameof( ActivityItem.State ) ) Raise( );
        }

        private void Raise( )
        {
            OnPropertyChanged( nameof( HasRunning ) );
            OnPropertyChanged( nameof( RunningCount ) );
            OnPropertyChanged( nameof( HasItems ) );
            OnPropertyChanged( nameof( Current ) );
            OnPropertyChanged( nameof( ShowStrip ) );
            OnPropertyChanged( nameof( BadgeText ) );
            OnPropertyChanged( nameof( HasBadge ) );
        }
    }
}
