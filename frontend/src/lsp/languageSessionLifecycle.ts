import type { BuildOutputKind, GatewayLanguageSession, OpenLanguageSessionRequest } from '../api/types'

export type LanguageSessionStatus = 'disabled' | 'connecting' | 'ready' | 'reconnecting' | 'error'

export interface LanguageSessionStatusChange {
  status: LanguageSessionStatus
  message?: string
}

export interface LanguageSessionConnectionPlan {
  key: string
  languageId: string
  modelLanguageId: string
  workspaceUri: string
  selectionRevision: number
  createRequest: () => OpenLanguageSessionRequest
}

export interface DesiredLanguageSession {
  key: string
  plan: LanguageSessionConnectionPlan | null
}

export interface LanguageSessionKeyInput {
  languageId: string
  toolchainId: string
  referenceSetId: string
  buildMode: string
  outputKind: BuildOutputKind
  selectionRevision: number
  filePaths: readonly string[]
  sourceOrder: readonly string[]
}

export function createLanguageSessionKey(input: LanguageSessionKeyInput): string {
  return JSON.stringify([input.languageId, input.toolchainId, input.referenceSetId, input.buildMode, input.outputKind, input.selectionRevision, input.filePaths, input.sourceOrder])
}

export interface LanguageClientHandle {
  start: () => Promise<void>
  dispose: () => Promise<void>
}

export type LanguageSessionTransportFailureKind = 'websocket-not-open' | 'websocket-open-failed' | 'websocket-closed' | 'initialize-timeout' | 'request-timeout'

export class LanguageSessionTransportError extends Error {
  readonly kind: LanguageSessionTransportFailureKind

  constructor(kind: LanguageSessionTransportFailureKind, message: string) {
    super(message)
    this.name = 'LanguageSessionTransportError'
    this.kind = kind
  }
}

export class LanguageSessionProtocolError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'LanguageSessionProtocolError'
  }
}

export type LanguageSessionInitialFailurePhase = 'request' | 'open' | 'descriptor' | 'socket' | 'initialize'

export interface LanguageSessionInitialRetryContext {
  phase: LanguageSessionInitialFailurePhase
  attempt: number
}

export interface LanguageSessionInitialRetryPolicy {
  initialDelayMs: number
  maximumDelayMs: number
  shouldRetry: (error: unknown, context: LanguageSessionInitialRetryContext) => boolean
}

interface SessionFreshness {
  current: boolean
}

interface ActiveLanguageSession {
  plan: LanguageSessionConnectionPlan
  descriptor: GatewayLanguageSession
  socket: WebSocket
  client: LanguageClientHandle
  intentionalClose: boolean
  ready: boolean
  freshness: SessionFreshness
  stopPromise?: Promise<void>
}

class InitialLanguageSessionFailure {
  readonly phase: LanguageSessionInitialFailurePhase
  readonly error: unknown

  constructor(phase: LanguageSessionInitialFailurePhase, error: unknown) {
    this.phase = phase
    this.error = error
  }
}

export interface LanguageSessionLifecycleDependencies {
  open: (request: OpenLanguageSessionRequest, signal: AbortSignal) => Promise<GatewayLanguageSession>
  close: (sessionId: string) => Promise<void>
  createSocket: (url: string) => WebSocket
  createClient: (plan: LanguageSessionConnectionPlan, descriptor: GatewayLanguageSession, socket: WebSocket, isCurrent: () => boolean) => LanguageClientHandle
  schedule: (callback: () => void, delay: number) => number
  cancelSchedule: (handle: number) => void
}

export class LanguageSessionLifecycle {
  private desired: DesiredLanguageSession | null = null
  private active: ActiveLanguageSession | null = null
  private generation = 0
  private reconnectHandle: number | null = null
  private attemptAbort: AbortController | null = null
  private queue: Promise<void> = Promise.resolve()
  private failedDesiredKey: string | null = null
  private disposed = false
  private readonly onStatus: (change: LanguageSessionStatusChange) => void
  private readonly dependencies: LanguageSessionLifecycleDependencies
  private readonly initialRetryPolicy: LanguageSessionInitialRetryPolicy | undefined

  constructor(onStatus: (change: LanguageSessionStatusChange) => void, dependencies: LanguageSessionLifecycleDependencies, initialRetryPolicy?: LanguageSessionInitialRetryPolicy) {
    this.onStatus = onStatus
    this.dependencies = dependencies
    this.initialRetryPolicy = initialRetryPolicy
  }

  update(desired: DesiredLanguageSession | null): void {
    if (this.disposed) return
    const previous = this.desired
    this.desired = desired
    const retriesFailedDesired = desired?.plan !== null && desired?.key === this.failedDesiredKey
    if (previous?.key === desired?.key) {
      if (this.active?.plan.key === desired?.key) return
      if (!retriesFailedDesired && (previous?.plan !== null || desired?.plan === null)) return
    }
    this.failedDesiredKey = null
    const generation = ++this.generation
    this.clearReconnect()
    this.interruptAttempt()
    this.enqueue(() => this.reconcile(generation, false), desired?.key ?? null)
  }

  async dispose(): Promise<void> {
    if (this.disposed) return
    this.disposed = true
    this.desired = null
    ++this.generation
    this.clearReconnect()
    this.interruptAttempt()
    await this.queue
    await this.stopActive()
    this.onStatus({ status: 'disabled' })
  }

  private enqueue(action: () => Promise<void>, failureKey: string | null = null): void {
    this.queue = this.queue.then(action, action).catch((error: unknown) => {
      if (!this.disposed) {
        if (failureKey !== null && this.desired?.key === failureKey) {
          this.failedDesiredKey = failureKey
        }
        this.onStatus({
          status: 'error',
          message: error instanceof Error ? error.message : 'Language server connection failed.',
        })
      }
    })
  }

  private async reconcile(generation: number, forceRestart: boolean): Promise<void> {
    if (this.disposed || generation !== this.generation) return
    const desired = this.desired
    if (!desired) {
      await this.stopActive()
      this.onStatus({ status: 'disabled' })
      return
    }

    if (!forceRestart && this.active?.plan.key === desired.key) return
    await this.stopActive()
    if (this.disposed || generation !== this.generation) return
    if (!desired.plan) {
      this.onStatus({ status: 'connecting' })
      return
    }

    await this.start(desired.plan, generation)
  }

  private async start(plan: LanguageSessionConnectionPlan, generation: number): Promise<void> {
    this.onStatus({ status: 'connecting' })
    const attempt = new AbortController()
    this.attemptAbort = attempt
    let retryAttempt = 0
    try {
      while (!attempt.signal.aborted) {
        try {
          await this.startOnce(plan, generation, attempt)
          return
        } catch (failure) {
          const initialFailure = failure instanceof InitialLanguageSessionFailure ? failure : new InitialLanguageSessionFailure('initialize', failure)
          if (attempt.signal.aborted || isAbortError(initialFailure.error)) return
          const delay = this.initialRetryDelay(initialFailure, retryAttempt)
          if (delay === null) throw initialFailure.error
          retryAttempt += 1
          await waitForScheduledRetry(delay, attempt.signal, this.dependencies)
        }
      }
    } finally {
      if (this.attemptAbort === attempt) this.attemptAbort = null
    }
  }

  private async startOnce(plan: LanguageSessionConnectionPlan, generation: number, attempt: AbortController): Promise<void> {
    let phase: LanguageSessionInitialFailurePhase = 'request'
    let descriptor: GatewayLanguageSession | null = null
    let socket: WebSocket | null = null
    let active: ActiveLanguageSession | null = null
    try {
      const request = plan.createRequest()
      phase = 'open'
      descriptor = await this.dependencies.open(request, attempt.signal)
      if (attempt.signal.aborted || this.disposed || generation !== this.generation || this.desired?.key !== plan.key) {
        await this.closeDescriptor(descriptor)
        return
      }

      phase = 'descriptor'
      validateDescriptor(plan, request, descriptor)
      phase = 'socket'
      socket = this.dependencies.createSocket(descriptor.webSocketUrl)
      const freshness: SessionFreshness = { current: true }
      let candidate: ActiveLanguageSession
      phase = 'initialize'
      const client = this.dependencies.createClient(plan, descriptor, socket, () => freshness.current && this.active === candidate && generation === this.generation && this.desired?.key === plan.key)
      candidate = {
        plan,
        descriptor,
        socket,
        client,
        intentionalClose: false,
        ready: false,
        freshness,
      }
      active = candidate
      this.active = candidate
      socket.addEventListener('close', () => this.handleUnexpectedClose(candidate))
      await waitForAbortable(client.start(), attempt.signal)
      if (this.active !== candidate || generation !== this.generation || this.desired?.key !== plan.key) {
        if (this.active === candidate) this.active = null
        await this.stop(candidate)
        return
      }
      candidate.ready = true
      this.onStatus({ status: 'ready' })
    } catch (error) {
      if (active) {
        if (this.active === active) this.active = null
        await this.stop(active)
      } else if (descriptor) {
        if (socket?.readyState === WebSocket.OPEN || socket?.readyState === WebSocket.CONNECTING) {
          try {
            socket.close(1000, 'Language session setup failed.')
          } catch {
            // The Gateway descriptor is still closed below.
          }
        }
        await this.closeDescriptor(descriptor)
      }
      throw new InitialLanguageSessionFailure(phase, error)
    }
  }

  private initialRetryDelay(failure: InitialLanguageSessionFailure, retryAttempt: number): number | null {
    const policy = this.initialRetryPolicy
    if (
      !policy?.shouldRetry(failure.error, {
        phase: failure.phase,
        attempt: retryAttempt,
      })
    ) {
      return null
    }
    const initialDelay = finiteNonNegativeDelay(policy.initialDelayMs)
    const maximumDelay = Math.max(initialDelay, finiteNonNegativeDelay(policy.maximumDelayMs))
    return Math.min(initialDelay * 2 ** Math.min(retryAttempt, 30), maximumDelay)
  }

  private handleUnexpectedClose(active: ActiveLanguageSession): void {
    const initialRetryOwnsClose = !active.ready && this.initialRetryPolicy !== undefined
    if (this.disposed || active.intentionalClose || initialRetryOwnsClose || this.active !== active) {
      return
    }
    this.active = null
    active.freshness.current = false
    this.attemptAbort?.abort()
    this.enqueue(() => this.stop(active))
    this.onStatus({ status: 'reconnecting' })
    const generation = ++this.generation
    this.reconnectHandle = this.dependencies.schedule(() => {
      this.reconnectHandle = null
      this.enqueue(() => this.reconcile(generation, true), active.plan.key)
    }, 500)
  }

  private async stopActive(): Promise<void> {
    const active = this.active
    if (!active) return
    this.active = null
    await this.stop(active)
  }

  private async stop(active: ActiveLanguageSession): Promise<void> {
    if (active.stopPromise) return active.stopPromise
    active.stopPromise = this.stopCore(active)
    return active.stopPromise
  }

  private async stopCore(active: ActiveLanguageSession): Promise<void> {
    active.intentionalClose = true
    active.freshness.current = false
    await active.client.dispose().catch(() => undefined)
    if (active.socket.readyState === WebSocket.OPEN || active.socket.readyState === WebSocket.CONNECTING) {
      try {
        active.socket.close(1000, 'Language session replaced.')
      } catch {
        // A native WebSocket can reject close() while its opening handshake is being aborted.
      }
    }
    await this.closeDescriptor(active.descriptor)
  }

  private closeDescriptor(descriptor: GatewayLanguageSession): Promise<void> {
    return this.dependencies.close(descriptor.sessionId).catch(() => undefined)
  }

  private interruptAttempt(): void {
    this.attemptAbort?.abort()
    if (!this.active) return
    this.active.intentionalClose = true
    this.active.freshness.current = false
    if (this.active.socket.readyState === WebSocket.OPEN || this.active.socket.readyState === WebSocket.CONNECTING) {
      try {
        this.active.socket.close(1000, 'Language session selection changed.')
      } catch {
        // The queued teardown still disposes the language client and Gateway session.
      }
    }
  }

  private clearReconnect(): void {
    if (this.reconnectHandle === null) return
    this.dependencies.cancelSchedule(this.reconnectHandle)
    this.reconnectHandle = null
  }
}

function validateDescriptor(plan: LanguageSessionConnectionPlan, request: OpenLanguageSessionRequest, descriptor: GatewayLanguageSession): void {
  if (
    !descriptor.sessionId ||
    !descriptor.compilerBuildIdentity ||
    !Number.isFinite(Date.parse(descriptor.expiresAtUtc)) ||
    Date.parse(descriptor.expiresAtUtc) <= Date.now() ||
    descriptor.languageId !== plan.languageId ||
    descriptor.languageId !== request.languageId ||
    descriptor.toolchainId !== request.toolchainId ||
    descriptor.lspVersion !== request.lspVersion ||
    descriptor.workspaceRevision !== request.workspace.revision ||
    descriptor.selectionRevision !== plan.selectionRevision ||
    descriptor.selectionRevision !== request.workspace.selectionRevision ||
    !descriptor.webSocketUrl ||
    !Array.isArray(descriptor.capabilities)
  ) {
    throw new Error('Gateway returned a mismatched language session descriptor.')
  }
}

async function waitForAbortable<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
  if (signal.aborted) throw new DOMException('Language session attempt was aborted.', 'AbortError')

  let abort: (() => void) | undefined
  const aborted = new Promise<never>((_, reject) => {
    abort = () => reject(new DOMException('Language session attempt was aborted.', 'AbortError'))
    signal.addEventListener('abort', abort, { once: true })
  })
  try {
    return await Promise.race([promise, aborted])
  } finally {
    if (abort) signal.removeEventListener('abort', abort)
  }
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function finiteNonNegativeDelay(value: number): number {
  return Number.isFinite(value) && value >= 0 ? value : 0
}

function waitForScheduledRetry(delay: number, signal: AbortSignal, dependencies: LanguageSessionLifecycleDependencies): Promise<void> {
  if (signal.aborted) {
    return Promise.reject(signal.reason ?? new DOMException('Language session retry was aborted.', 'AbortError'))
  }

  return new Promise<void>((resolve, reject) => {
    let handle: number | null = null
    let onAbort: () => void = () => undefined
    const cleanup = () => signal.removeEventListener('abort', onAbort)
    onAbort = () => {
      if (handle !== null) dependencies.cancelSchedule(handle)
      cleanup()
      reject(signal.reason ?? new DOMException('Language session retry was aborted.', 'AbortError'))
    }
    handle = dependencies.schedule(() => {
      cleanup()
      resolve()
    }, delay)
    signal.addEventListener('abort', onAbort, { once: true })
    if (signal.aborted) onAbort()
  })
}

export interface LspDiagnosticRevision {
  selectionRevision: number
  documentVersion: number
}

export function isCurrentLspDiagnostic(data: unknown, selectionRevision: number, documentVersion: number | undefined): boolean {
  if (!isDiagnosticRevision(data)) return true
  return data.selectionRevision === selectionRevision && (documentVersion === undefined || data.documentVersion === documentVersion)
}

function isDiagnosticRevision(value: unknown): value is LspDiagnosticRevision {
  if (typeof value !== 'object' || value === null) return false
  const candidate = value as Record<string, unknown>
  return typeof candidate.selectionRevision === 'number' && typeof candidate.documentVersion === 'number'
}
