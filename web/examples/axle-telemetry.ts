import { reactorV } from '../src/sdk'

const extensionIndex = await reactorV.extensions.list()
const extensionResults = await Promise.all(extensionIndex.items.map((extension) =>
  reactorV.extensions.get(extension.id)))
const extensionDetails = extensionResults.filter((extension): extension is NonNullable<typeof extension> => extension !== null)
const axleExtension = extensionDetails.find((extension) => extension.capabilities.includes('axles.telemetry'))

if (!axleExtension) throw new Error('No axle telemetry extension is registered.')

const subscription = await reactorV.events.subscribe(
  {
    events: [`${axleExtension.id}.telemetry`],
    cadenceMs: 100,
    filters: { vehicleModel: 'metrobusxl2' },
  },
  (_eventName, payload) => {
    // Render only extension-owned telemetry: wheel world data, steering,
    // torque, resistance, suspension, and validation flags.
    console.table(payload)
  },
)

reactorV.events.onLifecycle((event) => {
  if (event.phase === 'shutting-down') void subscription.unsubscribe()
})
