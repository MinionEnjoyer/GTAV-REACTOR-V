using System;

namespace RageWebUI.Runtime
{
    /// <summary>Read-only, non-atomic Win32 context for the two scoped windows.
    /// No window titles, unrelated process names, input, DWM waits or GDI reads.
    /// A z-order/visibility snapshot is diagnostic context, never pixel proof.</summary>
    internal static class DesktopWindowState
    {
        internal static string Capture(IntPtr overlay, IntPtr game)
        {
            var overlayValid = overlay != IntPtr.Zero && NativeMethods.IsWindow(overlay);
            var gameValid = game != IntPtr.Zero && NativeMethods.IsWindow(game);
            var foreground = NativeMethods.GetForegroundWindow();
            var owner = overlayValid ? NativeMethods.GetWindowLongPtr(overlay, NativeMethods.GwlHwndParent) : IntPtr.Zero;
            var zOrder = "unknown";
            if (overlayValid && gameValid && NativeMethods.TryIsWindowAbove(overlay, game, out var above))
                zOrder = above ? "overlay-above-game" : "game-above-overlay";
            return $"overlay_valid={overlayValid} game_valid={gameValid} " +
                $"foreground={Relation(foreground, overlay, game)} owner={Relation(owner, overlay, game)} " +
                $"z_order={zOrder} overlay={Describe(overlay, overlayValid)} game={Describe(game, gameValid)} " +
                "sampled_nonatomically=True evidence_scope=native-window-state-not-pixels";
        }

        private static string Relation(IntPtr value, IntPtr overlay, IntPtr game) =>
            value == IntPtr.Zero ? "none" : value == overlay ? "overlay" : value == game ? "game" : "other";

        private static string Describe(IntPtr window, bool valid)
        {
            if (!valid) return "invalid";
            var bounds = NativeMethods.TryGetClientBounds(window, out var rect)
                ? $"{rect.Left},{rect.Top},{rect.Width},{rect.Height}" : "unknown";
            return $"[visible:{NativeMethods.IsWindowVisible(window)},enabled:{NativeMethods.IsWindowEnabled(window)}," +
                $"minimized:{NativeMethods.IsIconic(window)}," +
                $"style:0x{NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle).ToInt64():X}," +
                $"exstyle:0x{NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64():X},client:{bounds}]";
        }
    }
}
