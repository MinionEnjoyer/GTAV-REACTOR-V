using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ReactorV.WebView2Host;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class WebView2HostTests
    {
        [Fact]
        public void WindowedOverlayUsesAnOpaqueNonBlackChromaKey()
        {
            var argb = OverlayPresentationPolicy.ChromaKeyArgb;

            Assert.Equal(255, (argb >> 24) & 0xFF);
            Assert.NotEqual(unchecked((int)0xFF000000), argb);
            Assert.NotEqual(0, argb);
        }

        [Theory]
        [InlineData(true, true, false, true, true, true)]
        [InlineData(false, true, false, true, true, false)]
        [InlineData(true, false, false, true, true, false)]
        [InlineData(true, true, true, true, true, false)]
        [InlineData(true, true, false, false, true, false)]
        [InlineData(true, true, false, true, false, false)]
        public void PresentationRequiresRequestReadinessAndUsableGameWindow(
            bool requested,
            bool ready,
            bool minimized,
            bool foreground,
            bool hasBounds,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.ShouldPresent(
                    requested,
                    ready,
                    minimized,
                    foreground,
                    hasBounds));
        }

        [Fact]
        public void NavigationPolicyAllowsOnlyOneInlineDocument()
        {
            var pending = true;

            Assert.True(WebView2LocalPagePolicy.IsAllowedNavigation(
                "about:blank", ref pending));
            Assert.True(pending);
            Assert.True(WebView2LocalPagePolicy.IsAllowedNavigation(
                "data:text/html,reactor", ref pending));
            Assert.False(pending);
            Assert.False(WebView2LocalPagePolicy.IsAllowedNavigation(
                "data:text/html,replacement", ref pending));
        }

        [Theory]
        [InlineData("https://reactorv.local/assets/app.js", true)]
        [InlineData("https://reactorv.local.evil.example/app.js", false)]
        [InlineData("http://reactorv.local/app.js", false)]
        [InlineData("https://example.com/", false)]
        [InlineData("file:///C:/temp/index.html", false)]
        [InlineData("javascript:alert(1)", false)]
        public void NavigationPolicyPinsTheMappedOrigin(string uri, bool expected)
        {
            var pending = false;

            Assert.Equal(expected, WebView2LocalPagePolicy.IsAllowedNavigation(
                uri, ref pending));
        }

        [Theory]
        [InlineData("about:blank", true)]
        [InlineData("data:text/html,reactor", true)]
        [InlineData("https://reactorv.local/", true)]
        [InlineData("https://example.com/", false)]
        [InlineData("file:///C:/temp/index.html", false)]
        public void BridgeMessagesRequireTheTrustedDocumentSource(
            string source,
            bool expected)
        {
            Assert.Equal(expected, WebView2LocalPagePolicy.IsTrustedMessageSource(source));
        }

        [Fact]
        public void InlineDocumentInjectsBaseAndRestrictivePolicy()
        {
            using var fixture = TemporaryDirectory.Create();
            File.WriteAllText(
                Path.Combine(fixture.Path, "index.html"),
                "<!doctype html><html><head><title>Fixture</title></head>" +
                "<body><div id=\"root\"></div></body></html>");

            var html = WebView2LocalPagePolicy.InlineIndexHtml(fixture.Path);

            Assert.Contains("Content-Security-Policy", html);
            Assert.Contains("default-src 'none'", html);
            Assert.Contains("script-src https://reactorv.local", html);
            Assert.Contains("frame-src 'none'", html);
            Assert.Contains("<base href=\"https://reactorv.local/\">", html);
            Assert.True(
                html.IndexOf("Content-Security-Policy", StringComparison.Ordinal) <
                html.IndexOf("<title>", StringComparison.Ordinal));
        }

        [Fact]
        public void InlineDocumentRejectsMalformedIndex()
        {
            using var fixture = TemporaryDirectory.Create();
            File.WriteAllText(
                Path.Combine(fixture.Path, "index.html"),
                "<html><body>missing head</body></html>");

            Assert.Throws<InvalidOperationException>(
                () => WebView2LocalPagePolicy.InlineIndexHtml(fixture.Path));
        }

        [Fact]
        public async Task ReadinessIgnoresEmptyMarkersUntilPageIsReady()
        {
            var results = new Queue<string>(new[]
            {
                "null",
                "{}",
                "{\"readyState\":\"complete\",\"rootChildren\":1}",
            });

            var result = await WebView2ReadinessPolicy.WaitForMarkerAsync(
                () => Task.FromResult(results.Dequeue()),
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero);

            Assert.Contains("rootChildren", result);
            Assert.Empty(results);
        }

        [Fact]
        public async Task ReadinessTimeoutNeverBecomesAFalseSuccess()
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                WebView2ReadinessPolicy.WaitForMarkerAsync(
                    () => Task.FromResult("null"),
                    TimeSpan.Zero,
                    TimeSpan.Zero));
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            private TemporaryDirectory(string path) => Path = path;

            public string Path { get; }

            public static TemporaryDirectory Create()
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "ReactorV-Tests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(path);
                return new TemporaryDirectory(path);
            }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
        }
    }
}
