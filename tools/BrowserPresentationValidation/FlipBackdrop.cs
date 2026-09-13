using System;
using System.Drawing;
using System.Windows.Forms;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;

// An owned borderless D3D11 flip-model target, not GTA or proof of a particular
// independent-flip/MPO mode. Never changes display settings or enters exclusive
// fullscreen. The outer runner bounds the lifetime of this entire fixture.
internal sealed class FlipBackdrop : Form
{
    private Device? device;
    private SwapChain1? swapChain;
    private RenderTargetView? target;
    private readonly Timer timer = new Timer { Interval = 16 };
    internal int Frames { get; private set; }
    internal Exception? RenderFailure { get; private set; }

    internal FlipBackdrop()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen.Bounds;
        BackColor = Color.FromArgb(55, 45, 30);
        Text = "Reactor offline flip presentation fixture";
        timer.Tick += (_, __) => {
            try { Render(); }
            catch (Exception error) { RenderFailure = error; timer.Stop(); }
        };
    }

    internal void StartRendering()
    {
        Show(); Activate();
        device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        using (var dxgi = device.QueryInterface<SharpDX.DXGI.Device>())
        using (var adapter = dxgi.Adapter)
        using (var factory = adapter.GetParent<Factory2>())
        {
            var description = new SwapChainDescription1 {
                Width = ClientSize.Width, Height = ClientSize.Height,
                Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                SampleDescription = new SampleDescription(1, 0),
                Usage = Usage.RenderTargetOutput, SwapEffect = SwapEffect.FlipDiscard,
                Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore
            };
            swapChain = new SwapChain1(factory, device, Handle, ref description);
            factory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAll);
        }
        using (var buffer = swapChain.GetBackBuffer<Texture2D>(0))
            target = new RenderTargetView(device, buffer);
        Render(); timer.Start();
    }

    private void Render()
    {
        device!.ImmediateContext.OutputMerger.SetRenderTargets(target);
        // Smooth, low-contrast movement ensures desktop frames continue without
        // creating a flashing test background or matching the blue identity cells.
        var brightness = (float)(0.13 + 0.015 * Math.Sin(Frames * 0.02));
        device.ImmediateContext.ClearRenderTargetView(target,
            new RawColor4(brightness, brightness * 0.8f, brightness * 0.5f, 1));
        swapChain!.Present(1, PresentFlags.None);
        Frames++;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop(); timer.Dispose();
            target?.Dispose(); swapChain?.Dispose();
            device?.ImmediateContext.ClearState(); device?.Dispose();
        }
        base.Dispose(disposing);
    }
}
