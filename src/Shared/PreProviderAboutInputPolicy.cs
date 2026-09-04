using System;

namespace ReactorV.BootstrapInput
{
    internal readonly struct PreProviderButtonDecision
    {
        internal PreProviderButtonDecision(
            bool pressed,
            bool released,
            bool nextDown)
        {
            Pressed = pressed;
            Released = released;
            NextDown = nextDown;
        }

        internal bool Pressed { get; }
        internal bool Released { get; }
        internal bool NextDown { get; }
    }

    /// <summary>
    /// Pure fail-closed policy for the external preloader's About-only pointer
    /// sampler. It does not hook, capture, activate, or suppress game input.
    /// </summary>
    internal static class PreProviderAboutInputPolicy
    {
        internal const int PollIntervalMilliseconds = 20;
        internal const int IdlePollIntervalMilliseconds = 250;

        internal static bool ShouldSample(
            bool contentReady,
            bool visible,
            string? surface,
            bool providerConnected,
            bool gameForeground) =>
            contentReady &&
            visible &&
            !providerConnected &&
            gameForeground &&
            string.Equals(surface, "about", StringComparison.Ordinal);

        internal static bool ShouldCaptureWindowHitTests(
            bool contentReady,
            bool visible,
            string? surface,
            bool providerConnected)
        {
            _ = contentReady;
            _ = visible;
            _ = surface;
            _ = providerConnected;
            return false;
        }

        internal static bool TryNormalize(
            int left,
            int top,
            int width,
            int height,
            int screenX,
            int screenY,
            out float normalizedX,
            out float normalizedY)
        {
            normalizedX = 0f;
            normalizedY = 0f;
            if (width <= 1 || height <= 1 ||
                screenX < left || screenY < top ||
                screenX >= left + width || screenY >= top + height)
                return false;

            normalizedX = Math.Max(
                0f,
                Math.Min(1f, (screenX - left) / (float)(width - 1)));
            normalizedY = Math.Max(
                0f,
                Math.Min(1f, (screenY - top) / (float)(height - 1)));
            return true;
        }

        internal static PreProviderButtonDecision EvaluateLeftButton(
            bool eligible,
            bool down,
            bool pressedSinceLastPoll,
            bool previousDown)
        {
            if (!eligible)
                return new PreProviderButtonDecision(false, false, false);

            var pressed = pressedSinceLastPoll || (down && !previousDown);
            // The low transition bit can preserve a complete short tap between
            // polls. Emit both edges in that case so the bounded DOM adapter
            // observes one click instead of dropping it.
            var released = (!down && previousDown) ||
                (pressedSinceLastPoll && !down);
            return new PreProviderButtonDecision(pressed, released, down);
        }
    }
}
