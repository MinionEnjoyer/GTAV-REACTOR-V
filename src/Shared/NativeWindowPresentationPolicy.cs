namespace ReactorV.Windowing
{
    /// <summary>
    /// Distinguishes a top-level window that is merely WS_VISIBLE from one
    /// whose bounds can actually be presented on the user's virtual desktop.
    /// WebView2 requires a visible parent during controller creation, so
    /// Reactor briefly leases an offscreen WS_VISIBLE HWND without presenting
    /// overlay pixels to the player.
    /// </summary>
    internal static class NativeWindowPresentationPolicy
    {
        internal static bool IsPresentedToDesktop(
            bool nativeVisible,
            int windowLeft,
            int windowTop,
            int windowRight,
            int windowBottom,
            int desktopLeft,
            int desktopTop,
            int desktopRight,
            int desktopBottom)
        {
            if (!nativeVisible ||
                windowRight <= windowLeft ||
                windowBottom <= windowTop ||
                desktopRight <= desktopLeft ||
                desktopBottom <= desktopTop)
            {
                return false;
            }

            return windowLeft < desktopRight &&
                windowRight > desktopLeft &&
                windowTop < desktopBottom &&
                windowBottom > desktopTop;
        }
    }
}
