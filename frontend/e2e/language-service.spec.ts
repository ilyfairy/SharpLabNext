import { expect, type Locator, type Page, test } from '@playwright/test'
import { editorHost, switchEditor, waitForLanguageServiceReady } from './helpers/workbench'

async function openCSharpWorkbench(page: Page) {
  await page.goto('/')
  await expect(page.getByLabel('Language')).toBeEnabled()
  await expect(page.getByLabel('Language')).toHaveValue('csharp')
  await waitForLanguageServiceReady(page)
}

async function replaceSource(page: Page, source: string) {
  await page.getByRole('region', { name: 'Source editor' }).click({
    position: { x: 320, y: 180 },
  })
  await page.keyboard.press('ControlOrMeta+A')
  await page.keyboard.insertText(source)
}

async function expectTokenColor(root: Locator, text: string, color: string) {
  await expect
    .poll(() =>
      root
        .locator('span')
        .evaluateAll(
          (elements, expectedText) =>
            elements
              .filter((element) => element.textContent?.includes(expectedText))
              .map((element) => getComputedStyle(element).color),
          text,
        ),
    )
    .toContain(color)
}

test.describe('C# language service', () => {
  test.beforeEach(({ isMobile }) => {
    test.skip(isMobile, 'Desktop Monaco language-service coverage.')
  })

  test('shows Roslyn completion items in Monaco', async ({ page }) => {
    await openCSharpWorkbench(page)
    await replaceSource(page, 'using System;\nConsole.Wri')

    await page.keyboard.press('Control+Space')

    const suggestions = page.locator('.suggest-widget.visible')
    await expect(suggestions).toBeVisible()
    await expect(
      suggestions
        .locator('.monaco-list-row')
        .filter({ hasText: /WriteLine/ })
        .first(),
    ).toBeVisible()
  })

  test('offers and accepts top-level while completion in Monaco', async ({ page }) => {
    await openCSharpWorkbench(page)
    await replaceSource(page, 'whi')

    await page.keyboard.press('Control+Space')

    const suggestions = page.locator('.suggest-widget.visible')
    await expect(suggestions).toBeVisible()
    await expect(suggestions).not.toContainText('No suggestions.')
    await expect(
      suggestions.locator('.monaco-list-row').filter({ hasText: /while/ }).first(),
    ).toBeVisible()

    await page.keyboard.press('Tab')

    await expect(suggestions).toBeHidden()
    await expect(editorHost(page, 'monaco').locator('.view-lines')).toContainText('while')
  })

  test('opens and accepts Roslyn completion in CodeMirror with VS keys', async ({ page }) => {
    await openCSharpWorkbench(page)
    await switchEditor(page, 'codemirror')
    await waitForLanguageServiceReady(page)
    await replaceSource(page, 'using System;\nConsole.WriteL')

    await page.keyboard.press('Control+Space')

    const suggestions = page.locator('.cm-tooltip-autocomplete')
    await expect(suggestions).toBeVisible()
    await expect(
      suggestions
        .getByRole('option')
        .filter({ hasText: /WriteLine/ })
        .first(),
    ).toBeVisible()

    await page.keyboard.press('Tab')

    await expect(suggestions).toBeHidden()
    await expect
      .poll(async () =>
        (await editorHost(page, 'codemirror').locator('.cm-line').allTextContents()).join('\n'),
      )
      .toBe('using System;\nConsole.WriteLine')
  })

  test('shows Roslyn hover information in Monaco', async ({ page }) => {
    await openCSharpWorkbench(page)
    const writeLine = page
      .locator('.view-line')
      .filter({ hasText: 'Console.WriteLine' })
      .locator('span')
      .filter({ hasText: /^WriteLine$/ })
      .last()
    await expect(writeLine).toBeVisible()
    await writeLine.hover()

    const hover = page.locator('.monaco-hover:visible')
    await expect(hover).toBeVisible()
    await expect(hover).toContainText('WriteLine')
  })
})

test.describe('IL language highlighting', () => {
  test.beforeEach(({ isMobile }) => {
    test.skip(isMobile, 'Desktop Monaco and CodeMirror highlighting coverage.')
  })

  test('recovers Monaco suggestions after a complete empty IL result', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByLabel('Language')).toBeEnabled()
    await page.getByLabel('Language').selectOption('il')
    await waitForLanguageServiceReady(page)
    await replaceSource(
      page,
      `.assembly SharpLabNext.User {}
.class public auto ansi Program extends int32
{
  .method public static void Main() cil managed
  {
    .maxstack 1`,
    )

    await page.keyboard.press('Control+Space')
    const suggestions = page.locator('.suggest-widget.visible')
    await expect(suggestions).toContainText('No suggestions.')

    await page.keyboard.press('Enter')
    await expect(
      suggestions.locator('.monaco-list-row').filter({ hasText: /^add/ }).first(),
    ).toBeVisible()
  })

  test('keeps IL readable in both editors while the language service is unavailable', async ({
    page,
  }) => {
    await page.route('**/api/v1/language-sessions', async (route) => {
      if (route.request().method() !== 'POST') {
        await route.continue()
        return
      }
      await route.fulfill({
        status: 503,
        contentType: 'application/problem+json',
        body: JSON.stringify({
          type: 'about:blank',
          title: 'Language service unavailable for lexical fallback coverage.',
          status: 503,
        }),
      })
    })

    await page.goto('/')
    await expect(page.getByLabel('Language')).toBeEnabled()
    await page.getByLabel('Language').selectOption('il')
    await replaceSource(
      page,
      `.assembly Demo {}
.class public auto ansi Demo.Program extends [System.Runtime]System.Object
{
  .method public static void Main() cil managed
  {
    .entrypoint
    IL_0000: ldstr "Hello"
    call void [System.Console]System.Console::WriteLine(string)
    beq.s IL_0001
    conv.ovf.i4.un
    ldelem.ref
    tail. call void Demo.Program::Main()
    IL_0001: ret
  }
}
`,
    )

    const monaco = editorHost(page, 'monaco')
    await expect(monaco.locator('.view-lines')).toContainText('IL_0000')
    const monacoLines = monaco.locator('.view-lines')
    await expectTokenColor(monacoLines, '.assembly', 'rgb(175, 0, 219)')
    await expectTokenColor(monacoLines, 'IL_0000', 'rgb(175, 0, 219)')
    await expectTokenColor(monacoLines, 'ldstr', 'rgb(0, 0, 255)')
    await expectTokenColor(monacoLines, 'beq.s', 'rgb(0, 0, 255)')
    await expectTokenColor(monacoLines, 'conv.ovf.i4.un', 'rgb(0, 0, 255)')
    await expectTokenColor(monacoLines, 'ldelem.ref', 'rgb(0, 0, 255)')
    await expectTokenColor(monacoLines, 'tail.', 'rgb(0, 0, 255)')
    await expectTokenColor(monacoLines, 'Hello', 'rgb(163, 21, 21)')
    await expectTokenColor(monacoLines, 'System.Console', 'rgb(43, 145, 175)')

    await switchEditor(page, 'codemirror')
    const codeMirror = editorHost(page, 'codemirror')
    const codeMirrorContent = codeMirror.locator('.cm-content')
    await expect(codeMirrorContent).toContainText('IL_0000')
    await expectTokenColor(codeMirrorContent, '.assembly', 'rgb(175, 0, 219)')
    await expectTokenColor(codeMirrorContent, 'IL_0000', 'rgb(175, 0, 219)')
    await expectTokenColor(codeMirrorContent, 'ldstr', 'rgb(0, 0, 255)')
    await expectTokenColor(codeMirrorContent, 'beq.s', 'rgb(0, 0, 255)')
    await expectTokenColor(codeMirrorContent, 'conv.ovf.i4.un', 'rgb(0, 0, 255)')
    await expectTokenColor(codeMirrorContent, 'ldelem.ref', 'rgb(0, 0, 255)')
    await expectTokenColor(codeMirrorContent, 'tail.', 'rgb(0, 0, 255)')
    await expectTokenColor(codeMirrorContent, 'Hello', 'rgb(163, 21, 21)')
    await expectTokenColor(codeMirrorContent, 'System', 'rgb(43, 145, 175)')
  })
})
