using System;
using System.Collections.Generic;
using System.IO;
using RageWebUI.Core;
using ReactorV.ExternalGpu;
using ReactorV.Preloader;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class ExternalGpuBrowserSessionTests
    {
        [Fact]
        public void Default_off_gate_does_not_discover_or_start_a_producer()
        {
            var factory = new FakeFactory(new FakeProducer());
            var traces = new List<string>();

            var session = ExternalGpuBrowserSession.TryStart(
                enabled: false,
                CreateContext(),
                factory,
                (stage, detail) => traces.Add(stage + " " + detail));

            Assert.Null(session);
            Assert.Equal(0, factory.CreateCount);
            Assert.Contains(traces, line =>
                line.Contains("external_gpu_browser_shadow_disabled", StringComparison.Ordinal));
        }

        [Fact]
        public void Enabled_shadow_reuses_context_and_mirrors_existing_output_edges()
        {
            var producer = new FakeProducer();
            var factory = new FakeFactory(producer);
            var context = CreateContext();
            var traces = new List<string>();

            using var session = ExternalGpuBrowserSession.TryStart(
                enabled: true,
                context,
                factory,
                (stage, detail) => traces.Add(stage + " " + detail));

            Assert.NotNull(session);
            Assert.Same(context, factory.Context);
            Assert.Equal(1, producer.StartCount);

            session!.SetVisible(true);
            session.PostJson("{\"type\":\"state\"}");
            session.PostPointerInput(0.25f, 0.75f, true, false, 120);
            producer.RaiseContentReady();
            producer.RaiseContentUnavailable();

            Assert.Equal(new[] { true }, producer.Visibility);
            Assert.Equal(new[] { "{\"type\":\"state\"}" }, producer.Json);
            Assert.Single(producer.Pointer);
            Assert.Equal(
                (0.25f, 0.75f, true, false, 120),
                producer.Pointer[0]);
            Assert.Contains(traces, line =>
                line.Contains("authoritative_host=webview2", StringComparison.Ordinal) &&
                line.Contains("bridge_authority=bootstrap-server", StringComparison.Ordinal));
            Assert.Contains(traces, line =>
                line.Contains("shadow_content_ready", StringComparison.Ordinal));
            Assert.Contains(traces, line =>
                line.Contains("shadow_content_unavailable", StringComparison.Ordinal));
        }

        [Fact]
        public void Forwarding_fault_disables_only_shadow_and_disposes_once()
        {
            var producer = new FakeProducer { ThrowOnJson = true };
            var traces = new List<string>();
            var session = ExternalGpuBrowserSession.TryStart(
                enabled: true,
                CreateContext(),
                new FakeFactory(producer),
                (stage, detail) => traces.Add(stage + " " + detail));

            Assert.NotNull(session);
            session!.PostJson("{\"type\":\"state\"}");
            session.SetVisible(true);
            session.Dispose();
            session.Dispose();

            Assert.False(session.IsActive);
            Assert.Empty(producer.Visibility);
            Assert.Equal(1, producer.DisposeCount);
            Assert.Contains(traces, line =>
                line.Contains("operation=json", StringComparison.Ordinal) &&
                line.Contains("fallback=webview2", StringComparison.Ordinal));
        }

        [Fact]
        public void Producer_startup_event_fails_open_and_releases_the_shadow()
        {
            var producer = new FakeProducer();
            var traces = new List<string>();
            using var session = ExternalGpuBrowserSession.TryStart(
                enabled: true,
                CreateContext(),
                new FakeFactory(producer),
                (stage, detail) => traces.Add(stage + " " + detail));

            Assert.NotNull(session);
            producer.RaiseStartupFailed(new InvalidOperationException("cef failed"));

            Assert.False(session!.IsActive);
            Assert.Equal(1, producer.DisposeCount);
            Assert.Contains(traces, line =>
                line.Contains("operation=startup-event", StringComparison.Ordinal));
        }

        [Fact]
        public void Missing_optional_assembly_is_actionable_and_non_fatal()
        {
            var temporary = Path.Combine(
                Path.GetTempPath(),
                "ReactorV.ExternalGpuBrowser.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                var factory = new ExternalGpuBrowserProducerAssemblyFactory(temporary);
                var created = factory.TryCreate(
                    CreateContext(),
                    out var producer,
                    out var detail);

                Assert.False(created);
                Assert.Null(producer);
                Assert.Contains(
                    ExternalGpuBrowserProducerAssemblyFactory.AssemblyFileName,
                    detail,
                    StringComparison.Ordinal);
                Assert.Contains("disable externalGpuBrowserShadow", detail);
            }
            finally
            {
                Directory.Delete(temporary, recursive: true);
            }
        }

        [Fact]
        public void Preloader_source_keeps_webview_authoritative_and_native_gpu_default_on()
        {
            var program = ReadRepositoryFile(
                "src", "ReactorV.Preloader", "Program.cs");
            var settings = ReadRepositoryFile("ReactorV.Preloader.json");

            Assert.Contains(
                "[JsonProperty(\"externalGpuBrowserShadow\")]",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "public bool ExternalGpuBrowserShadow { get; set; } = true;",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"externalGpuBrowserShadow\": true",
                settings,
                StringComparison.Ordinal);
            Assert.Contains(
                "[JsonProperty(\"externalGpuFrameRate\")]",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "frameRate: options.ExternalGpuFrameRate",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"externalGpuFrameRate\": 30",
                settings,
                StringComparison.Ordinal);
            Assert.Contains("--external-gpu-browser-shadow", program);
            Assert.Contains("--no-external-gpu-browser-shadow", program);

            var webViewConstruction = program.IndexOf(
                "_hostWindow = new OverlayWindow(",
                StringComparison.Ordinal);
            var shadowConstruction = program.IndexOf(
                "_externalGpuBrowserSession = ExternalGpuBrowserSession.TryStart(",
                StringComparison.Ordinal);
            Assert.True(webViewConstruction >= 0);
            Assert.True(shadowConstruction > webViewConstruction);
            Assert.Contains(
                "private void PostBrowserJson(OverlayWindow window, string json)",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "window.PostJson(json);\n            " +
                "_externalGpuBrowserSession?.PostJson(json);",
                program.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Contains(
                "ExclusiveBrowserPresentationPolicy.Resolve(",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "window.SetExternalPresentationOwnership(true);",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "bridge_authority=webview dual_readiness=preserved",
                program,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "window.SetOverlayVisible(visible);\n            " +
                "_externalGpuBrowserSession?.SetVisible(visible);",
                program.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Contains(
                "_externalGpuBrowserSession?.Dispose();\n                " +
                "_hostServer?.Dispose();",
                program.Replace("\r\n", "\n", StringComparison.Ordinal));
        }

        private static ExternalGpuBrowserProducerContext CreateContext() =>
            new ExternalGpuBrowserProducerContext(
                4242,
                ".",
                ".",
                ".",
                new BridgeBroker(),
                640,
                360,
                60,
                enableDevTools: false);

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
            return File.ReadAllText(Path.Combine(current!.FullName, Path.Combine(parts)));
        }

        private sealed class FakeFactory : IExternalGpuBrowserProducerFactory
        {
            private readonly IExternalGpuBrowserProducer _producer;

            public FakeFactory(IExternalGpuBrowserProducer producer) =>
                _producer = producer;

            public string DiscoverySource => "test";
            public int CreateCount { get; private set; }
            public ExternalGpuBrowserProducerContext? Context { get; private set; }

            public bool TryCreate(
                ExternalGpuBrowserProducerContext context,
                out IExternalGpuBrowserProducer? producer,
                out string detail)
            {
                CreateCount++;
                Context = context;
                producer = _producer;
                detail = "fixture=true";
                return true;
            }
        }

        private sealed class FakeProducer : IExternalGpuBrowserProducer
        {
            public string RendererName => "fake-cef";
            public bool IsContentReady { get; private set; }
            public bool ThrowOnJson { get; set; }
            public int StartCount { get; private set; }
            public int DisposeCount { get; private set; }
            public List<bool> Visibility { get; } = new List<bool>();
            public List<string> Json { get; } = new List<string>();
            public List<(float, float, bool, bool, int)> Pointer { get; } =
                new List<(float, float, bool, bool, int)>();

            public event Action? ContentReady;
            public event Action? ContentUnavailable;
            public event Action<Exception>? StartupFailed;

            public bool Start()
            {
                StartCount++;
                return true;
            }

            public void SetVisible(bool visible) => Visibility.Add(visible);

            public void PostJson(string json)
            {
                if (ThrowOnJson)
                    throw new InvalidOperationException("json failed");
                Json.Add(json);
            }

            public void PostPointerInput(
                float normalizedX,
                float normalizedY,
                bool pressed,
                bool released,
                int wheelDelta) => Pointer.Add((
                    normalizedX,
                    normalizedY,
                    pressed,
                    released,
                    wheelDelta));

            public void RaiseStartupFailed(Exception error) => StartupFailed?.Invoke(error);

            public void RaiseContentReady()
            {
                IsContentReady = true;
                ContentReady?.Invoke();
            }

            public void RaiseContentUnavailable()
            {
                IsContentReady = false;
                ContentUnavailable?.Invoke();
            }

            public void Dispose() => DisposeCount++;
        }
    }
}
