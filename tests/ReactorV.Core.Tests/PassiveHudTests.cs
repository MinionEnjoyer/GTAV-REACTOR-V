using Newtonsoft.Json.Linq;
using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class PassiveHudTests
    {
        private static JObject Frame() => new JObject {
            ["schema"] = 1, ["visible"] = true, ["kind"] = "speedometer",
            ["speed"] = 42.3, ["units"] = "KMH", ["gear"] = "2", ["manual"] = false, ["notice"] = "",
        };
        [Fact] public void LeaseExpiresWithoutAllowingAnotherOwnerToClearIt()
        {
            var lease = new PassiveHudLease(); var f = Frame();
            Assert.True(lease.Accept("a", f, 0)); f["speed"] = 99;
            Assert.Equal(42.3, lease.Frame!.Value<double>("speed"));
            Assert.False(lease.Accept("b", new JObject { ["schema"] = 1, ["visible"] = false }, 200));
            Assert.True(lease.Active(999)); Assert.False(lease.Active(1000));
            Assert.True(lease.Accept("b", Frame(), 1000)); Assert.Equal("b", lease.Owner);
            lease.Clear(); Assert.False(lease.Active(1001));
        }
        [Theory]
        [InlineData("speed", "NaN")]
        [InlineData("units", "knots")]
        [InlineData("kind", "html")]
        [InlineData("schema", "1")]
        [InlineData("manual", "true")]
        public void InvalidPayloadDoesNotAcquireLease(string key, string value)
        {
            var f = Frame(); f[key] = value; var lease = new PassiveHudLease();
            Assert.False(lease.Accept("a", f, 0)); Assert.Null(lease.Owner);
        }
        [Fact] public void NumericGearIsRejectedRatherThanCoerced()
        {
            var f = Frame(); f["gear"] = 2;
            Assert.False(new PassiveHudLease().Accept("a", f, 0));
        }
        [Fact] public void NoInputLeaseAndBothRenderersRequirePaintProof()
        {
            Assert.False(MenuPresentationPolicy.ShouldAcquireManagedInputLease(false, true, "game"));
            Assert.Equal("passive-hud", HostSurfaceMode.Normalize("passive-hud"));
            Assert.True(HostSurfaceMode.RequiresPaintProof("passive-hud"));
            Assert.True(MenuPresentationPolicy.ShouldRetireInitializerAfterPaint(true, "passive-hud"));
            Assert.False(MenuPresentationPolicy.ShouldRetireInitializerAfterPaint(false, "passive-hud"));
            Assert.True(ExclusiveBrowserPresentationPolicy.IsNativeBootstrapSurface("passive-hud", false));
            Assert.False(OverlayApiStatePolicy.CanExposeVisibleSurface(true, "game")); // Menu API unchanged.
        }
    }
}
