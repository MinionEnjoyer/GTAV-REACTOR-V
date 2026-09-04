using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class LegacyLiveTestPackageSourceContractTests
    {
        private const string SupportedLegacySha256 =
            "677E4E355CFBDB13273B1D992407E3C261B3A108DC4DD5C8A0C4C1DA651802E5";

        [Fact]
        public void Legacy_package_includes_and_validates_the_working_cpu_frame_route_only_for_legacy()
        {
            var build = ReadRepositoryFile("build-package.ps1");
            Assert.Contains("'ReactorV.LegacyCpuFrames.enabled'", build);
            Assert.Contains("$requiredRendererFiles += $legacyCpuFrameMarkerName", build);
            Assert.Contains("$requiredZipEntries += \"plugins/ReactorV/$legacyCpuFrameMarkerName\"", build);
            Assert.Contains("Legacy requires the exact CPU-frame marker", build);
            Assert.Contains("incorrect edition-specific CPU-frame marker count", build);
            Assert.Contains("'ReactorV.LegacyCpuFrameBridge'", build);
        }

        [Fact]
        public void Legacy_hook_has_a_distinct_fully_gated_non_public_artifact()
        {
            var build = ReadRepositoryFile("build-package.ps1");

            Assert.Contains("[switch]$IncludeExperimentalLegacyRenderHook", build);
            Assert.Contains(
                "$includeExperimentalRenderHook -and -not $qualityGatesEnabled",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "$Configuration -eq 'Release' -and -not $SkipTests -and -not $SkipHarness",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "$qualityGatesEnabled -and -not $includeExperimentalRenderHook",
                build,
                StringComparison.Ordinal);
            Assert.Contains("'legacy-live-test'", build, StringComparison.Ordinal);
            Assert.Contains(
                "'ReactorV-0.2.0-legacy-live-test.zip'",
                build,
                StringComparison.Ordinal);
            Assert.Contains("public_release = $false", build, StringComparison.Ordinal);
            Assert.Contains("'GTA5.exe'", build, StringComparison.Ordinal);
            Assert.Contains("'1.0.3889.0'", build, StringComparison.Ordinal);
            Assert.Contains(SupportedLegacySha256, build, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "'ReactorV.LegacyLiveTest.json'",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "Choose exactly one experimental render-hook target: Enhanced or Legacy.",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "The Reactor ZIP contains the experimental, unshipped ReactorV.RenderHook.asi.",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "The Enhanced live-test marker must remain outside every Legacy, public, or developer player package.",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "The Legacy live-test marker must remain outside every Enhanced, public, or developer player package.",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "$expectedExternalGpuBrowserDefault = $includeExperimentalRenderHook",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "'ReactorV.D3D11OverlayRenderer.HotPath'",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "'ReactorV.LegacyHook.Integration'",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "'ReactorV.LegacyHook.ResizeExternalLifecycle'",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "CTest JUnit receipt omitted required qualification case(s)",
                build,
                StringComparison.Ordinal);
        }

        [Fact]
        public void Edition_specific_installer_pins_the_exact_legacy_build_and_rejects_cross_edition_markers()
        {
            var install = ReadRepositoryFile(
                "tools",
                "install-live-test-package.ps1");

            Assert.Contains(
                "[ValidateSet('Enhanced', 'Legacy')]",
                install,
                StringComparison.Ordinal);
            Assert.Contains("'legacy-live-test'", install, StringComparison.Ordinal);
            Assert.Contains(
                "'ReactorV-*-legacy-live-test.zip'",
                install,
                StringComparison.Ordinal);
            Assert.Contains("'GTA5.exe'", install, StringComparison.Ordinal);
            Assert.Contains("'1.0.3889.0'", install, StringComparison.Ordinal);
            Assert.Contains(SupportedLegacySha256, install, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "'plugins/ReactorV/ReactorV.LegacyLiveTest.json'",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "OtherMarker = 'plugins/ReactorV/ReactorV.EnhancedLiveTest.json'",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "The $Edition live-test ZIP contains the other edition's package marker",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "[string]$marker.artifact_kind -ne $expectedArtifactKind",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "[string]$marker.target_edition -ne $Edition",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "[string]$marker.game_executable -ne $expectedGameExecutable",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "([string]$marker.game_sha256).ToUpperInvariant() -ne $expectedGameSha256",
                install,
                StringComparison.Ordinal);
        }

        [Fact]
        public void Selected_archive_is_hashed_before_staging_extraction_or_install_mutation()
        {
            var install = ReadRepositoryFile(
                "tools",
                "install-live-test-package.ps1");

            var resolveArchive = install.IndexOf(
                "$resolvedArchive = Assert-FileSystemPath -Path $Archive -Kind Leaf",
                StringComparison.Ordinal);
            var selectedArchiveHash = install.IndexOf(
                "Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256",
                StringComparison.Ordinal);
            var firstStagingMutation = install.IndexOf(
                "New-Item -ItemType Directory -Path $stage, $backup",
                StringComparison.Ordinal);
            var extraction = install.IndexOf(
                "Expand-Archive -LiteralPath $resolvedArchive",
                StringComparison.Ordinal);
            var targetMutation = install.IndexOf(
                "$mutationStarted = $true",
                StringComparison.Ordinal);

            Assert.True(resolveArchive >= 0);
            Assert.True(selectedArchiveHash > resolveArchive);
            Assert.True(firstStagingMutation > selectedArchiveHash);
            Assert.True(extraction > selectedArchiveHash);
            Assert.True(targetMutation > extraction);
        }

        [Fact]
        public void Edition_specific_installer_is_transactional_and_preserves_the_allin1_bridge()
        {
            var install = ReadRepositoryFile(
                "tools",
                "install-live-test-package.ps1");

            Assert.Contains(
                "The Reactor package must not own the ALLIN1 bridge",
                install,
                StringComparison.Ordinal);
            Assert.Contains("$bridgeHashBefore", install, StringComparison.Ordinal);
            Assert.Contains("$bridgeContractHashBefore", install, StringComparison.Ordinal);
            Assert.Contains("Copy-DirectorySnapshot", install, StringComparison.Ordinal);
            Assert.Contains("Restore-InstallSnapshot", install, StringComparison.Ordinal);
            Assert.Contains("if ($mutationStarted)", install, StringComparison.Ordinal);
            Assert.Contains("Rollback also failed", install, StringComparison.Ordinal);
            Assert.Contains(
                "The Reactor V update changed or removed the existing ALLIN1 Reactor bridge pair.",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "Expanded package file count drifted from ZIP preflight",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "Duplicate or case-colliding path in live-test ZIP",
                install,
                StringComparison.Ordinal);
        }

        private static string ReadRepositoryFile(params string[] parts)
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null &&
                !(File.Exists(Path.Combine(current.FullName, "build-package.ps1")) &&
                  Directory.Exists(Path.Combine(current.FullName, "src"))))
            {
                current = current.Parent;
            }
            Assert.NotNull(current);
            return File.ReadAllText(
                Path.Combine(current!.FullName, Path.Combine(parts)));
        }
    }
}
