using System;

namespace ReactorV.Diagnostics
{
    public sealed class DiagnosticOptions
    {
        public string GameDirectory { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public string Edition { get; set; } = "Enhanced";
        public string ManifestPath { get; set; } = "";
        public bool IncludeSecurityEvents { get; set; }
        public int WaitSeconds { get; set; } = 120;
        public int RecordSeconds { get; set; } = 180;
        public string DumpMode { get; set; } = "none";
        public string ProcDumpPath { get; set; } = "";
        public bool Consent { get; set; }
        public string IsolationStatePath { get; set; } = "";

        public void Validate()
        {
            if (Edition != "Enhanced" && Edition != "Legacy")
                throw new ArgumentException("Edition must be Enhanced or Legacy.");
            if (WaitSeconds < 1 || WaitSeconds > 600 || RecordSeconds < 1 || RecordSeconds > 900)
                throw new ArgumentException("Wait must be 1–600 seconds; recording must be 1–900 seconds.");
            if (DumpMode != "none" && DumpMode != "mini" && DumpMode != "full")
                throw new ArgumentException("Dump mode must be none, mini or full.");
            if (DumpMode != "none" && (!Consent || string.IsNullOrWhiteSpace(ProcDumpPath)))
                throw new ArgumentException("Dump capture needs explicit consent and a Microsoft ProcDump executable you obtained yourself.");
        }
    }
}
