import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { GistDocument, GistWorkspaceState } from '../api/types'
import { decodeWire, stringifyWire } from '../api/wire'
import { GistDialog } from './GistDialog'

const workspace: GistWorkspaceState = {
  schemaVersion: 1,
  languageId: 'csharp',
  toolchainId: 'roslyn-stable',
  referenceSetId: 'net10-ref',
  outputId: 'ast',
  runtimeId: null,
  buildMode: 'release',
  releaseId: 'development',
  activeFile: 'Program.cs',
  sourceOrder: ['Program.cs'],
  files: [{ path: 'Program.cs', text: 'class Program {}' }],
}

function response(value: unknown, status = 200): Response {
  return new Response(stringifyWire(value), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

describe('GistDialog', () => {
  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('creates a Gist with the server-issued CSRF token', async () => {
    const saved: GistDocument = {
      id: 'abcdef',
      htmlUrl: 'https://gist.github.com/abcdef',
      ownerLogin: 'owner',
      isPublic: true,
      canUpdate: true,
      description: 'shared',
      sourceFormat: 'sharplabnext-v1',
      workspace,
      warnings: [],
    }
    const fetchMock = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
      const url = input.toString()
      if (url === '/api/v1/auth/github/status') {
        return response({
          available: true,
          authenticated: true,
          login: 'owner',
          csrfToken: 'csrf',
        })
      }
      if (url === '/api/v1/shares/gists') {
        expect(new Headers(init?.headers).get('X-SharpLabNext-CSRF')).toBe('csrf')
        const body = decodeWire<{ isPublic: boolean }>(JSON.parse(String(init?.body)))
        expect(body.isPublic).toBe(true)
        return response(saved, 201)
      }
      return response({ message: `Unexpected request ${url}` }, 500)
    })
    vi.stubGlobal('fetch', fetchMock)
    const onSaved = vi.fn()

    render(<GistDialog open workspace={workspace} currentGist={null} onClose={() => undefined} onSaved={onSaved} />)
    await screen.findByText('owner')
    fireEvent.change(screen.getByLabelText('Description'), {
      target: { value: 'shared' },
    })
    fireEvent.click(screen.getByLabelText('Public Gist'))
    fireEvent.click(screen.getByRole('button', { name: 'New Gist' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalledWith(saved))
  })

  it('offers explicit update only for an owned imported Gist', async () => {
    const current: GistDocument = {
      id: 'abcdef',
      htmlUrl: 'https://gist.github.com/abcdef',
      ownerLogin: 'owner',
      isPublic: false,
      canUpdate: true,
      description: 'existing',
      sourceFormat: 'sharplabnext-v1',
      workspace,
      warnings: [],
    }
    const fetchMock = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
      const url = input.toString()
      if (url === '/api/v1/auth/github/status') {
        return response({
          available: true,
          authenticated: true,
          login: 'owner',
          csrfToken: 'csrf',
        })
      }
      if (url === '/api/v1/shares/gists/abcdef' && init?.method === 'PATCH') return response(current)
      return response({ message: `Unexpected request ${url}` }, 500)
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<GistDialog open workspace={workspace} currentGist={current} onClose={() => undefined} onSaved={() => undefined} />)
    const save = await screen.findByRole('button', { name: 'Save changes' })
    fireEvent.click(save)

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/v1/shares/gists/abcdef', expect.objectContaining({ method: 'PATCH' })))
  })
})
