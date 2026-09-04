using System;
using System.Collections.Generic;
using System.IO;
using RageWebUI.Core;
using ReactorV.ExternalGpu;
using ReactorV.Preloader;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ExternalGpuBrowserResizeTests
{
    [Fact]
    public void Visibility_is_deferred_until_the_requested_surface_is_ready()
    {
        var producer = new ResizableProducer(640, 360);
        using var session = ExternalGpuBrowserSession.TryStart(
            enabled: true,
            CreateContext(),
            new Factory(producer),
            (_, _) => { });

        Assert.NotNull(session);
        session!.SetVisible(true);
        Assert.Equal(new[] { false }, producer.Visibility);

        producer.RaisePresentationReady();
        // Readiness is evidence only. Mirror Program's STA arbiter, which is
        // the sole authority allowed to promote the native producer.
        session.SetVisible(true);

        Assert.True(session.IsPresentationReady);
        Assert.Equal(new[] { false, true }, producer.Visibility);
    }

    [Fact]
    public void Resize_hides_the_old_surface_and_promotes_only_the_new_size()
    {
        var producer = new ResizableProducer(640, 360);
        producer.RaisePresentationReady();
        var traces = new List<string>();
        using var session = ExternalGpuBrowserSession.TryStart(
            enabled: true,
            CreateContext(),
            new Factory(producer),
            (stage, detail) => traces.Add(stage + " " + detail));

        Assert.NotNull(session);
        session!.SetVisible(true);
        Assert.True(session.Resize(2560, 1440));

        Assert.False(session.IsPresentationReady);
        Assert.Equal(2560, session.SurfaceWidth);
        Assert.Equal(1440, session.SurfaceHeight);
        Assert.Equal(new[] { true, false }, producer.Visibility);
        Assert.Contains(traces, line =>
            line.Contains(
                "external_gpu_browser_shadow_resize_requested",
                StringComparison.Ordinal) &&
            line.Contains("requested=2560x1440", StringComparison.Ordinal));

        producer.RaisePresentationReady();
        session.SetVisible(true);

        Assert.True(session.IsPresentationReady);
        Assert.Equal(new[] { true, false, true }, producer.Visibility);
    }

    [Fact]
    public void DirectX_readiness_is_driven_by_exact_size_submit_and_acknowledgement()
    {
        var root = FindRepositoryRoot();
        var session = File.ReadAllText(Path.Combine(
            root, "src", "ReactorV.DirectX", "ExternalGpuBrowserSession.cs"));
        var browser = File.ReadAllText(Path.Combine(
            root, "src", "ReactorV.DirectX", "Browser", "OffscreenBrowser.cs"));

        Assert.Contains("AcceleratedFrameSubmitted", browser);
        Assert.Contains("host.NotifyMoveOrResizeStarted();", browser);
        Assert.Contains("host.WasResized();", browser);
        Assert.Contains("OnAcceleratedFrameSubmitted", session);
        Assert.Contains("width != SurfaceWidth || height != SurfaceHeight", session);
        Assert.Contains("diagnostics.LastAcknowledgedGeneration", session);
        Assert.Contains("Volatile.Read(ref _sizedFrameReady) != 1", session);
        Assert.Contains(
            "visible && IsPresentationReady",
            session.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static ExternalGpuBrowserProducerContext CreateContext() => new(
        4242,
        ".",
        ".",
        ".",
        new BridgeBroker(),
        640,
        360,
        60,
        enableDevTools: false);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
            !(File.Exists(Path.Combine(current.FullName, "ReactorV.json")) &&
              Directory.Exists(Path.Combine(current.FullName, "src"))))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!.FullName;
    }

    private sealed class Factory : IExternalGpuBrowserProducerFactory
    {
        private readonly IExternalGpuBrowserProducer _producer;

        internal Factory(IExternalGpuBrowserProducer producer) =>
            _producer = producer;

        public string DiscoverySource => "test";

        public bool TryCreate(
            ExternalGpuBrowserProducerContext context,
            out IExternalGpuBrowserProducer? producer,
            out string detail)
        {
            producer = _producer;
            detail = "fixture=true";
            return true;
        }
    }

    private sealed class ResizableProducer : IResizableExternalGpuBrowserProducer
    {
        internal ResizableProducer(int width, int height)
        {
            SurfaceWidth = width;
            SurfaceHeight = height;
        }

        public string RendererName => "fake-resizable-cef";
        public bool IsContentReady => IsPresentationReady;
        public bool IsPresentationReady { get; private set; }
        public int SurfaceWidth { get; private set; }
        public int SurfaceHeight { get; private set; }
        public List<bool> Visibility { get; } = new();

        public event Action? ContentReady;
        public event Action? ContentUnavailable;
        public event Action<Exception>? StartupFailed;
        public event Action<bool, int, int>? PresentationReadinessChanged;

        public bool Start() => true;

        public bool Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            SurfaceWidth = width;
            SurfaceHeight = height;
            IsPresentationReady = false;
            PresentationReadinessChanged?.Invoke(false, width, height);
            return true;
        }

        public bool RefreshPresentation()
        {
            IsPresentationReady = false;
            PresentationReadinessChanged?.Invoke(
                false,
                SurfaceWidth,
                SurfaceHeight);
            return true;
        }

        public void SetVisible(bool visible) => Visibility.Add(visible);
        public void PostJson(string json) { }
        public void PostPointerInput(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta) { }

        internal void RaisePresentationReady()
        {
            IsPresentationReady = true;
            PresentationReadinessChanged?.Invoke(
                true,
                SurfaceWidth,
                SurfaceHeight);
            ContentReady?.Invoke();
        }

        internal void RaiseUnavailable() => ContentUnavailable?.Invoke();

        internal void RaiseStartupFailed(Exception error) =>
            StartupFailed?.Invoke(error);

        public void Dispose() { }
    }
}
