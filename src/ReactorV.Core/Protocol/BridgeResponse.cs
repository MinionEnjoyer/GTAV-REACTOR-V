using Newtonsoft.Json.Linq;

namespace RageWebUI.Core.Protocol
{
    public sealed class BridgeResponse
    {
        private BridgeResponse(
            string id,
            JToken? result,
            BridgeError? error,
            int protocolVersion)
        {
            Id = id;
            Result = result;
            Error = error;
            ProtocolVersion = protocolVersion;
        }

        public string Id { get; }

        public JToken? Result { get; }

        public BridgeError? Error { get; }

        public int ProtocolVersion { get; }

        public static BridgeResponse Success(
            string id,
            JToken? result = null,
            int protocolVersion = BridgeProtocol.MinimumSupportedProtocolVersion) =>
            new BridgeResponse(
                id,
                result ?? JValue.CreateNull(),
                null,
                protocolVersion);

        public static BridgeResponse Failure(string id, string code, string message) =>
            new BridgeResponse(
                id,
                null,
                new BridgeError(code, message),
                BridgeProtocol.MinimumSupportedProtocolVersion);

        public static BridgeResponse Failure(
            string id,
            BridgeError error,
            int protocolVersion = BridgeProtocol.MinimumSupportedProtocolVersion) =>
            new BridgeResponse(id, null, error, protocolVersion);
    }
}
