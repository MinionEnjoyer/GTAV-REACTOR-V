using System;
using System.Diagnostics;
using ReactorV;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class TargetProcessExitCodeReaderTests
    {
        [Fact]
        public void Retained_handle_reports_attached_process_exit_code()
        {
            if (!OperatingSystem.IsWindows()) return;

            var shell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "/d /c \"ping -n 2 127.0.0.1 >nul & exit /b 37\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(process);
            Assert.True(TargetProcessExitCodeReader.TryOpen(
                process!.Id,
                out var reader,
                out var openOutcome), openOutcome);
            using (reader)
            {
                process.WaitForExit(10_000);
                Assert.True(process.HasExited);
                Assert.True(reader!.TryRead(out var exitCode, out var readOutcome), readOutcome);
                Assert.Equal(37, exitCode);
            }
        }
    }
}
