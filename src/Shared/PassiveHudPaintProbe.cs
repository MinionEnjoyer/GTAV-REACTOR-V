using System;

namespace ReactorV.WebView2Host
{
    // Content evidence only: callers must separately verify the current host
    // identity, lease, target size and desktop presentation. Never use for menus.
    internal static class PassiveHudPaintProbe
    {
        internal static bool IsEligible(string? surface, string? presentationId) =>
            string.Equals(surface, "passive-hud", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(presentationId);

        internal static HudPaintEvidence Analyze(
            int width, int height, Func<int, int, uint> readPixel)
        {
            if (width <= 0 || height <= 0) return default;
            // At up to 4K, an eight-pixel discovery step avoids aliasing out
            // narrow glyph stems. This is a bounded one-off reveal probe, not
            // a per-frame scan; the content lattice below remains 128 x 72.
            var columns = Math.Min(512, (width + 7) / 8);
            var rows = Math.Min(288, (height + 7) / 8);
            var left = width;
            var top = height;
            var right = -1;
            var bottom = -1;
            var seeds = 0;
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var x = Sample(column, columns, width);
                var y = Sample(row, rows, height);
                if (IsMarkerRegion(x, y, width, height) || !IsVisible(readPixel(x, y)))
                    continue;
                seeds++;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }

            // A marker, isolated noise or one scanline is not HUD content.
            if (seeds < 3 || right - left < 16 || bottom - top < 8)
                return default;
            var padX = (width + columns - 1) / columns;
            var padY = (height + rows - 1) / rows;
            left = Math.Max(0, left - padX);
            top = Math.Max(0, top - padY);
            right = Math.Min(width - 1, right + padX);
            bottom = Math.Min(height - 1, bottom + padY);
            var contentWidth = right - left + 1;
            var contentHeight = bottom - top + 1;
            columns = Math.Min(128, contentWidth);
            rows = Math.Min(72, contentHeight);
            var opaque = 0;
            var visible = 0;
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var x = left + Sample(column, columns, contentWidth);
                var y = top + Sample(row, rows, contentHeight);
                if (IsMarkerRegion(x, y, width, height)) continue;
                var argb = readPixel(x, y);
                if ((argb >> 24) >= 32) opaque++;
                if (IsVisible(argb)) visible++;
            }
            return new HudPaintEvidence(left, top, contentWidth, contentHeight,
                columns * rows, opaque, visible);
        }

        private static int Sample(int index, int count, int extent) =>
            Math.Min(extent - 1, (int)Math.Round((index + 0.5d) * extent / count - 0.5d));

        private static bool IsMarkerRegion(int x, int y, int width, int height) =>
            x >= width - OverlayPresentationPolicy.PaintIdentityMarkerMaximumStride * 8 &&
            y >= height - OverlayPresentationPolicy.PaintIdentityMarkerMaximumStride;

        private static bool IsVisible(uint argb)
        {
            var r = (argb >> 16) & 255;
            var g = (argb >> 8) & 255;
            var b = argb & 255;
            return (argb >> 24) >= 32 && Math.Max(r, Math.Max(g, b)) >= 48 && r + g + b >= 100;
        }
    }

    internal readonly struct HudPaintEvidence
    {
        internal HudPaintEvidence(int x, int y, int width, int height,
            int samples, int opaque, int visible)
        {
            X = x; Y = y; Width = width; Height = height;
            Samples = samples; Opaque = opaque; Visible = visible;
        }
        internal int X { get; }
        internal int Y { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal int Samples { get; }
        internal int Opaque { get; }
        internal int Visible { get; }
        internal bool IsConcrete => OverlayPresentationPolicy.HasConcreteBrowserPixels(Samples, Opaque, Visible);
        public override string ToString() => $"{X},{Y},{Width},{Height}";
    }
}
