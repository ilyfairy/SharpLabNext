/**
 * Runtime child payloads are embedded as base64 JSON inside an operation
 * output chunk, so they do not pass through the normal API wire decoder.
 * These helpers are the explicit PascalCase transport boundary for them.
 */

export interface RuntimeGraphRoot {
  name: string
  nodeId: number
}

export interface RuntimeGraphEdge {
  name: string
  targetNodeId: number
}

export interface RuntimeGraphNode {
  id: number
  typeName: string
  kind: string
  displayValue: string | null
  edges: RuntimeGraphEdge[]
}

export interface RuntimeGraphDocument {
  roots: RuntimeGraphRoot[]
  nodes: RuntimeGraphNode[]
  truncated: boolean
  truncationReason: string | null
}

export interface RuntimeInspectionPayload {
  kind: string
  title: string
  graph: RuntimeGraphDocument
}

export function parseRuntimeInspectionPayload(value: unknown): RuntimeInspectionPayload | null {
  if (!isRecord(value) || typeof value.Kind !== 'string' || typeof value.Title !== 'string') {
    return null
  }
  const graph = parseRuntimeGraphPayload(value.Graph)
  return graph ? { kind: value.Kind, title: value.Title, graph } : null
}

export function parseRuntimeGraphPayload(value: unknown): RuntimeGraphDocument | null {
  if (!isRecord(value) || !Array.isArray(value.Roots) || !Array.isArray(value.Nodes)) return null
  if (
    typeof value.Truncated !== 'boolean' ||
    !(value.TruncationReason == null || typeof value.TruncationReason === 'string')
  ) {
    return null
  }

  const roots: RuntimeGraphRoot[] = []
  for (const root of value.Roots) {
    if (!isRecord(root) || typeof root.Name !== 'string' || !isSafeInteger(root.NodeId)) {
      return null
    }
    roots.push({ name: root.Name, nodeId: root.NodeId })
  }

  const nodes: RuntimeGraphNode[] = []
  for (const node of value.Nodes) {
    if (
      !isRecord(node) ||
      !isSafeInteger(node.Id) ||
      typeof node.TypeName !== 'string' ||
      typeof node.Kind !== 'string' ||
      !(node.DisplayValue == null || typeof node.DisplayValue === 'string') ||
      !Array.isArray(node.Edges)
    ) {
      return null
    }
    const edges: RuntimeGraphEdge[] = []
    for (const edge of node.Edges) {
      if (!isRecord(edge) || typeof edge.Name !== 'string' || !isSafeInteger(edge.TargetNodeId)) {
        return null
      }
      edges.push({ name: edge.Name, targetNodeId: edge.TargetNodeId })
    }
    nodes.push({
      id: node.Id,
      typeName: node.TypeName,
      kind: node.Kind,
      displayValue: node.DisplayValue ?? null,
      edges,
    })
  }

  return {
    roots,
    nodes,
    truncated: value.Truncated,
    truncationReason: value.TruncationReason ?? null,
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isSafeInteger(value: unknown): value is number {
  return Number.isSafeInteger(value)
}
