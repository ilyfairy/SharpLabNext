import { describe, expect, it } from 'vitest'
import type { OperationEvent } from '../api/types'
import {
  createExecutionFlowSourceModel,
  currentExecutionFlowSourceModel,
  toEditorRange,
  validateSourceRange,
} from './executionFlowModel'

function flowEvent(sequence: number, value: unknown): OperationEvent {
  return {
    operationId: 'op-flow',
    sequence,
    timestampUtc: new Date(0).toISOString(),
    traceId: 'trace-flow',
    payload: {
      kind: 'output-chunk',
      chunk: {
        channel: 'flow',
        encoding: 'utf-8',
        data: btoa(JSON.stringify(value)),
        truncated: false,
      },
    },
  }
}

function payload(eventKind = 'sequence-point') {
  return {
    EventKind: eventKind,
    DocumentPath: 'Program.cs',
    Range: { StartLine: 1, StartColumn: 0, EndLine: 1, EndColumn: 6 },
    ManagedThreadId: 4,
    TaskId: null,
    Name: null,
    Value: null,
    Truncated: false,
  }
}

describe('execution-flow source model', () => {
  const files = [{ path: 'Program.cs', text: 'line 1\nline 2\nline 3' }]

  it('normalizes runtime coordinates once and aggregates identical range kinds', () => {
    const model = createExecutionFlowSourceModel(
      [flowEvent(1, payload()), flowEvent(2, payload()), flowEvent(3, payload('branch'))],
      files,
    )

    expect(model.timeline.map((entry) => entry.locationLabel)).toEqual([
      'Program.cs:2:1',
      'Program.cs:2:1',
      'Program.cs:2:1',
    ])
    expect(model.hits).toMatchObject([
      { documentPath: 'Program.cs', eventKind: 'sequence-point', count: 2 },
      { documentPath: 'Program.cs', eventKind: 'branch', count: 1 },
    ])
    const firstHit = model.hits[0]
    expect(firstHit).toBeDefined()
    if (!firstHit) return
    expect(toEditorRange(firstHit.range)).toEqual({
      startLineNumber: 2,
      startColumn: 1,
      endLineNumber: 2,
      endColumn: 7,
    })
  })

  it('rejects unknown paths, negative coordinates, and ranges beyond the file', () => {
    const unknown = { ...payload(), DocumentPath: 'Unknown.cs' }
    const negative = {
      ...payload(),
      Range: { StartLine: -1, StartColumn: 0, EndLine: 1, EndColumn: 6 },
    }
    const beyond = {
      ...payload(),
      Range: { StartLine: 8, StartColumn: 0, EndLine: 8, EndColumn: 1 },
    }
    const model = createExecutionFlowSourceModel(
      [flowEvent(1, unknown), flowEvent(2, negative), flowEvent(3, beyond)],
      files,
    )

    expect(model.hits).toEqual([])
    expect(model.timeline.map((entry) => entry.target)).toEqual([null, null, null])
    expect(model.timeline.map((entry) => entry.sourceError)).toEqual([
      "The execution-flow source path 'Unknown.cs' is not in the workspace.",
      'Execution-flow runtime ranges must use non-negative coordinates.',
      'The execution-flow range is outside the source document.',
    ])
  })

  it('maps unique absolute PDB paths back to workspace-relative documents', () => {
    const nestedFiles = [
      { path: 'src/Program.fs', text: 'line 1\nline 2\nline 3' },
      { path: 'Library.fs', text: 'line 1\nline 2\nline 3' },
    ]
    const linux = {
      ...payload(),
      DocumentPath: '/tmp/sharplabnext/build-a1b2/src/Program.fs',
    }
    const windows = {
      ...payload(),
      DocumentPath: 'C:\\work\\build-c3d4\\Library.fs',
    }

    const model = createExecutionFlowSourceModel(
      [flowEvent(1, linux), flowEvent(2, windows)],
      nestedFiles,
    )

    expect(model.timeline.map((entry) => entry.locationLabel)).toEqual([
      'src/Program.fs:2:1',
      'Library.fs:2:1',
    ])
    expect(model.timeline.map((entry) => entry.target?.documentPath)).toEqual([
      'src/Program.fs',
      'Library.fs',
    ])
  })

  it('does not guess when an absolute PDB path matches multiple workspace suffixes', () => {
    const ambiguousFiles = [
      { path: 'Program.fs', text: 'line 1\nline 2\nline 3' },
      { path: 'src/Program.fs', text: 'line 1\nline 2\nline 3' },
    ]
    const absolute = {
      ...payload(),
      DocumentPath: '/tmp/sharplabnext/build-a1b2/src/Program.fs',
    }

    const model = createExecutionFlowSourceModel([flowEvent(1, absolute)], ambiguousFiles)

    expect(model.timeline[0]?.target).toBeNull()
    expect(model.timeline[0]?.sourceError).toContain('is not in the workspace')
  })

  it('validates editor bounds without throwing for malformed ranges', () => {
    expect(
      validateSourceRange('abc', {
        startLine: 0,
        startColumn: 0,
        endLine: 0,
        endColumn: 0,
      }),
    ).toBe('Execution-flow ranges must use positive 1-based coordinates.')
    expect(
      validateSourceRange('abc', {
        startLine: 1,
        startColumn: 1,
        endLine: 1,
        endColumn: 5,
      }),
    ).toBe('The execution-flow range is outside the source document.')
  })

  it('only exposes decorations for the current execution-flow revisions', () => {
    const model = createExecutionFlowSourceModel([flowEvent(1, payload())], files)
    const result = { outputId: 'execution-flow', workspaceRevision: 4, selectionRevision: 2 }

    expect(currentExecutionFlowSourceModel(model, result, result)).toBe(model)
    expect(
      currentExecutionFlowSourceModel(model, result, { ...result, workspaceRevision: 5 }),
    ).toBeNull()
    expect(
      currentExecutionFlowSourceModel(model, result, { ...result, selectionRevision: 3 }),
    ).toBeNull()
    expect(
      currentExecutionFlowSourceModel(model, result, { ...result, outputId: 'ast' }),
    ).toBeNull()
  })
})
