using System;

namespace RageWebUI.Core
{
    /// <summary>A publisher's window-lifetime high watermark. Hiding, losing
    /// paint, and reconnecting do not permit an older surface to return.</summary>
    public sealed class HostSurfaceSequence
    {
        public int Generation { get; private set; }
        private string? _mode;

        public bool TryAccept(string mode, int generation)
        {
            if (generation <= 0 || generation < Generation ||
                (mode != HostSurfaceMode.None && !HostSurfaceMode.RequiresPaintProof(mode)))
                return false;
            if (generation == Generation)
                return string.Equals(mode, _mode, StringComparison.Ordinal);
            Generation = generation;
            _mode = mode;
            return true;
        }
    }
}
