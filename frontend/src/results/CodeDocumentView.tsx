import { selectAll } from '@codemirror/commands'
import { foldGutter, foldKeymap, foldService } from '@codemirror/language'
import { EditorState, type Extension, StateEffect, StateField } from '@codemirror/state'
import { Decoration, type DecorationSet, drawSelection, EditorView, highlightSpecialChars, hoverTooltip, keymap, lineNumbers, type Tooltip } from '@codemirror/view'
import { lazy, type RefObject, Suspense, useEffect, useRef } from 'react'
import { appendCodeMirrorHoverSections, codeMirrorHoverSections, semanticDecorationRanges } from '../editor/CodeMirrorEditor'
import { semanticDecorationExtension, setSemanticDecorations } from '../editor/codeMirrorDecorations'
import { codeMirrorReadOnlyExtensions } from '../editor/codeMirrorLanguage'
import type { EditorFontSize, EditorKind } from '../editor/editorPreference'
import { type IlOutputLanguageSession, type IlOutputLanguageSessionOptions, useIlOutputLanguageSession } from './ilOutputLanguageSession'
import { type OutputFoldingRange, outputFoldingRanges } from './outputFoldingModel'
import { type SourceAssociation, sourceAssociationClass } from './sourceAssociationModel'

export interface CodeDocumentLineTooltip {
  startLine: number
  endLine: number
  heading: string
  body: string
}

export interface CodeDocumentLineAction {
  startLine: number
  endLine: number
  ariaLabel: string
  onActivate: () => void
}

export interface CodeDocumentLineAssociation {
  startLine: number
  endLine: number
  association: SourceAssociation
}

interface NavigableLineState {
  ranges: readonly (CodeDocumentLineAssociation & {
    navigable: boolean
    active: boolean
  })[]
  decorations: DecorationSet
}

const setNavigableLines = StateEffect.define<NavigableLineState['ranges']>()
const navigableLines = StateField.define<NavigableLineState>({
  create: () => ({ ranges: [], decorations: Decoration.none }),
  update(value, transaction) {
    const update = transaction.effects.find((effect) => effect.is(setNavigableLines))
    const ranges = update?.value ?? value.ranges
    if (!update && !transaction.docChanged) return value
    return {
      ranges,
      decorations: navigableLineDecorations(transaction.state, ranges),
    }
  },
  provide: (field) => EditorView.decorations.from(field, (value) => value.decorations),
})

export interface CodeDocumentViewProps {
  text: string
  languageId: string
  ariaLabel: string
  fontSize: EditorFontSize
  generationKey?: string | null
  ilOutputLanguageSessionOptions?: IlOutputLanguageSessionOptions | null
  lineTooltips?: readonly CodeDocumentLineTooltip[]
  lineActions?: readonly CodeDocumentLineAction[]
  lineAssociations?: readonly CodeDocumentLineAssociation[]
  activeAssociationKey?: string | null
  activeAssociationRevision?: number
  editorKind?: EditorKind
  onAssociationHover?: ((associationKey: string | null) => void) | undefined
}

const MonacoCodeDocumentView = lazy(async () => {
  const module = await import('./MonacoCodeDocumentView')
  return { default: module.MonacoCodeDocumentView }
})

export function CodeDocumentView(props: CodeDocumentViewProps) {
  if (props.editorKind === 'monaco') {
    return (
      <Suspense fallback={<div className="result-tab-empty">Loading output editor...</div>}>
        <MonacoCodeDocumentView {...props} />
      </Suspense>
    )
  }
  return <CodeMirrorCodeDocumentView {...props} />
}

function CodeMirrorCodeDocumentView({
  text,
  languageId,
  ariaLabel,
  fontSize,
  generationKey = null,
  ilOutputLanguageSessionOptions = null,
  lineTooltips = [],
  lineActions = [],
  lineAssociations = [],
  activeAssociationKey = null,
  activeAssociationRevision = 0,
  onAssociationHover,
}: CodeDocumentViewProps) {
  const ilOutputLanguageSession = useIlOutputLanguageSession(text, generationKey, languageId === 'il' ? ilOutputLanguageSessionOptions : null)
  const hostRef = useRef<HTMLDivElement>(null)
  const viewRef = useRef<EditorView | null>(null)
  const ilOutputLanguageSessionRef = useRef<IlOutputLanguageSession | null>(null)
  const textRef = useRef(text)
  const generationKeyRef = useRef(generationKey)
  const lineTooltipsRef = useRef(lineTooltips)
  const lineActionsRef = useRef(lineActions)
  const lineAssociationsRef = useRef(lineAssociations)
  const onAssociationHoverRef = useRef(onAssociationHover)
  const hoveredAssociationKeyRef = useRef<string | null>(null)
  const pointerGestureRef = useRef<{
    x: number
    y: number
    moved: boolean
  } | null>(null)
  const pendingLineActivationRef = useRef<number | null>(null)
  const handledAssociationRevealRevisionRef = useRef<number | null>(null)
  const navigableLinesSignatureRef = useRef<string | null>(null)
  const lineActionsSignature = lineActions.map(lineActionKey).join('|')
  const pendingActivationInputsRef = useRef({
    generationKey,
    lineActionsSignature,
    text,
  })
  const handledAssociationGenerationRef = useRef(generationKey)
  textRef.current = text
  generationKeyRef.current = generationKey
  lineTooltipsRef.current = lineTooltips
  lineActionsRef.current = lineActions
  lineAssociationsRef.current = lineAssociations
  onAssociationHoverRef.current = onAssociationHover
  ilOutputLanguageSessionRef.current = languageId === 'il' ? ilOutputLanguageSession : null

  useEffect(() => {
    const host = hostRef.current
    if (!host) return
    const updateHoveredAssociation = (associationKey: string | null) => {
      if (hoveredAssociationKeyRef.current === associationKey) return
      hoveredAssociationKeyRef.current = associationKey
      onAssociationHoverRef.current?.(associationKey)
    }
    const view = new EditorView({
      parent: host,
      state: EditorState.create({
        doc: textRef.current,
        extensions: [
          lineNumbers(),
          highlightSpecialChars(),
          drawSelection(),
          keymap.of([{ key: 'Mod-a', run: selectAll }]),
          ...codeDocumentFoldingExtensions(languageId),
          hoverTooltip(codeDocumentSourceMapHoverSource(lineTooltipsRef), {
            hideOnChange: true,
            hoverTime: 300,
          }),
          hoverTooltip(codeDocumentIlHoverSource(ilOutputLanguageSessionRef), {
            hideOnChange: true,
            hoverTime: 300,
          }),
          semanticDecorationExtension,
          navigableLines,
          EditorView.domEventHandlers({
            mousedown(event) {
              if (eventTargetInGutter(event.target)) return false
              if (event.button !== 0) return false
              pointerGestureRef.current = {
                x: event.clientX,
                y: event.clientY,
                moved: false,
              }
              return false
            },
            mousemove(event, view) {
              const gesture = pointerGestureRef.current
              if (gesture && (Math.abs(event.clientX - gesture.x) > 3 || Math.abs(event.clientY - gesture.y) > 3)) {
                gesture.moved = true
              }
              const line = lineNumberFromEventTarget(view, event.target)
              const association = line === null ? null : lineAssociationsRef.current.find((candidate) => line >= candidate.startLine && line <= candidate.endLine)
              updateHoveredAssociation(association?.association.key ?? null)
              return false
            },
            mouseleave() {
              updateHoveredAssociation(null)
              return false
            },
            click(event, view) {
              if (eventTargetInGutter(event.target)) return false
              const gesture = pointerGestureRef.current
              pointerGestureRef.current = null
              if (event.detail > 1 && pendingLineActivationRef.current !== null) {
                window.clearTimeout(pendingLineActivationRef.current)
                pendingLineActivationRef.current = null
              }
              if (event.button !== 0 || event.detail > 1 || gesture?.moved || !view.state.selection.main.empty || window.getSelection()?.isCollapsed === false) {
                return false
              }
              const assemblyLabel = languageId === 'asm' ? assemblyLabelTarget(view, event) : null
              if (assemblyLabel) {
                if (pendingLineActivationRef.current !== null) {
                  window.clearTimeout(pendingLineActivationRef.current)
                }
                const scheduledGenerationKey = generationKeyRef.current
                const scheduledText = textRef.current
                pendingLineActivationRef.current = window.setTimeout(() => {
                  pendingLineActivationRef.current = null
                  if (generationKeyRef.current !== scheduledGenerationKey || textRef.current !== scheduledText) {
                    return
                  }
                  view.dispatch({
                    selection: {
                      anchor: assemblyLabel.from,
                      head: assemblyLabel.to,
                    },
                    effects: EditorView.scrollIntoView(assemblyLabel.from, {
                      y: 'center',
                    }),
                  })
                  view.focus()
                }, 400)
                return false
              }
              const line = lineNumberFromEventTarget(view, event.target)
              if (line === null) return false
              const action = lineActionsRef.current.find((candidate) => line >= candidate.startLine && line <= candidate.endLine)
              if (!action) return false
              if (pendingLineActivationRef.current !== null) {
                window.clearTimeout(pendingLineActivationRef.current)
              }
              const actionKey = lineActionKey(action)
              const scheduledGenerationKey = generationKeyRef.current
              const scheduledText = textRef.current
              pendingLineActivationRef.current = window.setTimeout(() => {
                pendingLineActivationRef.current = null
                if (generationKeyRef.current !== scheduledGenerationKey || textRef.current !== scheduledText) {
                  return
                }
                lineActionsRef.current.find((candidate) => lineActionKey(candidate) === actionKey)?.onActivate()
              }, 400)
              return false
            },
            dblclick() {
              pointerGestureRef.current = null
              if (pendingLineActivationRef.current !== null) {
                window.clearTimeout(pendingLineActivationRef.current)
                pendingLineActivationRef.current = null
              }
              return false
            },
          }),
          ...codeMirrorReadOnlyExtensions(languageId),
          EditorView.contentAttributes.of({
            'aria-label': ariaLabel,
            tabindex: '0',
          }),
          EditorView.theme({
            '&': { height: '100%' },
            '.cm-scroller': { overflow: 'auto' },
            '.cm-content': {
              padding: '10px 0',
              fontFamily: "'Cascadia Code', 'Cascadia Mono', Consolas, monospace",
              fontSize: 'var(--code-font-size, 14px)',
              lineHeight: '1.5',
            },
            '.cm-gutters': {
              backgroundColor: '#F7F8FA',
              borderRight: '1px solid #E1E5EA',
              color: '#68717D',
              fontSize: 'var(--code-font-size, 14px)',
              lineHeight: '1.5',
            },
            '.code-document-source-tooltip': {
              display: 'grid',
              gap: '4px',
              maxWidth: 'min(560px, 80vw)',
              padding: '7px 9px',
            },
            '.code-document-source-tooltip strong': {
              color: '#1f5f99',
              fontFamily: "'Segoe UI', sans-serif",
              fontSize: '12px',
              fontWeight: '600',
            },
            '.code-document-source-tooltip code': {
              overflow: 'hidden',
              color: '#24292f',
              fontFamily: "'Cascadia Code', 'Cascadia Mono', Consolas, monospace",
              fontSize: '12px',
              textOverflow: 'ellipsis',
              whiteSpace: 'pre',
            },
            '.cm-source-navigable': {
              cursor: 'pointer',
            },
          }),
        ],
      }),
    })
    viewRef.current = view
    navigableLinesSignatureRef.current = null
    const cancelPendingActivationOnScroll = () => {
      if (pendingLineActivationRef.current === null) return
      window.clearTimeout(pendingLineActivationRef.current)
      pendingLineActivationRef.current = null
    }
    view.scrollDOM.addEventListener('scroll', cancelPendingActivationOnScroll, {
      passive: true,
    })
    const resizeObserver = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => view.requestMeasure())
    resizeObserver?.observe(host)
    return () => {
      if (pendingLineActivationRef.current !== null) {
        window.clearTimeout(pendingLineActivationRef.current)
        pendingLineActivationRef.current = null
      }
      view.scrollDOM.removeEventListener('scroll', cancelPendingActivationOnScroll)
      updateHoveredAssociation(null)
      resizeObserver?.disconnect()
      view.destroy()
      if (viewRef.current === view) viewRef.current = null
    }
  }, [ariaLabel, languageId])

  useEffect(() => {
    const previous = pendingActivationInputsRef.current
    pendingActivationInputsRef.current = {
      generationKey,
      lineActionsSignature,
      text,
    }
    if (previous.generationKey === generationKey && previous.lineActionsSignature === lineActionsSignature && previous.text === text) {
      return
    }
    if (pendingLineActivationRef.current !== null) {
      window.clearTimeout(pendingLineActivationRef.current)
      pendingLineActivationRef.current = null
    }
  }, [generationKey, lineActionsSignature, text])

  useEffect(() => {
    if (handledAssociationGenerationRef.current === generationKey) return
    handledAssociationGenerationRef.current = generationKey
    handledAssociationRevealRevisionRef.current = null
  }, [generationKey])

  useEffect(() => {
    const view = viewRef.current
    if (!view || view.state.doc.toString() === text) return
    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: text },
      effects: setSemanticDecorations.of([]),
    })
  }, [text])

  useEffect(() => {
    const view = viewRef.current
    if (!view) return
    view.dispatch({
      effects: setSemanticDecorations.of(languageId === 'il' ? semanticDecorationRanges(view.state.doc, ilOutputLanguageSession.semanticTokens) : []),
    })
  }, [ilOutputLanguageSession.semanticTokens, languageId])

  // biome-ignore lint/correctness/useExhaustiveDependencies: languageId recreates the EditorView and must replay its line decorations.
  useEffect(() => {
    const ranges = lineAssociations.map(({ startLine, endLine, association }) => ({
      startLine,
      endLine,
      association,
      active: association.key === activeAssociationKey,
      navigable: lineActions.some((action) => action.startLine <= endLine && action.endLine >= startLine),
    }))
    const signature = ranges.map((range) => `${range.startLine}:${range.endLine}:${range.association.key}:${range.active ? 1 : 0}:${range.navigable ? 1 : 0}`).join('|')
    if (navigableLinesSignatureRef.current === signature) return
    navigableLinesSignatureRef.current = signature
    viewRef.current?.dispatch({
      effects: setNavigableLines.of(ranges),
    })
  }, [activeAssociationKey, languageId, lineActions, lineAssociations])

  useEffect(() => {
    const view = viewRef.current
    if (!view || !activeAssociationKey) return
    if (handledAssociationRevealRevisionRef.current === activeAssociationRevision) return
    const association = lineAssociations.find((candidate) => candidate.association.key === activeAssociationKey)
    if (!association || association.startLine < 1 || association.startLine > view.state.doc.lines) {
      return
    }
    view.dispatch({
      effects: EditorView.scrollIntoView(view.state.doc.line(association.startLine).from, {
        y: 'center',
      }),
    })
    handledAssociationRevealRevisionRef.current = activeAssociationRevision
  }, [activeAssociationKey, activeAssociationRevision, lineAssociations])

  useEffect(() => {
    hostRef.current?.style.setProperty('--code-font-size', `${fontSize}px`)
    viewRef.current?.requestMeasure()
  }, [fontSize])

  return <div ref={hostRef} className="code-document-view codemirror-host" />
}

export function codeDocumentIlHoverSource(ilLanguageSessionRef: RefObject<IlOutputLanguageSession | null>) {
  return async (view: EditorView, position: number): Promise<Tooltip | null> => {
    const ilLanguageSession = ilLanguageSessionRef.current
    if (ilLanguageSession?.status === 'ready') {
      try {
        const line = view.state.doc.lineAt(position)
        const hover = await ilLanguageSession.hover({
          line: line.number - 1,
          character: position - line.from,
        })
        const sections = codeMirrorHoverSections(hover)
        if (hover && sections.length > 0) {
          const range = hover.range ? codeDocumentOffsetsForRange(view.state, hover.range) : null
          const word = view.state.wordAt(position)
          return {
            pos: range?.from ?? word?.from ?? position,
            end: range?.to ?? word?.to ?? position,
            above: true,
            create() {
              const dom = document.createElement('div')
              dom.className = 'cm-lsp-hover'
              dom.setAttribute('role', 'tooltip')
              appendCodeMirrorHoverSections(dom, sections)
              return { dom }
            },
          }
        }
      } catch {
        // A result remains useful with local IL highlighting while its
        // short-lived language session reconnects or becomes unavailable.
      }
    }
    return null
  }
}

export function codeDocumentSourceMapHoverSource(lineTooltipsRef: RefObject<readonly CodeDocumentLineTooltip[]>) {
  return (view: EditorView, position: number): Tooltip | null => {
    const line = view.state.doc.lineAt(position)
    const tooltip = lineTooltipsRef.current?.find((candidate) => line.number >= candidate.startLine && line.number <= candidate.endLine)
    if (!tooltip) return null
    return {
      pos: line.from,
      end: line.to,
      above: true,
      create() {
        const dom = document.createElement('div')
        dom.className = 'code-document-source-tooltip'
        dom.setAttribute('role', 'tooltip')
        const heading = document.createElement('strong')
        heading.textContent = tooltip.heading
        const body = document.createElement('code')
        body.textContent = tooltip.body
        dom.append(heading, body)
        return { dom }
      },
    }
  }
}

function codeDocumentOffsetsForRange(
  state: EditorState,
  range: {
    start: { line: number; character: number }
    end: { line: number; character: number }
  },
): { from: number; to: number } | null {
  const offset = (position: { line: number; character: number }): number | null => {
    if (!Number.isSafeInteger(position.line) || !Number.isSafeInteger(position.character) || position.line < 0 || position.character < 0 || position.line >= state.doc.lines) {
      return null
    }
    const line = state.doc.line(position.line + 1)
    return position.character <= line.length ? line.from + position.character : null
  }
  const from = offset(range.start)
  const to = offset(range.end)
  return from !== null && to !== null && to >= from ? { from, to } : null
}

function navigableLineDecorations(state: EditorState, ranges: NavigableLineState['ranges']): DecorationSet {
  const rangeByLine = new Map<number, NavigableLineState['ranges'][number]>()
  for (const range of ranges) {
    const startLine = Math.max(1, Math.min(state.doc.lines, range.startLine))
    const endLine = Math.max(startLine, Math.min(state.doc.lines, range.endLine))
    for (let lineNumber = startLine; lineNumber <= endLine; lineNumber += 1) {
      const current = rangeByLine.get(lineNumber)
      if (!current || (!current.active && range.active)) rangeByLine.set(lineNumber, range)
    }
  }
  return Decoration.set(
    [...rangeByLine.entries()].map(([lineNumber, range]) =>
      Decoration.line({
        attributes: {
          class: [range.navigable ? 'cm-source-navigable' : '', range.active ? 'cm-source-association-active' : '', sourceAssociationClass(range.association.colorIndex)].filter(Boolean).join(' '),
        },
      }).range(state.doc.line(lineNumber).from),
    ),
    true,
  )
}

export function codeDocumentFoldingExtensions(languageId: string): Extension[] {
  if (languageId !== 'asm' && languageId !== 'il') return []
  const rangeField = StateField.define<readonly OutputFoldingRange[]>({
    create: (state) => outputFoldingRanges(state.doc.toString(), languageId),
    update: (ranges, transaction) => (transaction.docChanged ? outputFoldingRanges(transaction.state.doc.toString(), languageId) : ranges),
  })
  return [
    rangeField,
    foldService.of((state, lineStart) => {
      const lineNumber = state.doc.lineAt(lineStart).number
      const range = state.field(rangeField).find((candidate) => candidate.startLine === lineNumber)
      if (!range || range.endLine > state.doc.lines) return null
      const from = state.doc.line(range.startLine).to
      const to = state.doc.line(range.endLine).to
      return to > from ? { from, to } : null
    }),
    foldGutter(),
    keymap.of(foldKeymap),
  ]
}

function lineNumberFromEventTarget(view: EditorView, target: EventTarget | null): number | null {
  const element = target instanceof Element ? target : target instanceof Node ? target.parentElement : null
  const line = element?.closest('.cm-line')
  if (!line || !view.contentDOM.contains(line)) return null
  const renderedLines = [...view.contentDOM.querySelectorAll('.cm-line')]
  const renderedIndex = renderedLines.indexOf(line)
  if (renderedIndex < 0) return null
  try {
    return view.state.doc.lineAt(view.posAtDOM(line, 0)).number
  } catch {
    return renderedIndex + 1
  }
}

function eventTargetInGutter(target: EventTarget | null): boolean {
  const element = target instanceof Element ? target : target instanceof Node ? target.parentElement : null
  return element?.closest('.cm-gutter') != null
}

function lineActionKey(action: CodeDocumentLineAction): string {
  return `${action.startLine}:${action.endLine}:${action.ariaLabel}`
}

function assemblyLabelTarget(view: EditorView, event: MouseEvent): { from: number; to: number } | null {
  let position: number | null = null
  try {
    position = view.posAtCoords({ x: event.clientX, y: event.clientY })
  } catch {
    const target = event.target instanceof Node ? event.target : null
    if (target) {
      try {
        position = view.posAtDOM(target, 0)
      } catch {
        position = null
      }
    }
  }
  if (position === null) return null
  const line = view.state.doc.lineAt(position)
  const offset = position - line.from
  const referenceMatch = [...line.text.matchAll(/\bG_M\w+\b/gi)].find((match) => offset >= (match.index ?? 0) && offset <= (match.index ?? 0) + match[0].length)
  const reference = referenceMatch?.[0]
  if (!reference || line.text[(referenceMatch?.index ?? 0) + reference.length] === ':') return null
  const declaration = new RegExp(`^\\s*${escapeRegExp(reference)}:`, 'im').exec(view.state.doc.toString())
  if (!declaration || declaration.index === undefined) return null
  const labelStart = declaration.index + (declaration[0].match(/^\s*/)?.[0].length ?? 0)
  return { from: labelStart, to: labelStart + reference.length }
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
