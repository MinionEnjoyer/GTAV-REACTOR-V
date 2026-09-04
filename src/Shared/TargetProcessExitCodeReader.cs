using System;
using System.Runtime.InteropServices;

namespace ReactorV
{
    /// <summary>
    /// Retains the minimum Windows process handle needed to read an exit code
    /// after the target has terminated. System.Diagnostics.Process can lose
    /// that information when it was attached by PID rather than launched by
    /// the current process.
    /// </summary>
    internal sealed class TargetProcessExitCodeReader : IDisposable
    {
        private const uint Synchronize = 0x00100000;
        private const uint ProcessQueryLimitedInformation = 0x00001000;
        private const uint StillActive = 259;

        private IntPtr _handle;

        private TargetProcessExitCodeReader(IntPtr handle)
        {
            _handle = handle;
        }

        internal static bool TryOpen(
            int processId,
            out TargetProcessExitCodeReader? reader,
            out string outcome)
        {
            reader = null;
            if (processId <= 0)
            {
                outcome = "invalid-process-id";
                return false;
            }

            var handle = OpenProcess(
                Synchronize | ProcessQueryLimitedInformation,
                false,
                processId);
            if (handle == IntPtr.Zero)
            {
                outcome = "open-failed-win32-" + Marshal.GetLastWin32Error();
                return false;
            }

            reader = new TargetProcessExitCodeReader(handle);
            outcome = "opened";
            return true;
        }

        internal bool TryRead(out int exitCode, out string outcome)
        {
            exitCode = 0;
            if (_handle == IntPtr.Zero)
            {
                outcome = "handle-closed";
                return false;
            }

            if (!GetExitCodeProcess(_handle, out var nativeExitCode))
            {
                outcome = "read-failed-win32-" + Marshal.GetLastWin32Error();
                return false;
            }

            if (nativeExitCode == StillActive)
            {
                outcome = "process-still-active";
                return false;
            }

            exitCode = unchecked((int)nativeExitCode);
            outcome = "read";
            return true;
        }

        public void Dispose()
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(
            IntPtr processHandle,
            out uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
