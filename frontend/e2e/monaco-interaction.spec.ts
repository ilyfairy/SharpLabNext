import { expect, test } from '@playwright/test'
import {
  editorHost,
  openWorkbench,
  replaceSource,
  sourceEditor,
  switchEditor,
} from './helpers/workbench'

const source = `using System;

int value = 1;
Console.WriteLine(value + 1);
`

test.describe('Monaco interaction stability', () => {
  test('copies exactly and replaces a native drag selection on the first key', async ({
    page,
    isMobile,
  }) => {
    test.skip(isMobile, 'Desktop native-input coverage.')
    await openWorkbench(page)
    await switchEditor(page, 'monaco')
    await replaceSource(page, source)

    const host = editorHost(page, 'monaco')
    await expect(host.locator('.native-edit-context')).toHaveCount(1)
    await expect(host.locator('textarea.inputarea')).toHaveCount(0)
    await expect(host.locator('.view-lines')).toContainText('Console.WriteLine(value + 1);')

    const editor = sourceEditor(page)
    await editor.click()
    await page.keyboard.press('ControlOrMeta+A')
    const origin = new URL(page.url()).origin
    await page.context().grantPermissions(['clipboard-read', 'clipboard-write'], { origin })
    await page.keyboard.press('ControlOrMeta+C')
    await expect
      .poll(() =>
        page.evaluate(() =>
          navigator.clipboard.readText().then((text) => text.replaceAll('\r\n', '\n')),
        ),
      )
      .toBe(source)
    await page.keyboard.press('ArrowRight')

    await page.getByLabel('Output', { exact: true }).selectOption('ast')
    await page.getByRole('button', { name: 'Build AST', exact: true }).click()
    await expect(page.getByRole('tree', { name: 'Abstract syntax tree' })).toBeVisible({
      timeout: 90_000,
    })

    const line = host.locator('.view-line').filter({ hasText: 'Console.WriteLine(value + 1);' })
    const points = await line.evaluate((element) => {
      const leaves = Array.from(element.querySelectorAll<HTMLElement>('span')).filter(
        (candidate) => candidate.children.length === 0,
      )
      const start = leaves.find((candidate) => candidate.textContent === 'WriteLine')
      const end = leaves.find((candidate) => candidate.textContent === '1')
      if (!start || !end) {
        throw new Error('Expected Monaco drag-selection tokens were not rendered.')
      }
      const startRect = start.getBoundingClientRect()
      const endRect = end.getBoundingClientRect()
      return {
        start: { x: startRect.left + 1, y: startRect.top + startRect.height / 2 },
        end: { x: endRect.right - 1, y: endRect.top + endRect.height / 2 },
      }
    })
    await page.mouse.move(points.start.x, points.start.y)
    await page.mouse.down()
    await page.mouse.move(points.end.x, points.end.y, { steps: 8 })
    await page.mouse.up()
    await expect(host.locator('.selected-text').first()).toBeVisible()

    await page.keyboard.press('X')
    await expect(host.locator('.view-lines')).toContainText('Console.X);')
    await expect(host.locator('.selected-text')).toHaveCount(0)
  })
})
