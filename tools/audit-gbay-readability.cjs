// Run an ALLIN1-mode Vite dev server first. No GTA process or game files are used.
// PLAYWRIGHT_MODULE may point at an existing installation; defaults to the standard package.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs');
const path = require('node:path');
const out = path.resolve(process.env.READABILITY_OUTPUT || path.join(__dirname, '..', '.work', 'readability'));
const baseUrl = process.env.READABILITY_URL || 'http://127.0.0.1:5182';
const assert = require('node:assert/strict');
const css = fs.readFileSync(path.join(__dirname, '..', 'web', 'src', 'styles.css'), 'utf8');
fs.mkdirSync(out, { recursive: true });
(async () => {
  const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true, args: ['--disable-gpu'] });
  const results = [];
  try {
    for (const [width, height] of [[1280,720], [1920,1080], [2560,1440], [3840,2160], [960,540]]) {
      const page = await browser.newPage({ viewport: { width, height }, reducedMotion: 'reduce' });
      for (const view of ['home','vehicles','weapons','gear','garage','delivery','diagnostics','addons','about','selection','workbench','confirmation','preloader']) {
        await page.goto(`${baseUrl}/visual-harness.html?view=${view}`);
        await page.locator('.gbay-shell, .gbay-weapon-editor-shell, .startup-transition-modal').waitFor();
        await page.evaluate(() => document.fonts.ready);
        const invalidFonts = await page.evaluate(source => {
          const variables = getComputedStyle(document.documentElement);
          return [...source.matchAll(/\\b(font(?:-size)?):\\s*([^;}]+)/g)].filter(([,property,value]) => {
            const resolved = value.replace(/var\\((--[\\w-]+)\\)/g, (_,name) => variables.getPropertyValue(name).trim());
            return !CSS.supports(property, resolved);
          }).map(([,property,value])=>({property,value}));
        }, css);
        assert.deepEqual(invalidFonts, [], 'Every font declaration must parse in the browser');
        const metrics = await page.evaluate(() => {
          const root = document.querySelector('.gbay-shell, .gbay-weapon-editor-shell, .startup-transition-modal');
          const box = root.getBoundingClientRect();
          const text = Array.from(document.querySelectorAll('[class*="gbay-"] *, [class*="startup-"] *')).filter(el => {
            const s = getComputedStyle(el), r = el.getBoundingClientRect();
            return s.display !== 'none' && s.visibility !== 'hidden' && r.width && r.height &&
              [...el.childNodes].some(n => n.nodeType === Node.TEXT_NODE && n.textContent.trim().length > 2);
          });
          const small = text.filter(el => parseFloat(getComputedStyle(el).fontSize) < 12.9).map(el => ({ cls: el.className, text: el.textContent.slice(0,70), size: getComputedStyle(el).fontSize }));
          const clipped = text.filter(el => el.scrollWidth > el.clientWidth + 3 && getComputedStyle(el).overflowX === 'hidden').map(el => ({cls: el.className, text: el.textContent.slice(0,70), width: el.clientWidth, needed: el.scrollWidth}));
          function color(value) { return value.match(/[\d.]+/g).map(Number); }
          function background(el) {
            if (!el) return [255,255,255];
            const c = color(getComputedStyle(el).backgroundColor), a = c[3] ?? 1;
            return a === 1 ? c.slice(0,3) : background(el.parentElement).map((v,i) => c[i] * a + v * (1-a));
          }
          function luminance(rgb) { return rgb.map(v => { v /= 255; return v <= .04045 ? v / 12.92 : ((v+.055)/1.055)**2.4; }).reduce((n,v,i) => n + v * [.2126,.7152,.0722][i],0); }
          const contrast = text.filter(el => !el.closest(':disabled,[aria-disabled="true"]')).map(el => {
            const s = getComputedStyle(el), bg = background(el), c = color(s.color), a = c[3] ?? 1;
            const l1 = luminance(bg), l2 = luminance(c.slice(0,3).map((v,i)=>v*a+bg[i]*(1-a)));
            const ratio = (Math.max(l1,l2)+.05)/(Math.min(l1,l2)+.05);
            const threshold = parseFloat(s.fontSize) >= 24 || (parseFloat(s.fontSize) >= 18.66 && parseFloat(s.fontWeight) >= 700) ? 3 : 4.5;
            return {cls:el.className,tag:el.tagName,text:el.textContent.slice(0,60),color:s.color,bg,ratio,threshold};
          }).filter(x=>x.ratio+.01<x.threshold);
          const footer = root.querySelector('footer').getBoundingClientRect();
          const scroll = root.querySelector('.gbay-workbench-options, .gbay-garage-grid, .gbay-catalog');
          return { shell: { x: box.x,y:box.y,width:box.width,height:box.height }, footerBottom: footer.bottom,
            outside: box.left < 16 || box.top < 16 || box.right > innerWidth - 16 || box.bottom > innerHeight - 16,
            small, clipped, contrast, scroll: scroll ? { client: scroll.clientHeight, total: scroll.scrollHeight } : null,
            overflowX: root.scrollWidth > root.clientWidth + 2 };
        });
        if (width === 1920 || (width === 1280 && ['workbench','vehicles','garage'].includes(view))) {
          await page.screenshot({ path: path.join(out, `${view}-${width}.png`) });
        }
        if (view === 'weapons') {
          const badge = await page.locator('.gbay-weapon-owned-badge').boundingBox();
          const favorite = await page.locator('.gbay-card-favorite').first().boundingBox();
          assert.ok(badge.x + badge.width <= favorite.x, 'Owned badge and favorite must not overlap');
          const category = await page.locator('.gbay-product-card.owned .gbay-card-visual small').boundingBox();
          assert.ok(category.y >= badge.y + badge.height || category.x + category.width <= badge.x, 'Category must not overlap ownership controls');
          await page.locator('.gbay-card-favorite').first().click();
          assert.equal(await page.evaluate(()=>document.documentElement.dataset.auditAction), 'weapon-favorite-0');
        }
        if (view === 'workbench') {
          await page.locator('.gbay-workbench-card').last().click();
          assert.equal(await page.evaluate(()=>document.documentElement.dataset.auditAction), 'component-grip', 'Last attachment is reachable by scrolling');
        }
        if (view === 'garage') {
          await page.locator('.gbay-garage-grid article').last().getByRole('button', {name:'RETRIEVE', exact:true}).click();
          assert.equal(await page.evaluate(()=>document.documentElement.dataset.auditAction), 'stored-davis-23-retrieve', 'Last stored vehicle is reachable');
        }
        if (view === 'confirmation') {
          await page.getByRole('button', {name:'Confirm', exact:true}).click();
          assert.equal(await page.evaluate(()=>document.documentElement.dataset.auditConfirmation), 'true');
          assert.equal(await page.evaluate(()=>document.documentElement.dataset.auditAction), undefined, 'Dialog action must not hit underlying UI');
        }
        results.push({ view, width, height, ...metrics });
      }
      await page.close();
    }
  } finally { await browser.close(); }
  fs.writeFileSync(path.join(out,'browser-audit.json'), JSON.stringify(results,null,2));
  const failures = results.filter(r => r.outside || r.overflowX || r.small.length || r.clipped.length || r.contrast.length);
  console.log(JSON.stringify({checked: results.length, failed:failures.length, layout:failures.filter(r=>r.outside||r.overflowX||r.small.length||r.clipped.length), contrast:[...new Map(failures.flatMap(r=>r.contrast).map(c=>[c.cls+c.tag+c.color,c])).values()]},null,2));
  if (failures.length) process.exitCode = 1;
})().catch(e => { console.error(e); process.exitCode = 1; });
