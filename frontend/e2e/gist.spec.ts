import { expect, type Page, test } from '@playwright/test'
import type { GistDocument, GistWorkspaceState } from '../src/api/types'
import { decodeWire } from '../src/api/wire'

const authenticatedStatus = {
  available: true,
  authenticated: true,
  login: 'octocat',
  csrfToken: 'csrf-browser-test',
}

function workspace(source: string): GistWorkspaceState {
  return {
    schemaVersion: 1,
    languageId: 'csharp',
    toolchainId: 'roslyn-stable',
    referenceSetId: 'net10-ref',
    outputId: 'il',
    runtimeId: null,
    buildMode: 'release',
    activeFile: 'Program.cs',
    sourceOrder: ['Program.cs'],
    files: [{ path: 'Program.cs', text: source }],
  }
}

function gist(id: string, source: string, overrides: Partial<GistDocument> = {}): GistDocument {
  return {
    id,
    htmlUrl: `https://gist.github.com/octocat/${id}`,
    ownerLogin: 'octocat',
    isPublic: true,
    canUpdate: false,
    description: 'Browser acceptance Gist',
    sourceFormat: 'sharplabnext-v1',
    workspace: workspace(source),
    warnings: [],
    ...overrides,
  }
}

async function openWorkbench(page: Page, fragment = '') {
  await page.goto(`/${fragment}`)
  await expect(page.getByLabel('Language')).toBeEnabled()
}

async function mockAuthenticatedGitHub(page: Page) {
  await page.route('**/api/v1/auth/github/status', (route) =>
    route.fulfill({ status: 200, json: authenticatedStatus }),
  )
}

test.describe('GitHub Gist browser workflows', () => {
  test.beforeEach(({ isMobile }) => {
    test.skip(isMobile, 'Desktop Gist coverage.')
  })

  test('restores an anonymous public Gist fragment into the workbench', async ({ page }) => {
    const document = gist('abcde1', 'public static class RestoredFromPublicGist {}')
    await page.route('**/api/v1/shares/gists/abcde1', (route) =>
      route.fulfill({ status: 200, json: document }),
    )

    await openWorkbench(page, '#gist:abcde1')

    await expect(page.getByLabel('Language')).toHaveValue('csharp')
    await expect(page.getByLabel('Output', { exact: true })).toHaveValue('il')
    await expect(page.locator('.monaco-editor .view-lines')).toContainText('RestoredFromPublicGist')
    await expect(page.getByLabel('Share notices')).toHaveCount(0)
    await expect.poll(() => new URL(page.url()).hash).toBe('#gist:abcde1')
  })

  test('creates a private Gist with the authenticated CSRF session', async ({ page }) => {
    await mockAuthenticatedGitHub(page)
    let capturedRequest: {
      description: string
      isPublic: boolean
      workspace: GistWorkspaceState
    } | null = null
    let capturedCsrf: string | undefined
    await page.route('**/api/v1/shares/gists', async (route) => {
      capturedRequest = decodeWire<{
        description: string
        isPublic: boolean
        workspace: GistWorkspaceState
      }>(route.request().postDataJSON())
      capturedCsrf = route.request().headers()['x-sharplabnext-csrf']
      await route.fulfill({
        status: 201,
        json: gist('c0ffee', capturedRequest.workspace.files[0]?.text ?? '', {
          isPublic: capturedRequest.isPublic,
          canUpdate: true,
          description: capturedRequest.description,
          workspace: capturedRequest.workspace,
        }),
      })
    })

    await openWorkbench(page)
    await page.getByLabel('Save to GitHub Gist').click()
    await expect(page.getByRole('dialog', { name: 'GitHub Gist' })).toBeVisible()
    await expect(page.getByText('octocat', { exact: true })).toBeVisible()
    await page.getByLabel('Description').fill('Private browser Gist')
    await expect(page.getByLabel('Public Gist')).not.toBeChecked()
    await page.getByRole('button', { name: 'New Gist' }).click()

    await expect.poll(() => capturedRequest).not.toBeNull()
    expect(capturedRequest).toMatchObject({
      description: 'Private browser Gist',
      isPublic: false,
    })
    expect(capturedCsrf).toBe(authenticatedStatus.csrfToken)
    await expect(page.getByRole('dialog', { name: 'GitHub Gist' })).toHaveCount(0)
    await expect.poll(() => new URL(page.url()).hash).toBe('#gist:c0ffee')
  })

  test('updates an owned Gist only through the explicit save command', async ({ page }) => {
    await mockAuthenticatedGitHub(page)
    const current = gist('decaf1', 'public static class OwnedGist {}', {
      isPublic: false,
      canUpdate: true,
      description: 'Before update',
    })
    let capturedRequest: { description: string; workspace: GistWorkspaceState } | null = null
    let capturedCsrf: string | undefined
    await page.route('**/api/v1/shares/gists/decaf1', async (route) => {
      if (route.request().method() === 'GET') {
        await route.fulfill({ status: 200, json: current })
        return
      }
      capturedRequest = decodeWire<{ description: string; workspace: GistWorkspaceState }>(
        route.request().postDataJSON(),
      )
      capturedCsrf = route.request().headers()['x-sharplabnext-csrf']
      await route.fulfill({
        status: 200,
        json: {
          ...current,
          description: capturedRequest.description,
          workspace: capturedRequest.workspace,
        },
      })
    })

    await openWorkbench(page, '#gist:decaf1')
    await page.getByLabel('Save to GitHub Gist').click()
    await expect(page.getByRole('button', { name: 'Save changes' })).toBeEnabled()
    await page.getByLabel('Description').fill('After update')
    await page.getByRole('button', { name: 'Save changes' }).click()

    await expect.poll(() => capturedRequest).not.toBeNull()
    expect(capturedRequest?.description).toBe('After update')
    expect(capturedCsrf).toBe(authenticatedStatus.csrfToken)
    await expect(page.getByRole('dialog', { name: 'GitHub Gist' })).toHaveCount(0)
    await expect.poll(() => new URL(page.url()).hash).toBe('#gist:decaf1')
  })

  test('shows a not-found error when an anonymous Gist cannot be loaded', async ({ page }) => {
    await page.route('**/api/v1/shares/gists/deadbe', (route) =>
      route.fulfill({ status: 404, json: { message: 'The Gist was not found.' } }),
    )

    await openWorkbench(page, '#gist:deadbe')

    await expect(page.getByText('Share URL could not be restored')).toBeVisible()
    await expect(page.getByText('The Gist was not found.')).toBeVisible()
  })

  for (const scenario of [
    { status: 401, label: 'unauthorized', message: 'The GitHub session has expired.' },
    { status: 403, label: 'forbidden', message: 'GitHub denied access to this Gist.' },
    { status: 429, label: 'rate limited', message: 'GitHub rate limit exceeded.' },
  ]) {
    test(`shows the ${scenario.label} create error inside the Gist dialog`, async ({ page }) => {
      await mockAuthenticatedGitHub(page)
      await page.route('**/api/v1/shares/gists', (route) =>
        route.fulfill({ status: scenario.status, json: { message: scenario.message } }),
      )

      await openWorkbench(page)
      await page.getByLabel('Save to GitHub Gist').click()
      await expect(page.getByRole('button', { name: 'New Gist' })).toBeEnabled()
      await page.getByRole('button', { name: 'New Gist' }).click()

      await expect(page.locator('.gist-error')).toHaveText(scenario.message)
      await expect(page.getByRole('dialog', { name: 'GitHub Gist' })).toBeVisible()
    })
  }
})
