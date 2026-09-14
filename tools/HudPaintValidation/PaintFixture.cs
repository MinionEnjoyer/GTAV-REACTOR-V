// Offline .NET Framework integration fixture. Invokes the compiled production
// PNG analyzer, without creating an overlay, loading GTA or retaining images.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;

internal static class PaintFixture
{
    private const ulong Identity = 0x6a3278b9d14ce520UL;
    private static MethodInfo Analyze;
    private static int Checks;
    private static double MaximumMs;
    private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    private static int Main(string[] args)
    {
        try
        {
            var runtime = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
                var file = Path.Combine(runtime, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            var assembly = Assembly.LoadFrom(Path.Combine(runtime, "RageWebUI.Runtime.dll"));
            Analyze = assembly.GetType("RageWebUI.Runtime.OverlayWindow", true)
                .GetMethod("AnalyzePresentationPixels", BindingFlags.NonPublic | BindingFlags.Static);
            var sizes = new[] { new Size(1280,720), new Size(1920,1080), new Size(2560,1440), new Size(3840,2160) };
            foreach (var size in sizes)
            foreach (var dpi in new[] { 1d, 1.25d, 1.5d, 2d })
            {
                using (var bitmap = Draw(size, dpi, true, true, "42"))
                {
                    Check(bitmap, Identity, true, true, "hud " + size + " dpi=" + dpi);
                    Check(bitmap, Identity ^ 0x3030303030303030UL, true, false, "stale identity");
                }
                using (var marker = Draw(size, dpi, false, true, ""))
                    Check(marker, Identity, true, false, "marker only");
            }
            foreach (var digits in new[] { "0", "1", "11", "88", "9999" })
            using (var bitmap = Draw(new Size(2560, 1440), 1.25, true, true, digits))
                Check(bitmap, Identity, true, true, "digit shape " + digits);
            using (var bitmap = Draw(new Size(2560,1440), 1.25, true, false, "42"))
                Check(bitmap, Identity, true, false, "missing marker");
            using (var bitmap = Draw(new Size(2560,1440), 1.25, false, false, ""))
                Check(bitmap, Identity, true, false, "empty image");
            using (var bitmap = Draw(new Size(3840,2160), 1, true, true, "42"))
            {
                Check(bitmap, Identity, true, true, "sparse hud");
                Check(bitmap, Identity, false, false, "sparse image cannot qualify as full menu");
                Check(bitmap, 0, true, false, "HUD without expected identity");
            }
            using (var bitmap = Draw(new Size(1920,1080), 1, false, true, ""))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(Color.FromArgb(255, 80, 95, 110)))
                    graphics.FillRectangle(brush, 350, 180, 900, 650);
                Check(bitmap, Identity, false, true, "menu card unchanged");
            }
            Console.WriteLine("PASS checks=" + Checks + " max_analyzer_ms=" + MaximumMs.ToString("F2") +
                " compiled_runtime=True synthetic_png_only=True installation_changed=False");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static Bitmap Draw(Size size, double dpi, bool content, bool marker, string digits)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (content)
            using (var font = new Font("Segoe UI", (float)(90 * dpi), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var small = new Font("Segoe UI", (float)(13 * dpi), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.FromArgb(190, 245, 248, 246)))
            {
                var x = size.Width - (int)(480 * dpi);
                var y = size.Height - (int)(300 * dpi);
                graphics.DrawString(digits, font, brush, x, y);
                graphics.DrawString("MPH", small, brush, x + (float)(270 * dpi), y + (float)(76 * dpi));
                graphics.DrawString("3", font, brush, x + (float)(320 * dpi), y);
            }
            if (marker)
            for (var index = 0; index < 8; index++)
            {
                var value = (byte)(Identity >> (index * 8));
                using (var brush = new SolidBrush(Color.FromArgb(255, 64 + (value >> 4) * 12, 64 + (value & 15) * 12, 208)))
                    graphics.FillRectangle(brush, size.Width - (int)(92 * dpi) + (int)(index * 12 * dpi),
                        size.Height - (int)(6 * dpi), (int)(8 * dpi), (int)(6 * dpi));
            }
        }
        return bitmap;
    }

    private static void Check(Bitmap bitmap, ulong expectedIdentity, bool hud, bool expected, string name)
    {
        byte[] png;
        using (var stream = new MemoryStream()) { bitmap.Save(stream, ImageFormat.Png); png = stream.ToArray(); }
        var clock = Stopwatch.StartNew();
        var result = Analyze.Invoke(null, new object[] { png, expectedIdentity, bitmap.Size, hud });
        clock.Stop();
        MaximumMs = Math.Max(MaximumMs, clock.Elapsed.TotalMilliseconds);
        var concrete = (bool)result.GetType().GetProperty("IsConcrete", Hidden).GetValue(result, null);
        if (concrete != expected) throw new Exception(name + " concrete=" + concrete + " expected=" + expected);
        Checks++;
    }
}
