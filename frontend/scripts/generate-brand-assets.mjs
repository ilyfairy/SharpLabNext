import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from '@playwright/test';

const publicRoot = resolve(import.meta.dirname, '..', 'public');
const source = await readFile(resolve(publicRoot, 'favicon.svg'), 'utf8');
const sourceUrl = `data:image/svg+xml;base64,${Buffer.from(source).toString('base64')}`;
const outputs = [
  ['favicon-32.png', 32],
  ['apple-touch-icon.png', 180],
  ['app-icon-192.png', 192],
  ['app-icon-512.png', 512],
];

const browser = await chromium.launch({ headless: true });
try {
  for (const [fileName, size] of outputs) {
    const page = await browser.newPage({
      viewport: { width: size, height: size },
      deviceScaleFactor: 1,
    })
    await page.setContent(`<style>html,body{margin:0;width:${size}px;height:${size}px;overflow:hidden}img{display:block;width:${size}px;height:${size}px}</style><img alt="" src="${sourceUrl}">`)
    const image = page.locator('img');
    await image.evaluate((element) => element.decode());
    await image.screenshot({ path: resolve(publicRoot, fileName) });
    await page.close();
  }
} finally {
  await browser.close();
}
