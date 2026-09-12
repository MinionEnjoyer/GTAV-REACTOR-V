using RageWebUI.Core;
using Xunit;

namespace ReactorV.Core.Tests
{
    public sealed class DeferredNativeSurfaceIntentTests
    {
        [Fact]
        public void Delayed_ready_after_old_paint_budget_consumes_the_exact_intent_once()
        {
            var intent = new DeferredNativeSurfaceIntent();
            intent.Defer(HostSurfaceMode.Initializing, generation: 7, providerSessionGeneration: 3);

            Assert.False(intent.TryConsumeReady(HostSurfaceMode.Initializing, 7, 3, nativePresenterReady: false));
            Assert.True(intent.IsPending);
            Assert.True(intent.TryConsumeReady(HostSurfaceMode.Initializing, 7, 3, nativePresenterReady: true));
            Assert.False(intent.IsPending);
            Assert.False(intent.TryConsumeReady(HostSurfaceMode.Initializing, 7, 3, nativePresenterReady: true));
        }

        [Fact]
        public void Cancel_before_ready_prevents_a_late_reveal()
        {
            var intent = new DeferredNativeSurfaceIntent();
            intent.Defer(HostSurfaceMode.PassiveHud, generation: 11, providerSessionGeneration: 5);
            intent.Clear();

            Assert.False(intent.TryConsumeReady(HostSurfaceMode.PassiveHud, 11, 5, nativePresenterReady: true));
        }

        [Fact]
        public void Stale_generation_or_replaced_provider_cannot_consume_current_intent()
        {
            var intent = new DeferredNativeSurfaceIntent();
            intent.Defer(HostSurfaceMode.Initializing, generation: 9, providerSessionGeneration: 4);

            Assert.False(intent.TryConsumeReady(HostSurfaceMode.Initializing, 8, 4, nativePresenterReady: true));
            Assert.False(intent.TryConsumeReady(HostSurfaceMode.Initializing, 9, 5, nativePresenterReady: true));
            Assert.True(intent.IsPending);
        }

        [Fact]
        public void Newer_singleton_intent_replaces_older_request()
        {
            var intent = new DeferredNativeSurfaceIntent();
            intent.Defer(HostSurfaceMode.Initializing, generation: 1, providerSessionGeneration: 1);
            intent.Defer(HostSurfaceMode.PassiveHud, generation: 2, providerSessionGeneration: 1);

            Assert.False(intent.TryConsumeReady(HostSurfaceMode.Initializing, 1, 1, nativePresenterReady: true));
            Assert.True(intent.TryConsumeReady(HostSurfaceMode.PassiveHud, 2, 1, nativePresenterReady: true));
        }

        [Theory]
        [InlineData(false, false, false, false, NativeSurfaceRequestAction.AwaitPaint)]
        [InlineData(true, true, true, false, NativeSurfaceRequestAction.DeferUntilNativeReady)]
        [InlineData(true, true, true, true, NativeSurfaceRequestAction.AwaitPaint)]
        [InlineData(true, false, false, false, NativeSurfaceRequestAction.StopUnavailable)]
        [InlineData(true, true, false, false, NativeSurfaceRequestAction.StopUnavailable)]
        public void Request_phase_defers_the_paint_budget_until_a_live_native_presenter_exists(
            bool nativeSurface, bool sessionExists, bool sessionActive,
            bool presentationReady, NativeSurfaceRequestAction expected)
        {
            Assert.Equal(expected, DeferredNativeSurfaceIntent.EvaluateRequest(
                nativeSurface, sessionExists, sessionActive, presentationReady));
        }
    }
}
