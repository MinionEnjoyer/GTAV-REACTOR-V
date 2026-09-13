using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ReactorV.Diagnostics
{
    // No injection, elevation, service, scheduled task, parent spoofing, or job-limit changes.
    public static class ProcessLifetime
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inJob);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out ExtendedLimits limits, int size, IntPtr length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        public static bool InJob(Process process)
        {
            bool result;
            if (!IsProcessInJob(process.Handle, IntPtr.Zero, out result)) throw new Win32Exception();
            return result;
        }
        public static uint CurrentJobFlags()
        {
            using (var current = Process.GetCurrentProcess()) if (!InJob(current)) return 0;
            ExtendedLimits limits;
            if (!QueryInformationJobObject(IntPtr.Zero, 9, out limits, Marshal.SizeOf(typeof(ExtendedLimits)), IntPtr.Zero))
                throw new Win32Exception();
            // Only the immediate job is returned. StartDetached verifies the child's actual membership too.
            return limits.Basic.Flags;
        }
        public static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid argument.");
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                result.Append(c);
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        public static Process StartDetached(string executable, string[] arguments, string directory)
        {
            using (var current = Process.GetCurrentProcess())
                if (InJob(current) && (CurrentJobFlags() & (0x800U | 0x1000U)) == 0)
                    throw new InvalidOperationException("The parent job does not permit independent collectors. Launch the collector manually outside this host; no fallback or job-limit change attempted.");
            var command = new StringBuilder(Quote(executable));
            foreach (string arg in arguments) command.Append(' ').Append(Quote(arg));
            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Flags = 1, ShowWindow = 0 };
            ProcessInfo info;
            // Suspended until membership is checked; no inherited console or handles.
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                0x01000000U | 0x08000000U | 0x00000004U, IntPtr.Zero, directory, ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Independent collector launch refused; no fallback attempted.");
            Process retained = null;
            bool resumed = false;
            try
            {
                bool inJob;
                if (!IsProcessInJob(info.Process, IntPtr.Zero, out inJob)) throw new Win32Exception();
                if (inJob) throw new InvalidOperationException("Child is still job-bound. Refusing to report a durable launch.");
                retained = Process.GetProcessById(info.ProcessId);
                var handle = retained.Handle;
                if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
                resumed = true;
                return retained;
            }
            finally
            {
                if (!resumed)
                {
                    // Only our own never-resumed child, not GTA or an already-running collector.
                    TerminateProcess(info.Process, 125);
                    if (retained != null) retained.Dispose();
                }
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }
        }
    }
}
