using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ControllerStartupSourceContractTests
{
    [Fact]
    public void Production_host_bounds_creation_and_checks_lifetime_before_publication()
    {
        var host = Read("src/ReactorV.Runtime/CompositionWebViewHost.cs");
        var creation = host.Substring(host.IndexOf("internal async Task EnsureCoreWebView2Async", StringComparison.Ordinal));
        creation = creation.Substring(0, creation.IndexOf("internal bool SetInputParentWindow", StringComparison.Ordinal));
        Assert.Contains("new BoundedResourceCreation<CoreWebView2CompositionController>", creation);
        Assert.Contains("deadline.Expired, _startupLifetime.Token", creation);
        Assert.Contains("late => late.Close()", creation);
        Assert.Contains("if (_controllerStartupAbandoned)", creation);
        Assert.Contains("if (_controllerCreationInProgress)", creation);
        int awaited = creation.IndexOf("controller = await bounded.WaitAsync();", StringComparison.Ordinal);
        int guard = creation.IndexOf("if (_disposed || _owner.IsDisposed", awaited, StringComparison.Ordinal);
        int published = creation.IndexOf("_controller = controller;", StringComparison.Ordinal);
        Assert.True(awaited < guard && guard < published);
        Assert.Contains("_startupLifetime.Cancel();", host);
        Assert.DoesNotContain("Task.Run", creation);
    }

    [Fact]
    public void Deadline_callback_is_diagnostic_only_and_timeout_is_not_retried_as_COM_failure()
    {
        var deadline = Read("src/Shared/ControllerStartupDeadline.cs");
        Assert.Contains("action=diagnostic_only", deadline);
        Assert.DoesNotContain("CoreWebView2", deadline);
        Assert.DoesNotContain("System.Windows.Forms", deadline);
        var window = Read("src/ReactorV.Runtime/OverlayWindow.cs");
        var retry = window.Substring(window.IndexOf("private async Task EnsureControllerWithRetryAsync", StringComparison.Ordinal));
        retry = retry.Substring(0, retry.IndexOf("private async void OnNavigationCompleted", StringComparison.Ordinal));
        Assert.Contains("catch (COMException error)", retry);
        Assert.DoesNotContain("catch (TimeoutException", retry);
        Assert.Contains("webview_initialization_failed", window);
        Assert.Contains("new CompositionWebViewHost(this, _trace)", window);
    }

    private static string Read(string relative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ReactorV.json"))) root = root.Parent;
        return File.ReadAllText(Path.Combine(root?.FullName ?? throw new DirectoryNotFoundException(), relative));
    }
}
