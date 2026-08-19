import { ChevronDown, ChevronRight } from 'lucide-react'
import {
  type KeyboardEvent as ReactKeyboardEvent,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react'
import type { AstDocument, AstNode, TextRange } from '../api/types'
import {
  type AstSourceMap,
  type AstSourceMapEntry,
  astAncestorIds,
  createAstSourceMap,
} from './astSourceMapModel'
import type { ExecutionFlowSourceTarget } from './executionFlowModel'

interface AstTreeViewProps {
  document: AstDocument
  sourceMap?: AstSourceMap | undefined
  activeSourceAssociationKey?: string | null
  activeSourceAssociationRevision?: number
  onNavigateToSource?: ((target: ExecutionFlowSourceTarget) => void) | undefined
}

interface AstNodeItemProps {
  entry: AstSourceMapEntry
  sourceMap: AstSourceMap
  expanded: ReadonlySet<string>
  selectedId: string | null
  focusedId: string
  onToggle: (nodeId: string) => void
  onSelect: (entry: AstSourceMapEntry) => void
  onFocus: (nodeId: string) => void
  onTreeKeyDown: (event: ReactKeyboardEvent<HTMLButtonElement>, entry: AstSourceMapEntry) => void
}

function formatRange(range: TextRange | null | undefined): string {
  if (!range) return 'n/a'
  return `${range.startLine + 1}:${range.startCharacter + 1}-${range.endLine + 1}:${range.endCharacter + 1}`
}

function collectExpanded(
  node: AstNode,
  nodeId: string,
  target: Set<string>,
  maximumDepth = Number.POSITIVE_INFINITY,
  depth = 0,
) {
  if (node.children.length === 0 || depth >= maximumDepth) return
  target.add(nodeId)
  node.children.forEach((child, index) => {
    collectExpanded(child, `${nodeId}.${index}`, target, maximumDepth, depth + 1)
  })
}

function defaultExpanded(root: AstNode): Set<string> {
  const expanded = new Set<string>()
  collectExpanded(root, '0', expanded, 3)
  return expanded
}

function AstNodeItem({
  entry,
  sourceMap,
  expanded,
  selectedId,
  focusedId,
  onToggle,
  onSelect,
  onFocus,
  onTreeKeyDown,
}: AstNodeItemProps) {
  const { node, id, category } = entry
  const hasChildren = node.children.length > 0
  const isExpanded = hasChildren && expanded.has(id)
  return (
    <div className="ast-tree-item" role="none">
      <div
        className={`ast-tree-row${id === '0' ? ' ast-tree-root-row' : ''}`}
        data-ast-category={category}
        data-selected={selectedId === id}
      >
        {hasChildren ? (
          <button
            type="button"
            className="ast-tree-toggle"
            aria-label={`${isExpanded ? 'Collapse' : 'Expand'} ${node.kind}`}
            aria-expanded={isExpanded}
            tabIndex={-1}
            onClick={() => onToggle(id)}
          >
            {isExpanded ? <ChevronDown aria-hidden="true" /> : <ChevronRight aria-hidden="true" />}
          </button>
        ) : (
          <span className="ast-tree-toggle-spacer" aria-hidden="true" />
        )}
        <button
          type="button"
          role="treeitem"
          className="ast-tree-select"
          aria-level={entry.depth + 1}
          aria-selected={selectedId === id}
          aria-expanded={hasChildren ? isExpanded : undefined}
          data-node-id={id}
          data-source-association-interaction={entry.association ? 'true' : undefined}
          tabIndex={focusedId === id ? 0 : -1}
          onClick={() => onSelect(entry)}
          onDoubleClick={() => {
            if (hasChildren) onToggle(id)
          }}
          onFocus={() => onFocus(id)}
          onKeyDown={(event) => onTreeKeyDown(event, entry)}
        >
          <span>{node.kind}</span>
          <code>{formatRange(node.range)}</code>
          {hasChildren && <small>{node.children.length}</small>}
        </button>
      </div>
      {isExpanded && (
        <div className="ast-tree-group">
          {node.children.map((_child, index) => {
            const childEntry = sourceMap.entries.get(`${id}.${index}`)
            if (!childEntry) return null
            return (
              <AstNodeItem
                key={childEntry.id}
                entry={childEntry}
                sourceMap={sourceMap}
                expanded={expanded}
                selectedId={selectedId}
                focusedId={focusedId}
                onToggle={onToggle}
                onSelect={onSelect}
                onFocus={onFocus}
                onTreeKeyDown={onTreeKeyDown}
              />
            )
          })}
        </div>
      )}
    </div>
  )
}

export function AstTreeView({
  document,
  sourceMap: providedSourceMap,
  activeSourceAssociationKey = null,
  activeSourceAssociationRevision = 0,
  onNavigateToSource,
}: AstTreeViewProps) {
  const createdSourceMap = useMemo(() => createAstSourceMap(document), [document])
  const sourceMap = providedSourceMap ?? createdSourceMap
  const [expanded, setExpanded] = useState<Set<string>>(() => defaultExpanded(document.root))
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [focusedId, setFocusedId] = useState('0')
  const treeRef = useRef<HTMLDivElement>(null)
  const pendingTreeActivationKeyRef = useRef<string | null>(null)
  const selectedEntry = selectedId ? (sourceMap.entries.get(selectedId) ?? null) : null

  useEffect(() => {
    setExpanded(defaultExpanded(document.root))
    setSelectedId(null)
    setFocusedId('0')
    pendingTreeActivationKeyRef.current = null
  }, [document])

  // biome-ignore lint/correctness/useExhaustiveDependencies: the revision intentionally repeats reveal for the same source range.
  useEffect(() => {
    if (!activeSourceAssociationKey) return
    if (pendingTreeActivationKeyRef.current === activeSourceAssociationKey) {
      pendingTreeActivationKeyRef.current = null
      return
    }
    const nodeId = sourceMap.preferredNodeIdByAssociationKey.get(activeSourceAssociationKey)
    if (!nodeId) return
    setSelectedId(nodeId)
    setFocusedId(nodeId)
    const target = sourceMap.entries.get(nodeId)
    setExpanded(
      new Set([
        ...astAncestorIds(sourceMap, nodeId),
        ...(target && target.node.children.length > 0 ? [nodeId] : []),
      ]),
    )
    window.requestAnimationFrame(() => {
      treeRef.current
        ?.querySelector<HTMLElement>(`[data-node-id="${nodeId}"]`)
        ?.scrollIntoView?.({ block: 'nearest' })
    })
  }, [activeSourceAssociationKey, activeSourceAssociationRevision, sourceMap])

  const properties = useMemo(() => {
    if (!selectedEntry) return []
    return Object.entries(selectedEntry.node.properties)
      .filter(([name]) => name !== 'type')
      .sort(([left], [right]) => left.localeCompare(right))
  }, [selectedEntry])

  const toggle = (nodeId: string) => {
    setExpanded((current) => {
      const next = new Set(current)
      if (next.has(nodeId)) next.delete(nodeId)
      else next.add(nodeId)
      return next
    })
  }

  const select = (entry: AstSourceMapEntry) => {
    setSelectedId(entry.id)
    setFocusedId(entry.id)
    if (entry.association && onNavigateToSource) {
      pendingTreeActivationKeyRef.current = entry.association.key
      onNavigateToSource(entry.association)
    }
  }

  const focusNode = (nodeId: string) => {
    setFocusedId(nodeId)
    window.requestAnimationFrame(() => {
      treeRef.current?.querySelector<HTMLElement>(`[data-node-id="${nodeId}"]`)?.focus()
    })
  }

  const treeKeyDown = (event: ReactKeyboardEvent<HTMLButtonElement>, entry: AstSourceMapEntry) => {
    const visible = [...(treeRef.current?.querySelectorAll<HTMLElement>('[role="treeitem"]') ?? [])]
    const index = visible.indexOf(event.currentTarget)
    const hasChildren = entry.node.children.length > 0
    let focusTarget: HTMLElement | null = null

    if (event.key === 'ArrowDown') focusTarget = visible[index + 1] ?? null
    else if (event.key === 'ArrowUp') focusTarget = visible[index - 1] ?? null
    else if (event.key === 'Home') focusTarget = visible[0] ?? null
    else if (event.key === 'End') focusTarget = visible.at(-1) ?? null
    else if (event.key === 'ArrowRight' && hasChildren) {
      if (!expanded.has(entry.id)) toggle(entry.id)
      else focusNode(`${entry.id}.0`)
    } else if (event.key === 'ArrowLeft') {
      if (hasChildren && expanded.has(entry.id)) toggle(entry.id)
      else if (entry.parentId) focusNode(entry.parentId)
    } else if (event.key === 'Enter' || event.key === ' ') {
      select(entry)
    } else {
      return
    }

    event.preventDefault()
    event.stopPropagation()
    if (focusTarget) focusNode(focusTarget.dataset.nodeId ?? entry.id)
  }

  const rootEntry = sourceMap.entries.get('0')
  if (!rootEntry) return <div className="result-tab-empty">No syntax tree was produced.</div>

  return (
    <div className="ast-view">
      <div className="ast-layout">
        <div className="ast-tree-scroll">
          <div ref={treeRef} className="ast-tree" role="tree" aria-label="Abstract syntax tree">
            <AstNodeItem
              entry={rootEntry}
              sourceMap={sourceMap}
              expanded={expanded}
              selectedId={selectedId}
              focusedId={focusedId}
              onToggle={toggle}
              onSelect={select}
              onFocus={setFocusedId}
              onTreeKeyDown={treeKeyDown}
            />
          </div>
        </div>
        <aside className="ast-inspector" aria-label="Selected AST node">
          {selectedEntry ? (
            <>
              <header>
                <strong data-ast-category={selectedEntry.category}>
                  {selectedEntry.node.kind}
                </strong>
                <code>{formatRange(selectedEntry.node.range)}</code>
              </header>
              <dl>
                <div>
                  <dt>Type</dt>
                  <dd>{selectedEntry.node.properties.type ?? selectedEntry.category}</dd>
                </div>
                <div>
                  <dt>Kind</dt>
                  <dd>{selectedEntry.node.kind}</dd>
                </div>
                <div>
                  <dt>Range</dt>
                  <dd>{formatRange(selectedEntry.node.range)}</dd>
                </div>
                <div>
                  <dt>Full range</dt>
                  <dd>{formatRange(selectedEntry.node.fullRange)}</dd>
                </div>
                <div>
                  <dt>Children</dt>
                  <dd>{selectedEntry.node.children.length}</dd>
                </div>
                {selectedEntry.documentPath && (
                  <div>
                    <dt>Source</dt>
                    <dd>{selectedEntry.documentPath}</dd>
                  </div>
                )}
                {properties.map(([name, value]) => (
                  <div key={name}>
                    <dt>{name}</dt>
                    <dd>{value ?? 'null'}</dd>
                  </div>
                ))}
              </dl>
            </>
          ) : (
            <div className="ast-inspector-empty">Select a syntax item to inspect it.</div>
          )}
        </aside>
      </div>
    </div>
  )
}
