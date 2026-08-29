using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RageWebUI.Core.Protocol;
using ReactorV.Integration;
using Xunit;

namespace RageWebUI.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReactorIntegrationCollection
{
    public const string Name = "Reactor integration registry";
}

[Collection(ReactorIntegrationCollection.Name)]
public sealed class ReactorIntegrationTests : IDisposable
{
    public ReactorIntegrationTests() => ReactorHostApi.Reset();

    public void Dispose() => ReactorHostApi.Reset();

    [Fact]
    public void Registration_describes_every_declarative_menu_kind()
    {
        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder =>
            {
                builder.AddAction(ReadAction(allowAdditionalParameters: true), (_, __) => ReactorActionResult.Success());
                builder.AddMenu(new ReactorMenuDescriptor(
                    "details",
                    "Details",
                    new ReactorMenuNode[] { new ReactorStatusNode("detail-status", "State", "Ready") }));
                builder.AddMenu(new ReactorMenuDescriptor(
                    "main",
                    "Main",
                    new ReactorMenuNode[]
                    {
                        new ReactorActionNode("run", "Run", "read"),
                        new ReactorToggleNode("toggle", "Toggle", "read", true),
                        new ReactorChoiceNode("choice", "Choice", "read", new[]
                        {
                            new ReactorChoiceOption("one", "One"),
                            new ReactorChoiceOption("two", "Two"),
                        }, "one"),
                        new ReactorRangeNode("range", "Range", "read", 5, 0, 10, 1),
                        new ReactorTextNode("text", "Text", "read"),
                        new ReactorSearchNode("search", "Search", "read"),
                        new ReactorKeybindNode("keybind", "Key", "read", "F10"),
                        new ReactorTabsNode("tabs", "Tabs", new[]
                        {
                            new ReactorMenuTab("first", "First", new ReactorMenuNode[]
                            {
                                new ReactorStatusNode("tab-status", "Status", "Online", "success"),
                            }),
                        }, "first"),
                        new ReactorListNode("list", "List", new ReactorMenuNode[]
                        {
                            new ReactorSeparatorNode("list-separator"),
                            new ReactorActionNode("list-action", "List action", "read"),
                        }),
                        new ReactorGridNode("grid", "Grid", new ReactorMenuNode[]
                        {
                            new ReactorMediaNode("media", "Preview", "asset://preview.png"),
                            new ReactorProgressNode("progress", "Progress", 0.5),
                        }, columns: 2),
                        new ReactorPaginationNode("pages", "Pages", "read", 2, 5),
                        new ReactorSeparatorNode("separator", "More"),
                        new ReactorSubmenuNode("submenu", "Details", "details"),
                    },
                    order: 10));
            });

        var extensions = ReactorHostApi.DescribeExtensions();
        Assert.Single(extensions);
        Assert.Equal(ReactorApi.ExtensionApiVersion, extensions[0]!.Value<int>("extensionApiVersion"));
        Assert.Equal("example.extension", extensions[0]!.Value<string>("id"));

        var menus = ReactorHostApi.DescribeMenus("example.extension", "main");
        var kinds = Descendants(menus[0]!["nodes"]!)
            .OfType<JObject>()
            .Select(value => value.Value<string>("kind"))
            .Where(value => value != null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var expected in Enum.GetNames(typeof(ReactorMenuNodeKind)).Select(value => value.ToLowerInvariant()))
            Assert.Contains(expected, kinds);
        Assert.True(ReactorHostApi.TryResolveMenuAction(
            "example.extension", "main", "pages", out var actionId));
        Assert.Equal("read", actionId);

        var extensionSummaries = ReactorHostApi.DescribeExtensionSummaries();
        Assert.Equal(1, extensionSummaries.Value<int>("total"));
        Assert.Equal(1, extensionSummaries["items"]![0]!.Value<int>("actionCount"));
        Assert.DoesNotContain(
            "response_too_large",
            BridgeProtocol.SerializeResponse(BridgeResponse.Success(
                "extensions", extensionSummaries, BridgeProtocol.CurrentProtocolVersion)),
            StringComparison.Ordinal);
        var extensionDetail = ReactorHostApi.DescribeExtension("example.extension");
        Assert.Equal("Example", extensionDetail!.Value<string>("name"));
        Assert.Equal(new[] { "details", "main" }, extensionDetail["menuIds"]!.Values<string>());
        var menuSummaries = ReactorHostApi.DescribeMenuSummaries("example.extension");
        Assert.Equal(2, menuSummaries.Value<int>("total"));
        Assert.False(menuSummaries.Value<bool>("truncated"));
        Assert.All(menuSummaries["items"]!, item => Assert.Equal(
            "example.extension", item!.Value<string>("extensionId")));
    }

    [Fact]
    public void Descriptors_and_builder_reject_invalid_or_ambiguous_contracts()
    {
        Assert.Throws<ArgumentException>(() => new ReactorExtensionDescriptor("Bad Id", "Bad", "1"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReactorActionDescriptor("unsafe", "Unsafe", (ReactorActionRisk)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReactorParameterDescriptor("value", (ReactorValueType)999));
        Assert.Throws<ArgumentException>(() =>
            new ReactorParameterDescriptor("value", ReactorValueType.Number, minimum: double.NaN));
        Assert.Throws<ArgumentException>(() =>
            new ReactorParameterDescriptor(
                "value", ReactorValueType.Integer, allowedValues: new[] { "one" }));
        Assert.Throws<ArgumentException>(() => new ReactorRangeNode("range", "Range", "read", 11, 0, 10, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReactorPaginationNode("pages", "Pages", "read", 0, 1));

        Assert.Throws<InvalidOperationException>(() => ReactorApi.RegisterExtension(
            Descriptor(),
            builder =>
            {
                builder.AddAction(ReadAction(), (_, __) => ReactorActionResult.Success());
                builder.AddAction(ReadAction(), (_, __) => ReactorActionResult.Success());
            }));

        Assert.Throws<InvalidOperationException>(() => ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder.AddMenu(new ReactorMenuDescriptor(
                "main", "Main", new[] { new ReactorActionNode("run", "Run", "missing") }))));
    }

    [Fact]
    public void Value_bearing_menu_nodes_require_a_compatible_action_contract()
    {
        Assert.Throws<InvalidOperationException>(() => ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(new ReactorMenuDescriptor(
                    "main", "Main", new[] { new ReactorToggleNode("toggle", "Toggle", "read", true) }))));

        Assert.Throws<InvalidOperationException>(() => ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder
                .AddAction(
                    new ReactorActionDescriptor(
                        "toggle",
                        "Toggle",
                        ReactorActionRisk.Gameplay,
                        new[] { new ReactorParameterDescriptor("value", ReactorValueType.String) }),
                    (_, __) => ReactorActionResult.Success())
                .AddMenu(new ReactorMenuDescriptor(
                    "main", "Main", new[] { new ReactorToggleNode("toggle", "Toggle", "toggle", true) }))));

        using var exact = ReactorApi.RegisterExtension(
            Descriptor("exact.extension"),
            builder => builder
                .AddAction(
                    new ReactorActionDescriptor(
                        "toggle",
                        "Toggle",
                        ReactorActionRisk.Gameplay,
                        new[] { new ReactorParameterDescriptor("value", ReactorValueType.Boolean, true) }),
                    (_, __) => ReactorActionResult.Success())
                .AddMenu(new ReactorMenuDescriptor(
                    "main", "Main", new[] { new ReactorToggleNode("toggle", "Toggle", "toggle", true) })));
        using var explicitExtras = ReactorApi.RegisterExtension(
            Descriptor("extras.extension"),
            builder => builder
                .AddAction(
                    ReadAction(allowAdditionalParameters: true),
                    (_, __) => ReactorActionResult.Success())
                .AddMenu(new ReactorMenuDescriptor(
                    "main", "Main", new[] { new ReactorPaginationNode("pages", "Pages", "read", 1, 2) })));

        Assert.True(ReactorHostApi.InvokeMenu(
            "exact.extension",
            "main",
            "toggle",
            "set-value",
            new JObject { ["value"] = false }).Succeeded);
        Assert.Equal(
            "invalid_params",
            ReactorHostApi.InvokeMenu(
                "exact.extension",
                "main",
                "toggle",
                "set-value",
                new JObject { ["value"] = "false" }).ErrorCode);
    }

    [Fact]
    public void Duplicate_extension_registration_is_rejected_and_original_remains_active()
    {
        using var first = RegisterReadExtension();
        Assert.Throws<InvalidOperationException>(() => RegisterReadExtension());
        Assert.True(ReactorHostApi.Invoke(
            "example.extension", "read", new JObject()).Succeeded);
    }

    [Fact]
    public void Typed_action_validation_precedes_handler_invocation()
    {
        var calls = 0;
        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder.AddAction(
                new ReactorActionDescriptor(
                    "set-level",
                    "Set level",
                    ReactorActionRisk.Gameplay,
                    new[] { new ReactorParameterDescriptor("level", ReactorValueType.Integer, true, 0, 5) }),
                (_, parameters) =>
                {
                    calls++;
                    return ReactorActionResult.Success(new JObject { ["level"] = parameters.Value<int>("level") });
                }));

        var wrongType = ReactorHostApi.Invoke(
            "example.extension", "set-level", new JObject { ["level"] = "5" });
        var outOfRange = ReactorHostApi.Invoke(
            "example.extension", "set-level", new JObject { ["level"] = 6 });
        var unknown = ReactorHostApi.Invoke(
            "example.extension", "set-level", new JObject { ["level"] = 3, ["extra"] = true });
        var wrongCase = ReactorHostApi.Invoke(
            "example.extension", "set-level", new JObject { ["Level"] = 3 });
        var valid = ReactorHostApi.Invoke(
            "example.extension", "set-level", new JObject { ["level"] = 3 });

        Assert.Equal("invalid_params", wrongType.ErrorCode);
        Assert.Equal("invalid_params", outOfRange.ErrorCode);
        Assert.Equal("invalid_params", unknown.ErrorCode);
        Assert.Equal("invalid_params", wrongCase.ErrorCode);
        Assert.True(valid.Succeeded);
        Assert.Equal(3, valid.Value!.Value<int>("level"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Menu_invocation_resolves_actions_and_rejects_unavailable_or_invalid_interactions()
    {
        var calls = 0;
        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder =>
            {
                builder.AddAction(ReadAction(), (_, __) =>
                {
                    calls++;
                    return ReactorActionResult.Success();
                });
                builder.AddMenu(new ReactorMenuDescriptor(
                    "main",
                    "Main",
                    new ReactorMenuNode[]
                    {
                        new ReactorActionNode("run", "Run", "read"),
                        new ReactorActionNode("disabled", "Disabled", "read", enabled: false),
                        new ReactorActionNode("hidden", "Hidden", "read", visible: false),
                        new ReactorStatusNode("status", "Status", "Ready"),
                    }));
            });

        var invoked = ReactorHostApi.InvokeMenu(
            "example.extension", "main", "run", "activate", new JObject());
        var adjustedAction = ReactorHostApi.InvokeMenu(
            "example.extension", "main", "run", "adjust", new JObject());
        var disabled = ReactorHostApi.InvokeMenu(
            "example.extension", "main", "disabled", "activate", new JObject());
        var hidden = ReactorHostApi.InvokeMenu(
            "example.extension", "main", "hidden", "activate", new JObject());
        var status = ReactorHostApi.InvokeMenu(
            "example.extension", "main", "status", "activate", new JObject());

        Assert.True(invoked.Succeeded);
        Assert.Equal("menu_interaction_not_allowed", adjustedAction.ErrorCode);
        Assert.Equal("menu_node_unavailable", disabled.ErrorCode);
        Assert.Equal("menu_node_unavailable", hidden.ErrorCode);
        Assert.Equal("menu_interaction_not_allowed", status.ErrorCode);
        Assert.False(ReactorHostApi.TryResolveMenuAction(
            "example.extension", "main", "disabled", out _));
        Assert.False(ReactorHostApi.TryResolveMenuAction(
            "example.extension", "main", "hidden", out _));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Confirmation_is_enforced_for_opt_in_gameplay_and_all_persistent_actions()
    {
        var calls = 0;
        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder =>
            {
                builder.AddAction(
                    new ReactorActionDescriptor(
                        "dangerous", "Dangerous", ReactorActionRisk.Gameplay,
                        requiresConfirmation: true),
                    (_, __) => { calls++; return ReactorActionResult.Success(); });
                builder.AddAction(
                    new ReactorActionDescriptor("purchase", "Purchase", ReactorActionRisk.Persistent),
                    (_, __) => { calls++; return ReactorActionResult.Success(); });
            });

        var gameplayPrompt = ReactorHostApi.Invoke(
            "example.extension", "dangerous", new JObject());
        var persistentPrompt = ReactorHostApi.Invoke(
            "example.extension", "purchase", new JObject());
        var gameplay = ReactorHostApi.Invoke(
            "example.extension", "dangerous", new JObject(), confirmed: true);
        var missingKey = ReactorHostApi.Invoke(
            "example.extension", "purchase", new JObject(), confirmed: true);

        Assert.True(gameplayPrompt.ConfirmationRequired);
        Assert.True(persistentPrompt.ConfirmationRequired);
        Assert.True(gameplay.Succeeded);
        Assert.Equal("idempotency_key_required", missingKey.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Persistent_action_is_replayed_once_and_conflicting_reuse_fails()
    {
        var calls = 0;
        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder.AddAction(
                new ReactorActionDescriptor(
                    "purchase",
                    "Purchase",
                    ReactorActionRisk.Persistent,
                    new[] { new ReactorParameterDescriptor("quantity", ReactorValueType.Integer, true, 1, 10) }),
                (_, parameters) =>
                {
                    calls++;
                    return ReactorActionResult.Success(new JObject { ["receipt"] = parameters.Value<int>("quantity") });
                }));

        var first = ReactorHostApi.Invoke(
            "example.extension", "purchase", new JObject { ["quantity"] = 2 }, true, "order-42");
        var replay = ReactorHostApi.Invoke(
            "example.extension", "purchase", new JObject { ["quantity"] = 2 }, true, "order-42");
        var conflict = ReactorHostApi.Invoke(
            "example.extension", "purchase", new JObject { ["quantity"] = 3 }, true, "order-42");

        Assert.True(first.Succeeded);
        Assert.False(first.Replayed);
        Assert.True(replay.Succeeded);
        Assert.True(replay.Replayed);
        Assert.Equal(2, replay.Value!.Value<int>("receipt"));
        Assert.Equal("idempotency_conflict", conflict.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Persistent_idempotency_is_independent_of_object_property_order()
    {
        var calls = 0;
        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder.AddAction(
                new ReactorActionDescriptor(
                    "save",
                    "Save",
                    ReactorActionRisk.Persistent,
                    new[]
                    {
                        new ReactorParameterDescriptor("first", ReactorValueType.Integer, true),
                        new ReactorParameterDescriptor("second", ReactorValueType.Object, true),
                    }),
                (_, __) => { calls++; return ReactorActionResult.Success(); }));

        var first = ReactorHostApi.Invoke(
            "example.extension",
            "save",
            new JObject
            {
                ["first"] = 1,
                ["second"] = new JObject { ["alpha"] = 1, ["beta"] = 2 },
            },
            true,
            "save-1");
        var replay = ReactorHostApi.Invoke(
            "example.extension",
            "save",
            new JObject
            {
                ["second"] = new JObject { ["beta"] = 2, ["alpha"] = 1 },
                ["first"] = 1,
            },
            true,
            "save-1");

        Assert.True(first.Succeeded);
        Assert.True(replay.Succeeded);
        Assert.True(replay.Replayed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Concurrent_persistent_reuse_is_rejected_until_the_first_invocation_completes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder.AddAction(
                new ReactorActionDescriptor("save", "Save", ReactorActionRisk.Persistent),
                (_, __) =>
                {
                    Interlocked.Increment(ref calls);
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5)))
                        return ReactorActionResult.Failure("fixture_timeout", "Fixture release timed out.");
                    return ReactorActionResult.Success();
                }));

        var firstTask = Task.Run(() => ReactorHostApi.Invoke(
            "example.extension", "save", new JObject(), true, "save-concurrent"));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var concurrent = ReactorHostApi.Invoke(
                "example.extension", "save", new JObject(), true, "save-concurrent");
            Assert.Equal("action_in_progress", concurrent.ErrorCode);
        }
        finally
        {
            release.Set();
        }

        Assert.True((await firstTask).Succeeded);
        Assert.True(ReactorHostApi.Invoke(
            "example.extension", "save", new JObject(), true, "save-concurrent").Replayed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Namespaced_events_are_bounded_and_apply_backpressure()
    {
        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder.AddEvent(new ReactorEventDescriptor("inventory.changed", maximumPayloadBytes: 64)));

        Assert.False(handle.TryPublishEvent("missing", new JObject()));
        Assert.False(handle.TryPublishEvent("inventory.changed", new JObject { ["text"] = new string('x', 100) }));
        var deeplyNested = new JObject();
        var cursor = deeplyNested;
        for (var depth = 0; depth < 30; depth++)
        {
            var child = new JObject();
            cursor["child"] = child;
            cursor = child;
        }
        Assert.False(handle.TryPublishEvent("inventory.changed", deeplyNested));
        for (var index = 0; index < 256; index++)
            Assert.True(handle.TryPublishEvent("inventory.changed", new JObject { ["index"] = index }));
        Assert.False(handle.TryPublishEvent("inventory.changed", new JObject()));

        var events = ReactorHostApi.DrainEvents();
        Assert.Equal(256, events.Count);
        Assert.Equal("example.extension.inventory.changed", events[0]!.Value<string>("event"));
        Assert.Equal(1, events[0]!.Value<long>("sequence"));
        Assert.Empty(ReactorHostApi.DrainEvents());
    }

    [Fact]
    public void Event_registration_enforces_the_wire_name_and_payload_boundaries()
    {
        var extensionId = "e" + new string('a', 45);
        var eventId = "e" + new string('b', 48);
        Assert.Equal(96, (extensionId + "." + eventId).Length);

        using var handle = ReactorApi.RegisterExtension(
            Descriptor(extensionId),
            builder => builder.AddEvent(new ReactorEventDescriptor(eventId)));
        Assert.True(handle.TryPublishEvent(eventId, new JObject { ["ready"] = true }));
        var published = ReactorHostApi.DrainEvents()[0]!.Value<string>("event");
        Assert.True(BridgeProtocol.IsValidEventName(published));
        Assert.NotNull(BridgeProtocol.SerializeEvent(published!, new JObject()));

        Assert.Throws<InvalidOperationException>(() => ReactorApi.RegisterExtension(
            Descriptor("e" + new string('c', 46)),
            builder => builder.AddEvent(new ReactorEventDescriptor(eventId))));
        Assert.Throws<InvalidOperationException>(() => ReactorApi.RegisterExtension(
            Descriptor("unsafe-extension"),
            builder => builder.AddEvent(new ReactorEventDescriptor("changed"))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReactorEventDescriptor("large", maximumPayloadBytes: 60 * 1024 + 1));
    }

    [Fact]
    public void Largest_accepted_menu_and_action_results_fit_inside_a_bridge_response()
    {
        ReactorMenuDescriptor? largestAccepted = null;
        for (var count = 1; count <= 256; count++)
        {
            try
            {
                largestAccepted = new ReactorMenuDescriptor(
                    "main",
                    "Main",
                    Enumerable.Range(0, count).Select(index => (ReactorMenuNode)new ReactorActionNode(
                        "node" + index,
                        "Node " + index,
                        "read",
                        new string('x', 512))));
            }
            catch (ArgumentException)
            {
                break;
            }
        }
        Assert.NotNull(largestAccepted);

        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success(
                    new JObject { ["text"] = new string('x', 59 * 1024) }))
                .AddMenu(largestAccepted!));

        var menuWire = BridgeProtocol.SerializeResponse(
            BridgeResponse.Success(
                "menu1",
                ReactorHostApi.DescribeMenus("example.extension", "main"),
                BridgeProtocol.CurrentProtocolVersion));
        Assert.DoesNotContain("response_too_large", menuWire, StringComparison.Ordinal);

        var action = ReactorHostApi.Invoke("example.extension", "read", new JObject());
        Assert.True(action.Succeeded);
        var actionWire = BridgeProtocol.SerializeResponse(
            BridgeResponse.Success("action1", action.ToJson(), BridgeProtocol.CurrentProtocolVersion));
        Assert.DoesNotContain("response_too_large", actionWire, StringComparison.Ordinal);

        handle.Dispose();
        using var oversized = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder.AddAction(
                ReadAction(),
                (_, __) => ReactorActionResult.Success(
                    new JObject { ["text"] = new string('x', 61 * 1024) })));
        Assert.Equal(
            "result_too_large",
            ReactorHostApi.Invoke("example.extension", "read", new JObject()).ErrorCode);

        using var deep = ReactorApi.RegisterExtension(
            Descriptor("deep.extension"),
            builder => builder.AddAction(
                ReadAction(),
                (_, __) =>
                {
                    var root = new JObject();
                    var current = root;
                    for (var depth = 0; depth < 30; depth++)
                    {
                        var child = new JObject();
                        current["child"] = child;
                        current = child;
                    }
                    return ReactorActionResult.Success(root);
                }));
        Assert.Equal(
            "result_too_deep",
            ReactorHostApi.Invoke("deep.extension", "read", new JObject()).ErrorCode);
    }

    [Fact]
    public void Oversized_extension_detail_is_rejected_at_registration()
    {
        Assert.Throws<InvalidOperationException>(() => ReactorApi.RegisterExtension(
            Descriptor(),
            builder =>
            {
                for (var index = 0; index < 150; index++)
                {
                    builder.AddAction(
                        new ReactorActionDescriptor(
                            "action" + index,
                            "Action " + index,
                            ReactorActionRisk.Read,
                            description: new string('x', 512)),
                        (_, __) => ReactorActionResult.Success());
                }
            }));
        Assert.Empty(ReactorHostApi.DescribeExtensionSummaries()["items"]!);
    }

    [Fact]
    public void Unfiltered_menu_summaries_are_transport_bounded_and_report_truncation()
    {
        var handles = new List<IReactorExtensionHandle>();
        try
        {
            for (var extensionIndex = 0; extensionIndex < 4; extensionIndex++)
            {
                var current = extensionIndex;
                var extensionId = "extension" + current + new string('e', 50);
                handles.Add(ReactorApi.RegisterExtension(
                    Descriptor(extensionId),
                    builder =>
                    {
                        builder.AddAction(ReadAction(), (_, __) => ReactorActionResult.Success());
                        for (var menuIndex = 0; menuIndex < 64; menuIndex++)
                        {
                            builder.AddMenu(new ReactorMenuDescriptor(
                                "menu" + menuIndex.ToString("D2") + new string('m', 56),
                                "M" + menuIndex.ToString("D2") + "-" + new string('x', 124),
                                new[] { new ReactorActionNode("run", "Run", "read") }));
                        }
                    }));
            }

            var summaries = ReactorHostApi.DescribeMenuSummaries();
            Assert.Equal(256, summaries.Value<int>("total"));
            Assert.True(summaries.Value<bool>("truncated"));
            Assert.InRange(summaries["items"]!.Count(), 1, 255);
            var wire = BridgeProtocol.SerializeResponse(BridgeResponse.Success(
                "menus", summaries, BridgeProtocol.CurrentProtocolVersion));
            Assert.DoesNotContain("response_too_large", wire, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    [Fact]
    public void Lifecycle_isolated_delivery_and_disposal_are_deterministic()
    {
        var first = new RecordingLifecycle();
        var second = new RecordingLifecycle(throwOn: ReactorLifecycleStage.StoryReady);
        using var firstHandle = ReactorApi.RegisterExtension(
            Descriptor("first.extension"), builder => builder.UseLifecycle(first));
        using var secondHandle = ReactorApi.RegisterExtension(
            Descriptor("second.extension"), builder => builder.UseLifecycle(second));

        var payload = new JObject { ["ready"] = true };
        ReactorHostApi.NotifyLifecycle(ReactorLifecycleStage.StoryReady, payload);
        payload["ready"] = false;

        Assert.Equal(ReactorLifecycleStage.Registered, first.Entries[0].Stage);
        Assert.Equal(ReactorLifecycleStage.StoryReady, first.Entries[1].Stage);
        Assert.True(first.Entries[1].Payload!.Value<bool>("ready"));
        Assert.Equal(2, second.Entries.Count);

        firstHandle.Dispose();
        Assert.Equal(ReactorLifecycleStage.Unloading, first.Entries.Last().Stage);
        ReactorHostApi.NotifyLifecycle(ReactorLifecycleStage.OverlayOpened);
        Assert.DoesNotContain(first.Entries, value => value.Stage == ReactorLifecycleStage.OverlayOpened);
        Assert.Contains(second.Entries, value => value.Stage == ReactorLifecycleStage.OverlayOpened);
    }

    [Fact]
    public void Menu_updates_removals_and_extension_disposal_are_immediate()
    {
        var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder =>
            {
                builder.AddAction(ReadAction(), (_, __) => ReactorActionResult.Success());
                builder.AddEvent(new ReactorEventDescriptor("changed"));
                builder.AddMenu(Menu("Before"));
            });

        handle.UpdateMenu(Menu("After"));
        Assert.Equal("After", ReactorHostApi.DescribeMenus("example.extension", "main")[0]!.Value<string>("label"));
        Assert.True(ReactorHostApi.TryResolveMenuAction(
            "example.extension", "main", "run", out var actionId));
        Assert.Equal("read", actionId);
        Assert.True(handle.RemoveMenu("main"));
        Assert.Empty(ReactorHostApi.DescribeMenus("example.extension", "main"));
        Assert.False(handle.RemoveMenu("main"));

        handle.Dispose();
        Assert.Empty(ReactorHostApi.DescribeExtensions());
        Assert.Equal("action_not_found", ReactorHostApi.Invoke(
            "example.extension", "read", new JObject()).ErrorCode);
        Assert.False(handle.TryPublishEvent("changed", new JObject()));
        Assert.Throws<ObjectDisposedException>(() => handle.UpdateMenu(Menu("Again")));
    }

    [Fact]
    public void Reset_unregisters_every_extension_and_clears_pending_events()
    {
        var lifecycle = new RecordingLifecycle();
        var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder
                .AddEvent(new ReactorEventDescriptor("changed"))
                .UseLifecycle(lifecycle));
        Assert.True(handle.TryPublishEvent("changed", new JObject()));

        ReactorHostApi.Reset();

        Assert.Empty(ReactorHostApi.DescribeExtensions());
        Assert.Empty(ReactorHostApi.DrainEvents());
        Assert.Equal(ReactorLifecycleStage.Unloading, lifecycle.Entries.Last().Stage);
        Assert.False(handle.TryPublishEvent("changed", new JObject()));
        handle.Dispose();
    }

    private static ReactorExtensionDescriptor Descriptor(string id = "example.extension") =>
        new ReactorExtensionDescriptor(id, "Example", "1.0.0", capabilities: new[] { "menu.routes" });

    private static ReactorActionDescriptor ReadAction(bool allowAdditionalParameters = false) =>
        new ReactorActionDescriptor(
            "read",
            "Read",
            ReactorActionRisk.Read,
            allowAdditionalParameters: allowAdditionalParameters);

    private static IReactorExtensionHandle RegisterReadExtension() => ReactorApi.RegisterExtension(
        Descriptor(), builder => builder.AddAction(ReadAction(), (_, __) => ReactorActionResult.Success()));

    private static ReactorMenuDescriptor Menu(string label) => new ReactorMenuDescriptor(
        "main", label, new[] { new ReactorActionNode("run", "Run", "read") });

    private static IEnumerable<JToken> Descendants(JToken root)
    {
        yield return root;
        foreach (var child in root.Children())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

    private sealed class RecordingLifecycle : IReactorExtensionLifecycle
    {
        private readonly ReactorLifecycleStage? _throwOn;
        public RecordingLifecycle(ReactorLifecycleStage? throwOn = null) => _throwOn = throwOn;
        public List<ReactorLifecycleContext> Entries { get; } = new();
        public void OnLifecycle(ReactorLifecycleContext context)
        {
            Entries.Add(context);
            if (context.Stage == _throwOn) throw new InvalidOperationException("Fixture failure");
        }
    }
}
