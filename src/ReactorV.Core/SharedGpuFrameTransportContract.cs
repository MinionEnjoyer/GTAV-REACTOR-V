using System;

namespace ReactorV.FrameTransport
{
    /// <summary>
    /// Stable names for the local descriptor channel between the external
    /// default-AppDomain browser producer and the in-process GTA consumer.
    /// The target GTA PID is part of every name; the pipe implementation must
    /// additionally authenticate both endpoint PIDs with the operating system.
    /// </summary>
    public static class SharedGpuFrameTransportNames
    {
        public static string DiscoveryMapping(int targetGtaProcessId) =>
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                @"Local\ReactorV.FrameDiscovery.v1.{0:X8}",
                Validate(targetGtaProcessId));

        public static string Pipe(
            int targetGtaProcessId,
            ulong sessionIdHigh,
            ulong sessionIdLow)
        {
            if (sessionIdHigh == 0 && sessionIdLow == 0)
                throw new ArgumentException(
                    "The shared-GPU session identifier cannot be all zero.",
                    nameof(sessionIdHigh));
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                @"\\.\pipe\ReactorV.Frame.v1.{0:X8}.{1:X16}{2:X16}",
                Validate(targetGtaProcessId),
                sessionIdHigh,
                sessionIdLow);
        }

        private static int Validate(int processId)
        {
            if (processId <= 0)
                throw new ArgumentOutOfRangeException(nameof(processId));
            return processId;
        }
    }

    /// <summary>
    /// Managed producer view of native/include/ReactorV.SharedGpuFrame.h.
    /// This type defines the wire ABI only. Resource, handle, synchronization,
    /// generation, and endpoint validation remain authoritative in the native
    /// GTA consumer before it duplicates or opens anything.
    /// </summary>
    public static class SharedGpuFrameProtocol
    {
        public const uint Magic = 0x46475652u; // "RVGF"
        public const ushort VersionMajor = 1;
        public const ushort VersionMinor = 1;
        public const uint DescriptorByteSize = 152;
        public const uint MaximumDimension = 8192;
        public const ulong MaximumBytes = 128ul * 1024ul * 1024ul;
        public const uint MaximumSlots = 3;
        public const uint RequiredFlags =
            (uint)(SharedGpuFrameFlags.ProducerLocalNtHandles |
                   SharedGpuFrameFlags.PremultipliedAlpha |
                   SharedGpuFrameFlags.TopLeftOrigin);
    }

    public enum SharedGpuPixelFormat : uint
    {
        Unknown = 0,
        Bgra8Unorm = 87,
        Bgra8UnormSrgb = 91,
    }

    public enum SharedGpuSynchronization : uint
    {
        None = 0,
        D3d11KeyedMutex = 1,
        D3d12SharedFence = 2,
    }

    [Flags]
    public enum SharedGpuFrameFlags : uint
    {
        None = 0,
        ProducerLocalNtHandles = 1u << 0,
        PremultipliedAlpha = 1u << 1,
        TopLeftOrigin = 1u << 2,
    }

    /// <summary>
    /// Pointer-free descriptor written as exactly 152 little-endian bytes.
    /// SharedTextureHandle and SharedFenceHandle are producer-local NT handle
    /// values. They are never valid handles in GTA until the authenticated
    /// consumer duplicates them from ProducerProcessId.
    /// </summary>
    public struct SharedGpuFrameDescriptorV1
    {
        public uint Magic;
        public ushort VersionMajor;
        public ushort VersionMinor;
        public uint ByteSize;
        public uint Flags;
        public uint ProducerProcessId;
        public uint ConsumerProcessId;
        public ulong ProducerCreationTime;
        public ulong SessionIdHigh;
        public ulong SessionIdLow;
        public ulong Generation;
        public ulong ResourceEpoch;
        public uint SlotIndex;
        public uint SlotCount;
        public uint Width;
        public uint Height;
        public SharedGpuPixelFormat PixelFormat;
        public SharedGpuSynchronization Synchronization;
        public ulong SharedTextureHandle;
        public ulong SharedFenceHandle;
        public ulong AcquireValue;
        public ulong ReleaseValue;
        public ulong ConsumerCreationTime;
        public ulong Reserved0;
        public ulong Reserved1;
        public ulong Reserved2;
    }

    /// <summary>
    /// Immutable identity negotiated over an authenticated control connection.
    /// Stamping prevents a producer from accidentally publishing a descriptor
    /// for another GTA process or an obsolete browser-process lifetime.
    /// </summary>
    public sealed class SharedGpuFrameSessionIdentity
    {
        public SharedGpuFrameSessionIdentity(
            int targetGtaProcessId,
            ulong targetGtaCreationTime,
            int producerProcessId,
            ulong producerCreationTime,
            ulong sessionIdHigh,
            ulong sessionIdLow)
        {
            if (targetGtaProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetGtaProcessId));
            if (targetGtaCreationTime == 0)
                throw new ArgumentOutOfRangeException(nameof(targetGtaCreationTime));
            if (producerProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(producerProcessId));
            if (producerCreationTime == 0)
                throw new ArgumentOutOfRangeException(nameof(producerCreationTime));
            if (sessionIdHigh == 0 && sessionIdLow == 0)
                throw new ArgumentException(
                    "The shared-GPU session identifier cannot be all zero.",
                    nameof(sessionIdHigh));

            TargetGtaProcessId = targetGtaProcessId;
            TargetGtaCreationTime = targetGtaCreationTime;
            ProducerProcessId = producerProcessId;
            ProducerCreationTime = producerCreationTime;
            SessionIdHigh = sessionIdHigh;
            SessionIdLow = sessionIdLow;
        }

        public int TargetGtaProcessId { get; }
        public ulong TargetGtaCreationTime { get; }
        public int ProducerProcessId { get; }
        public ulong ProducerCreationTime { get; }
        public ulong SessionIdHigh { get; }
        public ulong SessionIdLow { get; }
        public string PipeName => SharedGpuFrameTransportNames.Pipe(
            TargetGtaProcessId,
            SessionIdHigh,
            SessionIdLow);

        public SharedGpuFrameDescriptorV1 CreateDescriptor()
        {
            var descriptor = new SharedGpuFrameDescriptorV1();
            Stamp(ref descriptor);
            return descriptor;
        }

        public void Stamp(ref SharedGpuFrameDescriptorV1 descriptor)
        {
            descriptor.Magic = SharedGpuFrameProtocol.Magic;
            descriptor.VersionMajor = SharedGpuFrameProtocol.VersionMajor;
            descriptor.VersionMinor = SharedGpuFrameProtocol.VersionMinor;
            descriptor.ByteSize = SharedGpuFrameProtocol.DescriptorByteSize;
            descriptor.Flags = SharedGpuFrameProtocol.RequiredFlags;
            descriptor.ProducerProcessId = checked((uint)ProducerProcessId);
            descriptor.ConsumerProcessId = checked((uint)TargetGtaProcessId);
            descriptor.ProducerCreationTime = ProducerCreationTime;
            descriptor.ConsumerCreationTime = TargetGtaCreationTime;
            descriptor.SessionIdHigh = SessionIdHigh;
            descriptor.SessionIdLow = SessionIdLow;
        }
    }

    /// <summary>
    /// Exact, allocation-bounded descriptor codec. It intentionally performs
    /// only ABI/header checks on decode; native validation owns all security
    /// and GPU-resource policy before a handle can be duplicated or opened.
    /// </summary>
    public static class SharedGpuFrameWire
    {
        public static byte[] Encode(SharedGpuFrameDescriptorV1 descriptor)
        {
            var bytes = new byte[SharedGpuFrameProtocol.DescriptorByteSize];
            WriteUInt32(bytes, 0, descriptor.Magic);
            WriteUInt16(bytes, 4, descriptor.VersionMajor);
            WriteUInt16(bytes, 6, descriptor.VersionMinor);
            WriteUInt32(bytes, 8, descriptor.ByteSize);
            WriteUInt32(bytes, 12, descriptor.Flags);
            WriteUInt32(bytes, 16, descriptor.ProducerProcessId);
            WriteUInt32(bytes, 20, descriptor.ConsumerProcessId);
            WriteUInt64(bytes, 24, descriptor.ProducerCreationTime);
            WriteUInt64(bytes, 32, descriptor.SessionIdHigh);
            WriteUInt64(bytes, 40, descriptor.SessionIdLow);
            WriteUInt64(bytes, 48, descriptor.Generation);
            WriteUInt64(bytes, 56, descriptor.ResourceEpoch);
            WriteUInt32(bytes, 64, descriptor.SlotIndex);
            WriteUInt32(bytes, 68, descriptor.SlotCount);
            WriteUInt32(bytes, 72, descriptor.Width);
            WriteUInt32(bytes, 76, descriptor.Height);
            WriteUInt32(bytes, 80, (uint)descriptor.PixelFormat);
            WriteUInt32(bytes, 84, (uint)descriptor.Synchronization);
            WriteUInt64(bytes, 88, descriptor.SharedTextureHandle);
            WriteUInt64(bytes, 96, descriptor.SharedFenceHandle);
            WriteUInt64(bytes, 104, descriptor.AcquireValue);
            WriteUInt64(bytes, 112, descriptor.ReleaseValue);
            WriteUInt64(bytes, 120, descriptor.ConsumerCreationTime);
            WriteUInt64(bytes, 128, descriptor.Reserved0);
            WriteUInt64(bytes, 136, descriptor.Reserved1);
            WriteUInt64(bytes, 144, descriptor.Reserved2);
            return bytes;
        }

        public static bool TryDecode(
            byte[]? bytes,
            out SharedGpuFrameDescriptorV1 descriptor)
        {
            descriptor = default;
            if (bytes == null || bytes.Length != SharedGpuFrameProtocol.DescriptorByteSize)
                return false;

            descriptor.Magic = ReadUInt32(bytes, 0);
            descriptor.VersionMajor = ReadUInt16(bytes, 4);
            descriptor.VersionMinor = ReadUInt16(bytes, 6);
            descriptor.ByteSize = ReadUInt32(bytes, 8);
            descriptor.Flags = ReadUInt32(bytes, 12);
            if (descriptor.Magic != SharedGpuFrameProtocol.Magic ||
                descriptor.VersionMajor != SharedGpuFrameProtocol.VersionMajor ||
                descriptor.VersionMinor > SharedGpuFrameProtocol.VersionMinor ||
                descriptor.ByteSize != SharedGpuFrameProtocol.DescriptorByteSize ||
                descriptor.Flags != SharedGpuFrameProtocol.RequiredFlags)
            {
                descriptor = default;
                return false;
            }

            descriptor.ProducerProcessId = ReadUInt32(bytes, 16);
            descriptor.ConsumerProcessId = ReadUInt32(bytes, 20);
            descriptor.ProducerCreationTime = ReadUInt64(bytes, 24);
            descriptor.SessionIdHigh = ReadUInt64(bytes, 32);
            descriptor.SessionIdLow = ReadUInt64(bytes, 40);
            descriptor.Generation = ReadUInt64(bytes, 48);
            descriptor.ResourceEpoch = ReadUInt64(bytes, 56);
            descriptor.SlotIndex = ReadUInt32(bytes, 64);
            descriptor.SlotCount = ReadUInt32(bytes, 68);
            descriptor.Width = ReadUInt32(bytes, 72);
            descriptor.Height = ReadUInt32(bytes, 76);
            descriptor.PixelFormat =
                (SharedGpuPixelFormat)ReadUInt32(bytes, 80);
            descriptor.Synchronization =
                (SharedGpuSynchronization)ReadUInt32(bytes, 84);
            descriptor.SharedTextureHandle = ReadUInt64(bytes, 88);
            descriptor.SharedFenceHandle = ReadUInt64(bytes, 96);
            descriptor.AcquireValue = ReadUInt64(bytes, 104);
            descriptor.ReleaseValue = ReadUInt64(bytes, 112);
            descriptor.ConsumerCreationTime = ReadUInt64(bytes, 120);
            descriptor.Reserved0 = ReadUInt64(bytes, 128);
            descriptor.Reserved1 = ReadUInt64(bytes, 136);
            descriptor.Reserved2 = ReadUInt64(bytes, 144);
            return true;
        }

        private static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            for (var index = 0; index < 4; ++index)
                bytes[offset + index] = (byte)(value >> (index * 8));
        }

        private static void WriteUInt64(byte[] bytes, int offset, ulong value)
        {
            for (var index = 0; index < 8; ++index)
                bytes[offset + index] = (byte)(value >> (index * 8));
        }

        private static ushort ReadUInt16(byte[] bytes, int offset) =>
            (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            uint value = 0;
            for (var index = 0; index < 4; ++index)
                value |= (uint)bytes[offset + index] << (index * 8);
            return value;
        }

        private static ulong ReadUInt64(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (var index = 0; index < 8; ++index)
                value |= (ulong)bytes[offset + index] << (index * 8);
            return value;
        }
    }
}
