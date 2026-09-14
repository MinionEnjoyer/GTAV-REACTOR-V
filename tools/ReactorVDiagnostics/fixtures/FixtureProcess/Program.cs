// Synthetic process identity fixture. Not GTA and NEVER included in the diagnostic package.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class FixtureProgram
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--large-output")
        {
            for (int i = 0; i < 100; i++) { Console.WriteLine(new string('x', 4096)); Console.Error.WriteLine(new string('e', 4096)); }
            return 0;
        }
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 3;
        int exitCode = args.Length > 1 ? int.Parse(args[1]) : 0;
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "ReactorV");
        Directory.CreateDirectory(directory);
        string log = Path.Combine(directory, "ReactorV.NativeLifecycle.log");
        int pid = Process.GetCurrentProcess().Id;
        for (int i = 0; i < seconds * 4; i++)
        {
            File.AppendAllText(log, DateTime.UtcNow.ToString("o") + " pid=" + pid + " stage=synthetic_fixture_tick fixture_only=True sequence=" + i + Environment.NewLine);
            Thread.Sleep(250);
        }
        return exitCode;
    }
}
