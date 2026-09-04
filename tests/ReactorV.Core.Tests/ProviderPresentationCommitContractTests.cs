using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ProviderPresentationCommitContractTests
{
    [Fact]
    public void Commit_identity_is_exact_and_case_sensitive()
    {
        Assert.True(ProviderPresentationCommitContract.Matches(
            "menu-presentation-42",
            "menu-presentation-42"));
        Assert.False(ProviderPresentationCommitContract.Matches(
            "menu-presentation-42",
            "MENU-PRESENTATION-42"));
        Assert.False(ProviderPresentationCommitContract.Matches(
            "menu-presentation-42",
            "menu-presentation-43"));
    }

    [Fact]
    public void Empty_or_oversized_identity_fails_closed()
    {
        Assert.False(ProviderPresentationCommitContract.IsValidPresentationId(null));
        Assert.False(ProviderPresentationCommitContract.IsValidPresentationId(string.Empty));
        Assert.False(ProviderPresentationCommitContract.IsValidPresentationId("   "));
        Assert.True(ProviderPresentationCommitContract.IsValidPresentationId(
            new string('p', ProviderPresentationCommitContract.MaximumPresentationIdLength)));
        Assert.False(ProviderPresentationCommitContract.IsValidPresentationId(
            new string('p', ProviderPresentationCommitContract.MaximumPresentationIdLength + 1)));
        Assert.False(ProviderPresentationCommitContract.Matches(null, null));
    }
}
