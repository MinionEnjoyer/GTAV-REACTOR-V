# REACTOR V browser API

REACTOR V exposes an additive browser API. Existing v1 pages keep working
unchanged through `window.rageWebUI`; new pages use `window.reactorV`.

```ts
const runtime = await window.reactorV.runtime.handshake({ apiVersions: [2, 1] })
if (!runtime.capabilities.includes('menu.discovery')) throw new Error('Menus are unavailable')

await window.reactorV.overlay.setVisibility('visible')
await window.reactorV.overlay.setInputMode('menu')
```

Every helper returns a `Promise`. Rejections are `GtaBridgeError` instances
with a stable `code` and readable `message`.

## Two version numbers, two responsibilities

- **Bridge protocol v2** governs page-to-host messages, cancellation,
  deadlines, confirmation metadata, and idempotency metadata.
- **Extension API v1** is the current managed registration contract used by
  ALLIN1 and other gameplay extensions. `extensions.list` reports its
  `extensionApiVersion` independently of the bridge protocol.

A v2 browser therefore can safely host an extension whose
`extensionApiVersion` is `1`. Do not compare those numbers as if they were the
same API.

## v2 methods

| Helper | Wire method | Purpose |
|---|---|---|
| `runtime.handshake(request?)` | `runtime.handshake` | Negotiate the browser API and read runtime identity/capabilities. |
| `runtime.describe()` | `runtime.describe` | Enumerate methods, events, capabilities, and limits. |
| `overlay.setVisibility(state)` | `overlay.setVisibility` | Set `visible`, `hidden`, or `toggle`. |
| `overlay.setInputMode(mode)` | `overlay.setInputMode` | Select `game`, `menu`, `pointer`, or `exclusive` input. |
| `extensions.list()` | `extensions.list` | Read compact registered-extension summaries. |
| `extensions.get(extensionId)` | `extensions.get` | Read one extension's capabilities, typed actions, events, and menu IDs. |
| `extensions.invoke(request)` | `extensions.invoke` | Invoke one declared extension action. |
| `menu.list(extensionId?)` | `menu.list` | Read compact menu summaries (capped at 256 when unfiltered). |
| `menu.get(extensionId, menuId)` | `menu.get` | Read one exact flat menu descriptor. |
| `menu.invoke(request)` | `menu.invoke` | Invoke a declared menu node after host-side node/action verification. |
| `events.subscribe(request, listener?)` | `events.subscribe` | Register a bounded host subscription and optional local listener. |
| `events.unsubscribe(id)` | `events.unsubscribe` | Release a host subscription. |

`runtime.lifecycle` reports host phases such as `browser-ready`, `story-ready`,
and `shutting-down`. `input.action` reports normalized keyboard, mouse, controller,
and game actions. Extension events are namespaced as
`<extensionId>.<eventId>`.

## Extensions and safe invocation

`extensions.list()` returns `{ total, items }`, where each compact item carries
identity, version, extension API version, and action/event/menu counts. Call
`extensions.get(id)` for capabilities and full action/event/menu-ID detail.
Each action declares its parameter types, risk (`read`, `gameplay`, or
`persistent`), confirmation requirement, and whether extra parameters are
accepted. This split keeps discovery bounded even with many large extensions.

```ts
await window.reactorV.extensions.invoke({
  extensionId: 'allin1.online',
  actionId: 'gbay.purchase',
  parameters: { listingId: 'vehicle-42' },
  confirmed: true,
  idempotencyKey: crypto.randomUUID(),
})
```

Persistent actions require an explicit confirmation and a stable idempotency
key. The SDK places both values in the action payload and protocol-v2 envelope;
the host remains authoritative. A result reports `succeeded`,
`confirmationRequired`, `replayed`, and either `value` or `error`.

## Menus

Menu discovery returns `{ total, truncated, items }`. Each item contains
`extensionId`, `id`, `label`, `order`, and `nodeCount`; `truncated` tells a
global browser to narrow the query by extension. Fetch exact extension API v1
menu JSON with `menu.get`. Full extension/menu details are capped below the
64-KiB bridge limit and fail closed if an extension exceeds that budget:

```ts
const index = await window.reactorV.menu.list('allin1.online')
const wireMenus = await Promise.all(index.items.map((summary) =>
  window.reactorV.menu.get(summary.extensionId, summary.id)))
// { extensionId, id, label, description, icon, order, nodes }
```

`nodes` are a discriminated union keyed by `kind`: `action`, `toggle`,
`choice`, `range`, `text`, `search`, `keybind`, `tabs`, `list`, `grid`,
`media`, `status`, `progress`, `pagination`, `separator`, and `submenu`. Action-bearing nodes
contain `actionId`; the page must not invent or override that binding.

For route-oriented interfaces, use the explicit adapter and headless
controller:

```ts
const routed = window.reactorV.adaptMenusToRoutes(wireMenus, 'gbay')
const menu = new window.reactorV.MenuController(routed, {
  invoke: (request) => window.reactorV.menu.invoke(request),
})

menu.moveFocus(1)
await menu.activate()
menu.back()
menu.home()
```

The controller supports `push`, `replace`, `back`, `home`, focus movement,
activation, direct value changes, and range/choice/toggle adjustment. Browser
route IDs never cross the wire. `menu.invoke` sends only
`extensionId`, `menuId`, `nodeId`, `interaction`, optional `parameters` or
`value`, and confirmation/idempotency metadata. For a value control, `value`
is the conventional action parameter.
Pass action-specific values through `controller.activate({ parameters: {...} })`;
the host still resolves the node's registered `actionId` and validates those
parameters against its descriptor.

## Events and lifecycle

```ts
const stopLifecycle = window.reactorV.events.onLifecycle((event) => {
  if (event.phase === 'story-ready') console.log('Safe to show the main menu')
})

const subscription = await window.reactorV.events.subscribe(
  { events: ['allin1.axles.telemetry'], cadenceMs: 100 },
  (name, payload) => console.log(name, payload),
)

await subscription.unsubscribe()
stopLifecycle()
```

Subscriptions should be released when a page or panel closes. High-rate
telemetry belongs in bounded events, not repeated polling calls.

## Timeout and cancellation

Every call accepts `InvokeOptions` as its last argument. The old numeric
timeout remains valid for v1 helpers.

```ts
const controller = new AbortController()
const request = window.reactorV.runtime.describe({
  timeoutMs: 8_000,
  deadlineMs: 5_000,
  signal: controller.signal,
})

controller.abort() // rejects locally and sends a protocol-v2 cancel envelope
await request
```

Timeouts, deadlines, and idempotency keys are validated before a message is
posted. Calling `invoke` after bridge destruction rejects with `disposed`.
Malformed host messages are ignored, and one throwing event listener cannot
prevent other listeners from running.

## v1 compatibility

All original helpers and `window.rageWebUI` remain available:

```ts
const { gta, bridge } = window.rageWebUI
const state = await gta.getState()
await gta.player.setWantedLevel(0)
const stop = bridge.on('game.state', (next) => console.log(next.player.position))
```

The v1 helpers remain `overlay.ready`, `overlay.close`, `game.getState`,
`ui.notify`, player heal/invincibility/wanted/teleport, vehicle repair/spawn,
and world time/weather.

## Security boundary

The browser receives only described methods, extension actions, menus, and
events. It has no arbitrary native-call, memory-write, filesystem, or script
execution primitive. Game objects and native calls remain on the game thread;
persistent work remains confirmation- and idempotency-gated by the host.

See `web/examples` for ALLIN1/GBAY, axle telemetry, and suppressor fixtures.
Managed gameplay projects should also read [EXTENSIONS.md](EXTENSIONS.md) and
build the external-consumer examples in `examples/ReactorV.Extension.Examples`.
