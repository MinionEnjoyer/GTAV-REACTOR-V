using System;
using System.Collections.Generic;
using System.Drawing;

namespace ReactorV.WebView2Host
{
    internal static class DesktopProbeGeometry
    {
        // Keep the same physical pixel centres as the full-frame witness,
        // including midpoint rounding and clamping at normalized edges.
        internal static Point SamplePoint(Rectangle target, double x, double y)
        {
            if (target.Width <= 0 || target.Height <= 0 ||
                double.IsNaN(x) || double.IsInfinity(x) || x < 0 || x > 1 ||
                double.IsNaN(y) || double.IsInfinity(y) || y < 0 || y > 1)
                throw new ArgumentException("Invalid desktop sample geometry.");
            return new Point(
                checked(target.Left + Math.Max(0, Math.Min(target.Width - 1,
                    (int)Math.Round(x * target.Width - 0.5d)))),
                checked(target.Top + Math.Max(0, Math.Min(target.Height - 1,
                    (int)Math.Round(y * target.Height - 0.5d)))));
        }

        internal static Rectangle CaptureBounds(Rectangle target, IReadOnlyList<Point> points)
        {
            if (points == null || points.Count == 0)
                throw new ArgumentException("A desktop witness requires sample points.");
            var left = int.MaxValue;
            var top = int.MaxValue;
            var right = int.MinValue;
            var bottom = int.MinValue;
            foreach (var point in points)
            {
                if (!target.Contains(point))
                    throw new ArgumentException("A desktop sample lies outside the target.");
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }
            return new Rectangle(left, top, checked(right - left + 1), checked(bottom - top + 1));
        }
    }
}
