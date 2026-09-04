namespace RageWebUI.Core
{
    public enum DefaultMenuIntentDeadlineAction
    {
        None = 0,
        CompleteClaim = 1,
        RefreshVisibleInitializer = 2,
        ExpireWithoutHide = 3,
    }

    /// <summary>
    /// Pure decision boundary for the persistent bootstrap timer. Claim wins
    /// over a simultaneous deadline so an already presented typed menu is
    /// never hidden by the initializer's old timeout.
    /// </summary>
    public static class DefaultMenuIntentDeadlinePolicy
    {
        public static DefaultMenuIntentDeadlineAction Evaluate(
            bool deadlineArmed,
            bool claimObserved,
            bool deadlineExpired,
            bool initializerVisible)
        {
            if (!deadlineArmed)
                return DefaultMenuIntentDeadlineAction.None;
            if (claimObserved)
                return DefaultMenuIntentDeadlineAction.CompleteClaim;
            if (!deadlineExpired)
                return DefaultMenuIntentDeadlineAction.None;
            // A visible initializer is an explicit, still-owned user request.
            // Slow ScriptHook startup must never turn it into an empty F9
            // handoff merely because a wall-clock deadline elapsed. Its
            // bounded lease is refreshed until an atomic claim or an explicit
            // close/cancellation boundary wins.
            return initializerVisible
                ? DefaultMenuIntentDeadlineAction.RefreshVisibleInitializer
                : DefaultMenuIntentDeadlineAction.ExpireWithoutHide;
        }
    }
}
