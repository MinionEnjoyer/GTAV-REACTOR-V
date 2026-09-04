using System;
using System.IO;

namespace ReactorV
{
    internal static class NormalExitMarkerCleanup
    {
        private const string Allin1MarkerName = "ALLIN1_session.lock";

        internal static bool TryClearAllin1Marker(
            string gtaRoot,
            int exitCode,
            out string outcome)
        {
            if (exitCode != 0)
            {
                outcome = "preserved-nonzero-exit";
                return false;
            }

            try
            {
                var root = Path.GetFullPath(gtaRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (root.Length == 0)
                {
                    outcome = "invalid-game-root";
                    return false;
                }

                var marker = Path.GetFullPath(Path.Combine(
                    root,
                    "scripts",
                    Allin1MarkerName));
                var rootPrefix = root + Path.DirectorySeparatorChar;
                if (!marker.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    outcome = "marker-outside-game-root";
                    return false;
                }

                if (!File.Exists(marker))
                {
                    outcome = "marker-absent";
                    return true;
                }

                File.Delete(marker);
                if (File.Exists(marker))
                {
                    outcome = "marker-still-present";
                    return false;
                }

                outcome = "marker-cleared";
                return true;
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is ArgumentException ||
                error is NotSupportedException)
            {
                outcome = "marker-cleanup-failed-" + error.GetType().Name;
                return false;
            }
        }
    }
}
