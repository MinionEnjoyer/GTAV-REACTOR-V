using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class DualBrowserInputAuthorityTests
    {
        [Theory]
        [InlineData("about")]
        [InlineData("verifying")]
        [InlineData("setup-status")]
        [InlineData("initializing")]
        public void Bootstrap_surfaces_keep_WebView_as_the_only_pointer_owner(string surface)
        {
            Assert.False(DualBrowserInputAuthority.UseExternalGpuRenderer(true, surface));
        }

        [Fact]
        public void Provider_surface_uses_only_the_external_renderer_when_available()
        {
            Assert.True(DualBrowserInputAuthority.UseExternalGpuRenderer(true, "none"));
            Assert.False(DualBrowserInputAuthority.UseExternalGpuRenderer(false, "none"));
        }
    }
}
