using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace RageWebUI.Runtime
{
    internal static class NativeMethods
    {
        internal const int GwlHwndParent = -8;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpShowWindow = 0x0040;
        private const uint GaRootOwner = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        internal static bool TryGetClientBounds(IntPtr window, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (window == IntPtr.Zero || !GetClientRect(window, out var rect)) return false;
            var origin = new NativePoint();
            if (!ClientToScreen(window, ref origin)) return false;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;
            bounds = new Rectangle(origin.X, origin.Y, width, height);
            return true;
        }

        internal static bool IsForegroundOrOwnedBy(IntPtr window)
        {
            var foreground = GetForegroundWindow();
            return foreground == window ||
                (foreground != IntPtr.Zero && GetAncestor(foreground, GaRootOwner) == window);
        }
    }
}
