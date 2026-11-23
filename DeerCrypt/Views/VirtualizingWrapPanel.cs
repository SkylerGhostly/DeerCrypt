using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace DeerCrypt.Views
{
    /// <summary>
    /// A wrapping grid of equally sized tiles that only creates containers for the rows on
    /// screen (plus one row either side), reusing them as you scroll. Used as the items panel of
    /// the file list in grid view, so selection, drag and drop, rename, and the context menu keep
    /// working unchanged while a 20,000+ item folder opens instantly.
    ///
    /// Layout: the panel reports its full height (rows × tile height) so the ScrollViewer gets a
    /// correct extent; the visible window comes from <see cref="Layoutable.EffectiveViewportChanged"/>.
    /// Every tile has the size of the first realized container (all tiles share one template).
    /// </summary>
    public sealed class VirtualizingWrapPanel : VirtualizingPanel
    {
        private static readonly Size FallbackItemSize = new( 124, 130 );

        private readonly Dictionary<int, Control> _realized     = [ ];
        private readonly Dictionary<Control, int> _indexOf      = [ ];
        private readonly Stack<Control>           _recyclePool  = new( );

        private Size  _itemSize = Size.Infinity;  // unknown until the first container is measured
        private int   _columns  = 1;
        private Rect  _viewport;
        private bool  _hasViewport;

        public VirtualizingWrapPanel( )
        {
            EffectiveViewportChanged += ( _, e ) =>
            {
                var old = _viewport;
                _viewport    = e.EffectiveViewport;
                _hasViewport = true;

                // Only re-measure when the change can bring different rows into view
                if( !SameRows( old, _viewport ) ) InvalidateMeasure( );
            };
        }

        private bool SameRows( Rect a, Rect b )
        {
            if( double.IsInfinity( _itemSize.Height ) || _itemSize.Height <= 0 ) return false;
            int Row( double y ) => (int)Math.Floor( y / _itemSize.Height );
            return Row( a.Top ) == Row( b.Top ) && Row( a.Bottom ) == Row( b.Bottom ) && Math.Abs( a.Width - b.Width ) < 0.5;
        }

        // Measure / arrange

        protected override Size MeasureOverride( Size availableSize )
        {
            var items = Items;
            int count = items.Count;
            if( count == 0 )
            {
                RecycleAll( );
                return default;
            }

            if( double.IsInfinity( _itemSize.Width ) ) MeasureItemSize( items );

            double width = double.IsInfinity( availableSize.Width ) ? _itemSize.Width * 4 : availableSize.Width;
            _columns = Math.Max( 1, (int)Math.Floor( width / _itemSize.Width ) );
            int rows = (int)Math.Ceiling( count / (double)_columns );

            var (first, last) = VisibleRange( count, availableSize );

            // Recycle what scrolled out of range
            foreach( var kv in _realized.Where( kv => kv.Key < first || kv.Key > last ).ToList( ) )
                Recycle( kv.Key, kv.Value );

            // Realize what scrolled in. Tiles are measured unconstrained so a template change
            // (density switch) is noticed: the tile size is re-learned and layout runs again.
            Size seen = default;
            for( int i = first; i <= last; i++ )
            {
                if( !_realized.TryGetValue( i, out var c ) ) c = Realize( i, items [ i ] );
                c.Measure( Size.Infinity );
                if( i == first ) seen = c.DesiredSize;
            }
            if( seen.Width > 0 && seen.Height > 0
                && ( Math.Abs( seen.Width - _itemSize.Width ) > 0.5 || Math.Abs( seen.Height - _itemSize.Height ) > 0.5 ) )
            {
                _itemSize = seen;
                InvalidateMeasure( );
            }

            return new Size( width, rows * _itemSize.Height );
        }

        protected override Size ArrangeOverride( Size finalSize )
        {
            foreach( var (index, c) in _realized )
                c.Arrange( RectForIndex( index ) );
            return finalSize;
        }

        private Rect RectForIndex( int index )
        {
            int row = index / _columns, col = index % _columns;
            return new Rect( col * _itemSize.Width, row * _itemSize.Height, _itemSize.Width, _itemSize.Height );
        }

        /// <summary>First and last item index to realize: the visible rows plus one row of slack each way.</summary>
        private (int First, int Last) VisibleRange( int count, Size availableSize )
        {
            double top    = _hasViewport ? _viewport.Top    : 0;
            double bottom = _hasViewport ? _viewport.Bottom : ( double.IsInfinity( availableSize.Height ) ? 1200 : availableSize.Height );
            if( bottom <= top ) bottom = top + 1200;

            int firstRow = Math.Max( 0, (int)Math.Floor( top / _itemSize.Height ) - 1 );
            int lastRow  = (int)Math.Ceiling( bottom / _itemSize.Height ) + 1;

            int first = Math.Min( count - 1, firstRow * _columns );
            int last  = Math.Min( count - 1, ( lastRow + 1 ) * _columns - 1 );
            return (first, last);
        }

        /// <summary>Tile size comes from measuring one real container (tiles share a fixed-size template).</summary>
        private void MeasureItemSize( IReadOnlyList<object?> items )
        {
            var probe = _realized.TryGetValue( 0, out var existing ) ? existing : Realize( 0, items [ 0 ] );
            probe.Measure( Size.Infinity );
            var d = probe.DesiredSize;
            _itemSize = d.Width > 0 && d.Height > 0 ? d : FallbackItemSize;
        }

        // Containers

        private Control Realize( int index, object? item )
        {
            var generator = ItemContainerGenerator!;
            Control container;

            if( _recyclePool.Count > 0 )
            {
                container = _recyclePool.Pop( );
                container.IsVisible = true;
            }
            else if( generator.NeedsContainer( item, index, out object? recycleKey ) )
            {
                container = generator.CreateContainer( item, index, recycleKey );
                AddInternalChild( container );
            }
            else
            {
                // The item is its own container (not the case for ListBox, but stay correct)
                container = (Control)item!;
                AddInternalChild( container );
            }

            generator.PrepareItemContainer( container, item, index );
            generator.ItemContainerPrepared( container, item, index );
            _realized [ index ]   = container;
            _indexOf [ container ] = index;
            return container;
        }

        private void Recycle( int index, Control container )
        {
            _realized.Remove( index );
            _indexOf.Remove( container );
            ItemContainerGenerator!.ClearItemContainer( container );
            container.IsVisible = false; // kept as a child for reuse; hidden so it neither renders nor hit-tests
            _recyclePool.Push( container );
        }

        private void RecycleAll( )
        {
            foreach( var kv in _realized.ToList( ) ) Recycle( kv.Key, kv.Value );
        }

        protected override void OnItemsChanged( IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e )
        {
            base.OnItemsChanged( items, e );

            // Appending after everything on screen only changes the extent; anything else can shift
            // indices, so drop the realized set and rebuild it on the next measure.
            int lastRealized = _realized.Count == 0 ? -1 : _realized.Keys.Max( );
            bool appendBeyondView = e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex > lastRealized;
            if( !appendBeyondView ) RecycleAll( );

            if( items.Count == 0 ) _itemSize = Size.Infinity; // re-learn the tile size (density may have changed)
            InvalidateMeasure( );
        }

        protected override Control? ContainerFromIndex( int index ) =>
            _realized.TryGetValue( index, out var c ) ? c : null;

        protected override int IndexFromContainer( Control container ) =>
            _indexOf.TryGetValue( container, out int i ) ? i : -1;

        protected override IEnumerable<Control>? GetRealizedContainers( ) => _realized.Values;

        protected override Control? ScrollIntoView( int index )
        {
            var items = Items;
            if( index < 0 || index >= items.Count ) return null;
            if( double.IsInfinity( _itemSize.Width ) ) MeasureItemSize( items );

            if( !_realized.TryGetValue( index, out var container ) )
            {
                container = Realize( index, items [ index ] );
                container.Measure( Size.Infinity );
            }
            container.Arrange( RectForIndex( index ) );
            container.BringIntoView( );
            return container;
        }

        /// <summary>Arrow keys move through the grid: left/right by one, up/down by a whole row.</summary>
        protected override IInputElement? GetControl( NavigationDirection direction, IInputElement? from, bool wrap )
        {
            int count = Items.Count;
            if( count == 0 ) return null;

            int current = from is Control c && _indexOf.TryGetValue( c, out int i ) ? i : -1;
            int target = direction switch
            {
                NavigationDirection.Left     => current - 1,
                NavigationDirection.Right    => current + 1,
                NavigationDirection.Up       => current - _columns,
                NavigationDirection.Down     => current + _columns,
                NavigationDirection.First    => 0,
                NavigationDirection.Last     => count - 1,
                NavigationDirection.Next     => current + 1,
                NavigationDirection.Previous => current - 1,
                NavigationDirection.PageUp   => current - _columns * Math.Max( 1, (int)( _viewport.Height / _itemSize.Height ) ),
                NavigationDirection.PageDown => current + _columns * Math.Max( 1, (int)( _viewport.Height / _itemSize.Height ) ),
                _                            => current
            };

            if( current < 0 ) target = 0;
            if( target < 0 || target >= count )
            {
                if( !wrap ) target = Math.Clamp( target, 0, count - 1 );
                else target = ( target % count + count ) % count;
            }
            return ScrollIntoView( target );
        }
    }
}
