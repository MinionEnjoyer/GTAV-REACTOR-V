using System.Runtime.InteropServices;

namespace RageWebUI.DirectX.Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SharedTextureProducerDiagnostics
    {
        internal const uint ExpectedByteSize = 416;

        public uint ByteSize;
        public ushort Major;
        public ushort Minor;
        public SharedTextureSubmitStatus LastStatus;
        public uint Flags;
        public ulong ProbeAttempts;
        public ulong SubmitAttempts;
        public ulong Submitted;
        public ulong Backpressure;
        public ulong SessionInvalid;
        public ulong AdapterOrResourceInvalid;
        public ulong DeviceOrCopyFailure;
        public ulong ProducerStopped;
        public ulong InvalidFrame;
        public ulong UnknownFailure;
        public ulong AcknowledgementsAccepted;
        public ulong AcknowledgementsRejected;
        public ulong AcknowledgementFailures;
        public ulong LastAttemptedGeneration;
        public ulong LastSubmittedGeneration;
        public ulong LastAcknowledgedGeneration;
        public int AdapterLuidHigh;
        public uint AdapterLuidLow;
        public uint AdapterVendorId;
        public uint AdapterDeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string AdapterDescription;

        public static SharedTextureProducerDiagnostics CreateRequest() =>
            new SharedTextureProducerDiagnostics
            {
                ByteSize = checked((uint)Marshal.SizeOf<SharedTextureProducerDiagnostics>()),
                AdapterDescription = string.Empty
            };

        public string ToTraceDetail() =>
            $"status={LastStatus} flags=0x{Flags:x} " +
            $"probe_attempts={ProbeAttempts} submit_attempts={SubmitAttempts} " +
            $"submitted={Submitted} backpressure={Backpressure} " +
            $"session_invalid={SessionInvalid} adapter_resource_invalid={AdapterOrResourceInvalid} " +
            $"device_copy_failure={DeviceOrCopyFailure} invalid_frame={InvalidFrame} " +
            $"ack_accepted={AcknowledgementsAccepted} ack_rejected={AcknowledgementsRejected} " +
            $"ack_failures={AcknowledgementFailures} " +
            $"last_attempted_generation={LastAttemptedGeneration} " +
            $"last_submitted_generation={LastSubmittedGeneration} " +
            $"last_acknowledged_generation={LastAcknowledgedGeneration} " +
            $"adapter_luid={AdapterLuidHigh:x8}:{AdapterLuidLow:x8} " +
            $"adapter_vendor=0x{AdapterVendorId:x4} adapter_device=0x{AdapterDeviceId:x4} " +
            $"adapter=\"{AdapterDescription ?? string.Empty}\"";
    }
}
