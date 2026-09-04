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

### Standalone starter prefabs

`examples/ReactorV.StandaloneStarter` contains complete, independently compiled
SHVDN entry points, not injected-service sketches. Starter A uses F6; Starter B
uses F7. Both can register identical local menu/action IDs because extension IDs
provide the namespace. Neither references ALLIN1, claims F9, spawns a vehicle,
or performs disk/network work on tick.

Build a portable source-and-binary kit with
`tools/build-standalone-starter-kit.ps1`. Exported projects reference the kit's
compile-only Core assembly and reproduce their packaged DLLs byte-for-byte.
Only the consumer assembly goes into the game's `scripts` folder; do not copy
the kit's `reference` folder over the installed shared runtime.

`MenuPrefabs` supplies typed settings, list/grid containers, status panels, and
confirmation-gated actions. Bound row parameters are host-owned. Samples use
in-memory state; add persistence in the owning mod, not in Reactor's renderer.

`Manage-Starter.ps1` supports Check, Install/repair, and Uninstall. It checks
runtime/API compatibility and required capabilities, records portable ownership
receipts under `scripts/.reactorv/consumers`, and refuses changed/unowned files.
The two-starter installer intentionally cannot install arbitrary mods or remove
the shared runtime. These receipts are ownership data, not executable authority
or proof of live readiness. They are not automatically main-menu discovery
manifests; use the explicit preload registry contract for early discovery.

The build gate tests both edition folder layouts, two compiled consumers sharing
one registry, and removal of one without affecting the other. Desktop/Story
input and presentation still require live acceptance on the target installation.

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

### Present a menu with Reactor's built-in surface

Current hosts can render registered descriptors without a project-specific
React bundle. Feature-detect the optional presentation capability so an
extension compiled against API v1 still loads on an older Reactor host:

```csharp
var presentation = handle as IReactorMenuPresentationHandle;
if (presentation == null)
{
    // Explain that Reactor must be updated, or keep the project's legacy UI.
    return;
}

presentation.TryPresentMenu("settings", new JObject {
    ["source"] = "hotkey"
});

// Use the same calls for a hotkey toggle or lifecycle cleanup.
if (presentation.IsMenuPresented("settings"))
    presentation.TryDismissMenu("settings");
```

The built-in surface supports list/grid routes, controller and mouse input,
confirmation, pagination, and host-directed refresh/close results. Reactor
owns overlay capture and publishes typed presentation/dismissal events. An
extension still owns its menu state and every action handler.

Reactor's main-menu About surface also includes a **Detected Mods** tab. It
reads the bounded `extensions.list` summary catalog and reports each registered
extension's identity, version, extension API version, and action/event/menu
counts. Registration is the live readiness boundary shown by this view. The
tab does not scan folders, expose local paths, execute extension actions, or
render extension-supplied HTML. Before the managed bridge connects, the
external host can project compact identities from an explicitly declared,
validated `extension-registry` preload entry. Those rows are labelled
**Installed / awaiting runtime**. If that snapshot is not yet available, the
last typed registry summary is labelled **Last detected / awaiting runtime**.
Neither source claims live readiness, and the live registry replaces both
after connection. The pre-provider pointer lane is limited to fixed controls
inside this bundled About surface; it is not a general extension input or API
channel.

One Story Mode extension may add
`ReactorExtensionCapabilities.DefaultF9MenuOwner` to its descriptor
capabilities. After native bootstrap handoff and only while Story Mode is
playable, Reactor defers physical F9 to that extension instead of also
toggling its generic About surface. Registration rejects a second owner. A
remapped or controller shortcut remains independent.

For listing IDs, prices, package IDs, or other values that the browser must not
choose, use a node's final `boundParameters` constructor argument. Reactor
validates and copies these values at registration, rejects any browser attempt
to replace them, then merges them into the handler parameters:

```csharp
new ReactorActionNode(
    "buy-bus",
    "Purchase Bus",
    "purchase",
    description: "$120,000",
    enabled: true,
    visible: true,
    boundParameters: new JObject {
        ["listingId"] = "citybus",
        ["quotedPrice"] = 120000,
    });
```

Bound values are a transport-integrity feature, not the final authorization
check. The owning script must still revalidate the live listing, price,
character, balance, and save state immediately before applying a mutation.

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
