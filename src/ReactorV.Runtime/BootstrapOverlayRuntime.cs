using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using ReactorV.BootstrapHost;
using ReactorV.WebView2Host;

namespace RageWebUI.Runtime
{
    /// <summary>
    /// Lightweight provider proxy for the WebView that the native bootstrap
    /// launched while GTA was still loading. No browser, WinForms surface, or
    /// WebView profile is recreated beneath SHVDN.
    /// </summary>
    internal sealed class BootstrapOverlayRuntime :
        IOverlayRuntime,
        IProviderPresentationCommitRuntime,
        IProviderInputIntentRuntime,
        IContentGenerationRuntime,
        IBootstrapSurfaceRuntime,
        IAuthoritativeHostSurfaceRuntime,
        IReasonedVisibilityRuntime,
        IInteractionForegroundRuntime
    {
        private const int MaximumQueuedFrames = 512;
        private const int ContentRecoveryWaitMilliseconds = 5000;
        private const int TeardownBudgetMilliseconds = 750;

        private readonly int _gtaProcessId;
        private readonly string _logDirectory;
        private readonly BridgeBroker _broker;
        private readonly bool _startVisible;
        private readonly ConcurrentQueue<JObject> _outgoing = new ConcurrentQueue<JObject>();
        private readonly AutoResetEvent _outgoingReady = new AutoResetEvent(false);
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly object _pointerSync = new object();
        private readonly object _writeSync = new object();
        private readonly object _runtimeReadySync = new object();
        private NamedPipeClientStream? _pipe;
        private Thread? _reader;
        private Thread? _writer;
        private JObject? _pendingPointerMove;
        private readonly ConcurrentQueue<JObject> _pointerEdges = new ConcurrentQueue<JObject>();
        private bool _hasCursor;
        private float _lastCursorX;
        private float _lastCursorY;
        private int _queuedFrames;
        private int _visible;
        private int _contentGeneration;
        private int _contentReady;
        private int _lastTracedContentGeneration;
        private int _lastTracedContentReady = -1;
        private int _hostProcessId;
        private string _hostSurfaceMode = HostSurfaceMode.None;
        private string? _committedProviderPresentationId;
        private string? _userIntentAuthorizedProviderPresentationId;
        private int _bootstrapSurfaceRetirementPending;
        private int _bootstrapSurfaceRetirementRequiresHidden;
        private int _runtimeReadyGeneration;
        private string? _runtimeReadyRequestId;
        private RuntimeReadyHandoffState _runtimeReadyState =
            RuntimeReadyHandoffState.Unavailable;
        private long _runtimeReadyRequestStartedAt;
        private int _started;
        private int _disposed;
        private int _workerHandlesDisposed;

        public BootstrapOverlayRuntime(
            int gtaProcessId,
            string logDirectory,
            BridgeBroker broker,
            bool startVisible)
        {
            if (gtaProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(gtaProcessId));
            _gtaProcessId = gtaProcessId;
            _logDirectory = logDirectory;
            _broker = broker;
            _startVisible = startVisible;
        }

        public bool IsVisible => Volatile.Read(ref _visible) == 1;

        public string RendererName => "Bootstrap WebView2";

        public bool IsTrustedProviderForeground
        {
            get
            {
                var hostProcessId = Volatile.Read(ref _hostProcessId);
                return WindowedInputPolicy.IsTrustedProviderForeground(
                    hostProcessId > 0 ? (uint)hostProcessId : 0,
                    NativeMethods.GetForegroundProcessId());
            }
        }

        public string CurrentHostSurface => Volatile.Read(ref _hostSurfaceMode);

        public bool HasAuthoritativeHostSurfaceBoundary => true;

        public bool IsProviderPresentationCommitted(string presentationId) =>
            ProviderPresentationCommitContract.Matches(
                Volatile.Read(ref _committedProviderPresentationId),
                presentationId);

        public bool IsProviderPresentationAuthorizedByUserIntent(
            string presentationId) =>
            ProviderPresentationCommitContract.Matches(
                Volatile.Read(ref _userIntentAuthorizedProviderPresentationId),
                presentationId);

        public bool ArmProviderInputIntent(ProviderInputIntentToken token)
        {
            if (token.ProcessId != _gtaProcessId ||
                Volatile.Read(ref _disposed) != 0 ||
                _pipe?.IsConnected != true)
            {
                return false;
            }
            QueueFrame(new JObject
            {
                ["type"] = "provider_input_intent_arm",
                ["pid"] = token.ProcessId,
                ["epoch"] = token.Epoch,
                ["lifetimeMs"] = token.LifetimeMilliseconds,
            });
            return true;
        }

        public bool BindProviderInputIntent(
            int processId,
            long epoch,
            string presentationId)
        {
            if (processId != _gtaProcessId || epoch <= 0 ||
                !ProviderPresentationCommitContract.IsValidPresentationId(
                    presentationId) ||
                Volatile.Read(ref _disposed) != 0 ||
                _pipe?.IsConnected != true)
            {
                return false;
            }
            QueueFrame(new JObject
            {
                ["type"] = "provider_input_intent_bind",
                ["pid"] = processId,
                ["epoch"] = epoch,
                ["presentationId"] = presentationId,
            });
            return true;
        }

        public void CancelProviderInputIntent(int processId, long epoch)
        {
            if (processId != _gtaProcessId || epoch <= 0 ||
                Volatile.Read(ref _disposed) != 0 ||
                _pipe?.IsConnected != true)
            {
                return;
            }
            QueueFrame(new JObject
            {
                ["type"] = "provider_input_intent_cancel",
                ["pid"] = processId,
                ["epoch"] = epoch,
            });
        }

        public bool BootstrapSurfaceRetirementPending =>
            Volatile.Read(ref _bootstrapSurfaceRetirementPending) == 1;

        public void RetireBootstrapSurface(bool hide)
        {
            // Update the provider-side view immediately. The host applies the
            // same transition in FIFO order before any following typed menu
            // presentation, so CurrentHostSurface can never regress to the
            // initializer after managed ownership begins.
            // A later logical-only replacement must never weaken a hard
            // retirement that is still awaiting the host's hidden state.
            if (hide || !BootstrapSurfaceRetirementPending)
            {
                Interlocked.Exchange(
                    ref _bootstrapSurfaceRetirementRequiresHidden,
                    hide ? 1 : 0);
            }
            Interlocked.Exchange(ref _bootstrapSurfaceRetirementPending, 1);
            Interlocked.Exchange(ref _hostSurfaceMode, HostSurfaceMode.None);
            QueueFrame(new JObject
            {
                ["type"] = "retire_surface",
                ["hide"] = hide,
            });
        }

        public bool TryGetReadyContentGeneration(out int generation)
        {
            generation = Volatile.Read(ref _contentGeneration);
            return generation > 0 && Volatile.Read(ref _contentReady) == 1;
        }

        public RuntimeReadyHandoffState AdvanceRuntimeReadyHandoff(
            int expectedContentGeneration)
        {
            if (expectedContentGeneration <= 0 ||
                Volatile.Read(ref _disposed) != 0 ||
                _pipe?.IsConnected != true)
                return RuntimeReadyHandoffState.Unavailable;

            var currentGeneration = Volatile.Read(ref _contentGeneration);
            if (Volatile.Read(ref _contentReady) != 1 ||
                currentGeneration != expectedContentGeneration)
                return RuntimeReadyHandoffState.StaleGeneration;

            JObject? request = null;
            RuntimeReadyHandoffState state;
            var timedOut = false;
            lock (_runtimeReadySync)
            {
                if (_runtimeReadyGeneration == expectedContentGeneration &&
                    _runtimeReadyState != RuntimeReadyHandoffState.Unavailable)
                {
                    if (_runtimeReadyState == RuntimeReadyHandoffState.Pending &&
                        RuntimeReadyHandoffPolicy.HasLeaseAcknowledgementTimedOut(
                            ElapsedMilliseconds(_runtimeReadyRequestStartedAt)))
                    {
                        _runtimeReadyState =
                            RuntimeReadyHandoffState.SignalUnavailable;
                        timedOut = true;
                    }
                    state = _runtimeReadyState;
                }
                else
                {
                    _runtimeReadyGeneration = expectedContentGeneration;
                    _runtimeReadyRequestId = Guid.NewGuid().ToString("N");
                    _runtimeReadyState = RuntimeReadyHandoffState.Pending;
                    _runtimeReadyRequestStartedAt = Stopwatch.GetTimestamp();
                    state = _runtimeReadyState;
                    request = BootstrapHostHandshake.CreateRuntimeReadyLeaseRequest(
                        expectedContentGeneration,
                        _runtimeReadyRequestId);
                }
            }
            if (timedOut)
            {
                RuntimeTrace.Write(
                    _logDirectory,
                    "bootstrap_runtime_ready_ack_timeout",
                    $"generation={expectedContentGeneration} " +
                    $"timeout_ms={RuntimeReadyHandoffPolicy.LeaseAcknowledgementTimeoutMilliseconds}");
            }
            if (request != null) QueueFrame(request);
            return state;
        }

        private static long ElapsedMilliseconds(long startedAt)
        {
            if (startedAt <= 0) return long.MaxValue;
            var elapsedTicks = Stopwatch.GetTimestamp() - startedAt;
            if (elapsedTicks < 0) return long.MaxValue;
            return elapsedTicks * 1000L / Stopwatch.Frequency;
        }

        public bool Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return _pipe?.IsConnected == true;
            try
            {
                using (var ready = EventWaitHandle.OpenExisting(
                    BootstrapHostNames.ReadyEvent(_gtaProcessId)))
                {
                    if (!ready.WaitOne(ContentRecoveryWaitMilliseconds))
                    {
                        RuntimeTrace.Write(
                            _logDirectory,
                            "bootstrap_host_not_ready",
                            $"pid={_gtaProcessId} timeout_ms={ContentRecoveryWaitMilliseconds}");
                        return false;
                    }
                }

                var pipe = new NamedPipeClientStream(
                    ".",
                    BootstrapHostNames.Pipe(_gtaProcessId),
                    PipeDirection.InOut,
                    // Match the host's overlapped handle so the proxy reader
                    // cannot block its independent writer on the same stream.
                    PipeOptions.Asynchronous);
                pipe.Connect(500);
                _pipe = pipe;
                if (!GetNamedPipeServerProcessId(
                        pipe.SafePipeHandle,
                        out var hostProcessId) ||
                    hostProcessId == 0 ||
                    hostProcessId == (uint)_gtaProcessId)
                {
                    throw new InvalidDataException(
                        "The bootstrap host process identity could not be authenticated.");
                }
                Interlocked.Exchange(ref _hostProcessId, checked((int)hostProcessId));
                // The host authenticates the first frame before publishing
                // Connected. Write the hello synchronously so queued state or
                // input can never win the race to become that first frame.
                BootstrapHostWire.Write(
                    pipe,
                    BootstrapHostHandshake.CreateHello(_gtaProcessId));
                var acknowledgement = BootstrapHostWire.Read(pipe);
                if (!BootstrapHostHandshake.TryValidateReadyAcknowledgement(
                        acknowledgement,
                        out var contentGeneration,
                        out var contentReady,
                        out var acknowledgementFailure))
                    throw new InvalidDataException(
                        "The bootstrap host returned an invalid readiness acknowledgement: " +
                        acknowledgementFailure);
                if (!contentReady)
                    throw new InvalidOperationException(
                        "The bootstrap WebView content generation is not ready.");
                Interlocked.Exchange(ref _contentGeneration, contentGeneration);
                Interlocked.Exchange(ref _contentReady, 1);
                _lastTracedContentGeneration = contentGeneration;
                _lastTracedContentReady = 1;
                _writer = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "REACTOR V bootstrap proxy writer",
                };
                _reader = new Thread(ReaderLoop)
                {
                    IsBackground = true,
                    Name = "REACTOR V bootstrap proxy reader",
                };
                _writer.Start();
                _reader.Start();
                if (_startVisible)
                    SetVisible(true);
                RuntimeTrace.Write(
                    _logDirectory,
                    "bootstrap_host_attached",
                    $"pid={_gtaProcessId} host_pid={hostProcessId} " +
                    $"generation={contentGeneration} " +
                    $"pipe={BootstrapHostNames.Pipe(_gtaProcessId)}");
                return true;
            }
            catch (Exception error) when (
                error is IOException ||
                error is TimeoutException ||
                error is WaitHandleCannotBeOpenedException ||
                error is UnauthorizedAccessException ||
                error is InvalidOperationException ||
                error is InvalidDataException ||
                error is Newtonsoft.Json.JsonException)
            {
                RuntimeTrace.Write(
                    _logDirectory,
                    "bootstrap_host_attach_failed",
                    $"pid={_gtaProcessId} type={error.GetType().Name} message={error.Message}");
                _pipe?.Dispose();
                _pipe = null;
                Interlocked.Exchange(ref _hostProcessId, 0);
                return false;
            }
        }

        public void SetVisible(bool visible)
        {
            SetVisible(visible, HostVisibilityReason.Explicit);
        }

        public void SetVisible(bool visible, HostVisibilityReason reason)
        {
            if (!visible)
            {
                lock (_pointerSync)
                {
                    // The next visible presentation must receive an initial
                    // position even when the physical cursor did not move
                    // while the provider surface was hidden.
                    _hasCursor = false;
                    _pendingPointerMove = null;
                }
            }
            QueueFrame(new JObject
            {
                ["type"] = "visible",
                ["value"] = visible,
                ["reason"] = BootstrapHostVisibility.Serialize(reason),
            });
        }

        public void PumpInput()
        {
        }

        public void UpdateCursor(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            normalizedX = WindowedInputPolicy.Normalize(normalizedX);
            normalizedY = WindowedInputPolicy.Normalize(normalizedY);
            lock (_pointerSync)
            {
                if (!WindowedInputPolicy.ShouldForward(
                        _lastCursorX,
                        _lastCursorY,
                        _hasCursor,
                        normalizedX,
                        normalizedY,
                        pressed,
                        released,
                        wheelDelta))
                {
                    return;
                }

                _hasCursor = true;
                _lastCursorX = normalizedX;
                _lastCursorY = normalizedY;
                var frame = new JObject
                {
                    ["type"] = "pointer",
                    ["x"] = normalizedX,
                    ["y"] = normalizedY,
                    ["pressed"] = pressed,
                    ["released"] = released,
                    ["wheel"] = wheelDelta,
                };
                if (pressed || released || wheelDelta != 0)
                {
                    if (_pendingPointerMove != null)
                    {
                        _pointerEdges.Enqueue(_pendingPointerMove);
                        _pendingPointerMove = null;
                    }
                    _pointerEdges.Enqueue(frame);
                }
                else
                {
                    _pendingPointerMove = frame;
                }
            }
            _outgoingReady.Set();
        }

        public void PostResponse(BridgeResponse response) =>
            PostJson(BridgeProtocol.SerializeResponse(response));

        public void PostEvent(string eventName, JToken? payload) =>
            PostJson(BridgeProtocol.SerializeEvent(eventName, payload));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Closing a full-duplex pipe is the detach signal. Do not perform
            // one last synchronous write here: if the peer is already gone or
            // its read loop is unwinding, that write can indefinitely hold the
            // shared write lock and hang SHVDN AppDomain teardown.
            _stop.Set();
            _outgoingReady.Set();
            try { _pipe?.Dispose(); } catch { }
            var teardown = Stopwatch.StartNew();
            JoinWithinBudget(_reader, teardown);
            JoinWithinBudget(_writer, teardown);
            _pipe = null;
            Interlocked.Exchange(ref _hostProcessId, 0);
            if (_reader?.IsAlive != true && _writer?.IsAlive != true)
            {
                DisposeWorkerHandles();
            }
            else
            {
                // Do not dispose wait handles beneath a worker that outlived
                // the bounded caller-facing teardown. Finish cleanup on a
                // background thread after both workers have actually exited.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _reader?.Join(); } catch { }
                    try { _writer?.Join(); } catch { }
                    DisposeWorkerHandles();
                });
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(
            Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
            out uint serverProcessId);

        private static void JoinWithinBudget(Thread? thread, Stopwatch teardown)
        {
            if (thread?.IsAlive != true) return;
            var remaining = TeardownBudgetMilliseconds - (int)teardown.ElapsedMilliseconds;
            if (remaining > 0) thread.Join(remaining);
        }

        private void DisposeWorkerHandles()
        {
            if (Interlocked.Exchange(ref _workerHandlesDisposed, 1) != 0) return;
            _outgoingReady.Dispose();
            _stop.Dispose();
        }

        private void PostJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > BridgeProtocol.MaximumMessageLength)
                return;
            QueueFrame(new JObject
            {
                ["type"] = "post",
                ["json"] = json,
            });
        }

        private void ReaderLoop()
        {
            try
            {
                RuntimeTrace.Write(_logDirectory, "bootstrap_host_reader_started", $"pid={_gtaProcessId}");
                var pipe = _pipe;
                while (Volatile.Read(ref _disposed) == 0 && pipe?.IsConnected == true)
                {
                    var message = BootstrapHostWire.Read(pipe);
                    if (message == null) break;
                    if (string.Equals(message.Value<string>("type"), "state", StringComparison.Ordinal))
                    {
                        var visible = message.Value<bool>("visible");
                        Interlocked.Exchange(ref _visible, visible ? 1 : 0);
                        var generation = message.Value<int?>("generation") ?? 0;
                        var protocol = message.Value<int?>("protocol") ?? 0;
                        var ready = message.Value<bool?>("ready") == true;
                        if (protocol == BootstrapHostHandshake.ProtocolVersion && generation > 0)
                        {
                            var committedPresentationId =
                                message.Value<string>("providerPresentation");
                            Interlocked.Exchange(
                                ref _committedProviderPresentationId,
                                ready && ProviderPresentationCommitContract.IsValidPresentationId(
                                    committedPresentationId)
                                    ? committedPresentationId
                                    : null);
                            Interlocked.Exchange(
                                ref _userIntentAuthorizedProviderPresentationId,
                                ready && message.Value<bool?>(
                                    "providerPresentationUserIntent") == true &&
                                ProviderPresentationCommitContract.IsValidPresentationId(
                                    committedPresentationId)
                                    ? committedPresentationId
                                    : null);
                            var surface = HostSurfaceMode.Normalize(
                                message.Value<string>("surface"));
                            Interlocked.Exchange(
                                ref _hostSurfaceMode,
                                surface);
                            if (BootstrapSurfaceRetirementPending &&
                                string.Equals(
                                    surface,
                                    HostSurfaceMode.None,
                                    StringComparison.Ordinal) &&
                                (Volatile.Read(
                                    ref _bootstrapSurfaceRetirementRequiresHidden) == 0 ||
                                 !visible))
                            {
                                Interlocked.Exchange(
                                    ref _bootstrapSurfaceRetirementPending,
                                    0);
                            }
                            Interlocked.Exchange(ref _contentGeneration, generation);
                            var readyValue = ready ? 1 : 0;
                            Interlocked.Exchange(ref _contentReady, readyValue);
                            InvalidatePendingRuntimeReadyLease(generation, ready);
                            if (generation != _lastTracedContentGeneration ||
                                readyValue != _lastTracedContentReady)
                            {
                                _lastTracedContentGeneration = generation;
                                _lastTracedContentReady = readyValue;
                                RuntimeTrace.Write(
                                    _logDirectory,
                                    ready ? "bootstrap_host_generation_ready" : "bootstrap_host_generation_not_ready",
                                    $"generation={generation}");
                            }
                        }
                        continue;
                    }
                    if (string.Equals(
                            message.Value<string>("type"),
                            "runtime_ready_lease_ack",
                            StringComparison.Ordinal))
                    {
                        ObserveRuntimeReadyLeaseAcknowledgement(message);
                        continue;
                    }
                    if (!string.Equals(message.Value<string>("type"), "web", StringComparison.Ordinal))
                        continue;
                    var json = message.Value<string>("json");
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    if (_broker.TryEnqueue(json!, out var error)) continue;
                    var id = "invalid";
                    try
                    {
                        var candidate = JObject.Parse(json!).Value<string>("id");
                        if (!string.IsNullOrWhiteSpace(candidate) && candidate!.Length <= 64) id = candidate;
                    }
                    catch { }
                    PostResponse(BridgeResponse.Failure(
                        id,
                        error?.Code ?? "invalid_request",
                        error?.Message ?? "The bridge request was rejected."));
                }
            }
            catch (Exception error) when (
                error is IOException ||
                error is ObjectDisposedException ||
                error is InvalidOperationException ||
                error is InvalidDataException ||
                error is Newtonsoft.Json.JsonException)
            {
                RuntimeTrace.Write(
                    _logDirectory,
                    "bootstrap_host_reader_stopped",
                    $"type={error.GetType().Name} message={error.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _visible, 0);
                Interlocked.Exchange(ref _contentReady, 0);
                Interlocked.Exchange(ref _hostSurfaceMode, HostSurfaceMode.None);
                Interlocked.Exchange(ref _committedProviderPresentationId, null);
                Interlocked.Exchange(
                    ref _userIntentAuthorizedProviderPresentationId,
                    null);
                Interlocked.Exchange(ref _bootstrapSurfaceRetirementPending, 0);
                InvalidatePendingRuntimeReadyLease(
                    Volatile.Read(ref _contentGeneration),
                    ready: false);
                _stop.Set();
                _outgoingReady.Set();
            }
        }

        private void ObserveRuntimeReadyLeaseAcknowledgement(JObject message)
        {
            if (!BootstrapHostHandshake.TryValidateRuntimeReadyLeaseAcknowledgement(
                    message,
                    out var generation,
                    out var requestId,
                    out var validated,
                    out var signaled,
                    out var reason))
            {
                RuntimeTrace.Write(
                    _logDirectory,
                    "bootstrap_runtime_ready_ack_rejected",
                    $"reason={reason}");
                return;
            }

            lock (_runtimeReadySync)
            {
                if (_runtimeReadyState != RuntimeReadyHandoffState.Pending ||
                    generation != _runtimeReadyGeneration ||
                    !string.Equals(
                        requestId,
                        _runtimeReadyRequestId,
                        StringComparison.Ordinal))
                {
                    RuntimeTrace.Write(
                        _logDirectory,
                        "bootstrap_runtime_ready_ack_stale",
                        $"generation={generation} request={requestId}");
                    return;
                }
                _runtimeReadyState = !validated
                    ? RuntimeReadyHandoffState.StaleGeneration
                    : signaled
                        ? RuntimeReadyHandoffState.Signaled
                        : RuntimeReadyHandoffState.SignalUnavailable;
            }
            RuntimeTrace.Write(
                _logDirectory,
                signaled
                    ? "bootstrap_runtime_ready_acknowledged"
                    : "bootstrap_runtime_ready_not_signaled",
                $"generation={generation} validated={validated} " +
                $"signaled={signaled} request={requestId}");
        }

        private void InvalidatePendingRuntimeReadyLease(
            int currentGeneration,
            bool ready)
        {
            lock (_runtimeReadySync)
            {
                if (_runtimeReadyState == RuntimeReadyHandoffState.Pending &&
                    (!ready || currentGeneration != _runtimeReadyGeneration))
                    _runtimeReadyState = RuntimeReadyHandoffState.StaleGeneration;
            }
        }

        private void WriterLoop()
        {
            try
            {
                RuntimeTrace.Write(_logDirectory, "bootstrap_host_writer_started", $"pid={_gtaProcessId}");
                var pipe = _pipe;
                while (Volatile.Read(ref _disposed) == 0 && pipe?.IsConnected == true)
                {
                    while (_outgoing.TryDequeue(out var frame))
                    {
                        Interlocked.Decrement(ref _queuedFrames);
                        WriteFrame(pipe, frame);
                    }
                    JObject[] pointerFrames;
                    lock (_pointerSync)
                    {
                        if (_pendingPointerMove != null)
                        {
                            _pointerEdges.Enqueue(_pendingPointerMove);
                            _pendingPointerMove = null;
                        }
                        pointerFrames = _pointerEdges.ToArray();
                        while (_pointerEdges.TryDequeue(out _)) { }
                    }
                    foreach (var frame in pointerFrames) WriteFrame(pipe, frame);
                    if (_stop.WaitOne(0)) return;
                    _outgoingReady.WaitOne(16);
                }
            }
            catch (Exception error) when (
                error is IOException ||
                error is ObjectDisposedException ||
                error is InvalidOperationException ||
                error is InvalidDataException)
            {
                RuntimeTrace.Write(
                    _logDirectory,
                    "bootstrap_host_writer_stopped",
                    $"type={error.GetType().Name} message={error.Message}");
            }
        }

        private void QueueFrame(JObject frame)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var count = Interlocked.Increment(ref _queuedFrames);
            _outgoing.Enqueue(frame);
            while (count > MaximumQueuedFrames && _outgoing.TryDequeue(out _))
            {
                count = Interlocked.Decrement(ref _queuedFrames);
                RuntimeTrace.Write(_logDirectory, "bootstrap_proxy_frame_dropped", $"maximum={MaximumQueuedFrames}");
            }
            _outgoingReady.Set();
        }

        private void WriteFrame(Stream pipe, JObject frame)
        {
            lock (_writeSync)
                BootstrapHostWire.Write(pipe, frame);
        }
    }
}
