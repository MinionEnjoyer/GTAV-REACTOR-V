using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class PresentationTransferAbortSourceContractTests
{
    [Fact]
    public void TransferTimeoutsUseTheDedicatedExactAbortPath()
    {
        var script = ReadRepositoryFile(
            "src", "ReactorV.Script", "RageWebUiScript.cs");
        var tick = MethodRegion(
            script,
            "private void OnTick(object sender, EventArgs args)",
            "private void OnKeyDown(object sender, KeyEventArgs args)");

        Assert.Contains("var aborted = AbortPresentationTransfer(", tick);
        Assert.Contains("expiredPresentationId,", tick);
        Assert.Contains(
            "AbortPresentationTransfer(\n                    expiredProviderPresentationId,",
            NormalizeNewlines(tick));
        Assert.DoesNotContain(
            "CloseOverlay(\"presentation-ready-timeout\")",
            tick);
        Assert.DoesNotContain(
            "CloseOverlay(\n                    \"provider-paint-timeout\"",
            NormalizeNewlines(tick));
        Assert.Contains(
            "ScheduleDelayedPresentationRecovery(\n                    expiredPresentationId,",
            NormalizeNewlines(tick));
    }

    [Fact]
    public void StaleAbortReturnsBeforeChangingGlobalHostOrInputState()
    {
        var script = ReadRepositoryFile(
            "src", "ReactorV.Script", "RageWebUiScript.cs");
        var abort = MethodRegion(
            script,
            "private bool AbortPresentationTransfer(",
            "private string CurrentHostSurface");

        var exactAcknowledge = abort.IndexOf(
            "ReactorHostApi.AcknowledgeMenuPresentationHidden(",
            StringComparison.Ordinal);
        var staleReturn = abort.IndexOf(
            "if (dismissal == null)",
            StringComparison.Ordinal);
        var inputFailClosed = abort.IndexOf(
            "_inputMode = MenuPresentationPolicy.HiddenInputMode;",
            StringComparison.Ordinal);
        var hide = abort.IndexOf(
            "_overlay.SetVisible(false);",
            StringComparison.Ordinal);

        Assert.True(exactAcknowledge >= 0);
        Assert.True(staleReturn > exactAcknowledge);
        Assert.True(inputFailClosed > staleReturn);
        Assert.True(hide > inputFailClosed);
    }

    [Fact]
    public void InitializerRollbackPrecedesAndAvoidsTheNoFallbackHidePath()
    {
        var script = ReadRepositoryFile(
            "src", "ReactorV.Script", "RageWebUiScript.cs");
        var abort = MethodRegion(
            script,
            "private bool AbortPresentationTransfer(",
            "private string CurrentHostSurface");

        var initializer = abort.IndexOf(
            "if (HostSurfaceMode.IsInitializing(CurrentHostSurface))",
            StringComparison.Ordinal);
        var rollbackReturn = abort.IndexOf(
            "fallback=story-initializer input_mode=game",
            StringComparison.Ordinal);
        var cancelStartup = abort.IndexOf(
            "CancelStartupIntentIfActive(reason);",
            StringComparison.Ordinal);
        var hide = abort.IndexOf(
            "_overlay.SetVisible(false);",
            StringComparison.Ordinal);

        Assert.True(initializer >= 0);
        Assert.True(rollbackReturn > initializer);
        Assert.True(cancelStartup > rollbackReturn);
        Assert.True(hide > cancelStartup);
        Assert.Contains("_managedStartupStatusComplete = false;", abort);
        Assert.Contains("dismissal[\"reason\"] = \"presentation-failed\";", abort);
    }

    [Fact]
    public void BrowserRecognizesFailureAsTerminalAndRestoresTheInitializerIdentity()
    {
        var presentation = ReadRepositoryFile(
            "web", "src", "menu", "presentation.ts");
        var app = ReadRepositoryFile("web", "src", "App.tsx");

        Assert.Contains("'presentation-failed'", presentation);
        Assert.Contains("dismissal.reason !== 'superseded'", app);
        Assert.Contains(
            "dismissal.reason === 'presentation-failed'",
            app);
        Assert.Contains(
            "initializerPresentationWasStoryInitializerRef.current",
            app);
        Assert.Contains("hostSurfaceRef.current = 'initializing'", app);
        Assert.Contains("revokeProviderInput(dismissal.presentationId)", app);
    }

    [Fact]
    public void BrowserGenerationRecoveryRearmsTheExactMenuBeforeTelemetry()
    {
        var script = ReadRepositoryFile(
            "src", "ReactorV.Script", "RageWebUiScript.cs");
        var tick = MethodRegion(
            script,
            "private void OnTickCore()",
            "private void OnKeyDown(object sender, KeyEventArgs args)");
        var recovery = MethodRegion(
            script,
            "private void RecoverDelayedMenuPresentation()",
            "private void CancelStartupIntentIfActive(string reason)");

        Assert.Contains("RecoverDelayedMenuPresentation();", tick);
        Assert.Contains("_nextTelemetryAt", tick);
        Assert.True(
            tick.IndexOf("RecoverDelayedMenuPresentation();", StringComparison.Ordinal) <
            tick.IndexOf("if (Game.GameTime >= _nextTelemetryAt)", StringComparison.Ordinal));
        Assert.Contains("ReactorHostApi.CanMarkMenuPresentationReady(presentationId!)", recovery);
        Assert.Contains("Game.IsPaused", recovery);
        Assert.Contains("TryRestartActiveMenuPresentation(", recovery);
        Assert.Contains("_menuRevealGate.Begin(", recovery);
        Assert.Contains("PostCoreEvent(\n                MenuPresentationPolicy.EventName,", NormalizeNewlines(recovery));
        Assert.Contains("telemetry_independent=true", recovery);

        var dismissed = MethodRegion(
            script,
            "private void PublishActiveMenuDismissed(",
            "private void TraceRuntime(string stage, string? detail = null)");
        Assert.Contains("ClearRecoverableMenuPresentation(", dismissed);

        var generationSync = MethodRegion(
            script,
            "private void SynchronizeBrowserContentGeneration()",
            "private void TryCompleteRuntimeReadyHandoff()");
        Assert.Contains("_runtimeReadyHandoffAttempted = false;", generationSync);

        var browserReady = MethodRegion(
            script,
            "private void MarkBrowserReady(string source, int contentGeneration = 0)",
            "private void SynchronizeBrowserContentGeneration()");
        Assert.Contains("browser-generation-replaced", browserReady);
        Assert.Contains("_menuRevealGate.Cancel();", browserReady);
        Assert.Contains("_runtimeReadyHandoffAttempted = false;", browserReady);

        var retry = MethodRegion(
            script,
            "private bool ScheduleDelayedPresentationRecovery(",
            "private void ClearRecoverableMenuPresentation(");
        Assert.Contains("MaximumDelayedPresentationRecoveryAttempts", retry);
        Assert.Contains("ReactorHostApi.CanMarkMenuPresentationReady", retry);
        Assert.Contains("trigger=timeout telemetry_independent=true", retry);
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string MethodRegion(
        string source,
        string signature,
        string nextSignature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        var end = source.IndexOf(nextSignature, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method signature '{signature}'.");
        Assert.True(end > start, $"Could not find method boundary '{nextSignature}'.");
        return source.Substring(start, end - start);
    }

    private static string ReadRepositoryFile(params string[] path)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate != null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "ReactorV.json")) &&
                Directory.Exists(Path.Combine(candidate.FullName, "src")))
                return candidate.FullName;
            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the ReactorV source root.");
    }
}
