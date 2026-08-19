import {
  closeLanguageSession,
  languageSessionWebSocketUrl,
  openLanguageSession,
} from '../api/client'
import type { GatewayLanguageSession } from '../api/types'
import { createLanguageDocumentUri } from './languageDocumentUri'
import {
  isCurrentLspDiagnostic,
  type LanguageClientHandle,
  type LanguageSessionConnectionPlan,
  type LanguageSessionLifecycleDependencies,
  LanguageSessionProtocolError,
  type LanguageSessionStatus,
  LanguageSessionTransportError,
} from './languageSessionLifecycle'

export interface LspPosition {
  line: number
  character: number
}

export interface LspRange {
  start: LspPosition
  end: LspPosition
}

export const lspSemanticTokenTypes = [
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
  'identifier',
  'invalid',
] as const

export const lspSemanticTokenModifiers = [
  'static',
  'deprecated',
  'readonly',
  'abstract',
  'async',
  'declaration',
  'definition',
] as const

export interface CodeMirrorLanguageClientFeatureProfile {
  synchronizeDocuments: boolean
  diagnostics: boolean
  completion: boolean
  hover: boolean
  signatureHelp: boolean
  codeActions: boolean
  documentSymbols: boolean
  foldingRanges: boolean
  semanticTokens: boolean
}

export const defaultCodeMirrorLanguageClientFeatureProfile: Readonly<CodeMirrorLanguageClientFeatureProfile> =
  Object.freeze({
    synchronizeDocuments: true,
    diagnostics: true,
    completion: true,
    hover: true,
    signatureHelp: true,
    codeActions: true,
    documentSymbols: true,
    foldingRanges: true,
    semanticTokens: true,
  })

export const readOnlyIlOutputLanguageClientFeatureProfile: Readonly<CodeMirrorLanguageClientFeatureProfile> =
  Object.freeze({
    synchronizeDocuments: false,
    diagnostics: false,
    completion: false,
    hover: true,
    signatureHelp: false,
    codeActions: false,
    documentSymbols: false,
    foldingRanges: false,
    semanticTokens: true,
  })

export interface CodeMirrorLspDiagnostic {
  range: LspRange
  message: string
  severity?: number
  source?: string
  code?: string | number
  data?: unknown
  actions?: readonly CodeMirrorLspCodeAction[]
  raw?: unknown
}

export interface CodeMirrorSemanticToken {
  line: number
  character: number
  length: number
  tokenType: string
  tokenModifiers: readonly string[]
}

export interface CodeMirrorLspCompletionItem {
  label: string
  detail?: string
  documentation?: unknown
  insertText?: string
  insertTextFormat?: number
  kind?: number
  sortText?: string
  filterText?: string
  textEdit?: CodeMirrorLspTextEdit
  additionalTextEdits?: readonly CodeMirrorLspTextEdit[]
  raw: Record<string, unknown>
  documentVersion: number
}

export interface CodeMirrorLspCompletionList {
  isIncomplete: boolean
  items: readonly CodeMirrorLspCompletionItem[]
}

export interface CodeMirrorLspHover {
  contents: unknown
  range?: LspRange
}

export interface CodeMirrorLspParameterInformation {
  label: string | readonly [number, number]
  documentation?: unknown
}

export interface CodeMirrorLspSignatureInformation {
  label: string
  documentation?: unknown
  parameters: readonly CodeMirrorLspParameterInformation[]
  activeParameter?: number
}

export interface CodeMirrorLspSignatureHelp {
  signatures: readonly CodeMirrorLspSignatureInformation[]
  activeSignature: number
  activeParameter: number
}

export interface CodeMirrorLspTextEdit {
  range: LspRange
  newText: string
}

export interface CodeMirrorWorkspaceDocumentEdit {
  documentPath: string
  documentVersion: number
  edits: readonly CodeMirrorLspTextEdit[]
}

export interface CodeMirrorLspCodeAction {
  title: string
  kind?: string
  isPreferred?: boolean
  diagnostics: readonly Pick<CodeMirrorLspDiagnostic, 'range' | 'code'>[]
  documentEdits: readonly CodeMirrorWorkspaceDocumentEdit[]
}

export interface CodeMirrorDocumentSymbol {
  name: string
  detail?: string
  kind: number
  range: LspRange
  selectionRange: LspRange
  children: readonly CodeMirrorDocumentSymbol[]
}

export interface CodeMirrorLspFoldingRange {
  startLine: number
  startCharacter?: number
  endLine: number
  endCharacter?: number
  kind?: string
}

export interface CodeMirrorLanguageSink {
  publishDiagnostics: (
    documentPath: string,
    documentVersion: number | undefined,
    diagnostics: readonly CodeMirrorLspDiagnostic[],
  ) => void
  publishSemanticTokens: (
    documentPath: string,
    documentVersion: number,
    tokens: readonly CodeMirrorSemanticToken[],
  ) => void
  publishDocumentSymbols: (
    documentPath: string,
    documentVersion: number,
    symbols: readonly CodeMirrorDocumentSymbol[] | null,
  ) => void
  publishFoldingRanges: (
    documentPath: string,
    documentVersion: number,
    ranges: readonly CodeMirrorLspFoldingRange[] | null,
  ) => void
  clearDocument: (documentPath: string) => void
}

interface CompletionRequest {
  line: number
  character: number
  triggerKind: 1 | 2 | 3
  triggerCharacter?: string
}

interface ActiveCodeMirrorClient {
  isReady(): boolean
  changeDocument(path: string, text: string, version: number): void
  completion(path: string, request: CompletionRequest): Promise<CodeMirrorLspCompletionList | null>
  resolveCompletion(
    path: string,
    item: CodeMirrorLspCompletionItem,
  ): Promise<CodeMirrorLspCompletionItem | null>
  hover(path: string, position: LspPosition): Promise<CodeMirrorLspHover | null>
  signatureHelp(
    path: string,
    position: LspPosition,
    triggerCharacter?: string,
    isRetrigger?: boolean,
  ): Promise<CodeMirrorLspSignatureHelp | null>
}

interface ReadyClientWaiter {
  resolve: (client: ActiveCodeMirrorClient | null) => void
  timeout: number
}

const languageClientReadyTimeoutMs = 10_000
const completionRetryDelayMs = 25

export class CodeMirrorLanguageBridge {
  private client: ActiveCodeMirrorClient | null = null
  private sessionStatus: LanguageSessionStatus = 'disabled'
  private readonly readyClientWaiters = new Set<ReadyClientWaiter>()

  attach(client: ActiveCodeMirrorClient): () => void {
    this.client = client
    this.releaseReadyClientWaiters()
    return () => {
      if (this.client === client) this.client = null
    }
  }

  clientReady(client: ActiveCodeMirrorClient): void {
    if (this.client === client) this.releaseReadyClientWaiters()
  }

  setSessionStatus(status: LanguageSessionStatus): void {
    this.sessionStatus = status
    if (status === 'disabled' || status === 'error') {
      this.resolveReadyClientWaiters(null)
      return
    }
    this.releaseReadyClientWaiters()
  }

  changeDocument(path: string, text: string, version: number): void {
    this.client?.changeDocument(path, text, version)
  }

  async completion(
    path: string,
    request: CompletionRequest,
  ): Promise<CodeMirrorLspCompletionList | null> {
    const deadline = Date.now() + languageClientReadyTimeoutMs
    let transientRetries = 0
    while (true) {
      const client = await this.readyClient(deadline)
      if (!client) return null

      let completions: CodeMirrorLspCompletionList | null
      try {
        completions = await client.completion(path, request)
      } catch {
        // A socket can drop between the ready check and the request. Give the
        // same live client one short retry; if it was replaced, the loop below
        // waits for the replacement instead of exposing a false empty list.
        if (this.client !== client || !client.isReady()) continue
        if (transientRetries++ > 0) return null
        const remaining = deadline - Date.now()
        if (remaining <= 0) return null
        await new Promise<void>((resolve) => {
          window.setTimeout(resolve, Math.min(completionRetryDelayMs, remaining))
        })
        continue
      }
      transientRetries = 0
      if (completions || (this.client === client && client.isReady())) return completions
      // The request belonged to a session replaced while it was in flight.
      // Wait for the replacement instead of caching a transient empty result.
    }
  }

  resolveCompletion(
    path: string,
    item: CodeMirrorLspCompletionItem,
  ): Promise<CodeMirrorLspCompletionItem | null> {
    return this.client?.resolveCompletion(path, item) ?? Promise.resolve(null)
  }

  hover(path: string, position: LspPosition): Promise<CodeMirrorLspHover | null> {
    return this.client?.hover(path, position) ?? Promise.resolve(null)
  }

  signatureHelp(
    path: string,
    position: LspPosition,
    triggerCharacter?: string,
    isRetrigger = triggerCharacter === ',',
  ): Promise<CodeMirrorLspSignatureHelp | null> {
    return (
      this.client?.signatureHelp(path, position, triggerCharacter, isRetrigger) ??
      Promise.resolve(null)
    )
  }

  private readyClient(deadline: number): Promise<ActiveCodeMirrorClient | null> {
    const client = this.client
    if (client?.isReady()) return Promise.resolve(client)
    if (this.sessionStatus === 'disabled' || this.sessionStatus === 'error') {
      return Promise.resolve(null)
    }

    const remaining = deadline - Date.now()
    if (remaining <= 0) return Promise.resolve(null)
    return new Promise((resolve) => {
      const waiter: ReadyClientWaiter = {
        resolve,
        timeout: window.setTimeout(() => {
          this.readyClientWaiters.delete(waiter)
          resolve(null)
        }, remaining),
      }
      this.readyClientWaiters.add(waiter)
      // Initialization can finish between the check above and waiter registration.
      this.releaseReadyClientWaiters()
    })
  }

  private releaseReadyClientWaiters(): void {
    const client = this.client
    if (!client?.isReady()) return
    this.resolveReadyClientWaiters(client)
  }

  private resolveReadyClientWaiters(client: ActiveCodeMirrorClient | null): void {
    for (const waiter of this.readyClientWaiters) {
      window.clearTimeout(waiter.timeout)
      waiter.resolve(client)
    }
    this.readyClientWaiters.clear()
  }
}

export function createCodeMirrorLanguageSessionDependencies(
  bridge: CodeMirrorLanguageBridge,
  sink: CodeMirrorLanguageSink,
  featureProfile: Readonly<CodeMirrorLanguageClientFeatureProfile> = defaultCodeMirrorLanguageClientFeatureProfile,
): LanguageSessionLifecycleDependencies {
  return {
    open: (request, signal) => openLanguageSession(request, signal),
    close: closeLanguageSession,
    createSocket: (path) => new WebSocket(languageSessionWebSocketUrl(path)),
    createClient: (plan, descriptor, socket, isCurrent) =>
      new CodeMirrorLspClient(plan, descriptor, socket, isCurrent, bridge, sink, featureProfile),
    schedule: (callback, delay) => window.setTimeout(callback, delay),
    cancelSchedule: (handle) => window.clearTimeout(handle),
  }
}

export function codeMirrorDocumentUri(workspaceUri: string, path: string): string {
  return createLanguageDocumentUri(workspaceUri, path)
}

export function decodeSemanticTokens(
  data: readonly number[],
  tokenTypes: readonly string[],
  tokenModifiers: readonly string[],
): CodeMirrorSemanticToken[] {
  if (data.length % 5 !== 0) return []
  const result: CodeMirrorSemanticToken[] = []
  let line = 0
  let character = 0
  for (let index = 0; index < data.length; index += 5) {
    const deltaLine = data[index]
    const deltaCharacter = data[index + 1]
    const length = data[index + 2]
    const typeIndex = data[index + 3]
    const modifierBits = data[index + 4]
    if (
      !isNonNegativeInteger(deltaLine) ||
      !isNonNegativeInteger(deltaCharacter) ||
      !isPositiveInteger(length) ||
      !isNonNegativeInteger(typeIndex) ||
      !isNonNegativeInteger(modifierBits)
    ) {
      return []
    }
    const tokenType = tokenTypes[typeIndex]
    if (!tokenType) return []
    line += deltaLine
    character = deltaLine === 0 ? character + deltaCharacter : deltaCharacter
    const modifiers = tokenModifiers.filter(
      (_modifier, modifierIndex) => (modifierBits & (2 ** modifierIndex)) !== 0,
    )
    result.push({ line, character, length, tokenType, tokenModifiers: modifiers })
  }
  return result
}

interface LspDocument {
  path: string
  uri: string
  languageId: string
  text: string
  version: number
  diagnosticGeneration: number
  semanticGeneration: number
}

interface PendingRequest {
  resolve: (value: unknown) => void
  reject: (reason: Error) => void
  timeout: number
}

interface SemanticLegend {
  tokenTypes: string[]
  tokenModifiers: string[]
}

class CodeMirrorLspClient implements LanguageClientHandle, ActiveCodeMirrorClient {
  private readonly plan: LanguageSessionConnectionPlan
  private readonly socket: WebSocket
  private readonly isCurrent: () => boolean
  private readonly bridge: CodeMirrorLanguageBridge
  private readonly sink: CodeMirrorLanguageSink
  private readonly featureProfile: Readonly<CodeMirrorLanguageClientFeatureProfile>
  private readonly documents = new Map<string, LspDocument>()
  private readonly documentsByUri = new Map<string, LspDocument>()
  private readonly pending = new Map<number, PendingRequest>()
  private readonly semanticTimers = new Map<string, number>()
  private readonly structureTimers = new Map<string, number>()
  private readonly detachBridge: () => void
  private messageQueue: Promise<void> = Promise.resolve()
  private nextRequestId = 1
  private initialized = false
  private closing = false
  private disposed = false
  private completionSupported = false
  private completionResolveSupported = false
  private hoverSupported = false
  private signatureHelpSupported = false
  private codeActionSupported = false
  private documentSymbolSupported = false
  private foldingRangeSupported = false
  private semanticLegend: SemanticLegend | null = null

  constructor(
    plan: LanguageSessionConnectionPlan,
    _descriptor: GatewayLanguageSession,
    socket: WebSocket,
    isCurrent: () => boolean,
    bridge: CodeMirrorLanguageBridge,
    sink: CodeMirrorLanguageSink,
    featureProfile: Readonly<CodeMirrorLanguageClientFeatureProfile>,
  ) {
    this.plan = plan
    this.socket = socket
    this.isCurrent = isCurrent
    this.bridge = bridge
    this.sink = sink
    this.featureProfile = featureProfile
    const request = plan.createRequest()
    for (const file of request.workspace.files) {
      const document: LspDocument = {
        path: file.path,
        uri: codeMirrorDocumentUri(plan.workspaceUri, file.path),
        languageId: plan.languageId,
        text: file.text,
        version: file.version,
        diagnosticGeneration: 0,
        semanticGeneration: 0,
      }
      this.documents.set(file.path, document)
      this.documentsByUri.set(document.uri, document)
    }
    this.detachBridge = bridge.attach(this)
    socket.addEventListener('message', this.handleMessage)
    socket.addEventListener('close', this.handleClose)
  }

  async start(): Promise<void> {
    await waitForSocketOpen(this.socket)
    if (this.disposed || !this.isCurrent()) return

    const initializeResult = await this.request('initialize', {
      processId: null,
      clientInfo: { name: 'SharpLabNext', version: '1' },
      rootUri: this.plan.workspaceUri,
      workspaceFolders: [{ uri: this.plan.workspaceUri, name: 'SharpLabNext' }],
      capabilities: clientCapabilities(this.featureProfile),
    })
    this.readServerCapabilities(initializeResult)
    this.notify('initialized', {})
    this.initialized = true

    for (const document of this.documents.values()) {
      if (this.featureProfile.synchronizeDocuments) {
        this.notify('textDocument/didOpen', {
          textDocument: {
            uri: document.uri,
            languageId: document.languageId,
            version: document.version,
            text: document.text,
          },
        })
      }
      this.scheduleSemanticTokens(document.path, 0)
      if (this.featureProfile.documentSymbols || this.featureProfile.foldingRanges) {
        if (this.documentSymbolSupported || this.foldingRangeSupported) {
          this.scheduleStructure(document.path, 0)
        } else {
          if (this.featureProfile.documentSymbols) {
            this.sink.publishDocumentSymbols(document.path, document.version, null)
          }
          if (this.featureProfile.foldingRanges) {
            this.sink.publishFoldingRanges(document.path, document.version, null)
          }
        }
      }
    }
    this.bridge.clientReady(this)
  }

  isReady(): boolean {
    return this.initialized && !this.closing && !this.disposed && this.isCurrent()
  }

  changeDocument(path: string, text: string, version: number): void {
    if (!this.featureProfile.synchronizeDocuments) return
    const document = this.documents.get(path)
    if (!document || version <= document.version) return
    document.text = text
    document.version = version
    if (!this.initialized || this.disposed || !this.isCurrent()) return
    this.notify('textDocument/didChange', {
      textDocument: { uri: document.uri, version },
      contentChanges: [{ text }],
    })
    this.scheduleSemanticTokens(path, 120)
    this.scheduleStructure(path, 180)
  }

  async completion(
    path: string,
    request: CompletionRequest,
  ): Promise<CodeMirrorLspCompletionList | null> {
    const document = this.documents.get(path)
    if (!document || !this.initialized || !this.completionSupported || !this.isCurrent()) {
      return null
    }
    const version = document.version
    const result = await this.request('textDocument/completion', {
      textDocument: { uri: document.uri },
      position: { line: request.line, character: request.character },
      context: {
        triggerKind: request.triggerKind,
        ...(request.triggerCharacter ? { triggerCharacter: request.triggerCharacter } : {}),
      },
    })
    if (!this.isCurrent() || document.version !== version) return null
    return parseCompletionItems(result, version)
  }

  async resolveCompletion(
    path: string,
    item: CodeMirrorLspCompletionItem,
  ): Promise<CodeMirrorLspCompletionItem | null> {
    const document = this.documents.get(path)
    if (
      !document ||
      !this.initialized ||
      !this.isCurrent() ||
      document.version !== item.documentVersion
    ) {
      return null
    }
    if (!this.completionResolveSupported) return item

    const version = document.version
    const result = await this.request('completionItem/resolve', item.raw)
    if (!this.isCurrent() || document.version !== version) return null
    return parseCompletionItem(result, version, true) ?? item
  }

  async hover(path: string, position: LspPosition): Promise<CodeMirrorLspHover | null> {
    const document = this.documents.get(path)
    if (!document || !this.initialized || !this.hoverSupported || !this.isCurrent()) return null
    const version = document.version
    const result = await this.request('textDocument/hover', {
      textDocument: { uri: document.uri },
      position,
    }).catch(() => null)
    if (!this.isCurrent() || document.version !== version || !isRecord(result)) return null
    const range = parseRange(result.range)
    return {
      contents: result.contents,
      ...(range ? { range } : {}),
    }
  }

  async signatureHelp(
    path: string,
    position: LspPosition,
    triggerCharacter?: string,
    isRetrigger = triggerCharacter === ',',
  ): Promise<CodeMirrorLspSignatureHelp | null> {
    const document = this.documents.get(path)
    if (!document || !this.initialized || !this.signatureHelpSupported || !this.isCurrent()) {
      return null
    }
    const version = document.version
    const result = await this.request('textDocument/signatureHelp', {
      textDocument: { uri: document.uri },
      position,
      context: {
        triggerKind: triggerCharacter ? 2 : 1,
        ...(triggerCharacter ? { triggerCharacter } : {}),
        isRetrigger,
      },
    }).catch(() => null)
    if (!this.isCurrent() || document.version !== version) return null
    return parseSignatureHelp(result)
  }

  async dispose(): Promise<void> {
    if (this.disposed || this.closing) return
    this.closing = true
    for (const timer of this.semanticTimers.values()) window.clearTimeout(timer)
    this.semanticTimers.clear()
    for (const timer of this.structureTimers.values()) window.clearTimeout(timer)
    this.structureTimers.clear()

    if (this.initialized && this.socket.readyState === WebSocket.OPEN) {
      await this.request('shutdown', {}, 750).catch(() => undefined)
      this.notify('exit', {})
    }

    this.disposed = true
    this.closing = false
    this.detachBridge()
    this.socket.removeEventListener('message', this.handleMessage)
    this.socket.removeEventListener('close', this.handleClose)
    this.rejectPending(new Error('Language client was disposed.'))
    for (const document of this.documents.values()) this.sink.clearDocument(document.path)
    this.documents.clear()
    this.documentsByUri.clear()
  }

  private readServerCapabilities(result: unknown): void {
    const capabilities =
      isRecord(result) && isRecord(result.capabilities) ? result.capabilities : {}
    const completionProvider = isRecord(capabilities.completionProvider)
      ? capabilities.completionProvider
      : null
    this.completionSupported =
      this.featureProfile.completion &&
      (capabilities.completionProvider === true || completionProvider !== null)
    this.completionResolveSupported =
      this.featureProfile.completion && completionProvider?.resolveProvider === true
    this.hoverSupported =
      this.featureProfile.hover &&
      (capabilities.hoverProvider === true || isRecord(capabilities.hoverProvider))
    this.signatureHelpSupported =
      this.featureProfile.signatureHelp &&
      (capabilities.signatureHelpProvider === true || isRecord(capabilities.signatureHelpProvider))
    this.codeActionSupported =
      this.featureProfile.codeActions &&
      (capabilities.codeActionProvider === true || isRecord(capabilities.codeActionProvider))
    this.documentSymbolSupported =
      this.featureProfile.documentSymbols &&
      (capabilities.documentSymbolProvider === true ||
        isRecord(capabilities.documentSymbolProvider))
    this.foldingRangeSupported =
      this.featureProfile.foldingRanges &&
      (capabilities.foldingRangeProvider === true || isRecord(capabilities.foldingRangeProvider))
    const semanticProvider = isRecord(capabilities.semanticTokensProvider)
      ? capabilities.semanticTokensProvider
      : null
    const legend =
      semanticProvider && isRecord(semanticProvider.legend) ? semanticProvider.legend : null
    const tokenTypes = legend ? stringArray(legend.tokenTypes) : null
    const tokenModifiers = legend ? stringArray(legend.tokenModifiers) : null
    this.semanticLegend =
      this.featureProfile.semanticTokens && tokenTypes && tokenModifiers
        ? { tokenTypes, tokenModifiers }
        : null
  }

  private scheduleSemanticTokens(path: string, delay: number): void {
    if (!this.semanticLegend) return
    const document = this.documents.get(path)
    if (!document) return
    const generation = ++document.semanticGeneration
    const previous = this.semanticTimers.get(path)
    if (previous !== undefined) window.clearTimeout(previous)
    const timer = window.setTimeout(() => {
      this.semanticTimers.delete(path)
      void this.refreshSemanticTokens(path, generation)
    }, delay)
    this.semanticTimers.set(path, timer)
  }

  private async refreshSemanticTokens(path: string, generation: number): Promise<void> {
    const document = this.documents.get(path)
    const legend = this.semanticLegend
    if (
      !document ||
      !legend ||
      document.semanticGeneration !== generation ||
      this.disposed ||
      !this.initialized ||
      !this.isCurrent()
    ) {
      return
    }
    const version = document.version
    const result = await this.request('textDocument/semanticTokens/full', {
      textDocument: { uri: document.uri },
    }).catch(() => null)
    if (
      !this.isCurrent() ||
      document.version !== version ||
      document.semanticGeneration !== generation ||
      !isRecord(result)
    ) {
      return
    }
    const data = numberArray(result.data)
    if (!data) return
    this.sink.publishSemanticTokens(
      document.path,
      version,
      decodeSemanticTokens(data, legend.tokenTypes, legend.tokenModifiers),
    )
  }

  private scheduleStructure(path: string, delay: number): void {
    if (!this.documentSymbolSupported && !this.foldingRangeSupported) return
    const previous = this.structureTimers.get(path)
    if (previous !== undefined) window.clearTimeout(previous)
    const timer = window.setTimeout(() => {
      this.structureTimers.delete(path)
      void this.refreshStructure(path)
    }, delay)
    this.structureTimers.set(path, timer)
  }

  private async refreshStructure(path: string): Promise<void> {
    const document = this.documents.get(path)
    if (!document || this.disposed || !this.initialized || !this.isCurrent()) return
    const version = document.version
    const requests: Promise<void>[] = []
    if (this.documentSymbolSupported) {
      requests.push(
        this.request('textDocument/documentSymbol', {
          textDocument: { uri: document.uri },
        })
          .then((result) => {
            if (!this.isCurrent() || document.version !== version) return
            this.sink.publishDocumentSymbols(document.path, version, parseDocumentSymbols(result))
          })
          .catch(() => undefined),
      )
    } else if (this.featureProfile.documentSymbols) {
      this.sink.publishDocumentSymbols(document.path, version, null)
    }
    if (this.foldingRangeSupported) {
      requests.push(
        this.request('textDocument/foldingRange', {
          textDocument: { uri: document.uri },
        })
          .then((result) => {
            if (!this.isCurrent() || document.version !== version) return
            this.sink.publishFoldingRanges(document.path, version, parseFoldingRanges(result))
          })
          .catch(() => undefined),
      )
    } else if (this.featureProfile.foldingRanges) {
      this.sink.publishFoldingRanges(document.path, version, null)
    }
    await Promise.all(requests)
  }

  private request(method: string, parameters: unknown, timeoutMs = 10_000): Promise<unknown> {
    if (this.disposed || this.socket.readyState !== WebSocket.OPEN) {
      return Promise.reject(
        new LanguageSessionTransportError(
          'websocket-not-open',
          'Language server connection is not open.',
        ),
      )
    }
    const id = this.nextRequestId++
    return new Promise((resolve, reject) => {
      const timeout = window.setTimeout(() => {
        this.pending.delete(id)
        reject(
          new LanguageSessionTransportError(
            method === 'initialize' ? 'initialize-timeout' : 'request-timeout',
            `Language server request '${method}' timed out.`,
          ),
        )
      }, timeoutMs)
      this.pending.set(id, { resolve, reject, timeout })
      this.send({ jsonrpc: '2.0', id, method, params: parameters })
    })
  }

  private notify(method: string, parameters: unknown): void {
    if (this.socket.readyState !== WebSocket.OPEN) return
    this.send({ jsonrpc: '2.0', method, params: parameters })
  }

  private send(message: unknown): void {
    this.socket.send(JSON.stringify(message))
  }

  private readonly handleMessage = (event: MessageEvent<unknown>): void => {
    this.messageQueue = this.messageQueue
      .then(async () => this.receive(await messageText(event.data)))
      .catch(() => undefined)
  }

  private readonly handleClose = (): void => {
    this.rejectPending(
      new LanguageSessionTransportError('websocket-closed', 'Language server connection closed.'),
    )
  }

  private async receive(text: string): Promise<void> {
    let message: unknown
    try {
      message = JSON.parse(text)
    } catch {
      return
    }
    if (!isRecord(message)) return

    if (typeof message.id === 'number' && !('method' in message)) {
      const pending = this.pending.get(message.id)
      if (!pending) return
      this.pending.delete(message.id)
      window.clearTimeout(pending.timeout)
      if (isRecord(message.error)) {
        pending.reject(
          new LanguageSessionProtocolError(
            typeof message.error.message === 'string'
              ? message.error.message
              : 'Language server request failed.',
          ),
        )
      } else {
        pending.resolve(message.result)
      }
      return
    }

    if (typeof message.method !== 'string') return
    if ('id' in message && (typeof message.id === 'number' || typeof message.id === 'string')) {
      await this.handleServerRequest(message.id, message.method, message.params)
      return
    }
    this.handleNotification(message.method, message.params)
  }

  private async handleServerRequest(
    id: number | string,
    method: string,
    parameters: unknown,
  ): Promise<void> {
    let result: unknown = null
    if (method === 'workspace/configuration') {
      const items = isRecord(parameters) && Array.isArray(parameters.items) ? parameters.items : []
      result = items.map(() => null)
    } else if (method === 'workspace/workspaceFolders') {
      result = [{ uri: this.plan.workspaceUri, name: 'SharpLabNext' }]
    } else if (
      method !== 'client/registerCapability' &&
      method !== 'client/unregisterCapability' &&
      method !== 'window/workDoneProgress/create'
    ) {
      this.send({
        jsonrpc: '2.0',
        id,
        error: { code: -32601, message: `Method '${method}' is not supported by this client.` },
      })
      return
    }
    this.send({ jsonrpc: '2.0', id, result })
  }

  private handleNotification(method: string, parameters: unknown): void {
    if (method === 'textDocument/publishDiagnostics' && this.featureProfile.diagnostics) {
      this.publishDiagnostics(parameters)
      return
    }
    if (method === 'workspace/semanticTokens/refresh' && this.featureProfile.semanticTokens) {
      for (const document of this.documents.values()) this.scheduleSemanticTokens(document.path, 0)
    }
  }

  private publishDiagnostics(parameters: unknown): void {
    if (!this.isCurrent() || !isRecord(parameters) || typeof parameters.uri !== 'string') return
    const document = this.documentsByUri.get(parameters.uri)
    if (!document) return
    const version = isNonNegativeInteger(parameters.version) ? parameters.version : undefined
    if (version !== undefined && version !== document.version) return
    const diagnostics = Array.isArray(parameters.diagnostics)
      ? parameters.diagnostics.flatMap((value) => {
          const diagnostic = parseDiagnostic(value)
          return diagnostic &&
            isCurrentLspDiagnostic(diagnostic.data, this.plan.selectionRevision, document.version)
            ? [diagnostic]
            : []
        })
      : []
    this.sink.publishDiagnostics(document.path, version, diagnostics)
    if (this.codeActionSupported && diagnostics.length > 0) {
      const generation = ++document.diagnosticGeneration
      void this.publishCodeActions(document, diagnostics, generation)
    } else {
      document.diagnosticGeneration += 1
    }
  }

  private async publishCodeActions(
    document: LspDocument,
    diagnostics: readonly CodeMirrorLspDiagnostic[],
    generation: number,
  ): Promise<void> {
    const version = document.version
    const end = positionAtTextEnd(document.text)
    const result = await this.request('textDocument/codeAction', {
      textDocument: { uri: document.uri },
      range: { start: { line: 0, character: 0 }, end },
      context: {
        diagnostics: diagnostics.map((diagnostic) => diagnostic.raw ?? diagnostic),
        only: ['quickfix'],
      },
    }).catch(() => null)
    if (
      !this.isCurrent() ||
      document.version !== version ||
      document.diagnosticGeneration !== generation
    ) {
      return
    }
    const actions = parseCodeActions(result, this.documentsByUri)
    if (actions.length === 0) return
    const enriched = diagnostics.map((diagnostic) => {
      const matching = actions.filter((action) => codeActionMatchesDiagnostic(action, diagnostic))
      return matching.length > 0 ? { ...diagnostic, actions: matching } : diagnostic
    })
    this.sink.publishDiagnostics(document.path, version, enriched)
  }

  private rejectPending(error: Error): void {
    for (const pending of this.pending.values()) {
      window.clearTimeout(pending.timeout)
      pending.reject(error)
    }
    this.pending.clear()
  }
}

function clientCapabilities(
  featureProfile: Readonly<CodeMirrorLanguageClientFeatureProfile>,
): object {
  const textDocument: Record<string, unknown> = {}
  if (featureProfile.synchronizeDocuments) {
    textDocument.synchronization = {
      dynamicRegistration: false,
      willSave: false,
      didSave: false,
    }
  }
  if (featureProfile.completion) {
    textDocument.completion = {
      dynamicRegistration: false,
      contextSupport: true,
      completionItem: {
        snippetSupport: true,
        documentationFormat: ['markdown', 'plaintext'],
        resolveSupport: {
          properties: [
            'detail',
            'documentation',
            'insertTextFormat',
            'textEdit',
            'additionalTextEdits',
          ],
        },
      },
    }
  }
  if (featureProfile.hover) {
    textDocument.hover = { dynamicRegistration: false, contentFormat: ['markdown', 'plaintext'] }
  }
  if (featureProfile.signatureHelp) {
    textDocument.signatureHelp = {
      dynamicRegistration: false,
      contextSupport: true,
      signatureInformation: {
        documentationFormat: ['markdown', 'plaintext'],
        activeParameterSupport: true,
        parameterInformation: { labelOffsetSupport: true },
      },
    }
  }
  if (featureProfile.documentSymbols) {
    textDocument.documentSymbol = {
      dynamicRegistration: false,
      hierarchicalDocumentSymbolSupport: true,
    }
  }
  if (featureProfile.foldingRanges) {
    textDocument.foldingRange = {
      dynamicRegistration: false,
      lineFoldingOnly: false,
    }
  }
  if (featureProfile.codeActions) {
    textDocument.codeAction = {
      dynamicRegistration: false,
      isPreferredSupport: true,
      codeActionLiteralSupport: {
        codeActionKind: { valueSet: ['quickfix'] },
      },
    }
  }
  if (featureProfile.semanticTokens) {
    textDocument.semanticTokens = {
      dynamicRegistration: false,
      requests: { range: false, full: true },
      tokenTypes: [...lspSemanticTokenTypes],
      tokenModifiers: [...lspSemanticTokenModifiers],
      formats: ['relative'],
      overlappingTokenSupport: false,
      multilineTokenSupport: false,
    }
  }
  return {
    workspace: { workspaceFolders: true, configuration: true },
    textDocument,
  }
}

function parseCompletionItems(
  result: unknown,
  documentVersion: number,
): CodeMirrorLspCompletionList | null {
  const completionList = isRecord(result) ? result : null
  const values = Array.isArray(result)
    ? result
    : completionList && Array.isArray(completionList.items)
      ? completionList.items
      : null
  if (!values) return null
  return {
    isIncomplete: !Array.isArray(result) && completionList?.isIncomplete === true,
    items: values.flatMap((value) => {
      const item = parseCompletionItem(value, documentVersion, false)
      return item ? [item] : []
    }),
  }
}

function parseCompletionItem(
  value: unknown,
  documentVersion: number,
  strictEdits: boolean,
): CodeMirrorLspCompletionItem | null {
  if (!isRecord(value) || typeof value.label !== 'string') return null

  const textEdit = 'textEdit' in value ? parseTextEdit(value.textEdit) : undefined
  if (strictEdits && 'textEdit' in value && !textEdit) return null

  let additionalTextEdits: CodeMirrorLspTextEdit[] | undefined
  if ('additionalTextEdits' in value) {
    if (!Array.isArray(value.additionalTextEdits)) {
      if (strictEdits) return null
    } else {
      additionalTextEdits = value.additionalTextEdits.flatMap((candidate) => {
        const edit = parseTextEdit(candidate)
        return edit ? [edit] : []
      })
      if (additionalTextEdits.length !== value.additionalTextEdits.length) {
        if (strictEdits) return null
        additionalTextEdits = undefined
      }
    }
  }

  return {
    label: value.label,
    ...(typeof value.detail === 'string' ? { detail: value.detail } : {}),
    ...('documentation' in value ? { documentation: value.documentation } : {}),
    ...(typeof value.insertText === 'string' ? { insertText: value.insertText } : {}),
    ...(typeof value.insertTextFormat === 'number'
      ? { insertTextFormat: value.insertTextFormat }
      : {}),
    ...(typeof value.kind === 'number' ? { kind: value.kind } : {}),
    ...(typeof value.sortText === 'string' ? { sortText: value.sortText } : {}),
    ...(typeof value.filterText === 'string' ? { filterText: value.filterText } : {}),
    ...(textEdit ? { textEdit } : {}),
    ...(additionalTextEdits ? { additionalTextEdits } : {}),
    raw: value,
    documentVersion,
  }
}

function parseSignatureHelp(result: unknown): CodeMirrorLspSignatureHelp | null {
  if (!isRecord(result) || !Array.isArray(result.signatures)) return null
  const signatures = result.signatures.flatMap((value) => {
    if (!isRecord(value) || typeof value.label !== 'string') return []
    const parameters = Array.isArray(value.parameters)
      ? value.parameters.flatMap((parameter) => {
          if (!isRecord(parameter)) return []
          const label = parseParameterLabel(parameter.label)
          if (!label) return []
          return [
            {
              label,
              ...('documentation' in parameter ? { documentation: parameter.documentation } : {}),
            } satisfies CodeMirrorLspParameterInformation,
          ]
        })
      : []
    return [
      {
        label: value.label,
        ...('documentation' in value ? { documentation: value.documentation } : {}),
        parameters,
        ...(isNonNegativeInteger(value.activeParameter)
          ? { activeParameter: value.activeParameter }
          : {}),
      } satisfies CodeMirrorLspSignatureInformation,
    ]
  })
  if (signatures.length === 0) return null
  const activeSignature = Math.min(
    isNonNegativeInteger(result.activeSignature) ? result.activeSignature : 0,
    signatures.length - 1,
  )
  const signature = signatures[activeSignature]
  const activeParameter = Math.min(
    isNonNegativeInteger(result.activeParameter)
      ? result.activeParameter
      : (signature?.activeParameter ?? 0),
    Math.max(0, (signature?.parameters.length ?? 1) - 1),
  )
  return { signatures, activeSignature, activeParameter }
}

function parseParameterLabel(value: unknown): string | readonly [number, number] | null {
  if (typeof value === 'string') return value
  if (
    Array.isArray(value) &&
    value.length === 2 &&
    isNonNegativeInteger(value[0]) &&
    isNonNegativeInteger(value[1]) &&
    value[1] >= value[0]
  ) {
    return [value[0], value[1]]
  }
  return null
}

function parseDocumentSymbols(result: unknown): CodeMirrorDocumentSymbol[] {
  if (!Array.isArray(result)) return []
  return result.flatMap((value) => {
    const symbol = parseDocumentSymbol(value, 0)
    return symbol ? [symbol] : []
  })
}

function parseDocumentSymbol(value: unknown, depth: number): CodeMirrorDocumentSymbol | null {
  if (!isRecord(value) || depth > 64 || typeof value.name !== 'string') return null
  const location = isRecord(value.location) ? value.location : null
  const range = parseRange(value.range) ?? parseRange(location?.range)
  const selectionRange = parseRange(value.selectionRange) ?? range
  if (!range || !selectionRange || !isPositiveInteger(value.kind)) return null
  const children = Array.isArray(value.children)
    ? value.children.flatMap((child) => {
        const parsed = parseDocumentSymbol(child, depth + 1)
        return parsed ? [parsed] : []
      })
    : []
  return {
    name: value.name,
    ...(typeof value.detail === 'string' ? { detail: value.detail } : {}),
    kind: value.kind,
    range,
    selectionRange,
    children,
  }
}

function parseFoldingRanges(result: unknown): CodeMirrorLspFoldingRange[] {
  if (!Array.isArray(result)) return []
  return result.flatMap((value) => {
    if (
      !isRecord(value) ||
      !isNonNegativeInteger(value.startLine) ||
      !isNonNegativeInteger(value.endLine) ||
      value.endLine < value.startLine
    ) {
      return []
    }
    return [
      {
        startLine: value.startLine,
        ...(isNonNegativeInteger(value.startCharacter)
          ? { startCharacter: value.startCharacter }
          : {}),
        endLine: value.endLine,
        ...(isNonNegativeInteger(value.endCharacter) ? { endCharacter: value.endCharacter } : {}),
        ...(typeof value.kind === 'string' ? { kind: value.kind } : {}),
      } satisfies CodeMirrorLspFoldingRange,
    ]
  })
}

function parseCodeActions(
  result: unknown,
  documentsByUri: ReadonlyMap<string, LspDocument>,
): CodeMirrorLspCodeAction[] {
  if (!Array.isArray(result)) return []
  return result.flatMap((value) => {
    if (!isRecord(value) || typeof value.title !== 'string') return []
    if (typeof value.kind === 'string' && !value.kind.startsWith('quickfix')) return []
    const documentEdits = parseWorkspaceEdit(value.edit, documentsByUri)
    if (documentEdits.length === 0) return []
    const diagnostics = Array.isArray(value.diagnostics)
      ? value.diagnostics.flatMap((diagnostic) => {
          if (!isRecord(diagnostic)) return []
          const range = parseRange(diagnostic.range)
          if (!range) return []
          return [
            {
              range,
              ...(typeof diagnostic.code === 'string' || typeof diagnostic.code === 'number'
                ? { code: diagnostic.code }
                : {}),
            },
          ]
        })
      : []
    return [
      {
        title: value.title,
        ...(typeof value.kind === 'string' ? { kind: value.kind } : {}),
        ...(typeof value.isPreferred === 'boolean' ? { isPreferred: value.isPreferred } : {}),
        diagnostics,
        documentEdits,
      } satisfies CodeMirrorLspCodeAction,
    ]
  })
}

function parseWorkspaceEdit(
  value: unknown,
  documentsByUri: ReadonlyMap<string, LspDocument>,
): CodeMirrorWorkspaceDocumentEdit[] {
  if (!isRecord(value)) return []
  const edits = new Map<string, CodeMirrorLspTextEdit[]>()
  if (isRecord(value.changes)) {
    for (const [uri, candidates] of Object.entries(value.changes)) {
      if (!Array.isArray(candidates)) continue
      const parsed = candidates.flatMap((candidate) => {
        const edit = parseTextEdit(candidate)
        return edit ? [edit] : []
      })
      if (parsed.length > 0) edits.set(uri, parsed)
    }
  }
  if (Array.isArray(value.documentChanges)) {
    for (const change of value.documentChanges) {
      if (!isRecord(change) || !isRecord(change.textDocument) || !Array.isArray(change.edits)) {
        continue
      }
      const uri = change.textDocument.uri
      if (typeof uri !== 'string') continue
      const parsed = change.edits.flatMap((candidate) => {
        const edit = parseTextEdit(candidate)
        return edit ? [edit] : []
      })
      if (parsed.length > 0) edits.set(uri, parsed)
    }
  }
  return [...edits].flatMap(([uri, textEdits]) => {
    const document = documentsByUri.get(uri)
    return document
      ? [
          {
            documentPath: document.path,
            documentVersion: document.version,
            edits: textEdits,
          },
        ]
      : []
  })
}

function parseTextEdit(value: unknown): CodeMirrorLspTextEdit | null {
  if (!isRecord(value) || typeof value.newText !== 'string') return null
  const range = parseRange(value.range)
  return range ? { range, newText: value.newText } : null
}

function codeActionMatchesDiagnostic(
  action: CodeMirrorLspCodeAction,
  diagnostic: CodeMirrorLspDiagnostic,
): boolean {
  if (action.diagnostics.length > 0) {
    return action.diagnostics.some(
      (candidate) =>
        (candidate.code === undefined ||
          diagnostic.code === undefined ||
          String(candidate.code) === String(diagnostic.code)) &&
        rangesIntersect(candidate.range, diagnostic.range),
    )
  }
  return action.documentEdits.some((document) =>
    document.edits.some((edit) => rangesIntersect(edit.range, diagnostic.range)),
  )
}

function rangesIntersect(left: LspRange, right: LspRange): boolean {
  return comparePosition(left.start, right.end) <= 0 && comparePosition(right.start, left.end) <= 0
}

function comparePosition(left: LspPosition, right: LspPosition): number {
  return left.line === right.line ? left.character - right.character : left.line - right.line
}

function positionAtTextEnd(text: string): LspPosition {
  const lines = text.split(/\r\n|\r|\n/)
  const last = lines.at(-1) ?? ''
  return { line: Math.max(0, lines.length - 1), character: last.length }
}

function parseDiagnostic(value: unknown): CodeMirrorLspDiagnostic | null {
  if (!isRecord(value) || typeof value.message !== 'string') return null
  const range = parseRange(value.range)
  if (!range) return null
  return {
    range,
    message: value.message,
    ...(typeof value.severity === 'number' ? { severity: value.severity } : {}),
    ...(typeof value.source === 'string' ? { source: value.source } : {}),
    ...(typeof value.code === 'string' || typeof value.code === 'number'
      ? { code: value.code }
      : {}),
    ...('data' in value ? { data: value.data } : {}),
    raw: value,
  }
}

function parseRange(value: unknown): LspRange | null {
  if (!isRecord(value)) return null
  const start = parsePosition(value.start)
  const end = parsePosition(value.end)
  return start && end ? { start, end } : null
}

function parsePosition(value: unknown): LspPosition | null {
  return isRecord(value) &&
    isNonNegativeInteger(value.line) &&
    isNonNegativeInteger(value.character)
    ? { line: value.line, character: value.character }
    : null
}

function stringArray(value: unknown): string[] | null {
  return Array.isArray(value) && value.every((item) => typeof item === 'string') ? value : null
}

function numberArray(value: unknown): number[] | null {
  return Array.isArray(value) && value.every((item) => typeof item === 'number') ? value : null
}

async function messageText(data: unknown): Promise<string> {
  if (typeof data === 'string') return data
  if (data instanceof Blob) return data.text()
  if (data instanceof ArrayBuffer) return new TextDecoder().decode(data)
  if (ArrayBuffer.isView(data)) {
    return new TextDecoder().decode(new Uint8Array(data.buffer, data.byteOffset, data.byteLength))
  }
  throw new Error('Language server returned an unsupported WebSocket message.')
}

function waitForSocketOpen(socket: WebSocket): Promise<void> {
  if (socket.readyState === WebSocket.OPEN) return Promise.resolve()
  if (socket.readyState !== WebSocket.CONNECTING) {
    return Promise.reject(
      new LanguageSessionTransportError(
        'websocket-closed',
        'Language server connection closed before it opened.',
      ),
    )
  }
  return new Promise((resolve, reject) => {
    const cleanup = () => {
      socket.removeEventListener('open', onOpen)
      socket.removeEventListener('error', onError)
      socket.removeEventListener('close', onClose)
    }
    const onOpen = () => {
      cleanup()
      resolve()
    }
    const onError = () => {
      cleanup()
      reject(
        new LanguageSessionTransportError(
          'websocket-open-failed',
          'Language server WebSocket failed to open.',
        ),
      )
    }
    const onClose = () => {
      cleanup()
      reject(
        new LanguageSessionTransportError(
          'websocket-closed',
          'Language server connection closed before it opened.',
        ),
      )
    }
    socket.addEventListener('open', onOpen)
    socket.addEventListener('error', onError)
    socket.addEventListener('close', onClose)
  })
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isNonNegativeInteger(value: unknown): value is number {
  return Number.isSafeInteger(value) && Number(value) >= 0
}

function isPositiveInteger(value: unknown): value is number {
  return Number.isSafeInteger(value) && Number(value) > 0
}
