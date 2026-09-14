using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core
{
    /// <summary>STA-owned correlation between the script request and the
    /// preloader's authoritative host.surface generation (which includes startup).</summary>
    public sealed class PassiveHudGenerationMap
    {
        private int _providerGeneration;
        private int _hostGeneration;
        public bool BeginRequest(JObject? payload)
        {
            Clear();
            if (payload?.Value<string>("mode") != HostSurfaceMode.PassiveHud ||
                !PositiveGeneration(payload["generation"], out _providerGeneration)) return false;
            return true;
        }
        public void BindSurface(string mode, int generation)
        {
            if (mode != HostSurfaceMode.PassiveHud || generation <= 0) { Clear(); return; }
            _hostGeneration = generation;
        }
        public JObject? ToHostFrame(JObject? payload)
        {
            if (_hostGeneration <= 0 || _providerGeneration <= 0 || payload == null ||
                !PositiveGeneration(payload["hostSurfaceGeneration"], out var generation) ||
                generation != _providerGeneration) return null;
            var frame = (JObject)payload.DeepClone();
            frame["hostSurfaceGeneration"] = _hostGeneration;
            return frame;
        }
        public HostSurfacePresentation? ToProviderReceipt(HostSurfacePresentation? receipt) =>
            _providerGeneration > 0 && receipt?.Matches(HostSurfaceMode.PassiveHud, _hostGeneration) == true
                ? new HostSurfacePresentation(HostSurfaceMode.PassiveHud, _providerGeneration) : null;
        public void Clear() { _providerGeneration = _hostGeneration = 0; }
        private static bool PositiveGeneration(JToken? token, out int value)
        {
            value = 0;
            return token?.Type == JTokenType.Integer && int.TryParse(token.ToString(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
        }
    }
}
