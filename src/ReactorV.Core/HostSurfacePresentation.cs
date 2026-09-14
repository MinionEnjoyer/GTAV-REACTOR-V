using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core
{
    // Native visibility still permits closing an unverified window. This separate,
    // immutable receipt permits a HUD consumer to claim an exact displayed frame.
    public interface IHostSurfacePresentationRuntime
    {
        bool IsHostSurfacePresented(string mode, int generation);
    }

    public sealed class HostSurfacePresentation
    {
        public string Mode { get; }
        public int Generation { get; }
        public HostSurfacePresentation(string mode, int generation)
        {
            Mode = mode;
            Generation = generation;
        }

        public bool Matches(string mode, int generation) =>
            generation > 0 && HostSurfaceMode.RequiresPaintProof(mode) &&
            Generation == generation && Mode == mode;

        public JObject ToJson() => new JObject { ["mode"] = Mode, ["generation"] = Generation };

        public static HostSurfacePresentation? ReadState(JObject state)
        {
            if (state["visible"]?.Type != JTokenType.Boolean || !state.Value<bool>("visible") ||
                state["ready"]?.Type != JTokenType.Boolean || !state.Value<bool>("ready") ||
                !(state["verifiedHostSurface"] is JObject receipt) ||
                receipt["mode"]?.Type != JTokenType.String ||
                receipt["generation"]?.Type != JTokenType.Integer)
                return null;
            var mode = receipt.Value<string>("mode");
            if (!long.TryParse(receipt["generation"]!.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var generation)) return null;
            return HostSurfaceMode.RequiresPaintProof(mode) && generation > 0 && generation <= int.MaxValue
                ? new HostSurfacePresentation(mode!, (int)generation) : null;
        }
    }
}
