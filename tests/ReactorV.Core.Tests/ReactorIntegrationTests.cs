using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public void ManagedPhysicalF9HasExactlyOneRegisteredExtensionOwner()
    {
        using var first = ReactorApi.RegisterExtension(
            new ReactorExtensionDescriptor(
                "first.f9.owner",
                "First",
                "1.0.0",
                capabilities: new[]
                {
                    ReactorExtensionCapabilities.DefaultF9MenuOwner,
                }),
            builder => builder.AddAction(
                ReadAction(),
                (_, __) => ReactorActionResult.Success()));

        Assert.True(ReactorHostApi.HasExtensionCapability(
            ReactorExtensionCapabilities.DefaultF9MenuOwner));
        Assert.Throws<InvalidOperationException>(() =>
            ReactorApi.RegisterExtension(
                new ReactorExtensionDescriptor(
                    "second.f9.owner",
                    "Second",
                    "1.0.0",
                    capabilities: new[]
                    {
                        ReactorExtensionCapabilities.DefaultF9MenuOwner,
                    }),
                builder => builder.AddAction(
                    ReadAction(),
                    (_, __) => ReactorActionResult.Success())));
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
    public void Api_v1_menu_constructor_and_handle_contracts_remain_binary_compatible()
    {
        Assert.NotNull(typeof(ReactorActionNode).GetConstructor(new[]
            { typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool) }));
        Assert.NotNull(typeof(ReactorToggleNode).GetConstructor(new[]
            { typeof(string), typeof(string), typeof(string), typeof(bool), typeof(string), typeof(bool), typeof(bool) }));
        Assert.NotNull(typeof(ReactorChoiceNode).GetConstructor(new[]
            { typeof(string), typeof(string), typeof(string), typeof(IEnumerable<ReactorChoiceOption>), typeof(string), typeof(string), typeof(bool), typeof(bool) }));
        Assert.NotNull(typeof(ReactorRangeNode).GetConstructor(new[]
            { typeof(string), typeof(string), typeof(string), typeof(double), typeof(double), typeof(double), typeof(double), typeof(string), typeof(bool), typeof(bool) }));
        Assert.NotNull(typeof(ReactorTextNode).GetConstructor(new[]
            { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string), typeof(bool), typeof(bool) }));
        Assert.NotNull(typeof(ReactorSearchNode).GetConstructor(new[]
            { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string), typeof(bool), typeof(bool) }));
        Assert.NotNull(typeof(ReactorKeybindNode).GetConstructor(new[]
            { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool) }));
        Assert.NotNull(typeof(ReactorPaginationNode).GetConstructor(new[]
            { typeof(string), typeof(string), typeof(string), typeof(int), typeof(int), typeof(string), typeof(bool), typeof(bool) }));
        Assert.DoesNotContain(
            typeof(IReactorExtensionHandle).GetMethods(),
            method => method.Name.Contains("Present", StringComparison.Ordinal) ||
                method.Name.Contains("Dismiss", StringComparison.Ordinal));

        using var handle = RegisterReadExtension();
        Assert.IsAssignableFrom<IReactorMenuPresentationHandle>(handle);
        Assert.IsAssignableFrom<IReactorMenuPresentationStateHandle>(handle);
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

    [Fact]
    public void Reset_delivers_unloading_once_with_the_shutdown_payload()
    {
        var lifecycle = new RecordingLifecycle();
        var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder.UseLifecycle(lifecycle));
        var payload = new JObject { ["gameTime"] = 417 };

        ReactorHostApi.Reset(payload);
        payload["gameTime"] = 999;

        var unloading = lifecycle.Entries
            .Where(value => value.Stage == ReactorLifecycleStage.Unloading)
            .ToArray();
        Assert.Single(unloading);
        Assert.Equal(417, unloading[0].Payload!.Value<int>("gameTime"));
        Assert.False(handle.TryPublishEvent("changed", new JObject()));
        handle.Dispose();
    }

    [Fact]
    public void BeginShutdown_does_not_wait_for_extension_cleanup()
    {
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var completed = new ManualResetEventSlim(false);
        var lifecycle = new BlockingUnloadingLifecycle(started, release, completed);
        var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder.UseLifecycle(lifecycle));

        var elapsed = Stopwatch.StartNew();
        ReactorHostApi.BeginShutdown(new JObject { ["reason"] = "game-exit" });
        elapsed.Stop();

        Assert.True(elapsed.ElapsedMilliseconds < 250, elapsed.Elapsed.ToString());
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, lifecycle.UnloadingCount);
        release.Set();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, lifecycle.UnloadingCount);
        handle.Dispose();
    }

    [Fact]
    public void Menu_bound_parameters_are_immutable_merged_and_cannot_be_overridden()
    {
        JObject? received = null;
        var bound = new JObject { ["listingid"] = "vehicle-adder" };
        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder
                .AddAction(
                    new ReactorActionDescriptor(
                        "purchase",
                        "Purchase",
                        ReactorActionRisk.Read,
                        new[]
                        {
                            new ReactorParameterDescriptor("listingid", ReactorValueType.String, required: true),
                            new ReactorParameterDescriptor("quantity", ReactorValueType.Integer, minimum: 1),
                        }),
                    (_, parameters) =>
                    {
                        received = (JObject)parameters.DeepClone();
                        return ReactorActionResult.Success();
                    })
                .AddMenu(new ReactorMenuDescriptor(
                    "main",
                    "Main",
                    new[]
                    {
                        new ReactorActionNode(
                            "buy",
                            "Buy",
                            "purchase",
                            "",
                            true,
                            true,
                            boundParameters: bound),
                    })));

        bound["listingid"] = "mutated-after-registration";
        var descriptorNode = (JObject)ReactorHostApi.DescribeMenus(
            "example.extension", "main")[0]!["nodes"]![0]!;
        Assert.Equal(
            "vehicle-adder",
            descriptorNode["boundParameters"]!.Value<string>("listingid"));

        var result = ReactorHostApi.InvokeMenu(
            "example.extension",
            "main",
            "buy",
            "activate",
            new JObject { ["quantity"] = 2 });
        Assert.True(result.Succeeded);
        Assert.NotNull(received);
        Assert.Equal("vehicle-adder", received!.Value<string>("listingid"));
        Assert.Equal(2, received.Value<int>("quantity"));

        received = null;
        var overrideResult = ReactorHostApi.InvokeMenu(
            "example.extension",
            "main",
            "buy",
            "activate",
            new JObject { ["LISTINGID"] = "browser-choice" });
        Assert.Equal("bound_parameter_override", overrideResult.ErrorCode);
        Assert.Null(received);
    }

    [Fact]
    public void Bound_parameter_contracts_are_validated_on_registration_and_update()
    {
        var action = new ReactorActionDescriptor(
            "purchase",
            "Purchase",
            ReactorActionRisk.Read,
            new[] { new ReactorParameterDescriptor("listingid", ReactorValueType.String, required: true) });

        Assert.Throws<InvalidOperationException>(() => ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder
                .AddAction(action, (_, __) => ReactorActionResult.Success())
                .AddMenu(new ReactorMenuDescriptor(
                    "main",
                    "Main",
                    new[]
                    {
                        new ReactorActionNode(
                            "buy",
                            "Buy",
                            "purchase",
                            "",
                            true,
                            true,
                            boundParameters: new JObject { ["listingid"] = 42 }),
                    }))));

        using var handle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder
                .AddAction(action, (_, __) => ReactorActionResult.Success())
                .AddMenu(new ReactorMenuDescriptor(
                    "main",
                    "Main",
                    new[]
                    {
                        new ReactorActionNode(
                            "buy",
                            "Buy",
                            "purchase",
                            "",
                            true,
                            true,
                            boundParameters: new JObject { ["listingid"] = "valid" }),
                    })));

        Assert.Throws<InvalidOperationException>(() => handle.UpdateMenu(new ReactorMenuDescriptor(
            "main",
            "Main",
            new[]
            {
                new ReactorActionNode(
                    "buy",
                    "Buy",
                    "purchase",
                    "",
                    true,
                    true,
                    boundParameters: new JObject { ["unknown"] = true }),
            })));
    }

    [Fact]
    public void Menu_presentation_requests_are_owned_bounded_and_detached()
    {
        var extensionHandle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("Main")));
        var handle = (IReactorMenuPresentationHandle)extensionHandle;
        var context = new JObject { ["source"] = "gbay" };

        Assert.False(handle.TryPresentMenu("main", context));
        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        Assert.False(handle.TryPresentMenu("missing", context));
        Assert.True(handle.TryPresentMenu("main", context));
        context["source"] = "mutated";

        var presentation = Assert.Single(ReactorHostApi.DrainMenuPresentations()).Value<JObject>();
        Assert.NotNull(presentation);
        Assert.Equal("example.extension", presentation!.Value<string>("extensionId"));
        Assert.Equal("main", presentation.Value<string>("menuId"));
        Assert.Matches("^[0-9a-f]{32}$", presentation.Value<string>("presentationId")!);
        Assert.Equal("interactive-menu", presentation.Value<string>("inputMode"));
        Assert.Equal("gbay", presentation["context"]!.Value<string>("source"));

        for (var index = 0; index < ReactorRegistry.MaximumPendingMenuPresentations + 10; index++)
        {
            Assert.True(handle.TryPresentMenu(
                "main",
                new JObject { ["sequence"] = index }));
        }
        var coalesced = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>();
        Assert.Equal(
            ReactorRegistry.MaximumPendingMenuPresentations + 9,
            coalesced!["context"]!.Value<int>("sequence"));

        Assert.True(extensionHandle.RemoveMenu("main"));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        extensionHandle.Dispose();
        Assert.False(handle.TryPresentMenu("main"));
    }

    [Fact]
    public void Presentation_host_unavailability_fails_closed_and_drops_stale_requests()
    {
        using var extensionHandle = ReactorApi.RegisterExtension(
            Descriptor(),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("Main")));
        var handle = (IReactorMenuPresentationHandle)extensionHandle;

        Assert.False(handle.TryPresentMenu("main"));
        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        Assert.True(handle.TryPresentMenu("main"));
        ReactorHostApi.SetMenuPresentationHostAvailable(false);

        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.False(handle.TryPresentMenu("main"));
    }

    [Fact]
    public void Active_menu_query_and_dismissal_follow_authoritative_host_lifecycle()
    {
        using var firstHandle = ReactorApi.RegisterExtension(
            Descriptor("first.extension"),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("First")));
        using var secondHandle = ReactorApi.RegisterExtension(
            Descriptor("second.extension"),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("Second")));
        var first = (IReactorMenuPresentationHandle)firstHandle;
        var second = (IReactorMenuPresentationHandle)secondHandle;
        ReactorHostApi.SetMenuPresentationHostAvailable(true);

        Assert.True(first.TryPresentMenu("main"));
        Assert.True(second.TryPresentMenu("main"));
        var queued = new[]
        {
            Assert.Single(ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!,
            Assert.Single(ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!,
        };
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        // Drained generations remain authoritative while the script host is
        // dispatching them. F9 can therefore close this handoff state without
        // queuing another open request.
        Assert.True(first.IsMenuPresented("main"));
        Assert.True(second.IsMenuPresented("main"));

        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "first.extension",
            "main",
            queued[0].Value<string>("presentationId")!,
            out var firstSuperseded));
        Assert.Null(firstSuperseded);
        Assert.True(first.IsMenuPresented("main"));
        Assert.True(second.IsMenuPresented("main"));

        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "second.extension",
            "main",
            queued[1].Value<string>("presentationId")!,
            out var secondSuperseded));
        Assert.Equal(
            queued[0].Value<string>("presentationId"),
            secondSuperseded!.Value<string>("presentationId"));
        Assert.False(first.IsMenuPresented("main"));
        Assert.True(second.IsMenuPresented("main"));

        Assert.True(second.TryDismissMenu("main"));
        Assert.True(second.IsMenuPresented("main"));
        var dismissal = Assert.Single(ReactorHostApi.DrainMenuDismissals()).Value<JObject>();
        Assert.Equal("second.extension", dismissal!.Value<string>("extensionId"));
        Assert.Equal(queued[1].Value<string>("presentationId"), dismissal.Value<string>("presentationId"));
        Assert.True(second.TryDismissMenu("main"));
        Assert.Empty(ReactorHostApi.DrainMenuDismissals());
        Assert.False(first.TryPresentMenu("main"));

        // A superseded generation cannot be resurrected. A fresh request gets
        // a fresh authoritative presentation id.
        Assert.False(ReactorHostApi.MarkMenuPresentationActive(
            "first.extension",
            "main",
            queued[0].Value<string>("presentationId")!,
            out var finalSuperseded));
        Assert.Null(finalSuperseded);
        Assert.NotNull(ReactorHostApi.AcknowledgeMenuPresentationHidden(
            queued[1].Value<string>("presentationId")!));
        Assert.False(second.IsMenuPresented("main"));
        Assert.True(first.TryPresentMenu("main"));
        var replacement = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!;
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "first.extension",
            "main",
            replacement.Value<string>("presentationId")!,
            out finalSuperseded));
        Assert.Null(finalSuperseded);
        var hidden = ReactorHostApi.TakeActiveMenuPresentation();
        Assert.Equal("first.extension", hidden!.Value<string>("extensionId"));
        Assert.Equal(
            replacement.Value<string>("presentationId"),
            hidden.Value<string>("presentationId"));
        Assert.False(first.IsMenuPresented("main"));
    }

    [Fact]
    public void Menu_readiness_tracks_only_the_exact_active_presentation_generation()
    {
        using var extensionHandle = ReactorApi.RegisterExtension(
            Descriptor("ready.extension"),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("Ready")));
        var presentation = (IReactorMenuPresentationHandle)extensionHandle;
        var state = (IReactorMenuPresentationStateHandle)extensionHandle;
        ReactorHostApi.SetMenuPresentationHostAvailable(true);

        Assert.False(state.IsMenuPresentationReady("main"));
        Assert.True(presentation.TryPresentMenu("main"));
        Assert.True(presentation.IsMenuPresented("main"));
        Assert.False(state.IsMenuPresentationReady("main"));

        var dispatching = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!;
        var presentationId = dispatching.Value<string>("presentationId")!;
        Assert.False(state.IsMenuPresentationReady("main"));
        Assert.False(ReactorHostApi.MarkMenuPresentationReady("stale-presentation-id"));
        Assert.False(state.IsMenuPresentationReady("main"));

        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "ready.extension",
            "main",
            presentationId,
            out var superseded));
        Assert.Null(superseded);
        Assert.False(state.IsMenuPresentationReady("main"));
        Assert.False(ReactorHostApi.MarkMenuPresentationReady("stale-presentation-id"));
        Assert.False(state.IsMenuPresentationReady("main"));

        Assert.True(ReactorHostApi.MarkMenuPresentationReady(presentationId));
        Assert.True(state.IsMenuPresentationReady("main"));

        Assert.True(presentation.TryDismissMenu("main"));
        Assert.True(presentation.IsMenuPresented("main"));
        Assert.False(state.IsMenuPresentationReady("main"));
        var dismissal = Assert.Single(
            ReactorHostApi.DrainMenuDismissals()).Value<JObject>()!;
        Assert.NotNull(ReactorHostApi.AcknowledgeMenuPresentationHidden(
            dismissal.Value<string>("presentationId")!));
        Assert.False(presentation.IsMenuPresented("main"));
    }

    [Fact]
    public void Menu_readiness_clears_on_supersede_take_host_loss_and_reset()
    {
        using var firstHandle = ReactorApi.RegisterExtension(
            Descriptor("ready.first"),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("First")));
        using var secondHandle = ReactorApi.RegisterExtension(
            Descriptor("ready.second"),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("Second")));
        var firstPresentation = (IReactorMenuPresentationHandle)firstHandle;
        var firstState = (IReactorMenuPresentationStateHandle)firstHandle;
        var secondPresentation = (IReactorMenuPresentationHandle)secondHandle;
        var secondState = (IReactorMenuPresentationStateHandle)secondHandle;
        ReactorHostApi.SetMenuPresentationHostAvailable(true);

        var firstId = PresentAndActivate(firstPresentation, "ready.first");
        Assert.True(ReactorHostApi.MarkMenuPresentationReady(firstId));
        Assert.True(firstState.IsMenuPresentationReady("main"));

        Assert.True(secondPresentation.TryPresentMenu("main"));
        var secondDispatch = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!;
        var secondId = secondDispatch.Value<string>("presentationId")!;
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "ready.second",
            "main",
            secondId,
            out var superseded));
        Assert.Equal(firstId, superseded!.Value<string>("presentationId"));
        Assert.False(firstState.IsMenuPresentationReady("main"));
        Assert.False(secondState.IsMenuPresentationReady("main"));
        Assert.True(ReactorHostApi.MarkMenuPresentationReady(secondId));
        Assert.True(secondState.IsMenuPresentationReady("main"));

        Assert.NotNull(ReactorHostApi.TakeActiveMenuPresentation());
        Assert.False(secondState.IsMenuPresentationReady("main"));

        var replacementId = PresentAndActivate(secondPresentation, "ready.second");
        Assert.True(ReactorHostApi.MarkMenuPresentationReady(replacementId));
        ReactorHostApi.SetMenuPresentationHostAvailable(false);
        Assert.False(secondState.IsMenuPresentationReady("main"));

        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        var resetId = PresentAndActivate(secondPresentation, "ready.second");
        Assert.True(ReactorHostApi.MarkMenuPresentationReady(resetId));
        ReactorHostApi.Reset();
        Assert.False(secondState.IsMenuPresentationReady("main"));
    }

    [Fact]
    public void Pending_or_dispatching_menu_can_be_closed_without_a_late_reopen()
    {
        using var extensionHandle = ReactorApi.RegisterExtension(
            Descriptor("toggle.extension"),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("Toggle")));
        var handle = (IReactorMenuPresentationHandle)extensionHandle;
        ReactorHostApi.SetMenuPresentationHostAvailable(true);

        Assert.True(handle.TryPresentMenu("main"));
        Assert.True(handle.IsMenuPresented("main"));
        Assert.True(handle.TryDismissMenu("main"));
        Assert.False(handle.IsMenuPresented("main"));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.Empty(ReactorHostApi.DrainMenuDismissals());

        Assert.True(handle.TryPresentMenu("main"));
        var dispatching = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!;
        Assert.True(handle.IsMenuPresented("main"));
        Assert.True(handle.TryDismissMenu("main"));
        Assert.False(handle.IsMenuPresented("main"));
        Assert.False(ReactorHostApi.MarkMenuPresentationActive(
            "toggle.extension",
            "main",
            dispatching.Value<string>("presentationId")!,
            out var superseded));
        Assert.Null(superseded);
        Assert.Empty(ReactorHostApi.DrainMenuDismissals());
    }

    [Fact]
    public void Removing_an_active_menu_or_extension_queues_authoritative_dismissal()
    {
        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        var menuHandle = ReactorApi.RegisterExtension(
            Descriptor("menu.extension"),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("Menu")));
        var menuPresentation = PresentAndActivate(
            (IReactorMenuPresentationHandle)menuHandle,
            "menu.extension");

        Assert.True(menuHandle.RemoveMenu("main"));
        var menuDismissal = Assert.Single(ReactorHostApi.DrainMenuDismissals()).Value<JObject>();
        Assert.Equal(menuPresentation, menuDismissal!.Value<string>("presentationId"));
        Assert.NotNull(ReactorHostApi.AcknowledgeMenuPresentationHidden(
            menuPresentation));
        menuHandle.Dispose();

        var extensionHandle = ReactorApi.RegisterExtension(
            Descriptor("unload.extension"),
            builder => builder
                .AddAction(ReadAction(), (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("Menu")));
        var extensionPresentation = PresentAndActivate(
            (IReactorMenuPresentationHandle)extensionHandle,
            "unload.extension");

        extensionHandle.Dispose();
        var extensionDismissal = Assert.Single(ReactorHostApi.DrainMenuDismissals()).Value<JObject>();
        Assert.Equal(extensionPresentation, extensionDismissal!.Value<string>("presentationId"));
        Assert.NotNull(ReactorHostApi.AcknowledgeMenuPresentationHidden(
            extensionPresentation));
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

    private static string PresentAndActivate(
        IReactorMenuPresentationHandle handle,
        string extensionId)
    {
        Assert.True(handle.TryPresentMenu("main"));
        var presentation = Assert.Single(ReactorHostApi.DrainMenuPresentations()).Value<JObject>();
        var presentationId = presentation!.Value<string>("presentationId")!;
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            extensionId,
            "main",
            presentationId,
            out var superseded));
        Assert.Null(superseded);
        return presentationId;
    }

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

    private sealed class BlockingUnloadingLifecycle : IReactorExtensionLifecycle
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;
        private readonly ManualResetEventSlim _completed;
        private int _unloadingCount;

        public BlockingUnloadingLifecycle(
            ManualResetEventSlim started,
            ManualResetEventSlim release,
            ManualResetEventSlim completed)
        {
            _started = started;
            _release = release;
            _completed = completed;
        }

        public int UnloadingCount => Volatile.Read(ref _unloadingCount);

        public void OnLifecycle(ReactorLifecycleContext context)
        {
            if (context.Stage != ReactorLifecycleStage.Unloading) return;
            Interlocked.Increment(ref _unloadingCount);
            _started.Set();
            _release.Wait();
            _completed.Set();
        }
    }
}
