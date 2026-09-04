using System;
using System.Linq;
using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class MenuInputLeaseTests
{
    [Fact]
    public void Acquisition_has_a_seeded_arming_frame_before_interaction()
    {
        var lease = new MenuInputLease();

        var acquired = lease.Advance(
            wantsInteractiveInput: true,
            relevantInputsNeutral: true,
            elapsedMilliseconds: 10);

        Assert.Equal(MenuInputLeaseState.Hidden, acquired.PreviousState);
        Assert.Equal(MenuInputLeaseState.Arming, acquired.State);
        Assert.True(acquired.SeedPhysicalState);
        Assert.True(acquired.SuppressGameInput);
        Assert.False(acquired.AcceptMenuInput);

        var armed = lease.Advance(
            wantsInteractiveInput: true,
            relevantInputsNeutral: true,
            elapsedMilliseconds: 11);

        Assert.Equal(MenuInputLeaseState.Interactive, armed.State);
        Assert.False(armed.SeedPhysicalState);
        Assert.True(armed.SuppressGameInput);
        Assert.True(armed.AcceptMenuInput);
    }

    [Fact]
    public void Arming_waits_for_held_input_to_become_neutral()
    {
        var lease = new MenuInputLease();
        lease.Advance(true, relevantInputsNeutral: false, elapsedMilliseconds: 0);

        var held = lease.Advance(
            wantsInteractiveInput: true,
            relevantInputsNeutral: false,
            elapsedMilliseconds: 16);
        var released = lease.Advance(
            wantsInteractiveInput: true,
            relevantInputsNeutral: true,
            elapsedMilliseconds: 32);

        Assert.Equal(MenuInputLeaseState.Arming, held.State);
        Assert.False(held.AcceptMenuInput);
        Assert.Equal(MenuInputLeaseState.Interactive, released.State);
    }

    [Fact]
    public void Disarming_suppresses_through_grace_and_a_complete_neutral_frame()
    {
        var lease = CreateInteractiveLease();

        var closing = lease.Advance(
            wantsInteractiveInput: false,
            relevantInputsNeutral: false,
            elapsedMilliseconds: 100);
        var firstNeutral = lease.Advance(
            wantsInteractiveInput: false,
            relevantInputsNeutral: true,
            elapsedMilliseconds: 300);
        var released = lease.Advance(
            wantsInteractiveInput: false,
            relevantInputsNeutral: true,
            elapsedMilliseconds: 301);

        Assert.Equal(MenuInputLeaseState.Disarming, closing.State);
        Assert.True(closing.SuppressGameInput);
        Assert.False(closing.AcceptMenuInput);
        Assert.Equal(MenuInputLeaseState.Disarming, firstNeutral.State);
        Assert.True(firstNeutral.SuppressGameInput);
        Assert.Equal(MenuInputLeaseState.Hidden, released.State);
        Assert.False(released.SuppressGameInput);
    }

    [Fact]
    public void Disarming_does_not_release_before_the_close_grace()
    {
        var lease = CreateInteractiveLease();
        lease.Advance(false, relevantInputsNeutral: true, elapsedMilliseconds: 100);
        lease.Advance(false, relevantInputsNeutral: true, elapsedMilliseconds: 116);

        var beforeGrace = lease.Advance(
            wantsInteractiveInput: false,
            relevantInputsNeutral: true,
            elapsedMilliseconds: 299);
        var afterGrace = lease.Advance(
            wantsInteractiveInput: false,
            relevantInputsNeutral: true,
            elapsedMilliseconds: 300);

        Assert.Equal(MenuInputLeaseState.Disarming, beforeGrace.State);
        Assert.Equal(MenuInputLeaseState.Hidden, afterGrace.State);
    }

    [Fact]
    public void A_held_input_resets_the_disarming_neutral_proof()
    {
        var lease = CreateInteractiveLease();
        lease.Advance(false, relevantInputsNeutral: false, elapsedMilliseconds: 100);
        lease.Advance(false, relevantInputsNeutral: true, elapsedMilliseconds: 300);
        lease.Advance(false, relevantInputsNeutral: false, elapsedMilliseconds: 301);

        var firstNeutralAgain = lease.Advance(false, true, 302);
        var secondNeutralAgain = lease.Advance(false, true, 303);

        Assert.Equal(MenuInputLeaseState.Disarming, firstNeutralAgain.State);
        Assert.Equal(MenuInputLeaseState.Hidden, secondNeutralAgain.State);
    }

    [Fact]
    public void Reopening_during_disarming_rearms_and_reseeds()
    {
        var lease = CreateInteractiveLease();
        lease.Advance(false, relevantInputsNeutral: false, elapsedMilliseconds: 100);

        var reopened = lease.Advance(
            wantsInteractiveInput: true,
            relevantInputsNeutral: true,
            elapsedMilliseconds: 101);

        Assert.Equal(MenuInputLeaseState.Arming, reopened.State);
        Assert.True(reopened.SeedPhysicalState);
        Assert.True(reopened.SuppressGameInput);
        Assert.False(reopened.AcceptMenuInput);
    }

    [Fact]
    public void Raw_browser_keys_are_forwarded_only_by_an_interactive_lease()
    {
        var lease = new MenuInputLease();
        Assert.False(lease.CanForwardRawBrowserKey(
            overlayVisible: true,
            pointerInputMode: true));

        lease.Advance(true, relevantInputsNeutral: true, elapsedMilliseconds: 0);
        Assert.False(lease.CanForwardRawBrowserKey(
            overlayVisible: true,
            pointerInputMode: true));

        lease.Advance(true, relevantInputsNeutral: true, elapsedMilliseconds: 1);
        Assert.True(lease.CanForwardRawBrowserKey(
            overlayVisible: true,
            pointerInputMode: true));
        Assert.False(lease.CanForwardRawBrowserKey(
            overlayVisible: false,
            pointerInputMode: true));
        Assert.False(lease.CanForwardRawBrowserKey(
            overlayVisible: true,
            pointerInputMode: false));

        lease.Advance(false, relevantInputsNeutral: false, elapsedMilliseconds: 2);
        Assert.False(lease.CanForwardRawBrowserKey(
            overlayVisible: true,
            pointerInputMode: true));
    }

    [Fact]
    public void Input_contract_covers_all_groups_pause_and_pointer_controls()
    {
        Assert.Equal(new[] { 0, 1, 2 }, GameplayMenuInputBindings.ControlGroups);
        Assert.Contains(
            GameplayMenuInputBindings.FrontendPauseControl,
            GameplayMenuInputBindings.RelevantControls);
        Assert.Contains(
            GameplayMenuInputBindings.FrontendPauseAlternateControl,
            GameplayMenuInputBindings.RelevantControls);
        Assert.Contains(
            GameplayMenuInputBindings.CursorAcceptControl,
            GameplayMenuInputBindings.RelevantControls);
        Assert.Contains(
            GameplayMenuInputBindings.CursorCancelControl,
            GameplayMenuInputBindings.RelevantControls);
        Assert.Equal(
            GameplayMenuInputBindings.RelevantControls.Count,
            GameplayMenuInputBindings.RelevantControls.Distinct().Count());
    }

    [Fact]
    public void Invalid_grace_or_time_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MenuInputLease(-1));
        var lease = new MenuInputLease();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            lease.Advance(false, true, -1));
    }

    private static MenuInputLease CreateInteractiveLease()
    {
        var lease = new MenuInputLease();
        lease.Advance(true, relevantInputsNeutral: true, elapsedMilliseconds: 0);
        lease.Advance(true, relevantInputsNeutral: true, elapsedMilliseconds: 1);
        Assert.Equal(MenuInputLeaseState.Interactive, lease.State);
        return lease;
    }
}
