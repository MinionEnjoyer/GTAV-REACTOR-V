using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core
{
    /// <summary>
    /// Host-STA watchdog, independent of GTA ticks and of browser pixel expiry.
    /// A stale generation cannot renew or resurrect a retired native HUD window.
    /// </summary>
    public sealed class PassiveHudHostLease
    {
        private int _generation;
        private long _expires;
        private bool _armed;
        private DateTime _lastPublishedUtc;

        public void BeginSurface(string mode, int generation, long now)
        {
            if (mode == HostSurfaceMode.PassiveHud && generation > 0 && generation == _generation)
                return; // Replaying a surface must not renew an expired lease.
            Clear();
            if (mode != HostSurfaceMode.PassiveHud || generation <= 0) return;
            _generation = generation;
            _armed = true;
            _expires = now + PassiveHudContract.LeaseMilliseconds;
        }

        public static JObject CreateFrame(JObject frame, int generation, DateTime utcNow)
        {
            var payload = (JObject)frame.DeepClone();
            payload["hostSurfaceGeneration"] = generation;
            payload["hostPublishedUtc"] = utcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            return payload;
        }

        public bool Accept(JObject? frame, DateTime utcNow, long now)
        {
            if (!_armed || now >= _expires || frame == null) return false;
            try
            {
                if (frame["hostSurfaceGeneration"]?.Type != JTokenType.Integer ||
                    frame.Value<long>("hostSurfaceGeneration") != _generation ||
                    !PassiveHudContract.Valid(frame))
                    return false;
                // Json.NET parses ISO timestamps into Date tokens on the wire.
                // Converting those back through Value<string> loses UTC Kind.
                DateTime published;
                var stamp = frame["hostPublishedUtc"];
                if (stamp?.Type == JTokenType.Date) published = stamp.Value<DateTime>();
                else if (stamp?.Type != JTokenType.String ||
                    !DateTime.TryParse(stamp.Value<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out published)) return false;
                if (published.Kind != DateTimeKind.Utc) return false;
                var age = (utcNow.ToUniversalTime() - published).TotalMilliseconds;
                if (age < 0 || age >= PassiveHudContract.LeaseMilliseconds || published <= _lastPublishedUtc)
                    return false;
                _lastPublishedUtc = published;
                _expires = frame.Value<bool>("visible")
                    ? now + PassiveHudContract.LeaseMilliseconds - (long)Math.Ceiling(age) : now;
                return true;
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException ||
                error is InvalidCastException || error is OverflowException)
            {
                return false;
            }
        }

        public bool ShouldHide(long now)
        {
            if (!_armed || now < _expires) return false;
            _armed = false;
            return true;
        }

        public bool AllowsPresentation(long now) => _armed && now < _expires;

        public void Clear()
        {
            _armed = false;
            _generation = 0;
            _expires = 0;
            _lastPublishedUtc = DateTime.MinValue;
        }
    }
}
