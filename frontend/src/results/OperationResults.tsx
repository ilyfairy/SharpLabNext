import { Check, Copy } from 'lucide-react'
import { type CSSProperties, type KeyboardEvent as ReactKeyboardEvent, useEffect, useMemo, useRef, useState } from 'react'
import type { AstDocument, Diagnostic, ExplanationDocument, GeneratedSourceDocument, JitResult, OperationEvent, OperationResult, OutputChannel, OutputManifest, RunResult, UserExceptionInfo } from '../api/types'
import { defaultEditorFontSize, type EditorFontSize, type EditorKind } from '../editor/editorPreference'
import { AstTreeView } from './AstTreeView'
import { type AnsiSgrDocument, type AnsiSgrOutputChunk, type AnsiSgrStyle, parseAnsiSgrOutputChunks } from './ansiSgr'
import { createAstSourceMap } from './astSourceMapModel'
import { CodeDocumentView } from './CodeDocumentView'
import { createExecutionFlowSourceModel, type ExecutionFlowSourceModel, type ExecutionFlowSourceTarget } from './executionFlowModel'
import type { IlOutputLanguageSessionOptions } from './ilOutputLanguageSession'
import { createIlSourceLinks } from './ilSourceMapModel'
import { composeJitAssembly, type JitAssemblySection, type JitSourceFile, parseJitAssembly, remapJitLineRange } from './jitAssemblyModel'
import { parseRuntimePayloads, RuntimeFlowView, type RuntimeInspectionPayload, RuntimeInspectionView } from './RuntimeStructuredViews'
import { createSourceAssociation, type SourceAssociation } from './sourceAssociationModel'

export interface OperationContentView {
  contentRef: string
  mediaType: string
  text: string | null
  loading: boolean
  error: Error | null
}

export interface GeneratedSourceContentView extends GeneratedSourceDocument {
  text: string | null
  loading: boolean
  error: Error | null
}

interface OperationResultsProps {
  output: OutputManifest | undefined
  results: readonly OperationResult[]
  events: readonly OperationEvent[]
  content: OperationContentView | null
  generatedSourceContents?: readonly GeneratedSourceContentView[]
  activityResults?: readonly OperationResult[]
  activityEvents?: readonly OperationEvent[]
  pending: boolean
  resultGenerationKey?: string | null
  failure?: Error | null
  attentionKey?: string | null
  recoveryKey?: string | null
  executionFlow?: ExecutionFlowSourceModel
  sourceFiles?: readonly JitSourceFile[]
  codeFontSize?: EditorFontSize
  editorKind?: EditorKind
  activeSourceAssociationKey?: string | null
  activeSourceAssociationRevision?: number
  ilOutputLanguageSessionOptions?: IlOutputLanguageSessionOptions | null
  onNavigateToSource?: ((target: ExecutionFlowSourceTarget) => void) | undefined
  onSourceAssociationsChange?: ((associations: readonly SourceAssociation[]) => void) | undefined
  onSourceAssociationHover?: ((associationKey: string | null) => void) | undefined
  toolbarActions?: React.ReactNode
}

interface OutputSourceLink {
  startLine: number
  endLine: number
  heading: string
  body: string
  association: SourceAssociation
}

interface ResultTab {
  id: string
  label: string
  content: React.ReactNode
  copyText: string
}

export function decodeOutputChunk(data: string): string {
  try {
    const bytes = Uint8Array.from(atob(data), (character) => character.charCodeAt(0))
    return new TextDecoder().decode(bytes)
  } catch {
    return '[invalid base64 output chunk]'
  }
}

export function findTypedResult(events: readonly OperationEvent[]): OperationResult | null {
  for (let index = events.length - 1; index >= 0; index -= 1) {
    const payload = events[index]?.payload
    if (payload?.kind === 'typed-result') return payload.result
  }
  return null
}

function outputChunks(events: readonly OperationEvent[], channel: OutputChannel): string[] {
  return events.filter((event) => event.payload.kind === 'output-chunk' && event.payload.chunk.channel === channel).map((event) => (event.payload.kind === 'output-chunk' ? decodeOutputChunk(event.payload.chunk.data) : ''))
}

function outputChunksInOrder(events: readonly OperationEvent[]): AnsiSgrOutputChunk[] {
  return events.flatMap((event) => {
    if (event.payload.kind !== 'output-chunk') {
      return []
    }
    const channel = event.payload.chunk.channel
    if (channel !== 'stdout' && channel !== 'stderr') return []
    return [
      {
        channel,
        text: decodeOutputChunk(event.payload.chunk.data),
      },
    ]
  })
}

function outputText(events: readonly OperationEvent[], channel: OutputChannel): string {
  return outputChunks(events, channel).join('')
}

function diagnosticsFrom(results: readonly OperationResult[], events: readonly OperationEvent[]): Diagnostic[] {
  const seen = new Set<string>()
  const diagnostics: Diagnostic[] = []
  const candidates = [
    ...results.flatMap((result) => ('diagnostics' in result && Array.isArray(result.diagnostics) ? (result.diagnostics as Diagnostic[]) : [])),
    ...events.flatMap((event) => (event.payload.kind === 'diagnostic' ? [event.payload.diagnostic] : [])),
  ]
  for (const diagnostic of candidates) {
    const key = [diagnostic.source, diagnostic.code, diagnostic.message, diagnostic.filePath, diagnostic.range?.startLine, diagnostic.range?.startCharacter].join(':')
    if (seen.has(key)) continue
    seen.add(key)
    diagnostics.push(diagnostic)
  }
  return diagnostics
}

function formatUserException(exception: UserExceptionInfo): string {
  const lines: string[] = []
  let current: UserExceptionInfo | null | undefined = exception
  let depth = 0
  while (current) {
    const prefix = depth === 0 ? '' : `InnerException${depth}: `
    lines.push(`${prefix}${current.typeName}: ${current.message}`)
    if (current.stackTrace) lines.push(current.stackTrace)
    current = current.innerException
    depth += 1
  }
  return lines.join('\n')
}

function findUserException(results: readonly OperationResult[]): UserExceptionInfo | null {
  for (let index = results.length - 1; index >= 0; index -= 1) {
    const result = results[index]
    if (result?.resultType === 'run' && result.exception) return result.exception
  }
  return null
}

function diagnosticsCopyText(diagnostics: readonly Diagnostic[], failure: Error | null, userException: UserExceptionInfo | null): string {
  const lines = userException ? [formatUserException(userException)] : failure ? [`Operation failed: ${failure.message}`] : []
  for (const diagnostic of diagnostics) {
    const location = diagnostic.filePath ? `${diagnostic.filePath}${diagnostic.range ? `:${diagnostic.range.startLine + 1}:${diagnostic.range.startCharacter + 1}` : ''}` : null
    lines.push(`${[diagnostic.severity.toUpperCase(), diagnostic.code, location].filter(Boolean).join(' ')}: ${diagnostic.message}`)
  }
  return lines.join('\n')
}

function generatedSourceCopyText(document: GeneratedSourceContentView | null): string {
  if (!document) return ''
  if (document.error) return document.error.message
  return document.text ?? ''
}

function DiagnosticsView({ diagnostics, failure, userException }: { diagnostics: readonly Diagnostic[]; failure: Error | null; userException: UserExceptionInfo | null }) {
  if (diagnostics.length === 0 && !failure && !userException) {
    return <div className="result-tab-empty">No diagnostics.</div>
  }
  return (
    <div className="diagnostics-view">
      {userException ? (
        <section className="diagnostic-exception" role="alert" aria-label="Runtime exception">
          <strong>Exception</strong>
          <pre>{formatUserException(userException)}</pre>
        </section>
      ) : failure ? (
        <div className="diagnostic-failure" role="alert">
          <strong>Operation failed</strong>
          <span>{failure.message}</span>
        </div>
      ) : null}
      {diagnostics.length > 0 && (
        <ol className="diagnostic-list">
          {diagnostics.map((diagnostic) => (
            <li key={`${diagnostic.source}:${diagnostic.code}:${diagnostic.message}:${diagnostic.filePath}:${diagnostic.range?.startLine}:${diagnostic.range?.startCharacter}`} data-severity={diagnostic.severity}>
              <div>
                <strong>{diagnostic.code}</strong>
                <span>{diagnostic.severity}</span>
                {diagnostic.filePath && (
                  <code>
                    {diagnostic.filePath}
                    {diagnostic.range ? `:${diagnostic.range.startLine + 1}:${diagnostic.range.startCharacter + 1}` : ''}
                  </code>
                )}
              </div>
              <p>{diagnostic.message}</p>
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

function ansiStyle(style: AnsiSgrStyle): CSSProperties {
  if (style.inverse) {
    return {
      color: style.background ?? 'var(--ansi-terminal-background)',
      backgroundColor: style.foreground ?? 'var(--ansi-terminal-foreground)',
    }
  }
  return {
    color: style.foreground ?? undefined,
    backgroundColor: style.background ?? undefined,
  }
}

function selectOutputText(event: ReactKeyboardEvent<HTMLElement>) {
  if (event.key.toLowerCase() !== 'a' || (!event.ctrlKey && !event.metaKey) || event.altKey || event.shiftKey) {
    return
  }

  const selection = window.getSelection()
  if (selection) {
    const range = document.createRange()
    range.selectNodeContents(event.currentTarget)
    selection.removeAllRanges()
    selection.addRange(range)
  }
  event.preventDefault()
  event.stopPropagation()
}

function AnsiTextView({ document, empty, ariaLabel }: { document: AnsiSgrDocument; empty: string; ariaLabel: string }) {
  if (!document.text) return <div className="result-tab-empty">{empty}</div>
  let segmentOffset = 0
  const segments = document.segments.map((segment) => {
    const key = segmentOffset
    segmentOffset += segment.text.length
    return (
      <span className={['ansi-segment', segment.style.bold ? 'ansi-segment--bold' : '', segment.style.underline ? 'ansi-segment--underline' : ''].filter(Boolean).join(' ')} key={key} style={ansiStyle(segment.style)}>
        {segment.text}
      </span>
    )
  })
  return (
    <section
      className="result-document ansi-output"
      aria-label={ariaLabel}
      // biome-ignore lint/a11y/noNoninteractiveTabindex: The scrollable, styled output must receive focus so Mod+A stays within it.
      tabIndex={0}
      onKeyDown={selectOutputText}
    >
      {segments}
    </section>
  )
}

function ContentTextView({
  content,
  pending,
  resultGenerationKey,
  languageId,
  ariaLabel,
  fontSize,
  sourceLinks = [],
  onNavigate,
  activeSourceAssociationKey = null,
  activeSourceAssociationRevision = 0,
  ilOutputLanguageSessionOptions,
  editorKind,
  onAssociationHover,
}: {
  content: OperationContentView | null
  pending: boolean
  resultGenerationKey: string | null
  languageId: string
  ariaLabel: string
  fontSize: EditorFontSize
  sourceLinks?: readonly OutputSourceLink[]
  onNavigate?: ((target: ExecutionFlowSourceTarget) => void) | undefined
  activeSourceAssociationKey?: string | null
  activeSourceAssociationRevision?: number
  ilOutputLanguageSessionOptions?: IlOutputLanguageSessionOptions | null
  editorKind: EditorKind
  onAssociationHover?: ((associationKey: string | null) => void) | undefined
}) {
  if ((!content && pending) || content?.loading) {
    return <div className="result-tab-empty">Loading generated text...</div>
  }
  if (!content) {
    return <div className="result-tab-empty">No generated text was produced.</div>
  }
  if (content.error) {
    return <div className="result-tab-error">{content.error.message}</div>
  }
  return content.text ? (
    <CodeDocumentView
      text={content.text}
      languageId={languageId}
      ariaLabel={ariaLabel}
      fontSize={fontSize}
      generationKey={resultGenerationKey}
      lineAssociations={sourceLinks.map(({ startLine, endLine, association }) => ({
        startLine,
        endLine,
        association,
      }))}
      lineTooltips={sourceLinkTooltips(sourceLinks)}
      lineActions={
        onNavigate
          ? sourceLinks.map(({ startLine, endLine, association }) => ({
              startLine,
              endLine,
              ariaLabel: sourceAssociationAriaLabel(association),
              onActivate: () => onNavigate(association),
            }))
          : []
      }
      activeAssociationKey={activeSourceAssociationKey}
      activeAssociationRevision={activeSourceAssociationRevision}
      editorKind={editorKind}
      ilOutputLanguageSessionOptions={ilOutputLanguageSessionOptions ?? null}
      onAssociationHover={onAssociationHover}
    />
  ) : (
    <div className="result-tab-empty">The operation returned no text.</div>
  )
}

function GeneratedSourceView({
  documents,
  pending,
  resultGenerationKey,
  fontSize,
  selectedKey,
  onSelectedKeyChange,
  editorKind,
}: {
  documents: readonly GeneratedSourceContentView[]
  pending: boolean
  resultGenerationKey: string | null
  fontSize: EditorFontSize
  selectedKey: string | null
  onSelectedKeyChange: (key: string) => void
  editorKind: EditorKind
}) {
  const selected = documents.find((document) => generatedSourceDocumentKey(document) === selectedKey) ?? documents[0] ?? null
  if (!selected) {
    return pending ? <div className="result-tab-empty">Loading generated source...</div> : <div className="result-tab-empty">No generated source documents were produced.</div>
  }

  return (
    <div className="generated-source-view">
      <div className="generated-source-toolbar">
        <span>
          {documents.length} generated {documents.length === 1 ? 'document' : 'documents'}
        </span>
        <label className="generated-source-picker">
          <span>Document</span>
          <select aria-label="Generated source document" value={generatedSourceDocumentKey(selected)} onChange={(event) => onSelectedKeyChange(event.target.value)}>
            {documents.map((document) => (
              <option key={generatedSourceDocumentKey(document)} value={generatedSourceDocumentKey(document)}>
                {document.path}
              </option>
            ))}
          </select>
        </label>
      </div>
      {selected.loading ? (
        <div className="result-tab-empty">Loading {selected.path}...</div>
      ) : selected.error ? (
        <div className="result-tab-error">{selected.error.message}</div>
      ) : selected.text !== null ? (
        <CodeDocumentView text={selected.text} languageId={selected.languageId} ariaLabel={`Generated source ${selected.path}`} fontSize={fontSize} generationKey={resultGenerationKey} editorKind={editorKind} />
      ) : (
        <div className="result-tab-empty">{selected.path} returned no text.</div>
      )}
    </div>
  )
}

function generatedSourceDocumentKey(document: GeneratedSourceDocument): string {
  return `${document.contentRef}:${document.path}`
}

export function RunStatus({ result }: { result: RunResult | undefined }) {
  if (!result) return null
  const statusLabel =
    result.status === 'completed'
      ? null
      : {
          'user-exception': 'Exception',
          'non-zero-exit': 'Failed',
          timeout: 'Timed out',
          'out-of-memory': 'Out of memory',
          'process-crash': 'Crashed',
          cancelled: 'Cancelled',
          'output-limit-exceeded': 'Output limit',
        }[result.status]
  return (
    <div className="run-status" data-status={result.status} role="status" aria-label="Run status">
      {(statusLabel || result.outputTruncated || result.exception) && (
        <div className="run-status-message">
          {statusLabel && <strong>{statusLabel}</strong>}
          {result.outputTruncated && <strong>Output truncated</strong>}
          {result.exception && (
            <span title={`${result.exception.typeName}: ${result.exception.message}`}>
              {result.exception.typeName}: {result.exception.message}
            </span>
          )}
        </div>
      )}
      <div className="run-status-metrics">
        <span>Exit {result.exitCode ?? 'n/a'}</span>
        <span>{formatRunElapsed(result.elapsed)}</span>
      </div>
    </div>
  )
}

export function AstStatus({ document, nodeCount }: { document: AstDocument | undefined; nodeCount: number }) {
  if (!document) return null
  return (
    <div className="ast-status" role="status" aria-label="AST status">
      <span>Syntax tree</span>
      <span>{document.languageId}</span>
      <span>{nodeCount} nodes</span>
      {document.truncated && <span className="ast-status-warning">Truncated</span>}
    </div>
  )
}

export function JitStatus({ result }: { result: JitResult | undefined }) {
  if (!result) return null
  const statusLabel =
    result.status === 'completed'
      ? 'JIT ready'
      : {
          'no-matching-methods': 'No JIT methods',
          'inspection-failed': 'JIT failed',
          timeout: 'JIT timed out',
          'out-of-memory': 'JIT out of memory',
          'process-crash': 'JIT crashed',
          cancelled: 'JIT cancelled',
          'output-limit-exceeded': 'JIT output limit',
        }[result.status]
  return (
    <div className="run-status" data-status={result.status} role="status" aria-label="JIT status">
      <div className="run-status-message">
        <strong>{statusLabel}</strong>
      </div>
      <div className="run-status-metrics">
        <span>{formatRunElapsed(result.elapsed)}</span>
      </div>
    </div>
  )
}

function formatRunElapsed(elapsed: string): string {
  const match = /^(\d+):(\d{2}):(\d{2}(?:\.\d+)?)$/.exec(elapsed)
  if (!match) return elapsed
  const totalSeconds = Number(match[1]) * 3600 + Number(match[2]) * 60 + Number(match[3])
  if (totalSeconds < 1) return `${Math.max(1, Math.round(totalSeconds * 1000))} ms`
  if (totalSeconds < 10) return `${totalSeconds.toFixed(2)} s`
  return `${totalSeconds.toFixed(1)} s`
}

export function createJitOutputSourceLinks(sections: readonly JitAssemblySection[], sourceFiles: readonly JitSourceFile[]): OutputSourceLink[] {
  const links: OutputSourceLink[] = []
  let sectionStartLine = 1
  for (const section of sections) {
    const linkedRanges = section.summary?.linkedRanges ?? []
    let mappedRangeCount = 0
    for (const linkedRange of linkedRanges) {
      const rawEndLine = inclusiveEndLine(linkedRange.outputRange)
      const mapped = remapJitLineRange(section, linkedRange.outputRange.startLine, rawEndLine)
      const source = createIlSourceLinks([linkedRange], sourceFiles)[0]
      if (!mapped || !source) continue
      const label = linkedRange.precision === 'sequence-point' ? `JIT source: ${source.heading}` : `Approximate JIT source: ${source.heading}`
      links.push({
        startLine: sectionStartLine + mapped.startLine,
        endLine: sectionStartLine + mapped.endLine,
        heading: label,
        body: source.body,
        association: createSourceAssociation(source.target, label),
      })
      mappedRangeCount += 1
    }

    if (mappedRangeCount === 0 && section.source) {
      const source = section.source
      const sourceLine = sourceFiles
        .find((file) => file.path === source.documentPath)
        ?.text.replace(/\r\n?/g, '\n')
        .split('\n')[source.lineNumber - 1]
      const sourceColumn = Math.max(0, sourceLine?.indexOf(source.code) ?? 0) + 1
      const target: ExecutionFlowSourceTarget = {
        documentPath: source.documentPath,
        range: {
          startLine: source.lineNumber,
          startColumn: sourceColumn,
          endLine: source.lineNumber,
          endColumn: sourceColumn + Math.max(1, source.code.length),
        },
      }
      const label = `Approximate JIT source: ${source.documentPath}:${source.lineNumber}`
      links.push({
        startLine: sectionStartLine,
        endLine: sectionStartLine + section.text.split('\n').length - 1,
        heading: label,
        body: source.code,
        association: createSourceAssociation(target, label),
      })
    }
    sectionStartLine += section.text.split('\n').length + 1
  }
  return links
}

function inclusiveEndLine(range: { startLine: number; endLine: number; endCharacter: number }): number {
  return range.endLine > range.startLine && range.endCharacter === 0 ? range.endLine - 1 : range.endLine
}

function createIlOutputSourceLinks(linkedRanges: Parameters<typeof createIlSourceLinks>[0], sourceFiles: readonly JitSourceFile[], text: string | null | undefined): OutputSourceLink[] {
  return createIlSourceLinks(linkedRanges, sourceFiles, text ?? undefined).map((link) => ({
    startLine: link.startLine,
    endLine: link.endLine,
    heading: `Source: ${link.heading}`,
    body: link.body,
    association: createSourceAssociation(link.target, `IL source: ${link.heading}`),
  }))
}

function uniqueSourceAssociations(links: readonly OutputSourceLink[]): SourceAssociation[] {
  return [...new Map(links.map((link) => [link.association.key, link.association])).values()]
}

function sourceAssociationAriaLabel(association: SourceAssociation): string {
  return `Open ${association.documentPath}:${association.range.startLine}`
}

function sourceLinkTooltips(sourceLinks: readonly OutputSourceLink[]) {
  return sourceLinks.map(({ startLine, endLine, heading, body }) => ({
    startLine,
    endLine,
    heading,
    body,
  }))
}

function JitAssemblyView({
  text,
  pending,
  resultGenerationKey,
  error,
  fontSize,
  sourceLinks,
  onNavigate,
  activeSourceAssociationKey = null,
  activeSourceAssociationRevision = 0,
  editorKind,
  onAssociationHover,
}: {
  text: string
  pending: boolean
  resultGenerationKey: string | null
  error: Error | null
  fontSize: EditorFontSize
  sourceLinks: readonly OutputSourceLink[]
  onNavigate?: ((target: ExecutionFlowSourceTarget) => void) | undefined
  activeSourceAssociationKey?: string | null
  activeSourceAssociationRevision?: number
  editorKind: EditorKind
  onAssociationHover?: ((associationKey: string | null) => void) | undefined
}) {
  return (
    <div className="jit-view">
      {pending && !text ? (
        <div className="result-tab-empty">Receiving JIT assembly...</div>
      ) : error && !text ? (
        <div className="result-tab-error">{error.message}</div>
      ) : text ? (
        <CodeDocumentView
          text={text}
          languageId="asm"
          ariaLabel="JIT assembly"
          fontSize={fontSize}
          generationKey={resultGenerationKey}
          lineAssociations={sourceLinks.map(({ startLine, endLine, association }) => ({
            startLine,
            endLine,
            association,
          }))}
          lineTooltips={sourceLinkTooltips(sourceLinks)}
          lineActions={
            onNavigate
              ? sourceLinks.map(({ startLine, endLine, association }) => ({
                  startLine,
                  endLine,
                  ariaLabel: sourceAssociationAriaLabel(association),
                  onActivate: () => onNavigate(association),
                }))
              : []
          }
          activeAssociationKey={activeSourceAssociationKey}
          activeAssociationRevision={activeSourceAssociationRevision}
          editorKind={editorKind}
          onAssociationHover={onAssociationHover}
        />
      ) : (
        <div className="result-tab-empty">No JIT assembly was produced.</div>
      )}
    </div>
  )
}

function ExplanationView({ document }: { document: ExplanationDocument }) {
  return (
    <div className="explanation-view">
      {document.truncated && <div className="explanation-warning">Explanation was truncated.</div>}
      {document.files.map((file) => (
        <section key={file.path} className="explanation-file">
          <header>
            <strong>{file.path}</strong>
            <span>{file.nodes.length} syntax nodes</span>
          </header>
          <ol>
            {file.nodes.map((node) => (
              <li
                key={`${node.kind}:${node.range.startLine}:${node.range.startCharacter}:${node.range.endLine}:${node.range.endCharacter}:${node.depth}:${node.title}`}
                style={{
                  paddingLeft: `${10 + Math.min(node.depth, 12) * 12}px`,
                }}
              >
                <div>
                  <strong>{node.title}</strong>
                  <code>
                    {node.range.startLine + 1}:{node.range.startCharacter + 1}-{node.range.endLine + 1}:{node.range.endCharacter + 1}
                  </code>
                </div>
                <p>{node.description}</p>
              </li>
            ))}
          </ol>
        </section>
      ))}
    </div>
  )
}

function preferredTab(outputId: string | undefined, tabs: readonly ResultTab[]): string {
  const preferred =
    outputId === 'ast'
      ? 'ast'
      : outputId === 'il' || outputId === 'run-il'
        ? 'il'
        : outputId === 'generated-il'
          ? 'generated-il'
          : outputId === 'decompiled-csharp'
            ? 'decompiled-csharp'
            : outputId === 'javascript'
              ? 'javascript'
              : outputId === 'il-verify'
                ? 'verification'
                : outputId === 'execution-flow'
                  ? 'flow'
                  : outputId === 'run'
                    ? 'output'
                    : outputId === 'jit-asm'
                      ? 'jit'
                      : outputId === 'generated-source'
                        ? 'generated-source'
                        : outputId === 'explain'
                          ? 'explain'
                          : 'diagnostics'
  return tabs.some((tab) => tab.id === preferred) ? preferred : (tabs[0]?.id ?? 'diagnostics')
}

export function OperationResults({
  output,
  results,
  events,
  content,
  generatedSourceContents = [],
  activityResults = results,
  activityEvents = events,
  pending,
  resultGenerationKey = null,
  failure = null,
  attentionKey = null,
  recoveryKey = null,
  executionFlow,
  sourceFiles = [],
  codeFontSize = defaultEditorFontSize,
  editorKind = 'codemirror',
  activeSourceAssociationKey = null,
  activeSourceAssociationRevision = 0,
  ilOutputLanguageSessionOptions = null,
  onNavigateToSource,
  onSourceAssociationsChange,
  onSourceAssociationHover,
  toolbarActions,
}: OperationResultsProps) {
  const diagnostics = useMemo(() => diagnosticsFrom([...results, ...activityResults], activityEvents), [activityEvents, activityResults, results])
  const ast = results.find((result) => result.resultType === 'ast')
  const astSourceMap = useMemo(() => (ast?.resultType === 'ast' ? createAstSourceMap(ast.document) : null), [ast])
  const generatedSource = results.find((result) => result.resultType === 'generated-source')
  const explain = results.find((result) => result.resultType === 'explain')
  const verification = results.find((result) => result.resultType === 'artifact-verification')
  const artifactRender = [...results].reverse().find((result) => result.resultType === 'artifact-render')
  const run = results.find((result) => result.resultType === 'run')
  const jit = results.find((result) => result.resultType === 'jit')
  const outputDocument = useMemo(() => parseAnsiSgrOutputChunks(outputChunksInOrder(events)), [events])
  const userException = useMemo(() => findUserException([...results, ...activityResults]), [activityResults, results])
  const inspection = parseRuntimePayloads<RuntimeInspectionPayload>(events, 'inspection')
  const fallbackExecutionFlow = useMemo(() => createExecutionFlowSourceModel(events, []), [events])
  const flow = executionFlow ?? fallbackExecutionFlow
  const jitStream = outputText(events, 'jit')
  const preferredGeneratedSourceKey = generatedSourceContents[0] ? generatedSourceDocumentKey(generatedSourceContents[0]) : null
  const [selectedGeneratedSourceKey, setSelectedGeneratedSourceKey] = useState<string | null>(preferredGeneratedSourceKey)
  useEffect(() => {
    if (!generatedSourceContents.some((document) => generatedSourceDocumentKey(document) === selectedGeneratedSourceKey)) {
      setSelectedGeneratedSourceKey(preferredGeneratedSourceKey)
    }
  }, [generatedSourceContents, preferredGeneratedSourceKey, selectedGeneratedSourceKey])
  const selectedGeneratedSource = generatedSourceContents.find((document) => generatedSourceDocumentKey(document) === selectedGeneratedSourceKey) ?? generatedSourceContents[0] ?? null
  const jitText = content?.text ?? jitStream
  const jitResult = jit?.resultType === 'jit' ? jit : undefined
  const jitSections = useMemo(() => parseJitAssembly(jitText, jitResult?.methods, sourceFiles), [jitResult?.methods, jitText, sourceFiles])
  const visibleJitText = jitSections.length > 0 ? composeJitAssembly(jitSections) : jitText
  const jitSourceLinks = useMemo(() => createJitOutputSourceLinks(jitSections, sourceFiles), [jitSections, sourceFiles])
  const ilSourceLinks = useMemo(() => createIlOutputSourceLinks(artifactRender?.resultType === 'artifact-render' ? artifactRender.linkedRanges : [], sourceFiles, content?.text), [artifactRender, content?.text, sourceFiles])
  const outputSourceLinks = useMemo(() => (output?.id === 'jit-asm' ? jitSourceLinks : output?.id === 'il' || output?.id === 'run-il' || output?.id === 'generated-il' ? ilSourceLinks : []), [ilSourceLinks, jitSourceLinks, output?.id])
  const outputSourceAssociations = useMemo(() => (output?.id === 'ast' ? (astSourceMap?.associations ?? []) : uniqueSourceAssociations(outputSourceLinks)), [astSourceMap?.associations, output?.id, outputSourceLinks])
  useEffect(() => {
    onSourceAssociationsChange?.(outputSourceAssociations)
  }, [onSourceAssociationsChange, outputSourceAssociations])

  const tabs = useMemo(() => {
    const items: ResultTab[] = [
      {
        id: 'diagnostics',
        label: `Diagnostics${diagnostics.length + (userException ? 1 : 0) > 0 ? ` (${diagnostics.length + (userException ? 1 : 0)})` : ''}`,
        content: <DiagnosticsView diagnostics={diagnostics} failure={failure} userException={userException} />,
        copyText: diagnosticsCopyText(diagnostics, failure, userException),
      },
    ]
    if (output?.id === 'ast' || ast?.resultType === 'ast') {
      items.push({
        id: 'ast',
        label: 'AST',
        content:
          ast?.resultType === 'ast' ? (
            <AstTreeView
              document={ast.document}
              sourceMap={astSourceMap ?? undefined}
              activeSourceAssociationKey={activeSourceAssociationKey}
              activeSourceAssociationRevision={activeSourceAssociationRevision}
              onNavigateToSource={onNavigateToSource}
            />
          ) : (
            <div className="result-tab-empty">No syntax tree was produced.</div>
          ),
        copyText: ast?.resultType === 'ast' ? JSON.stringify(ast.document, null, 2) : '',
      })
    }
    if (output?.id === 'generated-source' || generatedSource?.resultType === 'generated-source') {
      items.push({
        id: 'generated-source',
        label: 'Generated Source',
        content: (
          <GeneratedSourceView
            documents={generatedSourceContents}
            pending={pending}
            resultGenerationKey={resultGenerationKey}
            fontSize={codeFontSize}
            selectedKey={selectedGeneratedSourceKey}
            onSelectedKeyChange={setSelectedGeneratedSourceKey}
            editorKind={editorKind}
          />
        ),
        copyText: generatedSourceCopyText(selectedGeneratedSource),
      })
    }
    if (output?.id === 'explain' || explain?.resultType === 'explain') {
      items.push({
        id: 'explain',
        label: 'Explain',
        content: explain?.resultType === 'explain' ? <ExplanationView document={explain.document} /> : <div className="result-tab-empty">No explanation was produced.</div>,
        copyText: explain?.resultType === 'explain' ? JSON.stringify(explain.document, null, 2) : '',
      })
    }
    if (output?.id === 'il' || output?.id === 'run-il') {
      items.push({
        id: 'il',
        label: 'IL',
        content: (
          <ContentTextView
            content={content}
            pending={pending}
            resultGenerationKey={resultGenerationKey}
            languageId="il"
            ariaLabel="Intermediate language"
            fontSize={codeFontSize}
            sourceLinks={ilSourceLinks}
            onNavigate={onNavigateToSource}
            activeSourceAssociationKey={activeSourceAssociationKey}
            activeSourceAssociationRevision={activeSourceAssociationRevision}
            editorKind={editorKind}
            ilOutputLanguageSessionOptions={ilOutputLanguageSessionOptions}
            onAssociationHover={onSourceAssociationHover}
          />
        ),
        copyText: content?.error?.message ?? content?.text ?? '',
      })
    }
    if (output?.id === 'generated-il') {
      items.push({
        id: 'generated-il',
        label: 'Generated IL',
        content: (
          <ContentTextView
            content={content}
            pending={pending}
            resultGenerationKey={resultGenerationKey}
            languageId="il"
            ariaLabel="Generated intermediate language"
            fontSize={codeFontSize}
            sourceLinks={ilSourceLinks}
            onNavigate={onNavigateToSource}
            activeSourceAssociationKey={activeSourceAssociationKey}
            activeSourceAssociationRevision={activeSourceAssociationRevision}
            editorKind={editorKind}
            ilOutputLanguageSessionOptions={ilOutputLanguageSessionOptions}
            onAssociationHover={onSourceAssociationHover}
          />
        ),
        copyText: content?.error?.message ?? content?.text ?? '',
      })
    }
    if (output?.id === 'decompiled-csharp') {
      items.push({
        id: 'decompiled-csharp',
        label: 'Decompiled C#',
        content: <ContentTextView content={content} pending={pending} resultGenerationKey={resultGenerationKey} languageId="csharp" ariaLabel="Decompiled C sharp" fontSize={codeFontSize} editorKind={editorKind} />,
        copyText: content?.error?.message ?? content?.text ?? '',
      })
    }
    if (output?.renderer === 'javascript') {
      items.push({
        id: 'javascript',
        label: output.displayName,
        content: <ContentTextView content={content} pending={pending} resultGenerationKey={resultGenerationKey} languageId="javascript" ariaLabel="JavaScript output" fontSize={codeFontSize} editorKind={editorKind} />,
        copyText: content?.error?.message ?? content?.text ?? '',
      })
    }
    if (output?.id === 'il-verify' || verification?.resultType === 'artifact-verification') {
      items.push({
        id: 'verification',
        label: `Verification${verification ? ` (${verification.findings.length})` : ''}`,
        content:
          verification?.resultType === 'artifact-verification' ? (
            <div className="verification-view">
              <div className="verification-summary">
                <strong>{verification.outcome}</strong>
                <span>
                  {verification.verifierId} {verification.verifierVersion}
                </span>
              </div>
              {verification.findings.length === 0 ? (
                <div className="result-tab-empty">No verification findings.</div>
              ) : (
                <ol className="finding-list">
                  {verification.findings.map((finding) => (
                    <li key={JSON.stringify(finding)}>
                      <div>
                        <strong>{finding.code}</strong>
                        {finding.metadataToken != null && (
                          <code>
                            0x
                            {finding.metadataToken.toString(16).padStart(8, '0')}
                          </code>
                        )}
                      </div>
                      <p>{finding.message}</p>
                      {(finding.typeName || finding.methodName) && <span>{[finding.typeName, finding.methodName].filter(Boolean).join('.')}</span>}
                    </li>
                  ))}
                </ol>
              )}
            </div>
          ) : (
            <div className="result-tab-empty">No verification result was produced.</div>
          ),
        copyText: verification?.resultType === 'artifact-verification' ? JSON.stringify(verification, null, 2) : '',
      })
    }
    if (output?.id === 'run' || output?.id === 'execution-flow' || run) {
      items.push({
        id: 'output',
        label: 'Output',
        content: (
          <div className="terminal-view">
            <AnsiTextView document={outputDocument} empty="No output." ariaLabel="Program output" />
          </div>
        ),
        copyText: outputDocument.copyText,
      })
    }
    if (inspection.length > 0) {
      items.push({
        id: 'inspection',
        label: 'Inspection',
        content: <RuntimeInspectionView payloads={inspection} />,
        copyText: JSON.stringify(inspection, null, 2),
      })
    }
    if (flow.timeline.length > 0 || output?.id === 'execution-flow') {
      items.push({
        id: 'flow',
        label: 'Flow',
        content: <RuntimeFlowView model={flow} onNavigate={onNavigateToSource} />,
        copyText: JSON.stringify(flow, null, 2),
      })
    }
    if (output?.id === 'jit-asm' || jit) {
      items.push({
        id: 'jit',
        label: 'JIT',
        content: (
          <JitAssemblyView
            text={visibleJitText}
            pending={pending || content?.loading === true}
            resultGenerationKey={resultGenerationKey}
            error={content?.error ?? null}
            fontSize={codeFontSize}
            sourceLinks={jitSourceLinks}
            onNavigate={onNavigateToSource}
            activeSourceAssociationKey={activeSourceAssociationKey}
            activeSourceAssociationRevision={activeSourceAssociationRevision}
            editorKind={editorKind}
            onAssociationHover={onSourceAssociationHover}
          />
        ),
        copyText: content?.error?.message ?? visibleJitText,
      })
    }
    return items
  }, [
    activeSourceAssociationKey,
    activeSourceAssociationRevision,
    ast,
    astSourceMap,
    content,
    codeFontSize,
    diagnostics,
    editorKind,
    explain,
    failure,
    flow,
    generatedSource,
    generatedSourceContents,
    inspection,
    ilOutputLanguageSessionOptions,
    ilSourceLinks,
    jit,
    output?.displayName,
    output?.id,
    output?.renderer,
    onNavigateToSource,
    onSourceAssociationHover,
    pending,
    resultGenerationKey,
    run,
    selectedGeneratedSource,
    selectedGeneratedSourceKey,
    outputDocument,
    userException,
    verification,
    visibleJitText,
    jitSourceLinks,
  ])

  const initialTab = preferredTab(output?.id, tabs)
  const [activeTab, setActiveTab] = useState(initialTab)
  const [copied, setCopied] = useState(false)
  useEffect(() => setActiveTab(initialTab), [initialTab])
  // biome-ignore lint/correctness/useExhaustiveDependencies: the revision intentionally repeats tab activation for the same association key.
  useEffect(() => {
    if (activeSourceAssociationKey && outputSourceAssociations.some((association) => association.key === activeSourceAssociationKey)) {
      setActiveTab(initialTab)
    }
  }, [activeSourceAssociationKey, activeSourceAssociationRevision, initialTab, outputSourceAssociations])
  const handledAttentionKey = useRef<string | null>(null)
  const autoSelectedDiagnosticsKey = useRef<string | null>(null)
  const requiresAttention = failure !== null || userException !== null || diagnostics.some((diagnostic) => diagnostic.severity === 'error')
  useEffect(() => {
    if (!requiresAttention || !attentionKey || handledAttentionKey.current === attentionKey) return
    handledAttentionKey.current = attentionKey
    autoSelectedDiagnosticsKey.current = attentionKey
    setActiveTab('diagnostics')
  }, [attentionKey, requiresAttention])
  useEffect(() => {
    if (pending || requiresAttention || !recoveryKey || autoSelectedDiagnosticsKey.current === null) {
      return
    }
    autoSelectedDiagnosticsKey.current = null
    setActiveTab(initialTab)
  }, [initialTab, pending, recoveryKey, requiresAttention])
  const selected = tabs.find((tab) => tab.id === activeTab) ?? tabs[0]

  const selectTab = (tabId: string) => {
    autoSelectedDiagnosticsKey.current = null
    setCopied(false)
    setActiveTab(tabId)
  }

  const copySelectedOutput = async () => {
    if (!selected || !navigator.clipboard) return
    await navigator.clipboard.writeText(selected.copyText)
    setCopied(true)
    window.setTimeout(() => setCopied(false), 1_500)
  }

  return (
    <div className="result-tabs-shell">
      <div className="result-tabs-toolbar">
        <div className="result-tabs" role="tablist" aria-label="Result views">
          {tabs.map((tab) => (
            <button key={tab.id} data-result-tab={tab.id} type="button" role="tab" title={tab.label} aria-selected={selected?.id === tab.id} onClick={() => selectTab(tab.id)}>
              {tab.label}
            </button>
          ))}
        </div>
        <div className="result-actions" role="toolbar" aria-label="Result controls">
          {toolbarActions}
          <button className="icon-button" type="button" title="Copy output" aria-label="Copy output" onClick={() => void copySelectedOutput()}>
            {copied ? <Check aria-hidden="true" size={15} /> : <Copy aria-hidden="true" size={15} />}
          </button>
        </div>
      </div>
      <div className="result-tab-panel" role="tabpanel">
        {selected?.content}
      </div>
    </div>
  )
}
