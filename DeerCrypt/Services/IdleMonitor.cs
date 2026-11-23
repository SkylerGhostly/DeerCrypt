using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;

namespace DeerCrypt.Services
{
    /// <summary>
    /// Remembers when the user last touched any DeerCrypt window (keyboard, click, pointer move,
    /// wheel), for auto-lock. Hooks every Window once at startup, so pop-out viewers and dialogs count.
    /// </summary>
    public static class IdleMonitor
    {
        private static bool _registered;

        public static DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

        public static TimeSpan IdleFor => DateTime.UtcNow - LastActivityUtc;

        public static void Touch( ) => LastActivityUtc = DateTime.UtcNow;

        public static void Register( )
        {
            if( _registered ) return;
            _registered = true;

            const RoutingStrategies tunnel = RoutingStrategies.Tunnel;
            InputElement.KeyDownEvent.AddClassHandler<Window>( ( _, _ ) => Touch( ), tunnel, handledEventsToo: true );
            InputElement.PointerPressedEvent.AddClassHandler<Window>( ( _, _ ) => Touch( ), tunnel, handledEventsToo: true );
            InputElement.PointerMovedEvent.AddClassHandler<Window>( ( _, _ ) => Touch( ), tunnel, handledEventsToo: true );
            InputElement.PointerWheelChangedEvent.AddClassHandler<Window>( ( _, _ ) => Touch( ), tunnel, handledEventsToo: true );
        }
    }
}
