using RageWebUI.Core.Protocol;

namespace RageWebUI.Core
{
    /// <summary>
    /// Receives trusted browser messages at the renderer boundary. The normal
    /// in-process host writes directly to <see cref="BridgeBroker"/> while the
    /// native-bootstrap host forwards the same messages over its bounded IPC
    /// channel to the managed gameplay provider.
    /// </summary>
    public interface IBridgeMessageSink
    {
        bool TryEnqueue(string json, out BridgeError? error);
    }
}
