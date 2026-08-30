import type { ExecutionFlowSourceTarget } from './executionFlowModel'

export type SourceAssociationPresentation = 'linked-lines' | 'active-range'
export type AstSourceAssociationCategory = 'node' | 'token' | 'trivia'

export interface SourceAssociation extends ExecutionFlowSourceTarget {
  key: string
  colorIndex: number
  label: string
  presentation?: SourceAssociationPresentation
  /**
   * AST associations carry their syntax category so selection resolution can
   * avoid treating trailing trivia as the selected construct when a range
   * spans multiple syntax nodes. Non-AST associations leave this undefined.
   */
  astCategory?: AstSourceAssociationCategory
}

export interface SourceAssociationActivation {
  associationKey: string
  generationId: string
}

export interface SourceAssociationLine {
  lineNumber: number
  association: SourceAssociation
  active: boolean
}

export const sourceAssociationColorCount = 8

export function sourceAssociationActivationKey(activation: SourceAssociationActivation | null, generationId: string | null): string | null {
  return activation?.generationId === generationId ? activation.associationKey : null
}

export function sourceAssociationLines(associations: readonly SourceAssociation[], activeAssociationKey: string | null | undefined): SourceAssociationLine[] {
  const lines = new Map<number, SourceAssociationLine>()
  const ordered = associations.filter(isLinkedLineSourceAssociation).sort((left, right) => left.key.localeCompare(right.key))
  for (const association of ordered) {
    const active = association.key === activeAssociationKey
    const endLine = association.range.endLine > association.range.startLine && association.range.endColumn === 1 ? association.range.endLine - 1 : association.range.endLine
    for (let lineNumber = association.range.startLine; lineNumber <= endLine; lineNumber += 1) {
      const current = lines.get(lineNumber)
      if (!current) {
        lines.set(lineNumber, { lineNumber, association, active })
      } else if (!current.active && active) {
        lines.set(lineNumber, { ...current, active: true })
      }
    }
  }
  return [...lines.values()].sort((left, right) => left.lineNumber - right.lineNumber)
}

/**
 * Linked-line mappings are the IL/JIT presentation. AST nodes use the
 * active-range presentation and must never create a whole-line decoration.
 * Undefined is accepted for old in-memory callers and means linked-lines.
 */
export function isLinkedLineSourceAssociation(association: SourceAssociation): boolean {
  return association.presentation !== 'active-range'
}

export function sourceAssociationForSelection(associations: readonly SourceAssociation[], documentPath: string, selection: ExecutionFlowSourceTarget['range']): SourceAssociation | null {
  const candidates = associations.filter((association) => association.presentation === 'active-range' && association.documentPath === documentPath)
  const exact = candidates.find((association) => rangesEqual(association.range, selection))
  if (exact) return exact

  // Trivia (especially the newline at the end of the last selected line) is
  // useful for exact inspection, but it must not win when a selection spans
  // multiple syntax constructs. Keep non-trivia candidates for the structural
  // selection pass and fall back to all candidates only when no syntax item is
  // available.
  const structuralCandidates = candidates.filter((association) => association.astCategory !== 'trivia')
  const selectionCandidates = structuralCandidates.length > 0 ? structuralCandidates : candidates

  const insideSelection = selectionCandidates.filter((association) => rangeContains(selection, association.range))
  if (insideSelection.length > 0) {
    // A selection can contain several sibling nodes (for example, three
    // top-level statements). Pick their smallest common syntax container
    // instead of whichever sibling happens to be first in the AST list.
    const maximalInsideSelection = insideSelection.filter((candidate) => !insideSelection.some((other) => other !== candidate && !rangesEqual(other.range, candidate.range) && rangeContains(other.range, candidate.range)))
    if (maximalInsideSelection.length > 1) {
      const commonContainer = smallestContainingSelection(selectionCandidates, selection)
      if (commonContainer) return commonContainer
    }
    return insideSelection.reduce((largest, candidate) => (rangeContains(candidate.range, largest.range) ? candidate : largest))
  }

  return smallestContainingSelection(selectionCandidates, selection)
}

function smallestContainingSelection(candidates: readonly SourceAssociation[], selection: ExecutionFlowSourceTarget['range']): SourceAssociation | null {
  const containingSelection = candidates.filter((association) => rangeContains(association.range, selection))
  if (containingSelection.length === 0) return null
  return containingSelection.reduce((smallest, candidate) => (rangeContains(smallest.range, candidate.range) ? candidate : smallest))
}

export function createSourceAssociation(target: ExecutionFlowSourceTarget, label: string): SourceAssociation {
  const key = sourceAssociationKey(target)
  return {
    ...target,
    key,
    colorIndex: stableHash(key) % sourceAssociationColorCount,
    label,
    presentation: 'linked-lines',
  }
}

export function sourceAssociationClass(colorIndex: number): string {
  const normalized = Math.abs(Math.trunc(colorIndex)) % sourceAssociationColorCount
  return `source-association source-association-${normalized}`
}

export function sourceAssociationKey(target: ExecutionFlowSourceTarget): string {
  const { range } = target
  return [target.documentPath.replaceAll('\\', '/').toLowerCase(), range.startLine, range.startColumn, range.endLine, range.endColumn].join(':')
}

function stableHash(value: string): number {
  let hash = 2166136261
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index)
    hash = Math.imul(hash, 16777619)
  }
  return hash >>> 0
}

function rangesEqual(left: ExecutionFlowSourceTarget['range'], right: ExecutionFlowSourceTarget['range']): boolean {
  return left.startLine === right.startLine && left.startColumn === right.startColumn && left.endLine === right.endLine && left.endColumn === right.endColumn
}

function rangeContains(outer: ExecutionFlowSourceTarget['range'], inner: ExecutionFlowSourceTarget['range']): boolean {
  return comparePosition(outer.startLine, outer.startColumn, inner.startLine, inner.startColumn) <= 0 && comparePosition(outer.endLine, outer.endColumn, inner.endLine, inner.endColumn) >= 0
}

function comparePosition(leftLine: number, leftColumn: number, rightLine: number, rightColumn: number): number {
  return leftLine === rightLine ? leftColumn - rightColumn : leftLine - rightLine
}
