// Run `pnpm exec vite --mode allin1 --host 127.0.0.1 --port 5186 --strictPort`
// from web first. Uses the real input adapter and GBAY DOM in isolated Chromium;
// it does not qualify the CEF shared-texture compositor or launch/change GTA.
// PLAYWRIGHT_MODULE / PNGJS_MODULE may point to existing package installations.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const { PNG } = require(process.env.PNGJS_MODULE || 'pngjs');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const base = process.env.CURSOR_TEST_URL || 'http://127.0.0.1:5186';
const out = path.resolve(__dirname, '..', '.work', 'gbay-cursor');

// Compare actual pixels, not just z-index declarations. Hiding an occluded
// cursor would leave this crop unchanged and fail the test.
function changedPixels(before, after) {
  const a = PNG.sync.read(before), b = PNG.sync.read(after);
  assert.equal(a.width, b.width);
  assert.equal(a.height, b.height);
  let changed = 0;
  for (let i = 0; i < a.data.length; i += 4) {
    if ([0, 1, 2].some(channel => Math.abs(a.data[i + channel] - b.data[i + channel]) > 20)) changed++;
  }
  return changed;
}

(async () => {
  fs.mkdirSync(out, { recursive: true });
  const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
  const results = [];
  try {
    for (const [width, height, scale] of [[1280, 720, 1], [1920, 1080, 1], [2560, 1440, 1.25]]) {
      const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: scale, reducedMotion: 'reduce' });
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      // Use the same typed DOM-event transport as CEF, with no real host/game.
      await page.addInitScript(() => { window.CefSharp = { PostMessage() {} }; });
      await page.goto(`${base}/visual-harness.html?view=weapons`);
      await page.locator('.gbay-card-favorite').first().waitFor();
      await page.evaluate(() => document.fonts.ready);
      await page.evaluate(async () => {
        const { installWindowedInputForwarding } = await import('/src/gta/windowedInput.ts');
        const gate = await import('/src/gta/providerInputGate.ts');
        const { bridge } = await import('/src/gta/bridge.ts');
        if (!bridge.isNative) throw new Error('Fixture must exercise the native event adapter');
        const emit = (event, payload) => window.dispatchEvent(new CustomEvent('ragewebui:message', {
          detail: { kind: 'event', event, payload },
        }));
        window.cursorTest = { emit, gate, dispose: installWindowedInputForwarding(), clicks: 0 };
        gate.revokeProviderInput();
        emit('host.surface', { mode: 'about', generation: 1 });
        const about = document.createElement('div');
        about.className = 'reactor-about-surface';
        about.style.cssText = 'position:fixed;left:0;top:0;width:200px;height:100px;z-index:100';
        about.innerHTML = '<div class="reactor-about-tabs"><button data-reactor-bootstrap-action="overview">About</button></div>';
        document.getElementById('root').append(about);
        emit('input.bootstrapPointer', { x: .03, y: .03, pressed: false, released: false, wheelDelta: 0 });
        if (document.querySelector('[data-reactor-windowed-cursor]').dataset.reactorWindowedCursorOwner !== 'bootstrap') {
          throw new Error('Fixture did not establish the splash cursor');
        }
        emit('host.provider', { connected: true, sessionGeneration: 1 });
        emit('host.surface', { mode: 'none', generation: 2 });
        about.remove();
        gate.prepareProviderInput('cursor-gbay');
        gate.activateProviderInput('cursor-gbay');
        document.querySelector('.gbay-card-favorite').addEventListener('click', () => window.cursorTest.clicks++);
      });
      const box = await page.locator('.gbay-card-favorite').first().boundingBox();
      const point = { x: (box.x + box.width / 2) / (width - 1), y: (box.y + box.height / 2) / (height - 1) };
      await page.evaluate(({ x, y }) => {
        const { emit } = window.cursorTest;
        emit('input.pointer', { x, y, pressed: true, released: false, wheelDelta: 0 });
        // No new provider sample between the stale events and our assertion.
        emit('input.bootstrapPointerReset', null);
        emit('input.bootstrapPointer', { x: .01, y: .01, pressed: true, released: true, wheelDelta: 0 });
        emit('host.provider', { connected: true, sessionGeneration: 1 });
        emit('host.surface', { mode: 'none', generation: 2 });
        emit('host.surface', { mode: 'malformed' });
      }, point);
      const cursor = page.locator('[data-reactor-windowed-cursor]');
      assert.equal(await cursor.evaluate(el => getComputedStyle(el).display), 'block');
      const metrics = await cursor.evaluate(el => {
        const rect = el.getBoundingClientRect(), style = getComputedStyle(el);
        return { owner: el.dataset.reactorWindowedCursorOwner, parent: el.parentElement.tagName,
          zIndex: style.zIndex, pointerEvents: style.pointerEvents, x: rect.x, y: rect.y,
          width: rect.width, height: rect.height,
          hitTarget: document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2)?.closest('button')?.className };
      });
      assert.equal(metrics.owner, 'provider');
      assert.equal(metrics.parent, 'BODY');
      assert.equal(metrics.zIndex, '2147483647');
      assert.equal(metrics.pointerEvents, 'none');
      assert.match(metrics.hitTarget, /gbay-card-favorite/);
      const clip = { x: Math.floor(metrics.x - 4), y: Math.floor(metrics.y - 4), width: 28, height: 28 };
      const visiblePixels = await page.screenshot({ clip });
      await cursor.evaluate(el => { el.style.visibility = 'hidden'; });
      const hiddenPixels = await page.screenshot({ clip });
      await cursor.evaluate(el => { el.style.visibility = ''; });
      const paintedPixels = changedPixels(visiblePixels, hiddenPixels);
      assert.ok(paintedPixels > 30, `Cursor must paint above the opaque GBAY button, changed=${paintedPixels}`);
      await page.evaluate(({ x, y }) => window.cursorTest.emit('input.pointer', {
        x, y, pressed: false, released: true, wheelDelta: 0,
      }), point);
      assert.equal(await page.evaluate(() => window.cursorTest.clicks), 1);
      assert.equal(await page.evaluate(() => document.documentElement.dataset.auditAction), 'weapon-favorite-0');
      await page.screenshot({ path: path.join(out, `gbay-cursor-${width}.png`) });
      await page.evaluate(() => window.cursorTest.emit('host.provider', { connected: false, sessionGeneration: 1 }));
      assert.equal(await cursor.evaluate(el => getComputedStyle(el).display), 'none');
      await page.evaluate(() => window.cursorTest.dispose());
      assert.equal(await cursor.count(), 0);
      assert.deepEqual(errors, []);
      results.push({ viewport: { width, height, scale }, cursor: metrics, paintedPixels,
        clickCount: 1, disconnectedCursorHidden: true });
      await page.close();
    }
  } finally { await browser.close(); }
  fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify({ scope: 'Chromium DOM, not GTA/CEF integration', results }, null, 2));
  console.log(JSON.stringify({ passed: results.length, output: out }));
})().catch(error => { console.error(error); process.exitCode = 1; });
