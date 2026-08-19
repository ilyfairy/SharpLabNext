import { expect, type Locator, type Page } from '@playwright/test'
import { decodeWire } from '../../src/api/wire'

export type EditorKind = 'monaco' | 'codemirror'

export interface ElementBox {
  x: number
  y: number
  width: number
  height: number
}

interface OperationCommandFrame {
  type: string
  commandId?: string
  operation?: string
  operationId?: string
  fromSequence?: number
  request?: unknown
}

interface OperationResponseFrame {
  type: string
  commandId?: string
  ok?: boolean
  payload?: unknown
  operationId?: string
}

export interface OperationWebSocketTrace {
  socketUrls: string[]
  sent: OperationCommandFrame[]
  received: OperationResponseFrame[]
  findStart: (operation: string, requestText?: string) => OperationCommandFrame | undefined
  operationIdForStart: (start: OperationCommandFrame) => string | null
  hasSubscription: (operationId: string, fromSequence?: number) => boolean
  hasEvent: (operationId: string) => boolean
}

export function observeOperationWebSocket(page: Page): OperationWebSocketTrace {
  const socketUrls: string[] = []
  const sent: OperationCommandFrame[] = []
  const received: OperationResponseFrame[] = []

  page.on('websocket', (socket) => {
    const url = new URL(socket.url())
    if (url.pathname !== '/api/v1/operations/ws') return

    socketUrls.push(socket.url())
    socket.on('framesent', ({ payload }) => {
      const frame = parseJsonFrame(payload)
      if (frame) sent.push(frame)
    })
    socket.on('framereceived', ({ payload }) => {
      const frame = parseJsonFrame(payload)
      if (frame) received.push(frame)
    })
  })

  return {
    socketUrls,
    sent,
    received,
    findStart: (operation, requestText) =>
      sent.find(
        (frame) =>
          frame.type === 'start' &&
          frame.operation === operation &&
          (requestText === undefined || JSON.stringify(frame.request).includes(requestText)),
      ),
    operationIdForStart: (start) => {
      if (!start.commandId) return null
      const response = received.find(
        (frame) => frame.type === 'response' && frame.commandId === start.commandId && frame.ok,
      )
      return response ? operationIdFromPayload(response.payload) : null
    },
    hasSubscription: (operationId, fromSequence = 0) =>
      sent.some(
        (frame) =>
          frame.type === 'subscribe' &&
          frame.operationId === operationId &&
          frame.fromSequence === fromSequence,
      ),
    hasEvent: (operationId) =>
      received.some((frame) => frame.type === 'event' && frame.operationId === operationId),
  }
}

function parseJsonFrame(payload: string | Buffer): OperationCommandFrame | null {
  try {
    const parsed = decodeWire<unknown>(
      JSON.parse(typeof payload === 'string' ? payload : payload.toString('utf8')),
    )
    return parsed && typeof parsed === 'object' && typeof parsed.type === 'string'
      ? (parsed as OperationCommandFrame)
      : null
  } catch {
    return null
  }
}

function operationIdFromPayload(payload: unknown): string | null {
  if (!payload || typeof payload !== 'object') return null
  const operationId = Reflect.get(payload, 'operationId')
  return typeof operationId === 'string' ? operationId : null
}

export async function openWorkbench(page: Page, options?: { waitForLsp?: boolean }) {
  await page.goto('/')
  await expect(page.getByLabel('Language')).toBeEnabled()
  if (options?.waitForLsp) {
    await waitForLanguageServiceReady(page)
  }
}

export async function waitForLanguageServiceReady(page: Page) {
  await expect(page.locator('[data-editor][data-language-service-status]')).toHaveAttribute(
    'data-language-service-status',
    'ready',
    { timeout: 30_000 },
  )
}

export function sourceEditor(page: Page): Locator {
  return page.getByRole('region', { name: /^Source editor(?:\.|$)/ }).first()
}

export function editorHost(page: Page, editor: EditorKind): Locator {
  return page.locator(`[data-editor="${editor}"]`)
}

export function workbenchPane(page: Page, pane: 'source' | 'result'): Locator {
  return page.locator(`[data-workbench-pane="${pane}"]`)
}

export function editorSwitch(page: Page): Locator {
  return page.getByRole('toolbar', { name: 'Editor', includeHidden: true })
}

async function revealEditorSwitch(page: Page) {
  const control = editorSwitch(page)
  if (await control.isVisible()) return
  const settings = page.getByRole('button', { name: 'Editor settings' })
  await settings.click()
  await expect(settings).toHaveAttribute('aria-expanded', 'true')
  await expect(control).toBeVisible()
}

export async function expectActiveEditor(page: Page, editor: EditorKind) {
  const activeName = editor === 'monaco' ? 'Monaco' : 'CodeMirror'
  const inactiveName = editor === 'monaco' ? 'CodeMirror' : 'Monaco'
  const control = editorSwitch(page)

  if (await control.isVisible()) {
    await expect(control.getByRole('button', { name: activeName, exact: true })).toHaveAttribute(
      'aria-pressed',
      'true',
    )
    await expect(control.getByRole('button', { name: inactiveName, exact: true })).toHaveAttribute(
      'aria-pressed',
      'false',
    )
  }
  await expect(editorHost(page, editor)).toBeVisible()
  await expect(editorHost(page, editor === 'monaco' ? 'codemirror' : 'monaco')).toHaveCount(0)
}

export async function switchEditor(page: Page, editor: EditorKind) {
  const name = editor === 'monaco' ? 'Monaco' : 'CodeMirror'
  await revealEditorSwitch(page)
  await editorSwitch(page).getByRole('button', { name, exact: true }).click()
  await expectActiveEditor(page, editor)
}

export async function expectEditorSwitchFits(page: Page) {
  await revealEditorSwitch(page)
  const control = editorSwitch(page)
  const monaco = await visibleBox(
    control.getByRole('button', { name: 'Monaco', exact: true }),
    'Monaco editor switch',
  )
  const codeMirror = await visibleBox(
    control.getByRole('button', { name: 'CodeMirror', exact: true }),
    'CodeMirror editor switch',
  )

  expect(monaco.x + monaco.width).toBeLessThanOrEqual(codeMirror.x + 0.5)
  await expectInsideViewport(page, monaco, 'Monaco editor switch')
  await expectInsideViewport(page, codeMirror, 'CodeMirror editor switch')
}

export async function replaceSource(page: Page, source: string) {
  const editor = sourceEditor(page)
  await editor.click({ position: { x: 240, y: 100 } })
  await page.keyboard.press('ControlOrMeta+A')
  const origin = new URL(page.url()).origin
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write'], { origin })
  await page.evaluate((text) => navigator.clipboard.writeText(text), source)
  await page.keyboard.press('ControlOrMeta+V')
}

export async function moveCursorToLine(page: Page, lineNumber: number) {
  const editor = sourceEditor(page)
  await editor.click({ position: { x: 240, y: 100 } })
  await page.keyboard.press('ControlOrMeta+Home')
  for (let line = 1; line < lineNumber; line += 1) {
    await page.keyboard.press('ArrowDown')
  }
  await page.keyboard.press('End')
}

export async function visibleBox(locator: Locator, label: string): Promise<ElementBox> {
  await expect(locator, `${label} should be visible`).toBeVisible()
  const box = await locator.boundingBox()
  expect(box, `${label} should have a layout box`).not.toBeNull()
  expect(box?.width ?? 0, `${label} should have a stable width`).toBeGreaterThan(1)
  expect(box?.height ?? 0, `${label} should have a stable height`).toBeGreaterThan(1)
  return box as ElementBox
}

export function expectHorizontalSplit(source: ElementBox, result: ElementBox) {
  expect(source.x + source.width).toBeLessThanOrEqual(result.x + 1)
  expect(
    Math.min(source.y + source.height, result.y + result.height) - Math.max(source.y, result.y),
  ).toBeGreaterThan(100)
}

export function expectVerticalSplit(source: ElementBox, result: ElementBox) {
  expect(source.y + source.height).toBeLessThanOrEqual(result.y + 1)
  expect(
    Math.min(source.x + source.width, result.x + result.width) - Math.max(source.x, result.x),
  ).toBeGreaterThan(100)
}

export async function expectInsideViewport(page: Page, box: ElementBox, label: string) {
  const viewport = page.viewportSize()
  expect(viewport, `${label} requires a fixed Playwright viewport`).not.toBeNull()
  expect(box.x, `${label} starts outside the viewport`).toBeGreaterThanOrEqual(-0.5)
  expect(box.y, `${label} starts outside the viewport`).toBeGreaterThanOrEqual(-0.5)
  expect(box.x + box.width, `${label} overflows horizontally`).toBeLessThanOrEqual(
    (viewport?.width ?? 0) + 0.5,
  )
  expect(box.y + box.height, `${label} overflows vertically`).toBeLessThanOrEqual(
    (viewport?.height ?? 0) + 0.5,
  )
}

export async function expectNoDocumentOverflow(page: Page) {
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

export async function expectResultContentFillsPane(page: Page) {
  const panelLocator = page.getByRole('tabpanel')
  const pane = await visibleBox(workbenchPane(page, 'result'), 'result pane')
  const body = await visibleBox(page.locator('.result-body'), 'result body')
  const shell = await visibleBox(page.locator('.result-tabs-shell'), 'result tabs shell')
  const toolbar = await visibleBox(page.locator('.result-tabs-toolbar'), 'result tabs toolbar')
  const tabs = await visibleBox(page.getByRole('tablist', { name: 'Result views' }), 'result tabs')
  const panel = await visibleBox(panelLocator, 'result tab panel')
  const contentLocator = panelLocator.locator(':scope > *')
  await expect(contentLocator, 'the selected result should have exactly one root').toHaveCount(1)
  const content = await visibleBox(contentLocator, 'selected result root')

  expect(Math.abs(body.y - pane.y), 'result body should start at the pane top').toBeLessThanOrEqual(
    1,
  )
  expect(
    Math.abs(body.y + body.height - (pane.y + pane.height)),
    'result body should reach the pane bottom',
  ).toBeLessThanOrEqual(1)
  expect(
    Math.abs(shell.y + shell.height - (body.y + body.height)),
    'result tabs should use the remaining result-body height',
  ).toBeLessThanOrEqual(1)
  expect(
    Math.abs(panel.height - (shell.height - toolbar.height)),
    'the selected result should fill below the tabs and actions',
  ).toBeLessThanOrEqual(2)
  expect(
    Math.abs(content.y - panel.y),
    'the selected result root should start at the panel top',
  ).toBeLessThanOrEqual(1)
  expect(
    Math.abs(content.y + content.height - (panel.y + panel.height)),
    'the selected result root should reach the panel bottom',
  ).toBeLessThanOrEqual(1)

  const resultDocument = panelLocator.locator('.result-document')
  const resultDocumentCount = await resultDocument.count()
  expect(
    resultDocumentCount,
    'a result tab should contain at most one text document',
  ).toBeLessThanOrEqual(1)
  if (resultDocumentCount === 1) {
    const document = await visibleBox(resultDocument, 'result text document')
    expect(
      Math.abs(document.y + document.height - (content.y + content.height)),
      'the result text document should reach its result root bottom',
    ).toBeLessThanOrEqual(1)
  }

  const codeDocument = panelLocator.locator('.code-document-view')
  const codeDocumentCount = await codeDocument.count()
  expect(
    codeDocumentCount,
    'a result tab should contain at most one code document',
  ).toBeLessThanOrEqual(1)
  if (codeDocumentCount === 1) {
    const host = await visibleBox(codeDocument, 'result code document')
    expect(
      Math.abs(host.y + host.height - (content.y + content.height)),
      'the result code document should reach its result root bottom',
    ).toBeLessThanOrEqual(1)
    const codeMirrorEditor = codeDocument.locator('.cm-editor')
    const monacoEditor = codeDocument.locator('.monaco-editor')
    const codeMirrorCount = await codeMirrorEditor.count()
    const monacoCount = await monacoEditor.count()
    expect(
      codeMirrorCount + monacoCount,
      'the result code document should contain exactly one editor',
    ).toBe(1)

    const editor = await visibleBox(
      codeMirrorCount === 1 ? codeMirrorEditor : monacoEditor,
      codeMirrorCount === 1 ? 'result CodeMirror editor' : 'result Monaco editor',
    )
    expect(
      Math.abs(editor.y - host.y),
      'the result editor should start at its host top',
    ).toBeLessThanOrEqual(1)
    expect(
      Math.abs(editor.y + editor.height - (host.y + host.height)),
      'the result editor should reach its host bottom',
    ).toBeLessThanOrEqual(1)

    if (codeMirrorCount === 1) {
      const scroller = await visibleBox(
        codeDocument.locator('.cm-scroller'),
        'result CodeMirror scroller',
      )
      expect(
        Math.abs(scroller.y + scroller.height - (host.y + host.height)),
        'the result editor scroller should reach its host bottom',
      ).toBeLessThanOrEqual(1)
    }
  }

  const copy = page.getByRole('button', { name: 'Copy output' })
  const copyBox = await visibleBox(copy, 'copy output action')
  expect(toolbar.height, 'result tabs and actions should use one compact row').toBeLessThanOrEqual(
    32,
  )
  expect(
    Math.abs(tabs.y - toolbar.y),
    'result tabs should share the action row',
  ).toBeLessThanOrEqual(1)
  expect(
    Math.abs(tabs.y + tabs.height / 2 - (copyBox.y + copyBox.height / 2)),
    'result tabs and the copy action should be vertically aligned',
  ).toBeLessThanOrEqual(1)
  expect(copyBox.y).toBeGreaterThanOrEqual(toolbar.y - 0.5)
  expect(copyBox.y + copyBox.height).toBeLessThanOrEqual(toolbar.y + toolbar.height + 0.5)
  await expect(page.locator('.result-header')).toHaveCount(0)
}

export async function expectCodeDocumentHorizontalScrollAtPanelBottom(
  page: Page,
  ariaLabel: string,
) {
  const document = page.getByRole('textbox', { name: ariaLabel, exact: true })
  await expect(document).toBeVisible()
  const metrics = await document.evaluate((content) => {
    const host = content.closest('.code-document-view')
    const panel = content.closest('.result-tab-panel')
    const editor = host?.querySelector('.cm-editor')
    const scroller = host?.querySelector('.cm-scroller')
    const line = host?.querySelector('.cm-line')
    if (!(host instanceof HTMLElement)) throw new Error('Result code document host is missing.')
    if (!(panel instanceof HTMLElement)) throw new Error('Result tab panel is missing.')
    if (!(editor instanceof HTMLElement)) throw new Error('Result CodeMirror editor is missing.')
    if (!(scroller instanceof HTMLElement))
      throw new Error('Result CodeMirror scroller is missing.')
    if (!(line instanceof HTMLElement)) throw new Error('Result CodeMirror line is missing.')

    const panelRect = panel.getBoundingClientRect()
    const hostRect = host.getBoundingClientRect()
    const editorRect = editor.getBoundingClientRect()
    const scrollerRect = scroller.getBoundingClientRect()
    return {
      panelBottom: panelRect.bottom,
      hostTop: hostRect.top,
      hostBottom: hostRect.bottom,
      editorTop: editorRect.top,
      editorBottom: editorRect.bottom,
      scrollerBottom: scrollerRect.bottom,
      clientWidth: scroller.clientWidth,
      scrollWidth: scroller.scrollWidth,
      overflowX: getComputedStyle(scroller).overflowX,
      whiteSpace: getComputedStyle(line).whiteSpace,
      wraps: editor.classList.contains('cm-lineWrapping'),
    }
  })

  expect(metrics.wraps).toBe(false)
  expect(metrics.whiteSpace).toBe('pre')
  expect(metrics.overflowX).toMatch(/auto|scroll/)
  expect(metrics.scrollWidth).toBeGreaterThan(metrics.clientWidth)
  expect(Math.abs(metrics.hostTop - metrics.editorTop)).toBeLessThanOrEqual(1)
  expect(Math.abs(metrics.hostBottom - metrics.editorBottom)).toBeLessThanOrEqual(1)
  expect(Math.abs(metrics.panelBottom - metrics.scrollerBottom)).toBeLessThanOrEqual(1)
}

export async function expectCodeDocumentHasNoVerticalOverflow(page: Page, ariaLabel: string) {
  const document = page.getByRole('textbox', { name: ariaLabel, exact: true })
  await expect(document).toBeVisible()
  const metrics = await document.evaluate((content) => {
    const scroller = content.closest('.code-document-view')?.querySelector('.cm-scroller')
    if (!(scroller instanceof HTMLElement)) {
      throw new Error('Result CodeMirror scroller is missing.')
    }
    return {
      clientHeight: scroller.clientHeight,
      scrollHeight: scroller.scrollHeight,
      overflowY: getComputedStyle(scroller).overflowY,
    }
  })

  expect(metrics.overflowY).toMatch(/auto|scroll/)
  expect(metrics.scrollHeight).toBeLessThanOrEqual(metrics.clientHeight + 1)
}

export async function waitForCompletedOperation(page: Page) {
  await expect(page.locator('.operation-state')).toHaveText('completed', { timeout: 90_000 })
  await expect(page.locator('.result-error')).toHaveCount(0)
}
