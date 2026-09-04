using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class DualBrowserPresentationReadinessCoordinatorTests
{
    [Fact]
    public void Shadow_required_buffers_webview_then_forwards_only_webview_request()
    {
        var gate = NewRequiredGate("presentation-a");

        var first = gate.Submit(
            1,
            PresentationReadyBrowserRole.WebViewAuthority,
            "presentation-a",
            "web-1",
            "web-payload",
            out var earlyDispatch);
        var second = gate.Submit(
            1,
            PresentationReadyBrowserRole.ExternalGpuShadow,
            "presentation-a",
            "cef-1",
            "cef-payload",
            out var dispatch);

        Assert.Equal(PresentationReadySubmissionStatus.Buffered, first);
        Assert.Null(earlyDispatch);
        Assert.Equal(PresentationReadySubmissionStatus.DispatchReady, second);
        AssertDispatch(dispatch, "presentation-a", "web-1", "web-payload", "cef-1");
    }

    [Fact]
    public void Shadow_required_is_order_independent_and_never_forwards_shadow_payload()
    {
        var gate = NewRequiredGate("presentation-a");

        Assert.Equal(
            PresentationReadySubmissionStatus.Buffered,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.ExternalGpuShadow,
                "presentation-a",
                "cef-1",
                "cef-payload",
                out var earlyDispatch));
        Assert.Null(earlyDispatch);

        Assert.Equal(
            PresentationReadySubmissionStatus.DispatchReady,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.WebViewAuthority,
                "presentation-a",
                "web-1",
                "web-payload",
                out var dispatch));
        AssertDispatch(dispatch, "presentation-a", "web-1", "web-payload", "cef-1");
    }

    [Fact]
    public void Completed_durable_presentation_can_begin_a_fresh_reopen_cycle()
    {
        var gate = NewRequiredGate("presentation-a");
        gate.Submit(
            1,
            PresentationReadyBrowserRole.WebViewAuthority,
            "presentation-a",
            "web-1",
            "web-payload-1",
            out _);
        gate.Submit(
            1,
            PresentationReadyBrowserRole.ExternalGpuShadow,
            "presentation-a",
            "cef-1",
            "cef-payload-1",
            out var first);
        Assert.NotNull(first);

        Assert.True(gate.BeginPresentation(1, "presentation-a"));
        Assert.Equal(
            PresentationReadySubmissionStatus.Buffered,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.ExternalGpuShadow,
                "presentation-a",
                "cef-2",
                "cef-payload-2",
                out _));
        Assert.Equal(
            PresentationReadySubmissionStatus.DispatchReady,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.WebViewAuthority,
                "presentation-a",
                "web-2",
                "web-payload-2",
                out var second));
        AssertDispatch(second, "presentation-a", "web-2", "web-payload-2", "cef-2");
    }

    [Fact]
    public void Roles_must_use_distinct_request_ids()
    {
        var gate = NewRequiredGate("presentation-a");
        Assert.Equal(
            PresentationReadySubmissionStatus.Buffered,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.WebViewAuthority,
                "presentation-a",
                "shared-id",
                "web-payload",
                out _));

        Assert.Equal(
            PresentationReadySubmissionStatus.Ignored,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.ExternalGpuShadow,
                "presentation-a",
                "shared-id",
                "cef-payload",
                out var duplicateDispatch));
        Assert.Null(duplicateDispatch);

        Assert.Equal(
            PresentationReadySubmissionStatus.DispatchReady,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.ExternalGpuShadow,
                "presentation-a",
                "cef-distinct",
                "cef-payload",
                out var dispatch));
        AssertDispatch(
            dispatch,
            "presentation-a",
            "shared-id",
            "web-payload",
            "cef-distinct");
    }

    [Fact]
    public void Shadow_disabled_releases_webview_immediately_and_ignores_shadow()
    {
        var gate = new DualBrowserPresentationReadinessCoordinator<string>();
        Assert.True(gate.BeginSession(1, externalGpuShadowRequired: false));
        Assert.True(gate.BeginPresentation(1, "presentation-a"));

        Assert.Equal(
            PresentationReadySubmissionStatus.Ignored,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.ExternalGpuShadow,
                "presentation-a",
                "cef-1",
                "cef-payload",
                out var ignored));
        Assert.Null(ignored);

        Assert.Equal(
            PresentationReadySubmissionStatus.DispatchReady,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.WebViewAuthority,
                "presentation-a",
                "web-1",
                "web-payload",
                out var dispatch));
        AssertDispatch(dispatch, "presentation-a", "web-1", "web-payload", null);
    }

    [Fact]
    public void Shadow_fault_releases_an_already_buffered_webview_request()
    {
        var gate = NewRequiredGate("presentation-a");
        Assert.Equal(
            PresentationReadySubmissionStatus.Buffered,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.WebViewAuthority,
                "presentation-a",
                "web-1",
                "web-payload",
                out _));

        Assert.True(gate.DisableExternalGpuShadow(1, out var dispatch));
        AssertDispatch(dispatch, "presentation-a", "web-1", "web-payload", null);
        Assert.Null(dispatch!.ResponseAlias);
    }

    [Fact]
    public void Replacement_clears_buffered_requests_and_rejects_stale_presentation()
    {
        var gate = NewRequiredGate("presentation-a");
        gate.Submit(
            1,
            PresentationReadyBrowserRole.WebViewAuthority,
            "presentation-a",
            "web-a",
            "payload-a",
            out _);

        Assert.True(gate.BeginPresentation(1, "presentation-b"));
        Assert.Equal(
            PresentationReadySubmissionStatus.Ignored,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.ExternalGpuShadow,
                "presentation-a",
                "cef-a",
                "stale",
                out var staleDispatch));
        Assert.Null(staleDispatch);

        Assert.Equal(
            PresentationReadySubmissionStatus.Buffered,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.ExternalGpuShadow,
                "presentation-b",
                "cef-b",
                "cef-payload-b",
                out _));
        Assert.Equal(
            PresentationReadySubmissionStatus.DispatchReady,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.WebViewAuthority,
                "presentation-b",
                "web-b",
                "web-payload-b",
                out var dispatch));
        AssertDispatch(dispatch, "presentation-b", "web-b", "web-payload-b", "cef-b");
    }

    [Fact]
    public void Exact_dismissal_clears_pending_readiness_without_erasing_replacement()
    {
        var gate = NewRequiredGate("presentation-a");
        gate.Submit(
            1,
            PresentationReadyBrowserRole.WebViewAuthority,
            "presentation-a",
            "web-a",
            "payload-a",
            out _);

        Assert.True(gate.CancelPresentation(1, "presentation-a"));
        Assert.False(gate.CancelPresentation(1, "presentation-a"));
        Assert.True(gate.BeginPresentation(1, "presentation-b"));
        Assert.False(gate.CancelPresentation(1, "presentation-a"));
        Assert.Equal(
            PresentationReadySubmissionStatus.Buffered,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.WebViewAuthority,
                "presentation-b",
                "web-b",
                "payload-b",
                out _));
    }

    [Fact]
    public void New_session_and_exact_reset_clear_all_previous_state()
    {
        var gate = NewRequiredGate("presentation-a");
        gate.Submit(
            1,
            PresentationReadyBrowserRole.WebViewAuthority,
            "presentation-a",
            "web-a",
            "payload-a",
            out _);

        Assert.True(gate.BeginSession(2, externalGpuShadowRequired: false));
        Assert.False(gate.ResetSession(1));
        Assert.True(gate.BeginPresentation(2, "presentation-b"));
        Assert.Equal(
            PresentationReadySubmissionStatus.Ignored,
            gate.Submit(
                1,
                PresentationReadyBrowserRole.ExternalGpuShadow,
                "presentation-a",
                "cef-a",
                "stale",
                out _));
        Assert.True(gate.ResetSession(2));
        Assert.Equal(
            PresentationReadySubmissionStatus.Ignored,
            gate.Submit(
                2,
                PresentationReadyBrowserRole.WebViewAuthority,
                "presentation-b",
                "web-b",
                "payload-b",
                out var afterReset));
        Assert.Null(afterReset);
        Assert.False(gate.BeginSession(2, externalGpuShadowRequired: false));
    }

    [Fact]
    public async Task Concurrent_arrivals_produce_exactly_one_dispatch()
    {
        var gate = NewRequiredGate("presentation-a");
        var dispatches = new ConcurrentBag<PresentationReadyDispatch<string>>();

        var tasks = Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            var role = index % 2 == 0
                ? PresentationReadyBrowserRole.WebViewAuthority
                : PresentationReadyBrowserRole.ExternalGpuShadow;
            var prefix = role == PresentationReadyBrowserRole.WebViewAuthority
                ? "web"
                : "cef";
            var status = gate.Submit(
                1,
                role,
                "presentation-a",
                $"{prefix}-{index}",
                $"payload-{index}",
                out var dispatch);
            if (status == PresentationReadySubmissionStatus.DispatchReady && dispatch != null)
                dispatches.Add(dispatch);
        }));

        await Task.WhenAll(tasks);

        var only = Assert.Single(dispatches);
        Assert.StartsWith("web-", only.AuthoritativeRequestId);
        Assert.StartsWith("cef-", only.ResponseAlias!.Value.AliasRequestId);
        Assert.NotEqual(
            only.AuthoritativeRequestId,
            only.ResponseAlias.Value.AliasRequestId);
    }

    private static DualBrowserPresentationReadinessCoordinator<string> NewRequiredGate(
        string presentationId)
    {
        var gate = new DualBrowserPresentationReadinessCoordinator<string>();
        Assert.True(gate.BeginSession(1, externalGpuShadowRequired: true));
        Assert.True(gate.BeginPresentation(1, presentationId));
        return gate;
    }

    private static void AssertDispatch(
        PresentationReadyDispatch<string>? dispatch,
        string presentationId,
        string authoritativeRequestId,
        string payload,
        string? aliasRequestId)
    {
        Assert.NotNull(dispatch);
        Assert.Equal(1, dispatch!.ProviderSessionGeneration);
        Assert.Equal(presentationId, dispatch.PresentationId);
        Assert.Equal(authoritativeRequestId, dispatch.AuthoritativeRequestId);
        Assert.Equal(payload, dispatch.AuthoritativePayload);
        if (aliasRequestId == null)
        {
            Assert.Null(dispatch.ResponseAlias);
            return;
        }

        Assert.NotNull(dispatch.ResponseAlias);
        var alias = dispatch.ResponseAlias!.Value;
        Assert.Equal(authoritativeRequestId, alias.AuthoritativeRequestId);
        Assert.Equal(aliasRequestId, alias.AliasRequestId);
        Assert.Equal(PresentationReadyBrowserRole.ExternalGpuShadow, alias.AliasRole);
    }
}
