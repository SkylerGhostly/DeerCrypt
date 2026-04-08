using System;
using Avalonia;
using Avalonia.Controls;

namespace DeerCrypt.Views
{
    /// <summary>
    /// A horizontal panel for breadcrumb items that trims left-most children when
    /// they would overflow the available width, matching Windows Explorer behaviour.
    /// The last two children are always kept visible; if they still don't fit their
    /// widths are distributed proportionally so the inner TextBlock's TextTrimming
    /// can show ellipsis.
    ///
    /// Hidden items are placed just off the left edge and clipped rather than
    /// collapsed via IsVisible, which avoids an invalidation loop that arises from
    /// invisible controls measuring as zero (changing the layout decision each pass).
    /// </summary>
    internal sealed class BreadcrumbPanel : Panel
    {
        private const double MinCrumbWidth = 32;

        // Natural widths cached from MeasureOverride so ArrangeOverride can reuse them.
        private double[] _naturalWidths = [];

        public BreadcrumbPanel()
        {
            ClipToBounds = true;
        }

        protected override Size MeasureOverride( Size availableSize )
        {
            var children = Children;
            int count    = children.Count;
            if( count == 0 ) return default;

            if( _naturalWidths.Length != count )
                _naturalWidths = new double[ count ];

            double totalWidth = 0;
            double maxHeight  = 0;

            for( int i = 0; i < count; i++ )
            {
                children[ i ].Measure( new Size( double.PositiveInfinity, availableSize.Height ) );
                _naturalWidths[ i ] = children[ i ].DesiredSize.Width;
                totalWidth         += _naturalWidths[ i ];
                if( children[ i ].DesiredSize.Height > maxHeight )
                    maxHeight = children[ i ].DesiredSize.Height;
            }

            return new Size(
                double.IsInfinity( availableSize.Width )
                    ? totalWidth
                    : Math.Min( totalWidth, availableSize.Width ),
                maxHeight );
        }

        protected override Size ArrangeOverride( Size finalSize )
        {
            var children = Children;
            int count    = children.Count;
            if( count == 0 ) return finalSize;

            // Use the widths cached during MeasureOverride.  If the cache is stale
            // (shouldn't happen in a normal layout cycle) fall back to DesiredSize.
            double[] natural = _naturalWidths.Length == count
                ? _naturalWidths
                : BuildFallbackWidths( children );

            double maxH = 0;
            foreach( var child in children )
                if( child.DesiredSize.Height > maxH )
                    maxH = child.DesiredSize.Height;

            // Determine the first index that should be visible 
            double total = 0;
            foreach( double w in natural ) total += w;

            int firstVisible;
            if( total <= finalSize.Width )
            {
                firstVisible = 0; // Fast path - everything fits naturally.
            }
            else
            {
                // Always keep at least the last 2 (or all if count < 2).
                int mustShow = Math.Min( 2, count );
                firstVisible = count - mustShow;

                double accumulated = 0;
                for( int i = count - 1; i >= count - mustShow; i-- )
                    accumulated += natural[ i ];

                // Try to extend the window leftward.
                for( int i = firstVisible - 1; i >= 0; i-- )
                {
                    if( accumulated + natural[ i ] <= finalSize.Width )
                    {
                        accumulated  += natural[ i ];
                        firstVisible  = i;
                    }
                    else break;
                }
            }

            // Arrange hidden children just off the left edge 
            // We do NOT set IsVisible because doing so triggers a re-measure in
            // which invisible children return DesiredSize = 0, changing the layout
            // decision on the next pass and creating an oscillation loop.
            // ClipToBounds = true (set in the constructor) clips these off-screen
            // items so they are never rendered.
            for( int i = 0; i < firstVisible; i++ )
                children[ i ].Arrange( new Rect( -natural[ i ], 0, natural[ i ], maxH ) );

            // Arrange visible children 
            double visibleTotal = 0;
            for( int i = firstVisible; i < count; i++ ) visibleTotal += natural[ i ];

            double x = 0;
            for( int i = firstVisible; i < count; i++ )
            {
                double w;
                if( visibleTotal <= finalSize.Width )
                {
                    w = natural[ i ]; // Fits - use natural width, no truncation.
                }
                else
                {
                    // Last ≤2 visible crumbs still overflow. Share the space
                    // proportionally so the inner TextBlock's TextTrimming kicks in.
                    double share = finalSize.Width * ( natural[ i ] / visibleTotal );
                    w = Math.Max( share, MinCrumbWidth );
                }

                children[ i ].Arrange( new Rect( x, 0, w, maxH ) );
                x += w;
            }

            return finalSize;
        }

        private static double[] BuildFallbackWidths( Controls children )
        {
            double[] widths = new double[ children.Count ];
            for( int i = 0; i < children.Count; i++ )
                widths[ i ] = children[ i ].DesiredSize.Width;
            return widths;
        }
    }
}
