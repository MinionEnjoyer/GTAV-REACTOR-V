using System;

namespace RageWebUI.Core
{
    /// <summary>
    /// One-shot, process-bound user intent minted by Reactor's trusted F9
    /// handler. The host records its own monotonic deadline when it receives
    /// the token; wall-clock time is never trusted across the pipe boundary.
    /// </summary>
    public readonly struct ProviderInputIntentToken
    {
        public ProviderInputIntentToken(int processId, long epoch, int lifetimeMilliseconds)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
            if (lifetimeMilliseconds <= 0 ||
                lifetimeMilliseconds > ProviderInputIntentGate.MaximumArmLifetimeMilliseconds)
            {
                throw new ArgumentOutOfRangeException(nameof(lifetimeMilliseconds));
            }

            ProcessId = processId;
            Epoch = epoch;
            LifetimeMilliseconds = lifetimeMilliseconds;
        }

        public int ProcessId { get; }
        public long Epoch { get; }
        public int LifetimeMilliseconds { get; }
    }

    /// <summary>
    /// Pure one-shot authority ledger. Arm and bind are distinct so a physical
    /// F9 edge cannot authorize an unrelated already-active presentation. A
    /// bound token may be consumed only once and only by its exact ID.
    /// </summary>
    public sealed class ProviderInputIntentGate
    {
        public const int DefaultArmLifetimeMilliseconds = 1500;
        public const int MaximumArmLifetimeMilliseconds = 2000;
        public const int BoundPresentationLifetimeMilliseconds = 6000;

        private readonly int _expectedProcessId;
        private long _highestEpoch;
        private long _armedEpoch;
        private long _armedDeadlineMilliseconds;
        private long _boundEpoch;
        private long _boundDeadlineMilliseconds;
        private string _boundPresentationId = string.Empty;
        private int _providerSessionGeneration;

        public ProviderInputIntentGate(int expectedProcessId)
        {
            if (expectedProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedProcessId));
            _expectedProcessId = expectedProcessId;
        }

        public bool TryArm(
            ProviderInputIntentToken token,
            long monotonicMilliseconds)
            => TryArm(token, monotonicMilliseconds, _providerSessionGeneration);

        public bool TryArm(
            ProviderInputIntentToken token,
            long monotonicMilliseconds,
            int providerSessionGeneration)
        {
            if (monotonicMilliseconds < 0 ||
                providerSessionGeneration != _providerSessionGeneration ||
                token.ProcessId != _expectedProcessId ||
                token.Epoch <= _highestEpoch ||
                token.LifetimeMilliseconds <= 0 ||
                token.LifetimeMilliseconds > MaximumArmLifetimeMilliseconds)
            {
                return false;
            }

            _highestEpoch = token.Epoch;
            _armedEpoch = token.Epoch;
            _armedDeadlineMilliseconds =
                monotonicMilliseconds + token.LifetimeMilliseconds;
            ClearBound();
            return true;
        }

        public bool TryBind(
            int processId,
            long epoch,
            string? presentationId,
            long monotonicMilliseconds)
            => TryBind(
                processId,
                epoch,
                presentationId,
                monotonicMilliseconds,
                _providerSessionGeneration);

        public bool TryBind(
            int processId,
            long epoch,
            string? presentationId,
            long monotonicMilliseconds,
            int providerSessionGeneration)
        {
            if (monotonicMilliseconds < 0 ||
                providerSessionGeneration != _providerSessionGeneration ||
                processId != _expectedProcessId ||
                epoch <= 0 ||
                epoch != _armedEpoch ||
                monotonicMilliseconds > _armedDeadlineMilliseconds ||
                !ProviderPresentationCommitContract.IsValidPresentationId(
                    presentationId))
            {
                return false;
            }

            _boundEpoch = epoch;
            _boundPresentationId = presentationId!;
            _boundDeadlineMilliseconds =
                monotonicMilliseconds + BoundPresentationLifetimeMilliseconds;
            ClearArmed();
            return true;
        }

        public bool TryConsume(
            string? presentationId,
            long monotonicMilliseconds,
            out long epoch)
            => TryConsume(
                presentationId,
                monotonicMilliseconds,
                _providerSessionGeneration,
                out epoch);

        public bool TryConsume(
            string? presentationId,
            long monotonicMilliseconds,
            int providerSessionGeneration,
            out long epoch)
        {
            epoch = 0;
            if (monotonicMilliseconds < 0 ||
                providerSessionGeneration != _providerSessionGeneration ||
                _boundEpoch <= 0 ||
                monotonicMilliseconds > _boundDeadlineMilliseconds)
            {
                ClearBound();
                return false;
            }
            if (!ProviderPresentationCommitContract.Matches(
                    _boundPresentationId,
                    presentationId))
            {
                return false;
            }

            epoch = _boundEpoch;
            ClearBound();
            return true;
        }

        public void Cancel(int processId, long epoch)
        {
            if (processId != _expectedProcessId || epoch <= 0)
                return;
            if (_armedEpoch == epoch) ClearArmed();
            if (_boundEpoch == epoch) ClearBound();
        }

        public bool BeginProviderSession(int providerSessionGeneration)
        {
            if (providerSessionGeneration <= _providerSessionGeneration)
                return false;

            _providerSessionGeneration = providerSessionGeneration;
            _highestEpoch = 0;
            ClearArmed();
            ClearBound();
            return true;
        }

        public bool RevokeProviderSession(int providerSessionGeneration)
        {
            if (providerSessionGeneration != _providerSessionGeneration)
                return false;

            ClearArmed();
            ClearBound();
            return true;
        }

        private void ClearArmed()
        {
            _armedEpoch = 0;
            _armedDeadlineMilliseconds = 0;
        }

        private void ClearBound()
        {
            _boundEpoch = 0;
            _boundDeadlineMilliseconds = 0;
            _boundPresentationId = string.Empty;
        }
    }
}
