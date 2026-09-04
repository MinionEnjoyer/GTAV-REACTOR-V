using System;
using Newtonsoft.Json.Linq;
using RageWebUI.Core.Protocol;

namespace RageWebUI.Core
{
    /// <summary>
    /// Stable contract between the small SHVDN bootstrap and the renderer
    /// implementation stored outside the scripts directory.
    /// </summary>
    public interface IOverlayRuntime : IDisposable
    {
        bool IsVisible { get; }

        string RendererName { get; }

        bool Start();

        void SetVisible(bool visible);

        void PumpInput();

        void UpdateCursor(float normalizedX, float normalizedY, bool pressed, bool released, int wheelDelta);

        void PostResponse(BridgeResponse response);

        void PostEvent(string eventName, JToken? payload);
    }

    /// <summary>
    /// Optional exact-presentation paint boundary. Browser readiness only
    /// proves that a provider prepared a frame; this contract reports when the
    /// renderer has committed that exact presentation for display.
    /// </summary>
    public interface IProviderPresentationCommitRuntime
    {
        bool IsProviderPresentationCommitted(string presentationId);
    }

    /// <summary>
    /// Optional authenticated fallback for a no-redirection composition host.
    /// A physical F9 edge arms one short-lived token; the next presentation
    /// from the registered default-F9 owner binds that token to its exact ID.
    /// The renderer may consume it only after exact paint and composition
    /// commit have passed. Startup and programmatic opens never receive it.
    /// </summary>
    public interface IProviderInputIntentRuntime
    {
        bool ArmProviderInputIntent(ProviderInputIntentToken token);

        bool BindProviderInputIntent(
            int processId,
            long epoch,
            string presentationId);

        void CancelProviderInputIntent(int processId, long epoch);

        bool IsProviderPresentationAuthorizedByUserIntent(
            string presentationId);
    }

    public static class ProviderPresentationCommitContract
    {
        public const int MaximumPresentationIdLength = 128;

        public static bool IsValidPresentationId(string? presentationId) =>
            !string.IsNullOrWhiteSpace(presentationId) &&
            presentationId!.Length <= MaximumPresentationIdLength;

        public static bool Matches(
            string? committedPresentationId,
            string? requestedPresentationId) =>
            IsValidPresentationId(committedPresentationId) &&
            IsValidPresentationId(requestedPresentationId) &&
            string.Equals(
                committedPresentationId,
                requestedPresentationId,
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Optional readiness contract implemented by the persistent bootstrap
    /// host. Consumers use the generation as a handoff token; a browser crash
    /// invalidates it until recovered content is ready again.
    /// </summary>
    public interface IContentGenerationRuntime
    {
        bool TryGetReadyContentGeneration(out int generation);

        RuntimeReadyHandoffState AdvanceRuntimeReadyHandoff(
            int expectedContentGeneration);
    }

    /// <summary>
    /// Read-only view of the native bootstrap surface currently owned by the
    /// persistent host. The managed provider uses this only to make a safe
    /// handoff decision; it cannot select or mutate a native surface through
    /// this contract.
    /// </summary>
    public interface IHostSurfaceRuntime
    {
        string CurrentHostSurface { get; }
    }

    /// <summary>
    /// Process-separated bootstrap hosts keep their surface identity outside
    /// the browser page. Retiring that identity through this typed boundary
    /// prevents a completed Story-mode handoff from revealing stale About or
    /// initializer pixels when a later managed menu closes.
    /// </summary>
    public interface IBootstrapSurfaceRuntime : IHostSurfaceRuntime
    {
        bool BootstrapSurfaceRetirementPending { get; }
        void RetireBootstrapSurface(bool hide);
    }

    /// <summary>
    /// Optional marker for a process-separated host that authors the
    /// generation-bound host.surface close event when visibility is revoked.
    /// Callers must not also post an unversioned browser event for that same
    /// close edge.
    /// </summary>
    public interface IAuthoritativeHostSurfaceRuntime
    {
        bool HasAuthoritativeHostSurfaceBoundary { get; }
    }

    /// <summary>
    /// Optional visibility contract for the persistent bootstrap host. A
    /// provider preparing the browser for a typed replacement is not closing
    /// the user's requested menu, so that hide must not cancel the process-
    /// scoped default-menu intent. Ordinary SetVisible(false) remains an
    /// explicit close for backwards-compatible callers.
    /// </summary>
    public interface IReasonedVisibilityRuntime
    {
        void SetVisible(bool visible, HostVisibilityReason reason);
    }

    /// <summary>
    /// Optional process-authenticated input boundary for an out-of-process
    /// overlay host. A managed game script may keep an already-owned pointer
    /// press alive while this exact host is transiently foreground, but must
    /// still treat every unrelated foreground process as a real Alt+Tab.
    /// </summary>
    public interface IInteractionForegroundRuntime
    {
        bool IsTrustedProviderForeground { get; }
    }

    public enum HostVisibilityReason
    {
        Explicit = 0,
        PresentationPreparation = 1,
    }

    public static class HostSurfaceMode
    {
        public const string None = "none";
        public const string About = "about";
        public const string Verifying = "verifying";
        public const string SetupStatus = "setup-status";
        public const string Initializing = "initializing";

        public static string Normalize(string? mode) =>
            string.Equals(mode, About, StringComparison.Ordinal)
                ? About
                : string.Equals(mode, Verifying, StringComparison.Ordinal)
                    ? Verifying
                    : string.Equals(mode, SetupStatus, StringComparison.Ordinal)
                        ? SetupStatus
                        : string.Equals(mode, Initializing, StringComparison.Ordinal)
                            ? Initializing
                            : None;

        public static bool IsInitializing(string? mode) =>
            string.Equals(mode, Initializing, StringComparison.Ordinal);

        public static bool RequiresPaintProof(string? mode) =>
            string.Equals(mode, About, StringComparison.Ordinal) ||
            string.Equals(mode, Verifying, StringComparison.Ordinal) ||
            string.Equals(mode, SetupStatus, StringComparison.Ordinal) ||
            string.Equals(mode, Initializing, StringComparison.Ordinal);
    }

    public enum RuntimeReadyHandoffState
    {
        Unavailable,
        Pending,
        Signaled,
        StaleGeneration,
        SignalUnavailable,
    }

    public static class RuntimeReadyHandoffPolicy
    {
        public const int LeaseAcknowledgementTimeoutMilliseconds = 1000;

        public static bool HasLeaseAcknowledgementTimedOut(
            long elapsedMilliseconds) =>
            elapsedMilliseconds >= LeaseAcknowledgementTimeoutMilliseconds;
    }
}
