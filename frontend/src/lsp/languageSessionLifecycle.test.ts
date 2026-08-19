import { describe, expect, it } from 'vitest'
import type { GatewayLanguageSession, OpenLanguageSessionRequest } from '../api/types'
import {
  createLanguageSessionKey,
  isCurrentLspDiagnostic,
  type LanguageSessionConnectionPlan,
  type LanguageSessionInitialRetryPolicy,
  LanguageSessionLifecycle,
  type LanguageSessionLifecycleDependencies,
  LanguageSessionProtocolError,
  LanguageSessionTransportError,
} from './languageSessionLifecycle'

class FakeWebSocket extends EventTarget {
  readyState: number = WebSocket.OPEN
  closeCode: number | undefined
  closeReason: string | undefined

  close(code?: number, reason?: string): void {
    if (this.readyState === WebSocket.CLOSED) return
    this.closeCode = code
    this.closeReason = reason
    this.readyState = WebSocket.CLOSED
    this.dispatchEvent(
      new CloseEvent('close', {
        code: code ?? 1000,
        ...(reason === undefined ? {} : { reason }),
      }),
    )
  }

  disconnect(): void {
    this.readyState = WebSocket.CLOSED
    this.dispatchEvent(new CloseEvent('close', { code: 1006 }))
  }
}

interface Harness {
  dependencies: LanguageSessionLifecycleDependencies
  opened: Array<{ request: OpenLanguageSessionRequest; signal: AbortSignal }>
  closed: string[]
  sockets: FakeWebSocket[]
  disposedClients: string[]
  freshness: Array<() => boolean>
  scheduled: Array<{ callback: () => void; delay: number }>
}

function createHarness(options?: {
  start?: (key: string) => Promise<void>
  open?: (
    request: OpenLanguageSessionRequest,
    signal: AbortSignal,
    index: number,
  ) => Promise<GatewayLanguageSession>
}): Harness {
  const opened: Harness['opened'] = []
  const closed: string[] = []
  const sockets: FakeWebSocket[] = []
  const disposedClients: string[] = []
  const freshness: Array<() => boolean> = []
  const scheduled: Harness['scheduled'] = []
  return {
    opened,
    closed,
    sockets,
    disposedClients,
    freshness,
    scheduled,
    dependencies: {
      open: async (request, signal) => {
        const index = opened.push({ request, signal })
        if (options?.open) return options.open(request, signal, index)
        return descriptorFor(request, index)
      },
      close: async (sessionId) => {
        closed.push(sessionId)
      },
      createSocket: () => {
        const socket = new FakeWebSocket()
        sockets.push(socket)
        return socket as unknown as WebSocket
      },
      createClient: (plan, _descriptor, _socket, isCurrent) => {
        freshness.push(isCurrent)
        return {
          start: () => options?.start?.(plan.key) ?? Promise.resolve(),
          dispose: async () => {
            disposedClients.push(plan.key)
          },
        }
      },
      schedule: (callback, delay) => {
        scheduled.push({ callback, delay })
        return scheduled.length
      },
      cancelSchedule: (handle) => {
        const item = scheduled[handle - 1]
        if (item) item.callback = () => undefined
      },
    },
  }
}

describe('LanguageSessionLifecycle', () => {
  it('aborts an in-flight client start and rebuilds from the new selection revision', async () => {
    const firstStart = deferred<void>()
    const harness = createHarness({
      start: (key) => (key === 'selection-1' ? firstStart.promise : Promise.resolve()),
    })
    const statuses: string[] = []
    const lifecycle = new LanguageSessionLifecycle(
      (change) => statuses.push(change.status),
      harness.dependencies,
    )

    lifecycle.update(desired(planFor(1)))
    await eventually(() => expect(harness.opened).toHaveLength(1))
    await eventually(() => expect(harness.freshness).toHaveLength(1))
    expect(harness.freshness[0]?.()).toBe(true)

    lifecycle.update(desired(planFor(2)))
    expect(harness.opened[0]?.signal.aborted).toBe(true)
    expect(harness.freshness[0]?.()).toBe(false)
    await eventually(() => expect(harness.opened).toHaveLength(2))
    await eventually(() => expect(statuses.at(-1)).toBe('ready'))

    expect(harness.opened[1]?.request.workspace.selectionRevision).toBe(2)
    expect(harness.closed).toContain('gateway-session-1')
    expect(harness.disposedClients).toContain('selection-1')
    expect(harness.freshness[1]?.()).toBe(true)

    await lifecycle.dispose()
    expect(harness.closed).toContain('gateway-session-2')
    expect(statuses.at(-1)).toBe('disabled')
  })

  it('opens a new Gateway session and full snapshot after an unexpected disconnect', async () => {
    let source = 'class Program { }'
    const harness = createHarness()
    const statuses: string[] = []
    const lifecycle = new LanguageSessionLifecycle(
      (change) => statuses.push(change.status),
      harness.dependencies,
    )
    const plan = planFor(7, () => source)

    lifecycle.update(desired(plan))
    await eventually(() => expect(statuses.at(-1)).toBe('ready'))
    source = 'class Program { static void Main() { } }'
    harness.sockets[0]?.disconnect()

    await eventually(() => expect(statuses).toContain('reconnecting'))
    expect(harness.scheduled).toHaveLength(1)
    expect(harness.scheduled[0]?.delay).toBe(500)
    harness.scheduled[0]?.callback()
    await eventually(() => expect(harness.opened).toHaveLength(2))
    await eventually(() => expect(statuses.at(-1)).toBe('ready'))

    expect(harness.closed).toContain('gateway-session-1')
    expect(harness.opened[1]?.request.workspace.files[0]?.text).toBe(source)
    expect(harness.opened[0]?.request.requestId).not.toBe(harness.opened[1]?.request.requestId)
    expect(harness.freshness[0]?.()).toBe(false)
    expect(harness.freshness[1]?.()).toBe(true)
    await lifecycle.dispose()
  })

  it('keeps the default pre-ready disconnect reconnect behavior without a retry policy', async () => {
    const firstStart = deferred<void>()
    let startAttempts = 0
    const harness = createHarness({
      start: async () => {
        startAttempts += 1
        if (startAttempts === 1) return firstStart.promise
      },
    })
    const statuses: string[] = []
    const lifecycle = new LanguageSessionLifecycle(
      (change) => statuses.push(change.status),
      harness.dependencies,
    )

    lifecycle.update(desired(planFor(6)))
    await eventually(() => expect(harness.sockets).toHaveLength(1))
    harness.sockets[0]?.disconnect()
    await eventually(() => expect(statuses.at(-1)).toBe('reconnecting'))
    expect(harness.scheduled).toHaveLength(1)
    expect(harness.scheduled[0]?.delay).toBe(500)
    harness.scheduled[0]?.callback()
    await eventually(() => expect(statuses.at(-1)).toBe('ready'))
    expect(harness.opened).toHaveLength(2)
    await lifecycle.dispose()
  })

  it('retries initial transport failures with capped exponential backoff', async () => {
    const harness = createHarness({
      open: async (request, _signal, index) => {
        if (index <= 4) {
          throw new LanguageSessionTransportError('websocket-open-failed', 'Temporary outage.')
        }
        return descriptorFor(request, index)
      },
    })
    const statuses: string[] = []
    const policy: LanguageSessionInitialRetryPolicy = {
      initialDelayMs: 100,
      maximumDelayMs: 250,
      shouldRetry: (error) => error instanceof LanguageSessionTransportError,
    }
    const lifecycle = new LanguageSessionLifecycle(
      (change) => statuses.push(change.status),
      harness.dependencies,
      policy,
    )

    lifecycle.update(desired(planFor(8)))
    const expectedDelays = [100, 200, 250, 250]
    for (let index = 0; index < expectedDelays.length; index += 1) {
      await eventually(() => expect(harness.opened).toHaveLength(index + 1))
      await eventually(() => expect(harness.scheduled).toHaveLength(index + 1))
      expect(harness.scheduled[index]?.delay).toBe(expectedDelays[index])
      harness.scheduled[index]?.callback()
    }
    await eventually(() => expect(statuses.at(-1)).toBe('ready'))
    expect(harness.opened).toHaveLength(5)
    expect(statuses).not.toContain('error')
    await lifecycle.dispose()
  })

  it('retries an initialize transport failure after disposing its failed session', async () => {
    let startAttempts = 0
    const harness = createHarness({
      start: async () => {
        startAttempts += 1
        if (startAttempts === 1) {
          throw new LanguageSessionTransportError(
            'initialize-timeout',
            'Language server initialize timed out.',
          )
        }
      },
    })
    const statuses: string[] = []
    const lifecycle = new LanguageSessionLifecycle(
      (change) => statuses.push(change.status),
      harness.dependencies,
      {
        initialDelayMs: 75,
        maximumDelayMs: 300,
        shouldRetry: (error) => error instanceof LanguageSessionTransportError,
      },
    )

    lifecycle.update(desired(planFor(10)))
    await eventually(() => expect(harness.scheduled).toHaveLength(1))
    expect(harness.scheduled[0]?.delay).toBe(75)
    expect(harness.closed).toContain('gateway-session-1')
    expect(harness.disposedClients).toContain('selection-10')
    harness.scheduled[0]?.callback()
    await eventually(() => expect(statuses.at(-1)).toBe('ready'))
    expect(startAttempts).toBe(2)
    expect(harness.opened).toHaveLength(2)
    await lifecycle.dispose()
  })

  it('cancels an initial retry when the desired workspace changes', async () => {
    const harness = createHarness({
      open: async (request, _signal, index) => {
        if (request.workspace.selectionRevision === 1 && index === 1) {
          throw new LanguageSessionTransportError('websocket-closed', 'Socket closed.')
        }
        return descriptorFor(request, index)
      },
    })
    const policy: LanguageSessionInitialRetryPolicy = {
      initialDelayMs: 100,
      maximumDelayMs: 1_000,
      shouldRetry: (error) => error instanceof LanguageSessionTransportError,
    }
    const lifecycle = new LanguageSessionLifecycle(() => undefined, harness.dependencies, policy)
    const first = desired(planFor(1))
    lifecycle.update(first)
    await eventually(() => expect(harness.scheduled).toHaveLength(1))
    expect(harness.opened[0]?.signal.aborted).toBe(false)

    lifecycle.update(desired(planFor(2)))
    await eventually(() => expect(harness.opened).toHaveLength(2))
    await eventually(() => expect(harness.freshness.at(-1)?.()).toBe(true))
    expect(harness.opened[0]?.signal.aborted).toBe(true)
    const openCount = harness.opened.length
    harness.scheduled[0]?.callback()
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(harness.opened).toHaveLength(openCount)
    await lifecycle.dispose()
  })

  it('closes a descriptor that does not match the requested language selection', async () => {
    const harness = createHarness({
      open: async (request, _signal, index) => ({
        ...descriptorFor(request, index),
        languageId: 'visual-basic',
      }),
    })
    const statuses: Array<{ status: string; message?: string }> = []
    const lifecycle = new LanguageSessionLifecycle(
      (change) => statuses.push(change),
      harness.dependencies,
    )

    lifecycle.update(desired(planFor(3)))
    await eventually(() => expect(statuses.at(-1)?.status).toBe('error'))

    expect(statuses.at(-1)?.message).toBe(
      'Gateway returned a mismatched language session descriptor.',
    )
    expect(harness.closed).toEqual(['gateway-session-1'])
    expect(harness.sockets).toHaveLength(0)
    await lifecycle.dispose()
  })

  it('does not retry protocol failures when a transport-only policy is enabled', async () => {
    const harness = createHarness({
      open: async () => {
        throw new LanguageSessionProtocolError('Invalid language session response.')
      },
    })
    const statuses: Array<{ status: string; message?: string }> = []
    const lifecycle = new LanguageSessionLifecycle(
      (change) => statuses.push(change),
      harness.dependencies,
      {
        initialDelayMs: 50,
        maximumDelayMs: 200,
        shouldRetry: (error) => error instanceof LanguageSessionTransportError,
      },
    )

    lifecycle.update(desired(planFor(9)))
    await eventually(() => expect(statuses.at(-1)?.status).toBe('error'))
    expect(harness.opened).toHaveLength(1)
    expect(harness.scheduled).toHaveLength(0)
    expect(statuses.at(-1)?.message).toBe('Invalid language session response.')
    await lifecycle.dispose()
  })

  it('retries a failed session when the same desired session is updated again', async () => {
    const harness = createHarness({
      open: async (request, _signal, index) => {
        if (index === 1) throw new Error('Temporary language session failure.')
        return descriptorFor(request, index)
      },
    })
    const statuses: string[] = []
    const lifecycle = new LanguageSessionLifecycle(
      (change) => statuses.push(change.status),
      harness.dependencies,
    )
    const requested = desired(planFor(4))

    lifecycle.update(requested)
    await eventually(() => expect(statuses.at(-1)).toBe('error'))
    expect(harness.opened).toHaveLength(1)

    lifecycle.update(requested)
    lifecycle.update(requested)
    await eventually(() => expect(statuses.at(-1)).toBe('ready'))

    expect(harness.opened).toHaveLength(2)
    expect(harness.freshness).toHaveLength(1)
    expect(harness.freshness[0]?.()).toBe(true)
    await lifecycle.dispose()
  })
})

describe('createLanguageSessionKey', () => {
  const input = {
    languageId: 'csharp',
    toolchainId: 'roslyn-stable',
    referenceSetId: 'net10-ref',
    buildMode: 'release',
    outputKind: 'auto',
    selectionRevision: 4,
    filePaths: ['Program.cs', 'Helper.cs'],
    sourceOrder: ['Program.cs', 'Helper.cs'],
  } as const

  it('changes for a selection revision or workspace structure but not source text', () => {
    const current = createLanguageSessionKey(input)

    expect(createLanguageSessionKey({ ...input })).toBe(current)
    expect(createLanguageSessionKey({ ...input, selectionRevision: 5 })).not.toBe(current)
    expect(createLanguageSessionKey({ ...input, outputKind: 'console' })).not.toBe(current)
    expect(
      createLanguageSessionKey({ ...input, filePaths: ['Program.cs', 'Renamed.cs'] }),
    ).not.toBe(current)
    expect(
      createLanguageSessionKey({ ...input, sourceOrder: ['Helper.cs', 'Program.cs'] }),
    ).not.toBe(current)
  })
})

describe('isCurrentLspDiagnostic', () => {
  it('rejects stale document or selection revisions and accepts standard diagnostics', () => {
    expect(isCurrentLspDiagnostic(undefined, 5, 8)).toBe(true)
    expect(isCurrentLspDiagnostic({ selectionRevision: 5, documentVersion: 8 }, 5, 8)).toBe(true)
    expect(isCurrentLspDiagnostic({ selectionRevision: 4, documentVersion: 8 }, 5, 8)).toBe(false)
    expect(isCurrentLspDiagnostic({ selectionRevision: 5, documentVersion: 7 }, 5, 8)).toBe(false)
  })
})

function desired(plan: LanguageSessionConnectionPlan) {
  return { key: plan.key, plan }
}

function planFor(
  selectionRevision: number,
  getSource: () => string = () => 'class Program { }',
): LanguageSessionConnectionPlan {
  return {
    key: `selection-${selectionRevision}`,
    languageId: 'csharp',
    modelLanguageId: 'csharp',
    workspaceUri: 'sharplabnext://workspace-test/',
    selectionRevision,
    createRequest: () => ({
      requestId: `request-${selectionRevision}-${crypto.randomUUID()}`,
      pipelineResolutionId: `pipeline-${selectionRevision}`,
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      workspace: {
        schemaVersion: 1,
        revision: 10,
        selectionRevision,
        languageId: 'csharp',
        files: [{ path: 'Program.cs', version: 1, text: getSource() }],
        activeFile: 'Program.cs',
        sourceOrder: ['Program.cs'],
        referenceSetId: 'net10-ref',
        buildOptions: {
          configuration: 'release',
          optimize: true,
          outputKind: 'console',
          allowUnsafe: false,
          emitPortablePdb: true,
          nullableContext: 'project-default',
          preprocessorSymbols: [],
          checkOverflow: false,
        },
      },
      lspVersion: '3.17',
    }),
  }
}

function descriptorFor(request: OpenLanguageSessionRequest, index: number): GatewayLanguageSession {
  return {
    sessionId: `gateway-session-${index}`,
    languageId: request.languageId,
    toolchainId: request.toolchainId,
    compilerBuildIdentity: 'roslyn-stable-test',
    lspVersion: '3.17',
    workspaceRevision: request.workspace.revision,
    selectionRevision: request.workspace.selectionRevision,
    expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
    webSocketUrl: `/api/v1/language-sessions/gateway-session-${index}/lsp`,
    capabilities: ['diagnostics', 'completion'],
  }
}

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

async function eventually(assertion: () => void): Promise<void> {
  let lastError: unknown
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      assertion()
      return
    } catch (error) {
      lastError = error
      await new Promise((resolve) => setTimeout(resolve, 0))
    }
  }
  throw lastError
}
