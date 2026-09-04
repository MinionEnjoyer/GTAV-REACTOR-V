using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RageWebUI.Windowing
{
    internal static class Win32GameWindowLocator
    {
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;
        private const uint GaRootOwner = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        internal static IntPtr Resolve(
            uint processId,
            IntPtr preferred,
            IntPtr excluded,
            out string detail)
        {
            var foreground = GetForegroundWindow();
            var foregroundRoot = foreground == IntPtr.Zero
                ? IntPtr.Zero
                : GetAncestor(foreground, GaRootOwner);

            if (preferred != IntPtr.Zero)
            {
                GetWindowThreadProcessId(preferred, out var preferredProcessId);
                var preferredWidth = 0;
                var preferredHeight = 0;
                if (GetClientRect(preferred, out var preferredRect))
                {
                    preferredWidth = Math.Max(
                        0,
                        preferredRect.Right - preferredRect.Left);
                    preferredHeight = Math.Max(
                        0,
                        preferredRect.Bottom - preferredRect.Top);
                }
                var preferredStyle = GetWindowLongPtr(
                    preferred,
                    GwlExStyle).ToInt64();
                var preferredCandidate = new GameWindowCandidate
                {
                    Handle = preferred.ToInt64(),
                    ProcessId = preferredProcessId,
                    ClientWidth = preferredWidth,
                    ClientHeight = preferredHeight,
                    Visible = IsWindowVisible(preferred),
                    Minimized = IsIconic(preferred),
                    ToolWindow = (preferredStyle & WsExToolWindow) != 0,
                    Excluded = preferred == excluded,
                    Foreground = preferred == foreground ||
                        preferred == foregroundRoot,
                    Preferred = true,
                };
                if (GameWindowSelectionPolicy.CanReusePreferred(
                        preferredCandidate,
                        processId))
                {
                    detail =
                        $"pid={processId} candidates=preferred-fast-path " +
                        $"selected=0x{preferred.ToInt64():X} " +
                        $"client={preferredWidth}x{preferredHeight} " +
                        "foreground=True preferred=True";
                    return preferred;
                }
            }

            var candidates = new List<GameWindowCandidate>();

            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId != processId)
                {
                    return true;
                }

                var width = 0;
                var height = 0;
                if (GetClientRect(window, out var rect))
                {
                    width = Math.Max(0, rect.Right - rect.Left);
                    height = Math.Max(0, rect.Bottom - rect.Top);
                }

                var extendedStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
                candidates.Add(new GameWindowCandidate
                {
                    Handle = window.ToInt64(),
                    ProcessId = ownerProcessId,
                    ClientWidth = width,
                    ClientHeight = height,
                    Visible = IsWindowVisible(window),
                    Minimized = IsIconic(window),
                    ToolWindow = (extendedStyle & WsExToolWindow) != 0,
                    Excluded = window == excluded,
                    Foreground = window == foreground || window == foregroundRoot,
                    Preferred = window == preferred,
                    ClassName = ReadClassName(window),
                    Title = ReadWindowTitle(window),
                });
                return true;
            }, IntPtr.Zero);

            var selected = GameWindowSelectionPolicy.SelectBest(candidates, processId);
            if (selected == null)
            {
                detail = $"pid={processId} candidates={candidates.Count} selected=none";
                return IntPtr.Zero;
            }

            detail =
                $"pid={processId} candidates={candidates.Count} " +
                $"selected=0x{selected.Handle:X} client={selected.ClientWidth}x{selected.ClientHeight} " +
                $"class={LogValue(selected.ClassName)} title={LogValue(selected.Title)} " +
                $"foreground={selected.Foreground} preferred={selected.Preferred}";
            return new IntPtr(selected.Handle);
        }

        internal static bool IsForegroundOrOwnedBy(IntPtr window) =>
            IsForegroundOrOwnedBy(
                window,
                IntPtr.Zero,
                interactionProcessId: 0,
                interactionCaptureActive: false);

        internal static bool IsForegroundOrOwnedBy(
            IntPtr window,
            IntPtr interactionWindow,
            uint interactionProcessId,
            bool interactionCaptureActive)
        {
            if (window == IntPtr.Zero)
            {
                return false;
            }

            var foreground = GetForegroundWindow();
            if (foreground == window ||
                (foreground != IntPtr.Zero && GetAncestor(foreground, GaRootOwner) == window))
            {
                return true;
            }

            if (foreground == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(window, out var targetProcessId);
            GetWindowThreadProcessId(foreground, out var foregroundProcessId);
            if (GameWindowSelectionPolicy.IsInteractionForegroundProcess(
                targetProcessId,
                foregroundProcessId,
                interactionProcessId,
                interactionCaptureActive))
            {
                return true;
            }

            return interactionCaptureActive &&
                interactionWindow != IntPtr.Zero &&
                (foreground == interactionWindow ||
                 GetAncestor(foreground, GaRootOwner) == interactionWindow);
        }

        internal static string DescribeForegroundRelationship(IntPtr window) =>
            DescribeForegroundRelationship(
                window,
                IntPtr.Zero,
                interactionProcessId: 0,
                interactionCaptureActive: false);

        internal static string DescribeForegroundRelationship(
            IntPtr window,
            IntPtr interactionWindow,
            uint interactionProcessId,
            bool interactionCaptureActive)
        {
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(window, out var targetProcessId);
            GetWindowThreadProcessId(foreground, out var foregroundProcessId);
            var interactionForeground = IsForegroundOrOwnedBy(
                window,
                interactionWindow,
                interactionProcessId,
                interactionCaptureActive);
            return
                $"window=0x{window.ToInt64():X} target_pid={targetProcessId} " +
                $"foreground=0x{foreground.ToInt64():X} foreground_pid={foregroundProcessId} " +
                $"interaction_window=0x{interactionWindow.ToInt64():X} " +
                $"interaction_pid={interactionProcessId} " +
                $"interaction_capture={interactionCaptureActive} " +
                $"interaction_foreground={interactionForeground}";
        }

        private static string ReadClassName(IntPtr window)
        {
            var buffer = new StringBuilder(256);
            return GetClassName(window, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        private static string ReadWindowTitle(IntPtr window)
        {
            var length = Math.Min(512, GetWindowTextLength(window));
            if (length <= 0)
            {
                return string.Empty;
            }
            var buffer = new StringBuilder(length + 1);
            return GetWindowText(window, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        private static string LogValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "-";
            }
            return value!.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '_');
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(
            IntPtr window,
            StringBuilder windowText,
            int maximumCount);
    }
}
