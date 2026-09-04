using System;

namespace RageWebUI.Shared
{
    internal static class BorderConnectedBlackNormalizer
    {
        internal static void Restore(
            byte[] pixels,
            int width,
            int height,
            int stride,
            byte replacementBlue,
            byte replacementGreen,
            byte replacementRed,
            byte blackThreshold = 12)
        {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (stride < width * 4 || pixels.Length < stride * height)
                throw new ArgumentOutOfRangeException(nameof(stride));

            var exterior = new bool[width * height];
            var queue = new int[width * height];
            var head = 0;
            var tail = 0;

            void Enqueue(int x, int y)
            {
                var index = (y * width) + x;
                if (exterior[index]) return;
                var offset = (y * stride) + (x * 4);
                if (pixels[offset] > blackThreshold ||
                    pixels[offset + 1] > blackThreshold ||
                    pixels[offset + 2] > blackThreshold)
                {
                    return;
                }
                exterior[index] = true;
                queue[tail++] = index;
            }

            for (var x = 0; x < width; x++)
            {
                Enqueue(x, 0);
                Enqueue(x, height - 1);
            }
            for (var y = 0; y < height; y++)
            {
                Enqueue(0, y);
                Enqueue(width - 1, y);
            }

            while (head < tail)
            {
                var index = queue[head++];
                var x = index % width;
                var y = index / width;
                if (x > 0) Enqueue(x - 1, y);
                if (x + 1 < width) Enqueue(x + 1, y);
                if (y > 0) Enqueue(x, y - 1);
                if (y + 1 < height) Enqueue(x, y + 1);
            }

            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (!exterior[(y * width) + x]) continue;
                    var offset = row + (x * 4);
                    pixels[offset] = replacementBlue;
                    pixels[offset + 1] = replacementGreen;
                    pixels[offset + 2] = replacementRed;
                    pixels[offset + 3] = byte.MaxValue;
                }
            }
        }
    }
}
