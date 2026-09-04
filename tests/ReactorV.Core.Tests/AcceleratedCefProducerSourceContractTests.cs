using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class AcceleratedCefProducerSourceContractTests
{
    [Fact]
    public void AcceleratedCefPaintIsCapabilityGatedAndCopiedSynchronously()
    {
        var root = FindRepositoryRoot();
        var browser = Read(root, "Browser", "OffscreenBrowser.cs");
        var handler = Read(root, "Browser", "AcceleratedRenderHandler.cs");
        var submitter = Read(root, "Browser", "AcceleratedFrameSubmitter.cs");
        var native = Read(root, "Native", "NativeCompositor.cs");
        var capabilities = Read(root, "Native", "SharedTextureCapabilities.cs");

        Assert.Contains("NativeAcceleratedFrameSubmitter.TryCreate", browser);
        Assert.Contains("useLegacyRenderHandler: !_acceleratedRendering", browser);
        Assert.Contains("windowInfo.SharedTextureEnabled = _acceleratedRendering", browser);
        Assert.Contains("_browser.RenderHandler = new AcceleratedRenderHandler", browser);
        Assert.Contains("else\n            {\n                _browser.Paint += OnPaint;", Normalize(browser));

        Assert.Contains("class AcceleratedRenderHandler : DefaultRenderHandler", handler);
        Assert.Contains("override void OnAcceleratedPaint", handler);
        Assert.Contains("_submitter.TrySubmit(", handler);
        Assert.Contains("must open and copy it synchronously", handler);

        Assert.Contains("colorType != ColorType.Bgra8888", submitter);
        Assert.Contains("SharedGpuPixelFormat.Bgra8Unorm", submitter);
        Assert.DoesNotContain("(uint)colorType", submitter);
        Assert.Contains("NativeCompositor.SubmitSharedTextureStatus(", submitter);
        Assert.Contains("AcceleratedSubmitDecision.Disable", submitter);
        Assert.Contains("AcceleratedSubmitHealth.Backpressure", submitter);
        Assert.Contains("SharedTextureSubmitStatus.SessionInvalid", submitter);
        Assert.Contains("SharedTextureSubmitStatus.AdapterOrResourceInvalid", submitter);
        Assert.Contains("SharedTextureSubmitStatus.DeviceOrCopyFailure", submitter);
        Assert.Contains("_submitHealth.Observe(AcceleratedSubmitHealth.HardFailure)", submitter);
        Assert.Contains("PublishUnavailableOnce();", submitter);
        Assert.Contains("PublishActionSafely(Unavailable)", submitter);

        Assert.Contains("RWUI_GetSharedTextureCapabilities", native);
        Assert.Contains("RWUI_SubmitSharedTexture", native);
        Assert.Contains("RWUI_SubmitSharedTextureStatus", native);
        Assert.Contains("RWUI_ProbeSharedTextureStatus", native);
        Assert.Contains("RWUI_GetSharedTextureProducerDiagnostics", native);
        Assert.Contains("SynchronousTransientCopy", capabilities);
        Assert.Contains("ExpectedByteSize = 24", capabilities);
        Assert.Contains("SupportsSynchronousBgra8", capabilities);
    }

    [Fact]
    public void CefCallbacksFailOpenAcrossNativeAndObserverFailures()
    {
        var root = FindRepositoryRoot();
        var browser = Read(root, "Browser", "OffscreenBrowser.cs");

        Assert.Contains("args.Handled = false;", browser);
        Assert.Contains("args.Handled = NativeCompositor.SubmitFrame(", browser);
        Assert.Contains("catch (Exception)", browser);
        Assert.Contains("PublishSafely(ContentUnavailable)", browser);
        Assert.Contains("PublishSafely(ContentReady)", browser);
        Assert.Contains("PublishSafely(AcceleratedTransportReady)", browser);
        Assert.Contains("PublishSafely(AcceleratedTransportUnavailable)", browser);
        Assert.Contains("handlers.GetInvocationList()", browser);
    }

    [Fact]
    public void MissingNativeAcceleratedAbiFallsBackWithoutCrashingBrowserCreation()
    {
        var root = FindRepositoryRoot();
        var native = Read(root, "Native", "NativeCompositor.cs");
        var submitter = Read(root, "Browser", "AcceleratedFrameSubmitter.cs");

        Assert.Contains("catch (EntryPointNotFoundException)", native);
        Assert.Contains("catch (DllNotFoundException)", native);
        Assert.Contains("catch (BadImageFormatException)", native);
        Assert.Contains("submitter = null;", submitter);
        Assert.Contains("allowBootstrapProbe && capabilities.SupportsBootstrapProbe", submitter);
        Assert.Contains("bool allowAcceleratedBootstrapProbe = false", Read(
            root,
            "Browser",
            "OffscreenBrowser.cs"));
    }

    [Fact]
    public void BrowserRoutingAcceptsTheAuthoritativeHostMessageSink()
    {
        var root = FindRepositoryRoot();
        var browser = Read(root, "Browser", "OffscreenBrowser.cs");
        var session = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.DirectX",
            "DirectXOverlaySession.cs"));

        Assert.Contains("private readonly IBridgeMessageSink _bridgeSink;", browser);
        Assert.Contains("IBridgeMessageSink bridgeSink", browser);
        Assert.Contains("_bridgeSink.TryEnqueue(json, out var error)", browser);
        Assert.DoesNotContain("BridgeBroker broker", browser);

        Assert.Contains("private readonly IBridgeMessageSink _bridgeSink;", session);
        Assert.Contains("IBridgeMessageSink bridgeSink", session);
        Assert.DoesNotContain("BridgeBroker broker", session);
    }

    [Fact]
    public void ExternalSessionOwnsProducerAndUsesASeparateBoundedBootstrapProbe()
    {
        var root = FindRepositoryRoot();
        var session = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.DirectX",
            "ExternalGpuBrowserSession.cs"));
        var submitter = Read(root, "Browser", "AcceleratedFrameSubmitter.cs");
        var native = Read(root, "Native", "NativeCompositor.cs");
        var capabilities = Read(root, "Native", "SharedTextureCapabilities.cs");
        var browser = Read(root, "Browser", "OffscreenBrowser.cs");

        Assert.Contains(
            "public sealed class ExternalGpuBrowserSession : IExternalGpuBrowserProducer",
            session);
        Assert.Contains(
            "ExternalGpuBrowserSession(ExternalGpuBrowserProducerContext context)",
            session);
        Assert.Contains("NativeCompositor.StartSharedTextureProducer", session);
        Assert.Contains("NativeCompositor.SetSharedTextureProducerVisible(_desiredVisible)", session);
        Assert.Contains("NativeCompositor.SetSharedTextureProducerVisible(visible)", session);
        Assert.Contains("allowAcceleratedBootstrapProbe: true", session);
        Assert.DoesNotContain("forceCpuRendering: true", session);
        Assert.Contains("FirstAcceleratedFrameTimeoutMilliseconds = 10000", session);
        Assert.Contains("SharedGpuFrameProtocol.MaximumBytes", session);
        Assert.Contains("ThreadPool.QueueUserWorkItem", session);
        Assert.Contains("Never tear down CefSharp from OnAcceleratedPaint", session);

        var stopBrowser = session.IndexOf("StopBrowser();", StringComparison.Ordinal);
        var stopProducer = session.IndexOf("StopProducer();", stopBrowser, StringComparison.Ordinal);
        Assert.True(stopBrowser >= 0 && stopProducer > stopBrowser);

        Assert.Contains("RWUI_StartSharedTextureProducer", native);
        Assert.Contains("RWUI_SetSharedTextureProducerVisible", native);
        Assert.Contains("RWUI_ProbeSharedTexture", native);
        Assert.Contains("RWUI_StopSharedTextureProducer", native);
        Assert.Contains("BootstrapProbe = 1u << 4", capabilities);
        Assert.Contains("NativeCompositor.ProbeSharedTextureStatus(", submitter);
        Assert.Contains("probeStatus == SharedTextureSubmitStatus.Submitted", submitter);
        Assert.Contains("probe_status={_acceleratedSubmitter?.LastStatus}", browser);
        Assert.Contains("ProducerDiagnosticDetail()", browser);
        Assert.Contains("promoted.SupportsSynchronousBgra8", submitter);
        Assert.Contains("BootstrapProbeRetryMilliseconds = 100", submitter);
        Assert.Contains("Volatile.Write(ref _state, BootstrapPending)", submitter);
        Assert.Contains("Consumer attachment is independent of CEF startup", submitter);
    }

    [Fact]
    public void AcceleratedProbeDiagnosticsPreserveBooleanAbiAndExposeFailureBoundary()
    {
        var root = FindRepositoryRoot();
        var abi = File.ReadAllText(Path.Combine(
            root, "native", "include", "RageWebUI.Native.h"));
        var exports = File.ReadAllText(Path.Combine(
            root, "native", "src", "SharedGpuFrameExports.cpp"));
        var consumer = File.ReadAllText(Path.Combine(
            root, "native", "src", "SharedGpuFrameConsumer.h"));
        var managed = Read(root, "Native", "NativeCompositor.cs");
        var diagnostics = Read(root, "Native", "SharedTextureProducerDiagnostics.cs");

        Assert.Contains("RWUI_ProbeSharedTexture(", abi);
        Assert.Contains("RWUI_ProbeSharedTextureStatus(", abi);
        Assert.Contains("RWUI_GetSharedTextureProducerDiagnostics(", abi);
        Assert.Contains("RWUI_GetSharedTextureConsumerDiagnostics(", abi);
        Assert.Contains("RwuiSharedTextureSubmitStatus::Submitted) ? 1 : 0", exports);
        Assert.Contains("RecordRejectedAttempt(", exports);

        Assert.Contains("adapterOrResourceInvalid", abi);
        Assert.Contains("deviceOrCopyFailure", abi);
        Assert.Contains("acknowledgementsAccepted", abi);
        Assert.Contains("adapterDescription[128]", abi);
        Assert.Contains("lastReceiveError", abi);
        Assert.Contains("lastImportError", abi);
        Assert.Contains("receivedFrames_", consumer);
        Assert.Contains("publishedFrames_", consumer);

        Assert.Contains("ProbeSharedTextureStatus(", managed);
        Assert.Contains("TryGetSharedTextureProducerDiagnostics(", managed);
        Assert.Contains("catch (EntryPointNotFoundException)", managed);
        Assert.Contains("ExpectedByteSize = 416", diagnostics);
        Assert.Contains("adapter_luid=", diagnostics);
        Assert.Contains("ack_rejected=", diagnostics);
    }

    [Fact]
    public void HiddenExternalOutputKeepsTheRequiredBrowserDocumentRenderAwake()
    {
        var root = FindRepositoryRoot();
        var session = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.DirectX",
            "ExternalGpuBrowserSession.cs"));

        var setVisible = session.IndexOf(
            "public void SetVisible(bool visible)",
            StringComparison.Ordinal);
        var postJson = session.IndexOf(
            "public void PostJson(string json)",
            setVisible,
            StringComparison.Ordinal);
        Assert.True(setVisible >= 0 && postJson > setVisible);

        var body = session.Substring(setVisible, postJson - setVisible);
        Assert.Contains("NativeCompositor.SetSharedTextureProducerVisible(visible)", body);
        Assert.Contains("_browser?.SetVisible(true);", body);
        Assert.DoesNotContain("_browser?.SetVisible(visible);", body);
        Assert.Contains("startVisible: true", session);
    }

    [Fact]
    public void AcceleratedBootstrapRepaintIsCefUiMarshalledBoundedAndTerminallyCancelled()
    {
        var root = FindRepositoryRoot();
        var browser = Read(root, "Browser", "OffscreenBrowser.cs");
        var handler = Read(root, "Browser", "AcceleratedRenderHandler.cs");
        var submitter = Read(root, "Browser", "AcceleratedFrameSubmitter.cs");
        var session = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.DirectX",
            "ExternalGpuBrowserSession.cs"));

        Assert.Contains("RequestAcceleratedBootstrapPaint()", browser);
        Assert.Contains("Cef.PostAction(CefThreadIds.TID_UI", browser);
        Assert.Contains("_acceleratedSubmitter?.IsBootstrapPending != true", browser);
        Assert.Contains("host.WasHidden(false);", browser);
        Assert.Contains("host.Invalidate(PaintElementType.View);", browser);

        Assert.Contains("AcceleratedBootstrapRepaintIntervalMilliseconds = 250", session);
        Assert.Contains("MaximumAcceleratedBootstrapRepaintAttempts", session);
        Assert.Contains("AcceleratedBootstrapRepaintPolicy.EvaluateSurface(", session);
        Assert.Contains("Volatile.Read(ref _sizedFrameReady) == 1", session);
        var refreshStart = session.IndexOf("private bool RefreshPresentationCore(", StringComparison.Ordinal);
        var refreshEnd = session.IndexOf("public void PostJson(", refreshStart, StringComparison.Ordinal);
        var refresh = session.Substring(refreshStart, refreshEnd - refreshStart);
        Assert.Contains("StartAcceleratedBootstrapRepaintPump();", refresh);
        Assert.Contains("ArmSizedFrameDeadline();", refresh);
        Assert.Contains("browser.RequestAcceleratedPresentationPaint()", refresh);
        Assert.Contains("StartAcceleratedBootstrapRepaintPump();", session);
        Assert.Contains("CancelAcceleratedBootstrapRepaintPump();", session);
        Assert.Contains("OnFirstAcceleratedFrameTimeout()", session);
        Assert.Contains("first-accelerated-frame-timeout", session);
        Assert.Contains("timer.Change(", session);
        Assert.Contains("period: AcceleratedBootstrapRepaintIntervalMilliseconds", session);
        Assert.DoesNotContain("while (", session.Substring(
            session.IndexOf("private void PumpAcceleratedBootstrapPaint()", StringComparison.Ordinal),
            session.IndexOf("private void CancelAcceleratedBootstrapRepaintPump()", StringComparison.Ordinal) -
            session.IndexOf("private void PumpAcceleratedBootstrapPaint()", StringComparison.Ordinal)));

        Assert.Contains("AcceleratedPaintObservation", handler);
        Assert.Contains("AcceleratedFrameSubmitResult.CallbackFaulted", handler);
        Assert.Contains("_observer?.Invoke", handler);
        Assert.Contains("BootstrapProbeRejected", submitter);
        Assert.Contains("BootstrapProbeDeferred", submitter);
        Assert.Contains("accelerated_paint_first_callback", browser);
        Assert.Contains("accelerated_bootstrap_probe_rejected", browser);
        Assert.Contains("no-accelerated-paint-callback", session);
        Assert.Contains("bootstrap-probe-rejected", session);
    }

    [Fact]
    public void ExternalSessionFailureDisablesShadowWithoutFalseCpuReadiness()
    {
        var root = FindRepositoryRoot();
        var session = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.DirectX",
            "ExternalGpuBrowserSession.cs"));

        var queue = session.IndexOf("ThreadPool.QueueUserWorkItem", StringComparison.Ordinal);
        var disable = session.IndexOf("private void DisableExternalGpuPath", StringComparison.Ordinal);
        Assert.True(queue >= 0 && disable > queue);

        var disableEnd = session.IndexOf(
            "private void PublishContentReadyIfEligible",
            disable,
            StringComparison.Ordinal);
        Assert.True(disableEnd > disable);
        var disableBody = session.Substring(disable, disableEnd - disable);
        var stopBrowser = disableBody.IndexOf("StopBrowser();", StringComparison.Ordinal);
        var stopProducer = disableBody.IndexOf("StopProducer();", StringComparison.Ordinal);
        Assert.True(stopBrowser >= 0 && stopProducer > stopBrowser);
        Assert.Contains("Volatile.Write(ref _transportReady, 0)", disableBody);
        Assert.Contains("Volatile.Write(ref _started, 0)", disableBody);
        Assert.DoesNotContain("AttachBrowser", disableBody);
        Assert.DoesNotContain("forceCpuRendering: true", session);

        var dispose = session.IndexOf("public void Dispose()", StringComparison.Ordinal);
        var create = session.IndexOf("private OffscreenBrowser CreateBrowser", dispose, StringComparison.Ordinal);
        Assert.True(dispose >= 0 && create > dispose);
        var disposeBody = session.Substring(dispose, create - dispose);
        Assert.Contains("Interlocked.Exchange(ref _disposed, 1)", disposeBody);
        Assert.Contains("Stop();", disposeBody);
    }

    [Fact]
    public void EstablishedTransportDemotionCannotBeCancelledByStaleReadyState()
    {
        var root = FindRepositoryRoot();
        var session = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.DirectX",
            "ExternalGpuBrowserSession.cs"));
        var start = session.IndexOf(
            "private void OnAcceleratedTransportUnavailable()",
            StringComparison.Ordinal);
        var end = session.IndexOf(
            "private void QueueExternalGpuDisable(",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var body = session.Substring(start, end - start);
        var clearReady = body.IndexOf(
            "Volatile.Write(ref _transportReady, 0)",
            StringComparison.Ordinal);
        var noCancellation = body.IndexOf(
            "cancelIfTransportRecovered: false",
            StringComparison.Ordinal);
        Assert.True(clearReady >= 0 && noCancellation > clearReady);
        Assert.DoesNotContain("cancelIfTransportRecovered: true", body);
    }

    private static string Read(string root, string area, string fileName) => File.ReadAllText(Path.Combine(
        root,
        "src",
        "ReactorV.DirectX",
        area,
        fileName));

    private static string Normalize(string value) => value.Replace("\r\n", "\n");

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate != null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "ReactorV.json")) &&
                Directory.Exists(Path.Combine(candidate.FullName, "src")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the ReactorV source root for the accelerated CEF producer contract.");
    }
}
