using System;
using System.IO;
using Newtonsoft.Json.Linq;
using ReactorV.BootstrapHost;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class BootstrapHostTransportTests
    {
        [Fact]
        public void Names_are_scoped_to_the_gta_process()
        {
            Assert.Equal("ReactorV.BootstrapHost.4242", BootstrapHostNames.Pipe(4242));
            Assert.Equal(@"Local\ReactorV.BootstrapHostReady.4242", BootstrapHostNames.ReadyEvent(4242));
            Assert.Equal(@"Local\ReactorV.BootstrapHostConnected.4242", BootstrapHostNames.ConnectedEvent(4242));
            Assert.Equal(@"Local\ReactorV.BootstrapHostToggle.4242", BootstrapHostNames.ToggleEvent(4242));
            Assert.Equal(@"Local\ReactorV.BootstrapHostAboutToggle.4242", BootstrapHostNames.AboutToggleEvent(4242));
            Assert.Equal(@"Local\ReactorV.BootstrapHostVerifyToggle.4242", BootstrapHostNames.VerifyToggleEvent(4242));
            Assert.Equal(@"Local\ReactorV.BootstrapHostVerifyActive.4242", BootstrapHostNames.VerifyActiveEvent(4242));
            Assert.Equal(@"Local\ReactorV.BootstrapHostAboutActive.4242", BootstrapHostNames.AboutActiveEvent(4242));
            Assert.Equal(@"Local\ReactorV.BootstrapHostInitializerPromotion.4242", BootstrapHostNames.InitializerPromotionEvent(4242));
            Assert.Equal(@"Local\ReactorV.BootstrapHostClose.4242", BootstrapHostNames.CloseEvent(4242));
            Assert.Equal(
                @"Local\ReactorV.AcceptanceCaptureRequest.4242",
                BootstrapHostNames.AcceptanceCaptureRequestEvent(4242));
            Assert.Throws<ArgumentOutOfRangeException>(() => BootstrapHostNames.Pipe(0));
        }

        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void Acceptance_capture_wake_crosses_handles_without_window_message_polling()
        {
            const int processId = 2147483004;
            using (var received = new System.Threading.ManualResetEventSlim())
            using (var receiver = new LiveAcceptanceCaptureWakeReceiver(
                processId,
                received.Set))
            {
                Assert.True(LiveAcceptanceCaptureWakeSignal.TrySignal(
                    processId,
                    out var failure), failure);
                Assert.True(received.Wait(TimeSpan.FromSeconds(2)));

                received.Reset();
                Assert.False(received.Wait(TimeSpan.FromMilliseconds(100)));
            }
        }

        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void Acceptance_capture_wake_fails_closed_without_a_receiver()
        {
            const int processId = 2147483005;
            Assert.False(LiveAcceptanceCaptureWakeSignal.TrySignal(
                processId,
                out var failure));
            Assert.Equal("capture_wake_receiver_unavailable", failure);
        }

        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void Verification_active_boundary_is_a_cross_handle_manual_reset_acknowledgement()
        {
            const int processId = 2147483001;
            using (var owner = new System.Threading.EventWaitHandle(
                false,
                System.Threading.EventResetMode.ManualReset,
                BootstrapHostNames.VerifyActiveEvent(processId)))
            using (var observer = System.Threading.EventWaitHandle.OpenExisting(
                BootstrapHostNames.VerifyActiveEvent(processId)))
            {
                owner.Reset();
                Assert.False(observer.WaitOne(0));
                owner.Set();
                Assert.True(observer.WaitOne(0));
                Assert.True(observer.WaitOne(0));
                owner.Reset();
                Assert.False(observer.WaitOne(0));
            }
        }

        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void About_active_boundary_is_a_cross_handle_manual_reset_acknowledgement()
        {
            const int processId = 2147483002;
            using (var owner = new System.Threading.EventWaitHandle(
                false,
                System.Threading.EventResetMode.ManualReset,
                BootstrapHostNames.AboutActiveEvent(processId)))
            using (var observer = System.Threading.EventWaitHandle.OpenExisting(
                BootstrapHostNames.AboutActiveEvent(processId)))
            {
                owner.Reset();
                Assert.False(observer.WaitOne(0));
                owner.Set();
                Assert.True(observer.WaitOne(0));
                Assert.True(observer.WaitOne(0));
                owner.Reset();
                Assert.False(observer.WaitOne(0));
            }
        }

        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void Initializer_promotion_boundary_delivers_one_typed_edge()
        {
            const int processId = 2147483003;
            using (var producer = new System.Threading.EventWaitHandle(
                false,
                System.Threading.EventResetMode.AutoReset,
                BootstrapHostNames.InitializerPromotionEvent(processId)))
            using (var consumer = System.Threading.EventWaitHandle.OpenExisting(
                BootstrapHostNames.InitializerPromotionEvent(processId)))
            {
                producer.Set();
                Assert.True(consumer.WaitOne(0));
                Assert.False(consumer.WaitOne(0));
            }
        }

        [Fact]
        public void Wire_round_trips_one_bounded_json_frame()
        {
            using (var stream = new MemoryStream())
            {
                BootstrapHostWire.Write(stream, new JObject
                {
                    ["type"] = "visible",
                    ["value"] = true,
                });
                stream.Position = 0;
                var actual = BootstrapHostWire.Read(stream);
                Assert.Equal("visible", actual!.Value<string>("type"));
                Assert.True(actual.Value<bool>("value"));
            }
        }

        [Fact]
        public void Visibility_reasons_are_typed_and_unknown_values_fail_closed()
        {
            Assert.Equal(
                BootstrapHostVisibility.PresentationPreparation,
                BootstrapHostVisibility.Serialize(
                    HostVisibilityReason.PresentationPreparation));
            Assert.True(BootstrapHostVisibility.TryParse(
                BootstrapHostVisibility.PresentationPreparation,
                out var preparation));
            Assert.Equal(
                HostVisibilityReason.PresentationPreparation,
                preparation);

            Assert.False(BootstrapHostVisibility.TryParse(
                "future-or-malformed",
                out var failClosed));
            Assert.Equal(HostVisibilityReason.Explicit, failClosed);
        }

        [Fact]
        public void Wire_rejects_oversized_declared_frames_before_allocating_payload()
        {
            using (var stream = new MemoryStream())
            {
                var declared = BitConverter.GetBytes(BootstrapHostWire.MaximumFrameBytes + 1);
                stream.Write(declared, 0, declared.Length);
                stream.Position = 0;
                Assert.Throws<InvalidDataException>(() => BootstrapHostWire.Read(stream));
            }
        }

        [Fact]
        public void Handshake_accepts_only_the_exact_protocol_and_process_envelope()
        {
            var hello = BootstrapHostHandshake.CreateHello(4242);

            Assert.True(BootstrapHostHandshake.TryValidateHello(hello, 4242, out var reason));
            Assert.Equal(string.Empty, reason);
            Assert.Equal(3, hello.Count);
            Assert.Equal("hello", hello.Value<string>("type"));
            Assert.Equal(BootstrapHostHandshake.ProtocolVersion, hello.Value<int>("protocol"));
            Assert.Equal(4242, hello.Value<int>("pid"));
        }

        [Fact]
        public void Handshake_rejects_missing_wrong_or_extended_first_frames()
        {
            Assert.False(BootstrapHostHandshake.TryValidateHello(null, 4242, out var missing));
            Assert.Equal("hello_missing", missing);

            var wrongProtocol = BootstrapHostHandshake.CreateHello(4242);
            wrongProtocol["protocol"] = BootstrapHostHandshake.ProtocolVersion + 1;
            Assert.False(BootstrapHostHandshake.TryValidateHello(wrongProtocol, 4242, out var protocol));
            Assert.Equal("hello_protocol_invalid", protocol);

            var wrongPid = BootstrapHostHandshake.CreateHello(4243);
            Assert.False(BootstrapHostHandshake.TryValidateHello(wrongPid, 4242, out var pid));
            Assert.Equal("hello_pid_invalid", pid);

            var extended = BootstrapHostHandshake.CreateHello(4242);
            extended["unexpected"] = true;
            Assert.False(BootstrapHostHandshake.TryValidateHello(extended, 4242, out var fields));
            Assert.Equal("hello_field_count_invalid", fields);

            Assert.Throws<ArgumentOutOfRangeException>(() => BootstrapHostHandshake.CreateHello(0));
        }

        [Fact]
        public void Readiness_acknowledgement_is_typed_bounded_and_generation_aware()
        {
            var acknowledgement = BootstrapHostHandshake.CreateReadyAcknowledgement(7, true);

            Assert.True(BootstrapHostHandshake.TryValidateReadyAcknowledgement(
                acknowledgement,
                out var generation,
                out var ready,
                out var reason));
            Assert.Equal(string.Empty, reason);
            Assert.Equal(7, generation);
            Assert.True(ready);

            acknowledgement["unexpected"] = true;
            Assert.False(BootstrapHostHandshake.TryValidateReadyAcknowledgement(
                acknowledgement,
                out _,
                out _,
                out var extended));
            Assert.Equal("hello_ack_field_count_invalid", extended);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BootstrapHostHandshake.CreateReadyAcknowledgement(0, true));
        }

        [Fact]
        public void Pre_shvdn_browser_failure_invalidates_stale_ready_generation_until_recovery()
        {
            var readiness = new BootstrapHostReadinessGeneration();
            var firstGeneration = readiness.MarkReady();
            var staleAcknowledgement = readiness.CreateAcknowledgement();
            Assert.True(readiness.IsCurrentReady(firstGeneration));

            var recoveryGeneration = readiness.MarkUnavailable();
            Assert.False(readiness.IsCurrentReady(firstGeneration));
            Assert.False(readiness.IsCurrentReady(recoveryGeneration));
            Assert.True(BootstrapHostHandshake.TryValidateReadyAcknowledgement(
                staleAcknowledgement,
                out var staleGeneration,
                out var staleReady,
                out _));
            Assert.True(staleReady);
            Assert.NotEqual(recoveryGeneration, staleGeneration);

            Assert.Equal(recoveryGeneration, readiness.MarkReady());
            Assert.True(readiness.IsCurrentReady(recoveryGeneration));
            var recoveredAcknowledgement = readiness.CreateAcknowledgement();
            Assert.True(BootstrapHostHandshake.TryValidateReadyAcknowledgement(
                recoveredAcknowledgement,
                out var currentGeneration,
                out var currentReady,
                out _));
            Assert.True(currentReady);
            Assert.Equal(recoveryGeneration, currentGeneration);
        }

        [Fact]
        public void Runtime_ready_lease_frames_are_typed_bounded_and_correlated()
        {
            var request = BootstrapHostHandshake.CreateRuntimeReadyLeaseRequest(
                7,
                "lease-7");
            Assert.True(BootstrapHostHandshake.TryValidateRuntimeReadyLeaseRequest(
                request,
                out var requestGeneration,
                out var requestId,
                out var requestReason));
            Assert.Equal(string.Empty, requestReason);
            Assert.Equal(7, requestGeneration);
            Assert.Equal("lease-7", requestId);

            var acknowledgement =
                BootstrapHostHandshake.CreateRuntimeReadyLeaseAcknowledgement(
                    7,
                    "lease-7",
                    validated: true,
                    signaled: true);
            Assert.True(BootstrapHostHandshake.TryValidateRuntimeReadyLeaseAcknowledgement(
                acknowledgement,
                out var acknowledgementGeneration,
                out var acknowledgementId,
                out var validated,
                out var signaled,
                out var acknowledgementReason));
            Assert.Equal(string.Empty, acknowledgementReason);
            Assert.Equal(7, acknowledgementGeneration);
            Assert.Equal("lease-7", acknowledgementId);
            Assert.True(validated);
            Assert.True(signaled);

            acknowledgement["validated"] = false;
            Assert.False(BootstrapHostHandshake.TryValidateRuntimeReadyLeaseAcknowledgement(
                acknowledgement,
                out _,
                out _,
                out _,
                out _,
                out var inconsistent));
            Assert.Equal(
                "runtime_ready_lease_ack_result_inconsistent",
                inconsistent);
        }

        [Fact]
        public void Runtime_ready_signal_is_atomic_with_authoritative_generation_validation()
        {
            var readiness = new BootstrapHostReadinessGeneration();
            var readyGeneration = readiness.MarkReady();
            var signalCalls = 0;

            Assert.True(readiness.TrySignalCurrentReady(
                readyGeneration,
                () =>
                {
                    signalCalls++;
                    return true;
                },
                out var validated));
            Assert.True(validated);
            Assert.Equal(1, signalCalls);

            var recoveryGeneration = readiness.MarkUnavailable();
            Assert.False(readiness.TrySignalCurrentReady(
                readyGeneration,
                () =>
                {
                    signalCalls++;
                    return true;
                },
                out validated));
            Assert.False(validated);
            Assert.Equal(1, signalCalls);

            readiness.MarkReady();
            Assert.False(readiness.TrySignalCurrentReady(
                recoveryGeneration,
                () =>
                {
                    signalCalls++;
                    return false;
                },
                out validated));
            Assert.True(validated);
            Assert.Equal(2, signalCalls);
        }

        [Fact]
        public void Runtime_ready_lease_acknowledgement_timeout_is_bounded_and_fail_closed()
        {
            Assert.InRange(
                RuntimeReadyHandoffPolicy.LeaseAcknowledgementTimeoutMilliseconds,
                250,
                5000);
            Assert.False(RuntimeReadyHandoffPolicy.HasLeaseAcknowledgementTimedOut(
                RuntimeReadyHandoffPolicy.LeaseAcknowledgementTimeoutMilliseconds - 1));
            Assert.True(RuntimeReadyHandoffPolicy.HasLeaseAcknowledgementTimedOut(
                RuntimeReadyHandoffPolicy.LeaseAcknowledgementTimeoutMilliseconds));
        }
    }
}
