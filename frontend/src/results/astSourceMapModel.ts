import type { AstDocument, AstNode, TextRange } from '../api/types'
import type { ExecutionFlowSourceTarget } from './executionFlowModel'
import { type AstSourceAssociationCategory, createSourceAssociation, type SourceAssociation } from './sourceAssociationModel'

export type AstNodeCategory = 'node' | 'token' | 'trivia'

export interface AstSourceMapEntry {
  id: string
  parentId: string | null
  depth: number
  node: AstNode
  category: AstNodeCategory
  documentPath: string | null
  association: SourceAssociation | null
}

export interface AstSourceMap {
  entries: ReadonlyMap<string, AstSourceMapEntry>
  associations: readonly SourceAssociation[]
  preferredNodeIdByAssociationKey: ReadonlyMap<string, string>
  nodeCount: number
}

interface AssociationCandidate {
  association: SourceAssociation
  nodeId: string
  depth: number
}

export function createAstSourceMap(document: AstDocument): AstSourceMap {
  const entries = new Map<string, AstSourceMapEntry>()
  const candidates = new Map<string, AssociationCandidate>()

  const visit = (node: AstNode, id: string, parentId: string | null, depth: number, inheritedPath: string | null) => {
    const documentPath = documentPathForNode(node, inheritedPath)
    const association = sourceAssociationForNode(node, documentPath)
    entries.set(id, {
      id,
      parentId,
      depth,
      node,
      category: astNodeCategory(node),
      documentPath,
      association,
    })

    if (association) {
      const existing = candidates.get(association.key)
      if (!existing || depth > existing.depth) {
        candidates.set(association.key, { association, nodeId: id, depth })
      }
    }

    node.children.forEach((child, index) => {
      visit(child, `${id}.${index}`, id, depth + 1, documentPath)
    })
  }

  visit(document.root, '0', null, 0, null)

  // Editors resolve a click with Array.find(), so deepest syntax items must be
  // considered before their containing nodes. Equal source ranges are kept once.
  const orderedCandidates = [...candidates.values()].sort((left, right) => right.depth - left.depth || left.association.key.localeCompare(right.association.key))

  return {
    entries,
    associations: orderedCandidates.map(({ association }) => association),
    preferredNodeIdByAssociationKey: new Map(orderedCandidates.map(({ association, nodeId }) => [association.key, nodeId])),
    nodeCount: entries.size,
  }
}

export function astNodeCategory(node: AstNode): AstNodeCategory {
  if (node.properties.isTrivia === 'true') return 'trivia'
  if (node.properties.isToken === 'true') return 'token'
  return 'node'
}

export function astAncestorIds(sourceMap: AstSourceMap, nodeId: string): string[] {
  const ancestors: string[] = []
  let current = sourceMap.entries.get(nodeId)?.parentId ?? null
  while (current) {
    ancestors.push(current)
    current = sourceMap.entries.get(current)?.parentId ?? null
  }
  return ancestors
}

function documentPathForNode(node: AstNode, inheritedPath: string | null): string | null {
  const ownPath = node.kind === 'Document' ? node.properties.path : null
  return typeof ownPath === 'string' && ownPath.length > 0 ? ownPath : inheritedPath
}

function sourceAssociationForNode(node: AstNode, documentPath: string | null): SourceAssociation | null {
  if (!documentPath || node.kind === 'Document' || node.kind === 'Workspace') return null
  if (isEmptyRange(node.range)) return null

  const target: ExecutionFlowSourceTarget = {
    documentPath,
    range: toSourceRange(node.range),
  }
  return {
    ...createSourceAssociation(target, `AST ${node.kind}`),
    presentation: 'active-range',
    astCategory: astNodeCategory(node) as AstSourceAssociationCategory,
  }
}

function toSourceRange(range: TextRange): ExecutionFlowSourceTarget['range'] {
  return {
    startLine: range.startLine + 1,
    startColumn: range.startCharacter + 1,
    endLine: range.endLine + 1,
    endColumn: range.endCharacter + 1,
  }
}

function isEmptyRange(range: TextRange): boolean {
  return range.startLine === range.endLine && range.startCharacter === range.endCharacter
}
