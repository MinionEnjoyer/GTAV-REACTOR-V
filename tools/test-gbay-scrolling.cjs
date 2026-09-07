// Real wheel events, not Playwright's click-triggered automatic scrolling.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const out = path.resolve(__dirname, '..', '.work', 'customizer-scrolling');
const base = process.env.SCROLL_TEST_URL || 'http://127.0.0.1:5184';
fs.mkdirSync(out, { recursive: true });

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const results = [];
  try {
    for (const [width, height] of [[1280,720], [1920,1080], [2560,1440], [3840,2160]]) {
      for (const view of ['selection', 'workbench']) {
        const page = await browser.newPage({ viewport: { width, height }, reducedMotion: 'reduce' });
        await page.goto(`${base}/visual-harness.html?view=${view}&scroll-audit=1`);
        const list = page.locator('.gbay-workbench-scrollbox');
        await list.waitFor();
        await page.evaluate(() => document.fonts.ready);
        const overflow = await list.evaluate(el => el.scrollHeight - el.clientHeight);
        assert.ok(overflow > 100, `${view} fixture must really overflow`);
        const box = await list.boundingBox();
        await page.mouse.move(box.x + box.width / 2, box.y + Math.min(box.height / 2, 80));
        await page.mouse.wheel(0, 650);
        await page.waitForFunction(() => document.querySelector('.gbay-workbench-scrollbox').scrollTop > 100);
        const down = await list.evaluate(el => el.scrollTop);
        assert.equal(await page.evaluate(() => document.documentElement.dataset.auditPage), undefined,
          'Wheel in either customizer list must never invoke a page action');
        await page.mouse.wheel(0, -650);
        await page.waitForFunction(() => document.querySelector('.gbay-workbench-scrollbox').scrollTop < 5);
        // Synthetic forwarded-pointer events have no browser default action.
        // They must remain uncancelled so Reactor's existing fallback can scroll.
        const forwarded = await list.evaluate(el => {
          const leaf = el.querySelector('button strong');
          const wheel = new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: 120 });
          const unhandled = leaf.dispatchEvent(wheel);
          if (unhandled) el.scrollBy({ top: 120, behavior: 'instant' });
          return { unhandled, top: el.scrollTop };
        });
        assert.ok(forwarded.unhandled && forwarded.top > 0);
        await page.mouse.wheel(0, overflow * 2);
        await page.waitForFunction(() => {
          const el = document.querySelector('.gbay-workbench-scrollbox');
          return el.scrollTop >= el.scrollHeight - el.clientHeight - 2;
        });
        await list.locator('button').last().click();
        assert.match(await page.evaluate(() => document.documentElement.dataset.auditAction), /audit-3$/);
        assert.equal(await page.evaluate(() => document.documentElement.dataset.auditPage), undefined);
        if (width === 1920) await page.screenshot({ path: path.join(out, `${view}.png`) });
        results.push({ view, width, height, overflow, wheelScrollDown: down, forwarded, lastItemClickable: true });
        await page.close();
      }
    }
    // Preserve catalog paging; this is not a global disable of wheel handling.
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
    await page.goto(`${base}/visual-harness.html?view=weapons&scroll-audit=1`);
    await page.locator('.gbay-catalog').waitFor();
    await page.locator('.gbay-catalog').hover();
    await page.mouse.wheel(0, 120);
    await page.waitForFunction(() => document.documentElement.dataset.auditPage === 'next-page');
    await page.close();
  } finally { await browser.close(); }
  fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify({ results, marketplacePagingPreserved: true }, null, 2));
  console.log(JSON.stringify({ passed: results.length, marketplacePagingPreserved: true }));
})().catch(error => { console.error(error); process.exitCode = 1; });
