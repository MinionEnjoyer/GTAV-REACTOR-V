using System;
using System.Drawing;
using System.Runtime.InteropServices;
using ReactorV.Windowing;

namespace RageWebUI.Runtime
{
    internal static class VerifiedWindowPromotion
    {
        internal static bool IsTopmost(IntPtr window) => window != IntPtr.Zero &&
            (NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64() & 0x8) != 0;

        internal static bool Apply(IntPtr window, Rectangle bounds, bool move,
            Action<string, string?> trace)
        {
            var error = 0;
            var flags = NativeMethods.SwpNoActivate |
                (move ? 0 : NativeMethods.SwpNoMove | NativeMethods.SwpNoSize);
            var outcome = VerifiedWindowPromotionPolicy.Apply(
                () => {
                    var ok = NativeMethods.SetWindowPos(window, NativeMethods.HwndTopMost,
                        bounds.X, bounds.Y, bounds.Width, bounds.Height, flags);
                    if (!ok) error = Marshal.GetLastWin32Error();
                    return ok;
                },
                () => IsTopmost(window),
                () => {
                    // Preserve HWND, bounds, root, visibility and input state.
                    // Do not activate or insert a hide/show between paint proof
                    // and the independent desktop witness.
                    var ok = NativeMethods.SetWindowPos(window, NativeMethods.HwndNoTopMost,
                        0, 0, 0, 0, NativeMethods.SwpNoActivate |
                        NativeMethods.SwpNoMove | NativeMethods.SwpNoSize);
                    if (!ok) error = Marshal.GetLastWin32Error();
                    return ok;
                });
            var applied = VerifiedWindowPromotionPolicy.Succeeded(outcome);
            trace("webview_native_promotion_verified",
                $"hwnd=0x{window.ToInt64():X} outcome={outcome} " +
                $"native_topmost={applied} move={move} error={error}");
            return applied;
        }
    }
}
