using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class ExternalGpuBrowserPackageSourceContractTests
    {
        [Fact]
        public void Package_stages_the_preloader_matched_cef_dependency_closure()
        {
            var build = ReadRepositoryFile("build-package.ps1")
                .Replace("\r\n", "\n", StringComparison.Ordinal);

            Assert.Contains(
                "$source = Join-Path $preloaderOutput $file\n" +
                "    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {\n" +
                "        throw \"Expected external GPU browser dependency is missing from Preloader output: $source\"",
                build,
                StringComparison.Ordinal);
            Assert.Contains("$externalGpuBrowserDependencies = @(", build);
            Assert.Contains(
                "External GPU browser dependency does not match the Preloader build: $file",
                build);

            foreach (var file in new[]
            {
                "RageWebUI.DirectX.dll",
                "CefSharp.BrowserSubprocess.Core.dll",
                "CefSharp.BrowserSubprocess.exe",
                "CefSharp.Core.dll",
                "CefSharp.Core.Runtime.dll",
                "CefSharp.dll",
                "CefSharp.OffScreen.dll",
                "chrome_100_percent.pak",
                "chrome_200_percent.pak",
                "chrome_elf.dll",
                "d3dcompiler_47.dll",
                "dxcompiler.dll",
                "dxil.dll",
                "icudtl.dat",
                "libcef.dll",
                "libEGL.dll",
                "libGLESv2.dll",
                "resources.pak",
                "v8_context_snapshot.bin",
                "vk_swiftshader_icd.json",
                "vk_swiftshader.dll",
                "vulkan-1.dll",
            })
            {
                Assert.Contains("'" + file + "'", build, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Package_validates_cef_files_and_locale_in_staging_and_zip()
        {
            var build = ReadRepositoryFile("build-package.ps1");

            Assert.Contains(
                ") + $cefFiles + @(",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "'locales\\en-US.pak'",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "$cefFiles | ForEach-Object { \"plugins/ReactorV/$($_)\" }",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "'plugins/ReactorV/RageWebUI.Native.dll'",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "'plugins/ReactorV/ReactorV.Preloader.json'",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "'plugins/ReactorV/locales/en-US.pak'",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "Get-ChildItem (Join-Path $preloaderOutput 'locales')",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                "Assert-X64PeImage -Path $nativeLibrary",
                build,
                StringComparison.Ordinal);
        }

        [Fact]
        public void Release_gate_runs_the_packaged_external_gpu_path_for_both_apis()
        {
            var build = ReadRepositoryFile("build-package.ps1");
            var qualification = ReadRepositoryFile(
                "tools",
                "qualify-external-gpu-browser.ps1");
            var harness = ReadRepositoryFile(
                "src",
                "ReactorV.Harness",
                "ExternalGpuBrowserConsumerHarness.cs");

            Assert.Contains("external_gpu_browser_shadow =", build);
            Assert.Contains("external_gpu_browser.frame_rate", build);
            Assert.Contains("Packaged externalGpuFrameRate must be between 15 and 60 FPS.", build);
            Assert.Contains("tools\\qualify-external-gpu-browser.ps1", build);
            Assert.Contains(
                "$packagedPreloaderSettings.externalGpuBrowserShadow =",
                build);
            Assert.Contains("[bool]$includeExperimentalRenderHook", build);
            Assert.Contains("submitted_cpu_frames -ne 0", build);
            Assert.Contains("rendered_shared_frames -lt 1", build);

            Assert.Contains("Invoke-ApiQualification -Api 'd3d11'", qualification);
            Assert.Contains("Invoke-ApiQualification -Api 'd3d12'", qualification);
            Assert.Contains("external_gpu_browser_shadow_started", qualification);
            Assert.Contains("external_gpu_browser_shadow_content_ready", qualification);
            Assert.Contains("external_gpu_browser_shadow_stopped", qualification);
            Assert.Contains("external_gpu_browser_shadow_(?:unavailable|faulted|content_unavailable|start_rejected)", qualification);
            Assert.Contains("submitted_cpu_frames = $submitted", qualification);
            Assert.Contains("rendered_shared_frames = $rendered", qualification);

            Assert.Contains("NativeCompositor.ArmEnhancedHook()", harness);
            Assert.True(
                harness.IndexOf(
                    "armed = NativeCompositor.ArmEnhancedHook()",
                    StringComparison.Ordinal) <
                harness.IndexOf(
                    "testStarted = NativeCompositor.StartTest(",
                    StringComparison.Ordinal),
                "The consumer must be armed before the test swap chain is created.");
            Assert.Contains("SubmittedFrames == 0", harness);
            Assert.Contains("RenderedFrames > 0", harness);
            Assert.Contains(
                "TimeSpan.FromMilliseconds(400)",
                harness,
                StringComparison.Ordinal);
            Assert.Contains(
                "continuityTimer.Elapsed >=",
                harness,
                StringComparison.Ordinal);
            Assert.Contains(
                "stats.RenderedFrames <=\n                                    continuityFirstRenderedFrames",
                harness.Replace("\r\n", "\n", StringComparison.Ordinal),
                StringComparison.Ordinal);
            Assert.True(
                harness.IndexOf(
                    "continuityTimer.Elapsed >=",
                    StringComparison.Ordinal) <
                harness.IndexOf(
                    "frameReady.Set();",
                    StringComparison.Ordinal),
                "The package gate must observe continuity before signaling its first frame.");
            Assert.Contains("FrameReady.", harness);
            Assert.Contains("TeardownComplete.", harness);
        }

        [Fact]
        public void Packaged_gate_uses_a_self_test_only_managed_shutdown_signal()
        {
            var qualification = ReadRepositoryFile(
                "tools",
                "qualify-external-gpu-browser.ps1");
            var preloader = ReadRepositoryFile(
                "src",
                "ReactorV.Preloader",
                "Program.cs");

            Assert.Contains("'--self-test'", qualification, StringComparison.Ordinal);
            Assert.Contains(
                "Local\\ReactorV.Preloader.SelfTestStop.$($preloaderProcess.Id)",
                qualification,
                StringComparison.Ordinal);
            Assert.DoesNotContain("WindowCloser", qualification, StringComparison.Ordinal);

            Assert.Contains(
                "options.SelfTest && options.PersistentHost",
                preloader,
                StringComparison.Ordinal);
            Assert.Contains(
                "PreloaderSelfTestNames.StopEvent(",
                preloader,
                StringComparison.Ordinal);
            Assert.Contains(
                "_selfTestStop?.WaitOne(0) == true",
                preloader,
                StringComparison.Ordinal);
            Assert.Contains("\"self_test_stop\"", preloader, StringComparison.Ordinal);
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
            return File.ReadAllText(Path.Combine(current!.FullName, Path.Combine(parts)));
        }
    }
}
