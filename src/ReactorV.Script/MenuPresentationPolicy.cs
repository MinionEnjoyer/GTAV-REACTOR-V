using System;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;

namespace RageWebUI.Script
{
    internal enum ManagedF9EdgeDisposition
    {
        GenericToggle,
        ArmDefaultOwnerInputIntent,
        YieldToDefaultOwner,
    }

    internal static class MenuPresentationPolicy
    {
        internal const string EventName = "menu.presentation";
        internal const string DismissedEventName = "menu.dismissed";
        internal const string InputMode = "interactive-menu";
        internal const string InitialInputMode = "exclusive";
        internal const string HiddenInputMode = "game";
        internal const string StartupIntentProcessIdContextKey =
            "reactorStartupIntentProcessId";

        internal static bool OwnsBack(string mode) =>
            string.Equals(mode, "menu", StringComparison.Ordinal) ||
            string.Equals(mode, InputMode, StringComparison.Ordinal);

        internal static bool UsesPointer(string mode) =>
            string.Equals(mode, "pointer", StringComparison.Ordinal) ||
            string.Equals(mode, "exclusive", StringComparison.Ordinal) ||
            string.Equals(mode, InputMode, StringComparison.Ordinal);

        internal static string PendingPresentationInputMode => HiddenInputMode;

        internal static string ReadyPresentationInputMode => InputMode;

        // The external bootstrap host can be visible before the managed
        // provider owns a menu presentation. That passive status surface must
        // never acquire the managed cursor/input lease merely because its HWND
        // is visible; doing so exposes a cursor and DOM hover sounds even when
        // a stale composition root has not reached the desktop. Managed input
        // begins only after this provider has explicitly requested a surface
        // and the host reports that exact surface as actually presented.
        internal static bool ShouldAcquireManagedInputLease(
            bool overlayRequestedVisible,
            bool overlayPresented,
            string inputMode) =>
            overlayRequestedVisible &&
            overlayPresented &&
            !string.Equals(
                inputMode,
                HiddenInputMode,
                StringComparison.Ordinal);

        internal static bool ShouldRetireInitializerAfterPaint(
            bool matchingPresentationReady,
            string? currentHostSurface) =>
            matchingPresentationReady &&
            HostSurfaceMode.IsInitializing(currentHostSurface);

        internal static bool RequiresHideBeforeDispatch(
            bool overlayRequestedVisible,
            bool overlayVisible,
            string? currentHostSurface)
        {
            // Every Reactor surface is rendered in the same persistent React
            // document. Keep the current committed frame visible while the
            // replacement waits for presentationReady; hiding the HWND first
            // creates a game-only flash and is not an atomic transition.
            _ = overlayRequestedVisible;
            _ = overlayVisible;
            _ = currentHostSurface;
            return false;
        }

        internal static bool ShouldReleaseBootstrapSurface(
            bool overlayRequestedVisible,
            string? currentHostSurface,
            bool defaultMenuIntentActive) =>
            !overlayRequestedVisible &&
            !(defaultMenuIntentActive &&
              HostSurfaceMode.IsInitializing(currentHostSurface)) &&
            !string.Equals(
                HostSurfaceMode.Normalize(currentHostSurface),
                HostSurfaceMode.None,
                StringComparison.Ordinal);

        internal static bool ShouldRefreshManagedStartupStatus(
            bool complete,
            long elapsedMilliseconds,
            long nextRefreshAt,
            string? currentHostSurface) =>
            !complete &&
            elapsedMilliseconds >= nextRefreshAt &&
            HostSurfaceMode.IsInitializing(currentHostSurface);

        // Once the native bootstrap has handed physical F9 to the managed
        // runtime, a registered default owner keeps that key for the whole
        // Story session. GTA can briefly report player control, fade, or
        // cutscene state as unavailable while a menu is opening. Routing F9
        // back to Reactor's generic About surface during that transient state
        // creates two owners for one key press and leaves the typed extension
        // presentation queued behind an unrelated cursor lease.
        internal static bool ShouldDeferPhysicalF9ToExtension(
            bool hasDefaultMenuOwner) =>
            hasDefaultMenuOwner;

        /// <summary>
        /// Resolves one physical F9 edge before the generic Reactor toggle
        /// path is allowed to mutate visibility. A registered default owner
        /// is the sole authority for both opening and closing its menu. The
        /// generic script contributes only the bounded provider-input intent
        /// needed by an opening edge; once that intent or an owned provider
        /// presentation exists, it yields without hiding or otherwise
        /// changing the surface.
        /// </summary>
        internal static ManagedF9EdgeDisposition ResolveManagedF9Edge(
            bool isPhysicalF9,
            bool hasDefaultMenuOwner,
            bool defaultOwnerPresentationOrIntentActive)
        {
            if (!isPhysicalF9 ||
                !ShouldDeferPhysicalF9ToExtension(hasDefaultMenuOwner))
                return ManagedF9EdgeDisposition.GenericToggle;

            return defaultOwnerPresentationOrIntentActive
                ? ManagedF9EdgeDisposition.YieldToDefaultOwner
                : ManagedF9EdgeDisposition.ArmDefaultOwnerInputIntent;
        }

        internal static bool ShouldServiceExtensionMenuQueue(
            bool storyModeReady,
            bool browserReady) =>
            storyModeReady && browserReady;

        internal static bool ShouldReconcileHostHide(
            bool overlayRequestedVisible,
            bool overlayPresented) =>
            overlayRequestedVisible && !overlayPresented;

        internal static bool TryGetStartupIntentProcessId(
            JObject payload,
            out int processId)
        {
            processId = 0;
            if (!(payload?["context"] is JObject context) ||
                context[StartupIntentProcessIdContextKey]?.Type != JTokenType.Integer)
                return false;
            var value = context.Value<long>(StartupIntentProcessIdContextKey);
            if (value <= 0 || value > int.MaxValue) return false;
            processId = (int)value;
            return true;
        }

        internal static bool IsValidPresentationId(string? presentationId)
        {
            if (string.IsNullOrWhiteSpace(presentationId) || presentationId!.Length > 128 ||
                !IsAsciiLetterOrDigit(presentationId[0]))
                return false;

            for (var index = 1; index < presentationId.Length; index++)
            {
                var character = presentationId[index];
                if (!IsAsciiLetterOrDigit(character) &&
                    character != '.' && character != '_' && character != ':' && character != '-')
                    return false;
            }
            return true;
        }

        private static bool IsAsciiLetterOrDigit(char character) =>
            (character >= 'A' && character <= 'Z') ||
            (character >= 'a' && character <= 'z') ||
            (character >= '0' && character <= '9');

        internal static bool TryCreatePayload(JObject record, out JObject? payload)
        {
            payload = null;
            var extensionId = record.Value<string>("extensionId");
            var menuId = record.Value<string>("menuId");
            var presentationId = record.Value<string>("presentationId");
            var context = record["context"] as JObject;
            if (string.IsNullOrWhiteSpace(extensionId) ||
                string.IsNullOrWhiteSpace(menuId) ||
                !IsValidPresentationId(presentationId) ||
                context == null)
                return false;

            payload = new JObject
            {
                ["extensionId"] = extensionId,
                ["menuId"] = menuId,
                ["presentationId"] = presentationId,
                ["context"] = context.DeepClone(),
                ["inputMode"] = InputMode,
            };
            return true;
        }
    }
}
