using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ExternalGpuPostAcceptPaintGateTests
{
    [Fact]
    public void Exact_dual_ready_presentation_is_accepted_once()
    {
        var gate = new ExternalGpuPostAcceptPaintGate();

        Assert.True(gate.BeginSession(1));
        Assert.True(gate.BeginPresentation(1, "gbay:home:1"));
        Assert.True(gate.RecordDualBrowserReady(1, "gbay:home:1"));
        Assert.True(gate.TryAcceptPostAcceptPaint(1, "gbay:home:1"));
        Assert.False(gate.TryAcceptPostAcceptPaint(1, "gbay:home:1"));
    }

    [Fact]
    public void Replacement_invalidates_the_previous_paint_proof()
    {
        var gate = new ExternalGpuPostAcceptPaintGate();

        Assert.True(gate.BeginSession(1));
        Assert.True(gate.BeginPresentation(1, "gbay:home:1"));
        Assert.True(gate.RecordDualBrowserReady(1, "gbay:home:1"));
        Assert.True(gate.BeginPresentation(1, "gbay:garage:2"));

        Assert.False(gate.TryAcceptPostAcceptPaint(1, "gbay:home:1"));
        Assert.False(gate.TryAcceptPostAcceptPaint(1, "gbay:garage:2"));
        Assert.True(gate.RecordDualBrowserReady(1, "gbay:garage:2"));
        Assert.True(gate.TryAcceptPostAcceptPaint(1, "gbay:garage:2"));
    }

    [Fact]
    public void Provider_session_generation_is_part_of_the_proof()
    {
        var gate = new ExternalGpuPostAcceptPaintGate();

        Assert.True(gate.BeginSession(1));
        Assert.True(gate.BeginPresentation(1, "gbay:home:1"));
        Assert.True(gate.RecordDualBrowserReady(1, "gbay:home:1"));
        Assert.True(gate.ResetSession(1));
        Assert.True(gate.BeginSession(2));
        Assert.True(gate.BeginPresentation(2, "gbay:home:1"));
        Assert.True(gate.RecordDualBrowserReady(2, "gbay:home:1"));

        Assert.False(gate.TryAcceptPostAcceptPaint(1, "gbay:home:1"));
        Assert.True(gate.TryAcceptPostAcceptPaint(2, "gbay:home:1"));
    }

    [Fact]
    public void Paint_cannot_arrive_before_dual_browser_readiness()
    {
        var gate = new ExternalGpuPostAcceptPaintGate();

        Assert.True(gate.BeginSession(1));
        Assert.True(gate.BeginPresentation(1, "gbay:home:1"));

        Assert.False(gate.TryAcceptPostAcceptPaint(1, "gbay:home:1"));
    }
}
