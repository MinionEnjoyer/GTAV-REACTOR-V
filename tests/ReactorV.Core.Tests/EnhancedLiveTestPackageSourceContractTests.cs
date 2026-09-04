using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class EnhancedLiveTestPackageSourceContractTests
    {
        private const string SupportedEnhancedSha256 =
            "0C52864D4521D9C9D441348AA1156958792DDE8825D0297C851753F167336401";

        [Fact]
        public void Experimental_hook_has_a_distinct_fully_gated_non_public_artifact()
        {
            var build = ReadRepositoryFile("build-package.ps1");

            Assert.Contains("[switch]$IncludeExperimentalEnhancedRenderHook", build);
            Assert.Contains(
                "$includeExperimentalRenderHook -and -not $qualityGatesEnabled",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "$qualityGatesEnabled -and -not $includeExperimentalRenderHook",
                build,
                StringComparison.Ordinal);
            Assert.Contains("'enhanced-live-test'", build, StringComparison.Ordinal);
            Assert.Contains(
                "'ReactorV-0.2.0-enhanced-live-test.zip'",
                build,
                StringComparison.Ordinal);
            Assert.Contains("public_release = $false", build, StringComparison.Ordinal);
            Assert.Contains(
                "The Enhanced live-test marker must remain outside every Legacy, public, or developer player package.",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "The Legacy live-test marker must remain outside every Enhanced, public, or developer player package.",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "The Reactor ZIP contains the experimental, unshipped ReactorV.RenderHook.asi.",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "$packagedPreloaderSettings.externalGpuBrowserShadow =",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "[bool]$includeExperimentalRenderHook",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "$expectedExternalGpuBrowserDefault = $includeExperimentalRenderHook",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "$harnessReport.external_gpu_browser.frame_rate",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"externalGpuBrowserShadow\": true",
                ReadRepositoryFile("ReactorV.Preloader.json"),
                StringComparison.Ordinal);
        }

        [Fact]
        public void Live_installer_fails_closed_to_the_pinned_enhanced_build_before_writing()
        {
            var install = ReadRepositoryFile(
                "tools",
                "install-live-test-package.ps1");

            Assert.Contains(
                "[ValidateSet('Enhanced', 'Legacy')]",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "[string]$Edition = 'Enhanced'",
                install,
                StringComparison.Ordinal);
            Assert.Contains("'GTA5_Enhanced.exe'", install, StringComparison.Ordinal);
            Assert.Contains("'1.0.1158.13'", install, StringComparison.Ordinal);
            Assert.Contains(SupportedEnhancedSha256, install, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "'ReactorV-*-enhanced-live-test.zip'",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "Unsupported GTA V $Edition executable for this controlled live test.",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "plugins/ReactorV/ReactorV.EnhancedLiveTest.json",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "The package marker does not authorize this exact $Edition live-test install.",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "The $Edition live-test ZIP contains the other edition's package marker",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "[string]$marker.target_edition -ne $Edition",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "$_.FullName.Replace('\\', '/').Equals(",
                install,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "$zip.GetEntry($liveTestMarkerRelativePath)",
                install,
                StringComparison.Ordinal);

            var executableHashCheck = install.IndexOf(
                "$actualGameSha256 -ne $expectedGameSha256",
                StringComparison.Ordinal);
            var selectedArchiveHash = install.IndexOf(
                "Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256",
                StringComparison.Ordinal);
            var firstMutation = install.IndexOf(
                "New-Item -ItemType Directory -Path $stage, $backup",
                StringComparison.Ordinal);
            var extraction = install.IndexOf(
                "Expand-Archive -LiteralPath $resolvedArchive",
                StringComparison.Ordinal);
            Assert.True(executableHashCheck >= 0);
            Assert.True(selectedArchiveHash > executableHashCheck);
            Assert.True(firstMutation > selectedArchiveHash);
            Assert.True(extraction > selectedArchiveHash);
            Assert.True(firstMutation > executableHashCheck);
            Assert.True(extraction > executableHashCheck);
        }

        [Fact]
        public void Live_installer_mirrors_payload_but_preserves_bridge_and_rolls_back()
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
            Assert.Contains(
                "Rollback also failed",
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
            Assert.Contains(
                "The Reactor package must not own extension artwork",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "$extensionAssetRelativePath = 'ui\\assets\\allin1'",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "Get-OwnedExtensionAssetManifest",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "Test-OwnedExtensionAssetManifest",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "The protected ALLIN1 artwork root may contain only PNG files",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "The Reactor V update changed or removed existing ALLIN1 catalog artwork",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "ExtensionAssetsPreserved = $extensionAssetsPreserved",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "PreloaderConfigurationPreserved = $preloaderConfigurationPreserved",
                install,
                StringComparison.Ordinal);
            Assert.Contains(
                "'plugins/ReactorV'",
                install.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "'ReactorV.Preloader.json'",
                install,
                StringComparison.OrdinalIgnoreCase);
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
