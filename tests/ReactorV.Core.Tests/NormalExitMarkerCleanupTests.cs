using System;
using System.IO;
using ReactorV;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class NormalExitMarkerCleanupTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "reactorv-marker-tests-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Normal_exit_clears_the_allin1_session_marker()
        {
            var scripts = Directory.CreateDirectory(Path.Combine(_root, "scripts"));
            var marker = Path.Combine(scripts.FullName, "ALLIN1_session.lock");
            File.WriteAllText(marker, "active");

            Assert.True(NormalExitMarkerCleanup.TryClearAllin1Marker(
                _root, 0, out var outcome));
            Assert.Equal("marker-cleared", outcome);
            Assert.False(File.Exists(marker));
        }

        [Fact]
        public void Crash_exit_preserves_the_marker_for_recovery()
        {
            var scripts = Directory.CreateDirectory(Path.Combine(_root, "scripts"));
            var marker = Path.Combine(scripts.FullName, "ALLIN1_session.lock");
            File.WriteAllText(marker, "active");

            Assert.False(NormalExitMarkerCleanup.TryClearAllin1Marker(
                _root, unchecked((int)0xC0000005), out var outcome));
            Assert.Equal("preserved-nonzero-exit", outcome);
            Assert.True(File.Exists(marker));
        }

        [Fact]
        public void Missing_marker_is_a_successful_no_op_on_normal_exit()
        {
            Directory.CreateDirectory(_root);

            Assert.True(NormalExitMarkerCleanup.TryClearAllin1Marker(
                _root, 0, out var outcome));
            Assert.Equal("marker-absent", outcome);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch
            {
            }
        }
    }
}
