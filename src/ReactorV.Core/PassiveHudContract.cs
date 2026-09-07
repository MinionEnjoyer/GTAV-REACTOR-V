using System;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core
{
    /// <summary>Versioned passive readout protocol. No action, pointer or menu authority.</summary>
    public static class PassiveHudContract
    {
        public const int Version = 1;
        public const string Capability = "presentation.passive-hud.v1";
        public const string EventId = "hud.frame";
        public const int LeaseMilliseconds = 1000;

        internal static bool Valid(JObject frame)
        {
            if (frame["schema"]?.Type != JTokenType.Integer || frame.Value<int?>("schema") != 1 || frame["visible"]?.Type != JTokenType.Boolean) return false;
            if (!frame.Value<bool>("visible")) return true;
            if (frame.Value<string>("kind") != "speedometer") return false;
            if (frame["speed"]?.Type != JTokenType.Float && frame["speed"]?.Type != JTokenType.Integer) return false;
            double speed = frame.Value<double>("speed");
            if (frame["units"]?.Type != JTokenType.String || frame["gear"]?.Type != JTokenType.String) return false;
            string? units = frame.Value<string>("units"), gear = frame.Value<string>("gear");
            return !double.IsNaN(speed) && !double.IsInfinity(speed) && speed >= 0 && speed <= 9999
                && (units == "KMH" || units == "MPH") && gear != null && gear.Length <= 3
                && frame["manual"]?.Type == JTokenType.Boolean
                && frame["notice"]?.Type == JTokenType.String && frame.Value<string>("notice")!.Length <= 180;
        }
    }

    // A single speedometer slot. The first live publisher owns its short lease;
    // another extension cannot clear or replace it until expiry/unregistration.
    internal sealed class PassiveHudLease
    {
        internal string? Owner { get; private set; }
        internal JObject? Frame { get; private set; }
        private long _expires;
        internal bool Active(long now) => Frame != null && now < _expires;
        internal bool Accept(string owner, JObject frame, long now)
        {
            try { if (!PassiveHudContract.Valid(frame)) return false; }
            catch { return false; }
            if (Active(now) && Owner != owner) return false;
            Owner = owner;
            Frame = frame.Value<bool>("visible") ? (JObject)frame.DeepClone() : null;
            _expires = now + PassiveHudContract.LeaseMilliseconds;
            return true;
        }
        internal void Clear() { Owner = null; Frame = null; _expires = 0; }
    }
}
