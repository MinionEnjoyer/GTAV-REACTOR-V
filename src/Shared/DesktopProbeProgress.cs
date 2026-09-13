using System;
using System.Globalization;

namespace ReactorV.WebView2Host
{
    // A small allowlisted stderr protocol. Never copy request payloads or
    // arbitrary child output into runtime logs, and never use it as pixel proof.
    internal sealed class DesktopProbeProgress
    {
        internal const string Prefix = "reactorv-probe-stage=";
        internal static bool TryParse(string? line, out string stage, out long elapsedMs)
        {
            stage = "unobserved";
            elapsedMs = 0;
            if (line == null || line.Length > 120 || !line.StartsWith(Prefix, StringComparison.Ordinal))
                return false;
            var parts = line.Substring(Prefix.Length).Split(' ');
            if (parts.Length != 2 || !parts[1].StartsWith("ms=", StringComparison.Ordinal) ||
                !long.TryParse(parts[1].Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out elapsedMs) ||
                elapsedMs > 60000)
                return false;
            switch (parts[0])
            {
                case "dispatch":
                case "request-decoded":
                case "gdi-capture":
                case "gdi-get-dc":
                case "gdi-create-dc":
                case "gdi-create-bitmap":
                case "gdi-select-bitmap":
                case "gdi-bitblt":
                case "gdi-read-bitmap":
                case "gdi-cleanup":
                case "gdi-evaluate":
                case "dxgi-create":
                case "dxgi-capture":
                case "dxgi-evaluate":
                case "result-write":
                    stage = parts[0];
                    return true;
                default:
                    return false;
            }
        }
    }
}
