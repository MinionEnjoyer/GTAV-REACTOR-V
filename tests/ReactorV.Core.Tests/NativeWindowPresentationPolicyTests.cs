using ReactorV.Windowing;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class NativeWindowPresentationPolicyTests
    {
        [Theory]
        [InlineData(false, 100, 100, 900, 700, false)]
        [InlineData(true, -32000, -32000, -31360, -31640, false)]
        [InlineData(true, 100, 100, 900, 700, true)]
        [InlineData(true, -100, 100, 100, 700, true)]
        [InlineData(true, 1920, 100, 2200, 700, false)]
        [InlineData(true, 100, 100, 100, 700, false)]
        public void VisibleCreationLeaseIsNotMistakenForPlayerPresentation(
            bool nativeVisible,
            int left,
            int top,
            int right,
            int bottom,
            bool expected)
        {
            Assert.Equal(
                expected,
                NativeWindowPresentationPolicy.IsPresentedToDesktop(
                    nativeVisible,
                    left,
                    top,
                    right,
                    bottom,
                    desktopLeft: 0,
                    desktopTop: 0,
                    desktopRight: 1920,
                    desktopBottom: 1080));
        }
    }
}
