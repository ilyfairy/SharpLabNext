import { expect, type Page, test } from '@playwright/test'
import { observeOperationWebSocket } from './helpers/workbench'

async function openWorkbench(page: Page) {
  await page.goto('/')
  await expect(page.getByLabel('Language')).toBeEnabled()
}

test.describe('Operation cancellation', () => {
  test.beforeEach(({ isMobile }) => {
    test.skip(isMobile, 'Desktop operation coverage.')
  })

  test('cancels an active Run operation from the result toolbar', async ({ page }) => {
    const operations = observeOperationWebSocket(page)
    await openWorkbench(page)
    await page.getByRole('region', { name: 'Source editor' }).click({
      position: { x: 320, y: 180 },
    })
    await page.keyboard.press('ControlOrMeta+A')
    await page.keyboard.insertText('while (true)\n{\n}\n')
    await page.getByLabel('Output', { exact: true }).selectOption('run')

    await page.getByRole('button', { name: 'Run', exact: true }).click()
    await expect.poll(() => operations.findStart('run'), { timeout: 30_000 }).toBeDefined()
    const runStart = operations.findStart('run')
    if (!runStart) throw new Error('The Run start command was not observed.')
    await expect.poll(() => operations.operationIdForStart(runStart)).toMatch(/^op_[0-9a-f]{32}$/)
    const runOperationId = operations.operationIdForStart(runStart)
    if (!runOperationId) throw new Error('The Run response did not contain an operation ID.')
    await expect
      .poll(() => operations.hasSubscription(runOperationId), { timeout: 30_000 })
      .toBe(true)
    await expect.poll(() => operations.hasEvent(runOperationId), { timeout: 30_000 }).toBe(true)

    const cancel = page.getByRole('button', { name: 'Cancel operation' })
    await expect(cancel).toBeEnabled()
    await cancel.click()
    await expect
      .poll(() =>
        operations.sent.some(
          (frame) => frame.type === 'cancel' && frame.operationId === runOperationId,
        ),
      )
      .toBe(true)

    await expect(page.locator('.operation-state')).toHaveText('cancelled', { timeout: 30_000 })
    await expect(page.locator('.result-error')).toHaveCount(0)
  })
})
