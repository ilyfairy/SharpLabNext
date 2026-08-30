import type { OperationEvent, OutputChannel } from '../api/types'
import type { ExecutionFlowSourceModel, ExecutionFlowSourceTarget } from './executionFlowModel'
import { parseRuntimeGraphPayload, parseRuntimeInspectionPayload, type RuntimeGraphDocument, type RuntimeGraphNode, type RuntimeInspectionPayload } from './runtimePayloadWire'

export type {
  RuntimeGraphDocument,
  RuntimeGraphEdge,
  RuntimeGraphNode,
  RuntimeGraphRoot,
  RuntimeInspectionPayload,
} from './runtimePayloadWire'

export interface ParsedRuntimePayload<T> {
  sequence: number
  value: T | null
  error: string | null
}

function decode(data: string): string {
  const bytes = Uint8Array.from(atob(data), (character) => character.charCodeAt(0))
  return new TextDecoder().decode(bytes)
}

export function parseRuntimePayloads<T>(events: readonly OperationEvent[], channel: OutputChannel): ParsedRuntimePayload<T>[] {
  const payloads: ParsedRuntimePayload<T>[] = []
  for (const event of events) {
    if (event.payload.kind !== 'output-chunk' || event.payload.chunk.channel !== channel) continue
    try {
      const parsed = parseRuntimeInspectionPayload(JSON.parse(decode(event.payload.chunk.data)))
      if (!parsed) throw new Error('Invalid runtime inspection payload.')
      payloads.push({
        sequence: event.sequence,
        value: parsed as T,
        error: null,
      })
    } catch {
      payloads.push({
        sequence: event.sequence,
        value: null,
        error: 'The runtime returned an invalid structured payload.',
      })
    }
  }
  return payloads
}

function GraphNode({ nodeId, nodes, ancestors }: { nodeId: number; nodes: ReadonlyMap<number, RuntimeGraphNode>; ancestors: ReadonlySet<number> }) {
  const node = nodes.get(nodeId)
  if (!node) return <span className="runtime-graph-missing">Missing node {nodeId}</span>
  const cyclic = ancestors.has(nodeId)
  const label = node.displayValue == null ? node.typeName : `${node.typeName}: ${node.displayValue}`
  if (cyclic) return <span className="runtime-graph-cycle">{label} (cycle)</span>
  if (node.edges.length === 0) return <span className="runtime-graph-value">{label}</span>

  const nextAncestors = new Set(ancestors)
  nextAncestors.add(nodeId)
  return (
    <details className="runtime-graph-node" open={ancestors.size < 2}>
      <summary>
        <span>{label}</span>
        <code>{node.kind}</code>
      </summary>
      <ol>
        {node.edges.map((edge) => (
          <li key={`${node.id}:${edge.name}:${edge.targetNodeId}`}>
            <strong>{edge.name}</strong>
            <GraphNode nodeId={edge.targetNodeId} nodes={nodes} ancestors={nextAncestors} />
          </li>
        ))}
      </ol>
    </details>
  )
}

export function RuntimeGraphView({ graph }: { graph: RuntimeGraphDocument }) {
  const nodes = new Map(graph.nodes.map((node) => [node.id, node]))
  return (
    <div className="runtime-graph">
      {graph.roots.length === 0 ? (
        <div className="result-tab-empty">No graph roots.</div>
      ) : (
        <ol className="runtime-graph-roots">
          {graph.roots.map((root) => (
            <li key={`${root.name}:${root.nodeId}`}>
              <strong>{root.name}</strong>
              <GraphNode nodeId={root.nodeId} nodes={nodes} ancestors={new Set()} />
            </li>
          ))}
        </ol>
      )}
      {graph.truncated && (
        <p className="runtime-structured-warning">
          Graph truncated
          {graph.truncationReason ? `: ${graph.truncationReason}` : '.'}
        </p>
      )}
    </div>
  )
}

export function RuntimeInspectionView({ payloads }: { payloads: readonly ParsedRuntimePayload<RuntimeInspectionPayload>[] }) {
  if (payloads.length === 0) return <div className="result-tab-empty">No inspection output.</div>
  return (
    <ol className="runtime-structured-list">
      {payloads.map((payload) => (
        <li key={payload.sequence}>
          {payload.value ? (
            <>
              <header>
                <strong>{payload.value.title}</strong>
                <code>{payload.value.kind}</code>
              </header>
              <RuntimeGraphView graph={payload.value.graph} />
            </>
          ) : (
            <p className="result-tab-error">{payload.error}</p>
          )}
        </li>
      ))}
    </ol>
  )
}

export function RuntimeFlowView({ model, onNavigate }: { model: ExecutionFlowSourceModel; onNavigate?: ((target: ExecutionFlowSourceTarget) => void) | undefined }) {
  if (model.timeline.length === 0) return <div className="result-tab-empty">No execution-flow output.</div>
  return (
    <ol className="runtime-flow-list">
      {model.timeline.map((entry) => {
        if (!entry.payload) {
          return (
            <li key={entry.sequence}>
              <p className="result-tab-error">{entry.error}</p>
            </li>
          )
        }
        const payload = entry.payload
        const graph = runtimeGraph(payload.value)
        return (
          <li key={entry.sequence} data-kind={payload.eventKind}>
            <div className="runtime-flow-marker" aria-hidden="true" />
            <div>
              <header>
                <strong>{payload.name ?? payload.eventKind}</strong>
                <code>{payload.eventKind}</code>
                <span>Thread {payload.managedThreadId}</span>
              </header>
              {entry.locationLabel && entry.target && onNavigate ? (
                <button className="runtime-flow-location" type="button" title={`Open ${entry.locationLabel}`} aria-label={`Open ${entry.locationLabel}`} onClick={() => onNavigate(entry.target as ExecutionFlowSourceTarget)}>
                  {entry.locationLabel}
                </button>
              ) : (
                entry.locationLabel && <p>{entry.locationLabel}</p>
              )}
              {entry.sourceError && <p className="runtime-structured-warning">{entry.sourceError}</p>}
              {graph && <RuntimeGraphView graph={graph} />}
              {payload.truncated && <p className="runtime-structured-warning">Execution-flow output was truncated.</p>}
            </div>
          </li>
        )
      })}
    </ol>
  )
}

function runtimeGraph(value: unknown): RuntimeGraphDocument | null {
  return parseRuntimeGraphPayload(value)
}
