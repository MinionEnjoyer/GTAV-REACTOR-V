using System;
using System.Collections.Generic;
using RageWebUI.Core;
using ReactorV.ExternalGpu;
using ReactorV.Preloader;
using Xunit;

namespace RageWebUI.Core.Tests;

/// <summary>
/// Behavioral regressions for the CEF surface-to-GTA viewport boundary. DOM
/// readiness alone is intentionally insufficient: the exact requested size
/// must be acknowledged before native pixels may become visible.
/// </summary>
public sealed class ExternalGpuSurfacePresentationRegressionTests
{
    [Fact]
    public void Mismatched_startup_surface_stays_hidden_until_exact_gta_size_is_ready()
    {
        var producer = new ResizableProducer(
            width: 640,
            height: 360,
            presentationReady: false);
        using var session = StartSession(producer, 2560, 1440);

        session.SetVisible(true);

        Assert.Equal(new[] { false }, producer.Visibility);
        Assert.False(session.IsPresentationReady);

        Assert.True(session.Resize(2560, 1440));
        producer.PublishAcknowledgement(640, 360);

        Assert.False(session.IsPresentationReady);
        Assert.DoesNotContain(true, producer.Visibility);

        producer.PublishAcknowledgement(2560, 1440);
        session.SetVisible(true);

        Assert.True(session.IsPresentationReady);
        Assert.Equal(2560, session.SurfaceWidth);
        Assert.Equal(1440, session.SurfaceHeight);
        Assert.Equal(new[] { false, false, true }, producer.Visibility);
    }

    [Fact]
    public void Gta_client_resize_hides_old_frame_then_reveals_only_new_size()
    {
        var producer = new ResizableProducer(
            width: 1920,
            height: 1080,
            presentationReady: true);
        using var session = StartSession(producer, 1920, 1080);

        session.SetVisible(true);
        Assert.Equal(new[] { true }, producer.Visibility);

        Assert.True(session.Resize(3440, 1440));

        Assert.False(session.IsPresentationReady);
        Assert.Equal(new[] { true, false }, producer.Visibility);

        producer.PublishAcknowledgement(1920, 1080);
        Assert.False(session.IsPresentationReady);
        Assert.Equal(new[] { true, false }, producer.Visibility);

        producer.PublishAcknowledgement(3440, 1440);
        session.SetVisible(true);

        Assert.True(session.IsPresentationReady);
        Assert.Equal(new[] { true, false, true }, producer.Visibility);
    }

    [Fact]
    public void Hidden_resize_does_not_reveal_when_new_size_becomes_ready()
    {
        var producer = new ResizableProducer(
            width: 1920,
            height: 1080,
            presentationReady: true);
        using var session = StartSession(producer, 1920, 1080);

        session.SetVisible(false);
        Assert.True(session.Resize(2560, 1440));
        producer.PublishAcknowledgement(2560, 1440);

        Assert.True(session.IsPresentationReady);
        Assert.DoesNotContain(true, producer.Visibility);
    }

    [Fact]
    public void Exact_size_ack_after_earlier_dom_ready_releases_deferred_visibility()
    {
        var producer = new ResizableProducer(
            width: 640,
            height: 360,
            presentationReady: false);
        using var session = StartSession(producer, 2560, 1440);

        // This is the real startup ordering: the document can finish loading
        // before the first exact-size shared texture reaches GTA.
        producer.PublishDomContentReady();
        session.SetVisible(true);
        Assert.True(session.Resize(2560, 1440));

        producer.PublishAcknowledgement(
            2560,
            1440,
            publishContentReady: false);
        session.SetVisible(true);

        Assert.True(session.IsPresentationReady);
        Assert.Equal(new[] { false, false, true }, producer.Visibility);
    }

    [Fact]
    public void Refresh_hides_the_acknowledged_surface_and_invalidates_readiness()
    {
        var producer = new ResizableProducer(
            width: 1920,
            height: 1080,
            presentationReady: true);
        using var session = StartSession(producer, 1920, 1080);

        session.SetVisible(true);
        Assert.True(session.IsPresentationReady);

        Assert.True(session.RefreshPresentation());

        Assert.False(session.IsPresentationReady);
        Assert.Equal(1920, session.SurfaceWidth);
        Assert.Equal(1080, session.SurfaceHeight);
        Assert.True(producer.Visibility.Count >= 2);
        Assert.True(producer.Visibility[0]);
        for (var index = 1; index < producer.Visibility.Count; index++)
            Assert.False(producer.Visibility[index]);
    }

    [Fact]
    public void Pre_refresh_ack_cannot_reveal_but_fresh_exact_size_ack_can()
    {
        var producer = new ResizableProducer(
            width: 2560,
            height: 1440,
            presentationReady: true);
        using var session = StartSession(producer, 2560, 1440);

        session.SetVisible(true);
        var preRefreshRevision = producer.PresentationRevision;

        Assert.True(session.RefreshPresentation());
        var refreshedRevision = producer.PresentationRevision;
        Assert.True(refreshedRevision > preRefreshRevision);

        producer.PublishAcknowledgement(
            2560,
            1440,
            presentationRevision: preRefreshRevision);

        Assert.False(session.IsPresentationReady);
        Assert.True(producer.Visibility.Count >= 2);
        Assert.True(producer.Visibility[0]);
        for (var index = 1; index < producer.Visibility.Count; index++)
            Assert.False(producer.Visibility[index]);

        producer.PublishAcknowledgement(
            2560,
            1440,
            presentationRevision: refreshedRevision);
        session.SetVisible(true);

        Assert.True(session.IsPresentationReady);
        Assert.True(producer.Visibility[producer.Visibility.Count - 1]);
    }

    [Fact]
    public void Retained_refresh_keeps_last_frame_visible_until_fresh_acknowledgement()
    {
        var producer = new RetainedResizableProducer(
            width: 2560,
            height: 1440,
            presentationReady: true);
        using var session = StartSession(producer, 2560, 1440);

        session.SetVisible(true);
        Assert.True(session.IsPresentationReady);
        var preRefreshRevision = producer.PresentationRevision;

        Assert.True(session.RefreshPresentation(retainCurrentFrame: true));
        Assert.False(session.IsPresentationReady);
        Assert.True(producer.RetainedRefreshRequested);
        Assert.Equal(new[] { true }, producer.Visibility);

        // Re-arbitrating the same native plane while the new frame is pending
        // must not translate readiness=false into a native hide.
        session.SetVisible(true);
        Assert.Equal(new[] { true }, producer.Visibility);

        producer.PublishAcknowledgement(
            2560,
            1440,
            presentationRevision: preRefreshRevision);
        Assert.False(session.IsPresentationReady);
        Assert.Equal(new[] { true }, producer.Visibility);

        producer.PublishAcknowledgement(2560, 1440);
        session.SetVisible(true);
        Assert.True(session.IsPresentationReady);
        Assert.Equal(new[] { true, true }, producer.Visibility);
    }

    [Fact]
    public void Authoritative_hide_cancels_retained_frame_auto_promotion()
    {
        var producer = new RetainedResizableProducer(
            width: 2560,
            height: 1440,
            presentationReady: true);
        using var session = StartSession(producer, 2560, 1440);

        session.SetVisible(true);
        Assert.True(session.RefreshPresentation(retainCurrentFrame: true));
        Assert.False(session.IsPresentationReady);

        session.SetVisible(false);
        producer.PublishAcknowledgement(2560, 1440);

        Assert.True(session.IsPresentationReady);
        Assert.Equal(new[] { true, false }, producer.Visibility);
    }

    private static ExternalGpuBrowserSession StartSession(
        IExternalGpuBrowserProducer producer,
        int width,
        int height)
    {
        var current = Environment.CurrentDirectory;
        var context = new ExternalGpuBrowserProducerContext(
            4242,
            current,
            current,
            current,
            new BridgeBroker(),
            width,
            height,
            frameRate: 60,
            enableDevTools: false);
        var session = ExternalGpuBrowserSession.TryStart(
            enabled: true,
            context,
            new ProducerFactory(producer),
            (_, _) => { });

        return Assert.IsType<ExternalGpuBrowserSession>(session);
    }

    private sealed class ProducerFactory : IExternalGpuBrowserProducerFactory
    {
        private readonly IExternalGpuBrowserProducer _producer;

        public ProducerFactory(IExternalGpuBrowserProducer producer) =>
            _producer = producer;

        public string DiscoverySource => "regression-fixture";

        public bool TryCreate(
            ExternalGpuBrowserProducerContext context,
            out IExternalGpuBrowserProducer? producer,
            out string detail)
        {
            producer = _producer;
            detail = $"requested={context.Width}x{context.Height}";
            return true;
        }
    }

    private sealed class ResizableProducer : IResizableExternalGpuBrowserProducer
    {
        private int _acknowledgedWidth;
        private int _acknowledgedHeight;
        private int _acknowledgedPresentationRevision;

        public ResizableProducer(
            int width,
            int height,
            bool presentationReady)
        {
            SurfaceWidth = width;
            SurfaceHeight = height;
            PresentationRevision = 1;
            if (presentationReady)
            {
                _acknowledgedWidth = width;
                _acknowledgedHeight = height;
                _acknowledgedPresentationRevision = PresentationRevision;
            }
        }

        public string RendererName => "resizable-fixture";
        public bool IsContentReady => true;
        public int SurfaceWidth { get; private set; }
        public int SurfaceHeight { get; private set; }
        public int PresentationRevision { get; private set; }
        public bool IsPresentationReady =>
            _acknowledgedWidth == SurfaceWidth &&
            _acknowledgedHeight == SurfaceHeight &&
            _acknowledgedPresentationRevision == PresentationRevision;
        public List<bool> Visibility { get; } = new List<bool>();

        public event Action? ContentReady;
        public event Action? ContentUnavailable;
        public event Action<Exception>? StartupFailed;
        public event Action<bool, int, int>? PresentationReadinessChanged;

        public bool Start() => true;

        public void SetVisible(bool visible) => Visibility.Add(visible);

        public bool Resize(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return false;
            SurfaceWidth = width;
            SurfaceHeight = height;
            PresentationRevision++;
            _acknowledgedWidth = 0;
            _acknowledgedHeight = 0;
            _acknowledgedPresentationRevision = 0;
            PresentationReadinessChanged?.Invoke(false, width, height);
            return true;
        }

        public bool RefreshPresentation()
        {
            PresentationRevision++;
            _acknowledgedWidth = 0;
            _acknowledgedHeight = 0;
            _acknowledgedPresentationRevision = 0;
            SetVisible(false);
            PresentationReadinessChanged?.Invoke(
                false,
                SurfaceWidth,
                SurfaceHeight);
            return true;
        }

        public void PublishAcknowledgement(
            int width,
            int height,
            bool publishContentReady = true,
            int? presentationRevision = null)
        {
            var acknowledgedRevision =
                presentationRevision ?? PresentationRevision;
            if (width == SurfaceWidth &&
                height == SurfaceHeight &&
                acknowledgedRevision == PresentationRevision)
            {
                _acknowledgedWidth = width;
                _acknowledgedHeight = height;
                _acknowledgedPresentationRevision = acknowledgedRevision;
            }
            PresentationReadinessChanged?.Invoke(
                IsPresentationReady,
                width,
                height);
            if (IsPresentationReady && publishContentReady)
                ContentReady?.Invoke();
        }

        public void PublishDomContentReady() => ContentReady?.Invoke();

        public void PostJson(string json) { }

        public void PostPointerInput(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta) { }

        public void Dispose() { }

        // Keep the complete production event surface intentional in the fake.
        public void PublishUnavailable() => ContentUnavailable?.Invoke();
        public void PublishStartupFailure(Exception error) =>
            StartupFailed?.Invoke(error);
    }

    private sealed class RetainedResizableProducer :
        IRetainedExternalGpuBrowserProducer
    {
        private int _acknowledgedWidth;
        private int _acknowledgedHeight;
        private int _acknowledgedPresentationRevision;

        internal RetainedResizableProducer(
            int width,
            int height,
            bool presentationReady)
        {
            SurfaceWidth = width;
            SurfaceHeight = height;
            PresentationRevision = 1;
            if (presentationReady)
            {
                _acknowledgedWidth = width;
                _acknowledgedHeight = height;
                _acknowledgedPresentationRevision = PresentationRevision;
            }
        }

        public string RendererName => "retained-resizable-fixture";
        public bool IsContentReady => true;
        public int SurfaceWidth { get; private set; }
        public int SurfaceHeight { get; private set; }
        public int PresentationRevision { get; private set; }
        public bool RetainedRefreshRequested { get; private set; }
        public bool IsPresentationReady =>
            _acknowledgedWidth == SurfaceWidth &&
            _acknowledgedHeight == SurfaceHeight &&
            _acknowledgedPresentationRevision == PresentationRevision;
        public List<bool> Visibility { get; } = new List<bool>();

        public event Action? ContentReady;
        public event Action? ContentUnavailable;
        public event Action<Exception>? StartupFailed;
        public event Action<bool, int, int>? PresentationReadinessChanged;

        public bool Start() => true;
        public void SetVisible(bool visible) => Visibility.Add(visible);
        public bool Resize(int width, int height) => false;
        public bool RefreshPresentation() => false;

        public bool RefreshPresentationRetainingCurrentFrame()
        {
            RetainedRefreshRequested = true;
            PresentationRevision++;
            _acknowledgedWidth = 0;
            _acknowledgedHeight = 0;
            _acknowledgedPresentationRevision = 0;
            PresentationReadinessChanged?.Invoke(
                false,
                SurfaceWidth,
                SurfaceHeight);
            return true;
        }

        public void PublishAcknowledgement(
            int width,
            int height,
            int? presentationRevision = null)
        {
            var revision = presentationRevision ?? PresentationRevision;
            if (width == SurfaceWidth && height == SurfaceHeight &&
                revision == PresentationRevision)
            {
                _acknowledgedWidth = width;
                _acknowledgedHeight = height;
                _acknowledgedPresentationRevision = revision;
            }
            PresentationReadinessChanged?.Invoke(
                IsPresentationReady,
                width,
                height);
            if (IsPresentationReady)
                ContentReady?.Invoke();
        }

        public void PostJson(string json) { }
        public void PostPointerInput(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta) { }
        public void Dispose() { }

        public void PublishUnavailable() => ContentUnavailable?.Invoke();
        public void PublishStartupFailure(Exception error) =>
            StartupFailed?.Invoke(error);
    }
}
