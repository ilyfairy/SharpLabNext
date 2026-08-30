import * as monaco from '../editor/monacoCore'
import {
  type CodeMirrorDocumentSymbol,
  CodeMirrorLanguageBridge,
  type CodeMirrorLanguageSink,
  type CodeMirrorLspCodeAction,
  type CodeMirrorLspCompletionItem,
  type CodeMirrorLspCompletionList,
  type CodeMirrorLspDiagnostic,
  type CodeMirrorLspFoldingRange,
  type CodeMirrorLspSignatureHelp,
  type CodeMirrorLspTextEdit,
  type CodeMirrorSemanticToken,
  createCodeMirrorLanguageSessionDependencies,
  type LspRange,
  lspSemanticTokenModifiers,
  lspSemanticTokenTypes,
} from './codeMirrorLanguageClient'
import { ilSenseCompletionTriggerCharacters } from './completionTriggerCharacters'
import type { LanguageSessionLifecycleDependencies, LanguageSessionStatus } from './languageSessionLifecycle'

interface MonacoDocumentState {
  path: string
  model: monaco.editor.ITextModel
  diagnostics: readonly CodeMirrorLspDiagnostic[]
  semanticTokens: readonly CodeMirrorSemanticToken[]
  symbols: readonly CodeMirrorDocumentSymbol[] | null
  foldingRanges: readonly CodeMirrorLspFoldingRange[] | null
  // Monaco drops empty providers before it records their `incomplete` bit.
  // Preserve the request position so one directly-following local insertion
  // can explicitly recover without leaking the retry to another cursor.
  emptyCompletionRetry: EmptyCompletionRetry | null
  emptyCompletionRecoveryVersion: number | null
}

export interface EmptyCompletionRetry {
  documentVersion: number
  lineNumber: number
  column: number
}

export interface CompletionContentChange {
  range: {
    startLineNumber: number
    startColumn: number
    endLineNumber: number
    endColumn: number
  }
  text: string
}

export interface MonacoDocumentSymbolsChange {
  path: string
  version: number
  symbols: readonly CodeMirrorDocumentSymbol[] | null
}

interface MonacoCompletionItem extends monaco.languages.CompletionItem {
  lspItem: CodeMirrorLspCompletionItem
  documentPath: string
}

export class MonacoLanguageBridge {
  private readonly transport = new CodeMirrorLanguageBridge()
  private readonly documents = new Map<string, MonacoDocumentState>()
  private readonly documentsByUri = new Map<string, MonacoDocumentState>()
  private readonly providerDisposables: monaco.IDisposable[] = []
  private readonly semanticChange = new monaco.Emitter<void>()
  private readonly foldingChange = new monaco.Emitter<monaco.languages.FoldingRangeProvider>()
  private readonly symbolsChange = new monaco.Emitter<MonacoDocumentSymbolsChange>()
  private registeredLanguageId: string | null = null
  private foldingProvider: monaco.languages.FoldingRangeProvider | null = null

  readonly onDidChangeDocumentSymbols = this.symbolsChange.event

  registerDocument(path: string, model: monaco.editor.ITextModel): void {
    this.unregisterDocument(path)
    const state: MonacoDocumentState = {
      path,
      model,
      diagnostics: [],
      semanticTokens: [],
      symbols: null,
      foldingRanges: null,
      emptyCompletionRetry: null,
      emptyCompletionRecoveryVersion: null,
    }
    this.documents.set(path, state)
    this.documentsByUri.set(model.uri.toString(), state)
  }

  unregisterDocument(path: string): void {
    const state = this.documents.get(path)
    if (!state) return
    monaco.editor.setModelMarkers(state.model, markerOwner, [])
    this.documents.delete(path)
    this.documentsByUri.delete(state.model.uri.toString())
  }

  consumeEmptyCompletionRetry(path: string, currentVersion: number, changes: readonly CompletionContentChange[]): boolean {
    const state = this.documents.get(path)
    if (!state) return false
    const retry = state.emptyCompletionRetry
    state.emptyCompletionRetry = null
    if (!canConsumeEmptyCompletionRetry(retry, currentVersion, changes, monacoLanguageTriggerCharacters(this.registeredLanguageId ?? '').completion)) {
      return false
    }
    // Suppress every empty Invoke response produced at this recovery version.
    // Monaco can start an automatic request near the explicit retrigger, and
    // either response must not arm an endless edit/retry loop.
    state.emptyCompletionRecoveryVersion = currentVersion
    return true
  }

  clearEmptyCompletionRetry(path: string): void {
    const state = this.documents.get(path)
    if (!state) return
    clearEmptyCompletionState(state)
  }

  changeDocument(path: string, text: string, version: number): void {
    this.transport.changeDocument(path, text, version)
  }

  // Keep completion requests pending while the selected LSP client initializes.
  setSessionStatus(status: LanguageSessionStatus): void {
    this.transport.setSessionStatus(status)
    if (status === 'ready') return
    for (const state of this.documents.values()) clearEmptyCompletionState(state)
  }

  setLanguage(languageId: string): void {
    if (this.registeredLanguageId === languageId) return
    this.disposeProviders()
    for (const state of this.documents.values()) clearEmptyCompletionState(state)
    this.registeredLanguageId = languageId
    const triggers = monacoLanguageTriggerCharacters(languageId)

    const foldingProvider: monaco.languages.FoldingRangeProvider = {
      onDidChange: this.foldingChange.event,
      provideFoldingRanges: (model) => {
        const state = this.documentState(model)
        return monacoFoldingRanges(state?.foldingRanges)
      },
    }
    this.foldingProvider = foldingProvider

    this.providerDisposables.push(
      monaco.languages.registerCompletionItemProvider(languageId, {
        triggerCharacters: [...triggers.completion],
        provideCompletionItems: async (model, position, context, token) => {
          const state = this.documentState(model)
          if (!state) return null
          const version = model.getVersionId()
          const triggerKind = context.triggerKind === monaco.languages.CompletionTriggerKind.Invoke ? 1 : context.triggerKind === monaco.languages.CompletionTriggerKind.TriggerCharacter ? 2 : 3
          const completionList = await this.transport.completion(state.path, {
            line: position.lineNumber - 1,
            character: position.column - 1,
            triggerKind,
            ...(context.triggerCharacter ? { triggerCharacter: context.triggerCharacter } : {}),
          })
          if (token.isCancellationRequested || model.getVersionId() !== version || !completionList) return null
          const recoveryResult = state.emptyCompletionRecoveryVersion === version
          state.emptyCompletionRetry = emptyCompletionRetryForResult(version, position, completionList.items.length, recoveryResult)
          if (completionList.items.length > 0) state.emptyCompletionRecoveryVersion = null
          return monacoCompletionList(model, position, state.path, completionList)
        },
        resolveCompletionItem: async (item, token) => {
          const candidate = item as MonacoCompletionItem
          const state = this.documents.get(candidate.documentPath)
          if (!state) return item
          const resolved = await this.transport.resolveCompletion(candidate.documentPath, candidate.lspItem)
          if (token.isCancellationRequested || !resolved) return item
          const position = positionForCompletionRange(candidate.range)
          return completionItem(state.model, position, state.path, resolved)
        },
      }),
      monaco.languages.registerHoverProvider(languageId, {
        provideHover: async (model, position, token) => {
          const state = this.documentState(model)
          if (!state) return null
          const version = model.getVersionId()
          const hover = await this.transport.hover(state.path, {
            line: position.lineNumber - 1,
            character: position.column - 1,
          })
          if (token.isCancellationRequested || model.getVersionId() !== version || !hover) return null
          const contents = markdownContents(hover.contents)
          if (contents.length === 0) return null
          return {
            contents,
            ...(hover.range ? { range: toMonacoRange(hover.range) } : {}),
          }
        },
      }),
      monaco.languages.registerSignatureHelpProvider(languageId, {
        signatureHelpTriggerCharacters: triggers.signature,
        signatureHelpRetriggerCharacters: triggers.signatureRetrigger,
        provideSignatureHelp: async (model, position, token, context) => {
          const state = this.documentState(model)
          if (!state) return null
          const version = model.getVersionId()
          const help = await this.transport.signatureHelp(state.path, { line: position.lineNumber - 1, character: position.column - 1 }, context.triggerCharacter, context.isRetrigger)
          if (token.isCancellationRequested || model.getVersionId() !== version || !help) return null
          return { value: signatureHelp(help), dispose() {} }
        },
      }),
      monaco.languages.registerDocumentSymbolProvider(languageId, {
        provideDocumentSymbols: (model) => {
          const state = this.documentState(model)
          return state?.symbols?.map(toMonacoDocumentSymbol) ?? []
        },
      }),
      monaco.languages.registerFoldingRangeProvider(languageId, foldingProvider),
      monaco.languages.registerDocumentSemanticTokensProvider(languageId, {
        onDidChange: this.semanticChange.event,
        getLegend: () => ({
          tokenTypes: [...lspSemanticTokenTypes],
          tokenModifiers: [...lspSemanticTokenModifiers],
        }),
        provideDocumentSemanticTokens: (model) => {
          const state = this.documentState(model)
          return { data: encodeSemanticTokens(state?.semanticTokens ?? []) }
        },
        releaseDocumentSemanticTokens() {},
      }),
      monaco.languages.registerCodeActionProvider(
        languageId,
        {
          provideCodeActions: (model, _range, context) => {
            const state = this.documentState(model)
            const actions = state ? monacoCodeActions(state, this.documents, context.markers) : []
            return { actions, dispose() {} }
          },
        },
        { providedCodeActionKinds: ['quickfix'] },
      ),
    )
  }

  createDependencies(): LanguageSessionLifecycleDependencies {
    return createCodeMirrorLanguageSessionDependencies(this.transport, this.sink)
  }

  clearLanguageState(): void {
    for (const state of this.documents.values()) this.clearDocument(state.path)
  }

  dispose(): void {
    this.disposeProviders()
    for (const state of this.documents.values()) {
      monaco.editor.setModelMarkers(state.model, markerOwner, [])
    }
    this.documents.clear()
    this.documentsByUri.clear()
    this.semanticChange.dispose()
    this.foldingChange.dispose()
    this.symbolsChange.dispose()
  }

  private readonly sink: CodeMirrorLanguageSink = {
    publishDiagnostics: (path, version, diagnostics) => {
      const state = this.currentDocument(path, version)
      if (!state) return
      state.diagnostics = diagnostics
      monaco.editor.setModelMarkers(state.model, markerOwner, diagnostics.map(toMonacoMarker))
    },
    publishSemanticTokens: (path, version, tokens) => {
      const state = this.currentDocument(path, version)
      if (!state) return
      state.semanticTokens = tokens
      this.semanticChange.fire()
    },
    publishDocumentSymbols: (path, version, symbols) => {
      const state = this.currentDocument(path, version)
      if (!state) return
      state.symbols = symbols
      this.symbolsChange.fire({ path, version, symbols })
    },
    publishFoldingRanges: (path, version, ranges) => {
      const state = this.currentDocument(path, version)
      if (!state) return
      state.foldingRanges = ranges
      this.fireFoldingChange()
    },
    clearDocument: (path) => this.clearDocument(path),
  }

  private clearDocument(path: string): void {
    const state = this.documents.get(path)
    if (!state) return
    clearEmptyCompletionState(state)
    state.diagnostics = []
    state.semanticTokens = []
    state.symbols = null
    state.foldingRanges = null
    monaco.editor.setModelMarkers(state.model, markerOwner, [])
    this.semanticChange.fire()
    this.fireFoldingChange()
    this.symbolsChange.fire({
      path,
      version: state.model.getVersionId(),
      symbols: null,
    })
  }

  private currentDocument(path: string, version: number | undefined): MonacoDocumentState | null {
    const state = this.documents.get(path)
    return state && (version === undefined || state.model.getVersionId() === version) ? state : null
  }

  private documentState(model: monaco.editor.ITextModel): MonacoDocumentState | null {
    const state = this.documentsByUri.get(model.uri.toString())
    return state?.model === model ? state : null
  }

  private disposeProviders(): void {
    for (const disposable of this.providerDisposables.splice(0)) disposable.dispose()
    this.foldingProvider = null
  }

  private fireFoldingChange(): void {
    if (this.foldingProvider) this.foldingChange.fire(this.foldingProvider)
  }
}

export function monacoCompletionList(model: monaco.editor.ITextModel, position: monaco.Position, documentPath: string, completionList: CodeMirrorLspCompletionList): monaco.languages.CompletionList {
  return {
    suggestions: completionList.items.map((item) => completionItem(model, position, documentPath, item)),
    // Preserve the protocol bit for non-empty lists. Empty-list retries are
    // handled by MonacoLanguageBridge/MonacoEditor because Monaco's suggest
    // model drops providers that have no items.
    ...(completionList.isIncomplete ? { incomplete: true } : {}),
  }
}

/**
 * An empty Monaco completion list cannot carry the provider's incomplete bit
 * through the suggest model. A later document version is therefore the only
 * safe point at which the editor may issue a one-shot explicit retry.
 */
export function canConsumeEmptyCompletionRetry(retry: EmptyCompletionRetry | null | undefined, currentVersion: number, changes: readonly CompletionContentChange[], triggerCharacters: readonly string[]): boolean {
  if (!retry || currentVersion <= retry.documentVersion || changes.length !== 1) return false
  const change = changes[0]
  if (!change || change.text.length === 0) return false
  // A literal space is a completion boundary, not a reason to reopen an
  // empty suggestion list. Keep newline-plus-indentation edits eligible so
  // pressing Enter can still recover contextual IL suggestions.
  if (change.text === ' ') return false
  const range = change.range
  const insertsAtRequestPosition = range.startLineNumber === retry.lineNumber && range.startColumn === retry.column && range.endLineNumber === retry.lineNumber && range.endColumn === retry.column
  if (!insertsAtRequestPosition) return false

  // Monaco already issues the language provider's TriggerCharacter request.
  // An explicit Invoke here can cancel it and lose the trigger context.
  return !triggerCharacters.some((trigger) => change.text.endsWith(trigger))
}

export function emptyCompletionRetryForResult(documentVersion: number, position: { lineNumber: number; column: number }, itemCount: number, recoveryResult: boolean): EmptyCompletionRetry | null {
  if (itemCount > 0 || recoveryResult) return null
  return {
    documentVersion,
    lineNumber: position.lineNumber,
    column: position.column,
  }
}

function clearEmptyCompletionState(state: MonacoDocumentState): void {
  state.emptyCompletionRetry = null
  state.emptyCompletionRecoveryVersion = null
}

export function createMonacoLanguageSessionDependencies(bridge: MonacoLanguageBridge): LanguageSessionLifecycleDependencies {
  return bridge.createDependencies()
}

export interface MonacoLanguageTriggerCharacters {
  completion: readonly string[]
  signature: readonly string[]
  signatureRetrigger: readonly string[]
}

export function monacoLanguageTriggerCharacters(languageId: string): MonacoLanguageTriggerCharacters {
  switch (languageId) {
    case 'il':
      return {
        completion: ilSenseCompletionTriggerCharacters,
        signature: ['(', ','],
        signatureRetrigger: [','],
      }
    case 'fsharp':
      return {
        completion: ['.'],
        signature: ['(', ','],
        signatureRetrigger: [','],
      }
    case 'csharp':
    case 'visual-basic':
      return {
        completion: ['.', ':', '<'],
        signature: ['(', ',', '<'],
        signatureRetrigger: [',', ')'],
      }
    default:
      return {
        completion: ['.', ':', '<'],
        signature: ['(', ','],
        signatureRetrigger: [','],
      }
  }
}

export function monacoFoldingRanges(ranges: readonly CodeMirrorLspFoldingRange[] | null | undefined): monaco.languages.FoldingRange[] | null {
  if (!ranges) return null
  return ranges.map((range) => ({
    start: range.startLine + 1,
    end: range.endLine + 1,
    ...(range.kind ? { kind: monaco.languages.FoldingRangeKind.fromValue(range.kind) } : {}),
  }))
}

function completionItem(model: monaco.editor.ITextModel, position: monaco.Position, documentPath: string, item: CodeMirrorLspCompletionItem): MonacoCompletionItem {
  const word = model.getWordUntilPosition(position)
  const fallbackRange = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn)
  const range = item.textEdit ? toMonacoRange(item.textEdit.range) : fallbackRange
  const documentation = markdownValue(item.documentation)
  const filterText = monacoCompletionFilterText(model, position, word.startColumn, range, item)
  return {
    label: item.label,
    kind: completionKind(item.kind),
    ...monacoCompletionInsertion(item),
    range,
    ...(item.detail ? { detail: item.detail } : {}),
    ...(documentation ? { documentation } : {}),
    ...(item.sortText ? { sortText: item.sortText } : {}),
    ...(filterText ? { filterText } : {}),
    ...(item.additionalTextEdits ? { additionalTextEdits: item.additionalTextEdits.map(toMonacoTextEdit) } : {}),
    lspItem: item,
    documentPath,
  }
}

function monacoCompletionFilterText(model: monaco.editor.ITextModel, position: monaco.Position, wordStartColumn: number, range: monaco.Range, item: CodeMirrorLspCompletionItem): string | undefined {
  const rangeEndsAtPosition = range.endLineNumber === position.lineNumber && range.endColumn === position.column
  const rangeStartsBeforeCurrentWord = range.startLineNumber === position.lineNumber && range.startColumn < wordStartColumn

  if (!rangeEndsAtPosition || !rangeStartsBeforeCurrentWord) return item.filterText

  // Monaco filters against the complete text between the replacement start and
  // the caret. Keep that target stable while the user extends the postfix suffix:
  // `task.` + `await` stays filterable as `task.a`, `task.aw`, and so on.
  const receiverPrefix = model.getValueInRange(new monaco.Range(range.startLineNumber, range.startColumn, position.lineNumber, wordStartColumn))
  const candidateFilterText = item.filterText ?? item.label
  return candidateFilterText.startsWith(receiverPrefix) ? candidateFilterText : `${receiverPrefix}${candidateFilterText}`
}

export function monacoCompletionInsertion(item: CodeMirrorLspCompletionItem): Pick<monaco.languages.CompletionItem, 'insertText' | 'insertTextRules'> {
  const insertText = item.textEdit?.newText ?? item.insertText ?? item.label
  if (item.insertTextFormat !== 2) return { insertText: plainCompletionText(insertText) }
  return {
    insertText,
    insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
  }
}

function positionForCompletionRange(range: monaco.IRange | monaco.languages.CompletionItemRanges): monaco.Position {
  const value = 'insert' in range ? range.insert : range
  return new monaco.Position(value.endLineNumber, value.endColumn)
}

function completionKind(kind: number | undefined): monaco.languages.CompletionItemKind {
  const kinds = monaco.languages.CompletionItemKind
  switch (kind) {
    case 1:
      return kinds.Text
    case 2:
      return kinds.Method
    case 3:
      return kinds.Function
    case 4:
      return kinds.Constructor
    case 5:
      return kinds.Field
    case 6:
      return kinds.Variable
    case 7:
      return kinds.Class
    case 8:
      return kinds.Interface
    case 9:
      return kinds.Module
    case 10:
      return kinds.Property
    case 11:
      return kinds.Unit
    case 12:
      return kinds.Value
    case 13:
      return kinds.Enum
    case 14:
      return kinds.Keyword
    case 15:
      return kinds.Snippet
    case 16:
      return kinds.Color
    case 17:
      return kinds.File
    case 18:
      return kinds.Reference
    case 19:
      return kinds.Folder
    case 20:
      return kinds.EnumMember
    case 21:
      return kinds.Constant
    case 22:
      return kinds.Struct
    case 23:
      return kinds.Event
    case 24:
      return kinds.Operator
    case 25:
      return kinds.TypeParameter
    default:
      return kinds.Text
  }
}

function signatureHelp(help: CodeMirrorLspSignatureHelp): monaco.languages.SignatureHelp {
  return {
    signatures: help.signatures.map((signature) => {
      const documentation = markdownValue(signature.documentation)
      return {
        label: signature.label,
        parameters: signature.parameters.map((parameter) => {
          const parameterDocumentation = markdownValue(parameter.documentation)
          const label: string | [number, number] = typeof parameter.label === 'string' ? parameter.label : [parameter.label[0], parameter.label[1]]
          return {
            label,
            ...(parameterDocumentation ? { documentation: parameterDocumentation } : {}),
          }
        }),
        ...(documentation ? { documentation } : {}),
        ...(signature.activeParameter !== undefined ? { activeParameter: signature.activeParameter } : {}),
      }
    }),
    activeSignature: help.activeSignature,
    activeParameter: help.activeParameter,
  }
}

function markdownContents(value: unknown): monaco.IMarkdownString[] {
  if (Array.isArray(value)) return value.flatMap(markdownContents)
  if (typeof value === 'string') return value ? [safeMarkdown(value)] : []
  if (!isRecord(value) || typeof value.value !== 'string' || !value.value) return []
  const content = typeof value.language === 'string' ? `\`\`\`${value.language}\n${value.value}\n\`\`\`` : value.value
  return [safeMarkdown(content)]
}

function markdownValue(value: unknown): string | monaco.IMarkdownString | null {
  if (typeof value === 'string') return value
  if (isRecord(value) && typeof value.value === 'string') return safeMarkdown(value.value)
  return null
}

function safeMarkdown(value: string): monaco.IMarkdownString {
  return { value, isTrusted: false, supportHtml: false }
}

function plainCompletionText(value: string): string {
  return value
    .replace(/\$\{\d+:([^}]*)\}/g, '$1')
    .replace(/\$\{\d+\}/g, '')
    .replace(/\$\d+/g, '')
}

function toMonacoMarker(diagnostic: CodeMirrorLspDiagnostic): monaco.editor.IMarkerData {
  return {
    ...toMonacoRange(diagnostic.range),
    severity: markerSeverity(diagnostic.severity),
    message: diagnostic.code === undefined ? diagnostic.message : `[${String(diagnostic.code)}] ${diagnostic.message}`,
    ...(diagnostic.source ? { source: diagnostic.source } : {}),
    ...(diagnostic.code !== undefined ? { code: String(diagnostic.code) } : {}),
  }
}

function markerSeverity(severity: number | undefined): monaco.MarkerSeverity {
  switch (severity) {
    case 1:
      return monaco.MarkerSeverity.Error
    case 2:
      return monaco.MarkerSeverity.Warning
    case 4:
      return monaco.MarkerSeverity.Hint
    default:
      return monaco.MarkerSeverity.Info
  }
}

function toMonacoDocumentSymbol(symbol: CodeMirrorDocumentSymbol): monaco.languages.DocumentSymbol {
  return {
    name: symbol.name,
    detail: symbol.detail ?? '',
    kind: Math.max(0, symbol.kind - 1) as monaco.languages.SymbolKind,
    range: toMonacoRange(symbol.range),
    selectionRange: toMonacoRange(symbol.selectionRange),
    tags: [],
    children: symbol.children.map(toMonacoDocumentSymbol),
  }
}

function toMonacoRange(range: LspRange): monaco.Range {
  return new monaco.Range(range.start.line + 1, range.start.character + 1, range.end.line + 1, range.end.character + 1)
}

function toMonacoTextEdit(edit: CodeMirrorLspTextEdit): monaco.editor.ISingleEditOperation {
  return { range: toMonacoRange(edit.range), text: edit.newText }
}

export function encodeSemanticTokens(tokens: readonly CodeMirrorSemanticToken[]): Uint32Array {
  const ordered = [...tokens].sort((left, right) => left.line - right.line || left.character - right.character)
  const data: number[] = []
  let previousLine = 0
  let previousCharacter = 0
  for (const token of ordered) {
    const type = lspSemanticTokenTypes.indexOf(token.tokenType as (typeof lspSemanticTokenTypes)[number])
    if (type < 0) continue
    const deltaLine = token.line - previousLine
    const deltaCharacter = deltaLine === 0 ? token.character - previousCharacter : token.character
    if (deltaLine < 0 || deltaCharacter < 0 || token.length <= 0) continue
    const modifiers = token.tokenModifiers.reduce((bits, modifier) => {
      const index = lspSemanticTokenModifiers.indexOf(modifier as (typeof lspSemanticTokenModifiers)[number])
      return index < 0 ? bits : bits | (2 ** index)
    }, 0)
    data.push(deltaLine, deltaCharacter, token.length, type, modifiers)
    previousLine = token.line
    previousCharacter = token.character
  }
  return new Uint32Array(data)
}

export function monacoCodeActions(state: MonacoDocumentState, documents: ReadonlyMap<string, MonacoDocumentState>, markers: readonly monaco.editor.IMarkerData[]): monaco.languages.CodeAction[] {
  const unique = new Map<string, { action: CodeMirrorLspCodeAction; markers: Set<monaco.editor.IMarkerData> }>()
  for (const diagnostic of state.diagnostics) {
    const matchingMarkers = markers.filter((marker) => markerMatchesDiagnostic(marker, diagnostic))
    if (matchingMarkers.length === 0) continue
    for (const action of diagnostic.actions ?? []) {
      const key = JSON.stringify([action.title, action.documentEdits])
      const existing = unique.get(key)
      if (existing) {
        for (const marker of matchingMarkers) existing.markers.add(marker)
      } else {
        unique.set(key, { action, markers: new Set(matchingMarkers) })
      }
    }
  }
  return [...unique.values()].flatMap(({ action, markers: actionMarkers }) => {
    const edits: monaco.languages.IWorkspaceTextEdit[] = []
    for (const documentEdit of action.documentEdits) {
      const document = documents.get(documentEdit.documentPath)
      if (!document || document.model.getVersionId() !== documentEdit.documentVersion) return []
      for (const edit of documentEdit.edits) {
        edits.push({
          resource: document.model.uri,
          versionId: documentEdit.documentVersion,
          textEdit: { range: toMonacoRange(edit.range), text: edit.newText },
        })
      }
    }
    return [
      {
        title: action.title,
        kind: action.kind ?? 'quickfix',
        isPreferred: action.isPreferred ?? false,
        diagnostics: [...actionMarkers],
        edit: { edits },
      },
    ]
  })
}

function markerMatchesDiagnostic(marker: monaco.editor.IMarkerData, diagnostic: CodeMirrorLspDiagnostic): boolean {
  const range = diagnostic.range
  const expectedMessage = diagnostic.code === undefined ? diagnostic.message : `[${String(diagnostic.code)}] ${diagnostic.message}`
  return (
    marker.startLineNumber === range.start.line + 1 &&
    marker.startColumn === range.start.character + 1 &&
    marker.endLineNumber === range.end.line + 1 &&
    marker.endColumn === range.end.character + 1 &&
    marker.message === expectedMessage &&
    (diagnostic.code === undefined || marker.code === String(diagnostic.code)) &&
    (diagnostic.source === undefined || marker.source === diagnostic.source)
  )
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

const markerOwner = 'sharplabnext-lsp'
