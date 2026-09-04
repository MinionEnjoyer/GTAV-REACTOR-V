using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Rectangle = System.Drawing.Rectangle;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Captures the monitor image through DXGI Desktop Duplication and crops it
    /// to the GTA client rectangle. Unlike a GDI BitBlt, Desktop Duplication
    /// receives flip-model Direct3D 11/12 presentation pixels.
    /// </summary>
    internal sealed class DxgiDesktopCaptureSession : IDisposable
    {
        private readonly Factory1 _factory;
        private readonly Adapter1 _adapter;
        private readonly Output _output;
        private readonly Output1 _output1;
        private readonly Device _device;
        private readonly OutputDuplication _duplication;
        private readonly Texture2D _staging;
        private readonly Rectangle _desktopBounds;
        private bool _frameAcquired;
        private bool _disposed;

        internal string OutputIdentity { get; }

        internal DxgiDesktopCaptureSession(Rectangle targetBounds)
        {
            if (targetBounds.Width <= 0 || targetBounds.Height <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(targetBounds),
                    "The target client rectangle must have positive dimensions.");

            _factory = new Factory1();
            (_adapter, _output, _desktopBounds) = SelectOutput(
                _factory,
                targetBounds);
            var outputDescription = _output.Description;
            if (outputDescription.Rotation != DisplayModeRotation.Identity &&
                outputDescription.Rotation != DisplayModeRotation.Unspecified)
                throw new NotSupportedException(
                    "The selected display is rotated; the DXGI acceptance " +
                    "capture currently requires an unrotated display.");

            OutputIdentity = string.Concat(
                _adapter.Description1.Description.Trim(),
                " | ",
                outputDescription.DeviceName);
            _output1 = _output.QueryInterface<Output1>();
            _device = new Device(_adapter, DeviceCreationFlags.BgraSupport);
            _duplication = _output1.DuplicateOutput(_device);
            _staging = new Texture2D(
                _device,
                new Texture2DDescription
                {
                    Width = _desktopBounds.Width,
                    Height = _desktopBounds.Height,
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

        internal Bitmap Capture(Rectangle targetBounds, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            if (timeoutMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            if (!_desktopBounds.IntersectsWith(targetBounds))
                throw new InvalidOperationException(
                    "The GTA client moved outside the display selected for capture.");

            SharpDX.DXGI.Resource? desktopResource = null;
            try
            {
                _duplication.AcquireNextFrame(
                    timeoutMilliseconds,
                    out _,
                    out desktopResource);
                _frameAcquired = true;
                using var frame = desktopResource.QueryInterface<Texture2D>();
                _device.ImmediateContext.CopyResource(frame, _staging);
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
                try
                {
                    _duplication.ReleaseFrame();
                }
                catch
                {
                    // Disposal must not mask the acceptance result.
                }
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
            var intersection = Rectangle.Intersect(_desktopBounds, targetBounds);
            if (intersection.Width <= 0 || intersection.Height <= 0)
                throw new InvalidOperationException(
                    "The GTA client does not intersect the duplicated desktop output.");

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
                    var sourceX = intersection.Left - _desktopBounds.Left;
                    var sourceY = intersection.Top - _desktopBounds.Top;
                    var destinationX = intersection.Left - targetBounds.Left;
                    var destinationY = intersection.Top - targetBounds.Top;
                    var rowBytes = intersection.Width * 4;
                    var row = new byte[rowBytes];
                    for (var y = 0; y < intersection.Height; y++)
                    {
                        var sourceRow = IntPtr.Add(
                            source.DataPointer,
                            ((sourceY + y) * source.RowPitch) + (sourceX * 4));
                        Marshal.Copy(sourceRow, row, 0, rowBytes);
                        var destinationRow = IntPtr.Add(
                            destination.Scan0,
                            ((destinationY + y) * destination.Stride) +
                            (destinationX * 4));
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
                var keepAdapter = false;
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
                            selectedAdapter.Dispose();
                        selectedAdapter = adapter;
                        selectedOutput = output;
                        selectedBounds = bounds;
                        selectedArea = area;
                        keepAdapter = true;
                    }
                    else
                    {
                        output.Dispose();
                    }
                }
                if (!keepAdapter && !ReferenceEquals(selectedAdapter, adapter))
                    adapter.Dispose();
            }

            if (selectedAdapter == null || selectedOutput == null || selectedArea <= 0)
                throw new InvalidOperationException(
                    "No DXGI desktop output intersects the GTA client rectangle.");
            return (selectedAdapter, selectedOutput, selectedBounds);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DxgiDesktopCaptureSession));
        }
    }
}
