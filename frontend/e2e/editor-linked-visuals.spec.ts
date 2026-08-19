import { inflateSync } from 'node:zlib'
import { expect, type Locator, type Page, type TestInfo, test } from '@playwright/test'
import {
  editorHost,
  expectActiveEditor,
  openWorkbench,
  replaceSource,
  switchEditor,
  waitForCompletedOperation,
  waitForLanguageServiceReady,
} from './helpers/workbench'

type ResultEditorKind = 'monaco' | 'codemirror'

const linkedSource = `using System;

public static class VisualProbe
{
    public static int Sum(int a, int b) => a + b;
    public static double Scale(double value) => value * 2;

    public static void Main()
    {
        int total = 0;
        for (var i = 0; i < 2; i++) total += i;
        Console.WriteLine(total + Sum(20, 22));
        Console.WriteLine(Scale(2.5));
    }
}
`

const jitColumnSource = `using System;
using System.Runtime.CompilerServices;

public static class JitColumnProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int SameLineFor(int input)
    {
        var total = 0;
        for (var i = input; i < input + 3; i++) total += i;
        return total;
    }

    public static void Main() => Console.WriteLine(SameLineFor(Environment.TickCount));
}
`

const longJitSource = `using System;
using System.Runtime.CompilerServices;

public static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step01(int value) => value + 1;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step02(int value) => value + 2;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step03(int value) => value + 3;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step04(int value) => value + 4;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step05(int value) => value + 5;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step06(int value) => value + 6;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step07(int value) => value + 7;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step08(int value) => value + 8;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step09(int value) => value + 9;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Step10(int value) => value + 10;

    public static void Main()
    {
        int value = 0;
        value = Step01(value);
        value = Step02(value);
        value = Step03(value);
        value = Step04(value);
        value = Step05(value);
        value = Step06(value);
        value = Step07(value);
        value = Step08(value);
        value = Step09(value);
        Console.WriteLine(Step10(value));
    }
}
`

const decompiledColorSource = `using System;

public sealed class VisualProbe
{
    public static int Convert(double value, string text) => (int)value + text.Length;
    public static void Reset() => Console.WriteLine(typeof(VisualProbe));
    public static void Main() => Reset();
}
`

async function selectOutput(page: Page, value: string) {
  const desktop = page.getByLabel('Output', { exact: true })
  if (await desktop.isVisible()) {
    await desktop.selectOption(value)
    return
  }
  await page.getByRole('combobox', { name: 'View', exact: true }).selectOption(value)
}

async function runOperation(page: Page, action: string) {
  const button = page.getByRole('button', { name: action, exact: true })
  await expect(button).toBeEnabled({ timeout: 30_000 })
  await button.click()
  await waitForCompletedOperation(page)
}

async function capture(page: Page, testInfo: TestInfo, name: string) {
  const path = testInfo.outputPath(name)
  await page.screenshot({ path, fullPage: true })
  await testInfo.attach(name, { path, contentType: 'image/png' })
}

function sourceAssociationLines(page: Page, editor: ResultEditorKind): Locator {
  return editor === 'monaco'
    ? editorHost(page, 'monaco').locator('.monaco-editor .monaco-source-association-line')
    : editorHost(page, 'codemirror').locator('.cm-line.cm-source-association-line')
}

function activeSourceAssociationLines(page: Page, editor: ResultEditorKind): Locator {
  return editor === 'monaco'
    ? editorHost(page, 'monaco').locator('.monaco-source-association-line-active')
    : editorHost(page, 'codemirror').locator('.cm-source-association-line-active')
}

function preciseSourceAssociationMarks(page: Page, editor: ResultEditorKind): Locator {
  return editor === 'monaco'
    ? editorHost(page, 'monaco').locator('.monaco-source-association-exact-active')
    : editorHost(page, 'codemirror').locator('.cm-source-association-exact-active')
}

function outputAssociationLines(page: Page, editor: ResultEditorKind): Locator {
  return editor === 'monaco'
    ? page.locator(
        '.code-document-view.monaco-code-document .monaco-output-source-navigable.source-association',
      )
    : page.locator('.code-document-view .cm-line.cm-source-navigable.source-association')
}

function activeOutputAssociationLines(page: Page, editor: ResultEditorKind): Locator {
  return editor === 'monaco'
    ? page.locator('.monaco-code-document .monaco-output-source-active')
    : page.locator('.code-document-view .cm-source-association-active')
}

async function expectWholeLineSourceAssociation(page: Page, editor: ResultEditorKind) {
  const lines = sourceAssociationLines(page, editor)
  await expect(lines.first()).toBeVisible({ timeout: 30_000 })
  await expect(activeSourceAssociationLines(page, editor)).toHaveCount(0)
  await expect(preciseSourceAssociationMarks(page, editor)).toHaveCount(0)

  const host = editorHost(page, editor)
  await expect(
    host.locator(editor === 'monaco' ? '.monaco-source-association' : '.cm-source-association'),
  ).toHaveCount(0)

  const metrics = await lines.first().evaluate((line, kind) => {
    const content =
      kind === 'monaco'
        ? line.closest('.monaco-editor')?.querySelector('.view-lines')
        : line.closest('.cm-content')
    if (!(content instanceof HTMLElement)) {
      throw new Error('The associated source line is missing its editor content container.')
    }
    const lineRect = line.getBoundingClientRect()
    const contentRect = content.getBoundingClientRect()
    return {
      tagName: line.tagName,
      hasInlineAssociation:
        line.querySelector(
          kind === 'monaco' ? '.monaco-source-association' : '.cm-source-association',
        ) !== null,
      backgroundColor: getComputedStyle(line).backgroundColor,
      lineWidth: lineRect.width,
      contentWidth: contentRect.width,
      leftDelta: Math.abs(lineRect.left - contentRect.left),
    }
  }, editor)

  expect(metrics.tagName).toBe('DIV')
  expect(metrics.hasInlineAssociation).toBe(false)
  expect(metrics.backgroundColor).not.toBe('rgba(0, 0, 0, 0)')
  expect(metrics.backgroundColor).not.toBe('transparent')
  expect(metrics.leftDelta).toBeLessThanOrEqual(1)
  expect(metrics.lineWidth + 1).toBeGreaterThanOrEqual(metrics.contentWidth)
}

async function expectPreciseActiveSourceAssociation(
  page: Page,
  editor: ResultEditorKind,
): Promise<{ x: number; y: number }> {
  const exact = preciseSourceAssociationMarks(page, editor)
  await expect(exact.first()).toBeVisible({ timeout: 5_000 })
  const exactRects = await exact.evaluateAll((elements) =>
    elements
      .map((element) => element.getBoundingClientRect())
      .filter((rect) => rect.width > 0 && rect.height > 0)
      .map((rect) => ({
        left: rect.left,
        right: rect.right,
        top: rect.top,
        bottom: rect.bottom,
        width: rect.width,
      })),
  )
  expect(exactRects.length).toBeGreaterThan(0)
  const first = exactRects[0]
  if (!first) throw new Error('The precise source association has no visible rectangle.')
  const sameLine = exactRects
    .filter((rect) => Math.abs(rect.top - first.top) <= 1)
    .sort((left, right) => left.left - right.left)
  for (let index = 1; index < sameLine.length; index += 1) {
    const previous = sameLine[index - 1]
    const current = sameLine[index]
    if (!previous || !current) continue
    expect(
      current.left - previous.right,
      'The precise source mark has a token gap',
    ).toBeLessThanOrEqual(1.5)
  }

  const lineMetrics = await activeSourceAssociationLines(page, editor).evaluateAll(
    (lines, targetTop) => {
      const line = lines.find((candidate) => {
        const rect = candidate.getBoundingClientRect()
        return rect.top <= targetTop + 1 && rect.bottom >= targetTop + 1
      })
      if (!(line instanceof HTMLElement)) {
        throw new Error('The precise source mark has no active whole-line association.')
      }
      const rect = line.getBoundingClientRect()
      return { left: rect.left, right: rect.right, width: rect.width }
    },
    first.top,
  )
  const preciseLeft = Math.min(...sameLine.map((rect) => rect.left))
  const preciseRight = Math.max(...sameLine.map((rect) => rect.right))
  expect(preciseLeft).toBeGreaterThanOrEqual(lineMetrics.left - 1)
  expect(preciseRight).toBeLessThanOrEqual(lineMetrics.right + 1)
  expect(preciseRight - preciseLeft).toBeLessThan(lineMetrics.width * 0.9)

  const style = await exact.first().evaluate((element) => {
    const computed = getComputedStyle(element)
    return {
      background: computed.backgroundColor,
      borderRadius: computed.borderRadius,
      boxShadow: computed.boxShadow,
      outlineStyle: computed.outlineStyle,
    }
  })
  expect(style.background).not.toBe('rgba(0, 0, 0, 0)')
  expect(style.borderRadius).toBe('0px')
  expect(style.boxShadow).toBe('none')
  expect(style.outlineStyle).toBe('none')

  return {
    x: preciseLeft + (preciseRight - preciseLeft) / 2,
    y: first.bottom - 2,
  }
}

interface RgbPixel {
  red: number
  green: number
  blue: number
}

function paeth(left: number, up: number, upperLeft: number): number {
  const estimate = left + up - upperLeft
  const leftDistance = Math.abs(estimate - left)
  const upDistance = Math.abs(estimate - up)
  const upperLeftDistance = Math.abs(estimate - upperLeft)
  if (leftDistance <= upDistance && leftDistance <= upperLeftDistance) return left
  return upDistance <= upperLeftDistance ? up : upperLeft
}

function pngPixelAtCssPoint(
  png: Buffer,
  point: { x: number; y: number },
  viewport: { width: number; height: number },
): RgbPixel {
  let offset = 8
  let width = 0
  let height = 0
  let bytesPerPixel = 0
  const compressed: Buffer[] = []
  while (offset < png.length) {
    const length = png.readUInt32BE(offset)
    const type = png.toString('ascii', offset + 4, offset + 8)
    const dataStart = offset + 8
    if (type === 'IHDR') {
      width = png.readUInt32BE(dataStart)
      height = png.readUInt32BE(dataStart + 4)
      const bitDepth = png[dataStart + 8]
      const colorType = png[dataStart + 9]
      if (bitDepth !== 8 || (colorType !== 2 && colorType !== 6)) {
        throw new Error(`Unsupported screenshot PNG format: depth ${bitDepth}, color ${colorType}.`)
      }
      bytesPerPixel = colorType === 6 ? 4 : 3
    } else if (type === 'IDAT') {
      compressed.push(png.subarray(dataStart, dataStart + length))
    } else if (type === 'IEND') {
      break
    }
    offset = dataStart + length + 4
  }
  if (width <= 0 || height <= 0 || bytesPerPixel === 0) {
    throw new Error('Screenshot PNG is missing a supported IHDR chunk.')
  }

  const packed = inflateSync(Buffer.concat(compressed))
  const stride = width * bytesPerPixel
  const pixels = new Uint8Array(stride * height)
  for (let row = 0; row < height; row += 1) {
    const packedStart = row * (stride + 1)
    const filter = packed[packedStart]
    for (let column = 0; column < stride; column += 1) {
      const raw = packed[packedStart + 1 + column] ?? 0
      const outputIndex = row * stride + column
      const left = column >= bytesPerPixel ? (pixels[outputIndex - bytesPerPixel] ?? 0) : 0
      const up = row > 0 ? (pixels[outputIndex - stride] ?? 0) : 0
      const upperLeft =
        row > 0 && column >= bytesPerPixel ? (pixels[outputIndex - stride - bytesPerPixel] ?? 0) : 0
      const predictor =
        filter === 0
          ? 0
          : filter === 1
            ? left
            : filter === 2
              ? up
              : filter === 3
                ? Math.floor((left + up) / 2)
                : filter === 4
                  ? paeth(left, up, upperLeft)
                  : Number.NaN
      if (!Number.isFinite(predictor)) throw new Error(`Unsupported PNG row filter ${filter}.`)
      pixels[outputIndex] = (raw + predictor) & 0xff
    }
  }

  const x = Math.min(width - 1, Math.max(0, Math.floor((point.x / viewport.width) * width)))
  const y = Math.min(height - 1, Math.max(0, Math.floor((point.y / viewport.height) * height)))
  const pixel = y * stride + x * bytesPerPixel
  return {
    red: pixels[pixel] ?? 0,
    green: pixels[pixel + 1] ?? 0,
    blue: pixels[pixel + 2] ?? 0,
  }
}

async function sampleViewportPixel(page: Page, point: { x: number; y: number }): Promise<RgbPixel> {
  const viewport = page.viewportSize()
  if (!viewport) throw new Error('The Playwright viewport is unavailable.')
  return pngPixelAtCssPoint(await page.screenshot(), point, viewport)
}

function pixelDistance(left: RgbPixel, right: RgbPixel): number {
  return Math.hypot(left.red - right.red, left.green - right.green, left.blue - right.blue)
}

async function expectSourceSelectionAboveAssociations(page: Page, editor: ResultEditorKind) {
  const viewport = page.viewportSize()
  if (!viewport) throw new Error('The Playwright viewport is unavailable.')
  const origin = new URL(page.url()).origin
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write'], { origin })
  const host = editorHost(page, editor)
  const activeMark = preciseSourceAssociationMarks(page, editor).first()
  await expect(activeMark).toBeVisible()
  const activeMarkRect = await activeMark.boundingBox()
  if (!activeMarkRect) throw new Error('The exact source association has no visible rectangle.')
  const activeMarkPoint = {
    x: activeMarkRect.x + activeMarkRect.width / 2,
    y: activeMarkRect.y + activeMarkRect.height / 2,
  }
  await page.mouse.click(activeMarkPoint.x, activeMarkPoint.y)
  if (editor === 'monaco') {
    const activeElement = await page.evaluate(() => ({
      tagName: document.activeElement?.tagName ?? null,
      className:
        document.activeElement instanceof HTMLElement ? document.activeElement.className : null,
    }))
    expect(
      activeElement.className?.includes('inputarea') ||
        activeElement.className?.includes('native-edit-context'),
      `The source click did not give Monaco text focus: ${JSON.stringify(activeElement)}`,
    ).toBe(true)
  }
  const selectionLayers =
    editor === 'monaco'
      ? host.locator('.view-overlays .selected-text')
      : host.locator('.cm-selectionLayer .cm-selectionBackground')
  await expect(selectionLayers).toHaveCount(0)
  await expect(preciseSourceAssociationMarks(page, editor).first()).not.toHaveCSS(
    'background-color',
    'rgba(0, 0, 0, 0)',
  )
  const beforeSelectionPng = await page.screenshot()
  await page.keyboard.press('Control+A')
  await page.keyboard.press('Control+C')

  const selection = selectionLayers.first()
  await expect(selection).toBeVisible()
  const selectionBackground = await selection.evaluate(
    (element) => getComputedStyle(element).backgroundColor,
  )
  expect(selectionBackground).not.toBe('rgba(0, 0, 0, 0)')
  const overlap = await page.evaluate(
    ({ editorKind, fallback }) => {
      const host = document.querySelector(
        editorKind === 'monaco' ? '[data-editor="monaco"]' : '[data-editor="codemirror"]',
      )
      if (!(host instanceof HTMLElement)) return { point: fallback, found: false }
      const exact = [
        ...host.querySelectorAll<HTMLElement>(
          editorKind === 'monaco'
            ? '.monaco-source-association-exact-active'
            : '.cm-source-association-exact-active',
        ),
      ].map((element) => element.getBoundingClientRect())
      const selections = [
        ...host.querySelectorAll<HTMLElement>(
          editorKind === 'monaco'
            ? '.view-overlays .selected-text'
            : '.cm-selectionLayer .cm-selectionBackground',
        ),
      ].map((element) => element.getBoundingClientRect())
      for (const exactRect of exact) {
        for (const selectionRect of selections) {
          const left = Math.max(exactRect.left, selectionRect.left)
          const right = Math.min(exactRect.right, selectionRect.right)
          const top = Math.max(exactRect.top, selectionRect.top)
          const bottom = Math.min(exactRect.bottom, selectionRect.bottom)
          if (right - left >= 2 && bottom - top >= 2) {
            return { point: { x: (left + right) / 2, y: (top + bottom) / 2 }, found: true }
          }
        }
      }
      return { point: fallback, found: false }
    },
    { editorKind: editor, fallback: activeMarkPoint },
  )
  expect(overlap.found, 'The exact association and selection rectangles do not overlap.').toBe(true)
  const afterSelectionPng = await page.screenshot()
  const beforeSelection = pngPixelAtCssPoint(beforeSelectionPng, overlap.point, viewport)
  const afterSelection = pngPixelAtCssPoint(afterSelectionPng, overlap.point, viewport)
  expect(
    pixelDistance(beforeSelection, afterSelection),
    'The real selection did not visibly replace the association color at the sampled source pixel.',
  ).toBeGreaterThan(12)

  const copied = await page.evaluate(() => navigator.clipboard.readText())
  expect(copied.replaceAll('\r\n', '\n').trimEnd()).toBe(linkedSource.trimEnd())
  await expect(preciseSourceAssociationMarks(page, editor).first()).toBeVisible()
}

async function textPointForAssociation(
  association: Locator,
  editor: ResultEditorKind,
): Promise<{ x: number; y: number; color: string }> {
  return association.evaluate((element, kind) => {
    const associationRect = element.getBoundingClientRect()
    const root = element.closest(kind === 'monaco' ? '.monaco-editor' : '.cm-editor')
    if (!(root instanceof HTMLElement)) throw new Error('The result editor root is missing.')
    const candidates = [
      ...root.querySelectorAll<HTMLElement>(
        kind === 'monaco' ? '.view-line span' : '.cm-line span',
      ),
    ]
      .filter(
        (candidate) =>
          candidate.children.length === 0 && (candidate.textContent ?? '').trim().length > 0,
      )
      .filter((candidate) => {
        const rect = candidate.getBoundingClientRect()
        return (
          rect.top < associationRect.bottom && rect.bottom > associationRect.top && rect.width > 2
        )
      })
    const token = candidates[Math.min(1, candidates.length - 1)]
    if (!token) throw new Error('The associated output line has no visible token.')
    const rect = token.getBoundingClientRect()
    return {
      x: rect.left + rect.width / 2,
      y: rect.top + rect.height / 2,
      color: getComputedStyle(token).color,
    }
  }, editor)
}

async function activateOutputAssociation(
  page: Page,
  editor: ResultEditorKind,
  association: Locator,
) {
  const point = await textPointForAssociation(association, editor)
  await page.mouse.click(point.x, point.y)
}

async function hoverOutputAssociationForExactText(
  page: Page,
  editor: ResultEditorKind,
  expectedText: string,
): Promise<{ x: number; y: number }> {
  const scroller = page.locator(
    editor === 'monaco'
      ? '.monaco-code-document .monaco-scrollable-element'
      : '.code-document-view .cm-scroller',
  )
  const fractions = [0, 0.12, 0.24, 0.4, 0.58, 0.76, 0.92]
  for (const fraction of fractions) {
    await scroller.first().evaluate((element, nextFraction) => {
      element.scrollTop = Math.floor((element.scrollHeight - element.clientHeight) * nextFraction)
      element.dispatchEvent(new Event('scroll', { bubbles: true }))
    }, fraction)
    await page.waitForTimeout(60)
    const associations = outputAssociationLines(page, editor)
    const count = await associations.count()
    for (let index = 0; index < count; index += 1) {
      const association = associations.nth(index)
      if (!(await association.isVisible())) continue
      const point = await textPointForAssociation(association, editor)
      await page.mouse.move(point.x, point.y)
      await page.waitForTimeout(20)
      const activeText = await preciseSourceAssociationMarks(page, editor).evaluateAll((marks) =>
        marks.map((mark) => mark.textContent ?? '').join(''),
      )
      if (activeText.replaceAll(/\s/g, '') === expectedText.replaceAll(/\s/g, '')) return point
    }
  }
  throw new Error(`No visible output association activated the exact source text ${expectedText}.`)
}

async function visibleCodeMirrorAssociationPoint(page: Page): Promise<{ x: number; y: number }> {
  return outputAssociationLines(page, 'codemirror').evaluateAll((lines) => {
    const scroller = document.querySelector('.jit-view .code-document-view .cm-scroller')
    if (!(scroller instanceof HTMLElement)) throw new Error('JIT scroller is missing.')
    const scrollerRect = scroller.getBoundingClientRect()
    const line = lines.find((candidate) => {
      const rect = candidate.getBoundingClientRect()
      return rect.top >= scrollerRect.top && rect.bottom <= scrollerRect.bottom
    })
    if (!(line instanceof HTMLElement)) {
      throw new Error('No linked JIT line is visible at the manual scroll position.')
    }
    const token = [...line.querySelectorAll<HTMLElement>('span')].find((candidate) => {
      const rect = candidate.getBoundingClientRect()
      return (
        candidate.children.length === 0 &&
        (candidate.textContent ?? '').trim().length > 0 &&
        rect.width > 2
      )
    })
    const rect = (token ?? line).getBoundingClientRect()
    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }
  })
}

async function expectCodeMirrorFoldDoesNotNavigate(page: Page, mobile: boolean) {
  const gutter = page.locator('.code-document-view .cm-foldGutter')
  await expect(gutter).toBeVisible()
  const width = await gutter.evaluate((element) => element.getBoundingClientRect().width)
  expect(width).toBeGreaterThanOrEqual(12)
  expect(width).toBeLessThanOrEqual(14)
  if (mobile) {
    await expect(editorHost(page, 'codemirror').locator('.cm-foldGutter')).toBeHidden()
  }

  const control = gutter.locator('.cm-gutterElement').filter({ hasText: '⌄' }).first()
  await expect(control).toBeVisible()
  const scroller = page.locator('.code-document-view .cm-scroller')
  const scrollTopBefore = await scroller.evaluate((element) => element.scrollTop)
  await control.click()
  await expect(page.locator('.code-document-view .cm-foldPlaceholder').first()).toBeVisible()
  await expect(activeSourceAssociationLines(page, 'codemirror')).toHaveCount(0)
  await expect(activeOutputAssociationLines(page, 'codemirror')).toHaveCount(0)
  expect(await scroller.evaluate((element) => element.scrollTop)).toBe(scrollTopBefore)

  const collapsedControlPoint = await gutter
    .locator('.cm-gutterElement')
    .evaluateAll((controls) => {
      const control = controls.find((candidate) => {
        const rect = candidate.getBoundingClientRect()
        return (candidate.textContent ?? '').trim().length > 0 && rect.width > 0 && rect.height > 0
      })
      if (!(control instanceof HTMLElement))
        throw new Error('The unfolded gutter control is missing.')
      const rect = control.getBoundingClientRect()
      return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }
    })
  await page.mouse.click(collapsedControlPoint.x, collapsedControlPoint.y)
  await expect(page.locator('.code-document-view .cm-foldPlaceholder')).toHaveCount(0)
}

async function expectMonacoFoldDoesNotNavigate(page: Page) {
  const expanded = page.locator('.monaco-code-document .codicon-folding-expanded').first()
  await expect(expanded).toBeVisible()
  await expanded.click()
  const collapsed = page.locator('.monaco-code-document .codicon-folding-collapsed').first()
  await expect(collapsed).toBeVisible()
  await expect(activeSourceAssociationLines(page, 'monaco')).toHaveCount(0)
  await expect(activeOutputAssociationLines(page, 'monaco')).toHaveCount(0)
  await collapsed.click()
  await expect(
    page.locator('.monaco-code-document .codicon-folding-expanded').first(),
  ).toBeVisible()
}

async function tokenColors(page: Page, editor: ResultEditorKind, token: string): Promise<string[]> {
  const spans =
    editor === 'monaco'
      ? page.locator('.monaco-code-document .view-line span')
      : page.locator('.code-document-view .cm-line span')
  return spans.evaluateAll((candidates, expected) => {
    return candidates
      .filter((span) => span.children.length === 0 && (span.textContent ?? '').trim() === expected)
      .map((span) => getComputedStyle(span).color)
  }, token)
}

async function sourceTokenColors(
  page: Page,
  editor: ResultEditorKind,
  token: string,
): Promise<string[]> {
  const spans =
    editor === 'monaco'
      ? editorHost(page, 'monaco').locator('.view-lines .view-line span')
      : editorHost(page, 'codemirror').locator('.cm-line span')
  return spans.evaluateAll((candidates, expected) => {
    return candidates
      .filter((span) => span.children.length === 0 && (span.textContent ?? '').trim() === expected)
      .map((span) => getComputedStyle(span).color)
  }, token)
}

async function expectHeaderGeometry(page: Page, width: number) {
  await page.setViewportSize({ width, height: width <= 520 ? 915 : 700 })
  const compact = width <= 1100
  const run = page.locator(
    compact ? '.mobile-command-bar .run-button' : '.selector-group--result > .run-button',
  )
  const actions = page.locator('.app-bar-actions')
  await expect(run).toBeVisible()
  await expect(actions).toBeVisible()
  const [runRect, actionRect, brandRect] = await Promise.all([
    run.boundingBox(),
    actions.boundingBox(),
    page.locator('.brand').boundingBox(),
  ])
  if (!runRect || !actionRect || !brandRect) {
    throw new Error(`The ${width}px header is missing a geometry rectangle.`)
  }
  expect(runRect.x + runRect.width, `${width}px Render overlaps app actions`).toBeLessThanOrEqual(
    actionRect.x + 0.5,
  )
  expect(brandRect.x + brandRect.width, `${width}px brand overlaps Render`).toBeLessThanOrEqual(
    runRect.x + 0.5,
  )
  expect(
    actionRect.x + actionRect.width,
    `${width}px actions exceed the viewport`,
  ).toBeLessThanOrEqual(width)

  const overflow = await page.evaluate(() => ({
    document: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    body: document.body.scrollWidth - document.documentElement.clientWidth,
  }))
  expect(overflow.document, `${width}px document overflow`).toBeLessThanOrEqual(0)
  expect(overflow.body, `${width}px body overflow`).toBeLessThanOrEqual(0)
  if (compact) await expect(page.locator('.selector-bar')).toBeHidden()
  else await expect(page.locator('.selector-bar')).toBeVisible()

  const [sourceRect, resultRect] = await Promise.all([
    page.locator('.source-pane').boundingBox(),
    page.locator('.result-pane').boundingBox(),
  ])
  if (!sourceRect || !resultRect) throw new Error(`The ${width}px workbench panes are missing.`)
  if (width > 860) {
    expect(
      Math.abs(sourceRect.y - resultRect.y),
      `${width}px panes should remain horizontal`,
    ).toBeLessThanOrEqual(1)
    expect(sourceRect.x + sourceRect.width).toBeLessThanOrEqual(resultRect.x + 1)
  } else {
    expect(resultRect.y, `${width}px result pane should be below source`).toBeGreaterThanOrEqual(
      sourceRect.y + sourceRect.height,
    )
  }
}

test.describe('linked editor visual regressions', () => {
  test('keeps Render and app actions separate at responsive boundaries and disconnect', async ({
    page,
    isMobile,
  }, testInfo) => {
    test.skip(isMobile, 'One desktop context resizes through every responsive boundary.')
    test.setTimeout(120_000)
    await openWorkbench(page)

    const widths = [1440, 1101, 1100, 1024, 861, 860, 412]
    for (const width of widths) await expectHeaderGeometry(page, width)

    await page.routeWebSocket('**/api/v1/operations/ws', (socket) =>
      socket.close({ code: 1011, reason: 'responsive disconnected-state acceptance' }),
    )
    await page.reload()
    await expect(page.getByLabel('Language')).toBeEnabled()
    await expect(page.locator('.app-health[data-state="error"]')).toBeVisible({ timeout: 10_000 })
    for (const width of widths.toReversed()) await expectHeaderGeometry(page, width)
    await page.setViewportSize({ width: 1024, height: 700 })
    await capture(page, testInfo, 'tablet-disconnected-header.png')
  })

  test('keeps new IL associations inactive until navigation and paints whole source lines', async ({
    page,
    isMobile,
  }, testInfo) => {
    const editor: ResultEditorKind = isMobile ? 'codemirror' : 'monaco'
    await openWorkbench(page)
    await expectActiveEditor(page, editor)
    await replaceSource(page, linkedSource)
    await selectOutput(page, 'il')
    await runOperation(page, 'Render IL')
    await expect(page.locator('.code-document-view')).toContainText('.method')
    await expect(page.locator('.code-document-view')).not.toContainText(/sequence point:/i)

    const origin = new URL(page.url()).origin
    await page.context().grantPermissions(['clipboard-read', 'clipboard-write'], { origin })
    await page.getByRole('button', { name: 'Copy output' }).click()
    const copiedIl = await page.evaluate(() => navigator.clipboard.readText())
    expect(copiedIl).toContain('.method')
    expect(copiedIl).not.toMatch(/sequence point:/i)

    await expectWholeLineSourceAssociation(page, editor)
    await expect(activeOutputAssociationLines(page, editor)).toHaveCount(0)
    if (editor === 'monaco') await expectMonacoFoldDoesNotNavigate(page)
    else await expectCodeMirrorFoldDoesNotNavigate(page, isMobile)

    const outputPoint = await hoverOutputAssociationForExactText(page, editor, 'i++')
    await expect(activeSourceAssociationLines(page, editor).first()).toBeVisible({ timeout: 5_000 })
    const hoverPrecisePoint = await expectPreciseActiveSourceAssociation(page, editor)
    expect(
      await preciseSourceAssociationMarks(page, editor).evaluateAll((marks) =>
        marks.map((mark) => mark.textContent ?? '').join(''),
      ),
    ).toBe('i++')
    const activeLineRect = await activeSourceAssociationLines(page, editor).first().boundingBox()
    if (!activeLineRect) throw new Error('The hovered source association line has no rectangle.')
    const wholeLinePoint = {
      x: activeLineRect.x + 4,
      y: activeLineRect.y + activeLineRect.height / 2,
    }
    const hoveredWholeLinePixel = await sampleViewportPixel(page, wholeLinePoint)
    const hoveredExactPixel = await sampleViewportPixel(page, hoverPrecisePoint)
    const appBar = await page.locator('.app-bar').boundingBox()
    if (!appBar) throw new Error('The app bar has no geometry rectangle.')
    await page.mouse.move(appBar.x + appBar.width / 2, appBar.y + appBar.height / 2)
    await expect(preciseSourceAssociationMarks(page, editor)).toHaveCount(0)
    await expect(activeSourceAssociationLines(page, editor)).toHaveCount(0)
    await expect(sourceAssociationLines(page, editor).first()).toBeVisible()
    const idleWholeLinePixel = await sampleViewportPixel(page, wholeLinePoint)
    const idleExactPixel = await sampleViewportPixel(page, hoverPrecisePoint)
    expect(
      pixelDistance(idleWholeLinePixel, hoveredWholeLinePixel),
      'Hover must remove the whole-line source fill so only the exact span remains.',
    ).toBeGreaterThan(12)
    expect(
      pixelDistance(idleExactPixel, hoveredExactPixel),
      'Hover must visibly strengthen only the exact source span.',
    ).toBeGreaterThan(12)

    await page.mouse.click(outputPoint.x, outputPoint.y)
    await expect(activeSourceAssociationLines(page, editor).first()).toBeVisible({
      timeout: 5_000,
    })
    await expect(activeOutputAssociationLines(page, editor).first()).toBeVisible()
    await expect(preciseSourceAssociationMarks(page, editor).first()).toBeVisible()
    await expectSourceSelectionAboveAssociations(page, editor)
    await capture(
      page,
      testInfo,
      `${isMobile ? 'mobile-codemirror' : 'desktop-monaco'}-il-linked.png`,
    )
  })

  test('maps optimized JIT instructions to exact same-line source spans', async ({
    page,
    isMobile,
  }, testInfo) => {
    test.setTimeout(180_000)
    const editor: ResultEditorKind = isMobile ? 'codemirror' : 'monaco'
    await openWorkbench(page)
    await expectActiveEditor(page, editor)
    await replaceSource(page, jitColumnSource)
    await selectOutput(page, 'jit-asm')
    await runOperation(page, 'JIT')

    await expect(page.locator('.jit-view')).toContainText('SameLineFor')
    await expectWholeLineSourceAssociation(page, editor)
    await expect(activeOutputAssociationLines(page, editor)).toHaveCount(0)

    await hoverOutputAssociationForExactText(page, editor, 'i++')
    await expect(activeSourceAssociationLines(page, editor).first()).toBeVisible({ timeout: 5_000 })
    await expectPreciseActiveSourceAssociation(page, editor)
    expect(
      await preciseSourceAssociationMarks(page, editor).evaluateAll((marks) =>
        marks.map((mark) => mark.textContent ?? '').join(''),
      ),
    ).toBe('i++')

    await capture(
      page,
      testInfo,
      `${isMobile ? 'mobile-codemirror' : 'desktop-monaco'}-jit-exact-i-plus-plus.png`,
    )
  })

  test('does not snap CodeMirror JIT output back after a linked click and manual scroll', async ({
    page,
    isMobile,
  }, testInfo) => {
    test.skip(isMobile, 'The mobile project covers CodeMirror association rendering via IL.')
    test.setTimeout(180_000)

    await openWorkbench(page)
    await switchEditor(page, 'codemirror')
    await replaceSource(page, longJitSource)
    await selectOutput(page, 'jit-asm')
    await runOperation(page, 'JIT')

    await expect(page.getByLabel('JIT assembly')).toContainText('Step10')
    await expectWholeLineSourceAssociation(page, 'codemirror')
    await expect(activeOutputAssociationLines(page, 'codemirror')).toHaveCount(0)
    await expectCodeMirrorFoldDoesNotNavigate(page, false)

    const firstLinkedLine = outputAssociationLines(page, 'codemirror').first()
    await expect(firstLinkedLine).toBeVisible({ timeout: 30_000 })
    await activateOutputAssociation(page, 'codemirror', firstLinkedLine)

    const scroller = page.locator('.jit-view .code-document-view .cm-scroller')
    const immediateScrollTop = await scroller.evaluate((element) => {
      const maximum = element.scrollHeight - element.clientHeight
      if (maximum < 100) throw new Error('JIT output is not long enough for the scroll regression.')
      const target = Math.max(1, Math.floor(maximum * 0.72))
      element.scrollTop = target
      element.dispatchEvent(new Event('scroll', { bubbles: true }))
      return target
    })
    await expect
      .poll(() => scroller.evaluate((element) => element.scrollTop))
      .toBe(immediateScrollTop)
    const immediateScrollerBox = await scroller.boundingBox()
    if (!immediateScrollerBox) throw new Error('The JIT scroller has no visible layout box.')
    await page.mouse.move(
      immediateScrollerBox.x + Math.min(180, immediateScrollerBox.width / 2),
      immediateScrollerBox.y + immediateScrollerBox.height / 2,
    )
    await page.waitForTimeout(1_100)
    expect(await scroller.evaluate((element) => element.scrollTop)).toBe(immediateScrollTop)
    await expect(activeOutputAssociationLines(page, 'codemirror')).toHaveCount(0)

    const settledActivationPoint = await visibleCodeMirrorAssociationPoint(page)
    await page.mouse.click(settledActivationPoint.x, settledActivationPoint.y)
    await expect(activeOutputAssociationLines(page, 'codemirror').first()).toBeVisible({
      timeout: 5_000,
    })

    const settledScrollTop = await scroller.evaluate((element) => {
      const maximum = element.scrollHeight - element.clientHeight
      const target = Math.max(1, Math.floor(maximum * 0.32))
      element.scrollTop = target
      element.dispatchEvent(new Event('scroll', { bubbles: true }))
      return target
    })
    await expect
      .poll(() => scroller.evaluate((element) => element.scrollTop))
      .toBe(settledScrollTop)

    const hoverPoint = await visibleCodeMirrorAssociationPoint(page)
    await page.mouse.move(hoverPoint.x, hoverPoint.y)
    await page.waitForTimeout(1_000)

    const finalScrollTop = await scroller.evaluate((element) => element.scrollTop)
    expect(Math.abs(finalScrollTop - settledScrollTop)).toBeLessThanOrEqual(2)
    await capture(page, testInfo, 'desktop-codemirror-jit-no-snap.png')
  })

  test('uses VS keyword and user-type colors in decompiled C# for the active result editor', async ({
    page,
    isMobile,
  }) => {
    const editor: ResultEditorKind = isMobile ? 'codemirror' : 'monaco'
    await openWorkbench(page)
    await expectActiveEditor(page, editor)
    await replaceSource(page, decompiledColorSource)

    for (const keyword of ['using', 'public', 'static', 'int', 'double', 'string', 'void']) {
      await expect
        .poll(() => sourceTokenColors(page, editor, keyword), {
          message: `${keyword} should render in VS blue in the source editor`,
        })
        .toContain('rgb(0, 0, 255)')
    }

    await selectOutput(page, 'decompiled-csharp')
    await runOperation(page, 'Decompile')

    const document = page.locator('.code-document-view')
    await expect(document).toContainText('VisualProbe')
    for (const keyword of ['int', 'double', 'void', 'string']) {
      await expect
        .poll(() => tokenColors(page, editor, keyword), {
          message: `${keyword} should render in blue`,
        })
        .toContain('rgb(0, 0, 255)')
    }
    await expect
      .poll(() => tokenColors(page, editor, 'VisualProbe'), {
        message: 'The user type should render in VS teal',
      })
      .toContain('rgb(43, 145, 175)')
  })

  test('gives CodeMirror keyword completion a readable face and strong selected contrast', async ({
    page,
    isMobile,
  }) => {
    await openWorkbench(page, { waitForLsp: true })
    if (isMobile) await expectActiveEditor(page, 'codemirror')
    else await switchEditor(page, 'codemirror')
    await waitForLanguageServiceReady(page)
    await replaceSource(page, 'a')
    await page.keyboard.press('Escape')
    await page.keyboard.press('Control+Space')

    const completion = page.locator('.cm-tooltip-autocomplete')
    await expect(completion).toBeVisible({ timeout: 30_000 })
    const options = completion.getByRole('option')
    const keyword = options.filter({ has: page.locator('.cm-completionIcon-keyword') }).first()
    await expect(keyword).toBeVisible()

    const keywordIndex = await options.evaluateAll((rows) =>
      rows.findIndex((row) => row.querySelector('.cm-completionIcon-keyword') !== null),
    )
    expect(keywordIndex).toBeGreaterThanOrEqual(0)
    let selectedIndex = await options.evaluateAll((rows) =>
      rows.findIndex((row) => row.getAttribute('aria-selected') === 'true'),
    )
    const optionCount = await options.count()
    for (let step = 0; selectedIndex !== keywordIndex && step < optionCount; step += 1) {
      await page.keyboard.press('ArrowDown')
      selectedIndex = await options.evaluateAll((rows) =>
        rows.findIndex((row) => row.getAttribute('aria-selected') === 'true'),
      )
    }
    await expect(keyword).toHaveAttribute('aria-selected', 'true')

    const codeContent = editorHost(page, 'codemirror').locator('.cm-content')
    await expect(codeContent).toHaveCSS('font-family', /Cascadia Code/)
    await expect(codeContent).toHaveCSS('font-weight', '450')
    await expect(keyword).toHaveCSS('background-color', 'rgb(0, 103, 192)')
    await expect(keyword.locator('.cm-completionLabel')).toHaveCSS('color', 'rgb(255, 255, 255)')
    await expect(keyword.locator('.cm-completionIcon-keyword')).toHaveCSS(
      'color',
      'rgb(255, 255, 255)',
    )

    if (optionCount > 1) {
      await page.keyboard.press('ArrowDown')
      await expect(keyword).not.toHaveAttribute('aria-selected', 'true')
      await expect(keyword.locator('.cm-completionLabel')).toHaveCSS('color', 'rgb(0, 0, 255)')
      await expect(keyword.locator('.cm-completionIcon-keyword')).toHaveCSS(
        'color',
        'rgb(0, 0, 255)',
      )
    }
  })
})
