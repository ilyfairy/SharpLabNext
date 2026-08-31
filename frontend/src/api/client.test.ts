import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createGist,
  getGist,
  getOperationContent,
  languageSessionWebSocketUrl,
  openLanguageSession,
  openLanguageSessionWithResolution,
  operationCommandsWebSocketUrl,
  operationEventsWebSocketUrl,
  resetOperationCommandConnectionForTests,
  resolveSelection,
  resolveSelectionHttp,
  startExplain,
  startRun,
  subscribeToGatewayConnectionStatus,
  subscribeToOperationEvents,
  updateGist,
} from './client'
import type { ExplainRequest, GistWorkspaceState, OpenLanguageSessionRequest, OperationEvent, ResolveSelectionRequest, ResolveSelectionResponse, RunRequest } from './types'
import { decodeWire, stringifyWire } from './wire'

class MockWebSocket {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSED = 3
  static instances: MockWebSocket[] = []

  readonly url: string
  readonly closeCalls: Array<[number | undefined, string | undefined]> = []
  readonly sent: string[] = []
  readyState = MockWebSocket.CONNECTING
  onopen: (() => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onerror: (() => void) | null = null
  onclose: (() => void) | null = null

  constructor(url: string | URL) {
    this.url = url.toString()
    MockWebSocket.instances.push(this)
  }

  open(): void {
    this.readyState = MockWebSocket.OPEN
    this.onopen?.()
  }

  send(data: string): void {
    this.sent.push(data)
  }

  emitResponse(commandId: string, payload: unknown, status = 200): void {
    this.onmessage?.(
      new MessageEvent('message', {
        data: stringifyWire({
          type: 'response',
          commandId,
          ok: true,
          status,
          payload,
        }),
      }),
    )
  }

  emitFailure(commandId: string, status: number, error: Record<string, unknown>): void {
    this.onmessage?.(
      new MessageEvent('message', {
        data: stringifyWire({
          type: 'response',
          commandId,
          ok: false,
          status,
          error,
        }),
      }),
    )
  }

  emitOperation(operationEvent: OperationEvent): void {
    this.onmessage?.(
      new MessageEvent('message', {
        data: stringifyWire({
          type: 'event',
          operationId: operationEvent.operationId,
          event: operationEvent,
        }),
      }),
    )
  }

  disconnect(): void {
    this.readyState = MockWebSocket.CLOSED
    this.onclose?.()
  }

  close(code?: number, reason?: string): void {
    this.closeCalls.push([code, reason])
    this.readyState = MockWebSocket.CLOSED
    this.onclose?.()
  }
}

afterEach(() => {
  resetOperationCommandConnectionForTests()
  vi.useRealTimers()
  vi.unstubAllGlobals()
  MockWebSocket.instances = []
})

async function waitForSocket(index: number): Promise<MockWebSocket> {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const socket = MockWebSocket.instances[index]
    if (socket) return socket
    await Promise.resolve()
  }
  throw new Error(`Expected operation WebSocket ${index}.`)
}

async function waitForCommand<T>(socket: MockWebSocket, index: number): Promise<T> {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const command = socket.sent[index]
    if (command) return decodeWire<T>(JSON.parse(command))
    await Promise.resolve()
  }
  throw new Error(`Expected operation WebSocket command ${index}.`)
}

describe('languageSessionWebSocketUrl', () => {
  it('converts only the Gateway same-origin language-session path', () => {
    const url = languageSessionWebSocketUrl('/api/v1/language-sessions/glsp_0123456789abcdef/lsp')

    expect(url).toBe(`${window.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${window.location.host}/api/v1/language-sessions/glsp_0123456789abcdef/lsp`)
  })

  it.each([
    'ws://attacker.test/lsp',
    'https://attacker.test/lsp',
    '//attacker.test/lsp',
    '/api/v1/language-sessions/session/lsp?upstream=http://attacker.test',
    '/api/v1/language-sessions/session/lsp#fragment',
    '/api/v1/language-sessions/session/../lsp',
    '/api/v1/language-sessions/session%2Fother/lsp',
    '/api/v1/operations/session/lsp',
  ])('rejects a browser-selectable or malformed upstream path: %s', (path) => {
    expect(() => languageSessionWebSocketUrl(path)).toThrow('Gateway returned an invalid language session WebSocket path.')
  })
})

describe('operationEventsWebSocketUrl', () => {
  it('creates a same-origin WebSocket URL with an explicit resume sequence', () => {
    const operationId = `op_${'a'.repeat(32)}`

    expect(operationEventsWebSocketUrl(operationId, 17)).toBe(`${window.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${window.location.host}/api/v1/operations/${operationId}/events?FromSequence=17`)
  })

  it.each(['op-build', `op_${'A'.repeat(32)}`, `op_${'a'.repeat(31)}`, `op_${'a'.repeat(33)}`, `op_${'g'.repeat(32)}`])('rejects a malformed operation ID: %s', (operationId) => {
    expect(() => operationEventsWebSocketUrl(operationId, 0)).toThrow('Gateway returned an invalid operation ID.')
  })

  it.each([-1, 0.5, Number.MAX_SAFE_INTEGER + 1])('rejects an invalid resume sequence: %s', (fromSequence) => {
    expect(() => operationEventsWebSocketUrl(`op_${'a'.repeat(32)}`, fromSequence)).toThrow('Operation event sequence must be a non-negative safe integer.')
  })
})

describe('operationCommandsWebSocketUrl', () => {
  it('uses one same-origin operation control endpoint', () => {
    expect(operationCommandsWebSocketUrl()).toBe(`${window.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${window.location.host}/api/v1/operations/ws`)
  })

  it('reports disconnect and reconnect for the persistent operation socket', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('WebSocket', MockWebSocket)
    const connectionStatuses: string[] = []
    const streamStatuses: string[] = []
    const unsubscribeStatus = subscribeToGatewayConnectionStatus((status) => connectionStatuses.push(status))
    const unsubscribeOperation = subscribeToOperationEvents(`op_${'c'.repeat(32)}`, 0, {
      onEvent: () => undefined,
      onStatus: (status) => streamStatuses.push(status),
      onError: () => undefined,
    })

    expect(connectionStatuses).toEqual(['idle', 'connecting'])
    const first = await waitForSocket(0)
    first.open()
    await Promise.resolve()
    expect(connectionStatuses.at(-1)).toBe('open')

    first.disconnect()
    expect(connectionStatuses.at(-1)).toBe('reconnecting')
    expect(streamStatuses.at(-1)).toBe('reconnecting')

    await vi.advanceTimersByTimeAsync(250)
    const replacement = await waitForSocket(1)
    replacement.open()
    await Promise.resolve()
    expect(connectionStatuses.at(-1)).toBe('open')

    unsubscribeOperation()
    unsubscribeStatus()
  })

  it('resolves selections over the shared command socket', async () => {
    vi.stubGlobal('WebSocket', MockWebSocket)
    const request: ResolveSelectionRequest = {
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      outputId: 'decompiled-csharp',
      runtimeId: null,
      buildMode: 'release',
      catalogRevision: 'catalog-test',
      workspaceRevision: 42,
    }
    const response: ResolveSelectionResponse = {
      effectiveSelection: {
        languageId: 'csharp',
        toolchainId: 'roslyn-stable',
        referenceSetId: 'net10-ref',
        outputId: 'decompiled-csharp',
        runtimeId: null,
      },
      selectionChanges: [],
      effectiveCapabilities: {
        languageServerCapabilities: [],
        buildCapabilities: ['managed-pe'],
        outputCapabilities: ['decompiled-csharp'],
        runtimeCapabilities: [],
      },
      pipelineResolutionId: 'pipeline-ws-selection',
      pipelinePlan: {
        releaseId: 'content',
        languageWorkerId: 'roslyn-stable',
        compilerWorkerId: 'roslyn-stable',
        referenceSetId: 'net10-ref',
        stages: [],
        runtimeId: null,
        securityPolicyId: 'compiler-default',
        workerImageIds: [],
      },
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
    }

    const pending = resolveSelection(request)
    const socket = await waitForSocket(0)
    socket.open()
    const command = await waitForCommand<{
      commandId: string
      type: string
      request: ResolveSelectionRequest
    }>(socket, 0)
    expect(JSON.parse(socket.sent[0] ?? '{}')).toMatchObject({
      Type: 'resolve-selection',
      CommandId: expect.any(String),
      Request: {
        LanguageId: request.languageId,
        OutputId: request.outputId,
        WorkspaceRevision: request.workspaceRevision,
      },
    })
    expect(command).toMatchObject({ type: 'resolve-selection', request })
    socket.emitResponse(command.commandId, response)

    await expect(pending).resolves.toEqual(response)
  })

  it('bounds an ignored command response and detaches the half-open socket', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('WebSocket', MockWebSocket)
    const pending = resolveSelection({
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      outputId: 'decompiled-csharp',
      runtimeId: null,
      buildMode: 'release',
      catalogRevision: 'catalog-test',
      workspaceRevision: 42,
    })
    const socket = await waitForSocket(0)
    socket.open()
    await waitForCommand(socket, 0)

    const rejected = expect(pending).rejects.toThrow("Gateway operation command 'resolve-selection' timed out.")
    await vi.advanceTimersByTimeAsync(10_000)
    await rejected
    expect(socket.closeCalls).toContainEqual([1000, 'Gateway operation command timed out.'])
  })

  it('restores the last valid selection once before starts on a replacement socket', async () => {
    vi.stubGlobal('WebSocket', MockWebSocket)
    const request: ResolveSelectionRequest = {
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      outputId: 'run',
      runtimeId: 'dotnet-10-linux-x64',
      buildMode: 'release',
      catalogRevision: 'catalog-test',
      workspaceRevision: 42,
    }
    const response: ResolveSelectionResponse = {
      effectiveSelection: {
        languageId: 'csharp',
        toolchainId: 'roslyn-stable',
        referenceSetId: 'net10-ref',
        outputId: 'run',
        runtimeId: 'dotnet-10-linux-x64',
      },
      selectionChanges: [],
      effectiveCapabilities: {
        languageServerCapabilities: [],
        buildCapabilities: ['managed-pe'],
        outputCapabilities: ['run'],
        runtimeCapabilities: ['run'],
      },
      pipelineResolutionId: 'pr_reconnect',
      pipelinePlan: {
        releaseId: 'content',
        languageWorkerId: 'roslyn-stable',
        compilerWorkerId: 'roslyn-stable',
        referenceSetId: 'net10-ref',
        stages: [
          { id: 'artifact', kind: 'build', providerId: 'roslyn-stable' },
          { id: 'run', kind: 'run', providerId: 'dotnet-10-linux-x64' },
        ],
        runtimeId: 'dotnet-10-linux-x64',
        securityPolicyId: 'runtime-job-default',
        workerImageIds: [],
      },
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
    }
    const runRequest = (suffix: string): RunRequest => ({
      requestId: `req-${suffix}`,
      idempotencyKey: `run:req-${suffix}`,
      pipelineResolutionId: response.pipelineResolutionId,
      artifactRef: `sha256:${'a'.repeat(64)}`,
      runtimeProfileId: 'dotnet-10-linux-x64',
      options: {
        arguments: [],
        stdin: null,
        instrumentation: 'none',
        securityPolicyId: 'runtime-job-default',
      },
      deadlineUtc: new Date(Date.now() + 30_000).toISOString(),
    })

    const initialResolution = resolveSelection(request)
    const first = await waitForSocket(0)
    first.open()
    const initialCommand = await waitForCommand<{
      commandId: string
      type: string
    }>(first, 0)
    first.emitResponse(initialCommand.commandId, response)
    await expect(initialResolution).resolves.toEqual(response)
    first.disconnect()

    const firstRun = startRun(runRequest('first'))
    const replacement = await waitForSocket(1)
    replacement.open()

    const replay = await waitForCommand<{
      commandId: string
      type: string
      request: ResolveSelectionRequest
    }>(replacement, 0)
    expect(replacement.sent).toHaveLength(1)
    expect(replay).toMatchObject({ type: 'resolve-selection', request })
    const restartedResponse = {
      ...response,
      pipelineResolutionId: 'pr_after_gateway_restart',
    }
    replacement.emitResponse(replay.commandId, restartedResponse)

    const firstStart = await waitForCommand<{
      commandId: string
      type: string
      operation: string
      request: RunRequest
    }>(replacement, 1)
    expect(replacement.sent).toHaveLength(2)
    expect(firstStart).toMatchObject({ type: 'start', operation: 'run' })
    expect(firstStart.request.pipelineResolutionId).toBe(restartedResponse.pipelineResolutionId)
    replacement.emitResponse(firstStart.commandId, {
      operationId: `op_${'a'.repeat(32)}`,
      requestId: firstStart.request.requestId,
      createdAtUtc: new Date().toISOString(),
      isExisting: false,
    })
    await expect(firstRun).resolves.toMatchObject({ requestId: 'req-first' })

    const secondRun = startRun(runRequest('second'))
    const secondStart = await waitForCommand<{
      commandId: string
      type: string
      operation: string
      request: RunRequest
    }>(replacement, 2)
    expect(replacement.sent).toHaveLength(3)
    expect(secondStart).toMatchObject({ type: 'start', operation: 'run' })
    replacement.emitResponse(secondStart.commandId, {
      operationId: `op_${'b'.repeat(32)}`,
      requestId: secondStart.request.requestId,
      createdAtUtc: new Date().toISOString(),
      isExisting: false,
    })
    await expect(secondRun).resolves.toMatchObject({ requestId: 'req-second' })

    const commands = [...first.sent, ...replacement.sent].map((value) => decodeWire<{ type: string }>(JSON.parse(value)))
    expect(commands.filter((command) => command.type === 'resolve-selection')).toHaveLength(2)
    expect(commands.filter((command) => command.type === 'start')).toHaveLength(2)
  })

  it('reconnects and retries stale language-session recovery once after a Gateway restart', async () => {
    vi.stubGlobal('WebSocket', MockWebSocket)
    const selectionRequest: ResolveSelectionRequest = {
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      outputId: 'decompiled-csharp',
      runtimeId: null,
      buildMode: 'release',
      catalogRevision: 'catalog-test',
      workspaceRevision: 9,
    }
    const initialResponse: ResolveSelectionResponse = {
      effectiveSelection: {
        languageId: 'csharp',
        toolchainId: 'roslyn-stable',
        referenceSetId: 'net10-ref',
        outputId: 'decompiled-csharp',
        runtimeId: null,
      },
      selectionChanges: [],
      effectiveCapabilities: {
        languageServerCapabilities: ['completion'],
        buildCapabilities: ['managed-pe'],
        outputCapabilities: ['decompiled-csharp'],
        runtimeCapabilities: [],
      },
      pipelineResolutionId: 'pipeline-before-restart',
      pipelinePlan: {
        releaseId: 'content',
        languageWorkerId: 'roslyn-stable',
        compilerWorkerId: 'roslyn-stable',
        referenceSetId: 'net10-ref',
        stages: [],
        runtimeId: null,
        securityPolicyId: 'compiler-default',
        workerImageIds: [],
      },
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
    }
    const refreshedResponse = {
      ...initialResponse,
      pipelineResolutionId: 'pipeline-after-restart',
    }
    const languageRequest: OpenLanguageSessionRequest = {
      requestId: 'lsp-recovery',
      pipelineResolutionId: initialResponse.pipelineResolutionId,
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      workspace: {
        schemaVersion: 1,
        revision: 9,
        selectionRevision: 2,
        languageId: 'csharp',
        files: [{ path: 'Program.cs', version: 1, text: 'class Program {}' }],
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
    }
    const fetchMock = vi.fn(async (_input: string | URL | Request, init?: RequestInit) => {
      const url = String(_input)
      if (url.endsWith('/api/v1/catalog')) {
        return new Response(stringifyWire({ revision: 'catalog-after-restart' }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        })
      }
      if (fetchMock.mock.calls.length === 1) {
        return new Response(
          stringifyWire({
            error: 'invalid-pipeline-resolution',
            message: 'Resolve the selection again before opening a language session.',
          }),
          { status: 400, headers: { 'content-type': 'application/json' } },
        )
      }
      const body = decodeWire<OpenLanguageSessionRequest>(JSON.parse(String(init?.body)))
      expect(body.pipelineResolutionId).toBe(refreshedResponse.pipelineResolutionId)
      return new Response(
        stringifyWire({
          sessionId: 'glsp_recovered',
          languageId: body.languageId,
          toolchainId: body.toolchainId,
          compilerBuildIdentity: 'roslyn-test',
          lspVersion: '3.17',
          workspaceRevision: body.workspace.revision,
          selectionRevision: body.workspace.selectionRevision,
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          webSocketUrl: '/api/v1/language-sessions/glsp_recovered/lsp',
          capabilities: ['completion'],
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      )
    })
    vi.stubGlobal('fetch', fetchMock)

    const selection = resolveSelection(selectionRequest)
    const socket = await waitForSocket(0)
    socket.open()
    const initialCommand = await waitForCommand<{ commandId: string }>(socket, 0)
    socket.emitResponse(initialCommand.commandId, initialResponse)
    await expect(selection).resolves.toEqual(initialResponse)

    const opening = openLanguageSession(languageRequest)
    // Recovery must not reuse an OPEN command socket that belonged to the
    // pre-restart Gateway process. It closes that object and waits for a fresh
    // socket before resolving the replacement pipeline.
    const replacement = await waitForSocket(1)
    expect(socket.closeCalls).toContainEqual([1000, 'Refreshing the Gateway pipeline resolution.'])
    replacement.open()
    const refreshCommand = await waitForCommand<{
      commandId: string
      type: string
      request: ResolveSelectionRequest
    }>(replacement, 0)
    expect(refreshCommand).toMatchObject({
      type: 'resolve-selection',
      request: selectionRequest,
    })
    // Exercise the bounded retry: the first fresh socket can still be lost
    // during a rolling Gateway replacement, so recovery gets one more socket
    // rather than looping forever or returning the original 400 immediately.
    replacement.emitFailure(refreshCommand.commandId, 502, {
      error: 'gateway-restarted',
      message: 'The command socket was replaced while resolving the selection.',
    })
    const retrySocket = await waitForSocket(2)
    expect(replacement.closeCalls).toContainEqual([1000, 'Refreshing the Gateway pipeline resolution.'])
    retrySocket.open()
    const retryCommand = await waitForCommand<{
      commandId: string
      type: string
      request: ResolveSelectionRequest
    }>(retrySocket, 0)
    expect(retryCommand).toMatchObject({
      type: 'resolve-selection',
      request: selectionRequest,
    })
    retrySocket.emitFailure(retryCommand.commandId, 400, {
      error: 'stale-catalog',
      message: 'The browser is using an older catalog revision.',
    })
    const refreshedCommand = await waitForCommand<{
      commandId: string
      type: string
      request: ResolveSelectionRequest
    }>(retrySocket, 1)
    expect(refreshedCommand.request.catalogRevision).toBe('catalog-after-restart')
    retrySocket.emitResponse(refreshedCommand.commandId, refreshedResponse)

    await expect(opening).resolves.toMatchObject({
      sessionId: 'glsp_recovered',
    })
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })
})

describe('secondary language-session selection', () => {
  const selectionRequest: ResolveSelectionRequest = {
    languageId: 'il',
    toolchainId: 'mobius-ilasm-stable',
    referenceSetId: 'net10-ref',
    outputId: 'il',
    runtimeId: null,
    buildMode: 'release',
    catalogRevision: 'catalog-test',
    workspaceRevision: 12,
  }
  const selectionResponse: ResolveSelectionResponse = {
    effectiveSelection: {
      languageId: 'il',
      toolchainId: 'mobius-ilasm-stable',
      referenceSetId: 'net10-ref',
      outputId: 'il',
      runtimeId: null,
    },
    selectionChanges: [],
    effectiveCapabilities: {
      languageServerCapabilities: ['hover', 'semantic-tokens'],
      buildCapabilities: ['managed-pe'],
      outputCapabilities: ['il'],
      runtimeCapabilities: [],
    },
    pipelineResolutionId: 'pipeline-il-output',
    pipelinePlan: {
      releaseId: 'content',
      languageWorkerId: 'mobius-ilasm-stable',
      compilerWorkerId: 'mobius-ilasm-stable',
      referenceSetId: 'net10-ref',
      stages: [],
      runtimeId: null,
      securityPolicyId: 'compiler-default',
      workerImageIds: [],
    },
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
  }
  const languageRequest: OpenLanguageSessionRequest = {
    requestId: 'lsp-il-output',
    pipelineResolutionId: 'expired-pipeline-il-output',
    languageId: 'il',
    toolchainId: 'mobius-ilasm-stable',
    referenceSetId: 'net10-ref',
    workspace: {
      schemaVersion: 1,
      revision: 12,
      selectionRevision: 4,
      languageId: 'il',
      files: [{ path: 'Output.il', version: 1, text: '.assembly Output {}' }],
      activeFile: 'Output.il',
      sourceOrder: ['Output.il'],
      referenceSetId: 'net10-ref',
      buildOptions: {
        configuration: 'release',
        optimize: true,
        outputKind: 'library',
        allowUnsafe: false,
        emitPortablePdb: true,
        nullableContext: 'project-default',
        preprocessorSymbols: [],
        checkOverflow: false,
      },
    },
    lspVersion: '3.17',
  }

  it('resolves over HTTP without changing the shared operation selection', async () => {
    const fetchMock = vi.fn(
      async (_input: string | URL | Request, _init?: RequestInit) =>
        new Response(stringifyWire(selectionResponse), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
    )
    vi.stubGlobal('fetch', fetchMock)
    vi.stubGlobal('WebSocket', MockWebSocket)

    await expect(resolveSelectionHttp(selectionRequest)).resolves.toEqual(selectionResponse)

    expect(MockWebSocket.instances).toHaveLength(0)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/v1/selections/resolve')
    expect(decodeWire(JSON.parse(String(fetchMock.mock.calls[0]?.[1]?.body)))).toEqual(selectionRequest)
  })

  it('recovers an output session with its private resolution request', async () => {
    const fetchMock = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
      const url = String(input)
      if (url === '/api/v1/selections/resolve') {
        return new Response(stringifyWire(selectionResponse), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        })
      }
      if (fetchMock.mock.calls.length === 1) {
        return new Response(
          stringifyWire({
            error: 'invalid-pipeline-resolution',
            message: 'Resolve the selection again before opening a language session.',
          }),
          { status: 400, headers: { 'content-type': 'application/json' } },
        )
      }
      const request = decodeWire<OpenLanguageSessionRequest>(JSON.parse(String(init?.body)))
      return new Response(
        stringifyWire({
          sessionId: 'glsp_il_output',
          languageId: request.languageId,
          toolchainId: request.toolchainId,
          compilerBuildIdentity: 'ilsense-test',
          lspVersion: '3.17',
          workspaceRevision: request.workspace.revision,
          selectionRevision: request.workspace.selectionRevision,
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          webSocketUrl: '/api/v1/language-sessions/glsp_il_output/lsp',
          capabilities: ['hover', 'semantic-tokens'],
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      )
    })
    vi.stubGlobal('fetch', fetchMock)

    await expect(openLanguageSessionWithResolution(languageRequest, selectionRequest)).resolves.toMatchObject({ sessionId: 'glsp_il_output' })

    expect(fetchMock.mock.calls.map((call) => call[0])).toEqual(['/api/v1/language-sessions', '/api/v1/selections/resolve', '/api/v1/language-sessions'])
    const retriedRequest = decodeWire<OpenLanguageSessionRequest>(JSON.parse(String(fetchMock.mock.calls[2]?.[1]?.body)))
    expect(retriedRequest.pipelineResolutionId).toBe(selectionResponse.pipelineResolutionId)
  })
})

describe('subscribeToOperationEvents', () => {
  it('resumes after disconnect, deduplicates replayed events, and stops at a terminal event', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('WebSocket', MockWebSocket)
    const operationId = `op_${'b'.repeat(32)}`
    const events: OperationEvent[] = []
    const statuses: string[] = []
    const errors: Error[] = []
    const operationEvent = (sequence: number, payload: OperationEvent['payload']): OperationEvent => ({
      operationId,
      sequence,
      timestampUtc: new Date(0).toISOString(),
      traceId: 'trace-operation-events',
      payload,
    })

    subscribeToOperationEvents(operationId, 4, {
      onEvent: (event) => events.push(event),
      onStatus: (status) => statuses.push(status),
      onError: (error) => errors.push(error),
    })

    expect(MockWebSocket.instances).toHaveLength(1)
    const first = MockWebSocket.instances[0]
    expect(first?.url).toBe(operationCommandsWebSocketUrl())
    first?.open()
    await Promise.resolve()
    await Promise.resolve()
    await Promise.resolve()
    const firstSubscribe = decodeWire<{
      commandId: string
      type: string
      operationId: string
      fromSequence: number
    }>(JSON.parse(first?.sent[0] ?? '{}'))
    expect(firstSubscribe).toMatchObject({
      type: 'subscribe',
      operationId,
      fromSequence: 4,
    })
    first?.emitResponse(firstSubscribe.commandId, {
      operationId,
      fromSequence: 4,
    })
    const progress = operationEvent(5, {
      kind: 'progress',
      stage: 'compile',
      fraction: 0.5,
    })
    first?.emitOperation(progress)
    first?.emitOperation(progress)
    first?.emitOperation(operationEvent(4, { kind: 'progress', stage: 'replayed' }))
    first?.disconnect()

    await vi.advanceTimersByTimeAsync(250)

    expect(MockWebSocket.instances).toHaveLength(2)
    const resumed = MockWebSocket.instances[1]
    expect(resumed?.url).toBe(operationCommandsWebSocketUrl())
    resumed?.open()
    await Promise.resolve()
    await Promise.resolve()
    await Promise.resolve()
    const resumedSubscribe = decodeWire<{
      commandId: string
      fromSequence: number
    }>(JSON.parse(resumed?.sent[0] ?? '{}'))
    expect(resumedSubscribe.fromSequence).toBe(5)
    resumed?.emitResponse(resumedSubscribe.commandId, {
      operationId,
      fromSequence: 5,
    })
    resumed?.emitOperation(
      operationEvent(6, {
        kind: 'completed',
        status: 'completed',
        elapsed: '00:00:00.0100000',
      }),
    )

    expect(events.map((event) => event.sequence)).toEqual([5, 6])
    expect(resumed?.closeCalls).toEqual([])
    expect(statuses).toContain('reconnecting')
    expect(statuses.at(-1)).toBe('closed')
    expect(errors).toEqual([])

    await vi.advanceTimersByTimeAsync(10_000)
    expect(MockWebSocket.instances).toHaveLength(2)
  })
})

describe('getOperationContent', () => {
  it('uses the operation-scoped content endpoint', async () => {
    const fetchMock = vi.fn(async () => new Response('.method public static void Main()'))
    vi.stubGlobal('fetch', fetchMock)

    await expect(getOperationContent('op_123', 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')).resolves.toContain('.method')
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/operations/op_123/contents/sha256/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', undefined)
  })

  it('rejects malformed references before issuing a request', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    await expect(getOperationContent('op_123', 'sha256:not-a-digest')).rejects.toThrow('invalid content reference')
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('Explain and Gist API paths', () => {
  const workspace: GistWorkspaceState = {
    schemaVersion: 1,
    languageId: 'csharp',
    toolchainId: 'roslyn-stable',
    referenceSetId: 'net10-ref',
    outputId: 'explain',
    runtimeId: null,
    buildMode: 'release',
    releaseId: 'content',
    activeFile: 'Program.cs',
    sourceOrder: ['Program.cs'],
    files: [{ path: 'Program.cs', text: 'class Program {}' }],
  }

  it('starts Explain through the persistent operation command session', async () => {
    vi.stubGlobal('WebSocket', MockWebSocket)
    const request = {
      requestId: 'req-explain',
      idempotencyKey: 'explain:req-explain',
      pipelineResolutionId: 'pipeline-explain',
      workspace: {
        ...workspace,
        revision: 1,
        selectionRevision: 1,
        files: [{ path: 'Program.cs', text: 'class Program {}', version: 1 }],
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
      deadlineUtc: new Date().toISOString(),
    } satisfies ExplainRequest

    const start = startExplain(request)
    const socket = await waitForSocket(0)
    socket.open()
    const command = await waitForCommand<{
      commandId: string
      type: string
      operation: string
      request: ExplainRequest
    }>(socket, 0)
    expect(command).toMatchObject({
      type: 'start',
      operation: 'explain',
      request,
    })
    socket.emitResponse(
      command.commandId,
      {
        operationId: `op_${'e'.repeat(32)}`,
        requestId: request.requestId,
        createdAtUtc: new Date().toISOString(),
        isExisting: false,
      },
      202,
    )
    await expect(start).resolves.toMatchObject({
      requestId: request.requestId,
    })
  })

  it('encodes Gist overrides and sends CSRF only on explicit writes', async () => {
    const fetchMock = vi.fn(
      async (_input: string | URL | Request, _init?: RequestInit) =>
        new Response(
          stringifyWire({
            id: 'abcdef',
            htmlUrl: 'https://gist.github.com/abcdef',
            isPublic: false,
            canUpdate: true,
            description: '',
            sourceFormat: 'sharplabnext-v1',
            workspace,
            warnings: [],
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await getGist('abcdef', { target: 'asm', branch: 'main', mode: 'debug' })
    await createGist({ description: '', isPublic: false, workspace }, 'csrf-token')
    await updateGist('abcdef', { description: '', workspace }, 'csrf-token')

    const first = fetchMock.mock.calls[0]
    const second = fetchMock.mock.calls[1]
    const third = fetchMock.mock.calls[2]
    expect(first?.[0]).toBe('/api/v1/shares/gists/abcdef?Target=asm&Branch=main&Mode=debug')
    expect(new Headers(second?.[1]?.headers).get('X-SharpLabNext-CSRF')).toBe('csrf-token')
    expect(third?.[0]).toBe('/api/v1/shares/gists/abcdef')
    expect(third?.[1]?.method).toBe('PATCH')
  })
})
