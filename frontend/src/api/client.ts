import type {
  ApiProblem,
  BuildRequest,
  CancelResult,
  CatalogDocument,
  CreateGistRequest,
  ExplainRequest,
  GatewayLanguageSession,
  GistDocument,
  GitHubAuthStatus,
  GitHubOAuthStartResponse,
  JitRequest,
  OpenLanguageSessionRequest,
  OperationEvent,
  OperationHandle,
  OperationState,
  RenderArtifactRequest,
  ResolveSelectionRequest,
  ResolveSelectionResponse,
  RunRequest,
  TransformArtifactRequest,
  UpdateGistRequest,
  VerifyArtifactRequest,
} from './types'
import { decodeWire, stringifyWire } from './wire'

export class ApiError extends Error {
  readonly status: number
  readonly problem: ApiProblem | null

  constructor(status: number, problem: ApiProblem | null, fallbackMessage: string) {
    super(problem?.message ?? problem?.detail ?? problem?.title ?? fallbackMessage)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }
}

async function readProblem(response: Response): Promise<ApiProblem | null> {
  try {
    return decodeWire<ApiProblem>(await response.json())
  } catch {
    return null
  }
}

async function requestJson<T>(path: string, init: Omit<RequestInit, 'body'> & { body?: unknown } = {}, signal?: AbortSignal): Promise<T> {
  const { body, ...requestWithoutBody } = init
  const headers = new Headers(requestWithoutBody.headers)
  if (body !== undefined) headers.set('content-type', 'application/json')

  const requestInit: RequestInit = {
    ...requestWithoutBody,
    headers,
    ...(body === undefined ? {} : { body: stringifyWire(body) }),
  }
  if (signal) requestInit.signal = signal
  const response = await fetch(path, requestInit)
  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response), `Gateway request failed (${response.status}).`)
  }

  return decodeWire<T>(await response.json())
}

export function getCatalog(signal?: AbortSignal): Promise<CatalogDocument> {
  return requestJson<CatalogDocument>('/api/v1/catalog', {}, signal)
}

export function resolveSelection(request: ResolveSelectionRequest, signal?: AbortSignal): Promise<ResolveSelectionResponse> {
  return operationConnection().resolveSelection(request, signal)
}

/**
 * Resolves a secondary pipeline without changing the shared operation
 * connection's current selection. Read-only result views use this for a
 * language service that is different from the source editor's language.
 */
export function resolveSelectionHttp(request: ResolveSelectionRequest, signal?: AbortSignal): Promise<ResolveSelectionResponse> {
  return requestJson<ResolveSelectionResponse>('/api/v1/selections/resolve', { method: 'POST', body: request }, signal)
}

export function openLanguageSession(request: OpenLanguageSessionRequest, signal?: AbortSignal): Promise<GatewayLanguageSession> {
  return openLanguageSessionWithRecovery(request, signal)
}

/** Opens a session with a private resolution request for restart recovery. */
export function openLanguageSessionWithResolution(request: OpenLanguageSessionRequest, resolutionRequest: ResolveSelectionRequest, signal?: AbortSignal): Promise<GatewayLanguageSession> {
  return openLanguageSessionWithRecovery(request, signal, resolutionRequest)
}

async function openLanguageSessionWithRecovery(request: OpenLanguageSessionRequest, signal?: AbortSignal, resolutionRequest?: ResolveSelectionRequest): Promise<GatewayLanguageSession> {
  try {
    return await requestJson<GatewayLanguageSession>('/api/v1/language-sessions', { method: 'POST', body: request }, signal)
  } catch (error) {
    // Resolution IDs are intentionally in-memory Gateway capabilities. A
    // Gateway restart invalidates the ID held by an already-open workbench.
    // Re-resolve the exact selection through the shared command connection,
    // then retry the language-session request once with the replacement ID.
    if (!isInvalidPipelineResolution(error)) throw error
    const refreshed = resolutionRequest
      ? {
          response: await resolveSelectionHttp(resolutionRequest, signal),
          request: resolutionRequest,
        }
      : await refreshLatestSelectionForLanguageSession(request, signal)
    if (!refreshed || !matchesLanguageSessionResolution(request, refreshed.response)) throw error
    return requestJson<GatewayLanguageSession>(
      '/api/v1/language-sessions',
      {
        method: 'POST',
        body: {
          ...request,
          pipelineResolutionId: refreshed.response.pipelineResolutionId,
        },
      },
      signal,
    )
  }
}

function matchesLanguageSessionResolution(request: OpenLanguageSessionRequest, response: ResolveSelectionResponse): boolean {
  const selection = response.effectiveSelection
  return selection.languageId === request.languageId && selection.toolchainId === request.toolchainId && selection.referenceSetId === request.referenceSetId && response.pipelinePlan.referenceSetId === request.referenceSetId
}

async function refreshLatestSelectionForLanguageSession(request: OpenLanguageSessionRequest, signal?: AbortSignal): Promise<{
  response: ResolveSelectionResponse
  request: ResolveSelectionRequest
} | null> {
  const refreshed = await operationConnection().refreshLatestSelection(signal, {
    languageId: request.languageId,
    toolchainId: request.toolchainId,
    referenceSetId: request.referenceSetId,
    workspaceRevision: request.workspace.revision,
    buildMode: request.workspace.buildOptions.configuration,
  })
  if (!refreshed) return null

  // The operation connection is shared by all workbench selections. Do not
  // apply a response belonging to another language/toolchain to this session.
  const selection = refreshed.response.effectiveSelection
  if (selection.languageId !== request.languageId || selection.toolchainId !== request.toolchainId || selection.referenceSetId !== request.referenceSetId || refreshed.response.pipelinePlan.referenceSetId !== request.referenceSetId) {
    return null
  }
  return {
    response: refreshed.response,
    request: refreshed.request,
  }
}

function isInvalidPipelineResolution(error: unknown): boolean {
  return error instanceof ApiError && error.status === 400 && (error.problem?.error ?? error.problem?.code) === 'invalid-pipeline-resolution'
}

function isStaleCatalog(error: unknown): boolean {
  return error instanceof ApiError && error.status === 400 && (error.problem?.error ?? error.problem?.code) === 'stale-catalog'
}

function isRecord(value: object): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

export async function closeLanguageSession(sessionId: string): Promise<void> {
  const response = await fetch(`/api/v1/language-sessions/${encodeURIComponent(sessionId)}`, {
    method: 'DELETE',
  })
  if (response.ok || response.status === 404) return
  throw new ApiError(response.status, await readProblem(response), `Language session close failed (${response.status}).`)
}

export function languageSessionWebSocketUrl(path: string): string {
  if (!path.startsWith('/') || path.startsWith('//')) {
    throw new Error('Gateway returned an invalid language session WebSocket path.')
  }

  const url = new URL(path, window.location.origin)
  const segments = url.pathname.split('/')
  if (
    url.origin !== window.location.origin ||
    url.search !== '' ||
    url.hash !== '' ||
    segments.length !== 6 ||
    segments[1] !== 'api' ||
    segments[2] !== 'v1' ||
    segments[3] !== 'language-sessions' ||
    !/^[A-Za-z0-9_-]{1,128}$/.test(segments[4] ?? '') ||
    segments[5] !== 'lsp'
  ) {
    throw new Error('Gateway returned an invalid language session WebSocket path.')
  }

  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  return url.href
}

export function startBuild(request: BuildRequest, signal?: AbortSignal): Promise<OperationHandle> {
  return startOperation('build', request, signal)
}

export function startArtifactRender(request: RenderArtifactRequest, signal?: AbortSignal): Promise<OperationHandle> {
  return startOperation('artifact-render', request, signal)
}

export function startArtifactTransform(request: TransformArtifactRequest, signal?: AbortSignal): Promise<OperationHandle> {
  return startOperation('artifact-transform', request, signal)
}

export function startVerification(request: VerifyArtifactRequest, signal?: AbortSignal): Promise<OperationHandle> {
  return startOperation('verification', request, signal)
}

export function startRun(request: RunRequest, signal?: AbortSignal): Promise<OperationHandle> {
  return startOperation('run', request, signal)
}

export function startJit(request: JitRequest, signal?: AbortSignal): Promise<OperationHandle> {
  return startOperation('jit', request, signal)
}

export function startExplain(request: ExplainRequest, signal?: AbortSignal): Promise<OperationHandle> {
  return startOperation('explain', request, signal)
}

export function getGitHubAuthStatus(signal?: AbortSignal): Promise<GitHubAuthStatus> {
  return requestJson<GitHubAuthStatus>('/api/v1/auth/github/status', {}, signal)
}

export function startGitHubOAuth(returnPath: string, signal?: AbortSignal): Promise<GitHubOAuthStartResponse> {
  const query = new URLSearchParams({ ReturnPath: returnPath })
  return requestJson<GitHubOAuthStartResponse>(`/api/v1/auth/github/start?${query.toString()}`, {}, signal)
}

export async function logoutGitHub(csrfToken: string): Promise<void> {
  const response = await fetch('/api/v1/auth/github/logout', {
    method: 'POST',
    headers: { 'X-SharpLabNext-CSRF': csrfToken },
  })
  if (response.ok) return
  throw new ApiError(response.status, await readProblem(response), `GitHub logout failed (${response.status}).`)
}

export interface GistLoadOptions {
  target?: string | null
  branch?: string | null
  mode?: 'debug' | 'release' | null
}

export function getGist(id: string, options: GistLoadOptions = {}, signal?: AbortSignal): Promise<GistDocument> {
  const query = new URLSearchParams()
  if (options.target) query.set('Target', options.target)
  if (options.branch) query.set('Branch', options.branch)
  if (options.mode) query.set('Mode', options.mode)
  const suffix = query.size > 0 ? `?${query.toString()}` : ''
  return requestJson<GistDocument>(`/api/v1/shares/gists/${encodeURIComponent(id)}${suffix}`, {}, signal)
}

export function createGist(request: CreateGistRequest, csrfToken: string, signal?: AbortSignal): Promise<GistDocument> {
  return requestJson<GistDocument>(
    '/api/v1/shares/gists',
    {
      method: 'POST',
      headers: { 'X-SharpLabNext-CSRF': csrfToken },
      body: request,
    },
    signal,
  )
}

export function updateGist(id: string, request: UpdateGistRequest, csrfToken: string, signal?: AbortSignal): Promise<GistDocument> {
  return requestJson<GistDocument>(
    `/api/v1/shares/gists/${encodeURIComponent(id)}`,
    {
      method: 'PATCH',
      headers: { 'X-SharpLabNext-CSRF': csrfToken },
      body: request,
    },
    signal,
  )
}

export async function getOperationContent(operationId: string, contentRef: string, signal?: AbortSignal): Promise<string> {
  const match = /^sha256:([0-9a-f]{64})$/.exec(contentRef)
  if (!match) throw new Error('Gateway returned an invalid content reference.')

  const response = await fetch(`/api/v1/operations/${encodeURIComponent(operationId)}/contents/sha256/${match[1]}`, signal ? { signal } : undefined)
  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response), `Operation content request failed (${response.status}).`)
  }
  return response.text()
}

export function getOperation(operationId: string, signal?: AbortSignal): Promise<OperationState> {
  return operationConnection().send<OperationState>({ type: 'state', operationId }, signal)
}

export function cancelOperation(operationId: string): Promise<CancelResult> {
  return operationConnection().send<CancelResult>({
    type: 'cancel',
    operationId,
    reason: 'user',
  })
}

export type OperationEventStreamStatus = 'connecting' | 'open' | 'reconnecting' | 'closed' | 'error'
export type GatewayConnectionStatus = 'idle' | 'connecting' | 'open' | 'reconnecting' | 'closed'

interface OperationEventSubscription {
  onEvent: (event: OperationEvent) => void
  onStatus: (status: OperationEventStreamStatus) => void
  onError: (error: Error) => void
}

export function subscribeToOperationEvents(operationId: string, fromSequence: number, subscription: OperationEventSubscription): () => void {
  if (!Number.isSafeInteger(fromSequence) || fromSequence < 0) {
    throw new Error('Operation event sequence must be a non-negative safe integer.')
  }

  return operationConnection().subscribe(operationId, fromSequence, subscription)
}

export function operationCommandsWebSocketUrl(): string {
  const url = new URL('/api/v1/operations/ws', window.location.origin)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  return url.href
}

// Retained for clients that still use the compatibility event-only endpoint.
export function operationEventsWebSocketUrl(operationId: string, fromSequence: number): string {
  validateOperationSubscription(operationId, fromSequence)
  const url = new URL(`/api/v1/operations/${operationId}/events?FromSequence=${fromSequence}`, window.location.origin)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  return url.href
}

type OperationStartKind = 'build' | 'explain' | 'artifact-transform' | 'artifact-render' | 'verification' | 'run' | 'jit'

type OperationCommand =
  | { type: 'resolve-selection'; request: ResolveSelectionRequest }
  | { type: 'start'; operation: OperationStartKind; request: object }
  | { type: 'state'; operationId: string }
  | { type: 'cancel'; operationId: string; reason: string }
  | { type: 'subscribe'; operationId: string; fromSequence: number }

const operationCommandTimeoutMs = 10_000

interface OperationCommandResponse {
  type: 'response'
  commandId: string
  ok: boolean
  status: number
  payload?: unknown
  error?: ApiProblem | null
}

interface OperationCommandEvent {
  type: 'event'
  operationId: string
  event: OperationEvent
}

interface PendingOperationCommand {
  resolve: (value: unknown) => void
  reject: (error: Error) => void
  timeout: number
  removeAbortListener?: () => void
}

interface ActiveOperationSubscription extends OperationEventSubscription {
  lastSequence: number
  terminal: boolean
}

class OperationCommandConnection {
  private socket: WebSocket | null = null
  private connecting: Promise<void> | null = null
  private connectingReject: ((error: Error) => void) | null = null
  private pending = new Map<string, PendingOperationCommand>()
  private subscriptions = new Map<string, ActiveOperationSubscription>()
  private reconnectTimer: number | null = null
  private reconnectAttempt = 0
  private nextCommandId = 1
  private disposed = false
  private selectionCommandTail: Promise<void> = Promise.resolve()
  private latestSelection: {
    request: ResolveSelectionRequest
    pipelineResolutionId: string
  } | null = null
  private selectionSocket: WebSocket | null = null
  private connectionStatus: GatewayConnectionStatus = 'idle'
  private connectionStatusListeners = new Set<(status: GatewayConnectionStatus) => void>()

  subscribeToConnectionStatus(listener: (status: GatewayConnectionStatus) => void): () => void {
    this.connectionStatusListeners.add(listener)
    listener(this.connectionStatus)
    return () => this.connectionStatusListeners.delete(listener)
  }

  resolveSelection(request: ResolveSelectionRequest, signal?: AbortSignal): Promise<ResolveSelectionResponse> {
    const stableRequest = { ...request }
    return this.enqueueSelectionCommand(async () => {
      if (signal?.aborted) {
        throw new DOMException('The operation command was aborted.', 'AbortError')
      }

      // The server may process a command after its browser-side caller is aborted.
      // Treat this socket as unbound until a successful response establishes the
      // last valid selection again.
      this.selectionSocket = null
      const resolved = await this.resolveSelectionOnSocket(stableRequest, signal)
      this.latestSelection = {
        request: resolved.request,
        pipelineResolutionId: resolved.payload.pipelineResolutionId,
      }
      this.selectionSocket = resolved.socket
      return resolved.payload
    })
  }

  /**
   * Re-resolve the last selection on the current command socket. This is used
   * by language-session recovery after a Gateway restart, where the previous
   * in-memory pipeline resolution no longer exists. The request is deliberately
   * taken from the last successful selection rather than reconstructed from a
   * language-session request (which does not contain an output ID/runtime ID).
   */
  refreshLatestSelection(
    signal?: AbortSignal,
    expected?: {
      languageId: string
      toolchainId: string
      referenceSetId: string
      workspaceRevision: number
      buildMode: ResolveSelectionRequest['buildMode']
    },
  ): Promise<{
    response: ResolveSelectionResponse
    request: ResolveSelectionRequest
  } | null> {
    return this.enqueueSelectionCommand(async () => {
      const latest = this.latestSelection
      if (!latest) return null
      if (
        expected &&
        (latest.request.languageId !== expected.languageId ||
          latest.request.toolchainId !== expected.toolchainId ||
          latest.request.referenceSetId !== expected.referenceSetId ||
          latest.request.workspaceRevision !== expected.workspaceRevision ||
          latest.request.buildMode !== expected.buildMode)
      ) {
        return null
      }
      if (signal?.aborted) {
        throw new DOMException('The operation command was aborted.', 'AbortError')
      }

      // A browser can keep an OPEN TCP/WebSocket object after the Gateway
      // process that created the pipeline has been replaced.  Closing only the
      // language session is insufficient in that state: the next command may
      // still be routed through the old connection (or never receive a
      // response).  Force a new command socket before re-resolving, then replay
      // active operation subscriptions on that socket.
      let lastError: unknown = null
      for (let attempt = 0; attempt < 2; attempt += 1) {
        try {
          await this.forceReconnect(signal)
          const resolved = await this.resolveSelectionOnSocket(latest.request, signal)
          this.latestSelection = {
            request: resolved.request,
            pipelineResolutionId: resolved.payload.pipelineResolutionId,
          }
          this.selectionSocket = resolved.socket
          return { response: resolved.payload, request: resolved.request }
        } catch (error) {
          lastError = error
          if (signal?.aborted) throw error
        }
      }
      throw lastError instanceof Error ? lastError : new Error('Pipeline resolution refresh failed.')
    })
  }

  start(operation: OperationStartKind, request: object, signal?: AbortSignal): Promise<OperationHandle> {
    return this.enqueueSelectionCommand(async () => {
      await this.ensureLatestSelection(signal)
      const requestWithCurrentResolution = this.requestWithCurrentResolution(request)
      const { payload } = await this.sendCommand<OperationHandle>({ type: 'start', operation, request: requestWithCurrentResolution }, signal)
      return payload
    })
  }

  async send<T>(command: OperationCommand, signal?: AbortSignal): Promise<T> {
    const { payload } = await this.sendCommand<T>(command, signal)
    return payload
  }

  private async sendCommand<T>(command: OperationCommand, signal?: AbortSignal): Promise<{ payload: T; socket: WebSocket }> {
    if (signal?.aborted) throw new DOMException('The operation command was aborted.', 'AbortError')
    await this.ensureOpen()
    const socket = this.socket
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      throw new Error('The operation command WebSocket is not open.')
    }

    const commandId = `cmd_${this.nextCommandId++}`
    return new Promise<{ payload: T; socket: WebSocket }>((resolve, reject) => {
      let pending!: PendingOperationCommand
      const timeout = window.setTimeout(() => {
        if (this.pending.get(commandId) !== pending) return
        this.pending.delete(commandId)
        pending.removeAbortListener?.()
        reject(new Error(`Gateway operation command '${command.type}' timed out.`))
        // A command that receives no response is indistinguishable from a
        // half-open WebSocket. Detach it so the next command cannot reuse the
        // same dead socket; refreshLatestSelection will establish a bounded
        // replacement and replay subscriptions.
        if (this.socket === socket) {
          this.socket = null
          this.selectionSocket = null
          try {
            socket.close(1000, 'Gateway operation command timed out.')
          } catch {
            // The detached socket is no longer used by this connection.
          }
          this.scheduleReconnect()
        }
      }, operationCommandTimeoutMs)
      pending = {
        resolve: (value) => resolve({ payload: value as T, socket }),
        reject,
        timeout,
      }
      if (signal) {
        const abort = () => {
          if (this.pending.get(commandId) !== pending) return
          this.pending.delete(commandId)
          window.clearTimeout(pending.timeout)
          reject(new DOMException('The operation command was aborted.', 'AbortError'))
        }
        signal.addEventListener('abort', abort, { once: true })
        pending.removeAbortListener = () => signal.removeEventListener('abort', abort)
      }
      this.pending.set(commandId, pending)
      try {
        socket.send(stringifyWire({ ...command, commandId }))
      } catch (error) {
        this.pending.delete(commandId)
        window.clearTimeout(pending.timeout)
        pending.removeAbortListener?.()
        reject(error instanceof Error ? error : new Error('The operation command could not be sent.'))
      }
    })
  }

  subscribe(operationId: string, fromSequence: number, subscription: OperationEventSubscription): () => void {
    validateOperationSubscription(operationId, fromSequence)
    const active: ActiveOperationSubscription = {
      ...subscription,
      lastSequence: fromSequence,
      terminal: false,
    }
    this.subscriptions.set(operationId, active)
    subscription.onStatus(this.socket?.readyState === WebSocket.OPEN ? 'open' : 'connecting')
    void this.ensureOpen()
      .then(() => this.startSubscription(operationId, active))
      .catch((error: unknown) => {
        if (error instanceof ApiError) this.failSubscription(active, error)
        else if (!active.terminal) active.onStatus('reconnecting')
      })

    return () => {
      if (this.subscriptions.get(operationId) === active) this.subscriptions.delete(operationId)
      active.terminal = true
      subscription.onStatus('closed')
    }
  }

  dispose(): void {
    this.disposed = true
    if (this.reconnectTimer !== null) window.clearTimeout(this.reconnectTimer)
    this.reconnectTimer = null
    this.connectingReject?.(new Error('Operation command client disposed.'))
    this.connectingReject = null
    this.socket?.close(1000, 'Operation command client disposed.')
    this.socket = null
    this.selectionSocket = null
    this.connecting = null
    for (const pending of this.pending.values()) pending.reject(new Error('Operation command client disposed.'))
    this.pending.clear()
    this.subscriptions.clear()
    this.setConnectionStatus('closed')
    this.connectionStatusListeners.clear()
  }

  private ensureOpen(): Promise<void> {
    if (this.disposed) return Promise.reject(new Error('Operation command client is disposed.'))
    if (this.socket?.readyState === WebSocket.OPEN) return Promise.resolve()
    if (this.connecting) return this.connecting

    this.setConnectionStatus(this.connectionStatus === 'idle' ? 'connecting' : 'reconnecting')

    this.connecting = new Promise<void>((resolve, reject) => {
      this.connectingReject = reject
      const socket = new WebSocket(operationCommandsWebSocketUrl())
      this.socket = socket
      socket.onopen = () => {
        if (this.socket !== socket) return
        this.connecting = null
        this.connectingReject = null
        this.reconnectAttempt = 0
        this.setConnectionStatus('open')
        for (const subscription of this.subscriptions.values()) {
          if (!subscription.terminal) subscription.onStatus('open')
        }
        resolve()
      }
      socket.onmessage = (message) => this.onMessage(socket, message)
      socket.onerror = () => {
        if (this.socket !== socket) return
        this.setConnectionStatus(this.disposed ? 'closed' : 'reconnecting')
        for (const subscription of this.subscriptions.values()) {
          if (!subscription.terminal) subscription.onStatus('reconnecting')
        }
      }
      socket.onclose = () => {
        if (this.socket !== socket) return
        this.socket = null
        this.selectionSocket = null
        const wasConnecting = this.connecting !== null
        this.connecting = null
        this.connectingReject = null
        this.setConnectionStatus(this.disposed ? 'closed' : 'reconnecting')
        if (wasConnecting) reject(new Error('The operation command WebSocket closed before opening.'))
        this.rejectPending(new Error('The operation command WebSocket disconnected.'))
        this.scheduleReconnect()
      }
    })
    return this.connecting
  }

  /**
   * Detach the current command socket and establish a fresh one.  The socket
   * identity is cleared before calling close(), so a delayed native `close`
   * event from the old object cannot mutate the new connection.  Any callers
   * waiting for the old handshake are rejected, and active event subscriptions
   * are explicitly replayed after the replacement opens.
   */
  private async forceReconnect(signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error('Operation command client is disposed.')
    if (signal?.aborted) {
      throw new DOMException('The operation command was aborted.', 'AbortError')
    }

    if (this.reconnectTimer !== null) {
      window.clearTimeout(this.reconnectTimer)
      this.reconnectTimer = null
    }

    const staleSocket = this.socket
    this.socket = null
    this.selectionSocket = null
    this.connectingReject?.(new Error('The operation command WebSocket was replaced.'))
    this.connectingReject = null
    this.connecting = null
    this.rejectPending(new Error('The operation command WebSocket was replaced.'))
    if (staleSocket && staleSocket.readyState !== WebSocket.CLOSED) {
      try {
        staleSocket.close(1000, 'Refreshing the Gateway pipeline resolution.')
      } catch {
        // The replacement connection remains independent of the stale object.
      }
    }

    await this.ensureOpen()
    if (signal?.aborted) {
      throw new DOMException('The operation command was aborted.', 'AbortError')
    }

    // A forced reconnect bypasses scheduleReconnect(), so preserve the event
    // stream contract explicitly instead of dropping active subscriptions.
    await Promise.all([...this.subscriptions.entries()].filter(([, subscription]) => !subscription.terminal).map(([operationId, subscription]) => this.startSubscription(operationId, subscription)))
  }

  /**
   * Resolve a selection on the currently open command socket. Catalog
   * revisions are part of the request contract, so a page that survived a
   * release can receive `stale-catalog` even after the socket itself has been
   * repaired. Fetch the current catalog once and retry with the same language,
   * toolchain, reference set, output, runtime, build mode, and workspace
   * revision; only the catalog revision is replaced.
   */
  private async resolveSelectionOnSocket(request: ResolveSelectionRequest, signal?: AbortSignal): Promise<{
    payload: ResolveSelectionResponse
    socket: WebSocket
    request: ResolveSelectionRequest
  }> {
    let currentRequest = request
    for (let attempt = 0; attempt < 2; attempt += 1) {
      this.selectionSocket = null
      try {
        const { payload, socket } = await this.sendCommand<ResolveSelectionResponse>({ type: 'resolve-selection', request: currentRequest }, signal)
        return { payload, socket, request: currentRequest }
      } catch (error) {
        if (!isStaleCatalog(error) || attempt !== 0) throw error
        const catalog = await getCatalog(signal)
        if (!catalog.revision || catalog.revision === currentRequest.catalogRevision) throw error
        currentRequest = {
          ...currentRequest,
          catalogRevision: catalog.revision,
        }
      }
    }
    throw new Error('Selection resolution failed after refreshing the catalog.')
  }

  private onMessage(socket: WebSocket, message: MessageEvent): void {
    if (this.socket !== socket) return
    if (typeof message.data !== 'string') {
      this.failAllSubscriptions(new Error('Gateway sent a non-text operation command message.'))
      return
    }

    let envelope: OperationCommandResponse | OperationCommandEvent
    try {
      envelope = decodeWire<OperationCommandResponse | OperationCommandEvent>(JSON.parse(message.data))
    } catch (error) {
      this.failAllSubscriptions(error)
      return
    }

    if (envelope.type === 'response') {
      const pending = this.pending.get(envelope.commandId)
      if (!pending) return
      this.pending.delete(envelope.commandId)
      window.clearTimeout(pending.timeout)
      pending.removeAbortListener?.()
      if (envelope.ok) pending.resolve(envelope.payload)
      else pending.reject(new ApiError(envelope.status, envelope.error ?? null, `Gateway command failed (${envelope.status}).`))
      return
    }

    if (envelope.type !== 'event') {
      this.failAllSubscriptions(new Error('Gateway sent an unknown operation command message.'))
      return
    }
    const subscription = this.subscriptions.get(envelope.operationId)
    const operationEvent = envelope.event
    if (!subscription || subscription.terminal) return
    if (operationEvent.operationId !== envelope.operationId || !Number.isSafeInteger(operationEvent.sequence) || operationEvent.sequence <= 0) {
      this.failSubscription(subscription, new Error('Gateway sent an invalid operation event identity.'))
      return
    }
    if (operationEvent.sequence <= subscription.lastSequence) return
    subscription.lastSequence = operationEvent.sequence
    subscription.onEvent(operationEvent)
    if (operationEvent.payload.kind === 'completed' || operationEvent.payload.kind === 'failed') {
      subscription.terminal = true
      subscription.onStatus('closed')
    }
  }

  private startSubscription(operationId: string, subscription: ActiveOperationSubscription): Promise<void> {
    if (subscription.terminal || this.subscriptions.get(operationId) !== subscription) return Promise.resolve()
    return this.send({
      type: 'subscribe',
      operationId,
      fromSequence: subscription.lastSequence,
    }).then(() => undefined)
  }

  private scheduleReconnect(): void {
    const active = [...this.subscriptions.entries()].filter(([, subscription]) => !subscription.terminal)
    if (this.disposed || this.reconnectTimer !== null) return
    this.reconnectAttempt += 1
    this.setConnectionStatus('reconnecting')
    for (const [, subscription] of active) subscription.onStatus('reconnecting')
    const delay = Math.min(250 * 2 ** Math.min(this.reconnectAttempt - 1, 4), 4_000)
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = null
      void this.ensureOpen()
        .then(() => Promise.all([...this.subscriptions.entries()].filter(([, subscription]) => !subscription.terminal).map(([operationId, subscription]) => this.startSubscription(operationId, subscription))))
        .catch(() => this.scheduleReconnect())
    }, delay)
  }

  private setConnectionStatus(status: GatewayConnectionStatus): void {
    if (this.connectionStatus === status) return
    this.connectionStatus = status
    for (const listener of this.connectionStatusListeners) listener(status)
  }

  private rejectPending(error: Error): void {
    for (const pending of this.pending.values()) {
      window.clearTimeout(pending.timeout)
      pending.removeAbortListener?.()
      pending.reject(error)
    }
    this.pending.clear()
  }

  private failSubscription(subscription: ActiveOperationSubscription, error: unknown): void {
    subscription.terminal = true
    subscription.onStatus('error')
    subscription.onError(error instanceof Error ? error : new Error('Invalid operation command message.'))
  }

  private failAllSubscriptions(error: unknown): void {
    for (const subscription of this.subscriptions.values()) {
      if (!subscription.terminal) this.failSubscription(subscription, error)
    }
  }

  private enqueueSelectionCommand<T>(command: () => Promise<T>): Promise<T> {
    const result = this.selectionCommandTail.then(command)
    this.selectionCommandTail = result.then(() => undefined, () => undefined)
    return result
  }

  private async ensureLatestSelection(signal?: AbortSignal): Promise<void> {
    const latest = this.latestSelection
    if (!latest) return

    await this.ensureOpen()
    const socket = this.socket
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      throw new Error('The operation command WebSocket is not open.')
    }
    if (this.selectionSocket === socket) return

    const { payload, socket: replaySocket, request } = await this.resolveSelectionOnSocket(latest.request, signal)
    if (this.socket !== replaySocket || replaySocket.readyState !== WebSocket.OPEN) {
      throw new Error('The operation command WebSocket disconnected while restoring its selection.')
    }
    // Resolution IDs are scoped to the Gateway process. A replacement socket
    // after a process restart is expected to receive a fresh ID; retaining the
    // old equality check made every subsequent operation fail permanently.
    this.latestSelection = {
      request,
      pipelineResolutionId: payload.pipelineResolutionId,
    }
    this.selectionSocket = replaySocket
  }

  private requestWithCurrentResolution(request: object): object {
    const latest = this.latestSelection
    if (!latest || !isRecord(request) || typeof request.pipelineResolutionId !== 'string') {
      return request
    }
    if (request.pipelineResolutionId === latest.pipelineResolutionId) return request
    return { ...request, pipelineResolutionId: latest.pipelineResolutionId }
  }
}

let sharedOperationConnection: OperationCommandConnection | null = null

function operationConnection(): OperationCommandConnection {
  sharedOperationConnection ??= new OperationCommandConnection()
  return sharedOperationConnection
}

function startOperation<TRequest extends object>(operation: OperationStartKind, request: TRequest, signal?: AbortSignal): Promise<OperationHandle> {
  return operationConnection().start(operation, request, signal)
}

function validateOperationSubscription(operationId: string, fromSequence: number): void {
  if (!/^op_[0-9a-f]{32}$/.test(operationId)) {
    throw new Error('Gateway returned an invalid operation ID.')
  }
  if (!Number.isSafeInteger(fromSequence) || fromSequence < 0) {
    throw new Error('Operation event sequence must be a non-negative safe integer.')
  }
}

export function resetOperationCommandConnectionForTests(): void {
  sharedOperationConnection?.dispose()
  sharedOperationConnection = null
}

export function subscribeToGatewayConnectionStatus(listener: (status: GatewayConnectionStatus) => void): () => void {
  return operationConnection().subscribeToConnectionStatus(listener)
}
