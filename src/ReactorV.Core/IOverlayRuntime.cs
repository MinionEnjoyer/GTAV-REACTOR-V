using System;
using Newtonsoft.Json.Linq;
using RageWebUI.Core.Protocol;

namespace RageWebUI.Core
{
    /// <summary>
    /// Stable contract between the small SHVDN bootstrap and the renderer
    /// implementation stored outside the scripts directory.
    /// </summary>
    public interface IOverlayRuntime : IDisposable
    {
        bool IsVisible { get; }

        string RendererName { get; }

        bool Start();

        void SetVisible(bool visible);

        void PumpInput();

        void UpdateCursor(float normalizedX, float normalizedY, bool pressed, bool released, int wheelDelta);

        void PostResponse(BridgeResponse response);

        void PostEvent(string eventName, JToken? payload);
    }
}
