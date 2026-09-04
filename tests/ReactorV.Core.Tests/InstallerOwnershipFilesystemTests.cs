using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace RageWebUI.Core.Tests
{
    public sealed class InstallerOwnershipFilesystemTests
    {
        [Fact]
        public async Task LiveInstallerPreservesUserAndExtensionOwnedFilesByteForByte()
        {
            var repositoryRoot = FindRepositoryRoot();
            var testScript = Path.Combine(repositoryRoot, "tools", "test-install-ownership.ps1");

            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            if (OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
            }
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(testScript);

            using var process = Process.Start(startInfo)
                ?? throw new XunitException("PowerShell ownership test process did not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Preserve the timeout as the actionable test failure.
                }

                throw new XunitException("PowerShell ownership test exceeded the 60-second limit.");
            }

            var output = await standardOutput;
            var error = await standardError;

            Assert.True(
                process.ExitCode == 0,
                $"PowerShell ownership test failed with exit code {process.ExitCode}.{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{output}{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{error}");
            Assert.Contains("OWNERSHIP_FILESYSTEM_PASS", output, StringComparison.Ordinal);
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var marker = Path.Combine(current.FullName, "tools", "test-install-ownership.ps1");
                if (File.Exists(marker))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new XunitException("Could not locate the ReactorV repository root for the ownership test.");
        }
    }
}
