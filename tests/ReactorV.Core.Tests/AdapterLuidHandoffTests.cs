using System;
using System.IO;
using RageWebUI.DirectX;
using RageWebUI.DirectX.Browser;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class AdapterLuidHandoffTests
{
    [Fact]
    public void CefValueUsesSignedHighUnsignedLowInvariantDecimal()
    {
        var luid = new GpuAdapterLuid(-17, 4_064_643_960u);

        Assert.Equal("-17,4064643960", luid.ToCefCommandLineValue());
        Assert.Equal(luid, new GpuAdapterLuid(-17, 4_064_643_960u));
        Assert.NotEqual(luid, new GpuAdapterLuid(17, 4_064_643_960u));
    }

    [Theory]
    [InlineData(false, false, false, (int)AdapterLuidDiscoveryDecision.Continue)]
    [InlineData(true, false, false, (int)AdapterLuidDiscoveryDecision.StartBrowser)]
    [InlineData(true, true, false, (int)AdapterLuidDiscoveryDecision.StartBrowser)]
    [InlineData(false, true, false, (int)AdapterLuidDiscoveryDecision.DisableExternalGpuPath)]
    [InlineData(true, true, true, (int)AdapterLuidDiscoveryDecision.Stop)]
    public void DiscoveryWaitIsBoundedAndStopWins(
        bool discovered,
        bool deadlineReached,
        bool stopping,
        int expected)
    {
        Assert.Equal(
            (AdapterLuidDiscoveryDecision)expected,
            AdapterLuidDiscoveryWaitPolicy.Evaluate(
                discovered,
                deadlineReached,
                stopping));
    }

    [Fact]
    public void NativeMappingIsProcessIdentityValidatedAndConsumerOwned()
    {
        var root = FindRepositoryRoot();
        var discovery = File.ReadAllText(Path.Combine(
            root, "native", "src", "AdapterLuidDiscovery.cpp"));
        var exports = File.ReadAllText(Path.Combine(
            root, "native", "src", "AdapterLuidExports.cpp"));
        var consumer = File.ReadAllText(Path.Combine(
            root, "native", "src", "SharedGpuFrameConsumer.cpp"));
        var compositor = File.ReadAllText(Path.Combine(
            root, "native", "src", "DirectXCompositor.cpp"));

        Assert.Contains("Local\\\\ReactorV.AdapterLuid.v1.%08X", discovery);
        Assert.Contains("QueryProcessCreationTime", discovery);
        Assert.Contains("publisherCreationTime == targetCreationTime", discovery);
        Assert.Contains("confirmedCreationTime != expectedCreationTime", discovery);
        Assert.Contains("RWUI_QueryTargetAdapterLuid", exports);
        Assert.Contains("adapterLuidPublisher_.Publish(authoritativeAdapterLuid)", consumer);
        Assert.Contains("adapterLuidPublisher_.Clear()", consumer);
        Assert.Contains("d3d12Device_->GetAdapterLuid()", compositor);
    }

    [Fact]
    public void ExternalBrowserWaitsOffUiPinsCefAndQueuesState()
    {
        var root = FindRepositoryRoot();
        var session = File.ReadAllText(Path.Combine(
            root, "src", "ReactorV.DirectX", "ExternalGpuBrowserSession.cs"));
        var cef = File.ReadAllText(Path.Combine(
            root, "src", "ReactorV.DirectX", "Browser", "CefRuntime.cs"));
        var browser = File.ReadAllText(Path.Combine(
            root, "src", "ReactorV.DirectX", "Browser", "OffscreenBrowser.cs"));
        var native = File.ReadAllText(Path.Combine(
            root, "src", "ReactorV.DirectX", "Native", "NativeAdapterLuidDiscovery.cs"));
        var bootstrapHarness = File.ReadAllText(Path.Combine(
            root, "src", "ReactorV.Harness", "BootstrapHostHarness.cs"));

        Assert.Contains("StartAdapterLuidDiscovery();", session);
        Assert.Contains("new Timer(", session);
        Assert.Contains("AdapterLuidDiscoveryTimeoutMilliseconds", session);
        Assert.Contains("NativeAdapterLuidDiscovery.TryQuery(", session);
        Assert.Contains("var browser = CreateBrowser(adapterLuid);", session);
        Assert.Contains("This callback runs on a Timer/ThreadPool worker", session);
        Assert.Contains("adapter-luid-discovery-timeout", session);
        Assert.Contains("_pendingPostJson.Enqueue(json);", session);
        Assert.Contains("browser.PostJson(_pendingPostJson.Dequeue())", session);
        Assert.DoesNotContain("AttachBrowser(CreateBrowser())", session);

        Assert.Contains("RWUI_QueryTargetAdapterLuid", native);
        Assert.Contains("settings.CefCommandLineArgs[\"use-angle\"] = \"d3d11\"", cef);
        Assert.Contains("settings.CefCommandLineArgs[\"use-adapter-luid\"]", cef);
        Assert.Contains("adapterLuid.Value.ToCefCommandLineValue()", cef);
        Assert.Contains("GpuAdapterLuid? adapterLuid = null", browser);
        Assert.Contains("CefRuntime.EnsureInitialized(", browser);
        Assert.Contains("AdapterConsumerFixture.Start(", bootstrapHarness);
        Assert.Contains("NativeCompositor.StartTest(", bootstrapHarness);
        Assert.True(
            bootstrapHarness.IndexOf(
                "var armed = NativeCompositor.ArmEnhancedHook()",
                StringComparison.Ordinal) <
            bootstrapHarness.IndexOf(
                "var started = NativeCompositor.StartTest(",
                StringComparison.Ordinal),
            "The Enhanced consumer must be armed before its target swap chain is created.");
        Assert.Contains("NativeAdapterLuidDiscovery.TryQuery(processId", bootstrapHarness);
        Assert.Contains("NativeCompositor.StopTest()", bootstrapHarness);
        Assert.Contains("NativeCompositor.Shutdown()", bootstrapHarness);
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate != null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "ReactorV.json")) &&
                Directory.Exists(Path.Combine(candidate.FullName, "native")))
            {
                return candidate.FullName;
            }
            candidate = candidate.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the ReactorV repository root.");
    }
}
