namespace RageWebUI.Core.Protocol
{
    /// <summary>
    /// A protocol-v2 request to cancel a queued request. The id is the id of
    /// the original request, which keeps cancellation idempotent and avoids a
    /// second correlation-id namespace.
    /// </summary>
    public sealed class BridgeCancel
    {
        internal BridgeCancel(
            string id,
            int protocolVersion,
            int requestedProtocolVersion,
            int minimumProtocolVersion,
            string? reason)
        {
            Id = id;
            ProtocolVersion = protocolVersion;
            RequestedProtocolVersion = requestedProtocolVersion;
            MinimumProtocolVersion = minimumProtocolVersion;
            Reason = reason;
        }

        public string Id { get; }

        public int ProtocolVersion { get; }

        public int RequestedProtocolVersion { get; }

        public int MinimumProtocolVersion { get; }

        public string? Reason { get; }
    }
}
