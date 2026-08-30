import { expect, test } from '@playwright/test';
import { editorHost, openWorkbench, replaceSource, sourceEditor, switchEditor, waitForLanguageServiceReady } from './helpers/workbench';

const semanticSource = `using System;

public sealed class SemanticWidget
{
    public static void Main() => Console.WriteLine("ready");
}
`;

test.describe('CodeMirror interaction stability', () => {
  test.beforeEach(({ isMobile }) => {
    test.skip(isMobile, 'Desktop pointer and hover coverage.');
  });

  test('keeps focused and inactive text selections visibly distinct from the active line', async ({ page }) => {
    await openWorkbench(page);
    await switchEditor(page, 'codemirror');

    const editor = sourceEditor(page);
    await editor.click();
    await page.keyboard.press('ControlOrMeta+A');

    const selection = editorHost(page, 'codemirror').locator('.cm-selectionBackground').first();
    await expect(selection).toBeVisible();
    await expect(selection).toHaveCSS('background-color', 'rgb(159, 174, 190)');

    const selectedTextColor = await editorHost(page, 'codemirror')
      .locator('.cm-content')
      .evaluate((content) => getComputedStyle(content, '::selection').color);
    expect(selectedTextColor).toBe('rgb(30, 30, 30)');

    const activeLine = editorHost(page, 'codemirror').locator('.cm-activeLine').first();
    await expect(activeLine).toHaveCSS('background-color', 'rgba(237, 242, 247, 0.58)');

    await page.getByLabel('Language').focus();
    await expect(selection).toBeVisible();
    await expect(selection).toHaveCSS('background-color', 'rgb(205, 214, 222)');
  });

  test('renders Roslyn fenced hover content without exposing Markdown fences', async ({ page }) => {
    await openWorkbench(page, { waitForLsp: true });
    await switchEditor(page, 'codemirror');
    await waitForLanguageServiceReady(page);
    await replaceSource(page, semanticSource);

    const writeLine = editorHost(page, 'codemirror')
      .locator('.cm-semantic-method')
      .filter({ hasText: /^WriteLine$/ })
      .first();
    await expect(writeLine).toBeVisible({ timeout: 30_000 });
    await writeLine.hover();

    const hover = editorHost(page, 'codemirror').locator('.cm-lsp-hover');
    await expect(hover).toBeVisible();
    await expect(hover.locator('.cm-lsp-hover-code')).toContainText('WriteLine');
    await expect(hover).not.toContainText('```');
  });

  test('keeps the previous semantic decorations mounted while the next token set is pending', async ({ page }) => {
    await openWorkbench(page, { waitForLsp: true });
    await switchEditor(page, 'codemirror');
    await waitForLanguageServiceReady(page);
    await replaceSource(page, semanticSource);

    const host = editorHost(page, 'codemirror');
    const semanticType = host
      .locator('.cm-semantic-type')
      .filter({ hasText: /^SemanticWidget$/ })
      .first();
    await expect(semanticType).toBeVisible({ timeout: 30_000 });

    await sourceEditor(page).click();
    await page.keyboard.press('ControlOrMeta+End');
    await page.keyboard.insertText(' ');

    expect(await host.locator('.cm-semantic-type').filter({ hasText: /^SemanticWidget$/ }).count()).toBeGreaterThan(0);
    await expect(semanticType).toBeVisible();
  });
});
