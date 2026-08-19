import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, renderHook } from '@testing-library/react'
import { type ReactNode, StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resolveSelection } from '../api/client'
import type { ResolveSelectionRequest, ResolveSelectionResponse } from '../api/types'
import { createCatalogFixture } from '../test/catalogFixture'
import { resetWorkbenchStore, useWorkbenchStore } from './store'
import { useSelectionResolution } from './useSelectionResolution'

vi.mock('../api/client', () => ({
  resolveSelection: vi.fn(),
}))

const catalog = createCatalogFixture()
const resolveSelectionMock = vi.mocked(resolveSelection)

function responseFor(request: ResolveSelectionRequest): ResolveSelectionResponse {
  return {
    effectiveSelection: {
      languageId: request.languageId,
      toolchainId: request.toolchainId ?? 'roslyn-stable',
      referenceSetId: request.referenceSetId ?? 'net10-ref',
      outputId: request.outputId,
      runtimeId: request.runtimeId,
    },
    selectionChanges: [],
    effectiveCapabilities: {
      languageServerCapabilities: [],
      buildCapabilities: ['compile-check', 'ast'],
      outputCapabilities: ['compile-check', 'ast', 'decompiled-csharp'],
      runtimeCapabilities: [],
    },
    pipelineResolutionId: `pipeline-${request.languageId}-${request.workspaceRevision}`,
    pipelinePlan: {
      releaseId: catalog.releaseId,
      languageWorkerId: request.toolchainId ?? 'roslyn-stable',
      compilerWorkerId: request.toolchainId ?? 'roslyn-stable',
      referenceSetId: request.referenceSetId ?? 'net10-ref',
      stages: [],
      runtimeId: request.runtimeId,
      securityPolicyId: 'compiler-default',
      workerImageIds: [],
    },
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
  }
}

function wrapper(strict = false) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return ({ children }: { children: ReactNode }) => {
    const content = strict ? <StrictMode>{children}</StrictMode> : children
    return <QueryClientProvider client={client}>{content}</QueryClientProvider>
  }
}

async function flushAsync(): Promise<void> {
  await act(async () => {
    await Promise.resolve()
    await vi.advanceTimersByTimeAsync(0)
    await Promise.resolve()
  })
}

describe('useSelectionResolution', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    resetWorkbenchStore()
    resolveSelectionMock.mockReset()
    resolveSelectionMock.mockImplementation(async (request) => responseFor(request))
  })

  afterEach(() => {
    cleanup()
    vi.clearAllTimers()
    vi.useRealTimers()
  })

  it('waits for restoration and immediately resolves only the final initial snapshot', async () => {
    const { rerender } = renderHook(({ ready }) => useSelectionResolution(catalog, ready), {
      initialProps: { ready: false },
      wrapper: wrapper(),
    })
    const fsharp = catalog.languages.find((language) => language.id === 'fsharp')
    if (!fsharp) throw new Error('Expected the F# catalog fixture.')

    act(() => {
      useWorkbenchStore.getState().selectLanguage(fsharp, {
        languageId: 'fsharp',
        toolchainId: 'fsharp-stable',
        referenceSetId: 'net10-ref',
        outputId: 'ast',
        runtimeId: null,
      })
    })
    await flushAsync()
    expect(resolveSelectionMock).not.toHaveBeenCalled()

    rerender({ ready: true })
    await flushAsync()

    expect(resolveSelectionMock).toHaveBeenCalledOnce()
    expect(resolveSelectionMock.mock.calls[0]?.[0]).toMatchObject({
      languageId: 'fsharp',
      toolchainId: 'fsharp-stable',
      outputId: 'ast',
      workspaceRevision: useWorkbenchStore.getState().workspaceRevision,
    })
  })

  it('keeps a slow initial resolution marked as initial under StrictMode', async () => {
    let completeResolution: (() => void) | null = null
    resolveSelectionMock.mockImplementation(
      (request) =>
        new Promise<ResolveSelectionResponse>((resolve) => {
          completeResolution = () => resolve(responseFor(request))
        }),
    )

    const { result } = renderHook(() => useSelectionResolution(catalog, true), {
      wrapper: wrapper(true),
    })
    await flushAsync()
    expect(resolveSelectionMock).toHaveBeenCalledOnce()

    await act(async () => vi.advanceTimersByTimeAsync(251))
    expect(result.current.resolution).toBeNull()
    act(() => completeResolution?.())
    await flushAsync()

    expect(result.current.resolution).not.toBeNull()
    expect(result.current.isInitialSnapshot).toBe(true)
  })

  it('keeps the canonicalized follow-up selection in the initial bootstrap', async () => {
    const initial = useWorkbenchStore.getState()
    act(() => {
      initial.setSelectionIntent({
        languageId: initial.languageId,
        toolchainId: 'legacy-roslyn',
        referenceSetId: initial.referenceSetId,
        outputId: initial.outputId,
        runtimeId: initial.runtimeId,
      })
    })
    resolveSelectionMock.mockImplementation(async (request) => {
      const response = responseFor(request)
      if (request.toolchainId === 'legacy-roslyn') {
        response.effectiveSelection.toolchainId = 'roslyn-stable'
      }
      return response
    })

    const { result } = renderHook(() => useSelectionResolution(catalog, true), {
      wrapper: wrapper(),
    })
    await flushAsync()
    await flushAsync()

    expect(resolveSelectionMock).toHaveBeenCalledTimes(2)
    expect(resolveSelectionMock.mock.calls[0]?.[0].toolchainId).toBe('legacy-roslyn')
    expect(resolveSelectionMock.mock.calls[1]?.[0].toolchainId).toBe('roslyn-stable')
    expect(result.current.resolution?.effectiveSelection.toolchainId).toBe('roslyn-stable')
    expect(result.current.isInitialSnapshot).toBe(true)
  })

  it('keeps the 250ms debounce for a later language switch', async () => {
    renderHook(() => useSelectionResolution(catalog, true), { wrapper: wrapper() })
    await flushAsync()
    expect(resolveSelectionMock).toHaveBeenCalledOnce()

    const fsharp = catalog.languages.find((language) => language.id === 'fsharp')
    if (!fsharp) throw new Error('Expected the F# catalog fixture.')
    act(() => {
      useWorkbenchStore.getState().selectLanguage(fsharp, {
        languageId: 'fsharp',
        toolchainId: 'fsharp-stable',
        referenceSetId: 'net10-ref',
        outputId: 'ast',
        runtimeId: null,
      })
    })

    await act(async () => vi.advanceTimersByTimeAsync(249))
    expect(resolveSelectionMock).toHaveBeenCalledOnce()

    await act(async () => vi.advanceTimersByTimeAsync(1))
    await flushAsync()
    expect(resolveSelectionMock).toHaveBeenCalledTimes(2)
    expect(resolveSelectionMock.mock.calls[1]?.[0]).toMatchObject({
      languageId: 'fsharp',
      toolchainId: 'fsharp-stable',
      outputId: 'ast',
    })
  })

  it('coalesces rapid revisions and aborts the superseded resolution', async () => {
    const requests: ResolveSelectionRequest[] = []
    const signals: AbortSignal[] = []
    resolveSelectionMock.mockImplementation((request, signal) => {
      requests.push(request)
      if (signal) signals.push(signal)
      if (requests.length > 1) return Promise.resolve(responseFor(request))
      return new Promise<ResolveSelectionResponse>((_resolve, reject) => {
        signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))
      })
    })

    renderHook(() => useSelectionResolution(catalog, true), { wrapper: wrapper() })
    await flushAsync()
    expect(requests).toHaveLength(1)

    act(() => {
      const store = useWorkbenchStore.getState()
      store.setSource('class First {}')
      store.setSource('class Second {}')
      store.setSource('class Latest {}')
    })
    const latestRevision = useWorkbenchStore.getState().workspaceRevision

    await act(async () => vi.advanceTimersByTimeAsync(249))
    expect(requests).toHaveLength(1)
    expect(signals[0]?.aborted).toBe(false)

    await act(async () => vi.advanceTimersByTimeAsync(1))
    await flushAsync()
    expect(requests).toHaveLength(2)
    expect(requests[1]?.workspaceRevision).toBe(latestRevision)
    expect(signals[0]?.aborted).toBe(true)
  })
})
