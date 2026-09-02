import { expect, type Locator, type Page, test } from '@playwright/test';
import { decodeV3 } from '../src/share';
import {
  editorHost,
  editorSwitch,
  expectActiveEditor,
  expectCodeDocumentHasNoVerticalOverflow,
  expectCodeDocumentHorizontalScrollAtPanelBottom,
  expectEditorSwitchFits,
  expectHorizontalSplit,
  expectInsideViewport,
  expectNoDocumentOverflow,
  expectResultContentFillsPane,
  expectVerticalSplit,
  moveCursorToLine,
  observeOperationWebSocket,
  openWorkbench,
  replaceSource,
  sourceEditor,
  switchEditor,
  visibleBox,
  waitForCompletedOperation,
  waitForLanguageServiceReady,
  workbenchPane,
} from './helpers/workbench'

const editorPreferenceStorageKey = 'sharplabnext.editor'
const editorFontSizeStorageKey = 'sharplabnext.editor-font-size'

const switchSource = `using System;

public static class SwitchPreserved
{
    public static int Value() => 42;
}
`

const jitSource = `using System;
using System.Runtime.CompilerServices;

public static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Other() => 41;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CurrentTarget() => Other() + 1;

    public static void Main() => Console.WriteLine(CurrentTarget());
}
`

const semanticSource = `public sealed class SemanticWidget
{
    public int CalculateValue(int input)
    {
        string escaped = "line\\n";
        int localValue = input + 1;
        return localValue;
    }
}
`

const cppCliHighlightSource = `#include <vector>

using namespace System;

int main(array<String^>^ args)
{
    const char* raw = R"tag(raw body)tag";
    const wchar_t* wide = L"wide text";
    Object^ value = gcnew Object();
    Console::WriteLine("Hello from C++/CLI");
    return 0;
}
`

function expectStableRect(before: { x: number; y: number; width: number; height: number }, after: { x: number; y: number; width: number; height: number }, label: string) {
  for (const key of ['x', 'y', 'width', 'height'] as const) {
    expect(Math.abs(after[key] - before[key]), `${label} ${key} changed`).toBeLessThanOrEqual(1)
  }
}

interface TopToolbarSample {
  run: { x: number; y: number; width: number; height: number; right: number }
  actions: { x: number; y: number; width: number; height: number }
  sourceSelectorClientWidth: number
  sourceSelectorScrollWidth: number
  selectorClientWidth: number
  selectorScrollWidth: number
  documentClientWidth: number
  documentScrollWidth: number
  bodyScrollWidth: number
  resolvingVisible: boolean
}

async function startTopToolbarProbe(page: Page, isMobile: boolean) {
  await page.evaluate((mobile) => {
    const key = '__sharplabnextTopToolbarProbe'
    const samples: TopToolbarSample[] = []
    const runSelector = mobile ? '.mobile-command-bar .run-button' : '.selector-group--result > .run-button'
    const record = () => {
      const run = document.querySelector<HTMLElement>(runSelector)
      const actions = document.querySelector<HTMLElement>('.app-bar-actions')
      const sourceSelector = document.querySelector<HTMLElement>('.selector-group--source')
      const selector = document.querySelector<HTMLElement>('.selector-group--result')
      if (!run || !actions || !sourceSelector || !selector || run.getClientRects().length === 0) return
      const runRect = run.getBoundingClientRect()
      const actionsRect = actions.getBoundingClientRect()
      samples.push({
        run: {
          x: runRect.x,
          y: runRect.y,
          width: runRect.width,
          height: runRect.height,
          right: runRect.right,
        },
        actions: {
          x: actionsRect.x,
          y: actionsRect.y,
          width: actionsRect.width,
          height: actionsRect.height,
        },
        sourceSelectorClientWidth: sourceSelector.clientWidth,
        sourceSelectorScrollWidth: sourceSelector.scrollWidth,
        selectorClientWidth: selector.clientWidth,
        selectorScrollWidth: selector.scrollWidth,
        documentClientWidth: document.documentElement.clientWidth,
        documentScrollWidth: document.documentElement.scrollWidth,
        bodyScrollWidth: document.body.scrollWidth,
        resolvingVisible: document.querySelector('.app-health[aria-label="Resolving"]') !== null,
      })
    }
    const observer = new MutationObserver(record)
    const actions = document.querySelector('.app-bar-actions')
    if (!actions) throw new Error('App-bar actions are missing.')
    observer.observe(actions, { childList: true, subtree: true })
    const timer = window.setInterval(record, 5)
    record()
    Reflect.set(window, key, { samples, observer, timer })
  }, isMobile)
}

async function stopTopToolbarProbe(page: Page): Promise<TopToolbarSample[]> {
  return page.evaluate(() => {
    const key = '__sharplabnextTopToolbarProbe'
    const probe = Reflect.get(window, key) as
      | {
          samples: TopToolbarSample[]
          observer: MutationObserver
          timer: number
        }
      | undefined
    if (!probe) throw new Error('Top-toolbar probe was not started.')
    probe.observer.disconnect()
    window.clearInterval(probe.timer)
    Reflect.deleteProperty(window, key)
    return probe.samples
  })
}

function expectStableTopToolbar(samples: TopToolbarSample[]) {
  expect(samples.length).toBeGreaterThan(2)
  const initial = samples[0]
  if (!initial) throw new Error('Top-toolbar probe produced no initial sample.')
  for (const sample of samples) {
    expect(sample.run.right, 'Run action overlaps the app-bar actions').toBeLessThanOrEqual(sample.actions.x + 0.5)
    expect(sample.sourceSelectorScrollWidth, 'Source selector overflowed horizontally').toBeLessThanOrEqual(sample.sourceSelectorClientWidth + 1)
    expect(sample.selectorScrollWidth, 'Result selector overflowed horizontally').toBeLessThanOrEqual(sample.selectorClientWidth + 1)
    expect(sample.documentScrollWidth, 'Document overflowed horizontally').toBeLessThanOrEqual(sample.documentClientWidth)
    expect(sample.bodyScrollWidth, 'Body overflowed horizontally').toBeLessThanOrEqual(sample.documentClientWidth)
    expect(sample.resolvingVisible, 'Routine selection resolution must stay silent').toBe(false)
    expectStableRect(initial.run, sample.run, 'Run action during live resolution')
    expectStableRect(initial.actions, sample.actions, 'App actions during live resolution')
  }
}

test.describe('workbench experience contract', () => {
  test('keeps live result geometry stable and selects diagnostics without discarding the last output', async ({ page, isMobile }) => {
    await openWorkbench(page)
    await waitForCompletedOperation(page)
    const stableOutput = page.getByRole('textbox', {
      name: 'Decompiled C sharp',
    })
    await expect(stableOutput).toBeVisible()
    const stableText = await stableOutput.textContent()
    expect(stableText?.trim().length ?? 0).toBeGreaterThan(0)

    const sourceBefore = await visibleBox(workbenchPane(page, 'source'), 'source before live edit')
    const resultBefore = await visibleBox(workbenchPane(page, 'result'), 'result before live edit')
    const toolbarBefore = await visibleBox(page.locator('.result-tabs-toolbar'), 'result toolbar before live edit')
    const coreTabs = page.getByRole('tablist', { name: 'Result views' }).getByRole('tab')
    await expect(coreTabs).toHaveCount(2)
    await expect(coreTabs.nth(0)).toContainText('Diagnostics')
    await expect(coreTabs.nth(1)).toHaveText('Decompiled C#')
    const tabRectsBefore = await coreTabs.evaluateAll((tabs) =>
      tabs.map((tab) => {
        const rect = tab.getBoundingClientRect()
        return { x: rect.x, y: rect.y, width: rect.width, height: rect.height }
      }),
    )
    await startTopToolbarProbe(page, isMobile)

    await replaceSource(page, 'public static class Broken { this is not valid C# }')
    await expect(page.getByRole('status', { name: 'Result stale' })).toBeVisible()
    await expect(stableOutput).toHaveText(stableText ?? '')
    const sourceStale = await visibleBox(workbenchPane(page, 'source'), 'source after live edit')
    const resultStale = await visibleBox(workbenchPane(page, 'result'), 'result after live edit')
    const toolbarStale = await visibleBox(page.locator('.result-tabs-toolbar'), 'result toolbar after live edit')
    expectStableRect(sourceBefore, sourceStale, 'source pane after edit')
    expectStableRect(resultBefore, resultStale, 'result pane after edit')
    expectStableRect(toolbarBefore, toolbarStale, 'result toolbar after edit')

    await expect(page.locator('.result-state-spinner')).toBeVisible()
    const sourcePending = await visibleBox(workbenchPane(page, 'source'), 'source while pending')
    const resultPending = await visibleBox(workbenchPane(page, 'result'), 'result while pending')
    const toolbarPending = await visibleBox(page.locator('.result-tabs-toolbar'), 'result toolbar while pending')
    expectStableRect(sourceBefore, sourcePending, 'source pane while pending')
    expectStableRect(resultBefore, resultPending, 'result pane while pending')
    expectStableRect(toolbarBefore, toolbarPending, 'result toolbar while pending')

    const diagnosticsTab = page.getByRole('tab', { name: /^Diagnostics/ })
    await expect(diagnosticsTab).toHaveAttribute('aria-selected', 'true', {
      timeout: 90_000,
    })
    await expect(page.getByRole('tabpanel').getByRole('alert')).toContainText(/Compilation failed|not produced/)
    await expect(coreTabs).toHaveCount(2)
    await expect(coreTabs.nth(1)).toHaveText('Decompiled C#')
    const tabRectsFailed = await coreTabs.evaluateAll((tabs) =>
      tabs.map((tab) => {
        const rect = tab.getBoundingClientRect()
        return { x: rect.x, y: rect.y, width: rect.width, height: rect.height }
      }),
    )
    expect(tabRectsFailed).toHaveLength(tabRectsBefore.length)
    for (let index = 0; index < tabRectsBefore.length; index += 1) {
      const before = tabRectsBefore[index]
      const after = tabRectsFailed[index]
      if (!before || !after) throw new Error('A stable result tab rect is missing.')
      expectStableRect(before, after, `result tab ${index}`)
    }

    const sourceFailed = await visibleBox(workbenchPane(page, 'source'), 'source after failure')
    const resultFailed = await visibleBox(workbenchPane(page, 'result'), 'result after failure')
    const toolbarFailed = await visibleBox(page.locator('.result-tabs-toolbar'), 'result toolbar after failure')
    expectStableRect(sourceBefore, sourceFailed, 'source pane after failure')
    expectStableRect(resultBefore, resultFailed, 'result pane after failure')
    expectStableRect(toolbarBefore, toolbarFailed, 'result toolbar after failure')
    expectStableTopToolbar(await stopTopToolbarProbe(page))
    await expectNoDocumentOverflow(page)

    await replaceSource(page, 'using System;\nConsole.WriteLine(Repaired.Value());\npublic static class Repaired { public static int Value() => 42; }')
    await expect(page.locator('.result-state-spinner')).toBeVisible()
    await expect(diagnosticsTab).toHaveAttribute('aria-selected', 'true')
    await expect(page.getByRole('tab', { name: 'Decompiled C#' })).toHaveAttribute('aria-selected', 'true', { timeout: 90_000 })
    await expect(stableOutput).toContainText('Repaired')
  })

  test('desktop defaults to Monaco and preserves source and the local-only editor choice', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Desktop layout coverage.')
    await openWorkbench(page)

    expect(page.viewportSize()).toEqual({ width: 1440, height: 900 })
    await expectActiveEditor(page, 'monaco')

    const sourceBox = await visibleBox(workbenchPane(page, 'source'), 'desktop source pane')
    const resultBox = await visibleBox(workbenchPane(page, 'result'), 'desktop result pane')
    expect(sourceBox.width).toBeGreaterThan(500)
    expect(resultBox.width).toBeGreaterThan(500)
    expectHorizontalSplit(sourceBox, resultBox)
    await expectInsideViewport(page, sourceBox, 'desktop source pane')
    await expectInsideViewport(page, resultBox, 'desktop result pane')
    await expectEditorSwitchFits(page)

    await expect(page.getByLabel('Output', { exact: true })).toHaveValue('decompiled-csharp')
    await expect(page.locator('.status-bar .run-status')).toHaveCount(0)
    const idleStatusLayout = await page.locator('.status-result-bar').evaluate((resultBar) => {
      const settings = resultBar.querySelector('.status-editor-settings')
      const resultPane = document.querySelector('[data-workbench-pane="result"]')
      if (!(settings instanceof HTMLElement) || !(resultPane instanceof HTMLElement)) {
        throw new Error('Desktop status controls or result pane did not render.')
      }
      const resultBarRect = resultBar.getBoundingClientRect()
      const settingsRect = settings.getBoundingClientRect()
      const resultPaneRect = resultPane.getBoundingClientRect()
      return {
        resultBarLeft: resultBarRect.left,
        resultBarRight: resultBarRect.right,
        resultPaneLeft: resultPaneRect.left,
        resultPaneRight: resultPaneRect.right,
        settingsLeft: settingsRect.left,
        settingsRight: settingsRect.right,
      }
    })
    expect(Math.abs(idleStatusLayout.resultBarLeft - idleStatusLayout.resultPaneLeft)).toBeLessThanOrEqual(1)
    expect(Math.abs(idleStatusLayout.resultBarRight - idleStatusLayout.resultPaneRight)).toBeLessThanOrEqual(1)
    expect(idleStatusLayout.settingsLeft).toBeGreaterThan((idleStatusLayout.resultPaneLeft + idleStatusLayout.resultPaneRight) / 2)
    expect(idleStatusLayout.resultPaneRight - idleStatusLayout.settingsRight).toBeGreaterThanOrEqual(8)
    expect(idleStatusLayout.resultPaneRight - idleStatusLayout.settingsRight).toBeLessThanOrEqual(10)

    await page.getByLabel('Output', { exact: true }).selectOption('run')
    await expect(page.getByLabel('Runtime')).toBeVisible()
    await replaceSource(page, switchSource)
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')
    await expect.poll(() => new URL(page.url()).hash).toMatch(/^#v3:/)
    await page.waitForTimeout(800)
    const shareHash = new URL(page.url()).hash

    await switchEditor(page, 'codemirror')
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')
    const codeMirrorSourceBox = await visibleBox(workbenchPane(page, 'source'), 'desktop CodeMirror source pane')
    const codeMirrorResultBox = await visibleBox(workbenchPane(page, 'result'), 'desktop CodeMirror result pane')
    expectHorizontalSplit(codeMirrorSourceBox, codeMirrorResultBox)
    expect(Math.abs(codeMirrorSourceBox.width - sourceBox.width)).toBeLessThanOrEqual(2)
    expect(Math.abs(codeMirrorResultBox.width - resultBox.width)).toBeLessThanOrEqual(2)
    await expect.poll(() => page.evaluate((key) => localStorage.getItem(key), editorPreferenceStorageKey)).toBe('codemirror')
    await page.waitForTimeout(800)
    expect(new URL(page.url()).hash).toBe(shareHash)

    await switchEditor(page, 'monaco')
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')
    await page.waitForTimeout(400)
    expect(new URL(page.url()).hash).toBe(shareHash)
    await expectEditorSwitchFits(page)

    await switchEditor(page, 'codemirror')
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')

    await page.reload()
    await expect(page.getByLabel('Language')).toBeEnabled()
    await expectActiveEditor(page, 'codemirror')
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')
    expect(new URL(page.url()).hash).toBe(shareHash)
    await expectNoDocumentOverflow(page)
  })

  test('keeps a short desktop viewport and short stdout free of false overflow', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Compact-height desktop coverage.')
    await page.setViewportSize({ width: 1106, height: 498 })
    await openWorkbench(page)

    const workbench = await visibleBox(page.locator('.workbench'), 'compact-height workbench')
    const sourcePane = await visibleBox(workbenchPane(page, 'source'), 'compact-height source pane')
    const resultPane = await visibleBox(workbenchPane(page, 'result'), 'compact-height result pane')
    await expectInsideViewport(page, workbench, 'compact-height workbench')
    await expectInsideViewport(page, sourcePane, 'compact-height source pane')
    await expectInsideViewport(page, resultPane, 'compact-height result pane')
    expectHorizontalSplit(sourcePane, resultPane)

    await page.getByLabel('Output', { exact: true }).selectOption('run')
    await replaceSource(page, 'using System;\nConsole.Write("\\u001b[1;4;38;2;18;52;86mshort output <script>safe</script>\\u001b[0m");\nConsole.Write("\\u001b[2Jcursor");\n')
    await page.getByRole('button', { name: 'Run', exact: true }).click()
    await waitForCompletedOperation(page)
    await expectResultContentFillsPane(page)

    const stdout = page.locator('.terminal-view .result-document')
    await expect(stdout).toContainText('short output')
    const styledOutput = stdout.locator('.ansi-segment--bold.ansi-segment--underline', {
      hasText: 'short output',
    })
    await expect(styledOutput).toHaveCSS('color', 'rgb(18, 52, 86)')
    await expect(stdout.locator('script')).toHaveCount(0)
    await expect(stdout).toContainText('<script>safe</script>')
    expect(await stdout.textContent()).toContain('\u241b[2Jcursor')
    const terminalMetrics = await page.locator('.terminal-view').evaluate((terminal) => {
      const output = terminal.querySelector('.result-document')
      if (!(output instanceof HTMLElement)) {
        throw new Error('Run output did not render.')
      }
      const terminalRect = terminal.getBoundingClientRect()
      const outputRect = output.getBoundingClientRect()
      return {
        outputStartsAtTop: Math.abs(outputRect.top - terminalRect.top),
        outputEndsAtBottom: Math.abs(outputRect.bottom - terminalRect.bottom),
        nestedStatusCount: terminal.querySelectorAll('.run-status').length,
      }
    })
    expect(terminalMetrics.outputStartsAtTop).toBeLessThanOrEqual(1)
    expect(terminalMetrics.outputEndsAtBottom).toBeLessThanOrEqual(1)
    expect(terminalMetrics.nestedStatusCount).toBe(0)
    const statusMetrics = await page.locator('.status-bar .run-status').evaluate((status) => {
      const statusRect = status.getBoundingClientRect()
      const resultBar = status.parentElement
      const resultPane = document.querySelector('[data-workbench-pane="result"]')
      const metrics = status.querySelector('.run-status-metrics')
      const settings = resultBar?.querySelector('.status-editor-settings')
      const barRect = resultBar?.getBoundingClientRect()
      const resultRect = resultPane?.getBoundingClientRect()
      const metricsRect = metrics?.getBoundingClientRect()
      const settingsRect = settings?.getBoundingClientRect()
      return {
        text: status.textContent ?? '',
        height: statusRect.height,
        insideBar: barRect !== undefined && statusRect.top >= barRect.top - 1 && statusRect.bottom <= barRect.bottom + 1,
        resultBarLeft: barRect?.left ?? -1,
        resultBarRight: barRect?.right ?? -1,
        resultPaneLeft: resultRect?.left ?? -1,
        resultPaneRight: resultRect?.right ?? -1,
        metricsLeft: metricsRect?.left ?? -1,
        metricsRight: metricsRect?.right ?? -1,
        settingsLeft: settingsRect?.left ?? -1,
        settingsRight: settingsRect?.right ?? -1,
      }
    })
    expect(statusMetrics.text).toContain('Exit 0')
    expect(statusMetrics.text).not.toContain('completed')
    expect(statusMetrics.height).toBeLessThanOrEqual(26)
    expect(statusMetrics.insideBar).toBe(true)
    expect(Math.abs(statusMetrics.resultBarLeft - statusMetrics.resultPaneLeft)).toBeLessThanOrEqual(1)
    expect(Math.abs(statusMetrics.resultBarRight - statusMetrics.resultPaneRight)).toBeLessThanOrEqual(1)
    expect(statusMetrics.metricsLeft - statusMetrics.resultPaneLeft).toBeGreaterThanOrEqual(8)
    expect(statusMetrics.metricsLeft - statusMetrics.resultPaneLeft).toBeLessThanOrEqual(10)
    expect(statusMetrics.settingsLeft - statusMetrics.metricsRight).toBeGreaterThanOrEqual(12)
    expect(statusMetrics.resultPaneRight - statusMetrics.settingsRight).toBeGreaterThanOrEqual(8)
    const overflow = await stdout.evaluate((element) => ({
      clientWidth: element.clientWidth,
      clientHeight: element.clientHeight,
      scrollWidth: element.scrollWidth,
      scrollHeight: element.scrollHeight,
    }))
    expect(overflow.scrollWidth).toBeLessThanOrEqual(overflow.clientWidth + 1)
    expect(overflow.scrollHeight).toBeLessThanOrEqual(overflow.clientHeight + 1)
    await expectNoDocumentOverflow(page)
  })

  test('fills a short desktop result pane with unwrapped Decompiled C#', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Compact-height desktop result coverage.')
    await page.setViewportSize({ width: 1106, height: 498 })
    await openWorkbench(page)
    await waitForCompletedOperation(page)

    await replaceSource(
      page,
      `public static class Program
{
    public static int Main() => "${'x'.repeat(320)}".Length;
}
`,
    )
    await page.getByRole('button', { name: 'Decompile', exact: true }).click()
    await waitForCompletedOperation(page)

    await expectResultContentFillsPane(page)
    await expectCodeDocumentHorizontalScrollAtPanelBottom(page, 'Decompiled C sharp')
    await expectCodeDocumentHasNoVerticalOverflow(page, 'Decompiled C sharp')
    await expectNoDocumentOverflow(page)
  })

  test('language switches keep independent browser-local workspaces and correct default files', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Desktop language workspace coverage.')
    await openWorkbench(page)
    await replaceSource(page, 'public static class CachedCSharp {}')

    await page.getByLabel('Language').selectOption('php')
    await expect(page.getByRole('tab', { name: /index\.php/ })).toBeVisible()
    await expect(sourceEditor(page)).toContainText('function square')
    await expect(sourceEditor(page)).not.toContainText('CachedCSharp')
    await replaceSource(page, '<?php echo "Cached PHP", PHP_EOL;\n')

    await page.getByLabel('Language').selectOption('gsharp')
    await expect(page.getByRole('tab', { name: /Program\.gs/ })).toBeVisible()
    await expect(sourceEditor(page)).toContainText('Hello from G#')
    await expect(sourceEditor(page)).not.toContainText('Cached PHP')

    await page.getByLabel('Language').selectOption('csharp')
    await expect(page.getByRole('tab', { name: /Program\.cs/ })).toBeVisible()
    await expect(sourceEditor(page)).toContainText('CachedCSharp')

    await page.getByLabel('Language').selectOption('php')
    await expect(page.getByRole('tab', { name: /index\.php/ })).toBeVisible()
    await expect(sourceEditor(page)).toContainText('Cached PHP')

    await page.evaluate(() => window.history.replaceState(null, '', '/'))
    await page.reload()
    await expect(page.getByLabel('Language')).toBeEnabled()
    await expect(sourceEditor(page)).toContainText('CachedCSharp')
    await expect(page.getByLabel('Toolchain')).toHaveValue('roslyn-main')
    await expect(page.getByLabel('Reference set')).toHaveValue('net11-preview-ref')

    await page.getByLabel('Language').selectOption('php')
    await expect(sourceEditor(page)).toContainText('Cached PHP')
  })

  test('keeps routine health and identity metadata out of the workbench chrome', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Desktop chrome coverage.')
    await openWorkbench(page)

    await expect(page.locator('.brand')).not.toContainText('SharpLabNext')
    await expect(page.locator('.app-health')).toHaveCount(0)
    await expect(page.locator('.select-field > span:not(.visually-hidden)')).toHaveCount(0)
    await expect(page.locator('.identity-strip')).toBeHidden()
    await expect(page.locator('.status-bar')).not.toContainText(/Workspace r|Selection r|Catalog|LSP/)
    await expect(page.getByRole('toolbar', { name: 'Editor' })).toBeVisible()
  })

  test('editor selection stays browser-local across reloads and pages without changing shared state', async ({ page, context, isMobile }) => {
    await openWorkbench(page)
    await replaceSource(page, switchSource)
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')
    await expect.poll(() => new URL(page.url()).hash).toMatch(/^#v3:/)
    await page.waitForTimeout(800)

    const shareUrl = page.url()
    const preferredEditor = isMobile ? 'monaco' : 'codemirror'
    await switchEditor(page, preferredEditor)
    await expect.poll(() => page.evaluate((key) => localStorage.getItem(key), editorPreferenceStorageKey)).toBe(preferredEditor)
    await page.waitForTimeout(800)
    expect(page.url()).toBe(shareUrl)

    await page.reload()
    await expect(page.getByLabel('Language')).toBeEnabled()
    await expectActiveEditor(page, preferredEditor)
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')
    expect(page.url()).toBe(shareUrl)

    const siblingPage = await context.newPage()
    await siblingPage.goto(shareUrl)
    await expect(siblingPage.getByLabel('Language')).toBeEnabled()
    await expectActiveEditor(siblingPage, preferredEditor)
    await expect(siblingPage).toHaveURL(shareUrl)
    await expect(sourceEditor(siblingPage)).toContainText('SwitchPreserved')
    await expect.poll(() => siblingPage.evaluate((key) => localStorage.getItem(key), editorPreferenceStorageKey)).toBe(preferredEditor)
    await siblingPage.waitForTimeout(800)
    expect(siblingPage.url()).toBe(shareUrl)
    await siblingPage.close()
  })

  test('mobile defaults to CodeMirror and keeps a stable vertical split when switched to Monaco', async ({ page, isMobile }) => {
    test.skip(!isMobile, 'Mobile layout coverage.')
    await openWorkbench(page)
    await waitForCompletedOperation(page)

    expect(page.viewportSize()).toEqual({ width: 412, height: 915 })
    await expectActiveEditor(page, 'codemirror')
    const resultCode = page.getByRole('textbox', {
      name: 'Decompiled C sharp',
    })
    await expect(resultCode).toBeVisible()
    const resultTab = page.getByRole('tab', { name: 'Decompiled C#' })
    const editorSettings = page.getByRole('button', {
      name: 'Editor settings',
    })
    await expect(editorSettings).toBeVisible()
    await expect(editorSettings).toHaveAttribute('aria-expanded', 'false')
    await expect(editorSwitch(page)).toBeHidden()
    const fixedChromeFontSizes = {
      resultTab: await resultTab.evaluate((element) => getComputedStyle(element).fontSize),
      settings: await editorSettings.evaluate((element) => getComputedStyle(element).fontSize),
    }
    const editorSettingsUrl = page.url()
    await editorSettings.click()
    await expect(editorSwitch(page)).toBeVisible()
    await expect(page.getByLabel('Current code font size')).toHaveText('14px')
    await page.getByRole('button', { name: 'Increase code font size' }).click()
    await expect(page.getByLabel('Current code font size')).toHaveText('16px')
    await expect.poll(() => page.evaluate((key) => localStorage.getItem(key), editorFontSizeStorageKey)).toBe('16')
    expect(page.url()).toBe(editorSettingsUrl)
    await expect
      .poll(() =>
        editorHost(page, 'codemirror').evaluate((host) => {
          const content = host.querySelector('.cm-content')
          return content ? getComputedStyle(content).fontSize : null
        }),
      )
      .toBe('16px')
    await expect.poll(() => resultCode.evaluate((code) => getComputedStyle(code).fontSize)).toBe('16px')
    expect(await resultTab.evaluate((element) => getComputedStyle(element).fontSize)).toBe(fixedChromeFontSizes.resultTab)
    expect(await editorSettings.evaluate((element) => getComputedStyle(element).fontSize)).toBe(fixedChromeFontSizes.settings)
    await editorSettings.click()
    await expect(editorSwitch(page)).toBeHidden()
    await replaceSource(page, 'class C { }')

    const mobileFiles = page.getByRole('button', {
      name: /^Workspace files, current /,
    })
    await expect(mobileFiles).toHaveAttribute('aria-expanded', 'false')
    await expect(page.getByRole('tablist', { name: 'Workspace files' })).toBeHidden()
    const collapsedFileLayout = await page.locator('.source-pane').evaluate((sourcePane) => {
      const fileTabs = sourcePane.querySelector('.file-tabs')
      const editor = sourcePane.querySelector('.editor-region')
      if (!(fileTabs instanceof HTMLElement) || !(editor instanceof HTMLElement)) {
        throw new Error('Mobile source file layout is incomplete.')
      }
      const sourceRect = sourcePane.getBoundingClientRect()
      const fileTabsRect = fileTabs.getBoundingClientRect()
      const editorRect = editor.getBoundingClientRect()
      return {
        fileTabsHeight: fileTabsRect.height,
        editorTopOffset: editorRect.top - sourceRect.top,
        sourceHeight: sourceRect.height,
        editorTop: editorRect.top,
      }
    })
    expect(collapsedFileLayout.fileTabsHeight).toBe(0)
    expect(collapsedFileLayout.editorTopOffset).toBeLessThanOrEqual(1)
    await mobileFiles.click()
    await expect(mobileFiles).toHaveAttribute('aria-expanded', 'true')
    await expect(page.getByRole('tablist', { name: 'Workspace files' })).toBeVisible()
    const expandedFileLayout = await page.locator('.source-pane').evaluate((sourcePane) => {
      const editor = sourcePane.querySelector('.editor-region')
      if (!(editor instanceof HTMLElement)) {
        throw new Error('Mobile source editor is missing.')
      }
      const sourceRect = sourcePane.getBoundingClientRect()
      const editorRect = editor.getBoundingClientRect()
      return { sourceHeight: sourceRect.height, editorTop: editorRect.top }
    })
    expect(Math.abs(expandedFileLayout.sourceHeight - collapsedFileLayout.sourceHeight)).toBeLessThanOrEqual(1)
    expect(Math.abs(expandedFileLayout.editorTop - collapsedFileLayout.editorTop)).toBeLessThanOrEqual(1)
    const closeLastFile = page.getByRole('button', {
      name: 'Close Program.cs',
    })
    await expect(closeLastFile).toBeVisible()
    await closeLastFile.click()
    await expect(mobileFiles).toHaveAttribute('aria-expanded', 'false')
    await expect(sourceEditor(page)).toContainText('Hello from SharpLabNext')
    await replaceSource(page, 'class C { }')
    await expect(sourceEditor(page)).toContainText('class C { }')

    const shortCodeMirrorMetrics = await editorHost(page, 'codemirror').evaluate((host) => {
      const scroller = host.querySelector('.cm-scroller')
      const gutter = host.querySelector('.cm-gutters')
      const lineNumber = host.querySelector('.cm-lineNumbers .cm-gutterElement')
      const content = host.querySelector('.cm-content')
      const line = host.querySelector('.cm-line')
      if (!(scroller instanceof HTMLElement) || !(gutter instanceof HTMLElement) || !(lineNumber instanceof HTMLElement) || !(content instanceof HTMLElement) || !(line instanceof HTMLElement)) {
        throw new Error('CodeMirror layout is missing its compact gutter.')
      }
      const gutterRect = gutter.getBoundingClientRect()
      const lineNumberRect = lineNumber.getBoundingClientRect()
      const contentRect = content.getBoundingClientRect()
      return {
        clientWidth: scroller.clientWidth,
        scrollWidth: scroller.scrollWidth,
        scrollbarGutter: getComputedStyle(scroller).scrollbarGutter,
        gutterWidth: gutterRect.width,
        codeGap: contentRect.left + Number.parseFloat(getComputedStyle(line).paddingLeft) - (lineNumberRect.left + lineNumberRect.width),
        auxiliaryGuttersHidden: Array.from(host.querySelectorAll('.cm-foldGutter, .cm-gutter-lint')).every((element) => getComputedStyle(element).display === 'none'),
      }
    })
    expect(shortCodeMirrorMetrics.scrollbarGutter).toBe('auto')
    expect(shortCodeMirrorMetrics.gutterWidth).toBeLessThanOrEqual(40)
    expect(shortCodeMirrorMetrics.codeGap).toBeLessThanOrEqual(12)
    expect(shortCodeMirrorMetrics.auxiliaryGuttersHidden).toBe(true)
    expect(shortCodeMirrorMetrics.scrollWidth).toBeLessThanOrEqual(shortCodeMirrorMetrics.clientWidth + 1)

    const sourceBefore = await visibleBox(workbenchPane(page, 'source'), 'mobile source pane')
    const resultBefore = await visibleBox(workbenchPane(page, 'result'), 'mobile result pane')
    expect(sourceBefore.height).toBeGreaterThan(180)
    expect(resultBefore.height).toBeGreaterThan(180)
    expectVerticalSplit(sourceBefore, resultBefore)
    await expectInsideViewport(page, sourceBefore, 'mobile source pane')
    await expectInsideViewport(page, resultBefore, 'mobile result pane')

    await replaceSource(page, `public static class LongLine { public const string Value = "${'x'.repeat(320)}"; }`)
    const codeMirrorLineMetrics = await editorHost(page, 'codemirror').evaluate((host) => {
      const editor = host.querySelector('.cm-editor')
      const scroller = host.querySelector('.cm-scroller')
      const line = host.querySelector('.cm-line')
      if (!(editor instanceof HTMLElement) || !(scroller instanceof HTMLElement) || !line) {
        throw new Error('CodeMirror layout is incomplete.')
      }
      return {
        wraps: editor.classList.contains('cm-lineWrapping'),
        clientWidth: scroller.clientWidth,
        scrollWidth: scroller.scrollWidth,
        overflowX: getComputedStyle(scroller).overflowX,
        whiteSpace: getComputedStyle(line).whiteSpace,
      }
    })
    expect(codeMirrorLineMetrics.wraps).toBe(false)
    expect(codeMirrorLineMetrics.whiteSpace).toBe('pre')
    expect(codeMirrorLineMetrics.overflowX).toMatch(/auto|scroll/)
    expect(codeMirrorLineMetrics.scrollWidth).toBeGreaterThan(codeMirrorLineMetrics.clientWidth)
    await expectNoDocumentOverflow(page)

    await page.getByRole('combobox', { name: 'View', exact: true }).selectOption('run')
    await replaceSource(page, switchSource)
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')
    await switchEditor(page, 'monaco')
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')

    const monacoGutterMetrics = await editorHost(page, 'monaco').evaluate((host) => {
      const margin = host.querySelector('.monaco-editor .margin')
      const lineNumber = host.querySelector('.monaco-editor .line-numbers')
      const glyphMargin = host.querySelector('.monaco-editor .glyph-margin')
      const viewLines = host.querySelector('.monaco-editor .view-lines')
      if (!(margin instanceof HTMLElement) || !(lineNumber instanceof HTMLElement) || !(viewLines instanceof HTMLElement)) {
        throw new Error('Monaco layout is missing its source gutter.')
      }
      const marginRect = margin.getBoundingClientRect()
      const lineNumberRect = lineNumber.getBoundingClientRect()
      const viewLinesRect = viewLines.getBoundingClientRect()
      const codeGap = viewLinesRect.left - (lineNumberRect.left + lineNumberRect.width)
      return {
        gutterWidth: marginRect.width,
        codeGap,
        glyphMarginWidth: glyphMargin instanceof HTMLElement ? glyphMargin.getBoundingClientRect().width : 0,
        foldingLaneWidth: codeGap,
        lineNumber: lineNumber.textContent?.trim() ?? '',
      }
    })
    expect(monacoGutterMetrics.gutterWidth).toBeLessThanOrEqual(50)
    expect(monacoGutterMetrics.codeGap).toBeLessThanOrEqual(18)
    expect(monacoGutterMetrics.glyphMarginWidth).toBeLessThanOrEqual(0.5)
    expect(monacoGutterMetrics.foldingLaneWidth).toBeGreaterThanOrEqual(15)
    expect(monacoGutterMetrics.foldingLaneWidth).toBeLessThanOrEqual(17)
    expect(monacoGutterMetrics.lineNumber).not.toBe('')

    const sourceAfter = await visibleBox(workbenchPane(page, 'source'), 'mobile Monaco source pane')
    const resultAfter = await visibleBox(workbenchPane(page, 'result'), 'mobile Monaco result pane')
    expectVerticalSplit(sourceAfter, resultAfter)
    expect(Math.abs(sourceAfter.height - sourceBefore.height)).toBeLessThanOrEqual(2)
    expect(Math.abs(resultAfter.height - resultBefore.height)).toBeLessThanOrEqual(2)

    await expectEditorSwitchFits(page)

    await switchEditor(page, 'codemirror')
    await expect(sourceEditor(page)).toContainText('SwitchPreserved')
    const sourceRoundTrip = await visibleBox(workbenchPane(page, 'source'), 'mobile CodeMirror round-trip source pane')
    const resultRoundTrip = await visibleBox(workbenchPane(page, 'result'), 'mobile CodeMirror round-trip result pane')
    expectVerticalSplit(sourceRoundTrip, resultRoundTrip)
    expect(Math.abs(sourceRoundTrip.height - sourceBefore.height)).toBeLessThanOrEqual(2)
    expect(Math.abs(resultRoundTrip.height - resultBefore.height)).toBeLessThanOrEqual(2)
    await expectEditorSwitchFits(page)
    await expectNoDocumentOverflow(page)
  })

  test('runs PHP and displays all JIT methods in the mobile CodeMirror split', async ({ page, isMobile }) => {
    test.skip(!isMobile, 'Mobile PHP coverage.')
    test.setTimeout(240_000)
    const operations = observeOperationWebSocket(page)
    await openWorkbench(page)
    await expectActiveEditor(page, 'codemirror')

    const settings = page.getByRole('button', { name: 'Workbench settings' })
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'true')
    await page.getByLabel('Language').selectOption('php')
    await expect(page.getByLabel('Toolchain')).toHaveValue('peachpie-stable')
    await expect(page.getByLabel('Reference set')).toHaveValue('net10-ref')
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'false')

    const view = page.getByRole('combobox', { name: 'View', exact: true })
    await view.selectOption('run')
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'true')
    await expect(page.getByLabel('Runtime')).toBeVisible()
    await page.getByLabel('Runtime').selectOption('dotnet-10-linux-x64')
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'false')
    await page.getByRole('button', { name: 'Run', exact: true }).click()
    await waitForCompletedOperation(page)
    await expect(page.locator('.terminal-view .result-document')).toContainText('49')
    await expectResultContentFillsPane(page)
    await expect(page.locator('.terminal-view .run-status')).toHaveCount(0)
    const mobileRunStatus = await page.locator('.status-bar .run-status').evaluate((status) => {
      const statusRect = status.getBoundingClientRect()
      const resultBar = status.parentElement
      const metrics = status.querySelector('.run-status-metrics')
      const settings = resultBar?.querySelector('.status-editor-settings')
      const barRect = resultBar?.getBoundingClientRect()
      const metricsRect = metrics?.getBoundingClientRect()
      const settingsRect = settings?.getBoundingClientRect()
      return {
        text: status.textContent ?? '',
        height: statusRect.height,
        scrollWidth: status.scrollWidth,
        clientWidth: status.clientWidth,
        insideBar: barRect !== undefined && statusRect.top >= barRect.top - 1 && statusRect.bottom <= barRect.bottom + 1,
        resultBarLeft: barRect?.left ?? -1,
        resultBarRight: barRect?.right ?? -1,
        metricsLeft: metricsRect?.left ?? -1,
        metricsRight: metricsRect?.right ?? -1,
        settingsLeft: settingsRect?.left ?? -1,
        settingsRight: settingsRect?.right ?? -1,
      }
    })
    expect(mobileRunStatus.text).toContain('Exit 0')
    expect(mobileRunStatus.height).toBeLessThanOrEqual(26)
    expect(mobileRunStatus.scrollWidth).toBeLessThanOrEqual(mobileRunStatus.clientWidth + 1)
    expect(mobileRunStatus.insideBar).toBe(true)
    expect(mobileRunStatus.resultBarLeft).toBe(9)
    expect(mobileRunStatus.metricsLeft).toBe(9)
    expect(mobileRunStatus.settingsLeft - mobileRunStatus.metricsRight).toBeGreaterThanOrEqual(9)
    expect(mobileRunStatus.resultBarRight - mobileRunStatus.settingsRight).toBe(0)

    const runSource = await visibleBox(workbenchPane(page, 'source'), 'mobile PHP source pane')
    const runResult = await visibleBox(workbenchPane(page, 'result'), 'mobile PHP Run pane')
    expectVerticalSplit(runSource, runResult)

    await view.selectOption('jit-asm')
    await moveCursorToLine(page, 5)
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'true')
    await expect(page.getByRole('group', { name: 'JIT scope' })).toHaveCount(0)
    await settings.click()
    await expect(settings).toHaveAttribute('aria-expanded', 'false')

    await page.getByRole('button', { name: 'JIT', exact: true }).click()
    await expect.poll(() => operations.findStart('jit'), { timeout: 90_000 }).toBeDefined()
    const jitStart = operations.findStart('jit')
    if (!jitStart) throw new Error('The PHP JIT start command was not observed.')
    expect(jitStart.request).toMatchObject({ options: { methodFilter: null } })
    await expect.poll(() => operations.operationIdForStart(jitStart)).toMatch(/^op_[0-9a-f]{32}$/)
    const jitOperationId = operations.operationIdForStart(jitStart)
    if (!jitOperationId) throw new Error('The PHP JIT response did not contain an operation ID.')
    await expect.poll(() => operations.hasSubscription(jitOperationId), { timeout: 30_000 }).toBe(true)
    await expect.poll(() => operations.hasEvent(jitOperationId), { timeout: 30_000 }).toBe(true)
    await waitForCompletedOperation(page)
    await expect(page.getByLabel('JIT method')).toHaveCount(0)
    await expect(page.getByLabel('JIT assembly')).toContainText('square')
    await expect(page.locator('.jit-view .run-status')).toHaveCount(0)
    await expect(page.locator('.status-bar').getByRole('status', { name: 'JIT status' })).toContainText('JIT ready')
    await expectResultContentFillsPane(page)

    const jitSource = await visibleBox(workbenchPane(page, 'source'), 'mobile PHP JIT source pane')
    const jitResult = await visibleBox(workbenchPane(page, 'result'), 'mobile PHP JIT result pane')
    expectVerticalSplit(jitSource, jitResult)
    await expectInsideViewport(page, jitSource, 'mobile PHP JIT source pane')
    await expectInsideViewport(page, jitResult, 'mobile PHP JIT result pane')
    await expectEditorSwitchFits(page)
    await expectNoDocumentOverflow(page)
  })

  test('keeps a short mobile code result free of false vertical overflow', async ({ page, isMobile }) => {
    test.skip(!isMobile, 'Mobile result scrolling coverage.')
    await openWorkbench(page)
    await expectActiveEditor(page, 'codemirror')
    await replaceSource(
      page,
      `public static class Program
{
    public static int Main() => "${'x'.repeat(320)}".Length;
}
`,
    )

    await page.getByRole('button', { name: 'Decompile', exact: true }).click()
    await waitForCompletedOperation(page)
    await expectCodeDocumentHorizontalScrollAtPanelBottom(page, 'Decompiled C sharp')
    await expectCodeDocumentHasNoVerticalOverflow(page, 'Decompiled C sharp')
    await expectNoDocumentOverflow(page)
  })

  test('the mobile settings button reveals the selectors hidden from the command bar', async ({ page, isMobile }) => {
    test.skip(!isMobile, 'Mobile settings coverage.')
    await openWorkbench(page)

    const settings = page.getByRole('button', { name: 'Workbench settings' })
    await expect(settings).toHaveAttribute('aria-expanded', 'false')
    await expect(page.getByLabel('Language')).toBeHidden()

    await settings.click()

    await expect(settings).toHaveAttribute('aria-expanded', 'true')
    for (const label of ['Language', 'Toolchain', 'Reference set', 'Output']) {
      const box = await visibleBox(page.getByLabel(label, { exact: true }), `${label} selector`)
      await expectInsideViewport(page, box, `${label} selector`)
    }
    await expect(page.getByRole('group', { name: 'Mode' })).toBeVisible()
    await expectNoDocumentOverflow(page)
  })

  test('safe output edits compile automatically and consume operation events over WebSocket', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Desktop live-compilation coverage.')
    const operations = observeOperationWebSocket(page)
    const languageSockets: string[] = []
    page.on('websocket', (socket) => {
      const url = new URL(socket.url())
      if (/\/api\/v1\/language-sessions\/[^/]+\/lsp$/.test(url.pathname)) {
        languageSockets.push(socket.url())
      }
    })
    await openWorkbench(page, { waitForLsp: true })
    await expect.poll(() => languageSockets.some((url) => new URL(url).protocol === 'ws:')).toBe(true)

    await page.getByLabel('Output', { exact: true }).selectOption('compile-check')
    await replaceSource(page, 'public static class LiveCompile { public static void M() { MissingLiveSymbol(); } }')

    await expect
      .poll(() => operations.findStart('build', 'MissingLiveSymbol'), {
        timeout: 30_000,
      })
      .toBeDefined()
    const buildStart = operations.findStart('build', 'MissingLiveSymbol')
    if (!buildStart) throw new Error('The live Build start command was not observed.')
    await expect.poll(() => operations.operationIdForStart(buildStart)).toMatch(/^op_[0-9a-f]{32}$/)
    const operationId = operations.operationIdForStart(buildStart)
    if (!operationId) throw new Error('The live Build response did not contain an operation ID.')
    await expect.poll(() => operations.hasSubscription(operationId), { timeout: 30_000 }).toBe(true)
    await expect.poll(() => operations.hasEvent(operationId), { timeout: 30_000 }).toBe(true)
    expect(operations.socketUrls.length).toBeGreaterThan(0)
    const diagnosticsTab = page.getByRole('tablist', { name: 'Result views' }).getByRole('tab', { name: /^Diagnostics(?: \(\d+\))?$/ })
    await expect(diagnosticsTab).toHaveAttribute('aria-selected', 'true')
    await expect(page.getByRole('tabpanel')).toContainText('MissingLiveSymbol', {
      timeout: 90_000,
    })
    await waitForCompletedOperation(page)
  })

  test('typing live-runs Run over operation WebSockets while JIT remains explicit', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Desktop live runtime coverage.')
    test.setTimeout(180_000)
    const operations = observeOperationWebSocket(page)
    await openWorkbench(page)
    await waitForCompletedOperation(page)

    const liveSource = 'System.Console.WriteLine("LiveRunFromTyping");'
    await page.getByLabel('Output', { exact: true }).selectOption('run')
    await expect(page.getByLabel('Runtime')).toBeVisible()
    await replaceSource(page, liveSource)

    await expect
      .poll(() => operations.findStart('build', 'LiveRunFromTyping'), {
        timeout: 90_000,
      })
      .toBeDefined()
    const buildStart = operations.findStart('build', 'LiveRunFromTyping')
    if (!buildStart) throw new Error('The live Run Build start command was not observed.')
    await expect.poll(() => operations.operationIdForStart(buildStart)).toMatch(/^op_[0-9a-f]{32}$/)
    const buildOperationId = operations.operationIdForStart(buildStart)
    if (!buildOperationId) {
      throw new Error('The live Run Build response did not contain an operation ID.')
    }
    await expect.poll(() => operations.hasSubscription(buildOperationId), { timeout: 30_000 }).toBe(true)
    await expect.poll(() => operations.hasEvent(buildOperationId), { timeout: 30_000 }).toBe(true)

    await expect.poll(() => operations.findStart('run'), { timeout: 90_000 }).toBeDefined()
    const runStart = operations.findStart('run')
    if (!runStart) throw new Error('The live Run start command was not observed.')
    await expect.poll(() => operations.operationIdForStart(runStart)).toMatch(/^op_[0-9a-f]{32}$/)
    const runOperationId = operations.operationIdForStart(runStart)
    if (!runOperationId) throw new Error('The live Run response did not contain an operation ID.')
    await expect.poll(() => operations.hasSubscription(runOperationId), { timeout: 30_000 }).toBe(true)
    await expect.poll(() => operations.hasEvent(runOperationId), { timeout: 30_000 }).toBe(true)
    await waitForCompletedOperation(page)
    await expect(page.locator('.terminal-view .result-document')).toContainText('LiveRunFromTyping')
    await expectResultContentFillsPane(page)
    expect(operations.socketUrls.length).toBeGreaterThan(0)

    await page.getByLabel('Output', { exact: true }).selectOption('jit-asm')
    await expect(page.getByRole('group', { name: 'JIT scope' })).toHaveCount(0)
    await page.waitForTimeout(800)
    const jitStartsBeforeEdit = operations.sent.filter((frame) => frame.type === 'start' && frame.operation === 'jit').length
    await replaceSource(page, jitSource)
    await moveCursorToLine(page, 10)
    await page.keyboard.insertText(' ')
    await page.waitForTimeout(1_200)
    expect(operations.sent.filter((frame) => frame.type === 'start' && frame.operation === 'jit'), 'JIT must remain an explicit action after source edits').toHaveLength(jitStartsBeforeEdit)
  })

  test('refreshing a JIT workspace runs the restored output exactly once', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Desktop refresh and operation WebSocket coverage.')
    test.setTimeout(180_000)
    const operations = observeOperationWebSocket(page)
    await openWorkbench(page)
    await waitForCompletedOperation(page)

    await page.getByLabel('Output', { exact: true }).selectOption('jit-asm')
    await replaceSource(page, jitSource)
    await expect
      .poll(async () => {
        const hash = new URL(page.url()).hash
        if (!hash.startsWith('#v3:')) return null
        return (await decodeV3(hash)).state.outputId
      })
      .toBe('jit-asm')

    const startsBeforeRefresh = operations.sent.filter((frame) => frame.type === 'start' && frame.operation === 'jit').length
    expect(startsBeforeRefresh).toBe(0)

    await page.reload()
    await expect(page.getByLabel('Output', { exact: true })).toHaveValue('jit-asm')
    await expect.poll(() => operations.sent.filter((frame) => frame.type === 'start' && frame.operation === 'jit').length, { timeout: 90_000 }).toBe(startsBeforeRefresh + 1)
    await waitForCompletedOperation(page)

    await page.waitForTimeout(1_200)
    expect(operations.sent.filter((frame) => frame.type === 'start' && frame.operation === 'jit')).toHaveLength(startsBeforeRefresh + 1)
  })

  test('JIT always shows all methods without a filtering toolbar', async ({ page, isMobile }, testInfo) => {
    test.setTimeout(180_000)
    await openWorkbench(page, { waitForLsp: true })
    await waitForCompletedOperation(page)
    await expectActiveEditor(page, isMobile ? 'codemirror' : 'monaco')

    const output = isMobile ? page.getByRole('combobox', { name: 'View', exact: true }) : page.getByLabel('Output', { exact: true })
    await output.selectOption('jit-asm')
    await replaceSource(page, jitSource)

    await expect(page.getByRole('group', { name: 'JIT scope' })).toHaveCount(0)
    const run = page.getByRole('button', { name: 'JIT', exact: true })
    await expect(run).toBeEnabled({ timeout: 30_000 })
    await run.click()
    await waitForCompletedOperation(page)

    await expect(page.getByLabel('JIT method')).toHaveCount(0)
    await expect(page.locator('.jit-toolbar')).toHaveCount(0)
    const monacoJit = page.locator('.jit-view .monaco-code-document')
    const codeMirrorJit = page.locator('.jit-view .code-document-view:not(.monaco-code-document)')
    if (isMobile) {
      await expect(codeMirrorJit).toBeVisible()
      await expect(codeMirrorJit.locator('.cm-content')).toContainText('CurrentTarget')
      await expect(codeMirrorJit.locator('.cm-content')).toContainText('Other')
    } else {
      await expect(monacoJit).toBeVisible()
      await expect(monacoJit.locator('.view-lines')).toContainText('CurrentTarget')
      await expect(monacoJit.locator('.view-lines')).toContainText('Other')
    }
    await expect(page.locator('.jit-view .run-status')).toHaveCount(0)
    await expect(page.locator('.status-bar').getByRole('status', { name: 'JIT status' })).toContainText('JIT ready')
    await expectResultContentFillsPane(page)
    await page.screenshot({
      path: testInfo.outputPath(`jit-all-methods-${isMobile ? 'mobile-codemirror' : 'desktop-monaco'}.png`),
      fullPage: true,
    })

    await switchEditor(page, isMobile ? 'monaco' : 'codemirror')
    if (isMobile) {
      await expect(codeMirrorJit).toHaveCount(0)
      await expect(monacoJit).toBeVisible()
      await expect(monacoJit.locator('.view-lines')).toContainText('CurrentTarget')
      await expect(monacoJit.locator('.view-lines')).toContainText('Other')
    } else {
      await expect(monacoJit).toHaveCount(0)
      await expect(codeMirrorJit).toBeVisible()
      await expect(codeMirrorJit.locator('.cm-content')).toContainText('CurrentTarget')
      await expect(codeMirrorJit.locator('.cm-content')).toContainText('Other')
    }
    await expectResultContentFillsPane(page)
    await expectNoDocumentOverflow(page)
    await page.screenshot({
      path: testInfo.outputPath(`jit-all-methods-${isMobile ? 'mobile-monaco' : 'desktop-codemirror'}.png`),
      fullPage: true,
    })
  })

  test('real C# semantic tokens render VS type, method, and variable colors in both editors', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Desktop semantic-color coverage.')
    await openWorkbench(page, { waitForLsp: true })
    await waitForCompletedOperation(page)
    await page.getByLabel('Output', { exact: true }).selectOption('run')
    await replaceSource(page, semanticSource)

    await expectSemanticColor(editorHost(page, 'monaco').locator('.view-lines'), 'SemanticWidget', '#2b91af')
    await expectSemanticColor(editorHost(page, 'monaco').locator('.view-lines'), 'CalculateValue', '#795e26')
    await expectSemanticColor(editorHost(page, 'monaco').locator('.view-lines'), 'localValue', '#001080')
    await expectSemanticColor(editorHost(page, 'monaco').locator('.view-lines'), '\\n', '#ee0000')

    await switchEditor(page, 'codemirror')
    await waitForLanguageServiceReady(page)
    await expectSemanticColor(editorHost(page, 'codemirror').locator('.cm-semantic-type'), 'SemanticWidget', '#2b91af')
    await expectSemanticColor(editorHost(page, 'codemirror').locator('.cm-semantic-method'), 'CalculateValue', '#795e26')
    await expectSemanticColor(editorHost(page, 'codemirror').locator('.cm-semantic-variable'), 'localValue', '#001080')
    await expectSemanticColor(editorHost(page, 'codemirror').locator('.cm-semantic-escape'), '\\n', '#ee0000')
  })

  test('Monaco retains C++/CLI lexical colors across language, editor, and reload transitions', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Desktop Monaco lexical-color coverage.')
    await openWorkbench(page)
    await page.getByLabel('Language').selectOption('cppcli')
    await expectActiveEditor(page, 'monaco')
    await replaceSource(page, cppCliHighlightSource)
    await expectCppCliMonacoColors(page)

    await page.getByLabel('Language').selectOption('csharp')
    await page.getByLabel('Language').selectOption('cppcli')
    await expectCppCliMonacoColors(page)

    await switchEditor(page, 'codemirror')
    await switchEditor(page, 'monaco')
    await expectCppCliMonacoColors(page)

    await expect
      .poll(async () => {
        const hash = new URL(page.url()).hash
        if (!hash.startsWith('#v3:')) return null
        return (await decodeV3(hash)).state.languageId
      })
      .toBe('cppcli')
    await page.reload()
    await expect(page.getByLabel('Language')).toHaveValue('cppcli')
    await expectActiveEditor(page, 'monaco')
    await expectCppCliMonacoColors(page)
  })
})

async function expectCppCliMonacoColors(page: import('@playwright/test').Page) {
  const source = editorHost(page, 'monaco').locator('.view-lines')
  await expectSemanticColor(source, 'int', '#2b91af')
  await expectSemanticColor(source, 'String', '#2b91af')
  await expectSemanticColor(source, 'main', '#795e26')
  await expectSemanticColor(source, 'Console::WriteLine', '#795e26')
  await expectSemanticColor(source, '^', '#001080')
  await expectSemanticColor(source, '"Hello\u00a0from\u00a0C++/CLI"', '#a31515')
  await expectTokenContainingColor(source, '#include', '#0000ff')
  await expectTokenContainingColor(source, 'raw\u00a0body', '#a31515')
  await expectTokenContainingColor(source, 'wide\u00a0text', '#a31515')
}

async function expectSemanticColor(root: Locator, tokenText: string, expectedColor: string) {
  await expect
    .poll(
      async () =>
        colorToHex(
          await root.evaluateAll((elements, text) => {
            const candidates = elements.flatMap((element) => [element, ...element.querySelectorAll<HTMLElement>('span')])
            const token = candidates.find((candidate) => candidate.children.length === 0 && candidate.textContent?.trim() === text)
            return token ? getComputedStyle(token).color : null
          }, tokenText),
        ),
      { timeout: 30_000 },
    )
    .toBe(expectedColor)
}

async function expectTokenContainingColor(root: Locator, tokenText: string, expectedColor: string) {
  await expect
    .poll(
      async () =>
        colorToHex(
          await root.evaluateAll((elements, text) => {
            const candidates = elements.flatMap((element) => [element, ...element.querySelectorAll<HTMLElement>('span')])
            const token = candidates.find((candidate) => candidate.children.length === 0 && candidate.textContent?.includes(text))
            return token ? getComputedStyle(token).color : null
          }, tokenText),
        ),
      { timeout: 30_000 },
    )
    .toBe(expectedColor)
}

function colorToHex(color: string | null): string | null {
  if (!color) return null
  const channels = color
    .match(/\d+(?:\.\d+)?/g)
    ?.slice(0, 3)
    .map(Number)
  if (channels?.length !== 3) return color.toLowerCase()
  return `#${channels.map((channel) => Math.round(channel).toString(16).padStart(2, '0')).join('')}`
}
