using System.Collections.Generic;
using RageWebUI.Windowing;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class TargetWindowLifecycleJournalTests
    {
        [Fact]
        public void Identical_poll_states_are_written_only_once()
        {
            var records = new List<(string Stage, string? Detail)>();
            var journal = new TargetWindowLifecycleJournal(
                (stage, detail) => records.Add((stage, detail)));
            var visible = State(0x1234, exists: true, visible: true);

            Assert.True(journal.Observe(visible, "attached", 10.0, "selected=0x1234"));
            Assert.False(journal.Observe(visible, "poll", 250.0, "selected=0x1234"));
            Assert.False(journal.Observe(visible, "poll", 500.0, "selected=0x1234"));

            var record = Assert.Single(records);
            Assert.Equal("target_window_lifecycle_observed", record.Stage);
            Assert.Contains("reason=attached", record.Detail);
            Assert.Contains("hwnd=0x1234", record.Detail);
            Assert.Contains("visible=True", record.Detail);
        }

        [Fact]
        public void Hidden_and_destroyed_transitions_are_each_written_once()
        {
            var records = new List<(string Stage, string? Detail)>();
            var journal = new TargetWindowLifecycleJournal(
                (stage, detail) => records.Add((stage, detail)));

            journal.Observe(State(0xCAFE, true, true), "attached", 10.0);
            journal.Observe(State(0xCAFE, true, false), "poll", 20.0);
            journal.Observe(State(0xCAFE, true, false), "poll", 30.0);
            journal.Observe(State(0xCAFE, false, false), "process-exit-poll", 40.0);
            journal.Observe(State(0xCAFE, false, false), "poll", 50.0);

            Assert.Equal(3, records.Count);
            Assert.Equal("target_window_lifecycle_observed", records[0].Stage);
            Assert.Equal("target_window_lifecycle_changed", records[1].Stage);
            Assert.Contains("visible=False", records[1].Detail);
            Assert.Equal("target_window_lifecycle_changed", records[2].Stage);
            Assert.Contains("exists=False", records[2].Detail);
        }

        [Fact]
        public void Last_state_can_be_attached_to_process_exit_signal()
        {
            var journal = new TargetWindowLifecycleJournal((_, __) => { });
            journal.Observe(State(0xBEEF, true, false), "poll", 75.0);

            var description = journal.DescribeLastState();

            Assert.Contains("hwnd=0xBEEF", description);
            Assert.Contains("exists=True", description);
            Assert.Contains("visible=False", description);
            Assert.Contains("client=1920x1080", description);
        }

        private static TargetWindowLifecycleState State(
            long handle,
            bool exists,
            bool visible) =>
            new TargetWindowLifecycleState(
                handle,
                exists,
                visible,
                minimized: false,
                foreground: visible,
                clientWidth: exists ? 1920 : 0,
                clientHeight: exists ? 1080 : 0);
    }
}
