using System;
using System.IO;
using RageWebUI.Core;
using ReactorV.FrameTransport;
using ReactorV.ExternalGpu;
using ReactorV.Preloader;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class SharedGpuFrameTransportContractTests
    {
        [Fact]
        public void Names_match_native_discovery_and_session_scoped_pipe_contracts()
        {
            Assert.Equal(
                @"Local\ReactorV.FrameDiscovery.v1.00001092",
                SharedGpuFrameTransportNames.DiscoveryMapping(4242));
            Assert.Equal(
                @"\\.\pipe\ReactorV.Frame.v1.00001092.00000000000000010000000000000002",
                SharedGpuFrameTransportNames.Pipe(4242, 1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SharedGpuFrameTransportNames.DiscoveryMapping(0));
            Assert.Throws<ArgumentException>(() =>
                SharedGpuFrameTransportNames.Pipe(1, 0, 0));
        }

        [Fact]
        public void Session_identity_stamps_the_native_abi_and_both_process_lifetimes()
        {
            var session = new SharedGpuFrameSessionIdentity(
                targetGtaProcessId: 4242,
                targetGtaCreationTime: 0x0101010102020202,
                producerProcessId: 5252,
                producerCreationTime: 0x1122334455667788,
                sessionIdHigh: 0x8877665544332211,
                sessionIdLow: 0x1020304050607080);

            var descriptor = session.CreateDescriptor();

            Assert.Equal(SharedGpuFrameProtocol.Magic, descriptor.Magic);
            Assert.Equal(SharedGpuFrameProtocol.VersionMajor, descriptor.VersionMajor);
            Assert.Equal(SharedGpuFrameProtocol.VersionMinor, descriptor.VersionMinor);
            Assert.Equal(SharedGpuFrameProtocol.DescriptorByteSize, descriptor.ByteSize);
            Assert.Equal(SharedGpuFrameProtocol.RequiredFlags, descriptor.Flags);
            Assert.Equal(5252u, descriptor.ProducerProcessId);
            Assert.Equal(4242u, descriptor.ConsumerProcessId);
            Assert.Equal(0x1122334455667788ul, descriptor.ProducerCreationTime);
            Assert.Equal(0x0101010102020202ul, descriptor.ConsumerCreationTime);
            Assert.Equal(0x8877665544332211ul, descriptor.SessionIdHigh);
            Assert.Equal(0x1020304050607080ul, descriptor.SessionIdLow);
            Assert.Equal(
                @"\\.\pipe\ReactorV.Frame.v1.00001092.88776655443322111020304050607080",
                session.PipeName);
        }

        [Fact]
        public void Session_identity_rejects_unscoped_or_reusable_process_identity()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SharedGpuFrameSessionIdentity(0, 1, 2, 3, 4, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SharedGpuFrameSessionIdentity(1, 0, 2, 3, 4, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SharedGpuFrameSessionIdentity(1, 2, 0, 3, 4, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SharedGpuFrameSessionIdentity(1, 2, 3, 0, 4, 5));
            Assert.Throws<ArgumentException>(() =>
                new SharedGpuFrameSessionIdentity(1, 2, 3, 4, 0, 0));
        }

        [Fact]
        public void Descriptor_codec_matches_the_fixed_152_byte_little_endian_layout()
        {
            var session = new SharedGpuFrameSessionIdentity(
                4242,
                0x3131313132323232,
                5252,
                0x0102030405060708,
                0x1112131415161718,
                0x2122232425262728);
            var descriptor = session.CreateDescriptor();
            descriptor.Generation = 0x3132333435363738;
            descriptor.ResourceEpoch = 9;
            descriptor.SlotIndex = 1;
            descriptor.SlotCount = 3;
            descriptor.Width = 2560;
            descriptor.Height = 1440;
            descriptor.PixelFormat = SharedGpuPixelFormat.Bgra8Unorm;
            descriptor.Synchronization = SharedGpuSynchronization.D3d11KeyedMutex;
            descriptor.SharedTextureHandle = 0x4142434445464748;
            descriptor.AcquireValue = 10;
            descriptor.ReleaseValue = 11;

            var bytes = SharedGpuFrameWire.Encode(descriptor);

            Assert.Equal(152, bytes.Length);
            Assert.Equal(new byte[] { 0x52, 0x56, 0x47, 0x46 }, bytes[0..4]);
            Assert.Equal(0x38, bytes[48]);
            Assert.Equal(0x31, bytes[55]);
            Assert.Equal(0x48, bytes[88]);
            Assert.Equal(0x41, bytes[95]);
            Assert.Equal(0x32, bytes[120]);
            Assert.Equal(0x31, bytes[127]);
            Assert.True(SharedGpuFrameWire.TryDecode(bytes, out var decoded));
            Assert.Equal(descriptor.Generation, decoded.Generation);
            Assert.Equal(descriptor.Width, decoded.Width);
            Assert.Equal(descriptor.SharedTextureHandle, decoded.SharedTextureHandle);
            Assert.Equal(descriptor.ReleaseValue, decoded.ReleaseValue);
            Assert.Equal(descriptor.ConsumerCreationTime, decoded.ConsumerCreationTime);
        }

        [Fact]
        public void Managed_wire_values_are_pinned_to_the_native_authority()
        {
            var native = ReadRepositoryFile(
                "native", "include", "ReactorV.SharedGpuFrame.h");

            Assert.Contains("SharedGpuFrameMagic = 0x46475652u", native);
            Assert.Contains("SharedGpuFrameVersionMajor = 1", native);
            Assert.Contains("SharedGpuFrameVersionMinor = 1", native);
            Assert.Contains("std::uint64_t consumerCreationTime{}", native);
            Assert.Contains("SharedGpuFrameDescriptorV1ByteSize = 152", native);
            Assert.Contains("128ull * 1024ull * 1024ull", native);
            Assert.Equal(128ul * 1024ul * 1024ul, SharedGpuFrameProtocol.MaximumBytes);
            Assert.Contains("Bgra8Unorm = 87", native);
            Assert.Contains("Bgra8UnormSrgb = 91", native);
            Assert.Contains("D3d11KeyedMutex = 1", native);
            Assert.Contains("D3d12SharedFence = 2", native);
            Assert.Contains(
                "sizeof(SharedGpuFrameDescriptorV1) ==\n    SharedGpuFrameDescriptorV1ByteSize",
                native.Replace("\r\n", "\n", StringComparison.Ordinal));
        }

        [Fact]
        public void Descriptor_decode_fails_closed_on_size_or_header_drift()
        {
            Assert.False(SharedGpuFrameWire.TryDecode(
                new byte[SharedGpuFrameProtocol.DescriptorByteSize - 1],
                out _));

            var descriptor = new SharedGpuFrameSessionIdentity(
                1, 2, 3, 4, 5, 6).CreateDescriptor();
            var bytes = SharedGpuFrameWire.Encode(descriptor);
            bytes[0] ^= 0xff;
            Assert.False(SharedGpuFrameWire.TryDecode(bytes, out _));

            bytes = SharedGpuFrameWire.Encode(descriptor);
            bytes[6] = 2;
            Assert.False(SharedGpuFrameWire.TryDecode(bytes, out _));
        }

        [Fact]
        public void Preloader_producer_context_reuses_the_existing_bridge_authority()
        {
            var bridge = new BridgeBroker();
            var context = new ExternalGpuBrowserProducerContext(
                4242,
                ".",
                ".",
                ".",
                bridge,
                2560,
                1440,
                60,
                enableDevTools: false);

            Assert.Same(bridge, context.BridgeSink);
            Assert.Equal(
                @"Local\ReactorV.FrameDiscovery.v1.00001092",
                context.TransportDiscoveryName);
            Assert.Equal(4242, context.TargetGtaProcessId);
            Assert.Equal(2560, context.Width);
            Assert.Equal(1440, context.Height);
            Assert.Equal(60, context.FrameRate);
        }

        [Fact]
        public void Preloader_producer_context_rejects_invalid_transport_bounds()
        {
            var bridge = new BridgeBroker();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ExternalGpuBrowserProducerContext(
                    0, ".", ".", ".", bridge, 1, 1, 30, false));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ExternalGpuBrowserProducerContext(
                    1,
                    ".",
                    ".",
                    ".",
                    bridge,
                    checked((int)SharedGpuFrameProtocol.MaximumDimension + 1),
                    1,
                    30,
                    false));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ExternalGpuBrowserProducerContext(
                    1,
                    ".",
                    ".",
                    ".",
                    bridge,
                    8192,
                    8192,
                    30,
                    false));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ExternalGpuBrowserProducerContext(
                    1, ".", ".", ".", bridge, 1, 1, 61, false));
        }

        private static string ReadRepositoryFile(params string[] parts)
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null &&
                !(File.Exists(Path.Combine(current.FullName, "ReactorV.json")) &&
                  Directory.Exists(Path.Combine(current.FullName, "src"))))
            {
                current = current.Parent;
            }
            Assert.NotNull(current);
            return File.ReadAllText(Path.Combine(
                current!.FullName,
                Path.Combine(parts)));
        }
    }
}
