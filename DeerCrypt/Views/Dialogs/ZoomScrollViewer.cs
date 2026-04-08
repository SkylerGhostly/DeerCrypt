using Avalonia.Controls;
using Avalonia.Input;
using System;

namespace DeerCrypt.Views.Dialogs
{
    /// <summary>
    /// A <see cref="ScrollViewer"/> that does not respond to pointer wheel events.
    ///
    /// <para>
    /// Avalonia's <see cref="ScrollViewer"/> scrolls on every <see cref="PointerWheelChangedEvent"/>
    /// via a class handler registered with <c>handledEventsToo: true</c>, which means it fires
    /// even when the event is already marked <see cref="Avalonia.Interactivity.RoutedEventArgs.Handled"/>.
    /// A Window-level tunnel handler that sets <c>e.Handled = true</c> is therefore not sufficient
    /// to prevent the scroll - both the zoom (from the tunnel handler) and the scroll (from the
    /// class handler) run, in different layout passes, producing a one-frame jitter where the image
    /// appears to scroll up before snapping back to the zoom-corrected position.
    /// </para>
    ///
    /// <para>
    /// Overriding <see cref="OnPointerWheelChanged"/> and omitting the <c>base</c> call suppresses
    /// the scroll logic entirely.  Wheel-based zooming is handled exclusively by the parent
    /// <see cref="ImageViewerWindow"/>'s tunnel handler, which also manages the scroll offset.
    /// Scrollbar interaction (thumb drag, track click) is unaffected.
    /// </para>
    /// </summary>
    internal sealed class ZoomScrollViewer : ScrollViewer
    {
        // Tell Avalonia's styling system to resolve ControlTheme using ScrollViewer's
        // type key, not ZoomScrollViewer's.  Without this, Avalonia 11's exact-match
        // ControlTheme lookup finds no theme for the subclass and applies no template -
        // leaving Viewport at (0,0), breaking fit/layout/pan/zoom-reset entirely.
        protected override Type StyleKeyOverride => typeof( ScrollViewer );

        protected override void OnPointerWheelChanged( PointerWheelEventArgs e )
        {
            // Intentionally suppress the built-in scroll-on-wheel behaviour.
            // The ImageViewerWindow tunnel handler owns wheel input for zoom + offset.
        }
    }
}
