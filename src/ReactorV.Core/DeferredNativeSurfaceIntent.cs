using System;

namespace RageWebUI.Core
{
    public enum NativeSurfaceRequestAction
    {
        AwaitPaint = 0,
        DeferUntilNativeReady = 1,
        StopUnavailable = 2,
    }

    /// <summary>
    /// Retains one exact native-bootstrap request while the native producer is
    /// starting. The caller starts its normal paint budget only after a
    /// matching ready transition.
    /// </summary>
    public sealed class DeferredNativeSurfaceIntent
    {
        private string? _mode;
        private int _generation;
        private int _providerSessionGeneration;

        public bool IsPending => _generation > 0;

        public static NativeSurfaceRequestAction EvaluateRequest(
            bool nativeSurface,
            bool externalSessionExists,
            bool externalSessionActive,
            bool externalPresentationReady)
        {
            if (!nativeSurface) return NativeSurfaceRequestAction.AwaitPaint;
            if (!externalSessionExists || !externalSessionActive)
                return NativeSurfaceRequestAction.StopUnavailable;
            return externalPresentationReady
                ? NativeSurfaceRequestAction.AwaitPaint
                : NativeSurfaceRequestAction.DeferUntilNativeReady;
        }

        public void Defer(string mode, int generation, int providerSessionGeneration)
        {
            if (string.IsNullOrWhiteSpace(mode)) throw new ArgumentException("A mode is required.", nameof(mode));
            if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
            _mode = mode;
            _generation = generation;
            _providerSessionGeneration = providerSessionGeneration;
        }

        public bool TryConsumeReady(string? mode, int generation, int providerSessionGeneration, bool nativePresenterReady)
        {
            if (!nativePresenterReady || !Matches(mode, generation, providerSessionGeneration)) return false;
            Clear();
            return true;
        }

        public bool Matches(string? mode, int generation, int providerSessionGeneration) =>
            _generation == generation && generation > 0 &&
            _providerSessionGeneration == providerSessionGeneration &&
            string.Equals(_mode, mode, StringComparison.Ordinal);

        public void RebindPreProviderSession(int providerSessionGeneration)
        {
            if (IsPending && _providerSessionGeneration == 0 && providerSessionGeneration > 0)
                _providerSessionGeneration = providerSessionGeneration;
        }

        public void Clear()
        {
            _mode = null;
            _generation = 0;
            _providerSessionGeneration = 0;
        }
    }
}
