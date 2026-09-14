namespace RageWebUI.Script
{
    internal enum PassiveHudPresentationAction { None, Show, Hide }
    internal enum PassiveHudPresentationState { Idle, Requested, Presented, CoolingDown, Exhausted }

    // Requested visibility is not proof of host visibility. Allow one delayed
    // retry per active HUD interval, then wait for a fresh activation. A menu
    // preemption must reset this gate without hiding the new menu's surface.
    internal sealed class PassiveHudPresentationGate
    {
        internal const int RevealTimeoutMilliseconds = 4000;
        internal const int RetryDelayMilliseconds = 1000;
        internal const int MaximumAttempts = 2;
        private long _deadline;
        private long _retryAt;
        private bool _sawNativeVisible;
        internal int Attempts { get; private set; }
        internal PassiveHudPresentationState State { get; private set; }
        internal bool IsRequested => State == PassiveHudPresentationState.Requested ||
            State == PassiveHudPresentationState.Presented;

        internal PassiveHudPresentationAction Update(
            long now, bool active, bool surfaceAvailable, bool hostVisible, bool presentationVerified = false)
        {
            if (!surfaceAvailable)
            {
                Reset();
                return PassiveHudPresentationAction.None;
            }
            if (!active)
            {
                var hide = IsRequested;
                Reset();
                return hide ? PassiveHudPresentationAction.Hide : PassiveHudPresentationAction.None;
            }
            if (IsRequested)
            {
                if (hostVisible && presentationVerified)
                {
                    State = PassiveHudPresentationState.Presented;
                    return PassiveHudPresentationAction.None;
                }
                if (State == PassiveHudPresentationState.Presented ||
                    (_sawNativeVisible && !hostVisible) || now >= _deadline)
                {
                    State = Attempts >= MaximumAttempts
                        ? PassiveHudPresentationState.Exhausted : PassiveHudPresentationState.CoolingDown;
                    _retryAt = now + RetryDelayMilliseconds;
                    return PassiveHudPresentationAction.Hide;
                }
                _sawNativeVisible |= hostVisible;
                return PassiveHudPresentationAction.None;
            }
            if (Attempts >= MaximumAttempts || now < _retryAt || hostVisible)
                return PassiveHudPresentationAction.None;
            Attempts++;
            _sawNativeVisible = false;
            State = PassiveHudPresentationState.Requested;
            _deadline = now + RevealTimeoutMilliseconds;
            return PassiveHudPresentationAction.Show;
        }

        internal void Reset()
        {
            State = PassiveHudPresentationState.Idle;
            Attempts = 0;
            _deadline = _retryAt = 0;
            _sawNativeVisible = false;
        }
    }
}
