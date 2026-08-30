import { describe, expect, it, vi } from 'vitest'
import type { GatewayLanguageSession, OpenLanguageSessionRequest } from '../api/types'
import {
  CodeMirrorLanguageBridge,
  type CodeMirrorLanguageSink,
  codeMirrorDocumentUri,
  createCodeMirrorLanguageSessionDependencies,
  decodeSemanticTokens,
  lspSemanticTokenModifiers,
  lspSemanticTokenTypes,
  readOnlyIlOutputLanguageClientFeatureProfile,
} from './codeMirrorLanguageClient'
import type { LanguageSessionConnectionPlan, LanguageSessionTransportError } from './languageSessionLifecycle'

describe('CodeMirror language client', () => {
  it('appends shared semantic token types and modifiers without changing established indexes', () => {
    expect(lspSemanticTokenTypes.slice(0, 26)).toEqual([
      'namespace',
      'type',
      'class',
      'enum',
      'interface',
      'struct',
      'typeParameter',
      'parameter',
      'variable',
      'property',
      'enumMember',
      'event',
      'function',
      'method',
      'macro',
      'keyword',
      'modifier',
      'comment',
      'string',
      'number',
      'regexp',
      'operator',
      'delegate',
      'field',
      'label',
      'stringEscapeCharacter',
    ])
    expect(lspSemanticTokenTypes.slice(26)).toEqual(['identifier', 'invalid'])
    expect(lspSemanticTokenModifiers).toEqual(['static', 'deprecated', 'readonly', 'abstract', 'async', 'declaration', 'definition'])
  })

  it('encodes stable workspace document URIs', () => {
    expect(codeMirrorDocumentUri('sharplabnext://workspace/', 'src/My File.cs')).toBe('sharplabnext://workspace/src/My%20File.cs')
  })

  it('decodes relative semantic token positions and modifiers', () => {
    expect(decodeSemanticTokens([0, 4, 3, 1, 1, 0, 5, 2, 0, 0, 2, 1, 4, 1, 0], ['variable', 'method'], ['static'])).toEqual([
      {
        line: 0,
        character: 4,
        length: 3,
        tokenType: 'method',
        tokenModifiers: ['static'],
      },
      {
        line: 0,
        character: 9,
        length: 2,
        tokenType: 'variable',
        tokenModifiers: [],
      },
      {
        line: 2,
        character: 1,
        length: 4,
        tokenType: 'method',
        tokenModifiers: [],
      },
    ])
    expect(decodeSemanticTokens([0, 0, 2], ['method'], [])).toEqual([])
    expect(decodeSemanticTokens([0, 0, 2, 9, 0], ['method'], [])).toEqual([])
  })

  it('publishes language structure and exposes interactive LSP features', async () => {
    const socket = new FakeWebSocket()
    const bridge = new CodeMirrorLanguageBridge()
    const sink: CodeMirrorLanguageSink = {
      publishDiagnostics: vi.fn(),
      publishSemanticTokens: vi.fn(),
      publishDocumentSymbols: vi.fn(),
      publishFoldingRanges: vi.fn(),
      clearDocument: vi.fn(),
    }
    const dependencies = createCodeMirrorLanguageSessionDependencies(bridge, sink)
    const client = dependencies.createClient(plan(), descriptor(), socket as unknown as WebSocket, () => true)

    await client.start()
    await vi.waitFor(() => expect(sink.publishDiagnostics).toHaveBeenCalledTimes(2))
    await vi.waitFor(() => expect(sink.publishSemanticTokens).toHaveBeenCalledTimes(1))
    await vi.waitFor(() => expect(sink.publishDocumentSymbols).toHaveBeenCalledTimes(1))
    await vi.waitFor(() => expect(sink.publishFoldingRanges).toHaveBeenCalledTimes(1))
    expect(sink.publishDiagnostics).toHaveBeenLastCalledWith(
      'Program.cs',
      1,
      expect.arrayContaining([
        expect.objectContaining({
          message: 'Expected expression.',
          actions: [expect.objectContaining({ title: "Insert missing ';'" })],
        }),
      ]),
    )
    expect(sink.publishSemanticTokens).toHaveBeenCalledWith('Program.cs', 1, [expect.objectContaining({ tokenType: 'method', line: 0, character: 0 })])

    const completions = await bridge.completion('Program.cs', {
      line: 0,
      character: 3,
      triggerKind: 1,
    })
    expect(completions).toEqual({
      isIncomplete: true,
      items: [expect.objectContaining({ label: 'WriteLine' })],
    })
    expect(await bridge.hover('Program.cs', { line: 0, character: 1 })).toEqual(
      expect.objectContaining({
        contents: expect.objectContaining({ value: 'method docs' }),
      }),
    )
    expect(await bridge.signatureHelp('Program.cs', { line: 0, character: 4 }, '(')).toEqual({
      signatures: [
        {
          label: 'void Bad(int value)',
          parameters: [{ label: 'int value' }],
        },
      ],
      activeSignature: 0,
      activeParameter: 0,
    })
    await bridge.signatureHelp('Program.cs', { line: 0, character: 4 }, '<', false)
    await bridge.signatureHelp('Program.cs', { line: 0, character: 4 }, ')', true)
    expect(
      socket.sent
        .filter((message) => requestMethod(message) === 'textDocument/signatureHelp')
        .slice(-2)
        .map((message) => (message as { params?: { context?: unknown } }).params?.context),
    ).toEqual([
      { triggerKind: 2, triggerCharacter: '<', isRetrigger: false },
      { triggerKind: 2, triggerCharacter: ')', isRetrigger: true },
    ])
    expect(sink.publishDocumentSymbols).toHaveBeenCalledWith('Program.cs', 1, [expect.objectContaining({ name: 'Bad', kind: 6 })])
    expect(sink.publishFoldingRanges).toHaveBeenCalledWith('Program.cs', 1, [expect.objectContaining({ startLine: 0, endLine: 1 })])
    expect(socket.sent.map(requestMethod)).toEqual(expect.arrayContaining(['textDocument/didOpen', 'textDocument/documentSymbol', 'textDocument/foldingRange']))

    await client.dispose()
    expect(sink.clearDocument).toHaveBeenCalledWith('Program.cs')
  })

  it('marks WebSocket open, close, and initialize timeout failures as transport errors', async () => {
    vi.useFakeTimers()
    try {
      const openSocket = new FakeWebSocket()
      openSocket.readyState = WebSocket.CONNECTING
      const openClient = createCodeMirrorLanguageSessionDependencies(new CodeMirrorLanguageBridge(), emptySink()).createClient(plan(), descriptor(), openSocket as unknown as WebSocket, () => true)
      const openFailure = expect(openClient.start()).rejects.toMatchObject({
        name: 'LanguageSessionTransportError',
        kind: 'websocket-open-failed',
      } satisfies Partial<LanguageSessionTransportError>)
      openSocket.failOpen()
      await openFailure
      await openClient.dispose()

      const closeSocket = new FakeWebSocket(true, false, 'none')
      const closeClient = createCodeMirrorLanguageSessionDependencies(new CodeMirrorLanguageBridge(), emptySink()).createClient(plan(), descriptor(), closeSocket as unknown as WebSocket, () => true)
      const closeFailure = expect(closeClient.start()).rejects.toMatchObject({
        name: 'LanguageSessionTransportError',
        kind: 'websocket-closed',
      } satisfies Partial<LanguageSessionTransportError>)
      await Promise.resolve()
      closeSocket.close()
      await closeFailure
      await closeClient.dispose()

      const timeoutSocket = new FakeWebSocket(true, false, 'none')
      const timeoutClient = createCodeMirrorLanguageSessionDependencies(new CodeMirrorLanguageBridge(), emptySink()).createClient(plan(), descriptor(), timeoutSocket as unknown as WebSocket, () => true)
      const timeoutFailure = expect(timeoutClient.start()).rejects.toMatchObject({
        name: 'LanguageSessionTransportError',
        kind: 'initialize-timeout',
      } satisfies Partial<LanguageSessionTransportError>)
      await vi.advanceTimersByTimeAsync(9_999)
      await vi.advanceTimersByTimeAsync(1)
      await timeoutFailure
      await timeoutClient.dispose()
    } finally {
      vi.useRealTimers()
    }
  })

  it('resolves completion items with the original server payload and current document version', async () => {
    const socket = new FakeWebSocket(true, true)
    const bridge = new CodeMirrorLanguageBridge()
    const sink: CodeMirrorLanguageSink = {
      publishDiagnostics: vi.fn(),
      publishSemanticTokens: vi.fn(),
      publishDocumentSymbols: vi.fn(),
      publishFoldingRanges: vi.fn(),
      clearDocument: vi.fn(),
    }
    const client = createCodeMirrorLanguageSessionDependencies(bridge, sink).createClient(plan(), descriptor(), socket as unknown as WebSocket, () => true)

    await client.start()
    expect(socket.sent.find((message) => requestMethod(message) === 'initialize')).toMatchObject({
      params: {
        capabilities: {
          textDocument: {
            completion: {
              completionItem: {
                resolveSupport: {
                  properties: ['detail', 'documentation', 'insertTextFormat', 'textEdit', 'additionalTextEdits'],
                },
              },
            },
          },
        },
      },
    })

    const completions = await bridge.completion('Program.cs', {
      line: 0,
      character: 3,
      triggerKind: 1,
    })
    const item = completions?.items[0]
    expect(item).toMatchObject({
      label: 'WriteLine',
      documentVersion: 1,
      raw: {
        label: 'WriteLine',
        kind: 2,
        data: { resolveId: 'completion-1' },
      },
    })
    if (!item) throw new Error('Completion item was not returned.')

    const resolved = await bridge.resolveCompletion('Program.cs', item)
    expect(socket.sent.filter((message) => requestMethod(message) === 'completionItem/resolve')).toEqual([expect.objectContaining({ params: item.raw })])
    expect(resolved).toMatchObject({
      label: 'WriteLine',
      detail: 'void Console.WriteLine(string value)',
      documentVersion: 1,
      textEdit: {
        range: {
          start: { line: 0, character: 0 },
          end: { line: 0, character: 3 },
        },
        newText: 'Console.WriteLine',
      },
      additionalTextEdits: [
        {
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 0 },
          },
          newText: 'using System;\n',
        },
      ],
    })

    await client.dispose()
  })

  it('waits for the replacement language client instead of returning a transient empty list', async () => {
    const bridge = new CodeMirrorLanguageBridge()
    const sink: CodeMirrorLanguageSink = {
      publishDiagnostics: vi.fn(),
      publishSemanticTokens: vi.fn(),
      publishDocumentSymbols: vi.fn(),
      publishFoldingRanges: vi.fn(),
      clearDocument: vi.fn(),
    }
    const dependencies = createCodeMirrorLanguageSessionDependencies(bridge, sink)
    let firstIsCurrent = true
    const firstClient = dependencies.createClient(plan(), descriptor(), new FakeWebSocket() as unknown as WebSocket, () => firstIsCurrent)
    await firstClient.start()
    bridge.setSessionStatus('ready')

    firstIsCurrent = false
    bridge.setSessionStatus('connecting')
    let settled = false
    const completion = bridge.completion('Program.cs', { line: 0, character: 3, triggerKind: 1 }).then((result) => {
      settled = true
      return result
    })
    await Promise.resolve()
    expect(settled).toBe(false)

    await firstClient.dispose()
    const replacementClient = dependencies.createClient({ ...plan(), key: 'replacement' }, descriptor(), new FakeWebSocket() as unknown as WebSocket, () => true)
    await replacementClient.start()
    bridge.setSessionStatus('ready')

    expect(await completion).toEqual({
      isIncomplete: true,
      items: [expect.objectContaining({ label: 'WriteLine', documentVersion: 1 })],
    })
    await replacementClient.dispose()
  })

  it('returns the original completion item without a request when resolve is unsupported', async () => {
    const socket = new FakeWebSocket()
    const bridge = new CodeMirrorLanguageBridge()
    const sink: CodeMirrorLanguageSink = {
      publishDiagnostics: vi.fn(),
      publishSemanticTokens: vi.fn(),
      publishDocumentSymbols: vi.fn(),
      publishFoldingRanges: vi.fn(),
      clearDocument: vi.fn(),
    }
    const client = createCodeMirrorLanguageSessionDependencies(bridge, sink).createClient(plan(), descriptor(), socket as unknown as WebSocket, () => true)

    await client.start()
    const item = (
      await bridge.completion('Program.cs', {
        line: 0,
        character: 3,
        triggerKind: 1,
      })
    )?.items[0]
    if (!item) throw new Error('Completion item was not returned.')

    expect(await bridge.resolveCompletion('Program.cs', item)).toBe(item)
    expect(socket.sent.some((message) => requestMethod(message) === 'completionItem/resolve')).toBe(false)

    await client.dispose()
  })

  it('ignores semantic token responses superseded by a newer refresh generation', async () => {
    vi.useFakeTimers()
    try {
      const socket = new FakeWebSocket(false)
      const bridge = new CodeMirrorLanguageBridge()
      const sink: CodeMirrorLanguageSink = {
        publishDiagnostics: vi.fn(),
        publishSemanticTokens: vi.fn(),
        publishDocumentSymbols: vi.fn(),
        publishFoldingRanges: vi.fn(),
        clearDocument: vi.fn(),
      }
      const client = createCodeMirrorLanguageSessionDependencies(bridge, sink).createClient(plan(), descriptor(), socket as unknown as WebSocket, () => true)

      await client.start()
      await vi.advanceTimersByTimeAsync(0)
      expect(socket.semanticRequestIds).toHaveLength(1)

      bridge.changeDocument('Program.cs', 'NewBad()', 2)
      await vi.advanceTimersByTimeAsync(120)
      expect(socket.semanticRequestIds).toHaveLength(2)

      socket.respondSemanticTokens(1, [0, 0, 3, 1, 0])
      await vi.advanceTimersByTimeAsync(0)
      expect(sink.publishSemanticTokens).toHaveBeenCalledTimes(1)
      expect(sink.publishSemanticTokens).toHaveBeenLastCalledWith('Program.cs', 2, [expect.objectContaining({ tokenType: 'method' })])

      socket.respondSemanticTokens(0, [0, 0, 3, 0, 0])
      await vi.advanceTimersByTimeAsync(0)
      expect(sink.publishSemanticTokens).toHaveBeenCalledTimes(1)

      await client.dispose()
    } finally {
      vi.useRealTimers()
    }
  })

  it('keeps read-only IL output semantic tokens and hover without document synchronization or structure', async () => {
    const socket = new FakeWebSocket()
    const bridge = new CodeMirrorLanguageBridge()
    const sink: CodeMirrorLanguageSink = {
      publishDiagnostics: vi.fn(),
      publishSemanticTokens: vi.fn(),
      publishDocumentSymbols: vi.fn(),
      publishFoldingRanges: vi.fn(),
      clearDocument: vi.fn(),
    }
    const client = createCodeMirrorLanguageSessionDependencies(bridge, sink, readOnlyIlOutputLanguageClientFeatureProfile).createClient(outputPlan(), descriptor(), socket as unknown as WebSocket, () => true)

    await client.start()
    await vi.waitFor(() => expect(sink.publishSemanticTokens).toHaveBeenCalledTimes(1))

    const initialize = socket.sent.find((message) => requestMethod(message) === 'initialize') as {
      params?: { capabilities?: { textDocument?: Record<string, unknown> } }
    }
    expect(Object.keys(initialize.params?.capabilities?.textDocument ?? {})).toEqual(['hover', 'semanticTokens'])
    expect(socket.sent.map(requestMethod)).not.toEqual(expect.arrayContaining(['textDocument/didOpen', 'textDocument/didChange', 'textDocument/documentSymbol', 'textDocument/foldingRange']))
    expect(sink.publishSemanticTokens).toHaveBeenCalledWith('Output.il', 1, [expect.objectContaining({ tokenType: 'method', line: 0, character: 0 })])
    expect(await bridge.hover('Output.il', { line: 0, character: 1 })).toEqual(
      expect.objectContaining({
        contents: expect.objectContaining({ value: 'method docs' }),
      }),
    )

    bridge.changeDocument('Output.il', '.class Changed {}', 2)
    expect(socket.sent.map(requestMethod)).not.toContain('textDocument/didChange')
    expect(
      await bridge.completion('Output.il', {
        line: 0,
        character: 1,
        triggerKind: 1,
      }),
    ).toBeNull()
    expect(await bridge.signatureHelp('Output.il', { line: 0, character: 1 }, '(')).toBeNull()
    expect(sink.publishDiagnostics).not.toHaveBeenCalled()
    expect(sink.publishDocumentSymbols).not.toHaveBeenCalled()
    expect(sink.publishFoldingRanges).not.toHaveBeenCalled()

    await client.dispose()
  })
})

class FakeWebSocket extends EventTarget {
  readyState: number = WebSocket.OPEN
  readonly sent: unknown[] = []
  readonly semanticRequestIds: number[] = []
  private readonly autoRespondSemanticTokens: boolean
  private readonly completionResolveProvider: boolean
  private readonly initializeResponse: 'success' | 'none'

  constructor(autoRespondSemanticTokens = true, completionResolveProvider = false, initializeResponse: 'success' | 'none' = 'success') {
    super()
    this.autoRespondSemanticTokens = autoRespondSemanticTokens
    this.completionResolveProvider = completionResolveProvider
    this.initializeResponse = initializeResponse
  }

  send(data: string): void {
    const message = JSON.parse(data) as Record<string, unknown>
    this.sent.push(message)
    const method = message.method
    if (method === 'initialize') {
      if (this.initializeResponse === 'none') return
      this.respond(message.id, {
        capabilities: {
          completionProvider: this.completionResolveProvider ? { resolveProvider: true } : {},
          hoverProvider: true,
          signatureHelpProvider: { triggerCharacters: ['(', ','] },
          codeActionProvider: { codeActionKinds: ['quickfix'] },
          documentSymbolProvider: true,
          foldingRangeProvider: true,
          semanticTokensProvider: {
            legend: {
              tokenTypes: ['variable', 'method'],
              tokenModifiers: ['static'],
            },
            full: true,
          },
        },
      })
    } else if (method === 'textDocument/didOpen') {
      queueMicrotask(() =>
        this.notify('textDocument/publishDiagnostics', {
          uri: 'sharplabnext://workspace/Program.cs',
          version: 1,
          diagnostics: [
            {
              range: {
                start: { line: 0, character: 0 },
                end: { line: 0, character: 3 },
              },
              severity: 1,
              code: 'CS1002',
              source: 'roslyn',
              message: 'Expected expression.',
              data: { selectionRevision: 1, documentVersion: 1 },
            },
          ],
        }),
      )
    } else if (method === 'textDocument/semanticTokens/full') {
      if (typeof message.id === 'number') this.semanticRequestIds.push(message.id)
      if (this.autoRespondSemanticTokens) this.respond(message.id, { data: [0, 0, 3, 1, 1] })
    } else if (method === 'textDocument/documentSymbol') {
      this.respond(message.id, [
        {
          name: 'Bad',
          kind: 6,
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 5 },
          },
          selectionRange: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 3 },
          },
          children: [],
        },
      ])
    } else if (method === 'textDocument/foldingRange') {
      this.respond(message.id, [{ startLine: 0, endLine: 1, kind: 'region' }])
    } else if (method === 'textDocument/codeAction') {
      this.respond(message.id, [
        {
          title: "Insert missing ';'",
          kind: 'quickfix',
          isPreferred: true,
          diagnostics: [
            {
              range: {
                start: { line: 0, character: 0 },
                end: { line: 0, character: 3 },
              },
              code: 'CS1002',
            },
          ],
          edit: {
            changes: {
              'sharplabnext://workspace/Program.cs': [
                {
                  range: {
                    start: { line: 0, character: 5 },
                    end: { line: 0, character: 5 },
                  },
                  newText: ';',
                },
              ],
            },
          },
        },
      ])
    } else if (method === 'textDocument/completion') {
      this.respond(message.id, {
        isIncomplete: true,
        items: [
          {
            label: 'WriteLine',
            kind: 2,
            data: { resolveId: 'completion-1' },
          },
        ],
      })
    } else if (method === 'completionItem/resolve') {
      this.respond(message.id, {
        label: 'WriteLine',
        kind: 2,
        data: { resolveId: 'completion-1' },
        detail: 'void Console.WriteLine(string value)',
        insertText: 'Console',
        textEdit: {
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 3 },
          },
          newText: 'Console.WriteLine',
        },
        additionalTextEdits: [
          {
            range: {
              start: { line: 0, character: 0 },
              end: { line: 0, character: 0 },
            },
            newText: 'using System;\n',
          },
        ],
      })
    } else if (method === 'textDocument/hover') {
      this.respond(message.id, {
        contents: { kind: 'markdown', value: 'method docs' },
      })
    } else if (method === 'textDocument/signatureHelp') {
      this.respond(message.id, {
        signatures: [
          {
            label: 'void Bad(int value)',
            parameters: [{ label: 'int value' }],
          },
        ],
        activeSignature: 0,
        activeParameter: 0,
      })
    } else if (method === 'shutdown') {
      this.respond(message.id, null)
    }
  }

  close(): void {
    this.readyState = WebSocket.CLOSED
    this.dispatchEvent(new CloseEvent('close'))
  }

  failOpen(): void {
    this.readyState = WebSocket.CLOSED
    this.dispatchEvent(new Event('error'))
  }

  respondSemanticTokens(index: number, data: number[]): void {
    const id = this.semanticRequestIds[index]
    if (id === undefined) throw new Error(`Semantic request ${index} does not exist.`)
    this.respond(id, { data })
  }

  private respond(id: unknown, result: unknown): void {
    queueMicrotask(() => this.message({ jsonrpc: '2.0', id, result }))
  }

  private notify(method: string, parameters: unknown): void {
    this.message({ jsonrpc: '2.0', method, params: parameters })
  }

  private message(message: unknown): void {
    this.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(message) }))
  }
}

function requestMethod(message: unknown): unknown {
  return typeof message === 'object' && message !== null && 'method' in message ? message.method : undefined
}

function emptySink(): CodeMirrorLanguageSink {
  return {
    publishDiagnostics: vi.fn(),
    publishSemanticTokens: vi.fn(),
    publishDocumentSymbols: vi.fn(),
    publishFoldingRanges: vi.fn(),
    clearDocument: vi.fn(),
  }
}

function plan(): LanguageSessionConnectionPlan {
  return {
    key: 'key',
    languageId: 'csharp',
    modelLanguageId: 'csharp',
    workspaceUri: 'sharplabnext://workspace/',
    selectionRevision: 1,
    createRequest: request,
  }
}

function outputPlan(): LanguageSessionConnectionPlan {
  return {
    ...plan(),
    key: 'il-output',
    languageId: 'il',
    modelLanguageId: 'il',
    workspaceUri: 'sharplabnext:///',
    createRequest: () => {
      const value = request()
      return {
        ...value,
        languageId: 'il',
        toolchainId: 'mobius-ilasm-stable',
        workspace: {
          ...value.workspace,
          languageId: 'il',
          files: [{ path: 'Output.il', text: '.class Program {}', version: 1 }],
          activeFile: 'Output.il',
          sourceOrder: ['Output.il'],
        },
      }
    },
  }
}

function request(): OpenLanguageSessionRequest {
  return {
    requestId: 'lsp_test',
    pipelineResolutionId: 'pipeline',
    languageId: 'csharp',
    toolchainId: 'roslyn-stable',
    referenceSetId: 'net10-ref',
    workspace: {
      schemaVersion: 1,
      revision: 1,
      selectionRevision: 1,
      languageId: 'csharp',
      files: [{ path: 'Program.cs', text: 'Bad()', version: 1 }],
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
}

function descriptor(): GatewayLanguageSession {
  return {
    sessionId: 'session',
    languageId: 'csharp',
    toolchainId: 'roslyn-stable',
    compilerBuildIdentity: 'compiler',
    lspVersion: '3.17',
    workspaceRevision: 1,
    selectionRevision: 1,
    expiresAtUtc: '2099-01-01T00:00:00Z',
    webSocketUrl: '/lsp',
    capabilities: ['diagnostics', 'completion', 'hover', 'semantic-tokens'],
  }
}
