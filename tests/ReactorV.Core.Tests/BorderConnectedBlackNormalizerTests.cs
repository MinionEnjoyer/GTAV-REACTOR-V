using RageWebUI.Shared;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class BorderConnectedBlackNormalizerTests
{
    private const int Width = 32;
    private const int Height = 24;
    private const int Stride = Width * 4;

    [Fact]
    public void Restores_transparent_gap_without_erasing_detached_marker_or_enclosed_black()
    {
        var pixels = BlackFrame();
        FillRectangle(pixels, 3, 3, 25, 19, blue: 30, green: 120, red: 20);
        Set(pixels, 10, 10, 0, 0, 0);
        Set(pixels, 29, 22, 40, 220, 70);

        BorderConnectedBlackNormalizer.Restore(pixels, Width, Height, Stride, 94, 60, 79);

        Assert.Equal((94, 60, 79), Get(pixels, 27, 12));
        Assert.Equal((40, 220, 70), Get(pixels, 29, 22));
        Assert.Equal((0, 0, 0), Get(pixels, 10, 10));
    }

    [Fact]
    public void Marker_only_black_frame_does_not_become_a_qualified_menu()
    {
        var pixels = BlackFrame();
        Set(pixels, 29, 22, 40, 220, 70);

        BorderConnectedBlackNormalizer.Restore(pixels, Width, Height, Stride, 94, 60, 79);

        var changed = 0;
        var green = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var pixel = Get(pixels, x, y);
            if (pixel != (94, 60, 79)) changed++;
            if (pixel.Green > pixel.Red + 15 && pixel.Green > pixel.Blue + 5) green++;
        }

        Assert.True(changed / (double)(Width * Height) < 0.10);
        Assert.True(green / (double)(Width * Height) < 0.006);
    }

    private static byte[] BlackFrame()
    {
        var pixels = new byte[Stride * Height];
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        return pixels;
    }

    private static void FillRectangle(
        byte[] pixels, int left, int top, int right, int bottom,
        byte blue, byte green, byte red)
    {
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
            Set(pixels, x, y, blue, green, red);
    }

    private static void Set(byte[] pixels, int x, int y, byte blue, byte green, byte red)
    {
        var offset = (y * Stride) + (x * 4);
        pixels[offset] = blue;
        pixels[offset + 1] = green;
        pixels[offset + 2] = red;
        pixels[offset + 3] = 255;
    }

    private static (byte Blue, byte Green, byte Red) Get(byte[] pixels, int x, int y)
    {
        var offset = (y * Stride) + (x * 4);
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }
}
