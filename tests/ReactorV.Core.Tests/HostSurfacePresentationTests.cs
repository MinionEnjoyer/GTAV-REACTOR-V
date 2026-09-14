using Newtonsoft.Json.Linq;
using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class HostSurfacePresentationTests
{
    private static JObject State(int generation = 1) => new() {
        ["visible"] = true, ["ready"] = true,
        ["verifiedHostSurface"] = new HostSurfacePresentation(HostSurfaceMode.PassiveHud, generation).ToJson()
    };

    [Fact]
    public void RoundTripRequiresExactModeAndGeneration()
    {
        var receipt = HostSurfacePresentation.ReadState(JObject.Parse(State().ToString()));
        Assert.NotNull(receipt);
        Assert.True(receipt.Matches(HostSurfaceMode.PassiveHud, 1));
        Assert.False(receipt.Matches(HostSurfaceMode.PassiveHud, 2));
        Assert.False(receipt.Matches(HostSurfaceMode.None, 1));
        Assert.False(receipt.Matches(HostSurfaceMode.About, 1));
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("ready")]
    public void HiddenOrUnavailableCannotCarryPresentationProof(string key)
    {
        var state = State();
        state[key] = false;
        Assert.Null(HostSurfacePresentation.ReadState(state));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648L)]
    public void InvalidGenerationIsRejected(long generation)
    {
        var state = State();
        state["verifiedHostSurface"]!["generation"] = generation;
        Assert.Null(HostSurfacePresentation.ReadState(state));
    }

    [Fact]
    public void OversizedWireIntegerCannotBreakStateReader()
    {
        var state = JObject.Parse("{\"visible\":true,\"ready\":true,\"verifiedHostSurface\":{\"mode\":\"passive-hud\",\"generation\":999999999999999999999999999999}}");
        Assert.Null(HostSurfacePresentation.ReadState(state));
    }

    [Fact]
    public void OlderHostWithoutReceiptFailsClosed()
    {
        var state = State();
        state.Remove("verifiedHostSurface");
        Assert.Null(HostSurfacePresentation.ReadState(state));
    }

    [Fact]
    public void ReplayInitialTest2FailureThenFreshVehicleActivationWithoutGbay()
    {
        var gate = new PassiveHudPresentationGate();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = attempt * 2000;
            Assert.Equal(PassiveHudPresentationAction.Show, gate.Update(now, true, true, false));
            // Composition-qualified HWND is closeable, but no desktop proof exists.
            gate.Update(now + 50, true, true, true, false);
            Assert.Equal(PassiveHudPresentationState.Requested, gate.State);
            Assert.Equal(PassiveHudPresentationAction.Hide, gate.Update(now + 900, true, true, false));
        }
        Assert.Equal(PassiveHudPresentationState.Exhausted, gate.State);
        Assert.Equal(PassiveHudPresentationAction.None, gate.Update(10000, true, true, false));
        gate.Update(11000, false, true, false); // Leave vehicle, not open GBay.
        Assert.Equal(PassiveHudPresentationAction.Show, gate.Update(12000, true, true, false));
        var receipt = HostSurfacePresentation.ReadState(State(3));
        gate.Update(12050, true, true, true, receipt!.Matches(HostSurfaceMode.PassiveHud, 2));
        Assert.Equal(PassiveHudPresentationState.Requested, gate.State);
        gate.Update(12200, true, true, true, receipt.Matches(HostSurfaceMode.PassiveHud, 3));
        Assert.Equal(PassiveHudPresentationState.Presented, gate.State);
        Assert.Equal(PassiveHudPresentationAction.Hide, gate.Update(14000, true, true, false));
    }

    [Fact]
    public void UnverifiedNativeVisibilityCannotExtendRevealDeadline()
    {
        var gate = new PassiveHudPresentationGate();
        gate.Update(0, true, true, false);
        gate.Update(50, true, true, true);
        Assert.Equal(PassiveHudPresentationAction.Hide, gate.Update(4000, true, true, true));
        Assert.NotEqual(PassiveHudPresentationState.Presented, gate.State);
    }
}
