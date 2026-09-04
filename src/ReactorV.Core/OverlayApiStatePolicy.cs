using System;

namespace RageWebUI.Core
{
    /// <summary>
    /// Fail-closed policy shared by the production and harness API routers.
    /// A visible browser surface must own an input mode in the same game-thread
    /// transaction; otherwise a previously hidden surface can become visible
    /// while GTA still owns the click that opened it.
    /// </summary>
    internal static class OverlayApiStatePolicy
    {
        internal const string GameInputMode = "game";

        internal static bool IsSupportedInputMode(string? mode) =>
            string.Equals(mode, GameInputMode, StringComparison.Ordinal) ||
            string.Equals(mode, "menu", StringComparison.Ordinal) ||
            string.Equals(mode, "interactive-menu", StringComparison.Ordinal) ||
            string.Equals(mode, "pointer", StringComparison.Ordinal) ||
            string.Equals(mode, "exclusive", StringComparison.Ordinal);

        internal static bool CanExposeVisibleSurface(bool visible, string? mode) =>
            !visible ||
            (IsSupportedInputMode(mode) &&
             !string.Equals(mode, GameInputMode, StringComparison.Ordinal));
    }
}
