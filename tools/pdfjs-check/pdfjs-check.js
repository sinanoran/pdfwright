// Reads every PDF in a dump folder with Firefox's pdf.js and compares its text layer with the corpus line.
//
// Usage: node pdfjs-check.js <dumpDir> <corpusDir> [id-filter]
//
// The Playwright module is found through the first of: the environment variable PLAYWRIGHT_MODULE (a path to the playwright package folder,
// for example C:/work/node_modules/playwright), or a plain require('playwright') from the folder you run the script in or above it.
//
// A dump folder may also hold documents with an expected-strings file beside them (<name>.pdf and <name>.expected.txt, written by the S6a script conformance
// tests with PDFWRIGHT_DUMP): one string per line in the order the document was given them, and a last line starting with '!' that is the header cells of a table,
// which a later page repeats. Every page of such a document is scrolled into view so that its text layer exists, the text of the pages is joined in page order,
// and each string must start where the one before it ended; the script prints how many strings passed.
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

// The text of a whole document: each page is scrolled into view, because the viewer builds the text layer of the pages near the window only.
async function readDocument(page, file) {
  await page.goto('file:///' + file.replace(/\\/g, '/'));
  await page.waitForSelector('.page .textLayer span', { timeout: 15000, state: 'attached' });
  const count = await page.$$eval('.page', p => p.length);
  const texts = {};
  for (let n = 1; n <= count; n++) {
    await page.evaluate(n => document.querySelector('.page[data-page-number="' + n + '"]').scrollIntoView(), n);
    await page.waitForSelector('.page[data-page-number="' + n + '"] .textLayer span', { timeout: 15000, state: 'attached' });
    await page.waitForTimeout(300);
    texts[n] = await page.$$eval('.page[data-page-number="' + n + '"] .textLayer span', s => s.map(x => x.textContent).join(''));
  }
  return Object.keys(texts).map(Number).sort((a, b) => a - b).map(n => texts[n]).join('');
}

// Each string must start where the one before it ended; the header of a table may stand between two strings (a later page repeats it).
function matchStrings(text, strings, header) {
  let pos = 0, passed = 0;
  const failures = [];
  for (const e of strings) {
    while (header && !text.startsWith(e, pos) && text.startsWith(header, pos)) pos += header.length;
    if (text.startsWith(e, pos)) { pos += e.length; passed++; continue; }
    failures.push('expected ' + cps(e.slice(0, 20)) + ' read ' + cps(text.slice(pos, pos + 20)));
    const at = text.indexOf(e, pos);
    if (at >= 0) pos = at + e.length;
  }
  while (header && text.startsWith(header, pos)) pos += header.length;
  return { passed, total: strings.length, trailing: text.slice(pos), failures };
}

(async () => {
  const browser = await firefox.launch({ firefoxUserPrefs: { 'pdfjs.disabled': false, 'browser.download.open_pdf_attachments_inline': true } });
  const page = await browser.newPage();
  let ok = 0, total = 0, version = null, docPassed = 0, docTotal = 0, docEquiv = 0;
  for (const file of fs.readdirSync(dumpDir).filter(f => f.endsWith('.pdf')).sort()) {
    const id = file.slice(0, -4);
    if (filter && !id.includes(filter)) continue;
    const expectedFile = path.join(dumpDir, id + '.expected.txt');
    if (fs.existsSync(expectedFile)) {
      const lines = fs.readFileSync(expectedFile, 'utf8').split('\n').filter(l => l.length);
      const header = lines.length && lines[lines.length - 1].startsWith('!') ? clean(lines.pop().slice(1)) : '';
      const text = clean(await readDocument(page, path.resolve(dumpDir, file)));
      const strings = lines.map(clean);
      const exact = matchStrings(text, strings, header);
      // Canonically equivalent orders of marks (a Hebrew shin dot and a vowel point, say) count as read, as in the corpus mode above; they are counted apart.
      const r = exact.passed === exact.total && !exact.trailing ? exact : matchStrings(text.normalize('NFD'), strings.map(x => x.normalize('NFD')), header.normalize('NFD'));
      docPassed += r.passed; docTotal += r.total; docEquiv += r.passed - exact.passed;
      console.log(`${id}: ${r.passed} of ${r.total} strings${r.passed > exact.passed ? ' (' + (r.passed - exact.passed) + ' only up to canonical equivalence)' : ''}${r.trailing ? ', trailing text ' + cps(r.trailing.slice(0, 20)) : ''}`);
      for (const d of r.failures.slice(0, 5)) console.log('  DIFF ' + d);
      continue;
    }
    if (!(id in expected)) continue;
    total++;
    await page.goto('file:///' + path.resolve(dumpDir, file).replace(/\\/g, '/'));
    await page.waitForSelector('.textLayer span', { timeout: 15000, state: 'attached' });
    await page.waitForTimeout(300);
    if (!version) {
      // The pdf.js of the browser, from the viewer page; the browser version stands in when the page does not expose it.
      version = await page.evaluate(() => (typeof pdfjsLib !== 'undefined' && pdfjsLib.version) || (window.PDFViewerApplication && window.PDFViewerApplication.pdfjsVersion) || null).catch(() => null);
    }
    const text = await page.$$eval('.textLayer span', s => s.map(x => x.textContent).join(''));
    const got = clean(text), want = clean(expected[id]);
    const good = got.normalize('NFD') === want.normalize('NFD');
    if (good && got !== want) console.log('EQUIV ' + id);
    if (good) ok++;
    else console.log(`DIFF ${id}\n  got: ${cps(got)}\n  exp: ${cps(want)}`);
  }
  console.log(version ? `pdf.js ${version} (Firefox ${browser.version()})` : `pdf.js version not exposed by the page (Firefox ${browser.version()})`);
  if (total) console.log(`pdf.js: ${ok} of ${total} clean`);
  if (docTotal) console.log(`pdf.js documents: ${docPassed} of ${docTotal} strings (${docPassed - docEquiv} byte-equal, ${docEquiv} only up to canonical equivalence)`);
  await browser.close();
})();
