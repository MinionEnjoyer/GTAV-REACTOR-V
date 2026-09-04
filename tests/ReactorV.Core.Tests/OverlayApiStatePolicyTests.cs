using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class OverlayApiStatePolicyTests
{
    [Theory]
    [InlineData(false, "game", true)]
    [InlineData(true, "game", false)]
    [InlineData(true, "menu", true)]
    [InlineData(true, "interactive-menu", true)]
    [InlineData(true, "pointer", true)]
    [InlineData(true, "exclusive", true)]
    [InlineData(true, "unknown", false)]
    public void Visible_surface_requires_explicit_input_ownership(
        bool visible,
        string mode,
        bool expected)
    {
        Assert.Equal(
            expected,
            OverlayApiStatePolicy.CanExposeVisibleSurface(visible, mode));
    }
}
