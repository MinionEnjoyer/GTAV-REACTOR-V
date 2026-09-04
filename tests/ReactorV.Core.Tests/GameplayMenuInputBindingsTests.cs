using System.Linq;
using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class GameplayMenuInputBindingsTests
{
    [Fact]
    public void Matches_the_allin1_05_frontend_controller_contract()
    {
        var bindings = GameplayMenuInputBindings.All
            .ToDictionary(value => value.Action, value => value.Control);

        Assert.Equal(188, bindings["navigate-up"]);
        Assert.Equal(187, bindings["navigate-down"]);
        Assert.Equal(189, bindings["navigate-left"]);
        Assert.Equal(190, bindings["navigate-right"]);
        Assert.Equal(201, bindings["accept"]);
        Assert.Equal(202, bindings["back"]);
        Assert.Equal(205, bindings["previous-page"]);
        Assert.Equal(206, bindings["next-page"]);
        Assert.Equal(207, bindings["previous-category"]);
        Assert.Equal(208, bindings["next-category"]);
        Assert.Equal(204, bindings["filter-next"]);
        Assert.Equal(203, bindings["search"]);
        Assert.Equal(191, bindings["favorite"]);
        Assert.Equal(13, bindings.Count);
    }

    [Fact]
    public void Every_control_and_semantic_action_is_unique()
    {
        Assert.Equal(
            GameplayMenuInputBindings.All.Count,
            GameplayMenuInputBindings.All.Select(value => value.Control).Distinct().Count());
        Assert.Equal(
            GameplayMenuInputBindings.All.Count,
            GameplayMenuInputBindings.All.Select(value => value.Action).Distinct().Count());
    }

    [Theory]
    [InlineData("back", true, false)]
    [InlineData("back", false, true)]
    [InlineData("accept", true, true)]
    [InlineData("navigate-left", true, true)]
    public void Physical_secondary_edge_suppresses_exactly_one_duplicate_game_back(
        string action,
        bool physicalSecondaryBackPosted,
        bool expected)
    {
        Assert.Equal(
            expected,
            GameplayMenuInputBindings.ShouldEmitGameSemanticAction(
                action,
                physicalSecondaryBackPosted));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void Disabled_button_semantics_emit_only_on_the_down_edge(
        bool isDown,
        bool wasDown,
        bool expected)
    {
        Assert.Equal(
            expected,
            GameplayMenuInputBindings.IsButtonPressEdge(isDown, wasDown));
    }
}
