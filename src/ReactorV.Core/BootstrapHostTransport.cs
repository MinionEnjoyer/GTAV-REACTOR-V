using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;

namespace ReactorV.BootstrapHost
{
    /// <summary>Process-scoped names shared by the ASI, preloader host, and runtime proxy.</summary>
    public static class BootstrapHostNames
    {
        public static string Pipe(int processId) =>
            "ReactorV.BootstrapHost." + Validate(processId);

        public static string ReadyEvent(int processId) =>
            @"Local\ReactorV.BootstrapHostReady." + Validate(processId);

        public static string ConnectedEvent(int processId) =>
            @"Local\ReactorV.BootstrapHostConnected." + Validate(processId);

        public static string ToggleEvent(int processId) =>
            @"Local\ReactorV.BootstrapHostToggle." + Validate(processId);

        public static string AboutToggleEvent(int processId) =>
            @"Local\ReactorV.BootstrapHostAboutToggle." + Validate(processId);

        public static string VerifyToggleEvent(int processId) =>
            @"Local\ReactorV.BootstrapHostVerifyToggle." + Validate(processId);

        public static string VerifyActiveEvent(int processId) =>
            @"Local\ReactorV.BootstrapHostVerifyActive." + Validate(processId);

        public static string AboutActiveEvent(int processId) =>
            @"Local\ReactorV.BootstrapHostAboutActive." + Validate(processId);

        public static string InitializerPromotionEvent(int processId) =>
            @"Local\ReactorV.BootstrapHostInitializerPromotion." + Validate(processId);

        public static string CloseEvent(int processId) =>
            @"Local\ReactorV.BootstrapHostClose." + Validate(processId);

        public static string AcceptanceCaptureRequestEvent(int processId) =>
            @"Local\ReactorV.AcceptanceCaptureRequest." + Validate(processId);

        private static int Validate(int processId)
        {
            if (processId <= 0)
                throw new ArgumentOutOfRangeException(nameof(processId));
            return processId;
        }
    }

    /// <summary>
    /// Process-scoped wake signal for an already-written acceptance capture
    /// request.  The event carries no request data or authority: the receiver
    /// must still validate the bounded arm and request files, live harness PID,
    /// expected surface identity, and response path before capturing.
    /// </summary>
    public static class LiveAcceptanceCaptureWakeSignal
    {
        public static bool TrySignal(int targetProcessId, out string failure)
        {
            try
            {
                using (var signal = System.Threading.EventWaitHandle.OpenExisting(
                    BootstrapHostNames.AcceptanceCaptureRequestEvent(targetProcessId)))
                {
                    if (!signal.Set())
                    {
                        failure = "capture_wake_signal_rejected";
                        return false;
                    }
                }
                failure = string.Empty;
                return true;
            }
            catch (System.Threading.WaitHandleCannotBeOpenedException)
            {
                failure = "capture_wake_receiver_unavailable";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                failure = "capture_wake_receiver_access_denied";
                return false;
            }
        }
    }

    /// <summary>
    /// ThreadPool-backed receiver for the capture wake edge.  Unlike a
    /// WinForms timer or file-system callback posted during a fullscreen
    /// transition, the wait itself does not depend on the overlay STA pumping
    /// low-priority window messages.
    /// </summary>
    public sealed class LiveAcceptanceCaptureWakeReceiver : IDisposable
    {
        private readonly Action _callback;
        private readonly System.Threading.EventWaitHandle _signal;
        private readonly System.Threading.RegisteredWaitHandle _registration;
        private int _disposed;

        public LiveAcceptanceCaptureWakeReceiver(
            int targetProcessId,
            Action callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            _signal = new System.Threading.EventWaitHandle(
                false,
                System.Threading.EventResetMode.AutoReset,
                BootstrapHostNames.AcceptanceCaptureRequestEvent(targetProcessId));
            _registration = System.Threading.ThreadPool.RegisterWaitForSingleObject(
                _signal,
                static (state, timedOut) =>
                {
                    if (!timedOut)
                        ((LiveAcceptanceCaptureWakeReceiver)state!).Dispatch();
                },
                this,
                System.Threading.Timeout.Infinite,
                executeOnlyOnce: false);
        }

        private void Dispatch()
        {
            if (System.Threading.Volatile.Read(ref _disposed) != 0)
                return;
            try
            {
                _callback();
            }
            catch
            {
                // A diagnostic wake edge must never terminate the persistent
                // browser host. The fallback poll remains available.
            }
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _registration.Unregister(null);
            _signal.Dispose();
        }
    }

    /// <summary>
    /// Stable wire values for provider visibility intent. Unknown or omitted
    /// reasons fail closed as explicit user actions so an older/malformed
    /// provider can never leave a menu request armed after a real close.
    /// </summary>
    public static class BootstrapHostVisibility
    {
        public const string Explicit = "explicit";
        public const string PresentationPreparation =
            "presentation_preparation";

        public static string Serialize(HostVisibilityReason reason) =>
            reason == HostVisibilityReason.PresentationPreparation
                ? PresentationPreparation
                : Explicit;

        public static bool TryParse(
            string? value,
            out HostVisibilityReason reason)
        {
            if (string.Equals(
                    value,
                    PresentationPreparation,
                    StringComparison.Ordinal))
            {
                reason = HostVisibilityReason.PresentationPreparation;
                return true;
            }
            if (string.Equals(value, Explicit, StringComparison.Ordinal))
            {
                reason = HostVisibilityReason.Explicit;
                return true;
            }
            reason = HostVisibilityReason.Explicit;
            return false;
        }
    }

    /// <summary>
    /// Defines the authenticated first frame for a bootstrap-host provider
    /// session. The OS pipe client PID remains the primary process-boundary
    /// check; this exact envelope also prevents protocol drift or an accidental
    /// in-process pipe client from being promoted to the active provider.
    /// </summary>
    public static class BootstrapHostHandshake
    {
        public const int ProtocolVersion = 3;

        public static JObject CreateHello(int processId)
        {
            if (processId <= 0)
                throw new ArgumentOutOfRangeException(nameof(processId));
            return new JObject
            {
                ["type"] = "hello",
                ["protocol"] = ProtocolVersion,
                ["pid"] = processId,
            };
        }

        public static bool TryValidateHello(
            JObject? message,
            int expectedProcessId,
            out string reason)
        {
            if (expectedProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedProcessId));
            if (message == null)
            {
                reason = "hello_missing";
                return false;
            }
            if (message.Count != 3)
            {
                reason = "hello_field_count_invalid";
                return false;
            }

            var type = message["type"];
            if (type?.Type != JTokenType.String ||
                !string.Equals(type.Value<string>(), "hello", StringComparison.Ordinal))
            {
                reason = "hello_type_invalid";
                return false;
            }

            var protocol = message["protocol"];
            if (protocol?.Type != JTokenType.Integer ||
                protocol.Value<long>() != ProtocolVersion)
            {
                reason = "hello_protocol_invalid";
                return false;
            }

            var processId = message["pid"];
            if (processId?.Type != JTokenType.Integer ||
                processId.Value<long>() != expectedProcessId)
            {
                reason = "hello_pid_invalid";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static JObject CreateReadyAcknowledgement(
            int contentGeneration,
            bool ready)
        {
            if (contentGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(contentGeneration));
            return new JObject
            {
                ["type"] = "hello_ack",
                ["protocol"] = ProtocolVersion,
                ["generation"] = contentGeneration,
                ["ready"] = ready,
            };
        }

        public static bool TryValidateReadyAcknowledgement(
            JObject? message,
            out int contentGeneration,
            out bool ready,
            out string reason)
        {
            contentGeneration = 0;
            ready = false;
            if (message == null)
            {
                reason = "hello_ack_missing";
                return false;
            }
            if (message.Count != 4)
            {
                reason = "hello_ack_field_count_invalid";
                return false;
            }
            if (message["type"]?.Type != JTokenType.String ||
                !string.Equals(
                    message.Value<string>("type"),
                    "hello_ack",
                    StringComparison.Ordinal))
            {
                reason = "hello_ack_type_invalid";
                return false;
            }
            if (message["protocol"]?.Type != JTokenType.Integer ||
                message.Value<long>("protocol") != ProtocolVersion)
            {
                reason = "hello_ack_protocol_invalid";
                return false;
            }
            if (message["generation"]?.Type != JTokenType.Integer ||
                message.Value<long>("generation") <= 0 ||
                message.Value<long>("generation") > int.MaxValue)
            {
                reason = "hello_ack_generation_invalid";
                return false;
            }
            if (message["ready"]?.Type != JTokenType.Boolean)
            {
                reason = "hello_ack_ready_invalid";
                return false;
            }

            contentGeneration = message.Value<int>("generation");
            ready = message.Value<bool>("ready");
            reason = string.Empty;
            return true;
        }

        public static JObject CreateRuntimeReadyLeaseRequest(
            int contentGeneration,
            string requestId)
        {
            ValidateContentGeneration(contentGeneration);
            ValidateRequestId(requestId);
            return new JObject
            {
                ["type"] = "runtime_ready_lease",
                ["protocol"] = ProtocolVersion,
                ["generation"] = contentGeneration,
                ["requestId"] = requestId,
            };
        }

        public static bool TryValidateRuntimeReadyLeaseRequest(
            JObject? message,
            out int contentGeneration,
            out string requestId,
            out string reason)
        {
            contentGeneration = 0;
            requestId = string.Empty;
            if (!TryValidateRuntimeReadyEnvelope(
                    message,
                    "runtime_ready_lease",
                    4,
                    out contentGeneration,
                    out requestId,
                    out reason))
                return false;
            reason = string.Empty;
            return true;
        }

        public static JObject CreateRuntimeReadyLeaseAcknowledgement(
            int contentGeneration,
            string requestId,
            bool validated,
            bool signaled)
        {
            ValidateContentGeneration(contentGeneration);
            ValidateRequestId(requestId);
            if (signaled && !validated)
                throw new ArgumentException(
                    "A runtime-ready event cannot be signaled by an unvalidated lease.",
                    nameof(signaled));
            return new JObject
            {
                ["type"] = "runtime_ready_lease_ack",
                ["protocol"] = ProtocolVersion,
                ["generation"] = contentGeneration,
                ["requestId"] = requestId,
                ["validated"] = validated,
                ["signaled"] = signaled,
            };
        }

        public static bool TryValidateRuntimeReadyLeaseAcknowledgement(
            JObject? message,
            out int contentGeneration,
            out string requestId,
            out bool validated,
            out bool signaled,
            out string reason)
        {
            validated = false;
            signaled = false;
            if (!TryValidateRuntimeReadyEnvelope(
                    message,
                    "runtime_ready_lease_ack",
                    6,
                    out contentGeneration,
                    out requestId,
                    out reason))
                return false;
            if (message!["validated"]?.Type != JTokenType.Boolean ||
                message["signaled"]?.Type != JTokenType.Boolean)
            {
                reason = "runtime_ready_lease_ack_result_invalid";
                return false;
            }
            validated = message.Value<bool>("validated");
            signaled = message.Value<bool>("signaled");
            if (signaled && !validated)
            {
                reason = "runtime_ready_lease_ack_result_inconsistent";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private static bool TryValidateRuntimeReadyEnvelope(
            JObject? message,
            string expectedType,
            int expectedFieldCount,
            out int contentGeneration,
            out string requestId,
            out string reason)
        {
            contentGeneration = 0;
            requestId = string.Empty;
            if (message == null)
            {
                reason = expectedType + "_missing";
                return false;
            }
            if (message.Count != expectedFieldCount)
            {
                reason = expectedType + "_field_count_invalid";
                return false;
            }
            if (message["type"]?.Type != JTokenType.String ||
                !string.Equals(message.Value<string>("type"), expectedType, StringComparison.Ordinal))
            {
                reason = expectedType + "_type_invalid";
                return false;
            }
            if (message["protocol"]?.Type != JTokenType.Integer ||
                message.Value<long>("protocol") != ProtocolVersion)
            {
                reason = expectedType + "_protocol_invalid";
                return false;
            }
            if (message["generation"]?.Type != JTokenType.Integer ||
                message.Value<long>("generation") <= 0 ||
                message.Value<long>("generation") > int.MaxValue)
            {
                reason = expectedType + "_generation_invalid";
                return false;
            }
            var candidateRequestId = message.Value<string>("requestId");
            if (message["requestId"]?.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(candidateRequestId) ||
                candidateRequestId!.Length > 64)
            {
                reason = expectedType + "_request_id_invalid";
                return false;
            }
            contentGeneration = message.Value<int>("generation");
            requestId = candidateRequestId;
            reason = string.Empty;
            return true;
        }

        private static void ValidateContentGeneration(int contentGeneration)
        {
            if (contentGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(contentGeneration));
        }

        private static void ValidateRequestId(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64)
                throw new ArgumentException(
                    "The runtime-ready lease request ID is invalid.",
                    nameof(requestId));
        }
    }

    /// <summary>
    /// Tracks which browser-content generation owns the process-scoped Ready
    /// event. A browser failure advances the generation before resetting
    /// readiness, so a provider can never promote an acknowledgement from the
    /// page that just died.
    /// </summary>
    public sealed class BootstrapHostReadinessGeneration
    {
        private readonly object _sync = new object();
        private int _generation = 1;
        private bool _ready;

        public int CurrentGeneration
        {
            get { lock (_sync) return _generation; }
        }

        public bool IsReady
        {
            get { lock (_sync) return _ready; }
        }

        public int MarkUnavailable()
        {
            lock (_sync)
            {
                _ready = false;
                checked { _generation++; }
                return _generation;
            }
        }

        public int MarkReady()
        {
            lock (_sync)
            {
                _ready = true;
                return _generation;
            }
        }

        public JObject CreateAcknowledgement()
        {
            lock (_sync)
                return BootstrapHostHandshake.CreateReadyAcknowledgement(
                    _generation,
                    _ready);
        }

        public bool IsCurrentReady(int generation)
        {
            lock (_sync)
                return _ready && generation == _generation;
        }

        public bool TrySignalCurrentReady(
            int generation,
            Func<bool> signal,
            out bool validated)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            lock (_sync)
            {
                validated = _ready && generation == _generation;
                return validated && signal();
            }
        }
    }

    /// <summary>
    /// Length-prefixed JSON framing for the local named pipe. Framing is kept
    /// deliberately small and deterministic so a malformed or abandoned
    /// provider cannot make the bootstrap host allocate unbounded memory.
    /// </summary>
    public static class BootstrapHostWire
    {
        // BridgeProtocol caps the nested browser JSON at 64 KiB. The envelope
        // can roughly double that size while escaping a JSON string, so retain
        // a bounded 256 KiB transport ceiling without constraining the public
        // bridge contract.
        public const int MaximumFrameBytes = 256 * 1024;

        public static void Write(Stream stream, JObject message)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (message == null) throw new ArgumentNullException(nameof(message));
            var payload = Encoding.UTF8.GetBytes(message.ToString(Newtonsoft.Json.Formatting.None));
            if (payload.Length <= 0 || payload.Length > MaximumFrameBytes)
                throw new InvalidDataException("The bootstrap-host frame is outside the allowed size.");
            var length = BitConverter.GetBytes(payload.Length);
            stream.Write(length, 0, length.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        public static JObject? Read(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            var lengthBytes = ReadExactly(stream, sizeof(int));
            if (lengthBytes == null) return null;
            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > MaximumFrameBytes)
                throw new InvalidDataException("The bootstrap-host frame declared an invalid size.");
            var payload = ReadExactly(stream, length);
            if (payload == null) throw new EndOfStreamException("The bootstrap-host frame ended early.");
            return JObject.Parse(Encoding.UTF8.GetString(payload));
        }

        private static byte[]? ReadExactly(Stream stream, int length)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read <= 0) return offset == 0 ? null : throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }
    }
}
