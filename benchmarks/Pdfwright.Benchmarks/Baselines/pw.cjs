const { chromium } = require(process.env.PW_CORE || 'playwright-core');
(async () => {
  const rows = Number(process.argv[2] || 1000);
  const t0 = Date.now();
  const browser = await chromium.launch({ executablePath: process.env.PW_CHROME });
  const t1 = Date.now();
  const page = await browser.newPage();
  let html = '<html><body style="font-family:Segoe UI;font-size:10pt"><h1>Report</h1><table style="width:100%;border-collapse:collapse"><thead><tr>' + [1,2,3,4,5,6].map(i=>`<th>Col ${i}</th>`).join('') + '</tr></thead><tbody>';
  for (let r = 0; r < rows; r++) html += '<tr>' + [1,2,3,4,5,6].map(c=>`<td style="border-bottom:1px solid #ccc">Row ${r} cell ${c} text</td>`).join('') + '</tr>';
  html += '</tbody></table></body></html>';
  await page.setContent(html);
  const t2 = Date.now();
  const pdf = await page.pdf({ format: 'A4', displayHeaderFooter: true, margin: { top: '20mm', bottom: '20mm' } });
  const t3 = Date.now();
  console.log(JSON.stringify({ rows, launchMs: t1 - t0, setContentMs: t2 - t1, pdfMs: t3 - t2, totalMs: t3 - t0, bytes: pdf.length, node: process.pid }));
  await new Promise(r => setTimeout(r, 1500));
  await browser.close();
})();
