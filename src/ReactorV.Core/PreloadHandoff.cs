using System;
using System.IO;
using System.Threading;

namespace RageWebUI.Core
{
    /// <summary>
    /// Coordinates the hidden WebView2 warm-up process with the in-game
    /// renderer. The process-specific name prevents a stale GTA session from
    /// dismissing a preloader that belongs to a newer launch.
    /// </summary>
    public static class PreloadHandoff
    {
        private const string ContentReadyEventPrefix = @"Local\ReactorV.ContentReady.";
        private const string RuntimeReadyEventPrefix = @"Local\ReactorV.RuntimeReady.";

        public static string EventName(int gtaProcessId)
        {
            if (gtaProcessId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gtaProcessId),
                    "The GTA process identifier must be positive.");
            }

            return ContentReadyEventPrefix + gtaProcessId;
        }

        public static string RuntimeReadyEventName(int gtaProcessId)
        {
            ValidateProcessId(gtaProcessId);
            return RuntimeReadyEventPrefix + gtaProcessId;
        }

        public static EventWaitHandle CreateWaitHandle(int gtaProcessId) =>
            new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                EventName(gtaProcessId));

        public static bool TrySignal(int gtaProcessId)
            => TrySignalExisting(EventName(gtaProcessId));

        public static EventWaitHandle CreateRuntimeReadyWaitHandle(int gtaProcessId) =>
            new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                RuntimeReadyEventName(gtaProcessId));

        public static bool TrySignalRuntimeReady(int gtaProcessId)
            => TrySignalExisting(RuntimeReadyEventName(gtaProcessId));

        public static string PreloadDataReadyEventName(int gtaProcessId) =>
            PreloadDataCache.ReadyEventName(gtaProcessId);

        public static EventWaitHandle CreatePreloadDataReadyWaitHandle(int gtaProcessId) =>
            new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                PreloadDataReadyEventName(gtaProcessId));

        public static bool TrySignalPreloadDataReady(int gtaProcessId)
            => TrySignalExisting(PreloadDataReadyEventName(gtaProcessId));

        private static void ValidateProcessId(int gtaProcessId)
        {
            if (gtaProcessId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gtaProcessId),
                    "The GTA process identifier must be positive.");
            }
        }

        private static bool TrySignalExisting(string eventName)
        {
            try
            {
                using (var handoff = EventWaitHandle.OpenExisting(
                    eventName))
                {
                    return handoff.Set();
                }
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
