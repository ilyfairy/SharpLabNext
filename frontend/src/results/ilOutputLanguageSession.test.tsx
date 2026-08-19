import { act, cleanup, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, openLanguageSessionWithResolution, resolveSelectionHttp } from '../api/client'
import type { ResolveSelectionRequest, ResolveSelectionResponse } from '../api/types'
import {
  type LanguageSessionInitialRetryPolicy,
  LanguageSessionProtocolError,
  LanguageSessionTransportError,
} from '../lsp/languageSessionLifecycle'
import { useIlOutputLanguageSession } from './ilOutputLanguageSession'

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  openLanguageSessionWithResolution: vi.fn(),
  resolveSelectionHttp: vi.fn(),
}))

const lifecycleHarness = vi.hoisted(() => ({
  instances: [] as Array<{
    update: ReturnType<typeof vi.fn>
    dispose: ReturnType<typeof vi.fn>
    retryPolicy: LanguageSessionInitialRetryPolicy | undefined
  }>,
}))

vi.mock('../lsp/languageSessionLifecycle', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lsp/languageSessionLifecycle')>()),
  LanguageSessionLifecycle: class {
    readonly update = vi.fn()
    readonly dispose = vi.fn(() => Promise.resolve())

    constructor(
      _onStatus: unknown,
      _dependencies: unknown,
      retryPolicy?: LanguageSessionInitialRetryPolicy,
    ) {
      lifecycleHarness.instances.push({
        update: this.update,
        dispose: this.dispose,
        retryPolicy,
      })
    }
  },
}))

const resolveSelectionHttpMock = vi.mocked(resolveSelectionHttp)
const openLanguageSessionWithResolutionMock = vi.mocked(openLanguageSessionWithResolution)

const options = {
  catalogRevision: 'catalog-1',
  referenceSetId: 'net10-ref',
  buildMode: 'release',
  workspaceRevision: 7,
  selectionRevision: 11,
} as const

function responseFor(request: ResolveSelectionRequest): ResolveSelectionResponse {
  return {
    effectiveSelection: {
      languageId: 'il',
      toolchainId: 'mobius-ilasm-stable',
      referenceSetId: request.referenceSetId ?? 'net10-ref',
      outputId: 'il',
      runtimeId: null,
    },
    selectionChanges: [],
    effectiveCapabilities: {
      languageServerCapabilities: ['hover', 'semantic-tokens'],
      buildCapabilities: [],
      outputCapabilities: ['il'],
      runtimeCapabilities: [],
    },
    pipelineResolutionId: 'pipeline-il-output',
    pipelinePlan: {
      releaseId: 'release-1',
      languageWorkerId: 'mobius-ilasm-stable',
      compilerWorkerId: 'mobius-ilasm-stable',
      referenceSetId: request.referenceSetId ?? 'net10-ref',
      stages: [],
      runtimeId: null,
      securityPolicyId: 'compiler-default',
      workerImageIds: [],
    },
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
  }
}

async function flushAsync(): Promise<void> {
  await act(async () => {
    await Promise.resolve()
    await vi.advanceTimersByTimeAsync(0)
    await Promise.resolve()
  })
}

describe('useIlOutputLanguageSession', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    lifecycleHarness.instances.length = 0
    resolveSelectionHttpMock.mockReset()
    openLanguageSessionWithResolutionMock.mockReset()
  })

  afterEach(() => {
    cleanup()
    vi.clearAllTimers()
    vi.useRealTimers()
  })

  it.each([
    ['network failure', new TypeError('Network request failed.')],
    ['request timeout', new ApiError(408, null, 'Request timed out.')],
    ['rate limit', new ApiError(429, null, 'Too many requests.')],
    ['Gateway outage', new ApiError(503, null, 'Gateway unavailable.')],
  ])('retries a transient %s and starts the session after resolution succeeds', async (_, error) => {
    resolveSelectionHttpMock
      .mockRejectedValueOnce(error)
      .mockImplementation(async (request) => responseFor(request))

    const { result } = renderHook(() =>
      useIlOutputLanguageSession('.method public static void Main() {}', 'generation-1', options),
    )
    await flushAsync()

    expect(resolveSelectionHttpMock).toHaveBeenCalledOnce()
    expect(result.current.status).toBe('connecting')

    await act(async () => vi.advanceTimersByTimeAsync(249))
    expect(resolveSelectionHttpMock).toHaveBeenCalledOnce()

    await act(async () => vi.advanceTimersByTimeAsync(1))
    await flushAsync()

    expect(resolveSelectionHttpMock).toHaveBeenCalledTimes(2)
    expect(lifecycleHarness.instances).toHaveLength(1)
    expect(lifecycleHarness.instances[0]?.update).toHaveBeenCalledWith(
      expect.objectContaining({
        key: expect.stringContaining('pipeline-il-output'),
        plan: expect.objectContaining({ languageId: 'il', modelLanguageId: 'il' }),
      }),
    )
  })

  it('falls back after an unsupported selection response without retrying', async () => {
    resolveSelectionHttpMock.mockRejectedValue(
      new ApiError(400, null, 'The reference set does not support IL language services.'),
    )

    const { result } = renderHook(() =>
      useIlOutputLanguageSession('.class public Example {}', 'generation-1', options),
    )
    await flushAsync()

    expect(resolveSelectionHttpMock).toHaveBeenCalledOnce()
    expect(result.current.status).toBe('error')
    expect(result.current.semanticTokens).toEqual([])
    expect(lifecycleHarness.instances[0]?.update).toHaveBeenLastCalledWith(null)

    await act(async () => vi.advanceTimersByTimeAsync(60_000))
    expect(resolveSelectionHttpMock).toHaveBeenCalledOnce()
  })

  it('aborts a pending retry when the result changes', async () => {
    const signals: AbortSignal[] = []
    resolveSelectionHttpMock.mockImplementation(async (request, signal) => {
      if (signal) signals.push(signal)
      if (request.workspaceRevision === 7) throw new ApiError(503, null, 'Gateway unavailable.')
      return responseFor(request)
    })

    const { rerender } = renderHook(
      ({ workspaceRevision }) =>
        useIlOutputLanguageSession('.class public Example {}', 'generation-1', {
          ...options,
          workspaceRevision,
        }),
      { initialProps: { workspaceRevision: 7 } },
    )
    await flushAsync()
    expect(resolveSelectionHttpMock).toHaveBeenCalledOnce()

    rerender({ workspaceRevision: 8 })
    await flushAsync()

    expect(signals[0]?.aborted).toBe(true)
    expect(resolveSelectionHttpMock).toHaveBeenCalledTimes(2)

    await act(async () => vi.advanceTimersByTimeAsync(4_000))
    expect(resolveSelectionHttpMock).toHaveBeenCalledTimes(2)
  })

  it('classifies only transient IL output session transport and HTTP failures for retry', async () => {
    resolveSelectionHttpMock.mockImplementation(async (request) => responseFor(request))

    renderHook(() =>
      useIlOutputLanguageSession('.class public Example {}', 'generation-1', options),
    )
    await flushAsync()

    const policy = lifecycleHarness.instances[0]?.retryPolicy
    expect(policy).toBeDefined()
    if (!policy) throw new Error('The IL output retry policy was not installed.')

    const open = { phase: 'open' as const, attempt: 0 }
    const initialize = { phase: 'initialize' as const, attempt: 0 }
    expect(policy.shouldRetry(new TypeError('Network request failed.'), open)).toBe(true)
    expect(policy.shouldRetry(new TypeError('Programming error.'), initialize)).toBe(false)
    for (const status of [408, 429, 500, 502, 599]) {
      expect(
        policy.shouldRetry(new ApiError(status, null, 'Transient gateway failure.'), open),
      ).toBe(true)
    }
    expect(policy.shouldRetry(new ApiError(400, null, 'Bad request.'), open)).toBe(false)
    expect(
      policy.shouldRetry(
        new LanguageSessionTransportError('websocket-open-failed', 'Socket failed.'),
        initialize,
      ),
    ).toBe(true)
    expect(
      policy.shouldRetry(
        new LanguageSessionTransportError('websocket-closed', 'Socket closed.'),
        initialize,
      ),
    ).toBe(true)
    expect(
      policy.shouldRetry(
        new LanguageSessionTransportError('initialize-timeout', 'Initialize timed out.'),
        initialize,
      ),
    ).toBe(true)
    expect(
      policy.shouldRetry(
        new LanguageSessionProtocolError('Invalid initialize response.'),
        initialize,
      ),
    ).toBe(false)
    expect(
      policy.shouldRetry(new Error('Descriptor mismatch.'), { phase: 'descriptor', attempt: 0 }),
    ).toBe(false)
  })
})
