using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests
{
public sealed class LiveAcceptancePreviewCaptureContractTests
{
    [Fact]
    public void Desktop_identity_bracket_accepts_one_unchanged_presentation()
    {
        var identity = new LiveAcceptancePreviewIdentity(
            "none",
            4,
            7,
            "gbay-42");

        Assert.True(
            LiveAcceptancePreviewCaptureContract.TryValidateDesktopIdentityBracket(
                identity,
                identity,
                out var failure));
        Assert.Equal(string.Empty, failure);
    }

    [Theory]
    [InlineData("about", 4, 7, "", "none", 4, 7, "gbay-42")]
    [InlineData("none", 4, 7, "gbay-42", "none", 5, 7, "gbay-42")]
    [InlineData("none", 4, 7, "gbay-42", "none", 4, 8, "gbay-42")]
    [InlineData("none", 4, 7, "gbay-42", "none", 4, 7, "gbay-43")]
    public void Desktop_identity_bracket_rejects_surface_or_presentation_changes(
        string beforeMode,
        int beforeSurfaceGeneration,
        int beforeControllerGeneration,
        string beforePresentation,
        string afterMode,
        int afterSurfaceGeneration,
        int afterControllerGeneration,
        string afterPresentation)
    {
        var before = new LiveAcceptancePreviewIdentity(
            beforeMode,
            beforeSurfaceGeneration,
            beforeControllerGeneration,
            beforePresentation);
        var after = new LiveAcceptancePreviewIdentity(
            afterMode,
            afterSurfaceGeneration,
            afterControllerGeneration,
            afterPresentation);

        Assert.False(
            LiveAcceptancePreviewCaptureContract.TryValidateDesktopIdentityBracket(
                before,
                after,
                out var failure));
        Assert.Contains("changed", failure);
    }

    [Theory]
    [InlineData(4, "gbay-42", 5, "gbay-42", "surface generation")]
    [InlineData(4, "gbay-42", 4, "gbay-43", "presentation")]
    public void Desktop_identity_bracket_must_match_credited_lifecycle(
        int surfaceGeneration,
        string presentation,
        int lifecycleGeneration,
        string lifecyclePresentation,
        string expectedFailure)
    {
        var identity = new LiveAcceptancePreviewIdentity(
            "none",
            surfaceGeneration,
            7,
            presentation);

        Assert.False(
            LiveAcceptancePreviewCaptureContract.TryValidateDesktopIdentityBracket(
                identity,
                identity,
                lifecycleGeneration,
                lifecyclePresentation,
                out var failure));
        Assert.Contains(expectedFailure, failure);
    }

        [Fact]
        public void InitializerRequiresTwoFramesFromAcknowledgedGeneration()
        {
            var frames = new[]
            {
                Identity("initializing", 17, 3, string.Empty),
                Identity("initializing", 17, 3, string.Empty),
            };

            Assert.True(LiveAcceptancePreviewCaptureContract.TryValidateCorrelatedFrames(
                LiveAcceptanceVisualExpectation.Allin1Preloader,
                "initializing",
                17,
                frames,
                out var failure), failure);
        }

        [Fact]
        public void StaleInitializerGenerationFailsClosed()
        {
            var frames = new[]
            {
                Identity("initializing", 18, 3, string.Empty),
                Identity("initializing", 18, 3, string.Empty),
            };

            Assert.False(LiveAcceptancePreviewCaptureContract.TryValidateCorrelatedFrames(
                LiveAcceptanceVisualExpectation.Allin1Preloader,
                "initializing",
                17,
                frames,
                out var failure));
            Assert.Contains("acknowledged generation", failure);
        }

        [Fact]
        public void PresentationChangeBetweenFramesFailsClosed()
        {
            var frames = new[]
            {
                Identity("none", 21, 5, "menu-1"),
                Identity("none", 21, 5, "menu-2"),
            };

            Assert.False(LiveAcceptancePreviewCaptureContract.TryValidateCorrelatedFrames(
                LiveAcceptanceVisualExpectation.GbayMenu,
                null,
                null,
                frames,
                out var failure));
            Assert.Contains("changed", failure);
        }

        [Fact]
        public void GbayRequiresAnActiveTypedPresentation()
        {
            var frames = new[]
            {
                Identity("none", 21, 5, string.Empty),
                Identity("none", 21, 5, string.Empty),
            };

            Assert.False(LiveAcceptancePreviewCaptureContract.TryValidateCorrelatedFrames(
                LiveAcceptanceVisualExpectation.GbayMenu,
                null,
                null,
                frames,
                out var failure));
            Assert.Contains("active menu presentation", failure);
        }

        [Fact]
        public void EvidenceOnlyCannotBePromotedToBrowserRouteProof()
        {
            Assert.False(LiveAcceptancePreviewCaptureContract.RequiresHostPreview(
                LiveAcceptanceVisualExpectation.EvidenceOnly));
            Assert.True(LiveAcceptancePreviewCaptureContract.RequiresHostPreview(
                LiveAcceptanceVisualExpectation.ReactorAbout));
        }

        private static LiveAcceptancePreviewIdentity Identity(
            string mode,
            int surfaceGeneration,
            int controllerGeneration,
            string presentation) =>
            new LiveAcceptancePreviewIdentity(
                mode,
                surfaceGeneration,
                controllerGeneration,
                presentation);
    }
}
