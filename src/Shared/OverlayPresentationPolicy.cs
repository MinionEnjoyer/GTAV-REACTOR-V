namespace ReactorV.WebView2Host
{
    internal static class OverlayPresentationPolicy
    {
        // Windowed WebView2 cannot provide reliable per-pixel alpha through a
        // WinForms transparency-key surface. Give Chromium and the host the
        // same opaque key instead of allowing transparent pixels to be
        // composed as black by the DWM/WebView2 fallback path.
        internal const int ChromaKeyArgb = unchecked((int)0xFFFF00FF);

        internal static bool ShouldPresent(
            bool requestedVisible,
            bool browserReady,
            bool gameMinimized,
            bool gameForeground,
            bool hasClientBounds)
        {
            return requestedVisible &&
                browserReady &&
                !gameMinimized &&
                gameForeground &&
                hasClientBounds;
        }
    }
}
