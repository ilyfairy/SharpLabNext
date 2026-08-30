import { expect, type Locator, type Page, type TestInfo, test } from '@playwright/test';
import { replaceSource, waitForLanguageServiceReady } from './helpers/workbench';

async function openWorkbench(page: Page) {
  await page.goto('/');
  await expect(page.getByLabel('Language')).toBeEnabled();
}

async function waitForCompletedOperation(page: Page) {
  await expect(page.locator('.operation-state')).toHaveText('completed', { timeout: 90_000 });
  await expect(page.locator('.result-error')).toHaveCount(0);
}

async function assertNoDocumentOverflow(page: Page) {
  const dimensions = await page.evaluate(() => ({
    clientWidth: document.documentElement.clientWidth,
    clientHeight: document.documentElement.clientHeight,
    scrollWidth: document.documentElement.scrollWidth,
    scrollHeight: document.documentElement.scrollHeight,
    bodyScrollWidth: document.body.scrollWidth,
    bodyScrollHeight: document.body.scrollHeight,
  }))
  expect(dimensions.scrollWidth).toBeLessThanOrEqual(dimensions.clientWidth);
  expect(dimensions.bodyScrollWidth).toBeLessThanOrEqual(dimensions.clientWidth);
  expect(dimensions.scrollHeight).toBeLessThanOrEqual(dimensions.clientHeight);
  expect(dimensions.bodyScrollHeight).toBeLessThanOrEqual(dimensions.clientHeight);
}

async function assertSelectorControlsFitViewport(page: Page) {
  const layout = await page.evaluate(() => {
    const controls = [...document.querySelectorAll<HTMLElement>('.selector-bar .select-field, .selector-bar .mode-field, .selector-bar .run-button')].filter((element) => element.getClientRects().length > 0)
    const groups = [...document.querySelectorAll<HTMLElement>('.selector-bar .selector-group')]

    return {
      outOfBounds: controls
        .map((element) => ({
          label: element.querySelector('span, legend')?.textContent?.trim() ?? element.getAttribute('aria-label') ?? element.className,
          left: element.getBoundingClientRect().left,
          right: element.getBoundingClientRect().right,
        }))
        .filter(({ left, right }) => left < -0.5 || right > window.innerWidth + 0.5),
      overflowingGroups: groups
        .map((group) => ({
          className: group.className,
          clientWidth: group.clientWidth,
          scrollWidth: group.scrollWidth,
        }))
        .filter(({ clientWidth, scrollWidth }) => scrollWidth > clientWidth + 1),
    }
  });

  expect(layout.outOfBounds).toEqual([])
  expect(layout.overflowingGroups).toEqual([])
}

async function assertSelectAllIsConfinedTo(page: Page, outputDocument: Locator, expectedText: string) {
  await outputDocument.click()
  await expect(outputDocument).toBeFocused()
  await page.keyboard.press('ControlOrMeta+A')

  const selection = await outputDocument.evaluate((element) => {
    const current = window.getSelection()
    if (!current || current.rangeCount === 0) {
      return { text: '', rangeCount: 0, confined: false }
    }
    const ranges = Array.from({ length: current.rangeCount }, (_, index) => current.getRangeAt(index))
    return {
      text: current.toString(),
      rangeCount: current.rangeCount,
      confined: ranges.every((range) => element.contains(range.startContainer) && element.contains(range.endContainer)),
    }
  });

  expect(selection.rangeCount).toBeGreaterThan(0)
  expect(selection.confined).toBe(true)
  expect(selection.text).toContain(expectedText)
}

async function capture(page: Page, testInfo: TestInfo, name: string) {
  const path = testInfo.outputPath(name)
  await page.screenshot({ path, fullPage: true })
  await testInfo.attach(name, { path, contentType: 'image/png' })
}

test.describe('SharpLabNext workbench', () => {
  test('runs C# on the selected .NET runtime with a live language session', async ({ page, isMobile }, testInfo) => {
    test.skip(isMobile, 'Desktop workbench coverage.')
    await openWorkbench(page)
    await waitForLanguageServiceReady(page)

    await page.getByLabel('Output', { exact: true }).selectOption('run')
    await expect(page.getByLabel('Runtime')).toBeVisible()
    await expect(page.getByLabel('Runtime')).toHaveValue('dotnet-11-preview-linux-x64')
    await page.getByRole('button', { name: 'Run', exact: true }).click()

    await waitForCompletedOperation(page)
    await expect(page.getByRole('tab', { name: 'Output', exact: true })).toHaveAttribute('aria-selected', 'true')
    const programOutput = page.getByRole('region', {
      name: 'Program output',
      exact: true,
    })
    await expect(programOutput).toContainText('Hello from SharpLabNext')
    await assertSelectAllIsConfinedTo(page, programOutput, 'Hello from SharpLabNext')
    await expect(page.locator('.status-bar .run-status')).toContainText('Exit 0')
    await expect(page.locator('.terminal-view .run-status')).toHaveCount(0)
    await expect(page.getByLabel('Toolchain')).toHaveValue('roslyn-main')
    await expect(page.getByLabel('Reference set')).toHaveValue('net11-preview-ref')
    await assertNoDocumentOverflow(page)
    await capture(page, testInfo, 'desktop-csharp-run.png')
  });

  test('filters the F# toolchain and restores a v3 URL before rendering IL', async ({ page, isMobile }, testInfo) => {
    test.skip(isMobile, 'Desktop workbench coverage.')
    await openWorkbench(page)
    await page.getByLabel('Language').selectOption('fsharp')
    await expect(page.getByLabel('Toolchain')).toHaveValue('fsharp-stable')
    await expect(page.getByLabel('Toolchain').locator('option')).toHaveCount(1)
    await page.getByLabel('Output', { exact: true }).selectOption('il')

    await expect.poll(() => new URL(page.url()).hash.startsWith('#v3:')).toBe(true)
    const sharedUrl = page.url()
    await page.goto(sharedUrl)
    await expect(page.getByLabel('Language')).toBeEnabled()
    await expect(page.getByLabel('Language')).toHaveValue('fsharp')
    await expect(page.getByLabel('Output', { exact: true })).toHaveValue('il')

    await page.getByRole('button', { name: 'Render IL', exact: true }).click()
    await waitForCompletedOperation(page)
    await expect(page.getByRole('tab', { name: 'IL', exact: true })).toHaveAttribute('aria-selected', 'true')
    const ilDocument = page.getByRole('textbox', {
      name: 'Intermediate language',
      exact: true,
    })
    await expect(ilDocument).toContainText('.method')
    await expect(ilDocument).not.toContainText('Microsoft.FSharp.Collections.ArrayModule')
    await assertSelectAllIsConfinedTo(page, ilDocument, '.method')
    await capture(page, testInfo, 'desktop-fsharp-il.png')
  });

  test('decompiles IL without requiring an entry point outside Run', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Desktop IL artifact-pipeline coverage.')
    await openWorkbench(page)
    await page.getByLabel('Language').selectOption('il')
    await waitForLanguageServiceReady(page)
    await replaceSource(
      page,
      `.assembly SharpLabNext.User {}
.class public auto ansi Program extends [System.Runtime]System.Object
{
  .method public static void Method(string arg) cil managed
  {
    ldarg arg
    ldnull
    beq done

    ldstr "not null"
    call void [System.Console]System.Console::WriteLine(string)

  done: ret
  }
}`,
    )
    await page.getByLabel('Output', { exact: true }).selectOption('decompiled-csharp')
    await page.getByRole('button', { name: 'Decompile', exact: true }).click()

    await waitForCompletedOperation(page)
    await expect(page.getByRole('tab', { name: 'Decompiled C#', exact: true })).toHaveAttribute('aria-selected', 'true')
    const resultPanel = page.getByRole('tabpanel')
    await expect(resultPanel.locator('.monaco-editor .view-lines')).toContainText('Method')
    await expect(resultPanel).not.toContainText('No entry point found')
  })

  test('selects the ConstGenerics toolchain and renders its extended AST', async ({ page, isMobile }, testInfo) => {
    test.skip(isMobile, 'Desktop ConstGenerics coverage.')
    await openWorkbench(page)

    await page.getByLabel('Toolchain').selectOption('roslyn-const-generics')
    await expect(page.getByLabel('Reference set')).toHaveValue('const-generics-ref')
    await waitForLanguageServiceReady(page)

    const source = `using System;

public static class FixedValue<int Value>
{
    public static int GetValue() => Value;
}

public static class Program
{
    public static void Main() => Console.WriteLine(FixedValue<42>.GetValue());
}
`
    const editor = page.locator('.monaco-host')
    await editor.click({ position: { x: 320, y: 180 } })
    await page.keyboard.press('ControlOrMeta+A')
    await page.keyboard.insertText(source)
    await page.getByLabel('Output', { exact: true }).selectOption('ast')
    await page.getByRole('button', { name: 'Build AST', exact: true }).click()

    await waitForCompletedOperation(page)
    await expect(page.getByRole('tab', { name: 'AST', exact: true })).toHaveAttribute('aria-selected', 'true')
    for (let attempt = 0; attempt < 500; attempt++) {
      const collapsed = page.locator('.ast-tree-toggle[aria-expanded="false"]')
      if ((await collapsed.count()) === 0) break
      await collapsed.first().click()
    }
    await expect(page.getByLabel('Abstract syntax tree')).toContainText('LiteralTypeArgument')
    await expect(page.locator('.ast-toolbar')).toContainText('roslyn-const-generics')
    await assertNoDocumentOverflow(page)
    await capture(page, testInfo, 'desktop-const-generics-ast.png')
  })

  test('links execution flow to source and clears decorations when the result becomes stale', async ({ page, isMobile }, testInfo) => {
    test.skip(isMobile, 'Desktop execution-flow coverage.')
    await openWorkbench(page)
    await page.getByLabel('Output', { exact: true }).selectOption('execution-flow')
    await page.getByRole('button', { name: 'Run', exact: true }).click()

    await waitForCompletedOperation(page)
    await expect(page.getByRole('tab', { name: 'Flow', exact: true })).toHaveAttribute('aria-selected', 'true')
    const location = page.locator('.runtime-flow-location').first()
    await expect(location).toBeVisible()
    await expect(page.locator('.monaco-editor .execution-flow-range').first()).toBeVisible()
    const countMarker = page.locator('.monaco-editor .execution-flow-count').first()
    await expect(countMarker).toBeVisible()
    await expect.poll(() => countMarker.evaluate((element) => getComputedStyle(element, '::before').content)).not.toBe('none')

    await location.click()
    await expect(page.getByRole('textbox', { name: /Source editor\. Execution flow shows/ })).toBeFocused()

    await page.getByLabel('Output', { exact: true }).selectOption('ast')
    await expect(page.getByRole('status', { name: 'Result stale' })).toBeVisible()
    await expect(page.locator('.monaco-editor .execution-flow-range')).toHaveCount(0)
    await expect(page.locator('.monaco-editor .execution-flow-count')).toHaveCount(0)
    await expect(page.locator('.runtime-flow-location')).toHaveCount(0)
    await assertNoDocumentOverflow(page)
    await capture(page, testInfo, 'desktop-execution-flow-source.png')
  })

  test('reorders F# source files without overflowing the mobile workbench', async ({ page, isMobile }, testInfo) => {
    test.skip(!isMobile, 'Mobile F# source-order coverage.')
    await openWorkbench(page)
    const settings = page.getByRole('button', { name: 'Workbench settings' })
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'true')
    await page.getByLabel('Language').selectOption('fsharp')
    await expect(page.getByLabel('Toolchain')).toHaveValue('fsharp-stable')
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'false')
    const mobileFiles = page.getByRole('button', {
      name: /^Workspace files, current /,
    })
    await mobileFiles.click()
    await expect(mobileFiles).toHaveAttribute('aria-expanded', 'true')
    await page.getByRole('button', { name: 'Add file' }).click()
    await expect(mobileFiles).toHaveAttribute('aria-expanded', 'false')
    await mobileFiles.click()
    await expect(mobileFiles).toHaveAttribute('aria-expanded', 'true')

    const moveEarlier = page.getByRole('button', {
      name: 'Move File2.fs earlier in source order',
    })
    const moveLater = page.getByRole('button', {
      name: 'Move File2.fs later in source order',
    })
    await expect(moveEarlier).toBeInViewport({ ratio: 1 })
    await expect(moveEarlier).toBeEnabled()
    await expect(moveLater).toBeDisabled()

    await moveEarlier.click()

    await expect(moveEarlier).toBeDisabled()
    await expect(moveLater).toBeEnabled()
    const workspaceTabs = page.getByRole('tablist', {
      name: 'Workspace files',
    })
    await expect(workspaceTabs.getByRole('tab').nth(0)).toContainText('File2.fs')
    await expect(workspaceTabs.getByRole('tab').nth(1)).toContainText('Program.fs')
    await expect(workspaceTabs.getByRole('tab', { name: /File2\.fs/ })).toHaveAttribute('aria-selected', 'true')
    await assertNoDocumentOverflow(page)
    await capture(page, testInfo, 'mobile-fsharp-source-order.png')
  })

  test('keeps source and results visible in the mobile vertical split without horizontal overflow', async ({ page, isMobile }, testInfo) => {
    test.skip(!isMobile, 'Mobile workbench coverage.')
    await openWorkbench(page)

    await expect(page.getByLabel('Toolchain')).toHaveValue('roslyn-main')
    await expect(page.getByLabel('Reference set')).toHaveValue('net11-preview-ref')
    await assertSelectorControlsFitViewport(page)
    await page.getByRole('combobox', { name: 'View', exact: true }).selectOption('run')
    const settings = page.getByRole('button', { name: 'Workbench settings' })
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'true')
    await expect(page.getByLabel('Runtime')).toBeVisible()
    await assertSelectorControlsFitViewport(page)
    await capture(page, testInfo, 'mobile-runtime-toolbar.png')
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'false')

    const sourcePane = page.locator('[data-workbench-pane="source"]')
    const resultPane = page.locator('[data-workbench-pane="result"]')
    await expect(sourcePane).toBeVisible()
    await expect(resultPane).toBeVisible()
    const sourceBox = await sourcePane.boundingBox()
    const resultBox = await resultPane.boundingBox()
    expect(sourceBox).not.toBeNull()
    expect(resultBox).not.toBeNull()
    expect(sourceBox?.height ?? 0).toBeGreaterThan(180)
    expect(resultBox?.height ?? 0).toBeGreaterThan(180)
    expect((sourceBox?.y ?? 0) + (sourceBox?.height ?? 0)).toBeLessThanOrEqual((resultBox?.y ?? 0) + 1)
    await assertNoDocumentOverflow(page)
    await capture(page, testInfo, 'mobile-vertical-split.png')
  })
})
