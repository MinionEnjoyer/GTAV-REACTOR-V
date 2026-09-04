using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RageWebUI.Windowing
{
    /// <summary>
    /// A compact, comparable view of the target game's top-level window. The
    /// state deliberately excludes volatile discovery diagnostics so an idle
    /// 250 ms poll does not generate a stream of duplicate log records.
    /// </summary>
    internal readonly struct TargetWindowLifecycleState : IEquatable<TargetWindowLifecycleState>
    {
        internal TargetWindowLifecycleState(
            long windowHandle,
            bool exists,
            bool visible,
            bool minimized,
            bool foreground,
            int clientWidth,
            int clientHeight)
        {
            WindowHandle = windowHandle;
            Exists = exists;
            Visible = visible;
            Minimized = minimized;
            Foreground = foreground;
            ClientWidth = clientWidth;
            ClientHeight = clientHeight;
        }

        internal long WindowHandle { get; }
        internal bool Exists { get; }
        internal bool Visible { get; }
        internal bool Minimized { get; }
        internal bool Foreground { get; }
        internal int ClientWidth { get; }
        internal int ClientHeight { get; }

        public bool Equals(TargetWindowLifecycleState other) =>
            WindowHandle == other.WindowHandle &&
            Exists == other.Exists &&
            Visible == other.Visible &&
            Minimized == other.Minimized &&
            Foreground == other.Foreground &&
            ClientWidth == other.ClientWidth &&
            ClientHeight == other.ClientHeight;

        public override bool Equals(object? value) =>
            value is TargetWindowLifecycleState other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = WindowHandle.GetHashCode();
                hash = (hash * 397) ^ Exists.GetHashCode();
                hash = (hash * 397) ^ Visible.GetHashCode();
                hash = (hash * 397) ^ Minimized.GetHashCode();
                hash = (hash * 397) ^ Foreground.GetHashCode();
                hash = (hash * 397) ^ ClientWidth;
                hash = (hash * 397) ^ ClientHeight;
                return hash;
            }
        }

        internal string Describe() =>
            $"hwnd={(WindowHandle == 0 ? "none" : "0x" + WindowHandle.ToString("X", CultureInfo.InvariantCulture))} " +
            $"exists={Exists} visible={Visible} minimized={Minimized} " +
            $"foreground={Foreground} client={ClientWidth}x{ClientHeight}";
    }

    /// <summary>
    /// Emits exactly one initial record and then records only material target
    /// window state changes. It is intentionally observational: callers retain
    /// their existing process-exit and shutdown behavior.
    /// </summary>
    internal sealed class TargetWindowLifecycleJournal
    {
        private readonly object _sync = new object();
        private readonly Action<string, string?> _trace;
        private TargetWindowLifecycleState _last;
        private bool _hasLast;

        internal TargetWindowLifecycleJournal(Action<string, string?> trace)
        {
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        }

        internal bool Observe(
            TargetWindowLifecycleState state,
            string reason,
            double elapsedMilliseconds,
            string? discoveryDetail = null)
        {
            lock (_sync)
            {
                if (_hasLast && _last.Equals(state))
                {
                    return false;
                }

                var hadPrevious = _hasLast;
                var previous = _last;
                _last = state;
                _hasLast = true;
                var detail =
                    $"reason={LogToken(reason)} " +
                    $"elapsed_ms={elapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} " +
                    state.Describe();
                if (hadPrevious)
                {
                    detail += " previous_" + previous.Describe().Replace(" ", " previous_");
                }
                if (!string.IsNullOrWhiteSpace(discoveryDetail))
                {
                    detail += " discovery={" + SingleLine(discoveryDetail!) + "}";
                }

                _trace(
                    hadPrevious
                        ? "target_window_lifecycle_changed"
                        : "target_window_lifecycle_observed",
                    detail);
                return true;
            }
        }

        internal string DescribeLastState()
        {
            lock (_sync)
            {
                return _hasLast ? _last.Describe() : "window_state=unobserved";
            }
        }

        private static string LogToken(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? "unspecified"
                : SingleLine(value!).Replace(' ', '_').Replace('=', '_');

        private static string SingleLine(string value) =>
            value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    /// <summary>
    /// Win32 edge for the pure lifecycle journal. A previously selected HWND
    /// remains observable after it becomes hidden or is destroyed, which lets
    /// the trace distinguish window teardown from final process exit.
    /// </summary>
    internal sealed class TargetWindowLifecycleProbe
    {
        private readonly uint _processId;
        private readonly IntPtr _excludedWindow;
        private IntPtr _preferredWindow;

        internal TargetWindowLifecycleProbe(uint processId, IntPtr excludedWindow)
        {
            _processId = processId;
            _excludedWindow = excludedWindow;
        }

        internal TargetWindowLifecycleState Capture(out string discoveryDetail)
        {
            var selected = Win32GameWindowLocator.Resolve(
                _processId,
                _preferredWindow,
                _excludedWindow,
                out discoveryDetail);
            if (selected != IntPtr.Zero)
            {
                _preferredWindow = selected;
            }

            var window = _preferredWindow;
            if (window == IntPtr.Zero)
            {
                return new TargetWindowLifecycleState(0, false, false, false, false, 0, 0);
            }

            var exists = IsWindow(window);
            if (!exists)
            {
                return new TargetWindowLifecycleState(
                    window.ToInt64(),
                    false,
                    false,
                    false,
                    false,
                    0,
                    0);
            }

            var width = 0;
            var height = 0;
            if (GetClientRect(window, out var bounds))
            {
                width = Math.Max(0, bounds.Right - bounds.Left);
                height = Math.Max(0, bounds.Bottom - bounds.Top);
            }
            return new TargetWindowLifecycleState(
                window.ToInt64(),
                true,
                IsWindowVisible(window),
                IsIconic(window),
                Win32GameWindowLocator.IsForegroundOrOwnedBy(window),
                width,
                height);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);
    }
}
