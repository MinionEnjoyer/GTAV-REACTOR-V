using System;

namespace ReactorV.Windowing
{
    internal enum WindowPromotionOutcome
    {
        Applied,
        Recovered,
        PromotionRejected,
        ResetRejected,
        ReadbackRejected
    }

    internal static class VerifiedWindowPromotionPolicy
    {
        // A successful request is not proof that the native z-order changed.
        // Only repair a success/readback disagreement, once, synchronously.
        internal static WindowPromotionOutcome Apply(
            Func<bool> promote, Func<bool> isTopmost, Func<bool> reset)
        {
            if (!promote()) return WindowPromotionOutcome.PromotionRejected;
            if (isTopmost()) return WindowPromotionOutcome.Applied;
            if (!reset()) return WindowPromotionOutcome.ResetRejected;
            if (!promote()) return WindowPromotionOutcome.PromotionRejected;
            return isTopmost() ? WindowPromotionOutcome.Recovered
                : WindowPromotionOutcome.ReadbackRejected;
        }

        internal static bool Succeeded(WindowPromotionOutcome outcome) =>
            outcome == WindowPromotionOutcome.Applied || outcome == WindowPromotionOutcome.Recovered;
    }
}
