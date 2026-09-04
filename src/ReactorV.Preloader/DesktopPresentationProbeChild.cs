using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReactorV.WebView2Host;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Rectangle = System.Drawing.Rectangle;

namespace ReactorV.Preloader
{
    internal static class DesktopPresentationProbeChild
    {
        private const string ChildMode = "--desktop-presentation-probe";
        private const int MaximumEncodedRequestLength = 65536;
        private const int MaximumSamples = 128;
        private const int MaximumTimeoutMilliseconds = 10000;
        private const int MaximumGdiCaptureAttempts = 2;
        private const int GdiCaptureRetryMilliseconds = 32;
        private const int ChildResultReserveMilliseconds = 75;

        internal static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args.Length == 0 ||
                !string.Equals(args[0], ChildMode, StringComparison.Ordinal))
            {
                return false;
            }

            DesktopPresentationProbeWireResult result;
            try
            {
                if (args.Length != 2 ||
                    string.IsNullOrWhiteSpace(args[1]) ||
                    args[1].Length > MaximumEncodedRequestLength)
                {
                    result = DesktopPresentationProbeWireResult.Failed(
                        "invalid-request-argument");
                }
                else
                {
                    result = Execute(DecodeRequest(args[1]));
                }
            }
            catch (Exception error) when (!IsFatal(error))
            {
                result = DesktopPresentationProbeWireResult.Failed(
                    "probe-failed:" + error.GetType().Name + ":" +
                    NormalizeError(error.Message));
            }

            WriteResult(result);
            return true;
        }

        private static DesktopPresentationProbeRequest DecodeRequest(string encoded)
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length == 0 || bytes.Length > MaximumEncodedRequestLength)
                throw new InvalidDataException("The request payload is empty or oversized.");
            var json = new UTF8Encoding(false, true).GetString(bytes);
            var root = JObject.Parse(json);
            var request = new DesktopPresentationProbeRequest
            {
                X = RequiredInt(root, "x"),
                Y = RequiredInt(root, "y"),
                Width = RequiredInt(root, "w"),
                Height = RequiredInt(root, "h"),
                Tolerance = RequiredInt(root, "t"),
                TimeoutMilliseconds = RequiredInt(root, "ms"),
            };
            var sampleArray = root["s"] as JArray ??
                throw new InvalidDataException("The request has no sample array.");
            foreach (var token in sampleArray)
            {
                if (!(token is JObject sample))
                    throw new InvalidDataException("A sample is not an object.");
                request.Samples.Add(new DesktopPresentationProbeWireSample
                {
                    NormalizedX = RequiredDouble(sample, "x"),
                    NormalizedY = RequiredDouble(sample, "y"),
                    Red = RequiredInt(sample, "r"),
                    Green = RequiredInt(sample, "g"),
                    Blue = RequiredInt(sample, "b"),
                });
            }
            Validate(request);
            return request;
        }

        private static DesktopPresentationProbeWireResult Execute(
            DesktopPresentationProbeRequest request)
        {
            var timer = Stopwatch.StartNew();
            var target = new Rectangle(
                request.X,
                request.Y,
                request.Width,
                request.Height);
            // Copy the composited desktop first. Unlike a private browser
            // screenshot this is an OS-visible surface, and
            // it does not wait for a *subsequent* DXGI frame after the overlay
            // has already been promoted. Desktop Duplication remains the
            // isolated fallback for environments where GDI screen capture is
            // unavailable.
            try
            {
                DesktopPresentationProbeWireResult? best = null;
                var gdiAttempts = 0;
                while (gdiAttempts < MaximumGdiCaptureAttempts &&
                    timer.ElapsedMilliseconds <
                        request.TimeoutMilliseconds - ChildResultReserveMilliseconds)
                {
                    gdiAttempts++;
                    DesktopPresentationProbeWireResult current;
                    using (var image = CaptureCompositedDesktop(target))
                        current = Evaluate(
                            request,
                            target,
                            image,
                            SystemInformation.VirtualScreen,
                            "gdi-composited-desktop:bounded-attempt-" +
                            gdiAttempts);
                    if (current.Concrete)
                        return current;
                    if (best == null ||
                        current.Matching > best.Matching ||
                        (current.Matching == best.Matching &&
                         current.Readable > best.Readable))
                    {
                        best = current;
                    }

                    // Window promotion, the DirectComposition commit, and DWM
                    // presentation are separate boundaries. Give DWM one
                    // additional compositor turn, but do not spend the full
                    // probe deadline retrying a window which is genuinely
                    // transparent/black on the desktop. A real presentation
                    // repair belongs to the owner process, not this witness.
                    if (gdiAttempts < MaximumGdiCaptureAttempts)
                    {
                        var remaining = request.TimeoutMilliseconds -
                            ChildResultReserveMilliseconds -
                            (int)timer.ElapsedMilliseconds;
                        var settle = Math.Min(
                            GdiCaptureRetryMilliseconds,
                            Math.Max(0, remaining));
                        if (settle > 0)
                            System.Threading.Thread.Sleep(settle);
                    }
                }

                var bestGdi = best ??
                    DesktopPresentationProbeWireResult.Failed(
                        "gdi-produced-no-samples");
                return bestGdi;
            }
            catch (Exception error) when (
                error is ArgumentException ||
                error is System.ComponentModel.Win32Exception ||
                error is ExternalException)
            {
                return CaptureDesktopDuplication(
                    request,
                    target,
                    Math.Max(
                        1,
                        request.TimeoutMilliseconds - (int)timer.ElapsedMilliseconds),
                    "dxgi-gdi-unavailable");
            }
        }

        private static DesktopPresentationProbeWireResult CaptureDesktopDuplication(
            DesktopPresentationProbeRequest request,
            Rectangle target,
            int timeoutMilliseconds,
            string reason)
        {
            using (var capture = new DesktopDuplicationProbeCapture(target))
            using (var image = capture.Capture(target, timeoutMilliseconds))
            {
                return Evaluate(
                    request,
                    target,
                    image,
                    capture.DesktopBounds,
                    "dxgi-desktop-duplication:" + capture.OutputIdentity +
                    ":" + reason);
            }
        }

        private static Bitmap CaptureCompositedDesktop(Rectangle target)
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            if (!virtualScreen.Contains(target))
                throw new ArgumentException(
                    "The target is not fully contained by the virtual desktop.",
                    nameof(target));
            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows did not expose the desktop composition surface.");
            var memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screen);
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not create the capture device context.");
            }
            var nativeBitmap = CreateCompatibleBitmap(
                screen,
                target.Width,
                target.Height);
            if (nativeBitmap == IntPtr.Zero)
            {
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not allocate the capture bitmap.");
            }
            var previous = SelectObject(memory, nativeBitmap);
            try
            {
                if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not select the capture bitmap.");
                const uint SourceCopy = 0x00CC0020;
                const uint CaptureLayeredWindows = 0x40000000;
                if (!BitBlt(
                        memory,
                        0,
                        0,
                        target.Width,
                        target.Height,
                        screen,
                        target.Left,
                        target.Top,
                        SourceCopy | CaptureLayeredWindows))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not copy the composed desktop pixels.");
                }
                return Image.FromHbitmap(nativeBitmap);
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1))
                    SelectObject(memory, previous);
                DeleteObject(nativeBitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static DesktopPresentationProbeWireResult Evaluate(
            DesktopPresentationProbeRequest request,
            Rectangle target,
            Bitmap image,
            Rectangle readableBounds,
            string source)
        {
            var readable = 0;
            var matching = 0;
            foreach (var sample in request.Samples)
            {
                var localX = Math.Max(0, Math.Min(
                    target.Width - 1,
                    (int)Math.Round(sample.NormalizedX * target.Width - 0.5d)));
                var localY = Math.Max(0, Math.Min(
                    target.Height - 1,
                    (int)Math.Round(sample.NormalizedY * target.Height - 0.5d)));
                var desktopPoint = new Point(
                    target.Left + localX,
                    target.Top + localY);
                if (!readableBounds.Contains(desktopPoint))
                    continue;
                var observed = image.GetPixel(localX, localY);
                readable++;
                if (Math.Abs(observed.R - sample.Red) <= request.Tolerance &&
                    Math.Abs(observed.G - sample.Green) <= request.Tolerance &&
                    Math.Abs(observed.B - sample.Blue) <= request.Tolerance)
                {
                    matching++;
                }
            }

            var completeIdentity =
                readable == request.Samples.Count &&
                matching >= (request.Samples.Count * 3 + 3) / 4;
            return new DesktopPresentationProbeWireResult
            {
                Readable = readable,
                Matching = matching,
                Concrete = completeIdentity &&
                    OverlayPresentationPolicy.HasConcreteDesktopPixels(
                        readable,
                        matching),
                Source = source,
                Error = null,
            };
        }

        private static void Validate(DesktopPresentationProbeRequest request)
        {
            if (request.Width <= 0 || request.Height <= 0 ||
                request.Width > 32768 || request.Height > 32768 ||
                (long)request.X + request.Width > int.MaxValue ||
                (long)request.Y + request.Height > int.MaxValue)
            {
                throw new InvalidDataException("The target bounds are invalid.");
            }
            if (request.Tolerance < 0 || request.Tolerance > 255)
                throw new InvalidDataException("The tolerance is invalid.");
            if (request.TimeoutMilliseconds <= 0 ||
                request.TimeoutMilliseconds > MaximumTimeoutMilliseconds)
            {
                throw new InvalidDataException("The timeout is invalid.");
            }
            if (request.Samples.Count == 0 ||
                request.Samples.Count > MaximumSamples)
            {
                throw new InvalidDataException("The sample count is invalid.");
            }
            foreach (var sample in request.Samples)
            {
                if (!IsNormalized(sample.NormalizedX) ||
                    !IsNormalized(sample.NormalizedY) ||
                    sample.Red < 0 || sample.Red > 255 ||
                    sample.Green < 0 || sample.Green > 255 ||
                    sample.Blue < 0 || sample.Blue > 255)
                {
                    throw new InvalidDataException("A sample is invalid.");
                }
            }
        }

        private static int RequiredInt(JObject value, string name) =>
            value.Value<int?>(name) ??
            throw new InvalidDataException("Missing integer property: " + name);

        private static double RequiredDouble(JObject value, string name) =>
            value.Value<double?>(name) ??
            throw new InvalidDataException("Missing number property: " + name);

        private static bool IsNormalized(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) &&
            value >= 0d && value <= 1d;

        private static bool IsFatal(Exception error) =>
            error is OutOfMemoryException ||
            error is StackOverflowException ||
            error is AccessViolationException;

        private static string NormalizeError(string error)
        {
            var normalized = (error ?? string.Empty)
                .Trim()
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            return normalized.Length <= 160
                ? normalized
                : normalized.Substring(0, 160);
        }

        private static void WriteResult(DesktopPresentationProbeWireResult result)
        {
            var json = JsonConvert.SerializeObject(result, Formatting.None);
            using (var output = new StreamWriter(
                Console.OpenStandardOutput(),
                new UTF8Encoding(false)))
            {
                output.Write(json);
                output.Flush();
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(
            IntPtr deviceContext,
            int width,
            int height);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(
            IntPtr deviceContext,
            IntPtr value);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr value);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BitBlt(
            IntPtr destination,
            int destinationX,
            int destinationY,
            int width,
            int height,
            IntPtr source,
            int sourceX,
            int sourceY,
            uint operation);

        private sealed class DesktopPresentationProbeRequest
        {
            internal int X { get; set; }
            internal int Y { get; set; }
            internal int Width { get; set; }
            internal int Height { get; set; }
            internal int Tolerance { get; set; }
            internal int TimeoutMilliseconds { get; set; }
            internal List<DesktopPresentationProbeWireSample> Samples { get; } =
                new List<DesktopPresentationProbeWireSample>();
        }

        private sealed class DesktopPresentationProbeWireSample
        {
            internal double NormalizedX { get; set; }
            internal double NormalizedY { get; set; }
            internal int Red { get; set; }
            internal int Green { get; set; }
            internal int Blue { get; set; }
        }

        private sealed class DesktopPresentationProbeWireResult
        {
            [JsonProperty("readable")]
            internal int Readable { get; set; }

            [JsonProperty("matching")]
            internal int Matching { get; set; }

            [JsonProperty("concrete")]
            internal bool Concrete { get; set; }

            [JsonProperty("source")]
            internal string Source { get; set; } = "dxgi-desktop-duplication";

            [JsonProperty("error")]
            internal string? Error { get; set; }

            internal static DesktopPresentationProbeWireResult Failed(string error) =>
                new DesktopPresentationProbeWireResult
                {
                    Readable = 0,
                    Matching = 0,
                    Concrete = false,
                    Error = error,
                };
        }
    }

    /// <summary>
    /// Captures flip-model presentation pixels from the desktop output with the
    /// greatest intersection with the requested target rectangle.
    /// </summary>
    internal sealed class DesktopDuplicationProbeCapture : IDisposable
    {
        private readonly Factory1 _factory;
        private readonly Adapter1 _adapter;
        private readonly Output _output;
        private readonly Output1 _output1;
        private readonly Device _device;
        private readonly OutputDuplication _duplication;
        private readonly Texture2D _staging;
        private bool _frameAcquired;
        private bool _disposed;

        internal DesktopDuplicationProbeCapture(Rectangle targetBounds)
        {
            _factory = new Factory1();
            (_adapter, _output, DesktopBounds) = SelectOutput(
                _factory,
                targetBounds);
            var description = _output.Description;
            if (description.Rotation != DisplayModeRotation.Identity &&
                description.Rotation != DisplayModeRotation.Unspecified)
            {
                throw new NotSupportedException(
                    "The selected DXGI output is rotated.");
            }
            OutputIdentity = _adapter.Description1.Description.Trim() +
                " | " + description.DeviceName;
            _output1 = _output.QueryInterface<Output1>();
            _device = new Device(_adapter, DeviceCreationFlags.BgraSupport);
            _duplication = _output1.DuplicateOutput(_device);
            _staging = new Texture2D(
                _device,
                new Texture2DDescription
                {
                    Width = DesktopBounds.Width,
                    Height = DesktopBounds.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    OptionFlags = ResourceOptionFlags.None,
                });
        }

        internal Rectangle DesktopBounds { get; }
        internal string OutputIdentity { get; }

        internal Bitmap Capture(Rectangle targetBounds, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            SharpDX.DXGI.Resource? desktopResource = null;
            try
            {
                _duplication.AcquireNextFrame(
                    timeoutMilliseconds,
                    out _,
                    out desktopResource);
                _frameAcquired = true;
                using (var frame = desktopResource.QueryInterface<Texture2D>())
                {
                    _device.ImmediateContext.CopyResource(frame, _staging);
                }
                var mapped = _device.ImmediateContext.MapSubresource(
                    _staging,
                    0,
                    MapMode.Read,
                    SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    return CopyTarget(mapped, targetBounds);
                }
                finally
                {
                    _device.ImmediateContext.UnmapSubresource(_staging, 0);
                }
            }
            finally
            {
                desktopResource?.Dispose();
                if (_frameAcquired)
                {
                    _duplication.ReleaseFrame();
                    _frameAcquired = false;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_frameAcquired)
            {
                try { _duplication.ReleaseFrame(); }
                catch { }
                _frameAcquired = false;
            }
            _staging.Dispose();
            _duplication.Dispose();
            _device.Dispose();
            _output1.Dispose();
            _output.Dispose();
            _adapter.Dispose();
            _factory.Dispose();
        }

        private Bitmap CopyTarget(DataBox source, Rectangle targetBounds)
        {
            var intersection = Rectangle.Intersect(DesktopBounds, targetBounds);
            var result = new Bitmap(
                targetBounds.Width,
                targetBounds.Height,
                PixelFormat.Format32bppArgb);
            try
            {
                var destination = result.LockBits(
                    new Rectangle(0, 0, result.Width, result.Height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    var sourceX = intersection.Left - DesktopBounds.Left;
                    var sourceY = intersection.Top - DesktopBounds.Top;
                    var destinationX = intersection.Left - targetBounds.Left;
                    var destinationY = intersection.Top - targetBounds.Top;
                    var rowBytes = intersection.Width * 4;
                    var row = new byte[rowBytes];
                    for (var y = 0; y < intersection.Height; y++)
                    {
                        var sourceRow = IntPtr.Add(
                            source.DataPointer,
                            ((sourceY + y) * source.RowPitch) + sourceX * 4);
                        Marshal.Copy(sourceRow, row, 0, rowBytes);
                        var destinationRow = IntPtr.Add(
                            destination.Scan0,
                            ((destinationY + y) * destination.Stride) +
                            destinationX * 4);
                        Marshal.Copy(row, 0, destinationRow, rowBytes);
                    }
                }
                finally
                {
                    result.UnlockBits(destination);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static (Adapter1 Adapter, Output Output, Rectangle Bounds) SelectOutput(
            Factory1 factory,
            Rectangle targetBounds)
        {
            Adapter1? selectedAdapter = null;
            Output? selectedOutput = null;
            var selectedBounds = Rectangle.Empty;
            var selectedArea = 0L;
            foreach (var adapter in factory.Adapters1)
            {
                var adapterSelected = false;
                foreach (var output in adapter.Outputs)
                {
                    var native = output.Description.DesktopBounds;
                    var bounds = Rectangle.FromLTRB(
                        native.Left,
                        native.Top,
                        native.Right,
                        native.Bottom);
                    var intersection = Rectangle.Intersect(bounds, targetBounds);
                    var area = (long)intersection.Width * intersection.Height;
                    if (area > selectedArea)
                    {
                        selectedOutput?.Dispose();
                        if (selectedAdapter != null &&
                            !ReferenceEquals(selectedAdapter, adapter))
                        {
                            selectedAdapter.Dispose();
                        }
                        selectedAdapter = adapter;
                        selectedOutput = output;
                        selectedBounds = bounds;
                        selectedArea = area;
                        adapterSelected = true;
                    }
                    else
                    {
                        output.Dispose();
                    }
                }
                if (!adapterSelected && !ReferenceEquals(selectedAdapter, adapter))
                    adapter.Dispose();
            }
            if (selectedAdapter == null || selectedOutput == null || selectedArea <= 0)
                throw new InvalidOperationException(
                    "No DXGI output intersects the target desktop rectangle.");
            return (selectedAdapter, selectedOutput, selectedBounds);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DesktopDuplicationProbeCapture));
        }
    }
}
