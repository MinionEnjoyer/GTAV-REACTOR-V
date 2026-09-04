using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using RageWebUI.Shared;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Chooses one visual evidence source for an entire harness run. The
    /// choice is made before Reactor becomes visible by sampling a known solid
    /// synthetic GTA client. A black product frame can therefore never cause
    /// a mid-run source switch and escape the release gate.
    /// </summary>
    internal sealed class HarnessVisualCaptureSession : IDisposable
    {
        private readonly Color _hostColor;
        private readonly string _evidenceDirectory;
        private readonly string _sourceLog;
        private bool _qualified;
        private bool _usePrintWindow;
        private int _captureSequence;
        private bool _executionStateLeased;

        private HarnessVisualCaptureSession(
            Color hostColor,
            string evidenceDirectory)
        {
            _hostColor = hostColor;
            _evidenceDirectory = evidenceDirectory;
            Directory.CreateDirectory(_evidenceDirectory);
            _sourceLog = Path.Combine(
                _evidenceDirectory,
                "visual-capture-source.log");
            if (File.Exists(_sourceLog)) File.Delete(_sourceLog);
        }

        internal string Source => _usePrintWindow
            ? "window-print"
            : "desktop-gdi";

        internal static HarnessVisualCaptureSession Enable(
            Color hostColor,
            string evidenceDirectory) =>
            new HarnessVisualCaptureSession(
                hostColor,
                evidenceDirectory);

        internal void QualifyDesktop(Form host)
        {
            if (_qualified)
                throw new InvalidOperationException(
                    "The visual capture source was already qualified.");

            // Sample GDI before acquiring the scoped execution-state lease.
            // On a sleeping/offline console desktop SetThreadExecutionState
            // can make a simple GDI host probe appear healthy without making
            // DirectComposition/WebView pixels available. Source selection
            // must therefore use the untouched desktop state.
            using var initialDesktop = CaptureDesktop(host);
            var initialGdiHostColorFraction =
                ExpectedHostColorFraction(initialDesktop);

            var executionState = SetThreadExecutionState(
                ExecutionState.Continuous |
                ExecutionState.SystemRequired |
                ExecutionState.DisplayRequired);
            _executionStateLeased = executionState != 0;
            var wake = Stopwatch.StartNew();
            while (wake.Elapsed < TimeSpan.FromSeconds(1))
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            using var postLeaseDesktop = CaptureDesktop(host);
            var postLeaseGdiHostColorFraction =
                ExpectedHostColorFraction(postLeaseDesktop);
            var printWindowHostColorFraction = double.NaN;
            if (!IsQualifiedDesktopCapture(initialGdiHostColorFraction))
            {
                using var printedHost = CaptureWindow(
                    host.Handle,
                    Math.Max(1, host.ClientSize.Width),
                    Math.Max(1, host.ClientSize.Height),
                    Color.Black);
                printWindowHostColorFraction =
                    ExpectedHostColorFraction(printedHost);
                _usePrintWindow =
                    IsQualifiedPrintWindowCapture(
                        printWindowHostColorFraction);
                if (!_usePrintWindow)
                {
                    throw new InvalidOperationException(
                        "Neither desktop GDI nor PrintWindow produced concrete " +
                        "pixels for the known-color harness host. Visual " +
                        "acceptance cannot proceed safely.");
                }
            }
            _qualified = true;
            File.AppendAllText(
                _sourceLog,
                $"{DateTime.UtcNow:O} source={Source} " +
                $"execution_state_lease={_executionStateLeased} " +
                $"initial_gdi_host_color_fraction={initialGdiHostColorFraction:F6} " +
                $"post_lease_gdi_host_color_fraction={postLeaseGdiHostColorFraction:F6} " +
                $"print_window_host_color_fraction={printWindowHostColorFraction:F6} " +
                $"client={initialDesktop.Width}x{initialDesktop.Height}{Environment.NewLine}");
            Console.WriteLine(
                $"HARNESS INFO: visual capture source={Source} " +
                $"initialGdiHostColorFraction={initialGdiHostColorFraction:F4} " +
                $"printWindowHostColorFraction={printWindowHostColorFraction:F4}.");
        }

        internal Bitmap Capture(Form host)
        {
            if (!_qualified)
                throw new InvalidOperationException(
                    "The visual capture source was not qualified before Reactor startup.");

            Bitmap image;
            var printedContentBounds = Rectangle.Empty;
            if (_usePrintWindow)
            {
                var overlay = FindOwnedOverlayWindow(host.Handle);
                if (overlay == IntPtr.Zero)
                    throw new InvalidOperationException(
                        "The Reactor overlay HWND owned by the synthetic GTA " +
                        "host was not available for PrintWindow capture.");
                image = CaptureWindow(
                    overlay,
                    Math.Max(1, host.ClientSize.Width),
                    Math.Max(1, host.ClientSize.Height),
                    _hostColor);
                printedContentBounds = RestorePrintedTransparentSurround(
                    image,
                    _hostColor);
            }
            else
            {
                image = CaptureDesktop(host);
            }

            var sequence = Interlocked.Increment(ref _captureSequence);
            File.AppendAllText(
                _sourceLog,
                $"{DateTime.UtcNow:O} sequence={sequence} source={Source} " +
                $"image={image.Width}x{image.Height} " +
                $"content_bounds={printedContentBounds.X},{printedContentBounds.Y}," +
                $"{printedContentBounds.Width},{printedContentBounds.Height}" +
                Environment.NewLine);
            return image;
        }

        internal bool CanCapture(Form host) =>
            !_usePrintWindow || FindOwnedOverlayWindow(host.Handle) != IntPtr.Zero;

        public void Dispose()
        {
            if (_executionStateLeased)
            {
                SetThreadExecutionState(ExecutionState.Continuous);
                _executionStateLeased = false;
            }
        }

        private Bitmap CaptureDesktop(Form host)
        {
            var bounds = host.RectangleToScreen(host.ClientRectangle);
            var image = new Bitmap(
                Math.Max(1, bounds.Width),
                Math.Max(1, bounds.Height));
            using var graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(
                bounds.Location,
                Point.Empty,
                image.Size,
                CopyPixelOperation.SourceCopy);
            return image;
        }

        private static Bitmap CaptureWindow(
            IntPtr window,
            int width,
            int height,
            Color background)
        {
            var image = new Bitmap(
                Math.Max(1, width),
                Math.Max(1, height),
                PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(background);
            var deviceContext = graphics.GetHdc();
            bool printed;
            try
            {
                printed = PrintWindow(window, deviceContext, 0x2);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
            if (!printed)
            {
                image.Dispose();
                throw new InvalidOperationException(
                    "PrintWindow rejected the harness capture request.");
            }
            return image;
        }

        private static Rectangle RestorePrintedTransparentSurround(
            Bitmap image,
            Color hostColor)
        {
            // PrintWindow gives us concrete DirectComposition/WebView pixels
            // while the physical monitor is offline, but it flattens the
            // overlay's transparent outer surround to opaque black. Recover
            // only black pixels connected to the image border. A detached
            // paint-identity marker must not stretch one rectangular content
            // bound across otherwise transparent gaps; that made a valid
            // inset menu fail depending on its responsive width. Enclosed
            // product black remains untouched, so a real black frame still
            // fails the unchanged visual classifiers.
            var left = image.Width;
            var top = image.Height;
            var right = -1;
            var bottom = -1;
            var area = new Rectangle(0, 0, image.Width, image.Height);
            var data = image.LockBits(
                area,
                ImageLockMode.ReadWrite,
                PixelFormat.Format32bppArgb);
            try
            {
                var stride = Math.Abs(data.Stride);
                var pixels = new byte[stride * image.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

                for (var y = 0; y < image.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < image.Width; x++)
                    {
                        var offset = row + (x * 4);
                        var blue = pixels[offset];
                        var green = pixels[offset + 1];
                        var red = pixels[offset + 2];
                        if (red <= 12 && green <= 12 && blue <= 12)
                            continue;
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }

                if (right < left || bottom < top)
                    return Rectangle.Empty;

                var contentBounds = Rectangle.FromLTRB(
                    left,
                    top,
                    right + 1,
                    bottom + 1);
                BorderConnectedBlackNormalizer.Restore(
                    pixels,
                    image.Width,
                    image.Height,
                    stride,
                    hostColor.B,
                    hostColor.G,
                    hostColor.R);
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                return contentBounds;
            }
            finally
            {
                image.UnlockBits(data);
            }
        }

        private double ExpectedHostColorFraction(Bitmap image)
        {
            long matching = 0;
            long samples = 0;
            for (var y = 0; y < image.Height; y += 4)
            {
                for (var x = 0; x < image.Width; x += 4)
                {
                    var pixel = image.GetPixel(x, y);
                    samples++;
                    if (Math.Abs(pixel.R - _hostColor.R) <= 8 &&
                        Math.Abs(pixel.G - _hostColor.G) <= 8 &&
                        Math.Abs(pixel.B - _hostColor.B) <= 8)
                    {
                        matching++;
                    }
                }
            }
            return samples == 0 ? double.NaN : matching / (double)samples;
        }

        private static bool IsQualifiedDesktopCapture(double fraction) =>
            // CopyFromScreen must see virtually the entire known solid client.
            // A merely high fraction can mean the window is clipped by an
            // offline/resizing desktop; that produces black edge samples and
            // cannot qualify final composition transparency.
            !double.IsNaN(fraction) && fraction >= 0.98;

        private static bool IsQualifiedPrintWindowCapture(double fraction) =>
            // PrintWindow includes normal form/non-client edge rasterization,
            // so its known-host fraction is slightly lower even when every
            // requested client pixel is concrete.
            !double.IsNaN(fraction) && fraction >= 0.85;

        private static IntPtr FindOwnedOverlayWindow(IntPtr hostWindow)
        {
            var match = IntPtr.Zero;
            EnumWindows(
                (candidate, _) =>
                {
                    if (GetWindow(candidate, 4) != hostWindow)
                        return true;
                    var length = GetWindowTextLength(candidate);
                    if (length <= 0)
                        return true;
                    var title = new StringBuilder(length + 1);
                    GetWindowText(candidate, title, title.Capacity);
                    if (!string.Equals(
                        title.ToString(),
                        "REACTOR V Overlay",
                        StringComparison.Ordinal))
                        return true;
                    match = candidate;
                    return false;
                },
                IntPtr.Zero);
            return match;
        }

        [Flags]
        private enum ExecutionState : uint
        {
            Continuous = 0x80000000,
            SystemRequired = 0x00000001,
            DisplayRequired = 0x00000002,
        }

        [DllImport("kernel32.dll")]
        private static extern ExecutionState SetThreadExecutionState(
            ExecutionState executionState);

        private delegate bool EnumWindowsCallback(
            IntPtr window,
            IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(
            EnumWindowsCallback callback,
            IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(
            IntPtr window,
            StringBuilder title,
            int maximumCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(
            IntPtr window,
            IntPtr deviceContext,
            uint flags);
    }
}
