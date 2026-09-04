using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Core.Protocol;
using ReactorV.BootstrapHost;

namespace ReactorV.Preloader
{
    /// <summary>
    /// Owns the process-scoped IPC endpoint used by the late SHVDN provider.
    /// Browser callbacks only enqueue frames; all pipe I/O stays off the WebView
    /// STA and the game script thread.
    /// </summary>
    internal sealed class BootstrapOverlayServer : IBridgeMessageSink, IDisposable
    {
        private const int MaximumQueuedFrames = 512;

        private readonly int _gtaProcessId;
        private readonly Action<string, string?> _trace;
        private readonly ConcurrentQueue<JObject> _outgoing = new ConcurrentQueue<JObject>();
        private readonly AutoResetEvent _outgoingReady = new AutoResetEvent(false);
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly object _sessionSync = new object();
        private readonly EventWaitHandle _ready;
        private readonly EventWaitHandle _connected;
        private readonly BootstrapHostReadinessGeneration _contentReadiness =
            new BootstrapHostReadinessGeneration();
        private readonly DualBrowserPresentationReadinessCoordinator<string>
            _presentationReadiness =
                new DualBrowserPresentationReadinessCoordinator<string>();
        private readonly ExternalGpuPostAcceptPaintGate
            _externalGpuPostAcceptPaintGate =
                new ExternalGpuPostAcceptPaintGate();
        private readonly Dictionary<string, string>
            _presentationReadyResponseAliases =
                new Dictionary<string, string>(StringComparer.Ordinal);
        private Thread? _serverThread;
        private NamedPipeServerStream? _activePipe;
        private int _queuedFrames;
        private int _visible;
        private int _runtimeReadyLeaseSignaled;
        private int _defaultMenuRequested;
        private string? _defaultMenuDeadlineUtc;
        private int _disposed;
        private string _surfaceMode = HostSurfaceMode.None;
        private string? _providerPresentationId;
        private bool _providerPresentationUserIntent;
        private bool _providerSessionConnected;
        private int _providerSessionGeneration;
        private bool _externalGpuBrowserShadowRequired;
        private JObject? _bootstrapExtensionCatalog;

        public BootstrapOverlayServer(
            int gtaProcessId,
            Action<string, string?> trace,
            bool externalGpuBrowserShadowRequired = false)
        {
            if (gtaProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(gtaProcessId));
            _gtaProcessId = gtaProcessId;
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
            _externalGpuBrowserShadowRequired =
                externalGpuBrowserShadowRequired;
            _ready = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                BootstrapHostNames.ReadyEvent(gtaProcessId));
            _connected = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                BootstrapHostNames.ConnectedEvent(gtaProcessId));
        }

        public bool IsConnected => _connected.WaitOne(0);

        public bool IsVisible => Volatile.Read(ref _visible) == 1;

        public bool RuntimeReadyLeaseSignaled =>
            Volatile.Read(ref _runtimeReadyLeaseSignaled) == 1;

        internal bool IsExternalGpuBrowserShadowRequired
        {
            get
            {
                lock (_sessionSync)
                    return _externalGpuBrowserShadowRequired;
            }
        }

        public string CurrentSurfaceMode => Volatile.Read(ref _surfaceMode);

        internal IBridgeMessageSink CreateBrowserSink(
            PresentationReadyBrowserRole role) =>
                new BrowserBridgeSink(this, role);

        /// <summary>
        /// Disables the second-browser barrier after optional CEF discovery or
        /// runtime delivery fails. A WebView acknowledgement already waiting
        /// at the barrier is forwarded immediately through the same provider
        /// session rather than timing out.
        /// </summary>
        internal void DisableExternalGpuBrowserShadow(string reason)
        {
            PresentationReadyDispatch<string>? dispatch = null;
            lock (_sessionSync)
            {
                _externalGpuBrowserShadowRequired = false;
                if (_providerSessionConnected)
                {
                    _presentationReadiness.DisableExternalGpuShadow(
                        _providerSessionGeneration,
                        out dispatch);
                    _externalGpuPostAcceptPaintGate.ResetSession(
                        _providerSessionGeneration);
                    if (dispatch != null) QueuePresentationReadyDispatchLocked(dispatch);
                }
            }
            _trace(
                "bootstrap_dual_browser_barrier_disabled",
                $"reason={reason} pending_released={dispatch != null}");
            if (dispatch != null)
            {
                DualBrowserPresentationReady?.Invoke(
                    dispatch.PresentationId,
                    dispatch.ProviderSessionGeneration);
            }
        }

        public void Start()
        {
            if (_serverThread != null) return;
            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "REACTOR V bootstrap host pipe",
            };
            _serverThread.Start();
        }

        public void MarkContentReady()
        {
            int generation;
            lock (_sessionSync)
            {
                generation = _contentReadiness.MarkReady();
                _ready.Set();
                QueueStateLocked();
            }
            _trace(
                "bootstrap_host_ready",
                $"pid={_gtaProcessId} generation={generation} " +
                $"pipe={BootstrapHostNames.Pipe(_gtaProcessId)}");
            PublishStartupStatus();
        }

        public void MarkContentUnavailable()
        {
            int generation;
            lock (_sessionSync)
            {
                generation = _contentReadiness.MarkUnavailable();
                _providerPresentationId = null;
                _providerPresentationUserIntent = false;
                _ready.Reset();
                QueueStateLocked();
            }
            _trace(
                "bootstrap_host_not_ready",
                $"pid={_gtaProcessId} generation={generation}");
            PublishStartupStatus();
        }

        public void MarkPresentationUnavailable(string reason)
        {
            int generation;
            bool contentReady;
            lock (_sessionSync)
            {
                // Presentation health is intentionally independent of the
                // loaded browser document and process-scoped pipe. Retire any
                // committed provider identity, but preserve the Ready event
                // and its generation so a late provider can still attach.
                _providerPresentationId = null;
                _providerPresentationUserIntent = false;
                generation = _contentReadiness.CurrentGeneration;
                contentReady = _contentReadiness.IsReady;
                QueueStateLocked();
            }
            _trace(
                "bootstrap_host_presentation_not_ready",
                $"pid={_gtaProcessId} generation={generation} " +
                $"content_ready={contentReady} pipe_attachable={contentReady} " +
                $"reason={reason}");
        }

        public void PublishVisibility(bool visible)
        {
            Interlocked.Exchange(ref _visible, visible ? 1 : 0);
            lock (_sessionSync)
            {
                if (!_providerSessionConnected) return;
                QueueStateLocked();
            }
        }

        public void PublishSurfaceMode(string? mode)
        {
            Interlocked.Exchange(
                ref _surfaceMode,
                HostSurfaceMode.Normalize(mode));
            lock (_sessionSync)
            {
                if (!_providerSessionConnected) return;
                QueueStateLocked();
            }
        }

        public void PublishProviderPresentationCommitted(
            string presentationId,
            bool userIntentAuthorized = false)
        {
            if (!ProviderPresentationCommitContract.IsValidPresentationId(
                    presentationId))
            {
                lock (_sessionSync)
                {
                    _providerPresentationId = null;
                    _providerPresentationUserIntent = false;
                    QueueStateLocked();
                }
                _trace(
                    "bootstrap_provider_presentation_commit_rejected",
                    "reason=invalid_presentation_id");
                return;
            }

            lock (_sessionSync)
            {
                if (!_providerSessionConnected ||
                    Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                _providerPresentationId = presentationId;
                _providerPresentationUserIntent = userIntentAuthorized;
                QueueStateLocked();
            }
            _trace(
                "bootstrap_provider_presentation_committed",
                $"presentation={presentationId} " +
                $"user_intent_authorized={userIntentAuthorized}");
        }

        public void PublishDefaultMenuIntentState(
            bool requested,
            DateTime? deadlineUtc)
        {
            Volatile.Write(
                ref _defaultMenuDeadlineUtc,
                requested && deadlineUtc.HasValue
                    ? deadlineUtc.Value.ToUniversalTime().ToString("O")
                    : null);
            Volatile.Write(ref _defaultMenuRequested, requested ? 1 : 0);
            PublishStartupStatus();
        }

        /// <summary>
        /// Publishes only the compact package identities recovered from the
        /// validated preload snapshots. The catalog remains read-only and is
        /// used only before the managed provider connects; the live registry
        /// becomes authoritative immediately after that boundary.
        /// </summary>
        public void PublishBootstrapExtensionCatalog(PreloadDataBuildResult result)
        {
            var loaded = BootstrapExtensionCatalogContract.TryBuildFromSnapshots(
                result,
                out var catalog,
                out var outcome);
            bool providerConnected;
            lock (_sessionSync)
            {
                _bootstrapExtensionCatalog = loaded && catalog != null
                    ? (JObject)catalog.DeepClone()
                    : null;
                providerConnected = _providerSessionConnected;
            }
            _trace(
                loaded
                    ? "bootstrap_extension_catalog_ready"
                    : "bootstrap_extension_catalog_unavailable",
                loaded
                    ? $"total={catalog!.Value<int>("total")} source=preload"
                    : $"outcome={outcome}");
            if (loaded && !providerConnected)
                JsonRequested?.Invoke(
                    BootstrapExtensionCatalogContract.SerializeAvailableEvent(catalog!));
        }

        public bool TryEnqueue(string json, out BridgeError? error) =>
            TryEnqueue(
                PresentationReadyBrowserRole.WebViewAuthority,
                json,
                out error);

        private bool TryEnqueue(
            PresentationReadyBrowserRole browserRole,
            string json,
            out BridgeError? error)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > BridgeProtocol.MaximumMessageLength)
            {
                error = new BridgeError(
                    "invalid_request",
                    "The browser request exceeded the Reactor bridge limit.");
                return false;
            }

            try
            {
                var local = JObject.Parse(json);
                if (string.Equals(local.Value<string>("kind"), "host", StringComparison.Ordinal) &&
                    string.Equals(local.Value<string>("command"), "close", StringComparison.Ordinal))
                {
                    if (browserRole != PresentationReadyBrowserRole.WebViewAuthority)
                    {
                        error = new BridgeError(
                            "browser_role_denied",
                            "Only the WebView host may control bootstrap visibility.");
                        return false;
                    }
                    VisibilityRequested?.Invoke(
                        false,
                        HostVisibilityReason.Explicit);
                    error = null;
                    return true;
                }
                if (string.Equals(local.Value<string>("kind"), "host", StringComparison.Ordinal) &&
                    string.Equals(local.Value<string>("command"), "surface-ready", StringComparison.Ordinal))
                {
                    var mode = HostSurfaceMode.Normalize(local.Value<string>("mode"));
                    var generation = local.Value<int?>("generation") ?? 0;
                    if ((string.Equals(mode, "about", StringComparison.Ordinal) ||
                         string.Equals(mode, HostSurfaceMode.Verifying, StringComparison.Ordinal) ||
                         string.Equals(mode, HostSurfaceMode.SetupStatus, StringComparison.Ordinal) ||
                         string.Equals(mode, "initializing", StringComparison.Ordinal)) &&
                        generation > 0)
                    {
                        if (browserRole == PresentationReadyBrowserRole.WebViewAuthority)
                        {
                            SurfaceReady?.Invoke(mode, generation);
                        }
                        else if (browserRole ==
                                     PresentationReadyBrowserRole.ExternalGpuShadow &&
                                 HostSurfaceMode.RequiresPaintProof(mode))
                        {
                            ExternalSurfaceReady?.Invoke(mode, generation);
                        }
                        else
                        {
                            error = new BridgeError(
                                "browser_role_denied",
                                "The external GPU browser may acknowledge only known bootstrap surfaces; visibility remains host-owned.");
                            return false;
                        }
                        error = null;
                        return true;
                    }
                    error = new BridgeError(
                        "invalid_host_surface_ready",
                        "The host surface acknowledgement is invalid.");
                    return false;
                }
                if (string.Equals(local.Value<string>("kind"), "host", StringComparison.Ordinal) &&
                    string.Equals(local.Value<string>("command"), "provider-surface-painted", StringComparison.Ordinal))
                {
                    return TryAcceptExternalGpuPostAcceptPaint(
                        browserRole,
                        local,
                        out error);
                }
            }
            catch
            {
                // The managed BridgeBroker remains the authoritative parser
                // for all non-host messages and will return a typed error.
            }

            if (BridgeProtocol.TryParseRequest(
                    json,
                    out var parsedRequest,
                    out _) &&
                parsedRequest != null &&
                string.Equals(
                    parsedRequest.Method,
                    "overlay.presentationReady",
                    StringComparison.Ordinal))
            {
                return TryEnqueuePresentationReady(
                    browserRole,
                    parsedRequest,
                    json,
                    out error);
            }

            bool providerConnected;
            lock (_sessionSync) providerConnected = _providerSessionConnected;
            if (!providerConnected)
            {
                var snapshot = CreateStartupSnapshot(providerConnected: false);
                if (StartupStatusContract.TryCreateLocalResponse(json, snapshot, out var responseJson))
                {
                    if (!string.IsNullOrWhiteSpace(responseJson)) JsonRequested?.Invoke(responseJson!);
                    error = null;
                    return true;
                }
                JObject? catalog;
                lock (_sessionSync)
                    catalog = _bootstrapExtensionCatalog == null
                        ? null
                        : (JObject)_bootstrapExtensionCatalog.DeepClone();
                if (BootstrapExtensionCatalogContract.TryCreateLocalResponse(
                        json,
                        catalog,
                        out responseJson))
                {
                    if (!string.IsNullOrWhiteSpace(responseJson)) JsonRequested?.Invoke(responseJson!);
                    error = null;
                    return true;
                }
            }

            lock (_sessionSync)
            {
                if (!_providerSessionConnected)
                {
                    error = new BridgeError(
                        "provider_unavailable",
                        "The GTA provider is not connected yet.");
                    return false;
                }
                QueueFrame(new JObject
                {
                    ["type"] = "web",
                    ["json"] = json,
                });
            }
            error = null;
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_sessionSync)
            {
                _providerSessionConnected = false;
                _providerPresentationId = null;
                _providerPresentationUserIntent = false;
                if (_providerSessionGeneration > 0)
                {
                    _presentationReadiness.ResetSession(
                        _providerSessionGeneration);
                    _externalGpuPostAcceptPaintGate.ResetSession(
                        _providerSessionGeneration);
                }
                _presentationReadyResponseAliases.Clear();
            }
            _stop.Set();
            _outgoingReady.Set();
            try { _activePipe?.Dispose(); } catch { }
            if (_serverThread?.IsAlive == true) _serverThread.Join(1500);
            _connected.Reset();
            _ready.Reset();
            _connected.Dispose();
            _ready.Dispose();
            _outgoingReady.Dispose();
            _stop.Dispose();
        }

        public event Action<bool, HostVisibilityReason>? VisibilityRequested;
        public event Action<bool>? BootstrapSurfaceRetirementRequested;
        public event Action? RuntimeReadyLeaseSignaledChanged;
        public event Action<string, int>? SurfaceReady;
        public event Action<string, int>? ExternalSurfaceReady;
        public event Action<string>? JsonRequested;
        public event Action<float, float, bool, bool, int>? PointerRequested;
        public event Action? ProviderConnected;
        public event Action? ProviderDisconnected;
        public event Action<ProviderInputIntentToken>? ProviderInputIntentArmRequested;
        public event Action<int, long, string>? ProviderInputIntentBindRequested;
        public event Action<int, long>? ProviderInputIntentCancelRequested;
        public event Action<string, int>? DualBrowserPresentationReady;
        public event Action<string, int>? ExternalGpuPostAcceptPaintReady;

        private void ServerLoop()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(
                        BootstrapHostNames.Pipe(_gtaProcessId),
                        PipeDirection.InOut,
                         1,
                         PipeTransmissionMode.Byte,
                         // The host has one dedicated reader and one dedicated
                         // writer. Overlapped handles are required for those
                         // full-duplex operations to make progress concurrently
                         // on Windows rather than serializing on the pipe handle.
                         PipeOptions.Asynchronous,
                         BootstrapHostWire.MaximumFrameBytes,
                        BootstrapHostWire.MaximumFrameBytes))
                    {
                        _activePipe = pipe;
                        _trace("bootstrap_host_waiting", $"pid={_gtaProcessId}");
                        pipe.WaitForConnection();
                        if (Volatile.Read(ref _disposed) != 0) return;
                        if (!TryGetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) ||
                            clientPid != (uint)_gtaProcessId)
                        {
                            _trace(
                                "bootstrap_host_client_rejected",
                                $"expected_pid={_gtaProcessId} actual_pid={clientPid}");
                            pipe.Disconnect();
                            continue;
                        }

                        _trace("bootstrap_host_client_connected", $"pid={clientPid}");
                        // Complete a directional handshake before starting
                        // simultaneous pipe I/O. Writing the initial state at
                        // the same instant the client begins its synchronous
                        // hello write can deadlock two otherwise valid local
                        // full-duplex streams on some Windows pipe stacks.
                        var hello = BootstrapHostWire.Read(pipe);
                        if (!BootstrapHostHandshake.TryValidateHello(
                            hello,
                            _gtaProcessId,
                            out var handshakeFailure))
                        {
                            _trace(
                                "bootstrap_host_handshake_rejected",
                                $"pid={clientPid} reason={handshakeFailure}");
                            pipe.Disconnect();
                            continue;
                        }

                        int handshakeGeneration;
                        bool handshakeReady;
                        lock (_sessionSync)
                        {
                            var acknowledgement = _contentReadiness.CreateAcknowledgement();
                            BootstrapHostWire.Write(pipe, acknowledgement);
                            BootstrapHostHandshake.TryValidateReadyAcknowledgement(
                                acknowledgement,
                                out handshakeGeneration,
                                out handshakeReady,
                                out _);
                        }
                        if (!handshakeReady)
                        {
                            _trace(
                                "bootstrap_host_handshake_not_ready",
                                $"pid={clientPid} generation={handshakeGeneration}");
                            pipe.Disconnect();
                            continue;
                        }

                        lock (_sessionSync)
                        {
                            if (!_contentReadiness.IsCurrentReady(handshakeGeneration))
                            {
                                _trace(
                                    "bootstrap_host_handshake_stale",
                                    $"pid={clientPid} generation={handshakeGeneration} " +
                                    $"current_generation={_contentReadiness.CurrentGeneration}");
                                pipe.Disconnect();
                                continue;
                            }
                            PurgeOutgoingFrames("session_start");
                            _providerPresentationId = null;
                            _providerPresentationUserIntent = false;
                            _providerSessionGeneration++;
                            _presentationReadyResponseAliases.Clear();
                            _presentationReadiness.BeginSession(
                                _providerSessionGeneration,
                                _externalGpuBrowserShadowRequired);
                            _externalGpuPostAcceptPaintGate.BeginSession(
                                _providerSessionGeneration);
                            _providerSessionConnected = true;
                            _connected.Set();
                        }
                        _trace("bootstrap_host_provider_ready", $"pid={_gtaProcessId}");
                        PublishStartupStatus();
                        Thread? writer = null;
                        try
                        {
                            ProviderConnected?.Invoke();
                            writer = new Thread(() => WriterLoop(pipe))
                            {
                                IsBackground = true,
                                Name = "REACTOR V bootstrap host writer",
                            };
                            writer.Start();
                            lock (_sessionSync)
                            {
                                QueueStateLocked();
                            }
                            ReadLoop(pipe);
                        }
                        finally
                        {
                            lock (_sessionSync)
                            {
                                _providerSessionConnected = false;
                                _providerPresentationId = null;
                                _providerPresentationUserIntent = false;
                                _presentationReadiness.ResetSession(
                                    _providerSessionGeneration);
                                _externalGpuPostAcceptPaintGate.ResetSession(
                                    _providerSessionGeneration);
                                _presentationReadyResponseAliases.Clear();
                                _connected.Reset();
                            }
                            // Invalidate the writer's session before closing the
                            // stream so a clean detach cannot spend one second
                            // waiting for its polling loop to notice teardown.
                            _activePipe = null;
                            try { pipe.Disconnect(); } catch { }
                            _outgoingReady.Set();
                            if (writer?.IsAlive == true) writer.Join(1000);
                            lock (_sessionSync) PurgeOutgoingFrames("session_end");
                            _trace("bootstrap_host_client_disconnected", $"pid={clientPid}");
                            PublishStartupStatus();
                            ProviderDisconnected?.Invoke();
                        }
                    }
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                catch (IOException error)
                {
                    if (Volatile.Read(ref _disposed) == 0)
                        _trace("bootstrap_host_pipe_failed", $"type={error.GetType().Name} message={error.Message}");
                }
                catch (Exception error)
                {
                    if (Volatile.Read(ref _disposed) == 0)
                        _trace("bootstrap_host_server_failed", $"type={error.GetType().Name} message={error.Message}");
                }
                finally
                {
                    _activePipe = null;
                }
                if (_stop.WaitOne(100)) return;
            }
        }

        private void QueueStateLocked()
        {
            if (!_providerSessionConnected) return;
            QueueFrame(new JObject
            {
                ["type"] = "state",
                ["visible"] = Volatile.Read(ref _visible) == 1,
                ["ready"] = _contentReadiness.IsReady,
                ["generation"] = _contentReadiness.CurrentGeneration,
                ["protocol"] = BootstrapHostHandshake.ProtocolVersion,
                ["surface"] = Volatile.Read(ref _surfaceMode),
                ["providerPresentation"] = _providerPresentationId == null
                    ? JValue.CreateNull()
                    : new JValue(_providerPresentationId),
                ["providerPresentationUserIntent"] =
                    _providerPresentationUserIntent,
            });
        }

        private JObject CreateStartupSnapshot(bool providerConnected) =>
            StartupStatusContract.CreateRuntimeSnapshot(
                reactorReady: _contentReadiness.IsReady,
                nativeBridgeReady: true,
                providerConnected: providerConnected,
                defaultMenuRequested:
                    Volatile.Read(ref _defaultMenuRequested) == 1,
                defaultMenuDeadlineUtc: DateTime.TryParse(
                    Volatile.Read(ref _defaultMenuDeadlineUtc),
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var deadlineUtc)
                        ? deadlineUtc
                        : (DateTime?)null);

        internal void PublishStartupStatus()
        {
            bool providerConnected;
            lock (_sessionSync) providerConnected = _providerSessionConnected;
            if (!StartupStatusContract.IsBootstrapEventAuthority(providerConnected))
                return;
            var json = StartupStatusContract.SerializeEvent(
                CreateStartupSnapshot(providerConnected: false));
            JsonRequested?.Invoke(json);
        }

        private void ReadLoop(Stream pipe)
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                var message = BootstrapHostWire.Read(pipe);
                if (message == null) return;
                switch (message.Value<string>("type"))
                {
                    case "hello":
                        _trace("bootstrap_host_duplicate_hello", $"pid={_gtaProcessId}");
                        break;
                    case "visible":
                        if (message["value"]?.Type != JTokenType.Boolean ||
                            message["reason"]?.Type != JTokenType.String ||
                            !BootstrapHostVisibility.TryParse(
                                message.Value<string>("reason"),
                                out var visibilityReason))
                        {
                            _trace(
                                "bootstrap_visibility_rejected",
                                "reason=invalid_visibility_envelope");
                            break;
                        }
                        VisibilityRequested?.Invoke(
                            message.Value<bool>("value"),
                            visibilityReason);
                        break;
                    case "retire_surface":
                        if (message["hide"]?.Type == JTokenType.Boolean)
                        {
                            BootstrapSurfaceRetirementRequested?.Invoke(
                                message.Value<bool>("hide"));
                        }
                        else
                        {
                            _trace(
                                "bootstrap_surface_retire_rejected",
                                "reason=invalid_hide_value");
                        }
                        break;
                    case "provider_input_intent_arm":
                        if (message["pid"]?.Type == JTokenType.Integer &&
                            message["epoch"]?.Type == JTokenType.Integer &&
                            message["lifetimeMs"]?.Type == JTokenType.Integer &&
                            message.Value<int>("pid") == _gtaProcessId)
                        {
                            try
                            {
                                ProviderInputIntentArmRequested?.Invoke(
                                    new ProviderInputIntentToken(
                                        message.Value<int>("pid"),
                                        message.Value<long>("epoch"),
                                        message.Value<int>("lifetimeMs")));
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                _trace(
                                    "bootstrap_provider_input_intent_rejected",
                                    "reason=invalid-arm-envelope");
                            }
                        }
                        else
                        {
                            _trace(
                                "bootstrap_provider_input_intent_rejected",
                                "reason=invalid-arm-envelope");
                        }
                        break;
                    case "provider_input_intent_bind":
                        var bindPresentationId =
                            message.Value<string>("presentationId");
                        if (message["pid"]?.Type == JTokenType.Integer &&
                            message["epoch"]?.Type == JTokenType.Integer &&
                            message.Value<int>("pid") == _gtaProcessId &&
                            message.Value<long>("epoch") > 0 &&
                            ProviderPresentationCommitContract.IsValidPresentationId(
                                bindPresentationId))
                        {
                            ProviderInputIntentBindRequested?.Invoke(
                                _gtaProcessId,
                                message.Value<long>("epoch"),
                                bindPresentationId!);
                        }
                        else
                        {
                            _trace(
                                "bootstrap_provider_input_intent_rejected",
                                "reason=invalid-bind-envelope");
                        }
                        break;
                    case "provider_input_intent_cancel":
                        if (message["pid"]?.Type == JTokenType.Integer &&
                            message["epoch"]?.Type == JTokenType.Integer &&
                            message.Value<int>("pid") == _gtaProcessId &&
                            message.Value<long>("epoch") > 0)
                        {
                            ProviderInputIntentCancelRequested?.Invoke(
                                _gtaProcessId,
                                message.Value<long>("epoch"));
                        }
                        else
                        {
                            _trace(
                                "bootstrap_provider_input_intent_rejected",
                                "reason=invalid-cancel-envelope");
                        }
                        break;
                    case "post":
                        var json = message.Value<string>("json");
                        if (!string.IsNullOrWhiteSpace(json) && json!.Length <= BridgeProtocol.MaximumMessageLength)
                            PublishProviderJson(json);
                        break;
                    case "pointer":
                        PointerRequested?.Invoke(
                            (float)(message.Value<double?>("x") ?? 0d),
                            (float)(message.Value<double?>("y") ?? 0d),
                            message.Value<bool>("pressed"),
                            message.Value<bool>("released"),
                            message.Value<int>("wheel"));
                        break;
                    case "runtime_ready_lease":
                        HandleRuntimeReadyLease(message);
                        break;
                    case "detach":
                        return;
                }
            }
        }

        private void HandleRuntimeReadyLease(JObject message)
        {
            if (!BootstrapHostHandshake.TryValidateRuntimeReadyLeaseRequest(
                    message,
                    out var generation,
                    out var requestId,
                    out var reason))
            {
                _trace(
                    "bootstrap_host_runtime_ready_lease_rejected",
                    $"reason={reason}");
                return;
            }

            bool validated;
            bool signaled;
            lock (_sessionSync)
            {
                // Validation and the process-scoped RuntimeReady signal share
                // the same critical section as MarkContentUnavailable. A
                // browser failure therefore wins before this lease or after
                // the signal, never between the authoritative generation
                // check and the ownership handoff.
                signaled = _contentReadiness.TrySignalCurrentReady(
                    generation,
                    () => PreloadHandoff.TrySignalRuntimeReady(_gtaProcessId),
                    out validated);
                QueueFrame(
                    BootstrapHostHandshake.CreateRuntimeReadyLeaseAcknowledgement(
                        generation,
                        requestId,
                        validated,
                        signaled));
            }
            if (signaled &&
                Interlocked.Exchange(ref _runtimeReadyLeaseSignaled, 1) == 0)
            {
                RuntimeReadyLeaseSignaledChanged?.Invoke();
            }
            _trace(
                signaled
                    ? "bootstrap_host_runtime_ready_signaled"
                    : "bootstrap_host_runtime_ready_rejected",
                $"generation={generation} validated={validated} " +
                $"signaled={signaled} request={requestId}");
        }

        private void WriterLoop(Stream pipe)
        {
            try
            {
                while (Volatile.Read(ref _disposed) == 0 && _activePipe == pipe)
                {
                    while (_activePipe == pipe && _outgoing.TryDequeue(out var message))
                    {
                        Interlocked.Decrement(ref _queuedFrames);
                        BootstrapHostWire.Write(pipe, message);
                    }
                    if (_stop.WaitOne(0)) return;
                    _outgoingReady.WaitOne(100);
                }
            }
            catch (Exception error) when (
                error is IOException ||
                error is ObjectDisposedException ||
                error is InvalidOperationException ||
                error is InvalidDataException)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _trace(
                        "bootstrap_host_writer_stopped",
                        $"type={error.GetType().Name} message={error.Message}");
                }
            }
        }

        private void QueueFrame(JObject message)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var count = Interlocked.Increment(ref _queuedFrames);
            _outgoing.Enqueue(message);
            while (count > MaximumQueuedFrames && _outgoing.TryDequeue(out _))
            {
                count = Interlocked.Decrement(ref _queuedFrames);
                _trace("bootstrap_host_frame_dropped", $"maximum={MaximumQueuedFrames}");
            }
            _outgoingReady.Set();
        }

        private bool TryEnqueuePresentationReady(
            PresentationReadyBrowserRole browserRole,
            BridgeRequest request,
            string json,
            out BridgeError? error)
        {
            var presentationId =
                request.Parameters.Value<string>("presentationId");
            if (!ProviderPresentationCommitContract.IsValidPresentationId(
                    presentationId))
            {
                error = new BridgeError(
                    "invalid_parameters",
                    "presentationId is invalid.");
                return false;
            }

            PresentationReadyDispatch<string>? completedDispatch = null;
            lock (_sessionSync)
            {
                if (!_providerSessionConnected)
                {
                    error = new BridgeError(
                        "provider_unavailable",
                        "The GTA provider is not connected yet.");
                    return false;
                }

                var status = _presentationReadiness.Submit(
                    _providerSessionGeneration,
                    browserRole,
                    presentationId!,
                    request.Id,
                    json,
                    out var dispatch);
                if (status == PresentationReadySubmissionStatus.Ignored)
                {
                    error = new BridgeError(
                        "stale_presentation",
                        "The presentation-ready acknowledgement is stale or duplicated.");
                    return false;
                }

                if (dispatch != null)
                {
                    QueuePresentationReadyDispatchLocked(dispatch);
                    if (dispatch.ResponseAlias?.AliasRole ==
                            PresentationReadyBrowserRole.ExternalGpuShadow)
                    {
                        _externalGpuPostAcceptPaintGate.RecordDualBrowserReady(
                            dispatch.ProviderSessionGeneration,
                            dispatch.PresentationId);
                    }
                    completedDispatch = dispatch;
                }
            }

            _trace(
                "bootstrap_browser_presentation_ready",
                $"role={browserRole} presentation={presentationId} " +
                $"request={request.Id}");
            if (completedDispatch != null)
            {
                DualBrowserPresentationReady?.Invoke(
                    completedDispatch.PresentationId,
                    completedDispatch.ProviderSessionGeneration);
            }
            error = null;
            return true;
        }

        private bool TryAcceptExternalGpuPostAcceptPaint(
            PresentationReadyBrowserRole browserRole,
            JObject message,
            out BridgeError? error)
        {
            if (browserRole != PresentationReadyBrowserRole.ExternalGpuShadow)
            {
                error = new BridgeError(
                    "browser_role_denied",
                    "Only the external GPU browser may publish provider pixels.");
                return false;
            }

            var presentationId = message.Value<string>("presentationId");
            var providerSessionGeneration =
                message.Value<int?>("providerSessionGeneration") ?? 0;
            if (!ProviderPresentationCommitContract.IsValidPresentationId(
                    presentationId) ||
                providerSessionGeneration <= 0)
            {
                error = new BridgeError(
                    "invalid_host_paint_ready",
                    "The provider paint acknowledgement is invalid.");
                return false;
            }

            lock (_sessionSync)
            {
                if (!_externalGpuBrowserShadowRequired ||
                    !_providerSessionConnected ||
                    providerSessionGeneration != _providerSessionGeneration ||
                    !_externalGpuPostAcceptPaintGate.TryAcceptPostAcceptPaint(
                        providerSessionGeneration,
                        presentationId!))
                {
                    error = new BridgeError(
                        "stale_presentation",
                        "The provider paint acknowledgement is stale or duplicated.");
                    return false;
                }
            }

            _trace(
                "bootstrap_external_gpu_post_accept_paint_ready",
                $"session={providerSessionGeneration} " +
                $"presentation={presentationId}");
            ExternalGpuPostAcceptPaintReady?.Invoke(
                presentationId!,
                providerSessionGeneration);
            error = null;
            return true;
        }

        private void QueuePresentationReadyDispatchLocked(
            PresentationReadyDispatch<string> dispatch)
        {
            if (dispatch.ResponseAlias.HasValue)
            {
                if (_presentationReadyResponseAliases.Count >= 64)
                {
                    _presentationReadyResponseAliases.Clear();
                    _trace(
                        "bootstrap_presentation_aliases_reset",
                        "reason=capacity maximum=64");
                }
                _presentationReadyResponseAliases[
                    dispatch.AuthoritativeRequestId] =
                        dispatch.ResponseAlias.Value.AliasRequestId;
            }
            QueueFrame(new JObject
            {
                ["type"] = "web",
                ["json"] = dispatch.AuthoritativePayload,
            });
            _trace(
                "bootstrap_presentation_ready_dispatched",
                $"session={dispatch.ProviderSessionGeneration} " +
                $"presentation={dispatch.PresentationId} " +
                $"authority={dispatch.AuthoritativeRequestId} " +
                $"alias={dispatch.ResponseAlias?.AliasRequestId ?? "none"}");
        }

        private void PublishProviderJson(string json)
        {
            string? aliasJson = null;
            try
            {
                var message = JObject.Parse(json);
                if (string.Equals(
                        message.Value<string>("kind"),
                        "event",
                        StringComparison.Ordinal))
                {
                    var eventName = message.Value<string>("event");
                    var payload = message["payload"] as JObject;
                    var presentationId = payload?.Value<string>("presentationId");
                    if (ProviderPresentationCommitContract.IsValidPresentationId(
                            presentationId))
                    {
                        lock (_sessionSync)
                        {
                            if (string.Equals(
                                    eventName,
                                    "menu.presentation",
                                    StringComparison.Ordinal))
                            {
                                if (_presentationReadiness.BeginPresentation(
                                        _providerSessionGeneration,
                                        presentationId!))
                                {
                                    _externalGpuPostAcceptPaintGate.
                                        BeginPresentation(
                                            _providerSessionGeneration,
                                            presentationId!);
                                }
                            }
                            else if (string.Equals(
                                eventName,
                                "menu.dismissed",
                                StringComparison.Ordinal))
                            {
                                if (_presentationReadiness.CancelPresentation(
                                        _providerSessionGeneration,
                                        presentationId!))
                                {
                                    _externalGpuPostAcceptPaintGate.
                                        CancelPresentation(
                                            _providerSessionGeneration,
                                            presentationId!);
                                }
                            }
                        }
                    }
                }
                else if (string.Equals(
                    message.Value<string>("kind"),
                    "response",
                    StringComparison.Ordinal))
                {
                    var responseId = message.Value<string>("id");
                    string? aliasId = null;
                    if (!string.IsNullOrWhiteSpace(responseId))
                    {
                        lock (_sessionSync)
                        {
                            if (_presentationReadyResponseAliases.TryGetValue(
                                    responseId!,
                                    out aliasId))
                            {
                                _presentationReadyResponseAliases.Remove(
                                    responseId!);
                            }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(aliasId))
                    {
                        var alias = (JObject)message.DeepClone();
                        alias["id"] = aliasId;
                        aliasJson = alias.ToString(
                            Newtonsoft.Json.Formatting.None);
                    }
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // The provider's bridge contract remains authoritative. A
                // malformed frame is still forwarded so the browser can apply
                // its ordinary validation and diagnostics.
            }

            JsonRequested?.Invoke(json);
            if (aliasJson != null) JsonRequested?.Invoke(aliasJson);
        }

        // Called with _sessionSync held, after the previous writer has stopped
        // (or before a new writer starts), so queued browser operations are
        // never replayed into a replacement provider session.
        private void PurgeOutgoingFrames(string reason)
        {
            var purged = 0;
            while (_outgoing.TryDequeue(out _)) purged++;
            Interlocked.Exchange(ref _queuedFrames, 0);
            if (purged > 0)
            {
                _trace(
                    "bootstrap_host_frames_purged",
                    $"count={purged} reason={reason}");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(
            SafePipeHandle pipe,
            out uint clientProcessId);

        private static bool TryGetNamedPipeClientProcessId(
            SafePipeHandle pipe,
            out uint clientProcessId)
        {
            clientProcessId = 0;
            try { return GetNamedPipeClientProcessId(pipe, out clientProcessId); }
            catch (EntryPointNotFoundException) { return false; }
        }

        private sealed class BrowserBridgeSink : IBridgeMessageSink
        {
            private readonly BootstrapOverlayServer _owner;
            private readonly PresentationReadyBrowserRole _role;

            internal BrowserBridgeSink(
                BootstrapOverlayServer owner,
                PresentationReadyBrowserRole role)
            {
                _owner = owner;
                _role = role;
            }

            public bool TryEnqueue(string json, out BridgeError? error) =>
                _owner.TryEnqueue(_role, json, out error);
        }
    }
}
