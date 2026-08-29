# REACTOR V managed extension API

The managed extension API lets another Story Mode script expose its own
features to any Reactor interface without adding project-specific commands to
Reactor itself. The owning script remains authoritative: Reactor only routes a
validated action to a handler on the GTA script thread.

This is the intended split:

1. A gameplay project references `RageWebUI.Core.dll`.
2. It registers an extension once with `ReactorApi.RegisterExtension`.
3. The extension declares typed actions, events, and optional menus.
4. A React or plain HTML page discovers those declarations through browser API
   v2 and renders whichever UI fits the project.
5. The owning project performs the real gameplay or save operation and returns
   a small result. The browser never receives native-call, file, memory, or
   script-execution authority.

## Register from a GTA script

Keep the returned handle for the lifetime of the owning script and dispose it
when that script aborts:

```csharp
using Newtonsoft.Json.Linq;
using ReactorV.Integration;

private IReactorExtensionHandle? _reactor;

private void RegisterReactor()
{
    _reactor = ReactorApi.RegisterExtension(
        new ReactorExtensionDescriptor(
            "myproject.vehicles",
            "My Vehicle Pack",
            "1.0.0",
            capabilities: new[] { "storefront", "traffic" }),
        builder =>
        {
            builder.AddAction(
                new ReactorActionDescriptor(
                    "purchase",
                    "Purchase vehicle",
                    ReactorActionRisk.Persistent,
                    new[] {
                        new ReactorParameterDescriptor(
                            "listingId",
                            ReactorValueType.String,
                            required: true)
                    }),
                (_, parameters) => {
                    var receipt = StagePurchase(parameters.Value<string>("listingId"));
                    return ReactorActionResult.Success(
                        new JObject { ["receiptId"] = receipt, ["savePending"] = true });
                });
        });
}

private void OnAborted(object sender, EventArgs args)
{
    _reactor?.Dispose();
}
```

Handlers are synchronous and execute only when Reactor drains requests on the
GTA script tick. Do not perform slow network or disk work inside a handler.
Stage long work in the owning project, return a job/receipt ID, and publish
bounded progress events.

## Action risk and persistence

- `Read` returns state and must not mutate gameplay or durable data.
- `Gameplay` changes the current session. It may opt into confirmation.
- `Persistent` changes owned/save-linked state. It always requires explicit
  confirmation and a 1–128 character idempotency key.

Reusing a persistent idempotency key with the same parameters replays the prior
result without running the handler twice. Reusing it with different parameters
fails. This is appropriate for GBAY purchases, settings, package actions, and
other operations where a duplicated click would be harmful.

## Declarative menus

An extension can register multiple menus. Available node kinds are action,
toggle, choice, range, text, search, keybind, tabs, list, grid, media, status,
progress, pagination, separator, and submenu. Menu nodes reference a declared
action ID; the browser cannot replace that binding.

Value nodes follow one convention: their action receives a parameter named
`value`. Registration fails early when a toggle, choice, range, text, search,
keybind, or pagination node targets an incompatible action. Hidden and disabled
nodes cannot be invoked through the raw browser bridge.

```csharp
builder.AddAction(
    new ReactorActionDescriptor(
        "traffic.setenabled",
        "Spawn in traffic",
        ReactorActionRisk.Persistent,
        new[] { new ReactorParameterDescriptor("value", ReactorValueType.Boolean, true) }),
    (_, p) => ReactorActionResult.Success(
        new JObject { ["enabled"] = SetTrafficEnabled(p.Value<bool>("value")) }));

builder.AddMenu(new ReactorMenuDescriptor(
    "settings",
    "Vehicle Settings",
    new ReactorMenuNode[] {
        new ReactorToggleNode(
            "traffic",
            "Spawn in traffic",
            "traffic.setenabled",
            currentTrafficValue)
    }));
```

Call `handle.UpdateMenu(...)` when authoritative values change. Menu updates
replace one descriptor atomically; pages can fetch the new descriptor after an
extension event.

## Events and lifecycle

Declare every event before publishing it. Event names are automatically
namespaced as `<extensionId>.<eventId>`, payloads are copied and bounded, and a
full queue rejects new events instead of consuming unbounded memory.

```csharp
builder.AddEvent(new ReactorEventDescriptor("telemetry", maximumPayloadBytes: 4096));

handle.TryPublishEvent("telemetry", new JObject {
    ["vehicle"] = currentModel,
    ["speedMps"] = speed,
    ["wheelCount"] = wheelCount,
});
```

Use `builder.UseLifecycle(...)` to receive browser-ready, story-ready/unavailable,
overlay-opening/opened/closing/closed, suspend/resume, and unload stages. A
failing extension lifecycle callback is isolated from other extensions.

## Compiled examples

[`examples/ReactorV.Extension.Examples`](../examples/ReactorV.Extension.Examples)
contains buildable ALLIN1-style and axle-telemetry adapters. The ALLIN1 example
shows a save-pending GBAY purchase, traffic setting, garage delivery, dynamic
menu state, and order events. The axle example shows a range control and
rate-limited telemetry. Browser-side GBAY, axle, and weapon/suppressor examples
are in [`web/examples`](../web/examples).

The release harness registers and exercises its own ALLIN1 fixture, including
discovery, menu lookup, unconfirmed refusal, confirmed execution, idempotent
replay, event subscription, input mode, and overlay visibility.
