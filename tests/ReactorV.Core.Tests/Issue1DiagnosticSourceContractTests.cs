using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class Issue1DiagnosticSourceContractTests
{
    [Fact]
    public void All_three_identity_gated_proofs_use_the_same_scale_policy()
    {
        var overlay = Read("src/ReactorV.Runtime/OverlayWindow.cs");
        Assert.Equal(3, Regex.Matches(overlay, "OverlayPresentationPolicy.CaptureSizeMatchesTarget\\(").Count);
        Assert.Contains("_bootstrapPaintProofRasterizationScale != _webView.RasterizationScale", overlay);
        Assert.Contains("_bootstrapPaintProofWidth = restoreBounds.Width", overlay);
        Assert.Contains("evidence.PaintIdentityMarkerMatched", overlay);
        Assert.Contains("expectedPaintIdentity != 0", overlay);
        Assert.Contains("targetSizeMatches && paintIdentityMarkerMatches", overlay);
    }

    [Fact]
    public void Startup_breadcrumbs_precede_game_calls_and_are_bounded()
    {
        var script = Read("src/ReactorV.Script/RageWebUiScript.cs");
        Assert.True(script.IndexOf("diagnostic_first_tick_enter", StringComparison.Ordinal) <
            script.IndexOf("OnTickCore();", StringComparison.Ordinal));
        Assert.True(script.IndexOf("OnTickCore();", StringComparison.Ordinal) <
            script.IndexOf("diagnostic_first_tick_exit", StringComparison.Ordinal));
        Assert.Contains("_readinessDiagnosticPolls < 16", script);
        Assert.Contains("_nextDiagnosticHeartbeatAt = elapsed + 5000", script);
        var readiness = script.Substring(script.IndexOf("private bool IsPlayableStoryMode()", StringComparison.Ordinal));
        Assert.True(readiness.IndexOf("enter-is-loading", StringComparison.Ordinal) < readiness.IndexOf("Game.IsLoading", StringComparison.Ordinal));
        Assert.True(readiness.IndexOf("enter-player-character", StringComparison.Ordinal) < readiness.IndexOf("Game.Player.Character", StringComparison.Ordinal));
        Assert.True(readiness.IndexOf("enter-is-screen-faded-in", StringComparison.Ordinal) < readiness.IndexOf("Hash.IS_SCREEN_FADED_IN", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_callbacks_only_enqueue_and_worker_drains()
    {
        var hooks = Read("native/src/HookManager.cpp");
        var callbacks = hooks.Substring(hooks.IndexOf("HRESULT STDMETHODCALLTYPE PresentHook", StringComparison.Ordinal));
        Assert.Contains("RecordNativeDiagnostic", callbacks);
        Assert.DoesNotContain("DrainNativeDiagnostics", hooks);
        var worker = Read("native/src/DirectXCompositor.cpp");
        Assert.Contains("DrainNativeDiagnostics();", worker);
        Assert.Contains("diagnostic_worker_heartbeat", worker);
        Assert.Contains("diagnostic_device_status", worker);
    }

    private static string Read(string relative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ReactorV.json"))) root = root.Parent;
        return File.ReadAllText(Path.Combine(root?.FullName ?? throw new DirectoryNotFoundException(), relative));
    }
}
