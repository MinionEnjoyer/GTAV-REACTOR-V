using ReactorV.BootstrapHost;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class HostSurfaceIntentPolicyTests
    {
        [Theory]
        [InlineData(true, true, true, true, true, 1)]
        [InlineData(false, true, true, true, true, 2)]
        [InlineData(false, true, true, false, true, 3)]
        [InlineData(false, false, true, false, true, 4)]
        [InlineData(false, false, false, false, true, 5)]
        [InlineData(false, false, false, false, false, 0)]
        public void Coalesced_host_signals_apply_exactly_one_stable_priority(
            bool close,
            bool about,
            bool verify,
            bool initializer,
            bool toggle,
            int expected)
        {
            Assert.Equal(expected, (int)HostSurfaceIntentPolicy.EvaluateSignalBatch(
                close, about, verify, initializer, toggle));
        }

        [Theory]
        [InlineData(true, true, false, true)]
        [InlineData(true, true, true, false)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        public void Close_defers_an_unconsumed_objective_promotion_until_next_poll(
            bool close,
            bool initializer,
            bool runtimeReady,
            bool expected)
        {
            Assert.Equal(expected,
                HostSurfaceIntentPolicy.ShouldDeferInitializerAfterClose(
                    close, initializer, runtimeReady));
        }

        [Fact]
        public void Deferred_objective_promotion_wins_the_next_poll_exactly_once()
        {
            var deferred = HostSurfaceIntentPolicy.ShouldDeferInitializerAfterClose(
                close: true,
                initializerPromotion: true,
                runtimeReady: false);
            Assert.True(deferred);
            Assert.Equal(
                BootstrapHostSignalAction.PromoteInitializer,
                HostSurfaceIntentPolicy.EvaluateSignalBatch(
                    close: false,
                    about: false,
                    verify: false,
                    promoteInitializer: deferred,
                    toggleInitializer: false));
        }

        [Fact]
        public void Only_logical_provider_retirement_carries_the_presentation_handoff()
        {
            Assert.Equal(
                HostSurfaceIntentPolicy.PresentationHandoff,
                HostSurfaceIntentPolicy.RetirementHandoff(hide: false));
            Assert.Null(HostSurfaceIntentPolicy.RetirementHandoff(hide: true));
        }

        [Fact]
        public void Runtime_ready_native_toggle_forwards_intent_without_a_bootstrap_surface()
        {
            Assert.Equal(
                NativeHostToggleAction.ShowBootstrapSurface,
                HostSurfaceIntentPolicy.EvaluateNativeToggle(
                    runtimeReadyLeaseSignaled: false));
            Assert.Equal(
                NativeHostToggleAction.ForwardDefaultMenuIntentHidden,
                HostSurfaceIntentPolicy.EvaluateNativeToggle(
                    runtimeReadyLeaseSignaled: true));
        }

        [Fact]
        public void Bootstrap_surface_f9_is_a_real_show_close_toggle()
        {
            Assert.Equal(
                BootstrapSurfaceToggleAction.Show,
                HostSurfaceIntentPolicy.EvaluateBootstrapToggle(logicallyOpen: false));
            Assert.Equal(
                BootstrapSurfaceToggleAction.Close,
                HostSurfaceIntentPolicy.EvaluateBootstrapToggle(logicallyOpen: true));
        }

        [Fact]
        public void Presentation_preparation_hide_preserves_default_menu_intent()
        {
            Assert.False(HostSurfaceIntentPolicy.ShouldCancelDefaultMenuIntent(
                visible: false,
                HostVisibilityReason.PresentationPreparation));
            Assert.True(HostSurfaceIntentPolicy.ShouldCancelDefaultMenuIntent(
                visible: false,
                HostVisibilityReason.Explicit));
            Assert.False(HostSurfaceIntentPolicy.ShouldCancelDefaultMenuIntent(
                visible: true,
                HostVisibilityReason.Explicit));
        }

        [Fact]
        public void Pending_matching_generation_is_logically_open_for_toggle_close()
        {
            Assert.True(HostSurfaceIntentPolicy.IsLogicallyOpen(
                actuallyVisible: false,
                currentMode: "initializing",
                currentGeneration: 0,
                pendingGeneration: 12,
                pendingMode: "initializing",
                requestedMode: "initializing"));
        }

        [Theory]
        [InlineData(true, "about", 0, 0, "none", "about", true)]
        [InlineData(false, "about", 0, 0, "none", "about", false)]
        [InlineData(false, "about", 8, 0, "none", "about", true)]
        [InlineData(false, "about", 8, 12, "initializing", "about", true)]
        [InlineData(true, "about", 8, 12, "about", "initializing", false)]
        public void Logical_open_state_requires_the_requested_mode(
            bool visible,
            string currentMode,
            int currentGeneration,
            int pendingGeneration,
            string pendingMode,
            string requestedMode,
            bool expected)
        {
            Assert.Equal(expected, HostSurfaceIntentPolicy.IsLogicallyOpen(
                visible,
                currentMode,
                currentGeneration,
                pendingGeneration,
                pendingMode,
                requestedMode));
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void First_story_f9_preserves_the_promoted_initializer(
            bool openingEdgePending,
            bool initializerLogicallyOpen,
            bool expected)
        {
            Assert.Equal(
                expected,
                HostSurfaceIntentPolicy.ShouldConsumeOpeningInitializerToggle(
                    openingEdgePending,
                    initializerLogicallyOpen));
        }

        [Fact]
        public void Expired_paint_deadline_fails_closed_for_a_bounded_retry()
        {
            Assert.Equal(
                HostSurfaceReadyDeadlineAction.FailClosedAndRetry,
                HostSurfaceIntentPolicy.EvaluateReadyDeadline(
                    pendingGeneration: 4,
                    deadlineArmed: true,
                    deadlineExpired: true));
            Assert.Equal(
                HostSurfaceReadyDeadlineAction.None,
                HostSurfaceIntentPolicy.EvaluateReadyDeadline(
                    pendingGeneration: 0,
                    deadlineArmed: true,
                    deadlineExpired: true));
        }

        [Theory]
        [InlineData(true, "verifying", "about", true)]
        [InlineData(true, "verifying", "initializing", true)]
        [InlineData(false, "verifying", "about", false)]
        [InlineData(true, "about", "initializing", true)]
        [InlineData(true, "verifying", "none", false)]
        public void Authoritative_promotions_preserve_the_current_paint(
            bool visible,
            string currentMode,
            string requestedMode,
            bool expected)
        {
            Assert.Equal(expected,
                HostSurfaceIntentPolicy.ShouldPreserveVisibleSurfaceDuringPromotion(
                    visible,
                    currentMode,
                    requestedMode));
        }

        [Theory]
        [InlineData("about", false)]
        [InlineData("verifying", false)]
        [InlineData("initializing", true)]
        [InlineData("none", false)]
        public void Initializer_pixel_proof_parks_a_preserved_surface(
            string requestedMode,
            bool expected)
        {
            Assert.Equal(
                expected,
                HostSurfaceIntentPolicy.ShouldParkForBootstrapPixelProof(
                    requestedMode));
        }

        [Fact]
        public void Initializer_promotion_depends_on_story_evidence_not_about_state()
        {
            Assert.True(HostSurfaceIntentPolicy.ShouldPromoteToInitializer(
                objectiveStoryEvidence: true));
            Assert.False(HostSurfaceIntentPolicy.ShouldPromoteToInitializer(
                objectiveStoryEvidence: false));
        }

        [Theory]
        [InlineData("verifying", true)]
        [InlineData("about", false)]
        [InlineData("initializing", false)]
        [InlineData("none", false)]
        [InlineData("unknown", false)]
        [InlineData(null, false)]
        public void Verification_active_acknowledgement_is_set_for_exactly_one_surface(
            string? mode,
            bool expected)
        {
            Assert.Equal(expected,
                HostSurfaceIntentPolicy.IsVerificationActiveSurface(mode));
        }
    }
}
