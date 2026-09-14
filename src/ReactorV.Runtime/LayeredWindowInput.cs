using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RageWebUI.Runtime
{
    internal static class LayeredWindowInput
    {
        internal static void Initialize(IntPtr window, Action<string, string?> trace)
        {
            // HTTRANSPARENT only searches windows belonging to the same thread.
            // WS_EX_TRANSPARENT needs WS_EX_LAYERED for cross-process mouse
            // passthrough. Keep DirectComposition's alpha, with no color key,
            // bitmap copy or global opacity reduction.
            const long required = NativeMethods.WsExLayered |
                NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate;
            if (window == IntPtr.Zero || !NativeMethods.IsWindow(window) ||
                (NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64() & required) != required)
                throw new InvalidOperationException("Composition host lacks the required layered input styles.");
            if (!NativeMethods.SetLayeredWindowAttributes(window, 0, 255, NativeMethods.LwaAlpha))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot initialize composition host input passthrough.");
            if (!NativeMethods.GetLayeredWindowAttributes(window, out _, out var alpha, out var flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify composition host layer attributes.");
            if (alpha != 255 || flags != NativeMethods.LwaAlpha)
                throw new InvalidOperationException("Composition host layer attributes did not match the request.");
            trace("webview_layered_input_initialized",
                $"hwnd=0x{window.ToInt64():X} alpha={alpha} flags={flags} " +
                "evidence=native-configuration-not-input-or-pixels");
        }
    }
}
