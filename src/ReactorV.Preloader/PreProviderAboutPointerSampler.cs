using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using RageWebUI.Runtime;
using RageWebUI.Windowing;
using ReactorV.BootstrapInput;
using ReactorV.WebView2Host;

namespace ReactorV.Preloader
{
    /// <summary>
    /// Copies a bounded pointer stream into the passive About WebView before
    /// SHVDN attaches. It deliberately does not install hooks, capture or clip
    /// the cursor, activate the overlay, suppress a game message, or disable a
    /// GTA control. The browser accepts this private stream only on explicitly
    /// marked Reactor-owned About controls.
    /// </summary>
    internal sealed class PreProviderAboutPointerSampler : IDisposable
    {
        private const int VirtualKeyLeftButton = 0x01;
        private static readonly long WindowRefreshTicks =
            Math.Max(1L, Stopwatch.Frequency / 2L);

        private readonly uint _gameProcessId;
        private readonly OverlayWindow _window;
        private readonly Action<string, string?> _trace;
        private readonly Action<float, float, bool, bool> _postPointer;
        private readonly Action _resetPointer;
        private IntPtr _gameWindow;
        private long _nextWindowRefreshAt;
        private bool _active;
        private bool _previousDown;
        private bool _hasCursor;
        private float _lastX;
        private float _lastY;
        private int _edgeSequence;
        private bool _disposed;

        internal PreProviderAboutPointerSampler(
            int gameProcessId,
            OverlayWindow window,
            Action<string, string?> trace,
            Action<float, float, bool, bool>? postPointer = null,
            Action? resetPointer = null)
        {
            if (gameProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(gameProcessId));
            _gameProcessId = (uint)gameProcessId;
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
            _postPointer = postPointer ?? window.PostBootstrapPointerInput;
            _resetPointer = resetPointer ?? window.PostBootstrapPointerReset;
        }

        internal void Poll(
            bool contentReady,
            bool visible,
            string? surface,
            bool providerConnected)
        {
            if (_disposed ||
                !PreProviderAboutInputPolicy.ShouldSample(
                    contentReady,
                    visible,
                    surface,
                    providerConnected,
                    gameForeground: true))
            {
                Reset("host-boundary");
                return;
            }

            RefreshGameWindow();
            if (_gameWindow == IntPtr.Zero ||
                !Win32GameWindowLocator.IsForegroundOrOwnedBy(_gameWindow) ||
                !TryGetClientBounds(_gameWindow, out var bounds) ||
                !GetCursorPos(out var cursor) ||
                !PreProviderAboutInputPolicy.TryNormalize(
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    cursor.X,
                    cursor.Y,
                    out var x,
                    out var y))
            {
                Reset("game-boundary");
                return;
            }

            var buttonState = GetAsyncKeyState(VirtualKeyLeftButton);
            var down = (buttonState & 0x8000) != 0;
            var pressedSinceLastPoll = (buttonState & 0x0001) != 0;
            if (!_active)
            {
                // A button already held while About becomes eligible did not
                // begin inside Reactor. Seed its state but never synthesize a
                // press or later click from that outside edge.
                _active = true;
                _previousDown = down;
                _hasCursor = true;
                _lastX = x;
                _lastY = y;
                _postPointer(
                    x,
                    y,
                    false,
                    false);
                _trace(
                    "preprovider_about_pointer_active",
                    $"game_window=0x{_gameWindow.ToInt64():X}");
                return;
            }

            var button = PreProviderAboutInputPolicy.EvaluateLeftButton(
                eligible: true,
                down,
                pressedSinceLastPoll,
                _previousDown);
            _previousDown = button.NextDown;
            if (!WindowedInputPolicy.ShouldForward(
                    _lastX,
                    _lastY,
                    _hasCursor,
                    x,
                    y,
                    button.Pressed,
                    button.Released,
                    wheelDelta: 0))
                return;

            _hasCursor = true;
            _lastX = x;
            _lastY = y;
            _postPointer(
                x,
                y,
                button.Pressed,
                button.Released);
            if (button.Pressed || button.Released)
            {
                _trace(
                    "preprovider_about_pointer_edge",
                    $"sequence={++_edgeSequence} x={x:F4} y={y:F4} " +
                    $"pressed={button.Pressed} released={button.Released}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Reset("disposed");
        }

        internal void ResetBoundary(string reason) =>
            Reset(string.IsNullOrWhiteSpace(reason) ? "boundary" : reason);

        private void RefreshGameWindow()
        {
            var now = Stopwatch.GetTimestamp();
            if (_gameWindow != IntPtr.Zero && now < _nextWindowRefreshAt)
                return;

            var previous = _gameWindow;
            _gameWindow = Win32GameWindowLocator.Resolve(
                _gameProcessId,
                previous,
                _window.IsHandleCreated ? _window.Handle : IntPtr.Zero,
                out _);
            _nextWindowRefreshAt = now + WindowRefreshTicks;
        }

        private void Reset(string reason)
        {
            if (!_active && !_hasCursor && !_previousDown) return;
            _resetPointer();
            _active = false;
            _previousDown = false;
            _hasCursor = false;
            _lastX = 0f;
            _lastY = 0f;
            _trace("preprovider_about_pointer_reset", $"reason={reason}");
        }

        private static bool TryGetClientBounds(IntPtr window, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (window == IntPtr.Zero || !GetClientRect(window, out var rect))
                return false;
            var origin = new NativePoint();
            if (!ClientToScreen(window, ref origin)) return false;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 1 || height <= 1) return false;
            bounds = new Rectangle(origin.X, origin.Y, width, height);
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    }
}
