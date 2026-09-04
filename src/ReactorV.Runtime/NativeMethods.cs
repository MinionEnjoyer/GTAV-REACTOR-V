using System;
using System.Drawing;
using System.Runtime.InteropServices;
using RageWebUI.Windowing;

namespace RageWebUI.Runtime
{
    internal static class NativeMethods
    {
        internal static readonly IntPtr HwndTopMost = new IntPtr(-1);
        internal static readonly IntPtr HwndNoTopMost = new IntPtr(-2);
        internal const int GwlHwndParent = -8;
        internal const int GwlStyle = -16;
        internal const int GwlExStyle = -20;
        internal const int WsVisible = 0x10000000;
        internal const int WsExTransparent = 0x00000020;
        internal const int WsExToolWindow = 0x00000080;
        internal const int WsExNoActivate = 0x08000000;
        internal const int WsExNoRedirectionBitmap = 0x00200000;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoZOrder = 0x0004;
        internal const uint SwpFrameChanged = 0x0020;
        internal const uint SwpShowWindow = 0x0040;
        private const uint GwHwndNext = 2;
        private const int MaximumZOrderWalk = 4096;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
        private static extern void SetLastErrorNative(uint errorCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetTopWindow(IntPtr parent);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessage(
            IntPtr window,
            int message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr deviceContext, int x, int y);

        internal static bool TryReadDesktopPixel(int x, int y, out Color color)
        {
            color = Color.Empty;
            var device = GetDC(IntPtr.Zero);
            if (device == IntPtr.Zero)
                return false;
            try
            {
                var value = GetPixel(device, x, y);
                if (value == 0xFFFFFFFF)
                    return false;
                color = Color.FromArgb(
                    (int)(value & 0xFF),
                    (int)((value >> 8) & 0xFF),
                    (int)((value >> 16) & 0xFF));
                return true;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, device);
            }
        }

        internal static uint GetForegroundProcessId()
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return 0;
            return GetWindowThreadProcessId(foreground, out var processId) == 0
                ? 0
                : processId;
        }

        /// <summary>
        /// Compares two top-level windows without changing either one. The
        /// walk is bounded so a corrupt or changing desktop list cannot stall
        /// Reactor's UI thread. A false return means the relationship was not
        /// proven and callers must not mutate z-order on that evidence.
        /// </summary>
        internal static bool TryIsWindowAbove(
            IntPtr candidate,
            IntPtr reference,
            out bool candidateAbove)
        {
            candidateAbove = false;
            if (candidate == IntPtr.Zero || reference == IntPtr.Zero ||
                candidate == reference)
            {
                return false;
            }

            var current = GetTopWindow(IntPtr.Zero);
            for (var index = 0;
                 current != IntPtr.Zero && index < MaximumZOrderWalk;
                 index++)
            {
                if (current == candidate)
                {
                    candidateAbove = true;
                    return true;
                }
                if (current == reference)
                {
                    candidateAbove = false;
                    return true;
                }
                current = GetWindow(current, GwHwndNext);
            }

            return false;
        }

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
            => Win32GameWindowLocator.IsForegroundOrOwnedBy(window);

        internal static bool IsForegroundOrOwnedBy(
            IntPtr window,
            IntPtr interactionWindow,
            uint interactionProcessId,
            bool interactionCaptureActive) =>
            Win32GameWindowLocator.IsForegroundOrOwnedBy(
                window,
                interactionWindow,
                interactionProcessId,
                interactionCaptureActive);

        internal static IntPtr ResolveGameWindow(
            uint processId,
            IntPtr preferred,
            IntPtr excluded,
            out string detail) =>
            Win32GameWindowLocator.Resolve(processId, preferred, excluded, out detail);

        internal static string DescribeForegroundRelationship(IntPtr window) =>
            Win32GameWindowLocator.DescribeForegroundRelationship(window);

        internal static string DescribeForegroundRelationship(
            IntPtr window,
            IntPtr interactionWindow,
            uint interactionProcessId,
            bool interactionCaptureActive) =>
            Win32GameWindowLocator.DescribeForegroundRelationship(
                window,
                interactionWindow,
                interactionProcessId,
                interactionCaptureActive);

        internal static bool HasSubstantialClientBounds(IntPtr window) =>
            TryGetClientBounds(window, out var bounds) &&
            bounds.Width >= GameWindowSelectionPolicy.MinimumClientWidth &&
            bounds.Height >= GameWindowSelectionPolicy.MinimumClientHeight;

        internal static bool TrySetWindowOwner(
            IntPtr window,
            IntPtr owner,
            out IntPtr previousOwner,
            out IntPtr observedOwner,
            out int error)
        {
            previousOwner = IntPtr.Zero;
            observedOwner = IntPtr.Zero;
            error = 0;
            if (window == IntPtr.Zero)
            {
                error = 1400; // ERROR_INVALID_WINDOW_HANDLE
                return false;
            }

            // SetWindowLongPtr returns the previous value, so zero is both a
            // valid success result and the documented failure sentinel. Clear
            // last-error first and verify the resulting owner with an
            // independent readback before allowing the caller to cache it.
            SetLastErrorNative(0);
            previousOwner = SetWindowLongPtr(window, GwlHwndParent, owner);
            var setError = Marshal.GetLastWin32Error();
            var setSucceeded = previousOwner != IntPtr.Zero || setError == 0;

            SetLastErrorNative(0);
            observedOwner = GetWindowLongPtr(window, GwlHwndParent);
            var readError = Marshal.GetLastWin32Error();
            var readSucceeded = observedOwner != IntPtr.Zero || readError == 0;
            if (setSucceeded && readSucceeded && observedOwner == owner)
            {
                return true;
            }

            error = setError != 0
                ? setError
                : readError != 0
                    ? readError
                    : 13; // ERROR_INVALID_DATA: readback did not match.
            return false;
        }
    }
}
