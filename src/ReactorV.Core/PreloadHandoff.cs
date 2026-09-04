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
        private const string F9OwnershipReleasedEventPrefix =
            @"Local\ReactorV.F9OwnershipReleased.";
        private const string DefaultMenuIntentEventPrefix =
            @"Local\ReactorV.DefaultMenuIntent.";
        private const string DefaultMenuIntentClaimedEventPrefix =
            @"Local\ReactorV.DefaultMenuIntentClaimed.";
        private const string DefaultMenuIntentActiveEventPrefix =
            @"Local\ReactorV.DefaultMenuIntentActive.";
        private const string DefaultMenuIntentCancelledEventPrefix =
            @"Local\ReactorV.DefaultMenuIntentCancelled.";
        private const string DefaultMenuIntentMutexPrefix =
            @"Local\ReactorV.DefaultMenuIntentMutex.";

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

        public static string F9OwnershipReleasedEventName(int gtaProcessId)
        {
            ValidateProcessId(gtaProcessId);
            return F9OwnershipReleasedEventPrefix + gtaProcessId;
        }

        /// <summary>
        /// Process-scoped, typed startup intent emitted when the native F9
        /// owner opens Reactor before the managed provider is ready. It is not
        /// a replay of the key press: one opted-in gameplay extension may
        /// consume the intent once its own default menu is actually ready.
        /// </summary>
        public static string DefaultMenuIntentEventName(int gtaProcessId)
        {
            ValidateProcessId(gtaProcessId);
            return DefaultMenuIntentEventPrefix + gtaProcessId;
        }

        /// <summary>
        /// Process-scoped acknowledgement emitted only after an opted-in
        /// extension has both consumed the startup intent and successfully
        /// presented its typed default menu. The bootstrap host uses it to
        /// disarm expiry without hiding the newly presented menu.
        /// </summary>
        public static string DefaultMenuIntentClaimedEventName(int gtaProcessId)
        {
            ValidateProcessId(gtaProcessId);
            return DefaultMenuIntentClaimedEventPrefix + gtaProcessId;
        }

        public static string DefaultMenuIntentActiveEventName(int gtaProcessId)
        {
            ValidateProcessId(gtaProcessId);
            return DefaultMenuIntentActiveEventPrefix + gtaProcessId;
        }

        public static string DefaultMenuIntentCancelledEventName(int gtaProcessId)
        {
            ValidateProcessId(gtaProcessId);
            return DefaultMenuIntentCancelledEventPrefix + gtaProcessId;
        }

        public static string DefaultMenuIntentMutexName(int gtaProcessId)
        {
            ValidateProcessId(gtaProcessId);
            return DefaultMenuIntentMutexPrefix + gtaProcessId;
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

        /// <summary>
        /// Test/harness handle for the native-to-managed F9 ownership edge.
        /// Production creates and signals this event in ReactorV.Bootstrap.
        /// </summary>
        public static EventWaitHandle CreateF9OwnershipReleasedWaitHandle(
            int gtaProcessId) =>
            new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                F9OwnershipReleasedEventName(gtaProcessId));

        /// <summary>
        /// Host-side handle for the bounded default-menu intent. Auto-reset
        /// makes successful consumption atomic and exactly-once. Explicit
        /// close, hidden expiry, and a disconnect with no visibly owned
        /// initializer reset it; a visible active initializer survives a
        /// transient provider reconnect.
        /// </summary>
        public static EventWaitHandle CreateDefaultMenuIntentWaitHandle(
            int gtaProcessId) =>
            new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                DefaultMenuIntentEventName(gtaProcessId));

        /// <summary>
        /// Host-side acknowledgement handle. Auto-reset keeps the claim tied
        /// to one presentation and avoids stale acknowledgement replay.
        /// </summary>
        public static EventWaitHandle CreateDefaultMenuIntentClaimedWaitHandle(
            int gtaProcessId) =>
            new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                DefaultMenuIntentClaimedEventName(gtaProcessId));

        public static EventWaitHandle CreateDefaultMenuIntentActiveWaitHandle(
            int gtaProcessId) =>
            new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                DefaultMenuIntentActiveEventName(gtaProcessId));

        public static EventWaitHandle CreateDefaultMenuIntentCancelledWaitHandle(
            int gtaProcessId) =>
            new EventWaitHandle(
                true,
                EventResetMode.ManualReset,
                DefaultMenuIntentCancelledEventName(gtaProcessId));

        /// <summary>
        /// Starts one explicit startup-menu request. The named mutex keeps
        /// arm/cancel/consume/restore transitions atomic across the preloader
        /// and managed provider without sharing arbitrary input.
        /// </summary>
        public static bool TryArmDefaultMenuIntent(int gtaProcessId) =>
            WithDefaultMenuIntentLock(gtaProcessId, () =>
            {
                using (var intent = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentEventName(gtaProcessId)))
                using (var claimed = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentClaimedEventName(gtaProcessId)))
                using (var active = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentActiveEventName(gtaProcessId)))
                using (var cancelled = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentCancelledEventName(gtaProcessId)))
                {
                    intent.Reset();
                    claimed.Reset();
                    cancelled.Reset();
                    active.Set();
                    return intent.Set();
                }
            });

        public static bool TryCancelDefaultMenuIntent(int gtaProcessId) =>
            WithDefaultMenuIntentLock(gtaProcessId, () =>
            {
                using (var intent = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentEventName(gtaProcessId)))
                using (var claimed = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentClaimedEventName(gtaProcessId)))
                using (var active = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentActiveEventName(gtaProcessId)))
                using (var cancelled = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentCancelledEventName(gtaProcessId)))
                {
                    cancelled.Set();
                    active.Reset();
                    claimed.Reset();
                    intent.Reset();
                    return true;
                }
            });

        /// <summary>
        /// Consumes one startup default-menu intent if the native host has one
        /// pending for this exact GTA process. Absence fails closed: callers
        /// must not infer a request from provider connection or visibility.
        /// </summary>
        public static bool TryConsumeDefaultMenuIntent(int gtaProcessId)
        {
            return WithDefaultMenuIntentLock(gtaProcessId, () =>
            {
                using (var intent = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentEventName(gtaProcessId)))
                using (var active = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentActiveEventName(gtaProcessId)))
                using (var cancelled = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentCancelledEventName(gtaProcessId)))
                {
                    return active.WaitOne(0) &&
                        !cancelled.WaitOne(0) &&
                        intent.WaitOne(0);
                }
            });
        }

        /// <summary>
        /// Restores an atomically reserved intent after a transient host
        /// presentation failure. Explicit close wins regardless of ordering:
        /// both operations share the same process-scoped mutex and restore
        /// refuses a cancelled/inactive request.
        /// </summary>
        public static bool TryRestoreDefaultMenuIntent(int gtaProcessId) =>
            WithDefaultMenuIntentLock(gtaProcessId, () =>
            {
                using (var intent = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentEventName(gtaProcessId)))
                using (var active = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentActiveEventName(gtaProcessId)))
                using (var cancelled = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentCancelledEventName(gtaProcessId)))
                {
                    return active.WaitOne(0) &&
                        !cancelled.WaitOne(0) &&
                        intent.Set();
                }
            });

        /// <summary>
        /// Commits a successfully queued typed presentation. The preloader
        /// later takes this acknowledgement and disarms expiry without hiding
        /// the provider menu. An explicit close that won the mutex rejects the
        /// commit and the caller must remove its queued presentation.
        /// </summary>
        public static bool TryCommitDefaultMenuIntentClaim(int gtaProcessId) =>
            WithDefaultMenuIntentLock(gtaProcessId, () =>
            {
                using (var claimed = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentClaimedEventName(gtaProcessId)))
                using (var active = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentActiveEventName(gtaProcessId)))
                using (var cancelled = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentCancelledEventName(gtaProcessId)))
                {
                    return active.WaitOne(0) &&
                        !cancelled.WaitOne(0) &&
                        claimed.Set();
                }
            });

        public static bool TryTakeDefaultMenuIntentClaim(int gtaProcessId) =>
            WithDefaultMenuIntentLock(gtaProcessId, () =>
            {
                using (var intent = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentEventName(gtaProcessId)))
                using (var claimed = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentClaimedEventName(gtaProcessId)))
                using (var active = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentActiveEventName(gtaProcessId)))
                using (var cancelled = EventWaitHandle.OpenExisting(
                           DefaultMenuIntentCancelledEventName(gtaProcessId)))
                {
                    if (cancelled.WaitOne(0) || !claimed.WaitOne(0))
                        return false;
                    intent.Reset();
                    active.Reset();
                    return true;
                }
            });

        public static bool IsDefaultMenuIntentActive(int gtaProcessId) =>
            ReadDefaultMenuIntentEvent(
                DefaultMenuIntentActiveEventName(gtaProcessId),
                failClosedValue: false);

        public static bool IsDefaultMenuIntentCancelled(int gtaProcessId) =>
            ReadDefaultMenuIntentEvent(
                DefaultMenuIntentCancelledEventName(gtaProcessId),
                failClosedValue: true);

        /// <summary>
        /// Production dispatch seam shared by Script and packaged harnesses.
        /// A queued startup presentation remains eligible only while its exact
        /// process request is active and no explicit close/expiry won.
        /// </summary>
        public static bool CanDispatchDefaultMenuIntent(int gtaProcessId) =>
            IsDefaultMenuIntentActive(gtaProcessId) &&
            !IsDefaultMenuIntentCancelled(gtaProcessId);

        /// <summary>
        /// Returns true when managed code may consume F9. An absent boundary
        /// means the native bootstrap is not installed/running, so the managed
        /// fallback remains usable. If the native boundary exists, managed
        /// input fails closed until bootstrap signals its one-shot release.
        /// </summary>
        public static bool ManagedOwnsF9(int gtaProcessId)
        {
            var eventName = F9OwnershipReleasedEventName(gtaProcessId);
            try
            {
                using (var handoff = EventWaitHandle.OpenExisting(eventName))
                {
                    return handoff.WaitOne(0);
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public static string PreloadDataReadyEventName(int gtaProcessId) =>
            PreloadDataCache.ReadyEventName(gtaProcessId);

        public static EventWaitHandle CreatePreloadDataReadyWaitHandle(int gtaProcessId) =>
            new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                PreloadDataReadyEventName(gtaProcessId));

        public static bool TrySignalPreloadDataReady(
            int gtaProcessId,
            PreloadDataBuildResult? result)
        {
            if (!PreloadDataCache.IsReadyForHandoff(gtaProcessId, result))
            {
                return false;
            }
            return TrySignalExisting(PreloadDataReadyEventName(gtaProcessId));
        }

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

        private static bool ReadDefaultMenuIntentEvent(
            string eventName,
            bool failClosedValue)
        {
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(eventName))
                    return signal.WaitOne(0);
            }
            catch (UnauthorizedAccessException) { return failClosedValue; }
            catch (WaitHandleCannotBeOpenedException) { return failClosedValue; }
            catch (IOException) { return failClosedValue; }
        }

        private static bool WithDefaultMenuIntentLock(
            int gtaProcessId,
            Func<bool> operation)
        {
            ValidateProcessId(gtaProcessId);
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            var acquired = false;
            try
            {
                using (var mutex = new Mutex(
                           false,
                           DefaultMenuIntentMutexName(gtaProcessId)))
                {
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(1));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }
                    if (!acquired) return false;
                    try { return operation(); }
                    finally
                    {
                        mutex.ReleaseMutex();
                        acquired = false;
                    }
                }
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (IOException) { return false; }
        }
    }
}
