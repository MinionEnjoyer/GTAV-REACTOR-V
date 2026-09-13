using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReactorV.Diagnostics.Tests
{
    // Used only inside disposable fixture parents. Never alters a pre-existing job.
    public sealed class FixtureJob : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Limits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr Min, Max;
            public uint Count;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref Limits limits, int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        IntPtr handle;
        public FixtureJob(bool allowBreakaway)
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var limits = new Limits { Flags = 0x2000U | (allowBreakaway ? 0x800U : 0U) };
                if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf(typeof(Limits)))) throw new Win32Exception();
                using (var current = Process.GetCurrentProcess())
                    if (!AssignProcessToJobObject(handle, current.Handle)) throw new Win32Exception();
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
}
