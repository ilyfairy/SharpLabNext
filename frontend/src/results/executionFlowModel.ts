import type { OperationEvent } from '../api/types'
import { parseRuntimeGraphPayload } from './runtimePayloadWire'

export interface RuntimeFlowRange {
  startLine: number
  startColumn: number
  endLine: number
  endColumn: number
}

export interface RuntimeFlowPayload {
  eventKind: string
  documentPath: string | null
  range: RuntimeFlowRange | null
  managedThreadId: number
  taskId: number | null
  name: string | null
  value: unknown | null
  truncated: boolean
}

export interface ExecutionFlowSourceTarget {
  documentPath: string
  range: RuntimeFlowRange
}

export interface ExecutionFlowTimelineEntry {
  sequence: number
  payload: RuntimeFlowPayload | null
  error: string | null
  sourceError: string | null
  locationLabel: string | null
  target: ExecutionFlowSourceTarget | null
}

export interface ExecutionFlowSourceHit extends ExecutionFlowSourceTarget {
  key: string
  eventKind: string
  count: number
}

export interface ExecutionFlowSourceModel {
  timeline: ExecutionFlowTimelineEntry[]
  hits: ExecutionFlowSourceHit[]
}

export interface ExecutionFlowNavigationRequest extends ExecutionFlowSourceTarget {
  revision: number
}

export interface ExecutionFlowRevisionIdentity {
  outputId: string
  workspaceRevision: number
  selectionRevision: number
}

interface WorkspaceFile {
  path: string
  text: string
}

interface ParsedRange {
  range: RuntimeFlowRange | null
  error: string | null
}

interface ParsedFlowPayload {
  payload: RuntimeFlowPayload | null
  rangeError: string | null
  error: string | null
}

const invalidPayloadMessage = 'The runtime returned an invalid execution-flow payload.'

export function currentExecutionFlowSourceModel(model: ExecutionFlowSourceModel, result: ExecutionFlowRevisionIdentity | null, current: ExecutionFlowRevisionIdentity): ExecutionFlowSourceModel | null {
  return result?.outputId === 'execution-flow' && current.outputId === 'execution-flow' && result.workspaceRevision === current.workspaceRevision && result.selectionRevision === current.selectionRevision ? model : null
}

export function createExecutionFlowSourceModel(events: readonly OperationEvent[], files: readonly WorkspaceFile[]): ExecutionFlowSourceModel {
  const filesByPath = new Map(files.map((file) => [file.path, file]))
  const timeline: ExecutionFlowTimelineEntry[] = []
  const hits = new Map<string, ExecutionFlowSourceHit>()

  for (const event of events) {
    if (event.payload.kind !== 'output-chunk' || event.payload.chunk.channel !== 'flow') continue
    const parsed = parseFlowChunk(event.payload.chunk.data)
    if (!parsed.payload) {
      timeline.push({
        sequence: event.sequence,
        payload: null,
        error: parsed.error ?? invalidPayloadMessage,
        sourceError: null,
        locationLabel: null,
        target: null,
      })
      continue
    }

    const payload = parsed.payload
    const targetResult = validateSourceTarget(payload, filesByPath)
    const sourceError = parsed.rangeError ?? targetResult.error
    const target = sourceError ? null : targetResult.target
    const locationLabel = sourceLocation(payload, target?.documentPath)
    timeline.push({
      sequence: event.sequence,
      payload,
      error: null,
      sourceError,
      locationLabel,
      target,
    })

    if (!target) continue
    const key = JSON.stringify([target.documentPath, target.range.startLine, target.range.startColumn, target.range.endLine, target.range.endColumn, payload.eventKind])
    const existing = hits.get(key)
    if (existing) {
      existing.count += 1
    } else {
      hits.set(key, { ...target, key, eventKind: payload.eventKind, count: 1 })
    }
  }

  return { timeline, hits: [...hits.values()] }
}

export function toEditorRange(range: RuntimeFlowRange) {
  return {
    startLineNumber: range.startLine,
    startColumn: range.startColumn,
    endLineNumber: range.endLine,
    endColumn: range.endColumn,
  }
}

export function validateSourceRange(text: string, range: RuntimeFlowRange): string | null {
  if (![range.startLine, range.startColumn, range.endLine, range.endColumn].every(isPositiveInteger)) {
    return 'Execution-flow ranges must use positive 1-based coordinates.'
  }
  const lines = text.split(/\r\n|\r|\n/)
  if (range.startLine > lines.length || range.endLine > lines.length) {
    return 'The execution-flow range is outside the source document.'
  }
  if (range.endLine < range.startLine || (range.endLine === range.startLine && range.endColumn < range.startColumn)) {
    return 'The execution-flow range ends before it starts.'
  }

  const startText = lines[range.startLine - 1]
  const endText = lines[range.endLine - 1]
  if (startText === undefined || endText === undefined || range.startColumn > startText.length + 1 || range.endColumn > endText.length + 1) {
    return 'The execution-flow range is outside the source document.'
  }
  return null
}

function validateSourceTarget(payload: RuntimeFlowPayload, filesByPath: ReadonlyMap<string, WorkspaceFile>): { target: ExecutionFlowSourceTarget | null; error: string | null } {
  if (!payload.documentPath || !payload.range) return { target: null, error: null }
  const documentPath = resolveWorkspacePath(payload.documentPath, filesByPath)
  if (!documentPath) {
    return {
      target: null,
      error: `The execution-flow source path '${payload.documentPath}' is not in the workspace.`,
    }
  }
  const file = filesByPath.get(documentPath)
  if (!file) throw new Error('Resolved execution-flow workspace path is missing.')
  const error = validateSourceRange(file.text, payload.range)
  return error ? { target: null, error } : { target: { documentPath, range: payload.range }, error: null }
}

function resolveWorkspacePath(documentPath: string, filesByPath: ReadonlyMap<string, WorkspaceFile>): string | null {
  if (filesByPath.has(documentPath)) return documentPath

  const normalizedDocumentPath = normalizePath(documentPath)
  const matches: string[] = []
  for (const workspacePath of filesByPath.keys()) {
    const normalizedWorkspacePath = normalizePath(workspacePath)
    if (normalizedDocumentPath === normalizedWorkspacePath || normalizedDocumentPath.endsWith(`/${normalizedWorkspacePath}`)) {
      matches.push(workspacePath)
    }
  }
  return matches.length === 1 ? (matches[0] ?? null) : null
}

function normalizePath(path: string): string {
  return path
    .replaceAll('\\', '/')
    .replace(/^file:\/\/+/, '/')
    .replace(/^\.\//, '')
}

function parseFlowChunk(data: string): ParsedFlowPayload {
  try {
    const bytes = Uint8Array.from(atob(data), (character) => character.charCodeAt(0))
    return parseFlowPayload(JSON.parse(new TextDecoder().decode(bytes)))
  } catch {
    return { payload: null, rangeError: null, error: invalidPayloadMessage }
  }
}

function parseFlowPayload(value: unknown): ParsedFlowPayload {
  if (!isRecord(value) || typeof value.EventKind !== 'string' || value.EventKind.length === 0) {
    return { payload: null, rangeError: null, error: invalidPayloadMessage }
  }
  const parsedRange = parseRange(value.Range)
  const documentPath = typeof value.DocumentPath === 'string' ? value.DocumentPath : null
  const managedThreadId = isNonNegativeInteger(value.ManagedThreadId) ? value.ManagedThreadId : 0
  const taskId = isNonNegativeInteger(value.TaskId) ? value.TaskId : null
  const name = typeof value.Name === 'string' ? value.Name : null
  const graph = value.Value == null ? null : parseRuntimeGraphPayload(value.Value)
  if (value.Value != null && !graph) {
    return { payload: null, rangeError: null, error: invalidPayloadMessage }
  }
  return {
    payload: {
      eventKind: value.EventKind,
      documentPath,
      range: parsedRange.range,
      managedThreadId,
      taskId,
      name,
      value: graph,
      truncated: value.Truncated === true,
    },
    rangeError: parsedRange.error,
    error: null,
  }
}

function parseRange(value: unknown): ParsedRange {
  if (value == null) return { range: null, error: null }
  if (!isRecord(value)) return { range: null, error: invalidPayloadMessage }
  const coordinates = [value.StartLine, value.StartColumn, value.EndLine, value.EndColumn]
  if (!coordinates.every(isNonNegativeInteger)) {
    return {
      range: null,
      error: 'Execution-flow runtime ranges must use non-negative coordinates.',
    }
  }
  const runtimeRange = {
    startLine: value.StartLine as number,
    startColumn: value.StartColumn as number,
    endLine: value.EndLine as number,
    endColumn: value.EndColumn as number,
  }
  if (runtimeRange.endLine < runtimeRange.startLine || (runtimeRange.endLine === runtimeRange.startLine && runtimeRange.endColumn < runtimeRange.startColumn)) {
    return {
      range: null,
      error: 'The execution-flow range ends before it starts.',
    }
  }
  return {
    range: {
      startLine: runtimeRange.startLine + 1,
      startColumn: runtimeRange.startColumn + 1,
      endLine: runtimeRange.endLine + 1,
      endColumn: runtimeRange.endColumn + 1,
    },
    error: null,
  }
}

function sourceLocation(flow: RuntimeFlowPayload, resolvedDocumentPath?: string): string | null {
  const documentPath = resolvedDocumentPath ?? flow.documentPath
  if (!flow.range) return documentPath
  const location = `${flow.range.startLine}:${flow.range.startColumn}`
  return documentPath ? `${documentPath}:${location}` : location
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isPositiveInteger(value: unknown): value is number {
  return Number.isSafeInteger(value) && Number(value) > 0
}

function isNonNegativeInteger(value: unknown): value is number {
  return Number.isSafeInteger(value) && Number(value) >= 0
}
