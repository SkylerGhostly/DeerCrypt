using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Labs.Gif;
using Avalonia.Media;
using Avalonia.Threading;
using DeerCrypt.ViewModels.Dialogs;
using DeerCrypt.ViewModels.Viewers;
using DeerCrypt.Views.Dialogs;
using System;
using System.IO;

namespace DeerCrypt.Views.Viewers
{
    /// <summary>
    /// Zoom, pan, rotate, and keyboard handling for <see cref="ImageDocumentViewModel"/>.
    /// Zooming keeps the image point under the cursor still: the scroll correction is applied in
    /// LayoutUpdated (after layout, before render) so no wrong-offset frame is ever drawn.
    /// </summary>
    public partial class ImageViewerView : UserControl
    {
        // 1×1 transparent GIF: GifImage throws on a null Source and must not tick on a disposed stream
        private static readonly byte[] s_emptyGif = Convert.FromBase64String(
            "R0lGODlhAQABAIAAAAAAAP///yH5BAAAAAAALAAAAAABAAEAAAICRAEAOw==" );

        private ImageDocumentViewModel? _vm;
        private ZoomScrollViewer?       _scroll;
        private Grid?                   _container;
        private Image?                  _img;
        private GifImage?               _animImg;
        private LayoutTransformControl? _rotator;

        private bool   _isPanning;
        private Point  _panStart;
        private Vector _panOffset;
        private bool   _pendingFit;

        // True from a fit until the user zooms or pans. While set, any viewport size change
        // (the tab finishing layout, the window resizing) re-fits and re-centres. Without it, an
        // image that decodes before layout settles stays centred on a stale viewport.
        private bool   _fitMode;
        private bool   _pendingZoomCorrection;
        private double _zoomFx, _zoomFy;
        private Point  _zoomMouseInScroll;

        public ImageViewerView( )
        {
            InitializeComponent( );

            _scroll    = this.FindControl<ZoomScrollViewer>( "Scroll" );
            _container = this.FindControl<Grid>( "Container" );
            _img       = this.FindControl<Image>( "Img" );
            _animImg   = this.FindControl<GifImage>( "AnimImg" );
            _rotator   = this.FindControl<LayoutTransformControl>( "Rotator" );

            // Tunnel at the view so wheel and arrow keys are ours before the ScrollViewer sees them
            AddHandler( PointerWheelChangedEvent, OnWheelZoom, RoutingStrategies.Tunnel );
            AddHandler( KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel );

            _scroll!.SizeChanged   += ( _, _ ) => { if( _pendingFit || _fitMode ) FitToView( ); else UpdateContainerSize( ); };
            _scroll.LayoutUpdated  += OnScrollLayoutUpdated;

            _container!.AddHandler( PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel );
            _container.PointerMoved       += OnPointerMoved;
            _container.PointerReleased    += ( _, _ ) => EndPan( );
            _container.PointerCaptureLost += ( _, _ ) => EndPan( );
            _container.DoubleTapped       += ( _, _ ) => ToggleFitActual( );

            this.FindControl<Button>( "FitBtn" )!.Click     += ( _, _ ) => FitToView( );
            this.FindControl<Button>( "ActualBtn" )!.Click  += ( _, _ ) => ZoomFromCenter( 1.0 );
            this.FindControl<Button>( "ZoomInBtn" )!.Click  += ( _, _ ) => ZoomFromCenter( ( _vm?.Zoom ?? 1 ) * 1.25 );
            this.FindControl<Button>( "ZoomOutBtn" )!.Click += ( _, _ ) => ZoomFromCenter( ( _vm?.Zoom ?? 1 ) / 1.25 );

            // Clicking anywhere in the viewer gives it keyboard focus
            AddHandler( PointerPressedEvent, ( _, _ ) => Focus( ), RoutingStrategies.Tunnel, handledEventsToo: true );
        }

        // The view is rebuilt when a tab pops out or docks, so (re)attach to the document here
        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );
            if( _vm != null ) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as ImageDocumentViewModel;
            if( _vm == null ) return;

            _vm.PropertyChanged += OnVmPropertyChanged;
            SyncAnimatedSource( );
            ApplyRotation( );
            if( _vm.HasContent ) _pendingFit = true;
        }

        protected override void OnAttachedToVisualTree( VisualTreeAttachmentEventArgs e )
        {
            base.OnAttachedToVisualTree( e );
            Dispatcher.UIThread.Post( ( ) => { Focus( ); if( _pendingFit ) FitToView( ); }, DispatcherPriority.Loaded );
        }

        protected override void OnDetachedFromVisualTree( VisualTreeAttachmentEventArgs e )
        {
            base.OnDetachedFromVisualTree( e );
            // Stop the GIF from ticking against a stream the document may release
            _animImg?.Source = new MemoryStream( s_emptyGif );
        }

        private void OnVmPropertyChanged( object? sender, System.ComponentModel.PropertyChangedEventArgs e )
        {
            switch( e.PropertyName )
            {
                case nameof( ImageDocumentViewModel.Bitmap ):
                    if( _vm?.NaturalPixelSize.Width > 0 ) ScheduleFit( );
                    break;
                case nameof( ImageDocumentViewModel.AnimatedStream ):
                    SyncAnimatedSource( );
                    if( _vm?.NaturalPixelSize.Width > 0 ) ScheduleFit( );
                    break;
                case nameof( ImageDocumentViewModel.Zoom ):
                    UpdateContainerSize( );
                    break;
                case nameof( ImageDocumentViewModel.Rotation ):
                    ApplyRotation( );
                    ScheduleFit( );
                    break;
            }
        }

        private void SyncAnimatedSource( ) =>
            _animImg?.Source = _vm?.AnimatedStream is { } live ? live : (object)new MemoryStream( s_emptyGif );

        private void ApplyRotation( ) =>
            _rotator?.LayoutTransform = _vm is { Rotation: not 0 } ? new RotateTransform( _vm.Rotation ) : null;

        private void ScheduleFit( )
        {
            _pendingFit = true;
            Dispatcher.UIThread.Post( FitToView, DispatcherPriority.Background );
        }

        // Keyboard

        private void OnTunnelKeyDown( object? sender, KeyEventArgs e )
        {
            if( _vm == null || e.KeyModifiers.HasFlag( KeyModifiers.Control ) ) return;

            switch( e.Key )
            {
                case Key.Left:     Run( _vm.PreviousCommand ); break;
                case Key.Right:    Run( _vm.NextCommand ); break;
                case Key.Home:     Run( _vm.FirstCommand ); break;
                case Key.End:      Run( _vm.LastCommand ); break;
                case Key.R when e.KeyModifiers.HasFlag( KeyModifiers.Shift ): Run( _vm.RotateLeftCommand ); break;
                case Key.R:        Run( _vm.RotateRightCommand ); break;
                case Key.S:        Run( _vm.ToggleSlideshowCommand ); break;
                case Key.F:        _vm.RequestFullscreen( ); break;
                case Key.OemPlus or Key.Add:       ZoomFromCenter( _vm.Zoom * 1.25 ); break;
                case Key.OemMinus or Key.Subtract: ZoomFromCenter( _vm.Zoom / 1.25 ); break;
                case Key.D0 or Key.NumPad0:        FitToView( ); break;
                case Key.D1 or Key.NumPad1:        ZoomFromCenter( 1.0 ); break;
                case Key.Delete:   OnDeleteClicked( null, new RoutedEventArgs( ) ); break;
                default: return;
            }
            e.Handled = true;

            static void Run( System.Windows.Input.ICommand c ) { if( c.CanExecute( null ) ) c.Execute( null ); }
        }

        // Toolbar actions that need the view

        private async void OnDeleteClicked( object? sender, RoutedEventArgs e )
        {
            if( _vm?.CurrentItem is not { } item || TopLevel.GetTopLevel( this ) is not Window owner ) return;

            var confirm = new ConfirmDialogViewModel
            {
                Title       = "Delete",
                Message     = $"Delete \"{item.DisplayName}\"?",
                Detail      = "The image will be permanently deleted from the vault. This can't be undone.",
                ConfirmText = "Delete",
                CancelText  = "Cancel",
                Tone        = DialogTone.Danger
            };

            if( await new ConfirmDialog { DataContext = confirm }.ShowDialog<bool?>( owner ) != true ) return;

            string? error = await _vm.DeleteCurrentAsync( );
            if( error != null ) _vm.ErrorText = $"Couldn't delete: {error}";
        }

        private void OnFullscreenClicked( object? sender, RoutedEventArgs e ) => _vm?.RequestFullscreen( );

        // Layout

        /// <summary>Size of the (rotated) image on screen at the current zoom.</summary>
        private Size DisplaySize( double zoom )
        {
            var ps = _vm!.NaturalPixelSize;
            double w = ps.Width * zoom, h = ps.Height * zoom;
            return _vm.IsSideways ? new Size( h, w ) : new Size( w, h );
        }

        private void UpdateContainerSize( )
        {
            if( _container == null || _scroll == null || _vm == null ) return;
            var ps = _vm.NaturalPixelSize;
            if( ps.Width <= 0 || ps.Height <= 0 ) return;

            double w = ps.Width * _vm.Zoom, h = ps.Height * _vm.Zoom;
            if( _img     != null ) { _img.Width     = w; _img.Height     = h; }
            if( _animImg != null ) { _animImg.Width = w; _animImg.Height = h; }

            // Exactly the image's size: when smaller than the viewport, the scroll presenter centres
            // it at arrange time, so centring never depends on a viewport measured earlier.
            var shown = DisplaySize( _vm.Zoom );
            _container.Width  = shown.Width;
            _container.Height = shown.Height;
        }

        private void FitToView( )
        {
            if( _vm == null || _scroll == null ) return;
            var ps = _vm.NaturalPixelSize;
            if( ps.Width <= 0 || ps.Height <= 0 ) return;
            var vp = _scroll.Viewport;
            if( vp.Width <= 0 || vp.Height <= 0 ) return; // not laid out yet; SizeChanged retries
            _pendingFit = false;
            _fitMode    = true;

            var oneToOne = DisplaySize( 1.0 );
            double z = Math.Min( vp.Width / oneToOne.Width, vp.Height / oneToOne.Height );
            // Centre: the same post-layout offset correction zooming uses (image midpoint at viewport midpoint)
            _zoomFx = _zoomFy = 0.5;
            _zoomMouseInScroll     = new Point( vp.Width / 2, vp.Height / 2 );
            _pendingZoomCorrection = true;
            _vm.Zoom = Math.Clamp( z, ImageDocumentViewModel.ZoomMin, ImageDocumentViewModel.ZoomMax );
            UpdateContainerSize( );
            _scroll.InvalidateArrange( );
            DebugLayout( "fit" );
        }

        /// <summary>Debug builds only: prints the viewer's geometry to the terminal (for layout bug reports).</summary>
        [System.Diagnostics.Conditional( "DEBUG" )]
        private void DebugLayout( string reason )
        {
            Dispatcher.UIThread.Post( ( ) =>
            {
                if( _scroll == null || _img == null || _vm == null ) return;
                var c = _img.TranslatePoint( new Point( _img.Bounds.Width / 2, _img.Bounds.Height / 2 ), _scroll );
                Console.WriteLine( $"[image-viewer] {reason}: file={_vm.Title} natural={_vm.NaturalPixelSize.Width}x{_vm.NaturalPixelSize.Height} " +
                                   $"zoom={_vm.Zoom:F3} viewport={_scroll.Viewport.Width:F0}x{_scroll.Viewport.Height:F0} " +
                                   $"extent={_scroll.Extent.Width:F0}x{_scroll.Extent.Height:F0} offset={_scroll.Offset.X:F0},{_scroll.Offset.Y:F0} " +
                                   $"image={_img.Bounds.Width:F0}x{_img.Bounds.Height:F0} imageCentre={c?.X:F0},{c?.Y:F0} " +
                                   $"visible={IsEffectivelyVisible} scaling={TopLevel.GetTopLevel( this )?.RenderScaling}" );
            }, DispatcherPriority.Background );
        }

        private void ToggleFitActual( )
        {
            if( _vm == null || _scroll == null ) return;
            var vp = _scroll.Viewport;
            var oneToOne = DisplaySize( 1.0 );
            double fit = Math.Min( vp.Width / oneToOne.Width, vp.Height / oneToOne.Height );
            if( Math.Abs( _vm.Zoom - fit ) < 0.01 ) ZoomFromCenter( 1.0 ); else FitToView( );
        }

        private void ZoomFromCenter( double newZoom )
        {
            if( _scroll == null ) return;
            var vp = _scroll.Viewport;
            ZoomAtPoint( newZoom, new Point( vp.Width / 2, vp.Height / 2 ) );
        }

        private void ZoomAtPoint( double newZoom, Point mouseInScroll )
        {
            _fitMode = false;
            if( _vm == null || _scroll == null || _vm.NaturalPixelSize.Width <= 0 ) return;

            var shown = DisplaySize( _vm.Zoom );
            var vp    = _scroll.Viewport;
            double containerW = Math.Max( shown.Width, vp.Width );
            double containerH = Math.Max( shown.Height, vp.Height );
            double imgLeft = ( containerW - shown.Width ) / 2.0;
            double imgTop  = ( containerH - shown.Height ) / 2.0;

            double absX = _scroll.Offset.X + mouseInScroll.X;
            double absY = _scroll.Offset.Y + mouseInScroll.Y;
            _zoomFx = shown.Width  > 0 ? ( absX - imgLeft ) / shown.Width  : 0.5;
            _zoomFy = shown.Height > 0 ? ( absY - imgTop  ) / shown.Height : 0.5;
            _zoomMouseInScroll     = mouseInScroll;
            _pendingZoomCorrection = true;

            _vm.Zoom = Math.Clamp( newZoom, ImageDocumentViewModel.ZoomMin, ImageDocumentViewModel.ZoomMax );
        }

        private void OnScrollLayoutUpdated( object? sender, EventArgs e )
        {
            if( !_pendingZoomCorrection || _scroll == null || _vm == null ) return;
            _pendingZoomCorrection = false;

            var shown = DisplaySize( _vm.Zoom );
            var vp    = _scroll.Viewport;
            double contW = Math.Max( shown.Width, vp.Width );
            double contH = Math.Max( shown.Height, vp.Height );
            double left  = ( contW - shown.Width ) / 2.0;
            double top   = ( contH - shown.Height ) / 2.0;

            _scroll.Offset = new Vector(
                left + _zoomFx * shown.Width  - _zoomMouseInScroll.X,
                top  + _zoomFy * shown.Height - _zoomMouseInScroll.Y );
        }

        private void OnWheelZoom( object? sender, PointerWheelEventArgs e )
        {
            if( _vm == null || _vm.NaturalPixelSize.Width <= 0 || e.Delta.Y == 0 || _scroll == null ) return;
            if( !_scroll.IsPointerOver ) return; // let the toolbar's slider etc. handle their own wheel
            double factor = e.Delta.Y > 0 ? 1.12 : 1.0 / 1.12;
            ZoomAtPoint( _vm.Zoom * factor, e.GetPosition( _scroll ) );
            e.Handled = true;
        }

        // Panning

        private void OnPointerPressed( object? sender, PointerPressedEventArgs e )
        {
            _fitMode = false;
            if( !e.GetCurrentPoint( null ).Properties.IsLeftButtonPressed || e.ClickCount > 1 ) return;
            _isPanning = true;
            _panStart  = e.GetPosition( _scroll );
            _panOffset = _scroll!.Offset;
            _container!.Cursor = new Cursor( StandardCursorType.SizeAll );
            e.Pointer.Capture( _container );
        }

        private void OnPointerMoved( object? sender, PointerEventArgs e )
        {
            if( !_isPanning || _scroll == null ) return;
            var delta = e.GetPosition( _scroll ) - _panStart;
            _scroll.Offset = new Vector( _panOffset.X - delta.X, _panOffset.Y - delta.Y );
            e.Handled = true;
        }

        private void EndPan( )
        {
            if( !_isPanning ) return;
            _isPanning = false;
            _container?.Cursor = new Cursor( StandardCursorType.Hand );
        }
    }
}
