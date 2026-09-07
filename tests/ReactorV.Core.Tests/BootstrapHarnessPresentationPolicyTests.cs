using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class BootstrapHarnessPresentationPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void Shadow_refresh_retains_only_current_committed_pixels(bool ready, bool committed, bool expected)
    {
        Assert.Equal(expected, BootstrapHarnessPresentationPolicy.UseWebView(
            true, true, true, "none", true, ready, committed));
    }

    [Theory]
    [InlineData(false, true, true, "none", true)]
    [InlineData(true, false, true, "none", true)]
    [InlineData(true, true, false, "none", true)]
    [InlineData(true, true, true, "initializing", true)]
    [InlineData(true, true, true, "passive-hud", true)]
    [InlineData(true, true, true, "none", false)]
    public void Retention_cannot_override_production_hide_disconnect_bootstrap_or_dead_shadow(
        bool enabled, bool visible, bool connected, string surface, bool active)
    {
        Assert.False(BootstrapHarnessPresentationPolicy.UseWebView(
            enabled, visible, connected, surface, active, true, true));
    }
}
