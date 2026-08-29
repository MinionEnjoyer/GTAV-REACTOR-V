using System;
using System.IO;

namespace RageWebUI.Script.Configuration
{
    internal static class ReactorVDataDirectory
    {
        private const string CanonicalFolderName = "ReactorV";
        private const string LegacyFolderName = "RageWebUI";

        public static string Resolve()
        {
            var localRoot = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            var canonical = Path.Combine(localRoot, CanonicalFolderName);
            var legacy = Path.Combine(localRoot, LegacyFolderName);

            // Preserve existing logs, first-run state, and browser profiles on
            // the first branded-path upgrade. A failed migration is harmless:
            // the new canonical directory is still used and the legacy data is
            // left untouched for manual recovery.
            if (!Directory.Exists(canonical) && Directory.Exists(legacy))
            {
                try
                {
                    Directory.Move(legacy, canonical);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Directory.CreateDirectory(canonical);
            return canonical;
        }
    }
}
