import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { OperationEvent, OperationEventPayload, OutputChannel } from '../api/types'
import { createExecutionFlowSourceModel } from './executionFlowModel'
import { parseRuntimePayloads, RuntimeFlowView, type RuntimeInspectionPayload, RuntimeInspectionView } from './RuntimeStructuredViews'

function event(sequence: number, channel: OutputChannel, value: unknown): OperationEvent {
  const payload: OperationEventPayload = {
    kind: 'output-chunk',
    chunk: {
      channel,
      encoding: 'utf-8',
      data: btoa(JSON.stringify(value)),
      truncated: false,
    },
  }
  return {
    operationId: 'op-runtime',
    sequence,
    timestampUtc: new Date(0).toISOString(),
    traceId: 'trace-runtime',
    payload,
  }
}

const graph = {
  roots: [{ name: 'Root 1', nodeId: 1 }],
  nodes: [
    {
      id: 1,
      typeName: 'Example.Node',
      kind: 'object',
      displayValue: null,
      edges: [{ name: 'Next', targetNodeId: 1 }],
    },
  ],
  truncated: false,
  truncationReason: null,
}

const wireGraph = {
  Roots: [{ Name: 'Root 1', NodeId: 1 }],
  Nodes: [
    {
      Id: 1,
      TypeName: 'Example.Node',
      Kind: 'object',
      DisplayValue: null,
      Edges: [{ Name: 'Next', TargetNodeId: 1 }],
    },
  ],
  Truncated: false,
  TruncationReason: null,
}

describe('runtime structured views', () => {
  it('keeps frame boundaries while parsing inspection payloads', () => {
    const payload = {
      Kind: 'MemoryGraph',
      Title: 'Memory Graph',
      Graph: wireGraph,
    }
    const parsed = parseRuntimePayloads<RuntimeInspectionPayload>([event(2, 'inspection', payload), event(3, 'stdout', 'ignored')], 'inspection')

    expect(parsed).toEqual([
      {
        sequence: 2,
        value: { kind: 'MemoryGraph', title: 'Memory Graph', graph },
        error: null,
      },
    ])
  })

  it('does not silently accept legacy camelCase runtime payloads', () => {
    const parsed = parseRuntimePayloads<RuntimeInspectionPayload>(
      [
        event(2, 'inspection', {
          kind: 'MemoryGraph',
          title: 'Memory Graph',
          graph,
        }),
      ],
      'inspection',
    )

    expect(parsed).toEqual([
      {
        sequence: 2,
        value: null,
        error: 'The runtime returned an invalid structured payload.',
      },
    ])
  })

  it('renders graph roots and cycles without recursive overflow', () => {
    render(
      <RuntimeInspectionView
        payloads={[
          {
            sequence: 2,
            value: { kind: 'MemoryGraph', title: 'Memory Graph', graph },
            error: null,
          },
        ]}
      />,
    )

    expect(screen.getByText('Memory Graph')).toBeInTheDocument()
    expect(screen.getByText('Root 1')).toBeInTheDocument()
    expect(screen.getByText(/cycle/)).toBeInTheDocument()
  })

  it('renders source-linked flow events and thread identity', () => {
    const flow = {
      EventKind: 'sequence-point',
      DocumentPath: 'Program.cs',
      Range: { StartLine: 3, StartColumn: 1, EndLine: 3, EndColumn: 11 },
      ManagedThreadId: 7,
      TaskId: null,
      Name: null,
      Value: null,
      Truncated: false,
    }
    const model = createExecutionFlowSourceModel(
      [event(4, 'flow', flow)],
      [
        {
          path: 'Program.cs',
          text: 'line 1\nline 2\nline 3\n0123456789012345',
        },
      ],
    )
    const onNavigate = vi.fn()
    render(<RuntimeFlowView model={model} onNavigate={onNavigate} />)

    fireEvent.click(screen.getByRole('button', { name: 'Open Program.cs:4:2' }))
    expect(onNavigate).toHaveBeenCalledWith({
      documentPath: 'Program.cs',
      range: { startLine: 4, startColumn: 2, endLine: 4, endColumn: 12 },
    })
    expect(screen.getByText('Thread 7')).toBeInTheDocument()
  })
})
