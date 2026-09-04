using System;

namespace RageWebUI.Script
{
    internal enum MenuInputLeaseState
    {
        Hidden,
        Arming,
        Interactive,
        Disarming,
    }

    internal readonly struct MenuInputLeaseFrame
    {
        internal MenuInputLeaseFrame(
            MenuInputLeaseState previousState,
            MenuInputLeaseState state,
            bool seedPhysicalState)
        {
            PreviousState = previousState;
            State = state;
            SeedPhysicalState = seedPhysicalState;
        }

        internal MenuInputLeaseState PreviousState { get; }
        internal MenuInputLeaseState State { get; }
        internal bool SeedPhysicalState { get; }
        internal bool StateChanged => PreviousState != State;
        internal bool SuppressGameInput => State != MenuInputLeaseState.Hidden;

        internal bool AcceptMenuInput => State == MenuInputLeaseState.Interactive;
    }

    /// <summary>
    /// Pure ownership policy for the managed Story-mode menu. Acquisition has
    /// an explicit arming frame so already-held controls can be seeded without
    /// becoming synthetic menu actions. Release keeps the game suppressed
    /// through a bounded grace and at least one complete neutral frame, so the
    /// click/key that closed Reactor cannot fall through to GTA's frontend.
    /// </summary>
    internal sealed class MenuInputLease
    {
        internal const int DefaultCloseGraceMilliseconds = 200;

        private readonly int _closeGraceMilliseconds;
        private long _releaseNotBeforeMilliseconds;
        private int _consecutiveNeutralDisarmingFrames;

        internal MenuInputLease(
            int closeGraceMilliseconds = DefaultCloseGraceMilliseconds)
        {
            if (closeGraceMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(closeGraceMilliseconds));

            _closeGraceMilliseconds = closeGraceMilliseconds;
        }

        internal MenuInputLeaseState State { get; private set; } =
            MenuInputLeaseState.Hidden;

        internal bool SuppressGameInput => State != MenuInputLeaseState.Hidden;

        internal bool CanForwardRawBrowserKey(
            bool overlayVisible,
            bool pointerInputMode) =>
            State == MenuInputLeaseState.Interactive &&
            overlayVisible &&
            pointerInputMode;

        internal MenuInputLeaseFrame Advance(
            bool wantsInteractiveInput,
            bool relevantInputsNeutral,
            long elapsedMilliseconds)
        {
            if (elapsedMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));

            var previousState = State;
            var seedPhysicalState = false;

            switch (State)
            {
                case MenuInputLeaseState.Hidden:
                    if (wantsInteractiveInput)
                    {
                        State = MenuInputLeaseState.Arming;
                        seedPhysicalState = true;
                    }
                    break;

                case MenuInputLeaseState.Arming:
                    if (!wantsInteractiveInput)
                    {
                        BeginDisarming(elapsedMilliseconds);
                    }
                    else if (relevantInputsNeutral)
                    {
                        // Hidden -> Arming returns from the previous call. A
                        // second frame is therefore required before input can
                        // become interactive, preserving a real seed frame.
                        State = MenuInputLeaseState.Interactive;
                    }
                    break;

                case MenuInputLeaseState.Interactive:
                    if (!wantsInteractiveInput)
                        BeginDisarming(elapsedMilliseconds);
                    break;

                case MenuInputLeaseState.Disarming:
                    if (wantsInteractiveInput)
                    {
                        State = MenuInputLeaseState.Arming;
                        _consecutiveNeutralDisarmingFrames = 0;
                        seedPhysicalState = true;
                    }
                    else
                    {
                        _consecutiveNeutralDisarmingFrames = relevantInputsNeutral
                            ? _consecutiveNeutralDisarmingFrames + 1
                            : 0;

                        // Two consecutive neutral observations prove one full
                        // frame interval remained neutral. The time guard is a
                        // bounded close grace, not a broad input re-enable.
                        if (_consecutiveNeutralDisarmingFrames >= 2 &&
                            elapsedMilliseconds >= _releaseNotBeforeMilliseconds)
                        {
                            State = MenuInputLeaseState.Hidden;
                            _consecutiveNeutralDisarmingFrames = 0;
                        }
                    }
                    break;

                default:
                    throw new InvalidOperationException(
                        "Unknown Reactor menu input lease state.");
            }

            return new MenuInputLeaseFrame(
                previousState,
                State,
                seedPhysicalState);
        }

        private void BeginDisarming(long elapsedMilliseconds)
        {
            State = MenuInputLeaseState.Disarming;
            _consecutiveNeutralDisarmingFrames = 0;
            _releaseNotBeforeMilliseconds =
                elapsedMilliseconds + _closeGraceMilliseconds;
        }
    }
}
