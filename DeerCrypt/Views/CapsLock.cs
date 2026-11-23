using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Runtime.InteropServices;

namespace DeerCrypt.Views
{
    /// <summary>
    /// Tracks Caps Lock for password boxes: <c>CapsLock.IsOn</c> is true on a focused password
    /// TextBox while Caps Lock is on, so a dialog can bind a warning to it.
    ///
    /// The state comes from the OS where it can be asked (Windows; X11, which Avalonia uses on
    /// Linux, including under XWayland). Elsewhere it's inferred from what's typed: a capital
    /// letter without Shift (or a small one with Shift) means Caps Lock is on.
    /// </summary>
    public static class CapsLock
    {
        public static readonly AttachedProperty<bool> IsOnProperty =
            AvaloniaProperty.RegisterAttached<TextBox, bool>( "IsOn", typeof( CapsLock ) );

        public static bool GetIsOn( TextBox box ) => box.GetValue( IsOnProperty );

        private static bool?        _lastKnown;
        private static KeyModifiers _modifiers;
        private static bool         _registered;

        /// <summary>Hooks every TextBox in the app once (called at startup).</summary>
        public static void Register( )
        {
            if( _registered ) return;
            _registered = true;

            InputElement.GotFocusEvent.AddClassHandler<TextBox>( ( box, _ ) => Update( box, null ) );
            InputElement.LostFocusEvent.AddClassHandler<TextBox>( ( box, _ ) => box.SetValue( IsOnProperty, false ) );
            InputElement.KeyDownEvent.AddClassHandler<TextBox>( ( box, e ) =>
            {
                _modifiers = e.KeyModifiers;
                if( e.Key == Key.CapsLock && _lastKnown is { } known ) _lastKnown = !known; // fallback guess; the OS answer wins on KeyUp
            }, RoutingStrategies.Tunnel, handledEventsToo: true );
            InputElement.KeyUpEvent.AddClassHandler<TextBox>( ( box, _ ) => Update( box, null ), RoutingStrategies.Bubble, handledEventsToo: true );
            InputElement.TextInputEvent.AddClassHandler<TextBox>( ( box, e ) => Update( box, InferFromText( e.Text ) ),
                RoutingStrategies.Tunnel, handledEventsToo: true );
        }

        private static bool? InferFromText( string? text )
        {
            if( string.IsNullOrEmpty( text ) || text.Length != 1 || !char.IsLetter( text [ 0 ] ) ) return null;
            char c = text [ 0 ];
            if( char.ToUpperInvariant( c ) == char.ToLowerInvariant( c ) ) return null; // no case (e.g. CJK)
            bool shift = _modifiers.HasFlag( KeyModifiers.Shift );
            return char.IsUpper( c ) != shift;
        }

        private static void Update( TextBox box, bool? hint )
        {
            if( box.PasswordChar == '\0' ) return;
            bool? on = QueryOs( ) ?? hint ?? _lastKnown;
            if( on is not { } value ) return;
            _lastKnown = value;
            box.SetValue( IsOnProperty, value && box.IsFocused );
        }

        // OS queries

        private static IntPtr _x11Display;
        private static bool   _x11Failed;

        private static bool? QueryOs( )
        {
            try
            {
                if( OperatingSystem.IsWindows( ) )
                    return ( GetKeyState( 0x14 /* VK_CAPITAL */ ) & 1 ) != 0;

                if( OperatingSystem.IsLinux( ) && !_x11Failed )
                {
                    if( _x11Display == IntPtr.Zero ) _x11Display = XOpenDisplay( IntPtr.Zero );
                    if( _x11Display == IntPtr.Zero ) { _x11Failed = true; return null; }
                    if( XkbGetIndicatorState( _x11Display, XkbUseCoreKbd, out uint state ) != 0 ) return null;
                    return ( state & 1 ) != 0; // indicator 0 is Caps Lock
                }
            }
            catch( Exception ) when( OperatingSystem.IsLinux( ) )
            {
                _x11Failed = true; // no libX11 (e.g. a pure Wayland setup): infer from typing instead
            }
            catch { }
            return null;
        }

        private const uint XkbUseCoreKbd = 0x0100;

        [DllImport( "user32.dll" )]
        private static extern short GetKeyState( int key );

        [DllImport( "libX11.so.6" )]
        private static extern IntPtr XOpenDisplay( IntPtr name );

        [DllImport( "libX11.so.6" )]
        private static extern int XkbGetIndicatorState( IntPtr display, uint deviceSpec, out uint state );
    }
}
