using System;
using System.IO;
using Newtonsoft.Json;

namespace RageWebUI.Script.Configuration
{
    internal sealed class OverlayConfiguration
    {
        public string ToggleKey { get; set; } = "F9";

        public bool StartVisible { get; set; }

        public bool ShowFirstRunSplash { get; set; }

        public bool EnableDevTools { get; set; }

        public string Renderer { get; set; } = "auto";

        public int DirectXFrameRate { get; set; } = 30;

        public int TelemetryIntervalMilliseconds { get; set; } = 250;

        public static OverlayConfiguration Load(string assemblyDirectory)
        {
            var path = Path.Combine(assemblyDirectory, "ReactorV.json");
            if (!File.Exists(path))
            {
                // Accept the pre-branding filename so loose/manual installs
                // continue to start while users migrate their scripts folder.
                path = Path.Combine(assemblyDirectory, "RageWebUI.json");
            }
            if (!File.Exists(path))
            {
                return new OverlayConfiguration();
            }

            try
            {
                var loaded = JsonConvert.DeserializeObject<OverlayConfiguration>(File.ReadAllText(path));
                if (loaded == null)
                {
                    return new OverlayConfiguration();
                }

                loaded.TelemetryIntervalMilliseconds = Math.Max(50, Math.Min(1000, loaded.TelemetryIntervalMilliseconds));
                loaded.DirectXFrameRate = Math.Max(15, Math.Min(60, loaded.DirectXFrameRate));
                loaded.Renderer = (loaded.Renderer ?? "auto").Trim().ToLowerInvariant();
                if (loaded.Renderer != "auto" && loaded.Renderer != "directx" && loaded.Renderer != "windowed")
                {
                    loaded.Renderer = "auto";
                }
                return loaded;
            }
            catch
            {
                return new OverlayConfiguration();
            }
        }
    }
}
