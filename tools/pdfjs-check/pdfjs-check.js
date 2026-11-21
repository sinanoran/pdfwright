// Reads every PDF in a dump folder with Firefox's pdf.js and compares its text layer with the corpus line.
//
// Usage: node pdfjs-check.js <dumpDir> <corpusDir> [id-filter]
//
// The Playwright module is found through the first of: the environment variable PLAYWRIGHT_MODULE (a path to the playwright package folder,
// for example C:/work/node_modules/playwright), or a plain require('playwright') from the folder you run the script in or above it.
//
// Compared: the text with whitespace, U+034F and the bidi controls removed, in NFD (canonically equivalent mark orders count as equal;
// the script prints EQUIV for a line that is equal only that way).
'use strict';

const path = require('path');
const fs = require('fs');

const [dumpDir, corpusDir, filter] = process.argv.slice(2);
if (!dumpDir || !corpusDir) {
  console.error('Usage: node pdfjs-check.js <dumpDir> <corpusDir> [id-filter]   (PLAYWRIGHT_MODULE names the playwright package folder)');
  process.exit(2);
}

const { firefox } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

const expected = {};
for (const f of fs.readdirSync(corpusDir).filter(f => f.endsWith('.txt'))) {
  for (const line of fs.readFileSync(path.join(corpusDir, f), 'utf8').split('\n')) {
    const l = line.replace(/\r$/, '');
    if (!l || l.startsWith('#')) continue;
    const parts = l.split('|');
    expected[parts[0]] = parts.slice(2).join('|');
  }
}

const clean = s => s.replace(/[\u034F\u200E\u200F\u202A-\u202E\u2066-\u2069]/g, '').replace(/\s+/g, '');
const cps = s => [...s].map(c => c.codePointAt(0).toString(16).toUpperCase().padStart(4, '0')).join(' ');

(async () => {
  const browser = await firefox.launch({ firefoxUserPrefs: { 'pdfjs.disabled': false, 'browser.download.open_pdf_attachments_inline': true } });
  const page = await browser.newPage();
  let ok = 0, total = 0;
  for (const file of fs.readdirSync(dumpDir).filter(f => f.endsWith('.pdf')).sort()) {
    const id = file.slice(0, -4);
    if (filter && !id.includes(filter)) continue;
    if (!(id in expected)) continue;
    total++;
    await page.goto('file:///' + path.resolve(dumpDir, file).replace(/\\/g, '/'));
    await page.waitForSelector('.textLayer span', { timeout: 15000, state: 'attached' });
    await page.waitForTimeout(300);
    const text = await page.$$eval('.textLayer span', s => s.map(x => x.textContent).join(''));
    const got = clean(text), want = clean(expected[id]);
    const good = got.normalize('NFD') === want.normalize('NFD');
    if (good && got !== want) console.log('EQUIV ' + id);
    if (good) ok++;
    else console.log(`DIFF ${id}\n  got: ${cps(got)}\n  exp: ${cps(want)}`);
  }
  console.log(`pdf.js: ${ok} of ${total} clean`);
  await browser.close();
})();
