import { expect, type Page, test } from '@playwright/test';
import { expectNoDocumentOverflow, openWorkbench, switchEditor, visibleBox, workbenchPane } from './helpers/workbench';

async function splitPercent(page: Page): Promise<number> {
  const value = await page.getByRole('separator', { name: 'Resize source and result panes' }).getAttribute('aria-valuenow');
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) throw new Error(`Invalid pane split percentage '${value}'.`);
  return parsed;
}

test('resizes, persists, and resets the responsive source/result split', async ({ page, isMobile }) => {
  await openWorkbench(page);
  const separator = page.getByRole('separator', {
    name: 'Resize source and result panes',
  });
  await expect(separator).toHaveAttribute('aria-orientation', isMobile ? 'horizontal' : 'vertical');
  await expect(separator).toHaveAttribute('aria-valuenow', '50');

  const grid = await visibleBox(page.locator('.pane-grid'), 'pane grid');
  const handle = await visibleBox(separator, 'pane separator');
  const targetPercent = isMobile ? 58 : 64;
  const targetX = isMobile ? handle.x + handle.width / 2 : grid.x + grid.width * (targetPercent / 100);
  const targetY = isMobile ? grid.y + grid.height * (targetPercent / 100) : handle.y + handle.height / 2;
  await page.mouse.move(handle.x + handle.width / 2, handle.y + handle.height / 2);
  await page.mouse.down();
  await page.mouse.move(targetX, targetY, { steps: 8 });
  await page.mouse.up();
  await expect.poll(() => splitPercent(page)).toBeCloseTo(targetPercent, 0);

  const source = await visibleBox(workbenchPane(page, 'source'), 'source pane after resize');
  const result = await visibleBox(workbenchPane(page, 'result'), 'result pane after resize');
  const sourceSize = isMobile ? source.height : source.width;
  const resultSize = isMobile ? result.height : result.width;
  expect(sourceSize / (sourceSize + resultSize)).toBeCloseTo(targetPercent / 100, 1);

  await switchEditor(page, isMobile ? 'monaco' : 'codemirror');
  await expect.poll(() => splitPercent(page)).toBeCloseTo(targetPercent, 0);
  await page.reload();
  await expect(page.getByLabel('Language')).toBeEnabled();
  await expect.poll(() => splitPercent(page)).toBeCloseTo(targetPercent, 0);

  const restoredSeparator = page.getByRole('separator', {
    name: 'Resize source and result panes',
  });
  await restoredSeparator.dblclick();
  await expect(restoredSeparator).toHaveAttribute('aria-valuenow', '50');
  const resetSource = await visibleBox(workbenchPane(page, 'source'), 'reset source pane');
  const resetResult = await visibleBox(workbenchPane(page, 'result'), 'reset result pane');
  const resetSourceSize = isMobile ? resetSource.height : resetSource.width;
  const resetResultSize = isMobile ? resetResult.height : resetResult.width;
  expect(resetSourceSize / (resetSourceSize + resetResultSize)).toBeCloseTo(0.5, 2);
  await expectNoDocumentOverflow(page);
});
