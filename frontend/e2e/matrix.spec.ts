import { expect, type Page, test } from '@playwright/test'
import {
  expectCodeDocumentHorizontalScrollAtPanelBottom,
  expectResultContentFillsPane,
  moveCursorToLine,
  observeOperationWebSocket,
  waitForLanguageServiceReady,
} from './helpers/workbench'

const sharpLabV2HostSwap = '#v2:EYLgtghglgdgNAFxFANgHwQUwM4IAQDGA9gCaZA='
const sharpLabV1HostSwap = '#b:legacy/f:fs>asmr/DYUwLgBAhgdgzgdxAJwgXggFgExA'
const cppCliBootstrapNoise = [
  '<CrtImplementationDetails>',
  '<CppImplementationDetails>',
  '_crt_argv_mode',
  '_crt_app_type',
  'HINSTANCE__',
  '_IMAGE_DOS_HEADER',
  '_IMAGE_NT_HEADERS64',
]

async function openWorkbench(page: Page, fragment = '') {
  await page.goto(`/${fragment}`)
  if (fragment) await page.reload()
  await expect(page.getByLabel('Language')).toBeEnabled()
}

async function selectAvailableOption(page: Page, label: string, value: string) {
  const select = page.getByLabel(label, { exact: true })
  const option = select.locator(`option[value="${value}"]`)
  await expect(option).toHaveCount(1)
  await expect(option).toBeEnabled()
  await select.selectOption(value)
  await expect(select).toHaveValue(value)
}

async function runOperation(page: Page, action: string) {
  const button = page.getByRole('button', { name: action, exact: true })
  await expect(button).toBeEnabled({ timeout: 30_000 })
  await button.click()
  await expect(page.locator('.operation-state')).toHaveText('completed', { timeout: 90_000 })
  await expect(page.locator('.result-error')).toHaveCount(0)
}

async function readIdentity(page: Page): Promise<Record<string, string>> {
  const identity = await page
    .locator('.identity-strip > div')
    .evaluateAll((rows) =>
      Object.fromEntries(
        rows.map((row) => [
          row.querySelector('dt')?.textContent?.trim() ?? '',
          row.querySelector('dd')?.textContent?.trim() ?? '',
        ]),
      ),
    )

  for (const label of ['Toolchain', 'Reference set', 'Runtime']) {
    const select = page.getByLabel(label)
    if ((await select.count()) !== 1) continue

    const selected = await select.locator('option:checked').textContent()
    if (selected) identity[label] = selected.trim()
  }

  return identity
}

async function assertIdentity(page: Page, expected: Record<string, string>) {
  await expect(page.locator('.identity-strip')).toBeAttached()
  await expect
    .poll(async () => {
      const actual = await readIdentity(page)
      return Object.fromEntries(
        Object.entries(expected).map(([key, value]) => {
          const displayed = actual[key] ?? ''
          return [key, displayed.startsWith(value) ? value : displayed]
        }),
      )
    })
    .toMatchObject(expected)
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
  expect(dimensions.scrollWidth).toBeLessThanOrEqual(dimensions.clientWidth)
  expect(dimensions.bodyScrollWidth).toBeLessThanOrEqual(dimensions.clientWidth)
  expect(dimensions.scrollHeight).toBeLessThanOrEqual(dimensions.clientHeight)
  expect(dimensions.bodyScrollHeight).toBeLessThanOrEqual(dimensions.clientHeight)
}

test.describe('SharpLabNext desktop capability matrix', () => {
  test.beforeEach(({ isMobile }) => {
    test.skip(isMobile, 'Desktop matrix coverage.')
  })

  test('defaults a fresh workspace to the latest C# compiler, reference set, and runtime', async ({
    page,
  }) => {
    await openWorkbench(page)

    await expect(page.getByLabel('Language')).toHaveValue('csharp')
    await expect(page.getByLabel('Toolchain')).toHaveValue('roslyn-main')
    await expect(page.getByLabel('Reference set')).toHaveValue('net11-preview-ref')
    await expect(page.getByLabel('Output', { exact: true })).toHaveValue('decompiled-csharp')
    await expect(page.getByLabel('Toolchain')).toHaveAttribute('title', 'Compiler toolchain')
    await expect(page.getByLabel('Reference set')).toHaveAttribute(
      'title',
      'Reference set used for compilation',
    )
    await expect(page.getByRole('button', { name: 'Decompile', exact: true })).toBeEnabled()

    const widths = await page.evaluate(() => ({
      toolchain: document
        .querySelector<HTMLSelectElement>('select[aria-label="Toolchain"]')
        ?.getBoundingClientRect().width,
      api: document
        .querySelector<HTMLSelectElement>('select[aria-label="Reference set"]')
        ?.getBoundingClientRect().width,
    }))
    expect(widths.toolchain).toBeGreaterThanOrEqual(215)
    expect(widths.api).toBeGreaterThanOrEqual(135)

    await selectAvailableOption(page, 'Output', 'run')
    await expect(page.getByLabel('Runtime')).toHaveValue('dotnet-11-preview-linux-x64')
    await expect(page.getByLabel('Runtime')).toHaveAttribute(
      'title',
      'Runtime used for Run and JIT',
    )
  })

  test('runs Visual Basic with Roslyn stable on the .NET 11 preview runtime', async ({ page }) => {
    await openWorkbench(page)
    await selectAvailableOption(page, 'Language', 'visual-basic')
    await selectAvailableOption(page, 'Toolchain', 'roslyn-stable')
    await selectAvailableOption(page, 'Reference set', 'net11-preview-ref')
    await selectAvailableOption(page, 'Output', 'run')
    await expect(page.getByLabel('Runtime')).toBeVisible()
    await selectAvailableOption(page, 'Runtime', 'dotnet-11-preview-linux-x64')

    await runOperation(page, 'Run')

    await expect(page.getByRole('tab', { name: 'Output', exact: true })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    await expect(page.locator('.terminal-view .result-document')).toContainText(
      'Hello from SharpLabNext',
    )
    await assertIdentity(page, {
      Toolchain: 'Roslyn Stable 5.6.0',
      'Reference set': '.NET Main',
      Runtime: '.NET Main',
    })
    await assertNoDocumentOverflow(page)
  })

  test('renders IL with the default assembler and runs MiniLang through generated IL', async ({
    page,
  }) => {
    await openWorkbench(page)
    await selectAvailableOption(page, 'Language', 'il')
    await expect(page.getByLabel('Toolchain')).toHaveValue('mobius-ilasm-stable')
    await selectAvailableOption(page, 'Output', 'il')
    const longLiteral = `Hello-${'LongIlOutput'.repeat(48)}`
    await moveCursorToLine(page, 8)
    await page.keyboard.press('Shift+Home')
    await page.keyboard.insertText(`    ldstr "${longLiteral}"`)

    await runOperation(page, 'Render IL')

    await expect(page.getByRole('tab', { name: 'IL', exact: true })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    await expect(
      page.getByRole('textbox', { name: 'Intermediate language', exact: true }),
    ).toContainText('.method')
    await expectResultContentFillsPane(page)
    await expectCodeDocumentHorizontalScrollAtPanelBottom(page, 'Intermediate language')
    await assertIdentity(page, {
      Toolchain: 'Mobius ILAsm Stable',
      Runtime: 'Not required',
    })
    await assertNoDocumentOverflow(page)

    await selectAvailableOption(page, 'Language', 'minilang')
    await expect(page.getByLabel('Toolchain')).toHaveValue('minilang-stable')
    await selectAvailableOption(page, 'Output', 'generated-il')

    await runOperation(page, 'Build')

    await expect(page.getByRole('tab', { name: 'Generated IL', exact: true })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    const generatedIl = page.getByRole('textbox', {
      name: 'Generated intermediate language',
      exact: true,
    })
    await expect(generatedIl).toContainText('.assembly')
    await expect(generatedIl).toContainText('Hello from a third-party language')
    await assertIdentity(page, {
      Toolchain: 'MiniLang SDK Sample 1.0.0',
      Runtime: 'Not required',
    })

    await selectAvailableOption(page, 'Reference set', 'net10-ref')
    await selectAvailableOption(page, 'Output', 'run')
    await expect(page.getByLabel('Runtime')).toBeVisible()
    await selectAvailableOption(page, 'Runtime', 'dotnet-10-linux-x64')
    await runOperation(page, 'Run')

    await expect(page.locator('.terminal-view .result-document')).toContainText(
      'Hello from a third-party language',
    )
    await assertIdentity(page, {
      Toolchain: 'MiniLang SDK Sample 1.0.0',
      'Reference set': '.NET 10',
      Runtime: '.NET 10',
    })
    await assertNoDocumentOverflow(page)
  })

  test('uses Roslyn main for C# AST and Visual Basic compile check', async ({ page }) => {
    await openWorkbench(page)
    await selectAvailableOption(page, 'Toolchain', 'roslyn-main')
    await selectAvailableOption(page, 'Output', 'ast')

    await runOperation(page, 'Build AST')

    await expect(page.getByRole('tab', { name: 'AST', exact: true })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    await expect(page.locator('.ast-toolbar')).toContainText('roslyn-main')
    await assertIdentity(page, {
      Toolchain: 'Roslyn Main',
      Runtime: 'Not required',
    })
    await assertNoDocumentOverflow(page)

    await selectAvailableOption(page, 'Language', 'visual-basic')
    await expect(page.getByLabel('Toolchain')).toHaveValue('roslyn-main')
    await selectAvailableOption(page, 'Output', 'compile-check')
    await runOperation(page, 'Check')

    await expect(page.getByRole('tab', { name: 'Diagnostics', exact: true })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    await expect(page.locator('.result-tab-empty')).toContainText('No diagnostics')
    await assertIdentity(page, {
      Toolchain: 'Roslyn Main',
      Runtime: 'Not required',
    })
    await assertNoDocumentOverflow(page)
  })

  test('opens the default single-file F# language session without error markers', async ({
    page,
  }) => {
    await openWorkbench(page)
    await selectAvailableOption(page, 'Language', 'fsharp')
    await expect(page.getByLabel('Toolchain')).toHaveValue('fsharp-stable')
    await selectAvailableOption(page, 'Output', 'compile-check')

    await expect(
      page.getByRole('tablist', { name: 'Workspace files' }).getByRole('tab'),
    ).toHaveCount(1)
    await waitForLanguageServiceReady(page)
    await expect(page.locator('.squiggly-error')).toHaveCount(0)
    await assertIdentity(page, {
      Toolchain: 'F# Stable 43.12.204',
      'Reference set': '.NET 10',
      Runtime: 'Not required',
    })
    await assertNoDocumentOverflow(page)
  })

  test('decompiles, renders IL, and runs C++/CLI through the isolated Wine runtime', async ({
    page,
  }) => {
    test.setTimeout(240_000)
    const operations = observeOperationWebSocket(page)
    await openWorkbench(page)
    await selectAvailableOption(page, 'Language', 'cppcli')

    await expect(page.getByLabel('Toolchain')).toHaveValue('msvc-cppcli-netfx48')
    await expect(page.getByLabel('Reference set')).toHaveValue('netfx48-ref')
    await expect(page.getByLabel('Output', { exact: true })).toHaveValue('decompiled-csharp')
    await expect(page.getByRole('tab', { name: 'Program.cpp', exact: true })).toBeVisible()
    for (const unsupported of ['ast', 'il-verify', 'jit-asm', 'execution-flow', 'run-il']) {
      await expect(
        page.getByLabel('Output', { exact: true }).locator(`option[value="${unsupported}"]`),
      ).toHaveCount(0)
    }

    await runOperation(page, 'Decompile')
    const decompiled = page.getByRole('textbox', {
      name: 'Decompiled C sharp',
      exact: true,
    })
    await expect(decompiled).toContainText('main(')
    await expect(decompiled).toContainText('Hello from C++/CLI')
    for (const marker of cppCliBootstrapNoise) {
      await expect(decompiled).not.toContainText(marker)
    }

    await selectAvailableOption(page, 'Output', 'il')
    await runOperation(page, 'Render IL')
    const il = page.getByRole('textbox', { name: 'Intermediate language', exact: true })
    await expect(il).toContainText('main')
    for (const marker of cppCliBootstrapNoise) {
      await expect(il).not.toContainText(marker)
    }

    await selectAvailableOption(page, 'Output', 'run')
    await expect(page.getByLabel('Runtime')).toHaveValue('wine-netfx48-linux-x64')
    await runOperation(page, 'Run')
    await expect(page.locator('.terminal-view .result-document')).toContainText(
      'Hello from C++/CLI',
    )
    await expect
      .poll(() => operations.findStart('run', 'wine-netfx48-linux-x64'), { timeout: 30_000 })
      .toBeDefined()
    await assertIdentity(page, {
      Toolchain: 'MSVC C++/CLI 19.51 / .NET Framework 4.8',
      'Reference set': '.NET Framework 4.8',
      Runtime: '.NET Framework 4.8 / Wine 9.0',
    })
    await assertNoDocumentOverflow(page)
  })

  test('decompiles, runs, and displays all PHP JIT methods through PeachPie', async ({ page }) => {
    test.setTimeout(240_000)
    const operations = observeOperationWebSocket(page)
    await openWorkbench(page)
    await selectAvailableOption(page, 'Language', 'php')

    await expect(page.getByLabel('Toolchain')).toHaveValue('peachpie-stable')
    await expect(page.getByLabel('Reference set')).toHaveValue('net10-ref')
    await expect(page.getByLabel('Output', { exact: true })).toHaveValue('decompiled-csharp')
    await expect(page.getByText('LSP disabled', { exact: true })).toHaveCount(0)
    await expect(
      page.getByLabel('Output', { exact: true }).locator('option[value="execution-flow"]'),
    ).toHaveCount(0)

    await runOperation(page, 'Decompile')
    await expect(page.getByRole('tab', { name: 'Decompiled C#', exact: true })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    await expect(
      page.getByRole('textbox', { name: 'Decompiled C sharp', exact: true }),
    ).toContainText('square')
    await assertIdentity(page, {
      Toolchain: 'PeachPie Stable 1.1.13',
      'Reference set': '.NET 10',
      Runtime: 'Not required',
    })

    await selectAvailableOption(page, 'Output', 'run')
    await runOperation(page, 'Run')
    await expect(page.locator('.terminal-view .result-document')).toContainText('49')
    await assertIdentity(page, {
      Toolchain: 'PeachPie Stable 1.1.13',
      'Reference set': '.NET 10',
      Runtime: '.NET 10',
    })

    await selectAvailableOption(page, 'Output', 'jit-asm')
    const editor = page.getByRole('region', { name: /^Source editor(?:\.|$)/ }).first()
    await editor.click({ position: { x: 240, y: 100 } })
    await page.keyboard.press('ControlOrMeta+Home')
    for (let line = 1; line < 5; line += 1) await page.keyboard.press('ArrowDown')
    await page.keyboard.press('End')

    await expect(page.getByRole('group', { name: 'JIT scope' })).toHaveCount(0)
    await runOperation(page, 'JIT')
    await expect.poll(() => operations.findStart('jit'), { timeout: 90_000 }).toBeDefined()
    const jitStart = operations.findStart('jit')
    if (!jitStart) throw new Error('The PHP JIT start command was not observed.')
    expect(jitStart.request).toMatchObject({ options: { methodFilter: null } })
    await expect.poll(() => operations.operationIdForStart(jitStart)).toMatch(/^op_[0-9a-f]{32}$/)
    const jitOperationId = operations.operationIdForStart(jitStart)
    if (!jitOperationId) throw new Error('The PHP JIT response did not contain an operation ID.')
    await expect
      .poll(() => operations.hasSubscription(jitOperationId), { timeout: 30_000 })
      .toBe(true)
    await expect.poll(() => operations.hasEvent(jitOperationId), { timeout: 30_000 }).toBe(true)
    await expect(page.getByLabel('JIT method')).toHaveCount(0)
    await expect(page.getByLabel('JIT assembly')).toContainText('square')
    await assertNoDocumentOverflow(page)
  })

  test('restores SharpLab v2 and v1 host-swap vectors in the browser', async ({ page }) => {
    await openWorkbench(page, sharpLabV2HostSwap)
    await expect(page.getByLabel('Language')).toHaveValue('csharp')
    await expect(page.getByLabel('Toolchain')).toHaveValue('roslyn-stable')
    await expect(page.getByLabel('Output', { exact: true })).toHaveValue('il')
    await expect(page.locator('.monaco-editor .view-lines')).toContainText('test code')
    await assertIdentity(page, {
      Toolchain: 'Roslyn Stable 5.6.0',
      'Reference set': '.NET 10',
      Runtime: 'Not required',
    })
    await assertNoDocumentOverflow(page)

    await openWorkbench(page, sharpLabV1HostSwap)
    await expect(page.getByLabel('Language')).toHaveValue('fsharp')
    await expect(page.getByLabel('Toolchain')).toHaveValue('fsharp-stable')
    await expect(page.getByLabel('Output', { exact: true })).toHaveValue('jit-asm')
    await expect(page.getByRole('button', { name: 'Release', exact: true })).toHaveAttribute(
      'aria-pressed',
      'true',
    )
    await expect(page.locator('.monaco-editor .view-lines')).toContainText('let answer = 42')
    await assertIdentity(page, {
      Toolchain: 'F# Stable 43.12.204',
      'Reference set': '.NET 10',
      Runtime: '.NET 10',
    })
    await assertNoDocumentOverflow(page)
  })
})
