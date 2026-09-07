import type { MenuItem } from './gta/types'
import type { MenuControllerSnapshot } from './menu/controller'

const sections = ['home', 'vehicles', 'weapons', 'weapons.customize', 'gear', 'garage', 'addons', 'diagnostics', 'about']
const nav: MenuItem[] = sections.map((id) => ({ id: `gbay-nav-${id === 'weapons.customize' ? 'customization' : id}`, type: 'route', label: id === 'garage' ? 'My garage' : id, icon: '◆', routeId: id }))
function scene(id: string, items: MenuItem[]): MenuControllerSnapshot {
  return { menuId: id, stack: ['home', id], route: { id, menuId: id, title: id.toUpperCase(), items: [...nav, ...items] } }
}
const search: MenuItem = { id: 'search', label: 'Search', type: 'search', value: '', placeholder: 'Search the catalogue', action: 'catalog.search', maxLength: 80 }
const category: MenuItem = { id: 'category', label: 'Category', type: 'choice', value: 'all', action: 'catalog.category', options: ['All', 'Sports Classics', 'Super', 'Off-Road', 'Commercial', 'Emergency'].map((label) => ({ value: label.toLowerCase(), label })) }
const weaponCategory: MenuItem = { ...category, options: ['All', 'Pistols', 'Submachine Guns', 'Rifles', 'Heavy Weapons', 'Throwables'].map(label => ({value:label.toLowerCase(), label})) }
const gearCategory: MenuItem = { ...category, options: ['All', 'Armor', 'Equipment'].map(label => ({value:label.toLowerCase(), label})) }
const pages: MenuItem = { id: 'pages', label: 'Page', type: 'pagination', page: 1, pageCount: 14, action: 'catalog.page' }
const locations = ['Davis Garage', 'Garment Factory', 'Harmony Garage', 'Paleto Bay', 'Grapeseed', 'Harbour Marina', 'Yacht Helipad']
export const readabilityFixtures: Record<string, MenuControllerSnapshot> = {
  home: scene('home', sections.filter((id) => id !== 'home').map((id) => ({ id: `open-${id}`, label: id === 'weapons.customize' ? 'Customize weapons' : id, type: 'route', routeId: id, description: 'Browse available content and manage your Story Mode collection.' }))),
  vehicles: scene('vehicles', [search, category, pages,
    { id: 'ownership', label: 'Ownership', type: 'choice', value: 'all', action: 'catalog.ownership', options: [{ value: 'all', label: 'All listings' }, { value: 'owned', label: 'Owned vehicles' }] },
    { id: 'favorites', label: 'Favorites only', type: 'toggle', value: false, action: 'catalog.favorites' },
    ...['Benefactor Schafter LWB (Armored)', 'Pegassi Torero XO', 'Karin Sultan RS Classic', 'Bravado Buffalo STX', 'Ocelot Virtue', 'Grotti Turismo Classic'].map((label, i): MenuItem => ({ id: `vehicle-${i}`, label, type: 'command', action: 'vehicle.purchase', description: 'Category: Sports Classics · Manufacturer: Benefactor · Price: $2,450,000 · Ownership: Available' })),
  ]),
  weapons: scene('weapons', [search, weaponCategory, pages,
    ...['KRISS Vector .45 ACP — Suppressed', 'Special Carbine Mk II', 'Combat Pistol', 'Heavy Sniper Mk II', 'Compact Grenade Launcher', 'Smoke Grenade Bundle'].map((label, i): MenuItem => ({ id: `weapon-${i}`, label, type: 'command', enabled: i !== 0, action: 'weapon.purchase', description: `Category: Submachine Guns · Price: $12,500 · Ownership: ${i === 0 ? 'Owned' : 'Available'}` })),
    { id: 'weapon-favorite-0', label: 'Add favorite', type: 'command', action: 'weapon.favorite' },
  ]),
  gear: scene('gear', [gearCategory, pages, ...['Super Heavy Armor', 'Parachute', 'Night Vision', 'Jerry Can', 'Fire Extinguisher', 'Smoke Grenades'].map((label, i): MenuItem => ({ id: `gear-${i}`, label, type: 'command', action: 'gear.apply', description: 'Category: Equipment · Price: $2,500 · Status: Available' }))]),
  garage: scene('garage', [
    { id: 'location-filter', label: 'Garage', value: '0', type: 'choice', action: 'garage.location', options: locations.map((label, i) => ({ value: String(i), label })) },
    ...locations.flatMap((label, i): MenuItem[] => [
      { id: `location-${i}`, label, value: '8 / 10 spaces used', type: 'status' },
      { id: `location-waypoint-${i}`, label: `Navigate to ${label}`, action: 'garage.waypoint', type: 'command' },
    ]),
    { id: 'results', label: 'Stored vehicles', value: '24 stored vehicles', type: 'status' },
    ...Array.from({ length: 24 }, (_, i) => ['retrieve', 'sell'].map((action): MenuItem => ({ id: `stored-davis-${i}-${action}`, label: `${action} Benefactor Schafter LWB (Armored) ${i + 1}`, type: 'command', action: `garage.${action}`, description: `Location: Davis Garage · Plate: ALLIN1 · Sale value: $1,225,000 · Insured and stored safely` }))).flat(),
  ]),
  delivery: scene('vehicle-delivery', locations.map((label, i): MenuItem => ({ id: `deliver-${i}`, label, description: 'Two spaces available. The vehicle will be delivered here after confirmation.', type: 'command', action: 'vehicle.deliver' }))),
  diagnostics: scene('diagnostics', Array.from({ length: 16 }, (_, i): MenuItem => ({ id: `service-${i}`, label: ['Reactor rendering', 'ScriptHook bridge', 'Character inventory', 'Garage map streaming'][i % 4], value: i === 3 ? 'Waiting for a safe garage transition before requesting map assets' : 'Ready · last updated just now', tone: i === 3 ? 'warning' : 'success', type: 'status' }))),
  addons: scene('addons', [
    { id: 'heat', label: 'Suppressor heat and smoke', description: 'Gradual heat with weapon-specific wear and attachment tracking.', type: 'toggle', value: true, action: 'addon.heat' },
    { id: 'traffic', label: 'Addon vehicles in traffic', description: 'Applies only to compatible vehicles enabled in your catalogue.', type: 'toggle', value: false, action: 'addon.traffic' },
  ]),
  about: scene('about', [
    { id: 'version', label: 'Version', value: '0.6.1', type: 'status' },
    { id: 'edition', label: 'Edition', value: 'Enhanced', type: 'status' },
    { id: 'runtime', label: 'Runtime', value: 'ScriptHookVDotNet v3', type: 'status' },
    { id: 'support', label: 'Support development', value: 'buymeacoffee.com/minionenjoyer', type: 'status' },
    { id: 'support-action', label: 'Support ALLIN1', type: 'command', action: 'about.support' },
  ]),
}
