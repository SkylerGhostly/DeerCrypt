using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Labs.Gif;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeerCrypt.ViewModels.Dialogs;
using SkiaSharp;
using System;
using System.IO;

namespace DeerCrypt.Views.Dialogs
{
    public partial class ImageViewerWindow : Window
    {
        // Minimal 1×1 transparent GIF used as a placeholder when AnimatedStream is null.
        // Prevents GifImage's internal timer from ticking against a disposed stream.
        private static readonly byte[] s_emptyGif = Convert.FromBase64String(
            "R0lGODlhAQABAIAAAAAAAP///yH5BAAAAAAALAAAAAABAAEAAAICRAEAOw==" );

        private ImageViewerViewModel? _vm;
        private ScrollViewer?         _scroll;
        private Grid?                 _container;   // sized to max(imgSize, viewport)
        private Image?                _img;         // static images
        private GifImage?             _animImg;     // animated GIFs

        // Panning 
        private bool   _isPanning;
        private Point  _panStart;
        private Vector _panOffset;

        // Fit-on-first-viewport flag 
        // Set true when a new bitmap is ready; cleared after FitToWindow succeeds.
        private bool _pendingFit;

        // Pending zoom-offset correction 
        // Set by ZoomAtPoint; applied in OnScrollLayoutUpdated (fires after
        // layout but before the compositor renders - no wrong-offset frame).
        private bool  _pendingZoomCorrection;
        private double _zoomFx, _zoomFy;
        private Point  _zoomMouseInScroll;

        public ImageViewerWindow( )
        {
            InitializeComponent( );
        }

        // Lifecycle 

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );
            _vm = DataContext as ImageViewerViewModel;
        }

        protected override async void OnOpened( EventArgs e )
        {
            base.OnOpened( e );

            _scroll    = this.FindControl<ScrollViewer>( "Scroll" );
            _container = this.FindControl<Grid>( "Container" );
            _img       = this.FindControl<Image>( "Img" );
            _animImg   = this.FindControl<GifImage>( "AnimImg" );

            if( _vm == null ) return;

            _vm.CloseRequested  += OnVmCloseRequested;
            _vm.PropertyChanged += OnVmPropertyChanged;

            if( _scroll != null )
            {
                // Register at Window level (Tunnel) so the event is consumed before it
                // reaches the ScrollViewer - ScrollViewer's class handler fires even when
                // e.Handled is true (it doesn't use handledEventsToo=false), so registering
                // on _scroll itself isn't enough to prevent it from also scrolling.
                this.AddHandler(
                    PointerWheelChangedEvent,
                    OnWheelZoom,
                    RoutingStrategies.Tunnel );

                // When the viewport resizes (window resize), keep layout correct.
                // If a fit is pending (e.g. Viewport was 0 on first try), do it now.
                _scroll.SizeChanged += OnScrollSizeChanged;

                // Fires after every layout pass, before the compositor renders.
                // Used by ZoomAtPoint to apply the offset correction atomically.
                _scroll.LayoutUpdated += OnScrollLayoutUpdated;

                // Use Tunnel so we intercept PointerPressed before child controls
                // (e.g. GifImage) can handle/consume it, which would prevent panning.
                _container!.AddHandler(
                    PointerPressedEvent,
                    OnPointerPressed,
                    RoutingStrategies.Tunnel );
                _container.PointerMoved       += OnPointerMoved;
                _container.PointerReleased    += OnPointerReleased;
                _container.PointerCaptureLost += ( _, _ ) => EndPan( );
            }

            // Toolbar buttons (not data-bound because they call code-behind helpers)
            var fitBtn     = this.FindControl<Button>( "FitBtn" );
            var zoomInBtn  = this.FindControl<Button>( "ZoomInBtn" );
            var zoomOutBtn = this.FindControl<Button>( "ZoomOutBtn" );

            fitBtn?.Click     += ( _, _ ) => FitToWindow( );
            zoomInBtn?.Click  += ( _, _ ) => ZoomFromCenter( _vm.Zoom * 1.25 );
            zoomOutBtn?.Click += ( _, _ ) => ZoomFromCenter( _vm.Zoom / 1.25 );

            // Intercept arrow keys in the TUNNEL phase so the ScrollViewer never
            // sees them for scrolling. Delete is safe either way but consistent.
            this.AddHandler(
                KeyDownEvent,
                OnTunnelKeyDown,
                RoutingStrategies.Tunnel );

            await _vm.LoadCurrentAsync( );
        }

        private void OnVmCloseRequested( ) => Close( );

        protected override void OnClosed( EventArgs e )
        {
            if( _vm != null )
            {
                _vm.CloseRequested  -= OnVmCloseRequested;
                _vm.PropertyChanged -= OnVmPropertyChanged;
            }
            _vm?.Dispose( );   // frees the large bitmap / animated stream
            base.OnClosed( e );

            // Purge the CPU-side font strike cache immediately so the next render
            // re-rasterises glyphs fresh.  Then at Render priority (just before the
            // next frame), invalidate every visual in the main window's tree - not just
            // the root - so each composition layer is marked dirty and Skia re-draws
            // all glyphs in colour rather than reusing any stale monochrome entries.
            SKGraphics.PurgeFontCache( );
            Dispatcher.UIThread.Post( ( ) =>
            {
                if( Application.Current?.ApplicationLifetime
                        is IClassicDesktopStyleApplicationLifetime dt
                    && dt.MainWindow is { } mainWindow )
                {
                    InvalidateVisualTree( mainWindow );
                }
            }, DispatcherPriority.Render );
        }

        private static void InvalidateVisualTree( Visual root )
        {
            root.InvalidateVisual( );
            foreach( var child in root.GetVisualChildren( ) )
                InvalidateVisualTree( child );
        }

        // Keyboard (tunnel - fires before ScrollViewer) 

        private void OnTunnelKeyDown( object? sender, KeyEventArgs e )
        {
            if( _vm == null ) return;

            switch( e.Key )
            {
                case Key.Left:
                    if( _vm.PreviousCommand.CanExecute( null ) )
                    {
                        _vm.PreviousCommand.Execute( null );
                        e.Handled = true;
                    }
                    break;

                case Key.Right:
                    if( _vm.NextCommand.CanExecute( null ) )
                    {
                        _vm.NextCommand.Execute( null );
                        e.Handled = true;
                    }
                    break;

                case Key.Delete:
                    if( _vm.DeleteCommand.CanExecute( null ) )
                    {
                        _vm.DeleteCommand.Execute( null );
                        e.Handled = true;
                    }
                    break;
            }
        }

        // ViewModel → View 

        private void OnVmPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs e )
        {
            switch( e.PropertyName )
            {
                case nameof( ImageViewerViewModel.Bitmap ):
                    // Static image ready - schedule fit.
                    if( _vm?.NaturalPixelSize.Width > 0 )
                    {
                        _pendingFit = true;
                        Dispatcher.UIThread.Post( FitToWindow, DispatcherPriority.Background );
                    }
                    break;

                case nameof( ImageViewerViewModel.AnimatedStream ):
                    // Drive GifImage.Source from code-behind - the property throws
                    // ArgumentException when set to null via a XAML binding.
                    _animImg?.Source = _vm?.AnimatedStream is { } liveStream
                            ? liveStream
                            : ( object ) new MemoryStream( s_emptyGif );
                    // Animated image ready - schedule fit.
                    if( _vm?.NaturalPixelSize.Width > 0 )
                    {
                        _pendingFit = true;
                        Dispatcher.UIThread.Post( FitToWindow, DispatcherPriority.Background );
                    }
                    break;

                case nameof( ImageViewerViewModel.Zoom ):
                    UpdateContainerSize( );
                    break;
            }
        }

        private void OnScrollSizeChanged( object? sender, SizeChangedEventArgs e )
        {
            if( _pendingFit )
                FitToWindow( );  // retries until Viewport > 0
            else
                UpdateContainerSize( );
        }

        // Layout helpers 

        /// <summary>
        /// Sets the active image control's Width/Height to PixelSize × Zoom (Stretch="Fill"
        /// scales to these).  Sets Container.Width/Height to max(scaled, viewport) so the
        /// image is centred when smaller than the visible area.
        /// </summary>
        private void UpdateContainerSize( )
        {
            if( _container == null || _scroll == null || _vm == null ) return;
            var ps = _vm.NaturalPixelSize;
            if( ps.Width <= 0 || ps.Height <= 0 ) return;

            double imgW = ps.Width  * _vm.Zoom;
            double imgH = ps.Height * _vm.Zoom;

            if( _img     != null ) { _img.Width     = imgW; _img.Height     = imgH; }
            if( _animImg != null ) { _animImg.Width  = imgW; _animImg.Height = imgH; }

            var vp = _scroll.Viewport;
            _container.Width  = Math.Max( imgW, vp.Width  > 0 ? vp.Width  : imgW );
            _container.Height = Math.Max( imgH, vp.Height > 0 ? vp.Height : imgH );
        }

        /// <summary>
        /// Scales zoom so the whole image is visible inside the current viewport,
        /// then resets scroll to (0,0).
        /// </summary>
        private void FitToWindow( )
        {
            if( _vm == null || _scroll == null ) return;
            var ps = _vm.NaturalPixelSize;
            if( ps.Width <= 0 || ps.Height <= 0 ) return;

            var vp = _scroll.Viewport;
            if( vp.Width <= 0 || vp.Height <= 0 ) return;   // not laid out yet - retry via SizeChanged

            _pendingFit = false;

            double z = Math.Min( vp.Width / ps.Width, vp.Height / ps.Height );
            _vm.Zoom  = Math.Clamp( z, 0.05, 8.0 );

            Dispatcher.UIThread.Post(
                ( ) => { _scroll?.Offset = Vector.Zero; },
                DispatcherPriority.Render );
        }

        private void ZoomFromCenter( double newZoom )
        {
            if( _scroll == null ) return;
            var vp = _scroll.Viewport;
            ZoomAtPoint( Math.Clamp( newZoom, 0.05, 8.0 ),
                         new Point( vp.Width / 2, vp.Height / 2 ) );
        }

        // Zoom centred on a viewport point 

        /// <summary>
        /// Changes zoom while keeping the image point currently under
        /// <paramref name="mouseInScroll"/> (viewport-relative) stationary.
        /// The offset correction is deferred to <see cref="OnScrollLayoutUpdated"/>,
        /// which fires after the layout pass but before the compositor renders,
        /// so no intermediate wrong-offset frame is ever drawn.
        /// </summary>
        private void ZoomAtPoint( double newZoom, Point mouseInScroll )
        {
            if( _vm == null || _scroll == null ) return;
            var ps = _vm.NaturalPixelSize;
            if( ps.Width <= 0 ) return;

            double imgW = ps.Width  * _vm.Zoom;
            double imgH = ps.Height * _vm.Zoom;

            var    vp         = _scroll.Viewport;
            double containerW = Math.Max( imgW, vp.Width );
            double containerH = Math.Max( imgH, vp.Height );

            // Position of the image's top-left corner within the container.
            double imgLeft = ( containerW - imgW ) / 2.0;
            double imgTop  = ( containerH - imgH ) / 2.0;

            // Absolute position of the mouse in container space
            double absX = _scroll.Offset.X + mouseInScroll.X;
            double absY = _scroll.Offset.Y + mouseInScroll.Y;

            // Fractional position within the image (may be outside [0,1] if on margin)
            _zoomFx           = imgW > 0 ? ( absX - imgLeft ) / imgW : 0.5;
            _zoomFy           = imgH > 0 ? ( absY - imgTop  ) / imgH : 0.5;
            _zoomMouseInScroll = mouseInScroll;

            // Arm the correction - OnScrollLayoutUpdated will apply it after layout.
            _pendingZoomCorrection = true;

            _vm.Zoom = Math.Clamp( newZoom, 0.05, 8.0 );  // → UpdateContainerSize() → layout pass
        }

        /// <summary>
        /// Applies the zoom-offset correction after the layout pass triggered by
        /// a <see cref="ZoomAtPoint"/> call.  Fires before the compositor renders,
        /// so the scroll position is already correct on the first drawn frame.
        /// </summary>
        private void OnScrollLayoutUpdated( object? sender, EventArgs e )
        {
            if( !_pendingZoomCorrection || _scroll == null || _vm == null ) return;
            _pendingZoomCorrection = false;

            var    ps2        = _vm.NaturalPixelSize;
            double newImgW    = ps2.Width  * _vm.Zoom;
            double newImgH    = ps2.Height * _vm.Zoom;
            var    newVp      = _scroll.Viewport;
            double newContW   = Math.Max( newImgW, newVp.Width );
            double newContH   = Math.Max( newImgH, newVp.Height );
            double newImgLeft = ( newContW - newImgW ) / 2.0;
            double newImgTop  = ( newContH - newImgH ) / 2.0;

            _scroll.Offset = new Vector(
                newImgLeft + _zoomFx * newImgW - _zoomMouseInScroll.X,
                newImgTop  + _zoomFy * newImgH - _zoomMouseInScroll.Y );
        }

        // Wheel 

        private void OnWheelZoom( object? sender, PointerWheelEventArgs e )
        {
            if( _vm == null || _vm.NaturalPixelSize.Width <= 0 || e.Delta.Y == 0 ) return;

            double factor = e.Delta.Y > 0 ? 1.12 : 1.0 / 1.12;
            ZoomAtPoint(
                Math.Clamp( _vm.Zoom * factor, 0.05, 8.0 ),
                e.GetPosition( _scroll ) );
            e.Handled = true;  // block ScrollViewer from also scrolling vertically
        }

        // Panning 

        private void OnPointerPressed( object? sender, PointerPressedEventArgs e )
        {
            if( !e.GetCurrentPoint( null ).Properties.IsLeftButtonPressed ) return;

            _isPanning  = true;
            _panStart   = e.GetPosition( _scroll );
            _panOffset  = _scroll!.Offset;
            _container!.Cursor = new Cursor( StandardCursorType.SizeAll );
            e.Pointer.Capture( _container );
            e.Handled = true;
        }

        private void OnPointerMoved( object? sender, PointerEventArgs e )
        {
            if( !_isPanning || _scroll == null ) return;
            var delta = e.GetPosition( _scroll ) - _panStart;
            _scroll.Offset = new Vector(
                _panOffset.X - delta.X,
                _panOffset.Y - delta.Y );
            e.Handled = true;
        }

        private void OnPointerReleased( object? sender, PointerReleasedEventArgs e )
        {
            EndPan( );
        }

        private void EndPan( )
        {
            if( !_isPanning ) return;
            _isPanning = false;
            _container?.Cursor = new Cursor( StandardCursorType.Hand );
        }
    }
}
