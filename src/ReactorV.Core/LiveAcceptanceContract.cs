using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core
{
    /// <summary>
    /// Fixed, read-only command vocabulary used by the opt-in live acceptance
    /// runner. The runner is a developer tool and never starts GTA itself.
    /// Keeping the plan typed prevents an arbitrary command string from being
    /// promoted into an in-game action.
    /// </summary>
    public static class LiveAcceptanceContract
    {
        public const int SchemaVersion = 6;
        public const string Scenario = "reactor-allin1-live";
        public const string ExpectedGbayProviderId = "allin1.gbay";
        public const string ExpectedGbayRootMenuId = "home";
        public const int MinimumInstalledHashCount = 7;
        public const int MinimumScreenshotCount = 20;
        public const int MinimumOpenCloseCycles = 2;
        public const int RequiredTopLevelSectionCount = 9;
        public const int MinimumPointerPairCount = RequiredTopLevelSectionCount;
        public const string ExpectedPointerRoute = "bridge-event";

        public const string FrontendAboutToggle = "frontend.about.toggle";
        public const string FrontendAboutClose = "frontend.about.close";
        public const string StoryEarlyMenuToggle = "story.early-menu.toggle";
        public const string MenuSectionMatrix = "menu.section-matrix";
        public const string MenuClickWeaponsTab = "menu.click.weapons-tab";
        public const string MenuSemanticNavigation = "menu.semantic-navigation";
        public const string MenuBackClose = "menu.back-close";
        public const string MenuWeaponCustomizerOpen = "menu.weapon-customizer.open";
        public const string MenuWeaponCustomizerReturn = "menu.weapon-customizer.return";
        public const string MenuClose = "menu.close";
        public const string MenuReopen = "menu.reopen";
        public const string MenuFinalClose = "menu.final-close";

        private static readonly HashSet<string> Commands =
            new HashSet<string>(StringComparer.Ordinal)
            {
                FrontendAboutToggle,
                FrontendAboutClose,
                StoryEarlyMenuToggle,
                MenuSectionMatrix,
                MenuClickWeaponsTab,
                MenuSemanticNavigation,
                MenuBackClose,
                MenuWeaponCustomizerOpen,
                MenuWeaponCustomizerReturn,
                MenuClose,
                MenuReopen,
                MenuFinalClose,
            };

        private static readonly Regex RoutePattern = new Regex(
            @"stage=bootstrap_host_(?:(?<about>about_)|(?<verifying>verify_))?toggle_signaled\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SurfaceReadyPattern = new Regex(
            @"\bstage=bootstrap_host_surface_ready\s+mode=(?<mode>[a-z-]+)\s+generation=(?<generation>[1-9][0-9]*)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex StoryTransitionPattern = new Regex(
            @"\bstage=stage_changed\s+(?<stage>Managed runtime ready - initializing Reactor V\.\.\.|Story Mode ready)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex PointerEdgePattern = new Regex(
            @"\bstage=webview_pointer_edge\s+x=(?<x>-?[0-9]+(?:\.[0-9]+)?)\s+y=(?<y>-?[0-9]+(?:\.[0-9]+)?)\s+pressed=(?<pressed>True|False)\s+released=(?<released>True|False).*?\sroute=(?<route>[a-z-]+)\s+forwarded=(?<forwarded>True|False)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex MenuStatePattern = new Regex(
            @"\bstage=webview_acceptance_menu_state\s+presentation=(?<presentation>[A-Za-z0-9._:-]{1,128})\s+provider=(?<provider>[a-z][a-z0-9._-]{0,63})\s+root_menu=(?<root>[a-z][a-z0-9._-]{0,63})\s+menu=(?<menu>[a-z][a-z0-9._-]{0,63})\s+route=(?<route>[a-z][a-z0-9._-]{0,63})\s+section=(?<section>[a-z][a-z0-9._-]{0,63})\s+payload=(?<payload>ready|loading|error|empty)\s+items=(?<items>[0-9]{1,5})\s+content=(?<content>[0-9]{1,5})\s+actionable=(?<actionable>[0-9]{1,5})\s+status=(?<status>[0-9]{1,5})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsSupportedCommand(string? command) =>
            command != null && Commands.Contains(command);

        public static IReadOnlyCollection<string> OrderedCommands { get; } =
            new[]
            {
                FrontendAboutToggle,
                FrontendAboutClose,
                StoryEarlyMenuToggle,
                MenuSectionMatrix,
                MenuClickWeaponsTab,
                MenuSemanticNavigation,
                MenuBackClose,
                MenuWeaponCustomizerOpen,
                MenuWeaponCustomizerReturn,
                MenuClose,
                MenuReopen,
                MenuFinalClose,
            };

        public static LiveAcceptanceRoute ClassifyBootstrapRoute(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return LiveAcceptanceRoute.None;
            var match = RoutePattern.Match(line!);
            if (!match.Success) return LiveAcceptanceRoute.None;
            if (match.Groups["about"].Success) return LiveAcceptanceRoute.About;
            if (match.Groups["verifying"].Success) return LiveAcceptanceRoute.Verifying;
            return LiveAcceptanceRoute.Initializer;
        }

        public static bool RequiresInPlaceBootstrapPromotion(
            LiveAcceptanceRoute route) =>
            route == LiveAcceptanceRoute.Verifying;

        public static bool RequiresUnresolvedVerificationCleanup(
            LiveAcceptanceRoute openedRoute,
            LiveAcceptanceRoute promotedRoute) =>
            openedRoute == LiveAcceptanceRoute.Verifying &&
            promotedRoute == LiveAcceptanceRoute.None;

        /// <summary>
        /// Classifies passive native lifecycle evidence before the live runner
        /// is allowed to synthesize its first Story-mode F9 edge. ScriptHook's
        /// thread-creation marker is intentionally excluded because Enhanced
        /// emits it on the landing menu. A Story-ready marker is objective but
        /// too late for the required pre-provider initializer check.
        /// </summary>
        public static LiveAcceptanceStoryTransition ClassifyStoryTransition(
            string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return LiveAcceptanceStoryTransition.None;
            var match = StoryTransitionPattern.Match(line!);
            if (!match.Success) return LiveAcceptanceStoryTransition.None;
            return string.Equals(
                    match.Groups["stage"].Value,
                    "Managed runtime ready - initializing Reactor V...",
                    StringComparison.Ordinal)
                ? LiveAcceptanceStoryTransition.ManagedRuntimeStarting
                : LiveAcceptanceStoryTransition.StoryReady;
        }

        public static bool TryParseSurfaceReady(
            string? line,
            out LiveAcceptanceSurfaceReady surface)
        {
            surface = default;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var match = SurfaceReadyPattern.Match(line!);
            if (!match.Success ||
                !int.TryParse(
                    match.Groups["generation"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var generation) ||
                generation <= 0)
                return false;
            surface = new LiveAcceptanceSurfaceReady(
                match.Groups["mode"].Value,
                generation);
            return true;
        }

        public static bool TryParsePointerEdge(
            string? line,
            out LiveAcceptancePointerEdge edge)
        {
            edge = default;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var match = PointerEdgePattern.Match(line!);
            if (!match.Success ||
                !double.TryParse(
                    match.Groups["x"].Value,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var x) ||
                !double.TryParse(
                    match.Groups["y"].Value,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var y) ||
                !bool.TryParse(match.Groups["pressed"].Value, out var pressed) ||
                !bool.TryParse(match.Groups["released"].Value, out var released) ||
                !bool.TryParse(match.Groups["forwarded"].Value, out var forwarded))
                return false;
            edge = new LiveAcceptancePointerEdge(
                x,
                y,
                pressed,
                released,
                match.Groups["route"].Value,
                forwarded);
            return true;
        }

        public static bool IsValidPointerPair(
            LiveAcceptancePointerEdge down,
            LiveAcceptancePointerEdge up,
            double coordinateTolerance = 0.025)
        {
            if (coordinateTolerance < 0.0 ||
                double.IsNaN(coordinateTolerance) ||
                double.IsInfinity(coordinateTolerance))
                return false;
            return down.Pressed && !down.Released &&
                !up.Pressed && up.Released &&
                down.Forwarded && up.Forwarded &&
                string.Equals(
                    down.Route,
                    ExpectedPointerRoute,
                    StringComparison.Ordinal) &&
                string.Equals(down.Route, up.Route, StringComparison.Ordinal) &&
                Math.Abs(down.X - up.X) <= coordinateTolerance &&
                Math.Abs(down.Y - up.Y) <= coordinateTolerance;
        }

        /// <summary>
        /// Parses the bounded, read-only browser observation used by live
        /// acceptance. This message is never a game/API command. The runtime
        /// additionally binds its presentation/provider/root identities to
        /// the active native presentation before writing trace evidence.
        /// </summary>
        public static bool TryParseBrowserMenuState(
            string? json,
            out LiveAcceptanceMenuState state)
        {
            state = default;
            if (string.IsNullOrWhiteSpace(json) || json!.Length > 4096) return false;
            try
            {
                var message = JObject.Parse(json);
                if (!string.Equals(
                        message.Value<string>("kind"),
                        "acceptance",
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        message.Value<string>("command"),
                        "menu-state",
                        StringComparison.Ordinal) ||
                    message.Value<int?>("schemaVersion") != 1)
                    return false;

                var presentationId = message.Value<string>("presentationId");
                var providerId = message.Value<string>("providerId");
                var rootMenuId = message.Value<string>("rootMenuId");
                var menuId = message.Value<string>("menuId");
                var routeId = message.Value<string>("routeId");
                var sectionId = message.Value<string>("sectionId");
                var payloadStatus = message.Value<string>("payloadStatus");
                var itemCount = message.Value<int?>("itemCount");
                var contentItemCount = message.Value<int?>("contentItemCount");
                var actionableItemCount = message.Value<int?>("actionableItemCount");
                var statusItemCount = message.Value<int?>("statusItemCount");
                if (!IsPresentationId(presentationId) ||
                    !IsIdentifier(providerId) ||
                    !IsIdentifier(rootMenuId) ||
                    !IsIdentifier(menuId) ||
                    !IsIdentifier(routeId) ||
                    !IsIdentifier(sectionId) ||
                    !IsPayloadStatus(payloadStatus) ||
                    !IsBoundedCount(itemCount) ||
                    !IsBoundedCount(contentItemCount) ||
                    !IsBoundedCount(actionableItemCount) ||
                    !IsBoundedCount(statusItemCount))
                    return false;

                state = new LiveAcceptanceMenuState(
                    presentationId!,
                    providerId!,
                    rootMenuId!,
                    menuId!,
                    routeId!,
                    sectionId!,
                    payloadStatus!,
                    itemCount!.Value,
                    contentItemCount!.Value,
                    actionableItemCount!.Value,
                    statusItemCount!.Value);
                return state.HasConsistentCounts;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (InvalidCastException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        public static bool TryParseMenuStateTrace(
            string? line,
            out LiveAcceptanceMenuState state)
        {
            state = default;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var match = MenuStatePattern.Match(line!);
            if (!match.Success ||
                !TryParseCount(match, "items", out var itemCount) ||
                !TryParseCount(match, "content", out var contentCount) ||
                !TryParseCount(match, "actionable", out var actionableCount) ||
                !TryParseCount(match, "status", out var statusCount))
                return false;

            state = new LiveAcceptanceMenuState(
                match.Groups["presentation"].Value,
                match.Groups["provider"].Value,
                match.Groups["root"].Value,
                match.Groups["menu"].Value,
                match.Groups["route"].Value,
                match.Groups["section"].Value,
                match.Groups["payload"].Value,
                itemCount,
                contentCount,
                actionableCount,
                statusCount);
            return state.HasConsistentCounts;
        }

        public static bool TryValidateSectionIdentity(
            LiveAcceptanceSectionTarget target,
            LiveAcceptanceMenuState state,
            out string failure)
        {
            failure = string.Empty;
            if (!string.Equals(
                    state.ProviderId,
                    ExpectedGbayProviderId,
                    StringComparison.Ordinal))
                return Fail("section_provider_mismatch", out failure);
            if (!string.Equals(
                    state.RootMenuId,
                    ExpectedGbayRootMenuId,
                    StringComparison.Ordinal))
                return Fail("section_root_menu_mismatch", out failure);
            if (!string.Equals(state.MenuId, target.ExpectedMenuId, StringComparison.Ordinal))
                return Fail("section_menu_mismatch", out failure);
            if (!string.Equals(state.RouteId, target.ExpectedRouteId, StringComparison.Ordinal))
                return Fail("section_route_mismatch", out failure);
            if (!string.Equals(state.SectionId, target.Id, StringComparison.Ordinal))
                return Fail("section_identity_mismatch", out failure);
            return true;
        }

        public static bool TryValidateSectionPayload(
            LiveAcceptanceSectionTarget target,
            LiveAcceptanceMenuState state,
            out string failure)
        {
            failure = string.Empty;
            if (!state.HasConsistentCounts)
                return Fail("section_payload_counts_inconsistent", out failure);
            if (!string.Equals(state.PayloadStatus, "ready", StringComparison.Ordinal))
                return Fail("section_payload_not_ready", out failure);
            if (state.ContentItemCount < target.MinimumContentItemCount)
                return Fail("section_payload_content_missing", out failure);
            if (state.MeaningfulItemCount < target.MinimumMeaningfulItemCount)
                return Fail("section_payload_not_meaningful", out failure);
            return true;
        }

        private static bool IsPayloadStatus(string? value) =>
            string.Equals(value, "ready", StringComparison.Ordinal) ||
            string.Equals(value, "loading", StringComparison.Ordinal) ||
            string.Equals(value, "error", StringComparison.Ordinal) ||
            string.Equals(value, "empty", StringComparison.Ordinal);

        private static bool IsIdentifier(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value!.Length <= 64 &&
            Regex.IsMatch(value, @"^[a-z][a-z0-9._-]*$", RegexOptions.CultureInvariant);

        private static bool IsPresentationId(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value!.Length <= 128 &&
            Regex.IsMatch(
                value,
                @"^[A-Za-z0-9][A-Za-z0-9._:-]*$",
                RegexOptions.CultureInvariant);

        private static bool IsBoundedCount(int? value) =>
            value.HasValue && value.Value >= 0 && value.Value <= 10_000;

        private static bool TryParseCount(Match match, string group, out int value) =>
            int.TryParse(
                match.Groups[group].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value) && value >= 0 && value <= 10_000;

        /// <summary>
        /// Final fail-closed gate for a live receipt. A runner can only claim
        /// success after proving that a fresh GTA process, its real window,
        /// the installed Reactor payload, both bootstrap routes, a paired
        /// native click, independently presented desktop pixels, and repeated
        /// menu cycles were observed. Browser/DOM evidence cannot substitute
        /// for desktop composition evidence here.
        /// </summary>
        public static bool TryValidatePass(
            LiveAcceptanceProof? proof,
            out string failure)
        {
            failure = string.Empty;
            if (proof == null) return Fail("live_proof_missing", out failure);
            if (!proof.FreshGtaProcessObserved)
                return Fail("fresh_gta_process_not_observed", out failure);
            if (!proof.GtaMainWindowObserved)
                return Fail("gta_main_window_not_observed", out failure);
            if (!proof.GtaForegroundObserved)
                return Fail("gta_foreground_not_observed", out failure);
            if (proof.InstalledHashCount < MinimumInstalledHashCount)
                return Fail("installed_hash_set_incomplete", out failure);
            if (!proof.AboutRouteObserved)
                return Fail("about_route_not_observed", out failure);
            if (!proof.StoryTransitionObserved)
                return Fail("story_transition_not_observed", out failure);
            if (!proof.InitializerRouteObserved)
                return Fail("initializer_route_not_observed", out failure);
            if (!proof.AboutSurfaceObserved)
                return Fail("about_surface_not_observed", out failure);
            if (!proof.InitializerSurfaceObserved)
                return Fail("initializer_surface_not_observed", out failure);
            if (!proof.AboutDesktopPixelsObserved)
                return Fail("about_desktop_pixels_not_observed", out failure);
            if (!proof.InitializerDesktopPixelsObserved)
                return Fail("initializer_desktop_pixels_not_observed", out failure);
            if (!proof.GbayDesktopPixelsObserved)
                return Fail("gbay_desktop_pixels_not_observed", out failure);
            if (proof.DesktopPixelEvidenceCount < 3)
                return Fail("desktop_pixel_evidence_incomplete", out failure);
            if (!proof.EarlyInitializerBeforeProviderObserved)
                return Fail("early_initializer_before_provider_not_observed", out failure);
            if (!proof.PointerPairObserved)
                return Fail("pointer_down_up_pair_not_observed", out failure);
            if (proof.PointerPairCount < MinimumPointerPairCount)
                return Fail("pointer_section_matrix_incomplete", out failure);
            if (proof.TopLevelSectionCount < RequiredTopLevelSectionCount)
                return Fail("top_level_section_matrix_incomplete", out failure);
            if (proof.SectionPlumbingCount < RequiredTopLevelSectionCount)
                return Fail("top_level_section_plumbing_incomplete", out failure);
            if (proof.TopLevelSectionIdentityCount < RequiredTopLevelSectionCount)
                return Fail("top_level_section_identity_incomplete", out failure);
            if (proof.TopLevelSectionPayloadCount < RequiredTopLevelSectionCount)
                return Fail("top_level_section_payload_incomplete", out failure);
            if (proof.ForeignForegroundDuringPointer)
                return Fail("foreground_left_gta_during_pointer", out failure);
            if (!proof.SemanticNavigationObserved)
                return Fail("semantic_navigation_not_observed", out failure);
            if (!proof.SemanticAcceptObserved)
                return Fail("semantic_accept_not_observed", out failure);
            if (!proof.SemanticBackObserved)
                return Fail("semantic_back_not_observed", out failure);
            if (!proof.BackCloseObserved)
                return Fail("back_close_not_observed", out failure);
            if (!proof.PauseStateChecked)
                return Fail("pause_state_not_checked", out failure);
            if (proof.PauseMenuLeakObserved)
                return Fail("input_leaked_to_pause_menu", out failure);
            if (!proof.NativeCustomizerHandoffObserved)
                return Fail("native_customizer_handoff_not_observed", out failure);
            if (!proof.NativeCustomizerCameraObserved)
                return Fail("native_customizer_camera_not_observed", out failure);
            if (!proof.NativeCustomizerReturnObserved)
                return Fail("native_customizer_return_not_observed", out failure);
            if (proof.ForegroundObservationCount < 1)
                return Fail("foreground_timeline_empty", out failure);
            if (proof.ScreenshotCount < MinimumScreenshotCount)
                return Fail("screenshot_set_incomplete", out failure);
            if (proof.OpenCloseCycles < MinimumOpenCloseCycles)
                return Fail("open_close_cycles_incomplete", out failure);
            return true;
        }

        private static bool Fail(string reason, out string failure)
        {
            failure = reason;
            return false;
        }

        /// <summary>
        /// The complete fixed section order rendered by the ALLIN1 GBAY shell.
        /// Offsets are CSS-pixel centers from the shell's left edge. The live
        /// runner resolves them against the real client size, including the
        /// shell's 1600px maximum width; a fixed viewport-normalized point is
        /// incorrect at 1440p and wider resolutions.
        /// </summary>
        public static IReadOnlyList<LiveAcceptanceSectionTarget> TopLevelSections { get; } =
            new[]
            {
                new LiveAcceptanceSectionTarget("home", "home", "home", 62.0),
                new LiveAcceptanceSectionTarget("vehicles", "vehicles", "vehicles", 152.0),
                new LiveAcceptanceSectionTarget("weapons", "weapons", "weapons", 269.0),
                new LiveAcceptanceSectionTarget(
                    "customization", "weapons.customize", "weapons.customize", 336.0),
                new LiveAcceptanceSectionTarget("gear", "gear", "gear", 410.0),
                new LiveAcceptanceSectionTarget("garage", "garage", "garage", 528.0),
                new LiveAcceptanceSectionTarget("addons", "addons", "addons", 649.0),
                new LiveAcceptanceSectionTarget(
                    "diagnostics", "diagnostics", "diagnostics", 740.0),
                new LiveAcceptanceSectionTarget("about", "about", "about", 858.0),
            };

        public static LiveAcceptancePoint ResolveSectionPoint(
            LiveAcceptanceSectionTarget target,
            int clientWidth,
            int clientHeight)
        {
            return ResolveSectionPoint(target, clientWidth, clientHeight, 1.0);
        }

        public static LiveAcceptancePoint ResolveSectionPoint(
            LiveAcceptanceSectionTarget target,
            int clientWidth,
            int clientHeight,
            double deviceScale)
        {
            var shell = ResolveShell(clientWidth, clientHeight, deviceScale);
            if (target.ShellOffsetX <= 0.0 || target.ShellOffsetX >= shell.Width)
                throw new ArgumentOutOfRangeException(
                    nameof(target),
                    "The requested section is outside the visible GBAY navigation row.");
            return new LiveAcceptancePoint(
                ((shell.Left + target.ShellOffsetX) * deviceScale) / clientWidth,
                ((shell.Top + 98.5) * deviceScale) / clientHeight);
        }

        public static LiveAcceptancePoint ResolveFirstCatalogCardPoint(
            int clientWidth,
            int clientHeight)
        {
            return ResolveFirstCatalogCardPoint(clientWidth, clientHeight, 1.0);
        }

        public static LiveAcceptancePoint ResolveFirstCatalogCardPoint(
            int clientWidth,
            int clientHeight,
            double deviceScale)
        {
            var shell = ResolveShell(clientWidth, clientHeight, deviceScale);
            return new LiveAcceptancePoint(
                ((shell.Left + 12.0 + Math.Max(1.0, shell.Width - 48.0) / 6.0) *
                    deviceScale) /
                    clientWidth,
                0.45);
        }

        // Retained as a compatibility probe for older callers. New live code
        // must resolve against the actual client size with ResolveSectionPoint.
        public static LiveAcceptancePoint WeaponsTabPoint => ResolveSectionPoint(
            TopLevelSections[2],
            1920,
            1080);

        private static LiveAcceptanceShell ResolveShell(
            int clientWidth,
            int clientHeight,
            double deviceScale)
        {
            if (double.IsNaN(deviceScale) || double.IsInfinity(deviceScale) ||
                deviceScale <= 0.0 || deviceScale > 8.0)
                throw new ArgumentOutOfRangeException(
                    nameof(deviceScale),
                    "The live matrix requires a finite positive device scale.");
            var cssClientWidth = clientWidth / deviceScale;
            var cssClientHeight = clientHeight / deviceScale;
            if (cssClientWidth < 1024.0 || cssClientHeight < 640.0)
                throw new ArgumentOutOfRangeException(
                    nameof(clientWidth),
                    "The live matrix requires a GTA CSS viewport of at least 1024x640.");
            var width = Math.Min(
                Math.Min(cssClientWidth * 0.84, 1600.0),
                cssClientWidth - 32.0);
            var height = Math.Max(
                cssClientHeight * 0.90,
                Math.Min(650.0, cssClientHeight - 32.0));
            return new LiveAcceptanceShell(
                (cssClientWidth - width) / 2.0,
                (cssClientHeight - height) / 2.0,
                width,
                height);
        }

        public static bool IsValidPoint(double x, double y) =>
            !double.IsNaN(x) && !double.IsInfinity(x) &&
            !double.IsNaN(y) && !double.IsInfinity(y) &&
            x >= 0.0 && x <= 1.0 && y >= 0.0 && y <= 1.0;
    }

    public enum LiveAcceptanceRoute
    {
        None = 0,
        About = 1,
        Initializer = 2,
        Verifying = 3,
    }

    public readonly struct LiveAcceptancePoint
    {
        public LiveAcceptancePoint(double x, double y)
        {
            if (!LiveAcceptanceContract.IsValidPoint(x, y))
                throw new ArgumentOutOfRangeException(nameof(x));
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    public readonly struct LiveAcceptanceSectionTarget
    {
        public LiveAcceptanceSectionTarget(
            string id,
            string expectedMenuId,
            string expectedRouteId,
            double shellOffsetX,
            int minimumContentItemCount = 1,
            int minimumMeaningfulItemCount = 1)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A section id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(expectedMenuId))
                throw new ArgumentException("An expected menu id is required.", nameof(expectedMenuId));
            if (string.IsNullOrWhiteSpace(expectedRouteId))
                throw new ArgumentException("An expected route id is required.", nameof(expectedRouteId));
            if (shellOffsetX <= 0.0 || double.IsNaN(shellOffsetX) ||
                double.IsInfinity(shellOffsetX))
                throw new ArgumentOutOfRangeException(nameof(shellOffsetX));
            if (minimumContentItemCount < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumContentItemCount));
            if (minimumMeaningfulItemCount < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumMeaningfulItemCount));
            Id = id;
            ExpectedMenuId = expectedMenuId;
            ExpectedRouteId = expectedRouteId;
            ShellOffsetX = shellOffsetX;
            MinimumContentItemCount = minimumContentItemCount;
            MinimumMeaningfulItemCount = minimumMeaningfulItemCount;
        }

        public string Id { get; }
        public string ExpectedMenuId { get; }
        public string ExpectedRouteId { get; }
        public double ShellOffsetX { get; }
        public int MinimumContentItemCount { get; }
        public int MinimumMeaningfulItemCount { get; }
    }

    public readonly struct LiveAcceptanceMenuState
    {
        public LiveAcceptanceMenuState(
            string presentationId,
            string providerId,
            string rootMenuId,
            string menuId,
            string routeId,
            string sectionId,
            string payloadStatus,
            int itemCount,
            int contentItemCount,
            int actionableItemCount,
            int statusItemCount)
        {
            PresentationId = presentationId ?? throw new ArgumentNullException(nameof(presentationId));
            ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
            RootMenuId = rootMenuId ?? throw new ArgumentNullException(nameof(rootMenuId));
            MenuId = menuId ?? throw new ArgumentNullException(nameof(menuId));
            RouteId = routeId ?? throw new ArgumentNullException(nameof(routeId));
            SectionId = sectionId ?? throw new ArgumentNullException(nameof(sectionId));
            PayloadStatus = payloadStatus ?? throw new ArgumentNullException(nameof(payloadStatus));
            ItemCount = itemCount;
            ContentItemCount = contentItemCount;
            ActionableItemCount = actionableItemCount;
            StatusItemCount = statusItemCount;
        }

        public string PresentationId { get; }
        public string ProviderId { get; }
        public string RootMenuId { get; }
        public string MenuId { get; }
        public string RouteId { get; }
        public string SectionId { get; }
        public string PayloadStatus { get; }
        public int ItemCount { get; }
        public int ContentItemCount { get; }
        public int ActionableItemCount { get; }
        public int StatusItemCount { get; }
        public int MeaningfulItemCount => ActionableItemCount + StatusItemCount;
        public bool HasConsistentCounts =>
            ItemCount >= 0 && ItemCount <= 10_000 &&
            ContentItemCount >= 0 && ContentItemCount <= ItemCount &&
            ActionableItemCount >= 0 && StatusItemCount >= 0 &&
            ActionableItemCount <= ContentItemCount &&
            StatusItemCount <= ContentItemCount &&
            MeaningfulItemCount <= ContentItemCount;
    }

    internal readonly struct LiveAcceptanceShell
    {
        public LiveAcceptanceShell(double left, double top, double width, double height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public double Left { get; }
        public double Top { get; }
        public double Width { get; }
        public double Height { get; }
    }

    public readonly struct LiveAcceptanceSurfaceReady
    {
        public LiveAcceptanceSurfaceReady(string mode, int generation)
        {
            Mode = mode ?? throw new ArgumentNullException(nameof(mode));
            Generation = generation;
        }

        public string Mode { get; }
        public int Generation { get; }
    }

    public readonly struct LiveAcceptancePointerEdge
    {
        public LiveAcceptancePointerEdge(
            double x,
            double y,
            bool pressed,
            bool released,
            string route,
            bool forwarded)
        {
            X = x;
            Y = y;
            Pressed = pressed;
            Released = released;
            Route = route ?? throw new ArgumentNullException(nameof(route));
            Forwarded = forwarded;
        }

        public double X { get; }
        public double Y { get; }
        public bool Pressed { get; }
        public bool Released { get; }
        public string Route { get; }
        public bool Forwarded { get; }
    }

    /// <summary>
    /// Immutable binding to the one GTA client window accepted at the start
    /// of a live run. GTA can expose auxiliary owned windows later; capture
    /// and pointer work must keep using this original handle instead of
    /// asking <see cref="System.Diagnostics.Process.MainWindowHandle"/> again.
    /// </summary>
    public readonly struct LiveAcceptanceWindowBinding
    {
        public LiveAcceptanceWindowBinding(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                throw new ArgumentException("A live GTA window handle is required.", nameof(handle));
            Handle = handle;
        }

        public IntPtr Handle { get; }

        public TResult WithHandle<TResult>(Func<IntPtr, TResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return operation(Handle);
        }
    }

    /// <summary>
    /// Order-independent observation of the two events that prove the
    /// frontend About surface closed. Both events are emitted to the same
    /// preloader session log, but their write order is intentionally not a
    /// contract.
    /// </summary>
    public sealed class LiveAcceptanceAboutCloseObservation
    {
        public string? ToggleEvidence { get; private set; }
        public string? VisibilityEvidence { get; private set; }
        public bool IsComplete => ToggleEvidence != null && VisibilityEvidence != null;

        public bool Observe(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return IsComplete;
            if (line!.IndexOf(
                    "stage=bootstrap_host_native_about_toggle ",
                    StringComparison.Ordinal) >= 0 &&
                line.IndexOf("visible=False", StringComparison.Ordinal) >= 0)
                ToggleEvidence ??= line;
            if (line.IndexOf(
                    "stage=webview_visibility_applied ",
                    StringComparison.Ordinal) >= 0 &&
                line.IndexOf("visible=False", StringComparison.Ordinal) >= 0)
                VisibilityEvidence ??= line;
            return IsComplete;
        }

        public string ToEvidence() => IsComplete
            ? ToggleEvidence + " | " + VisibilityEvidence
            : string.Empty;
    }

    /// <summary>
    /// Order-aware observation of the early Story initializer race. The live
    /// gate only passes when the initializing surface paints before the
    /// managed provider announces readiness.
    /// </summary>
    public sealed class LiveAcceptanceEarlyInitializerObservation
    {
        public string? SurfaceEvidence { get; private set; }
        public string? ProviderReadyEvidence { get; private set; }
        public bool IsComplete => SurfaceEvidence != null;
        public bool ProviderWonRace =>
            ProviderReadyEvidence != null && SurfaceEvidence == null;

        public void Observe(string? line)
        {
            if (string.IsNullOrWhiteSpace(line) || IsComplete || ProviderWonRace)
                return;
            if (line!.IndexOf(
                    "stage=bootstrap_host_provider_ready ",
                    StringComparison.Ordinal) >= 0)
            {
                ProviderReadyEvidence = line;
                return;
            }
            if (LiveAcceptanceContract.TryParseSurfaceReady(line, out var surface) &&
                string.Equals(surface.Mode, "initializing", StringComparison.Ordinal))
                SurfaceEvidence = line;
        }
    }

    public enum LiveAcceptanceStoryTransition
    {
        None,
        ManagedRuntimeStarting,
        StoryReady,
    }

    public sealed class LiveAcceptanceProof
    {
        public bool FreshGtaProcessObserved { get; set; }
        public bool GtaMainWindowObserved { get; set; }
        public bool GtaForegroundObserved { get; set; }
        public int InstalledHashCount { get; set; }
        public bool AboutRouteObserved { get; set; }
        public bool StoryTransitionObserved { get; set; }
        public bool InitializerRouteObserved { get; set; }
        public bool AboutSurfaceObserved { get; set; }
        public bool InitializerSurfaceObserved { get; set; }
        public bool AboutPixelsObserved { get; set; }
        public bool InitializerPixelsObserved { get; set; }
        public bool GbayPixelsObserved { get; set; }
        public bool AboutDesktopPixelsObserved { get; set; }
        public bool InitializerDesktopPixelsObserved { get; set; }
        public bool GbayDesktopPixelsObserved { get; set; }
        public int BrowserCaptureEvidenceCount { get; set; }
        public int DesktopPixelEvidenceCount { get; set; }
        public bool EarlyInitializerBeforeProviderObserved { get; set; }
        public bool PointerPairObserved { get; set; }
        public int PointerPairCount { get; set; }
        public int TopLevelSectionCount { get; set; }
        public int SectionPlumbingCount { get; set; }
        public int TopLevelSectionIdentityCount { get; set; }
        public int TopLevelSectionPayloadCount { get; set; }
        public bool ForeignForegroundDuringPointer { get; set; }
        public bool SemanticNavigationObserved { get; set; }
        public bool SemanticAcceptObserved { get; set; }
        public bool SemanticBackObserved { get; set; }
        public bool BackCloseObserved { get; set; }
        public bool PauseStateChecked { get; set; }
        public bool PauseMenuLeakObserved { get; set; }
        public bool NativeCustomizerHandoffObserved { get; set; }
        public bool NativeCustomizerCameraObserved { get; set; }
        public bool NativeCustomizerReturnObserved { get; set; }
        public int ForegroundObservationCount { get; set; }
        public int ScreenshotCount { get; set; }
        public int OpenCloseCycles { get; set; }
    }
}
