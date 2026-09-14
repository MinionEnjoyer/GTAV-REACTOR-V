using System.Collections.Generic;
using ReactorV.Windowing;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class VerifiedWindowPromotionPolicyTests
{
    public static IEnumerable<object[]> Outcomes()
    {
        for (var bits = 0; bits < 32; bits++)
            yield return new object[] { (bits & 1) != 0, (bits & 2) != 0,
                (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0 };
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void EveryFailureAndReadbackCombinationIsBounded(
        bool firstMove, bool firstRead, bool reset, bool secondMove, bool secondRead)
    {
        var calls = new List<string>();
        var moves = 0;
        var reads = 0;
        var result = VerifiedWindowPromotionPolicy.Apply(
            () => { calls.Add("promote"); return ++moves == 1 ? firstMove : secondMove; },
            () => { calls.Add("read"); return ++reads == 1 ? firstRead : secondRead; },
            () => { calls.Add("reset"); return reset; });

        var expected = !firstMove ? WindowPromotionOutcome.PromotionRejected
            : firstRead ? WindowPromotionOutcome.Applied
            : !reset ? WindowPromotionOutcome.ResetRejected
            : !secondMove ? WindowPromotionOutcome.PromotionRejected
            : secondRead ? WindowPromotionOutcome.Recovered : WindowPromotionOutcome.ReadbackRejected;
        Assert.Equal(expected, result);
        Assert.Equal(!firstMove ? new[] { "promote" }
            : firstRead ? new[] { "promote", "read" }
            : !reset ? new[] { "promote", "read", "reset" }
            : !secondMove ? new[] { "promote", "read", "reset", "promote" }
            : new[] { "promote", "read", "reset", "promote", "read" }, calls);
        Assert.Equal(firstMove && (firstRead || (reset && secondMove && secondRead)),
            VerifiedWindowPromotionPolicy.Succeeded(result));
    }

    [Fact]
    public void APreviousSuccessDoesNotSkipReadbackOnTheNextRequest()
    {
        Assert.Equal(WindowPromotionOutcome.Applied,
            VerifiedWindowPromotionPolicy.Apply(() => true, () => true, () => throw new System.Exception()));
        Assert.Equal(WindowPromotionOutcome.ReadbackRejected,
            VerifiedWindowPromotionPolicy.Apply(() => true, () => false, () => true));
    }
}
