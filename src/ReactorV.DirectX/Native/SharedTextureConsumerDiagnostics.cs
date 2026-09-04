using System.Runtime.InteropServices;

namespace RageWebUI.DirectX.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SharedTextureConsumerDiagnostics
    {
        internal const uint ExpectedByteSize = 128;

        public uint ByteSize;
        public ushort Major;
        public ushort Minor;
        public uint Stage;
        public uint LastReceiveError;
        public uint LastImportError;
        public uint LastImportHresult;
        public ulong DiscoveryMisses;
        public ulong ProducerImageRejects;
        public ulong ConnectFailures;
        public ulong ReceivedFrames;
        public ulong ReceiveFailures;
        public ulong ImportedResources;
        public ulong PublishedFrames;
        public ulong CopyFailures;
        public ulong AcknowledgementsAccepted;
        public ulong AcknowledgementsRejected;
        public ulong AcknowledgementFailures;
        public ulong LastReceivedGeneration;
        public ulong LastPublishedGeneration;

        public static SharedTextureConsumerDiagnostics CreateRequest() =>
            new SharedTextureConsumerDiagnostics
            {
                ByteSize = checked((uint)Marshal.SizeOf<SharedTextureConsumerDiagnostics>())
            };

        public string ToTraceDetail() =>
            $"stage={Stage} receive_error={LastReceiveError} import_error={LastImportError} " +
            $"import_hresult=0x{LastImportHresult:X8} " +
            $"discovery_misses={DiscoveryMisses} producer_rejects={ProducerImageRejects} " +
            $"connect_failures={ConnectFailures} received={ReceivedFrames} " +
            $"receive_failures={ReceiveFailures} imported={ImportedResources} " +
            $"published={PublishedFrames} copy_failures={CopyFailures} " +
            $"ack_accepted={AcknowledgementsAccepted} ack_rejected={AcknowledgementsRejected} " +
            $"ack_failures={AcknowledgementFailures} " +
            $"last_received_generation={LastReceivedGeneration} " +
            $"last_published_generation={LastPublishedGeneration}";
    }
}
