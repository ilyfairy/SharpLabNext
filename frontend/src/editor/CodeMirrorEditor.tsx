import {
  acceptCompletion,
  snippet as applySnippet,
  autocompletion,
  type Completion,
  type CompletionContext,
  closeBrackets,
  closeBracketsKeymap,
  closeCompletion,
  completionKeymap as defaultCompletionKeymap,
  hasNextSnippetField,
  insertCompletionText,
  nextSnippetField,
  pickedCompletion,
  startCompletion,
} from '@codemirror/autocomplete'
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands'
import {
  bracketMatching,
  foldGutter,
  foldKeymap,
  indentOnInput,
  indentUnit,
  syntaxHighlighting,
} from '@codemirror/language'
import { type Action, type Diagnostic, lintGutter, setDiagnostics } from '@codemirror/lint'
import { searchKeymap } from '@codemirror/search'
import {
  Annotation,
  type ChangeSpec,
  Compartment,
  EditorState,
  type Extension,
  type Text,
  Transaction,
  type TransactionSpec,
} from '@codemirror/state'
import {
  drawSelection,
  dropCursor,
  EditorView,
  highlightActiveLine,
  highlightActiveLineGutter,
  highlightSpecialChars,
  hoverTooltip,
  type KeyBinding,
  keymap,
  lineNumbers,
  rectangularSelection,
  type Tooltip,
  type ViewUpdate,
} from '@codemirror/view'
import { type CSSProperties, useEffect, useRef } from 'react'
import type { BuildConfiguration, ResolveSelectionResponse } from '../api/types'
import {
  type CodeMirrorDocumentSymbol,
  CodeMirrorLanguageBridge,
  type CodeMirrorLanguageSink,
  type CodeMirrorLspCodeAction,
  type CodeMirrorLspCompletionItem,
  type CodeMirrorLspDiagnostic,
  type CodeMirrorLspFoldingRange,
  type CodeMirrorLspHover,
  type CodeMirrorLspSignatureHelp,
  type CodeMirrorSemanticToken,
  createCodeMirrorLanguageSessionDependencies,
  type LspPosition,
  type LspRange,
} from '../lsp/codeMirrorLanguageClient'
import { ilSenseCompletionTriggerCharacters } from '../lsp/completionTriggerCharacters'
import { createLanguageWorkspaceUri } from '../lsp/languageDocumentUri'
import {
  createLanguageSessionKey,
  LanguageSessionLifecycle,
  type LanguageSessionStatusChange,
} from '../lsp/languageSessionLifecycle'
import {
  type ExecutionFlowNavigationRequest,
  type ExecutionFlowSourceHit,
  type ExecutionFlowSourceModel,
  validateSourceRange,
} from '../results/executionFlowModel'
import {
  isLinkedLineSourceAssociation,
  type SourceAssociation,
  sourceAssociationClass,
  sourceAssociationForSelection,
  sourceAssociationLines,
} from '../results/sourceAssociationModel'
import {
  buildOutputKindForResolvedPipeline,
  createWorkbenchBuildOptions,
  type RememberedWorkbenchOutputKind,
  retainResolvedWorkbenchOutputKind,
} from '../workbench/buildOptions'
import {
  type CodeMirrorDecorationRange,
  type CodeMirrorFoldingRange,
  type CodeMirrorSignaturePresentation,
  executionFlowDecorationField,
  lspFoldingExtension,
  selectionLineDecorationExtension,
  semanticDecorationExtension,
  setExecutionFlowDecorations,
  setFoldingRanges,
  setSemanticDecorations,
  setSignatureHelp,
  setSourceAssociationDecorations,
  signatureHelpField,
  sourceAssociationDecorationField,
} from './codeMirrorDecorations'
import {
  codeMirrorLanguageExtension,
  semanticTokenCssClass,
  visualStudioLightEditorTheme,
  visualStudioLightHighlightStyle,
} from './codeMirrorLanguage'
import type { EditorFontSize } from './editorPreference'
import { ilStandaloneAssemblyIdentityNameRange } from './ilLanguageTokens'
import { sourceMethodFromDocumentSymbols } from './lspDocumentSymbols'
import { findSourceMethodAtLine, type SourceMethodSelection } from './sourceMethod'
import './CodeMirrorEditor.css'

export interface CodeMirrorWorkspaceFile {
  path: string
  text: string
}

export interface CodeMirrorLanguageSessionOptions {
  enabled: boolean
  resolution: ResolveSelectionResponse | null
  languageId: string
  toolchainId: string | null
  referenceSetId: string | null
  buildMode: BuildConfiguration
  workspaceRevision: number
  selectionRevision: number
  sourceOrder: readonly string[]
}

export interface CodeMirrorEditorProps {
  files: readonly CodeMirrorWorkspaceFile[]
  activeFile: string
  languageSession: CodeMirrorLanguageSessionOptions
  executionFlow: ExecutionFlowSourceModel | null
  sourceAssociations?: readonly SourceAssociation[]
  activeSourceAssociationKey?: string | null
  sourceNavigation: ExecutionFlowNavigationRequest | null
  fontSize: EditorFontSize
  onChange: (path: string, value: string) => void
  onSourceAssociationActivate?: ((associationKey: string) => void) | undefined
  onSourceAssociationPreview?: ((associationKey: string) => void) | undefined
  onLanguageSessionStatus?: (change: LanguageSessionStatusChange) => void
  onCursorMethodChange?: (selection: SourceMethodSelection | null) => void
}

export const codeMirrorCompletionKeymap: readonly KeyBinding[] = [
  { key: 'Ctrl-Space', run: startCompletion },
  { key: 'Tab', run: acceptCompletion },
  ...defaultCompletionKeymap.filter(
    (binding) => binding.key !== 'Ctrl-Space' && binding.key !== 'Tab',
  ),
]

export function advanceSnippetWithEnter(view: EditorView): boolean {
  if (!hasNextSnippetField(view.state)) return false
  return nextSnippetField(view)
}

export const codeMirrorEditorKeymap: readonly KeyBinding[] = [
  { key: 'Enter', run: advanceSnippetWithEnter },
  ...closeBracketsKeymap,
  ...codeMirrorCompletionKeymap,
  ...defaultKeymap,
  ...searchKeymap,
  ...historyKeymap,
  ...foldKeymap,
  indentWithTab,
]

interface CodeMirrorFileModel {
  path: string
  state: EditorState
  version: number
  languageId: string
  language: Compartment
  scrollTop: number
  scrollLeft: number
  documentSymbols: readonly CodeMirrorDocumentSymbol[] | null | undefined
}

interface LatestEditorState extends CodeMirrorEditorProps {}

const externalDocumentUpdate = Annotation.define<boolean>()

export function CodeMirrorEditor(props: CodeMirrorEditorProps) {
  const containerRef = useRef<HTMLElement>(null)
  const editorRef = useRef<EditorView | null>(null)
  const modelsRef = useRef(new Map<string, CodeMirrorFileModel>())
  const activePathRef = useRef(props.activeFile)
  const bridgeRef = useRef(new CodeMirrorLanguageBridge())
  const controllerRef = useRef<LanguageSessionLifecycle | null>(null)
  const onChangeRef = useRef(props.onChange)
  const onStatusRef = useRef(props.onLanguageSessionStatus)
  const onCursorMethodChangeRef = useRef(props.onCursorMethodChange)
  const languageStatusRef = useRef<LanguageSessionStatusChange['status']>('disabled')
  const languageSessionKeyRef = useRef<string | null>(null)
  const outputKindRef = useRef<RememberedWorkbenchOutputKind | null>(null)
  const workspaceIdRef = useRef(createWorkspaceId())
  const previewedSourceAssociationKeyRef = useRef<string | null>(null)
  const sourceAssociationDecorationsPresentRef = useRef(false)
  const sourceAssociationMouseUpTimerRef = useRef<number | null>(null)
  const sourceAssociationActivationTimerRef = useRef<number | null>(null)
  const latestRef = useRef<LatestEditorState>(props)

  onChangeRef.current = props.onChange
  onStatusRef.current = props.onLanguageSessionStatus
  onCursorMethodChangeRef.current = props.onCursorMethodChange
  latestRef.current = props

  // The editor is mounted once; every callback reads mutable refs updated on each render.
  // biome-ignore lint/correctness/useExhaustiveDependencies: remounting loses editor view state.
  useEffect(() => {
    const container = containerRef.current
    if (!container) return

    const initial = latestRef.current.files.find(
      (file) => file.path === latestRef.current.activeFile,
    ) ??
      latestRef.current.files[0] ?? { path: latestRef.current.activeFile, text: '' }
    const model = createFileModel(
      initial.path,
      initial.text,
      latestRef.current.languageSession.languageId,
      bridgeRef.current,
      handleViewUpdate,
      () => latestRef.current.languageSession.languageId,
    )
    modelsRef.current.set(initial.path, model)
    activePathRef.current = initial.path

    const view = new EditorView({ state: model.state, parent: container })
    editorRef.current = view
    const clearSourceAssociationActivation = () => {
      if (sourceAssociationActivationTimerRef.current === null) return
      window.clearTimeout(sourceAssociationActivationTimerRef.current)
      sourceAssociationActivationTimerRef.current = null
    }
    const clearSourceAssociationMouseUp = () => {
      if (sourceAssociationMouseUpTimerRef.current === null) return
      window.clearTimeout(sourceAssociationMouseUpTimerRef.current)
      sourceAssociationMouseUpTimerRef.current = null
    }
    let sourceAssociationPointerDown: { position: number; from: number; to: number } | null = null
    const positionAtMouseEvent = (event: MouseEvent): number | null => {
      try {
        const position = view.posAtCoords({ x: event.clientX, y: event.clientY })
        if (position !== null) return position
      } catch {
        // DOM coordinates are unavailable in some synthetic editor environments.
      }
      const target = event.target instanceof Node ? event.target : null
      if (!target) return null
      try {
        return view.posAtDOM(target, 0)
      } catch {
        return null
      }
    }
    const sourceAssociationMouseDown = (event: MouseEvent) => {
      sourceAssociationPointerDown = null
      if (event.button !== 0 || event.detail > 1) return
      const selection = view.state.selection.main
      if (selection.empty) return
      const position = positionAtMouseEvent(event)
      if (position === null || position < selection.from || position > selection.to) return
      sourceAssociationPointerDown = { position, from: selection.from, to: selection.to }
    }
    const sourceAssociationMouseUp = (event: MouseEvent) => {
      if (event.button !== 0) return
      clearSourceAssociationActivation()
      const pointerDown = sourceAssociationPointerDown
      sourceAssociationPointerDown = null
      const pointerUpPosition = positionAtMouseEvent(event)
      const target = event.target instanceof Element ? event.target : null
      const line = target?.closest('.cm-source-association-line')
      const clickedAssociationLine = Boolean(line && view.contentDOM.contains(line))
      const detail = event.detail
      const eventPath = activePathRef.current
      clearSourceAssociationMouseUp()
      sourceAssociationMouseUpTimerRef.current = window.setTimeout(() => {
        sourceAssociationMouseUpTimerRef.current = null
        if (activePathRef.current !== eventPath) return

        const state = latestRef.current
        const associations = state.sourceAssociations ?? []
        const hasActiveRangeAssociations = associations.some(
          (association) => association.presentation === 'active-range',
        )
        let selection = view.state.selection.main
        if (
          detail <= 1 &&
          pointerDown &&
          pointerUpPosition === pointerDown.position &&
          !selection.empty &&
          selection.from === pointerDown.from &&
          selection.to === pointerDown.to
        ) {
          view.dispatch({
            selection: { anchor: pointerDown.position },
            userEvent: 'select.pointer',
          })
          selection = view.state.selection.main
        }
        const start = lspPositionAt(view.state.doc, selection.from)
        const end = lspPositionAt(view.state.doc, selection.to)

        if (hasActiveRangeAssociations) {
          if (detail > 1) return
          const association = sourceAssociationForSelection(associations, eventPath, {
            startLine: start.line + 1,
            startColumn: start.character + 1,
            endLine: end.line + 1,
            endColumn: end.character + 1,
          })
          if (!association) return
          clearSourceAssociationActivation()
          if (!selection.empty) {
            state.onSourceAssociationActivate?.(association.key)
            return
          }
          sourceAssociationActivationTimerRef.current = window.setTimeout(() => {
            sourceAssociationActivationTimerRef.current = null
            latestRef.current.onSourceAssociationActivate?.(association.key)
          }, 400)
          return
        }

        if (!selection.empty || detail > 1 || !clickedAssociationLine) return
        const position = selection.head
        const association = associations.find((candidate) => {
          if (candidate.documentPath !== eventPath) return false
          const from = oneBasedOffset(
            view.state.doc,
            candidate.range.startLine,
            candidate.range.startColumn,
          )
          const to = oneBasedOffset(
            view.state.doc,
            candidate.range.endLine,
            candidate.range.endColumn,
          )
          return from !== null && to !== null && position >= from && position < to
        })
        if (!association) return
        clearSourceAssociationActivation()
        sourceAssociationActivationTimerRef.current = window.setTimeout(() => {
          sourceAssociationActivationTimerRef.current = null
          latestRef.current.onSourceAssociationActivate?.(association.key)
        }, 400)
      }, 0)
    }
    // A text drag ends with mouseup but browsers may suppress the following click.
    // Resolve the completed selection here so AST range navigation works reliably.
    view.contentDOM.addEventListener('mousedown', sourceAssociationMouseDown, true)
    view.contentDOM.addEventListener('mouseup', sourceAssociationMouseUp)
    const scrollListener = () => {
      const current = modelsRef.current.get(activePathRef.current)
      if (!current) return
      current.scrollTop = view.scrollDOM.scrollTop
      current.scrollLeft = view.scrollDOM.scrollLeft
    }
    view.scrollDOM.addEventListener('scroll', scrollListener, { passive: true })

    const sink: CodeMirrorLanguageSink = {
      publishDiagnostics: applyDiagnostics,
      publishSemanticTokens: applySemanticTokens,
      publishDocumentSymbols: applyDocumentSymbols,
      publishFoldingRanges: applyFoldingRanges,
      clearDocument: clearLanguageDocument,
    }
    const controller = new LanguageSessionLifecycle(
      handleLanguageStatus,
      createCodeMirrorLanguageSessionDependencies(bridgeRef.current, sink),
    )
    controllerRef.current = controller

    const resizeObserver =
      typeof ResizeObserver === 'undefined'
        ? null
        : new ResizeObserver(() => editorRef.current?.requestMeasure())
    resizeObserver?.observe(container)
    emitCursorMethod(model.state)

    return () => {
      resizeObserver?.disconnect()
      clearSourceAssociationMouseUp()
      clearSourceAssociationActivation()
      view.contentDOM.removeEventListener('mousedown', sourceAssociationMouseDown, true)
      view.contentDOM.removeEventListener('mouseup', sourceAssociationMouseUp)
      view.scrollDOM.removeEventListener('scroll', scrollListener)
      controllerRef.current = null
      void controller.dispose()
      editorRef.current = null
      modelsRef.current.clear()
      view.destroy()
    }
  }, [])

  // Model helpers only read editor refs; editor state changes are intentionally not effect inputs.
  // biome-ignore lint/correctness/useExhaustiveDependencies: helper identities do not own state.
  useEffect(() => {
    const view = editorRef.current
    if (!view) return
    const nextPaths = new Set(props.files.map((file) => file.path))
    for (const path of modelsRef.current.keys()) {
      if (!nextPaths.has(path)) modelsRef.current.delete(path)
    }

    for (const file of props.files) {
      let model = modelsRef.current.get(file.path)
      if (!model) {
        model = createFileModel(
          file.path,
          file.text,
          props.languageSession.languageId,
          bridgeRef.current,
          handleViewUpdate,
          () => latestRef.current.languageSession.languageId,
        )
        modelsRef.current.set(file.path, model)
      }
      if (model.languageId !== props.languageSession.languageId) {
        applyModelTransaction(model, {
          effects: model.language.reconfigure(
            codeMirrorLanguageExtension(props.languageSession.languageId),
          ),
        })
        model.languageId = props.languageSession.languageId
      }
      if (model.state.doc.toString() !== file.text) {
        applyModelTransaction(model, {
          changes: { from: 0, to: model.state.doc.length, insert: file.text },
          annotations: externalDocumentUpdate.of(true),
          effects: setSignatureHelp.of(null),
        })
        model.version += 1
        if (model.documentSymbols !== undefined) model.documentSymbols = null
        bridgeRef.current.changeDocument(model.path, file.text, model.version)
      }
    }

    const next = modelsRef.current.get(props.activeFile)
    if (!next) return
    if (activePathRef.current !== props.activeFile || view.state !== next.state) {
      const previous = modelsRef.current.get(activePathRef.current)
      if (previous) {
        previous.state = view.state.update({ effects: setSignatureHelp.of(null) }).state
        previous.scrollTop = view.scrollDOM.scrollTop
        previous.scrollLeft = view.scrollDOM.scrollLeft
      }
      activePathRef.current = props.activeFile
      next.state = next.state.update({ effects: setSignatureHelp.of(null) }).state
      view.setState(next.state)
      view.scrollDOM.scrollTop = next.scrollTop
      view.scrollDOM.scrollLeft = next.scrollLeft
    }
    emitCursorMethod(next.state)
  }, [props.activeFile, props.files, props.languageSession.languageId])

  // biome-ignore lint/correctness/useExhaustiveDependencies: the CSS font-size variable changes outside the editor state.
  useEffect(() => {
    editorRef.current?.requestMeasure()
  }, [props.fontSize])

  // Decorations are reapplied only when the execution-flow result changes.
  // biome-ignore lint/correctness/useExhaustiveDependencies: transaction helper reads current refs.
  useEffect(() => {
    const hits = props.executionFlow?.hits ?? []
    const hitsByPath = new Map<string, ExecutionFlowSourceHit[]>()
    for (const hit of hits) {
      const existing = hitsByPath.get(hit.documentPath)
      if (existing) existing.push(hit)
      else hitsByPath.set(hit.documentPath, [hit])
    }
    for (const model of modelsRef.current.values()) {
      const decorations = (hitsByPath.get(model.path) ?? []).flatMap((hit) => {
        const range = executionFlowRange(model.state.doc, hit)
        return range ? [range] : []
      })
      applyModelTransaction(model, { effects: setExecutionFlowDecorations.of(decorations) })
    }
  }, [props.executionFlow])

  // biome-ignore lint/correctness/useExhaustiveDependencies: transaction helper reads current editor models through refs.
  useEffect(() => {
    const linkedAssociations = (props.sourceAssociations ?? []).filter(
      isLinkedLineSourceAssociation,
    )
    if (linkedAssociations.length === 0 && !sourceAssociationDecorationsPresentRef.current) return

    const associationsByPath = new Map<string, SourceAssociation[]>()
    for (const association of linkedAssociations) {
      const existing = associationsByPath.get(association.documentPath)
      if (existing) existing.push(association)
      else associationsByPath.set(association.documentPath, [association])
    }
    let hasDecorations = false
    for (const model of modelsRef.current.values()) {
      const associations = (associationsByPath.get(model.path) ?? []).filter(
        (association) => sourceAssociationOffsets(model.state.doc, association) !== null,
      )
      const decorations = [
        ...sourceAssociationLines(associations, props.activeSourceAssociationKey).map(
          ({ lineNumber, association, active }) =>
            sourceAssociationLineRange(model.state.doc, lineNumber, association, active),
        ),
        ...associations.flatMap((association) => {
          const range = sourceAssociationOffsets(model.state.doc, association)
          const active = association.key === props.activeSourceAssociationKey
          return range
            ? [
                {
                  ...range,
                  className: [
                    'cm-source-association-range',
                    active ? 'cm-source-association-exact-active' : '',
                    active ? sourceAssociationClass(association.colorIndex) : '',
                  ]
                    .filter(Boolean)
                    .join(' '),
                },
              ]
            : []
        }),
      ]
      if (decorations.length > 0) hasDecorations = true
      applyModelTransaction(model, { effects: setSourceAssociationDecorations.of(decorations) })
    }
    sourceAssociationDecorationsPresentRef.current = hasDecorations
  }, [props.activeSourceAssociationKey, props.sourceAssociations])

  // biome-ignore lint/correctness/useExhaustiveDependencies: association identity resets preview state.
  useEffect(() => {
    previewedSourceAssociationKeyRef.current = null
  }, [props.sourceAssociations])

  // Navigation is keyed by the immutable navigation request and active file.
  // biome-ignore lint/correctness/useExhaustiveDependencies: cursor callback is read through a ref.
  useEffect(() => {
    const navigation = props.sourceNavigation
    const view = editorRef.current
    if (!navigation || !view || props.activeFile !== navigation.documentPath) return
    const model = modelsRef.current.get(navigation.documentPath)
    if (!model || validateSourceRange(model.state.doc.toString(), navigation.range)) return
    const from = oneBasedOffset(
      model.state.doc,
      navigation.range.startLine,
      navigation.range.startColumn,
    )
    const to = oneBasedOffset(model.state.doc, navigation.range.endLine, navigation.range.endColumn)
    if (from === null || to === null || to < from) return
    view.dispatch({
      selection: { anchor: from, head: to },
      effects: EditorView.scrollIntoView(from, { y: 'center' }),
    })
    view.focus()
    emitCursorMethod(view.state)
  }, [props.activeFile, props.sourceNavigation])

  useEffect(() => {
    const controller = controllerRef.current
    if (!controller) return
    const session = props.languageSession
    if (!session.enabled || !session.toolchainId || !session.referenceSetId) {
      languageSessionKeyRef.current = null
      outputKindRef.current = null
      bridgeRef.current.setSessionStatus?.('disabled')
      controller.update(null)
      return
    }

    const resolution = session.resolution
    const resolutionMatches =
      resolution?.effectiveSelection.languageId === session.languageId &&
      resolution.effectiveSelection.toolchainId === session.toolchainId &&
      resolution.effectiveSelection.referenceSetId === session.referenceSetId
    const resolvedOutputKind =
      resolution && resolutionMatches
        ? buildOutputKindForResolvedPipeline(session.languageId, resolution.pipelinePlan.stages)
        : null
    const retainedOutputKind = retainResolvedWorkbenchOutputKind(
      {
        languageId: session.languageId,
        toolchainId: session.toolchainId,
        referenceSetId: session.referenceSetId,
        buildMode: session.buildMode,
        selectionRevision: session.selectionRevision,
      },
      resolvedOutputKind,
      outputKindRef.current,
    )
    outputKindRef.current = retainedOutputKind.remembered
    const key = createLanguageSessionKey({
      languageId: session.languageId,
      toolchainId: session.toolchainId,
      referenceSetId: session.referenceSetId,
      buildMode: session.buildMode,
      outputKind: retainedOutputKind.outputKind,
      selectionRevision: session.selectionRevision,
      filePaths: props.files.map((file) => file.path),
      sourceOrder: session.sourceOrder,
    })
    if (languageSessionKeyRef.current !== key || languageStatusRef.current !== 'ready') {
      bridgeRef.current.setSessionStatus?.('connecting')
    }
    languageSessionKeyRef.current = key
    controller.update({
      key,
      plan:
        resolution && resolutionMatches
          ? {
              key,
              languageId: session.languageId,
              modelLanguageId: session.languageId,
              workspaceUri: createLanguageWorkspaceUri(session.languageId, workspaceIdRef.current),
              selectionRevision: session.selectionRevision,
              createRequest: () =>
                createLanguageSessionRequest(latestRef.current, modelsRef.current),
            }
          : null,
    })
  }, [props.files, props.languageSession])

  return (
    <section
      ref={containerRef}
      className="codemirror-host"
      aria-label={executionFlowAriaLabel(props.executionFlow)}
      data-editor="codemirror"
      data-language-service-status="disabled"
      style={{ '--editor-font-size': `${props.fontSize}px` } as CSSProperties}
    />
  )

  function createFileModel(
    path: string,
    text: string,
    languageId: string,
    bridge: CodeMirrorLanguageBridge,
    onUpdate: (path: string, update: ViewUpdate) => void,
    currentLanguageId: () => string,
  ): CodeMirrorFileModel {
    const language = new Compartment()
    return {
      path,
      version: 1,
      languageId,
      language,
      scrollTop: 0,
      scrollLeft: 0,
      documentSymbols: undefined,
      state: EditorState.create({
        doc: text,
        extensions: codeMirrorExtensions(
          path,
          language,
          languageId,
          currentLanguageId,
          bridge,
          onUpdate,
          (candidatePath) =>
            activePathRef.current === candidatePath
              ? (modelsRef.current.get(candidatePath)?.version ?? null)
              : null,
        ),
      }),
    }
  }

  function handleViewUpdate(path: string, update: ViewUpdate): void {
    const model = modelsRef.current.get(path)
    if (!model) return
    model.state = update.state
    const external = update.transactions.some(
      (transaction) => transaction.annotation(externalDocumentUpdate) === true,
    )
    if (update.docChanged && !external) {
      model.version += 1
      if (model.documentSymbols !== undefined) model.documentSymbols = null
      const text = update.state.doc.toString()
      onChangeRef.current(path, text)
      bridgeRef.current.changeDocument(path, text, model.version)
      const trigger = signatureTrigger(update)
      if (trigger === '(' || trigger === ',') {
        void requestSignatureHelp(model, update.state, trigger)
      }
    }
    if (
      path === activePathRef.current &&
      (update.docChanged || update.selectionSet || update.focusChanged)
    ) {
      emitCursorMethod(update.state)
    }
    if (
      path === activePathRef.current &&
      update.selectionSet &&
      update.transactions.some((transaction) => transaction.isUserEvent('select'))
    ) {
      previewSourceAssociation(update.state)
    }
  }

  function previewSourceAssociation(state: EditorState): void {
    const props = latestRef.current
    if (!props.onSourceAssociationPreview) return
    const associations = props.sourceAssociations ?? []
    if (!associations.some((association) => association.presentation === 'active-range')) return
    const selection = state.selection.main
    if (selection.empty) {
      previewedSourceAssociationKeyRef.current = null
      return
    }
    const start = lspPositionAt(state.doc, selection.from)
    const end = lspPositionAt(state.doc, selection.to)
    const association = sourceAssociationForSelection(associations, activePathRef.current, {
      startLine: start.line + 1,
      startColumn: start.character + 1,
      endLine: end.line + 1,
      endColumn: end.character + 1,
    })
    if (!association || association.key === previewedSourceAssociationKeyRef.current) return
    previewedSourceAssociationKeyRef.current = association.key
    props.onSourceAssociationPreview(association.key)
  }

  function applyModelTransaction(model: CodeMirrorFileModel, spec: TransactionSpec): void {
    const view = editorRef.current
    if (view && activePathRef.current === model.path && view.state === model.state) {
      view.dispatch(spec)
      model.state = view.state
      return
    }
    model.state = model.state.update(spec).state
  }

  function applyDiagnostics(
    path: string,
    version: number | undefined,
    diagnostics: readonly CodeMirrorLspDiagnostic[],
  ): void {
    const model = modelsRef.current.get(path)
    if (!model || (version !== undefined && version !== model.version)) return
    applyModelTransaction(
      model,
      setDiagnostics(
        model.state,
        codeMirrorDiagnostics(model.state.doc, diagnostics, (action) => applyCodeAction(action)),
      ),
    )
  }

  function applySemanticTokens(
    path: string,
    version: number,
    tokens: readonly CodeMirrorSemanticToken[],
  ): void {
    const model = modelsRef.current.get(path)
    if (!model || version !== model.version) return
    applyModelTransaction(model, {
      effects: setSemanticDecorations.of(semanticDecorationRanges(model.state.doc, tokens)),
    })
  }

  function applyDocumentSymbols(
    path: string,
    version: number,
    symbols: readonly CodeMirrorDocumentSymbol[] | null,
  ): void {
    const model = modelsRef.current.get(path)
    if (!model || version !== model.version) return
    model.documentSymbols = symbols ?? undefined
    if (path === activePathRef.current) emitCursorMethod(model.state)
  }

  function applyFoldingRanges(
    path: string,
    version: number,
    ranges: readonly CodeMirrorLspFoldingRange[] | null,
  ): void {
    const model = modelsRef.current.get(path)
    if (!model || version !== model.version) return
    applyModelTransaction(model, {
      effects: setFoldingRanges.of(ranges ? codeMirrorFoldingRanges(model.state.doc, ranges) : []),
    })
  }

  async function requestSignatureHelp(
    model: CodeMirrorFileModel,
    state: EditorState,
    trigger: '(' | ',',
  ): Promise<void> {
    const version = model.version
    const position = state.selection.main.head
    const help = await bridgeRef.current.signatureHelp(
      model.path,
      lspPositionAt(state.doc, position),
      trigger,
    )
    if (!help || model.version !== version || activePathRef.current !== model.path) return
    const presentation = signaturePresentation(help, position)
    if (!presentation) return
    applyModelTransaction(model, { effects: setSignatureHelp.of(presentation) })
  }

  function applyCodeAction(action: CodeMirrorLspCodeAction): void {
    const planned: Array<{ model: CodeMirrorFileModel; changes: readonly ChangeSpec[] }> = []
    for (const documentEdit of action.documentEdits) {
      const model = modelsRef.current.get(documentEdit.documentPath)
      if (!model || model.version !== documentEdit.documentVersion) return
      const changes = codeMirrorTextChanges(model.state.doc, documentEdit.edits)
      if (!changes) return
      planned.push({ model, changes })
    }
    for (const { model, changes } of planned) {
      const view = editorRef.current
      if (view && activePathRef.current === model.path && view.state === model.state) {
        view.dispatch({ changes })
        model.state = view.state
      } else {
        model.state = model.state.update({ changes }).state
        model.version += 1
        if (model.documentSymbols !== undefined) model.documentSymbols = null
        const text = model.state.doc.toString()
        onChangeRef.current(model.path, text)
        bridgeRef.current.changeDocument(model.path, text, model.version)
      }
    }
  }

  function handleLanguageStatus(change: LanguageSessionStatusChange): void {
    bridgeRef.current.setSessionStatus?.(change.status)
    languageStatusRef.current = change.status
    if (containerRef.current) {
      containerRef.current.dataset.languageServiceStatus = change.status
    }
    if (
      change.status === 'connecting' ||
      change.status === 'reconnecting' ||
      change.status === 'disabled' ||
      change.status === 'error'
    ) {
      const pending = change.status === 'connecting' || change.status === 'reconnecting'
      for (const model of modelsRef.current.values()) {
        model.documentSymbols = pending ? null : undefined
        applyModelTransaction(model, {
          effects: [setFoldingRanges.of([]), setSignatureHelp.of(null)],
        })
      }
      const active = modelsRef.current.get(activePathRef.current)
      if (active) emitCursorMethod(active.state)
    }
    if (change.status === 'ready') {
      const active = modelsRef.current.get(activePathRef.current)
      if (active) emitCursorMethod(active.state)
    }
    onStatusRef.current?.(change)
  }

  function clearLanguageDocument(path: string): void {
    const model = modelsRef.current.get(path)
    if (!model) return
    model.documentSymbols = undefined
    applyModelTransaction(model, setDiagnostics(model.state, []))
    applyModelTransaction(model, {
      effects: [setSemanticDecorations.of([]), setFoldingRanges.of([]), setSignatureHelp.of(null)],
    })
    if (path === activePathRef.current) emitCursorMethod(model.state)
  }

  function emitCursorMethod(state: EditorState): void {
    const model = modelsRef.current.get(activePathRef.current)
    const position = lspPositionAt(state.doc, state.selection.main.head)
    const syntaxSelection = findSourceMethodAtLine(
      state.doc.toString(),
      latestRef.current.languageSession.languageId,
      position.line + 1,
      activePathRef.current,
    )
    const topLevelSelection = syntaxSelection?.name === '<Main>$' ? syntaxSelection : null
    const selection = Array.isArray(model?.documentSymbols)
      ? (sourceMethodFromDocumentSymbols(
          model.documentSymbols,
          position,
          latestRef.current.languageSession.languageId,
        ) ?? topLevelSelection)
      : model?.documentSymbols === null || languageStatusRef.current === 'ready'
        ? topLevelSelection
        : syntaxSelection
    onCursorMethodChangeRef.current?.(selection)
  }
}

function codeMirrorExtensions(
  path: string,
  language: Compartment,
  languageId: string,
  currentLanguageId: () => string,
  bridge: CodeMirrorLanguageBridge,
  onUpdate: (path: string, update: ViewUpdate) => void,
  activeDocumentVersion: (path: string) => number | null,
): Extension[] {
  return [
    lineNumbers(),
    highlightActiveLineGutter(),
    highlightSpecialChars(),
    history(),
    foldGutter({
      openText: 'v',
      closedText: '>',
      foldingChanged: (update) =>
        update.transactions.some((transaction) =>
          transaction.effects.some((effect) => effect.is(setFoldingRanges)),
        ),
    }),
    drawSelection(),
    selectionLineDecorationExtension,
    dropCursor(),
    EditorState.allowMultipleSelections.of(true),
    indentOnInput(),
    syntaxHighlighting(visualStudioLightHighlightStyle),
    visualStudioLightEditorTheme,
    bracketMatching(),
    closeBrackets(),
    autocompletion({
      override: [completionSource(path, currentLanguageId, bridge, activeDocumentVersion)],
      defaultKeymap: false,
      interactionDelay: 0,
    }),
    rectangularSelection(),
    highlightActiveLine(),
    lintGutter(),
    semanticDecorationExtension,
    executionFlowDecorationField,
    sourceAssociationDecorationField,
    signatureHelpField,
    lspFoldingExtension,
    language.of(codeMirrorLanguageExtension(languageId)),
    hoverTooltip(hoverSource(path, bridge), { hideOnChange: true, hoverTime: 300 }),
    EditorView.contentAttributes.of({
      'aria-label': 'Source editor',
      autocapitalize: 'off',
      autocomplete: 'off',
      autocorrect: 'off',
      spellcheck: 'false',
    }),
    EditorState.tabSize.of(4),
    indentUnit.of('    '),
    keymap.of(codeMirrorEditorKeymap),
    EditorView.updateListener.of((update) => onUpdate(path, update)),
  ]
}

export function completionSource(
  path: string,
  currentLanguageId: () => string,
  bridge: CodeMirrorLanguageBridge,
  activeDocumentVersion: (path: string) => number | null,
) {
  // CodeMirror re-runs a source after typing when the server marks its list
  // incomplete. Keep that bit of protocol state local to this source so the
  // follow-up request uses LSP TriggerForIncompleteCompletions (3).
  let previousCompletionIncomplete = false
  let previousLanguageId: string | null = null

  return async (context: CompletionContext) => {
    const languageId = currentLanguageId()
    if (previousLanguageId !== languageId) {
      previousCompletionIncomplete = false
      previousLanguageId = languageId
    }
    const word = context.matchBefore(/[A-Za-z_][\w']*$/)
    const previous =
      context.pos > 0 ? context.state.doc.sliceString(context.pos - 1, context.pos) : ''
    const triggerCharacter = codeMirrorCompletionTriggerCharacter(languageId, previous)
    if (!context.explicit && !word && !triggerCharacter) {
      previousCompletionIncomplete = false
      return null
    }
    const position = lspPositionAt(context.state.doc, context.pos)
    const triggerKind = codeMirrorCompletionTriggerKind(
      languageId,
      previous,
      context.explicit,
      previousCompletionIncomplete,
    )
    previousCompletionIncomplete = false
    const completionList = await bridge.completion(path, {
      ...position,
      triggerKind,
      ...(triggerCharacter && !context.explicit ? { triggerCharacter } : {}),
    })
    if (!completionList || context.aborted) return null
    previousCompletionIncomplete = completionList.isIncomplete
    const validFor = codeMirrorCompletionValidFor(
      completionList.isIncomplete,
      completionList.items.length,
    )
    return {
      from: word?.from ?? context.pos,
      options: completionList.items.map((item) =>
        codeMirrorCompletion(item, path, bridge, activeDocumentVersion),
      ),
      ...(validFor ? { validFor } : {}),
    }
  }
}

export function codeMirrorCompletionTriggerCharacter(
  languageId: string,
  previousCharacter: string,
): string | undefined {
  const triggerCharacters =
    languageId === 'il' ? ilSenseCompletionTriggerCharacters : ['.', ':', '<']
  return triggerCharacters.some((candidate) => candidate === previousCharacter)
    ? previousCharacter
    : undefined
}

export function codeMirrorCompletionTriggerKind(
  languageId: string,
  previousCharacter: string,
  explicit: boolean,
  previousResultIncomplete: boolean,
): 1 | 2 | 3 {
  if (explicit) return 1
  if (codeMirrorCompletionTriggerCharacter(languageId, previousCharacter)) return 2
  if (!explicit && previousResultIncomplete) return 3
  return 1
}

export function codeMirrorCompletionValidFor(
  isIncomplete: boolean,
  itemCount = 1,
): RegExp | undefined {
  return isIncomplete || itemCount === 0 ? undefined : /^[\w']*$/
}

export function codeMirrorCompletion(
  item: CodeMirrorLspCompletionItem,
  path: string,
  bridge: CodeMirrorLanguageBridge,
  activeDocumentVersion: (path: string) => number | null = () => item.documentVersion,
): Completion {
  const inserted = completionText(item)
  const info = documentationText(item.documentation)
  const type = completionType(item.kind)
  const matchingLabel = item.filterText && item.filterText.length > 0 ? item.filterText : item.label
  const completion: Completion = {
    // CodeMirror matches against Completion.label. LSP's filterText is the
    // matching key, while label may include display-only assembly/type text.
    label: matchingLabel,
    ...(matchingLabel === item.label ? {} : { displayLabel: item.label }),
    ...(item.sortText ? { sortText: item.sortText } : {}),
    ...(item.detail ? { detail: item.detail } : {}),
    ...(info ? { info } : {}),
    apply: (view, selected, from, to) => {
      const document = view.state.doc
      const selection = view.state.selection
      const documentVersion = activeDocumentVersion(path)
      const canResolve = documentVersion === item.documentVersion
      closeCompletion(view)
      if (documentVersion === null) return
      const hasInitialEdits =
        item.textEdit !== undefined || (item.additionalTextEdits?.length ?? 0) > 0
      if (
        canResolve &&
        (item.insertTextFormat === 2 || hasInitialEdits) &&
        applyResolvedCompletion(view, selected, item, document, from, to)
      ) {
        return
      }

      void (async () => {
        let resolved: CodeMirrorLspCompletionItem | null = item
        if (canResolve) {
          try {
            resolved = await bridge.resolveCompletion(path, item)
          } catch {
            resolved = item
          }
        }

        if (
          activeDocumentVersion(path) !== documentVersion ||
          !view.state.doc.eq(document) ||
          !view.state.selection.eq(selection)
        ) {
          return
        }

        if (
          canResolve &&
          resolved &&
          resolved !== item &&
          applyResolvedCompletion(view, selected, resolved, document, from, to)
        ) {
          return
        }

        applyCompletionText(view, selected, inserted, item.insertTextFormat, from, to)
      })()
    },
    ...(type ? { type } : {}),
    ...(item.filterText ? { displayLabel: item.label } : {}),
  }
  return completion
}

interface CompletionTextChange {
  from: number
  to: number
  insert: string
  primary: boolean
}

function applyResolvedCompletion(
  view: EditorView,
  completion: Completion,
  item: CodeMirrorLspCompletionItem,
  document: Text,
  from: number,
  to: number,
): boolean {
  const primaryRange = item.textEdit ? offsetsForRange(document, item.textEdit.range) : { from, to }
  if (!primaryRange || primaryRange.from < 0 || primaryRange.to > document.length) return false

  const inserted = completionText(item)
  const primary: CompletionTextChange = {
    from: primaryRange.from,
    to: primaryRange.to,
    insert: item.insertTextFormat === 2 ? '' : inserted,
    primary: true,
  }
  const changes: CompletionTextChange[] = [primary]
  for (const edit of item.additionalTextEdits ?? []) {
    const range = offsetsForRange(document, edit.range)
    if (!range) return false
    changes.push({ from: range.from, to: range.to, insert: edit.newText, primary: false })
  }
  changes.sort((left, right) => left.from - right.from || left.to - right.to)
  for (let index = 1; index < changes.length; index += 1) {
    const previous = changes[index - 1]
    const current = changes[index]
    if (!previous || !current || completionChangesOverlap(previous, current)) return false
  }

  if (item.insertTextFormat === 2) {
    const additionalChanges = changes
      .filter((change) => !change.primary)
      .map(({ from: changeFrom, to: changeTo, insert }) => ({
        from: changeFrom,
        to: changeTo,
        insert,
      }))
    let mappedFrom = primary.from
    let mappedTo = primary.to
    if (additionalChanges.length > 0) {
      const changeSet = view.state.changes(additionalChanges)
      mappedFrom = changeSet.mapPos(primary.from, 1)
      mappedTo = changeSet.mapPos(primary.to, -1)
      view.dispatch({
        changes: additionalChanges,
        annotations: Transaction.userEvent.of('input.complete.additional'),
      })
    }
    applySnippet(inserted)(view, completion, mappedFrom, mappedTo)
    return true
  }

  let cursor = primary.from + primary.insert.length
  for (const change of changes) {
    if (!change.primary && change.to <= primary.from) {
      cursor += change.insert.length - (change.to - change.from)
    }
  }
  view.dispatch({
    changes: changes.map(({ from: changeFrom, to: changeTo, insert }) => ({
      from: changeFrom,
      to: changeTo,
      insert,
    })),
    selection: { anchor: cursor },
    scrollIntoView: true,
    annotations: [pickedCompletion.of(completion), Transaction.userEvent.of('input.complete')],
  })
  return true
}

function applyCompletionText(
  view: EditorView,
  completion: Completion,
  inserted: string,
  insertTextFormat: number | undefined,
  from: number,
  to: number,
): void {
  if (insertTextFormat === 2) {
    applySnippet(inserted)(view, completion, from, to)
    return
  }
  view.dispatch({
    ...insertCompletionText(view.state, inserted, from, to),
    annotations: pickedCompletion.of(completion),
  })
}

function completionChangesOverlap(
  left: CompletionTextChange,
  right: CompletionTextChange,
): boolean {
  if (left.from === left.to && right.from === right.to) return left.from === right.from
  if (left.from === left.to) return left.from > right.from && left.from < right.to
  if (right.from === right.to) return right.from > left.from && right.from < left.to
  return right.from < left.to
}

function completionText(item: CodeMirrorLspCompletionItem): string {
  const value = item.textEdit?.newText ?? item.insertText ?? item.label
  return item.insertTextFormat === 2 ? value : plainCompletionText(value)
}

function hoverSource(path: string, bridge: CodeMirrorLanguageBridge) {
  return async (view: EditorView, position: number): Promise<Tooltip | null> => {
    const hover = await bridge.hover(path, lspPositionAt(view.state.doc, position))
    const sections = codeMirrorHoverSections(hover)
    if (!hover || sections.length === 0) return null
    const range = hover.range ? offsetsForRange(view.state.doc, hover.range) : null
    const word = view.state.wordAt(position)
    return {
      pos: range?.from ?? word?.from ?? position,
      end: range?.to ?? word?.to ?? position,
      above: true,
      create: () => {
        const dom = document.createElement('div')
        dom.className = 'cm-lsp-hover'
        appendCodeMirrorHoverSections(dom, sections)
        return { dom }
      },
    }
  }
}

export function codeMirrorDiagnostics(
  document: Text,
  diagnostics: readonly CodeMirrorLspDiagnostic[],
  applyAction?: (action: CodeMirrorLspCodeAction) => void,
): Diagnostic[] {
  return diagnostics.flatMap((diagnostic) => {
    const range = offsetsForRange(document, diagnostic.range)
    if (!range) return []
    const to = range.to === range.from && range.from < document.length ? range.from + 1 : range.to
    const prefix = diagnostic.code === undefined ? '' : `[${String(diagnostic.code)}] `
    return [
      {
        from: range.from,
        to,
        severity: diagnosticSeverity(diagnostic.severity),
        message: `${prefix}${diagnostic.message}`,
        ...(diagnostic.source ? { source: diagnostic.source } : {}),
        ...(applyAction && diagnostic.actions && diagnostic.actions.length > 0
          ? { actions: diagnostic.actions.map((action) => diagnosticAction(action, applyAction)) }
          : {}),
      } satisfies Diagnostic,
    ]
  })
}

function diagnosticAction(
  action: CodeMirrorLspCodeAction,
  applyAction: (action: CodeMirrorLspCodeAction) => void,
): Action {
  return {
    name: action.title,
    ...(action.isPreferred ? { markClass: 'cm-lint-action-preferred' } : {}),
    apply: () => applyAction(action),
  }
}

export function semanticDecorationRanges(
  document: Text,
  tokens: readonly CodeMirrorSemanticToken[],
): CodeMirrorDecorationRange[] {
  return tokens.flatMap((token) => {
    const from = offsetForPosition(document, { line: token.line, character: token.character })
    if (from === null) return []
    const line = document.line(token.line + 1)
    const to = from + token.length
    if (to > line.to) return []
    const modifierClasses = token.tokenModifiers
      .map((modifier) => modifier.toLowerCase().replace(/[^a-z0-9-]/g, ''))
      .filter(Boolean)
      .map((modifier) => `cm-semantic-${modifier}`)
    return [
      {
        from,
        to,
        className: [
          'cm-semantic-token',
          `cm-semantic-${semanticTokenCssClass(token.tokenType)}`,
          ...modifierClasses,
        ].join(' '),
      },
    ]
  })
}

function offsetsForRange(document: Text, range: LspRange): { from: number; to: number } | null {
  const from = offsetForPosition(document, range.start)
  const to = offsetForPosition(document, range.end)
  return from !== null && to !== null && to >= from ? { from, to } : null
}

function offsetForPosition(document: Text, position: LspPosition): number | null {
  if (
    !Number.isSafeInteger(position.line) ||
    !Number.isSafeInteger(position.character) ||
    position.line < 0 ||
    position.character < 0 ||
    position.line >= document.lines
  ) {
    return null
  }
  const line = document.line(position.line + 1)
  return position.character <= line.length ? line.from + position.character : null
}

function lspPositionAt(document: Text, offset: number): LspPosition {
  const line = document.lineAt(offset)
  return { line: line.number - 1, character: offset - line.from }
}

function oneBasedOffset(document: Text, lineNumber: number, column: number): number | null {
  return offsetForPosition(document, { line: lineNumber - 1, character: column - 1 })
}

function executionFlowRange(
  document: Text,
  hit: ExecutionFlowSourceHit,
): CodeMirrorDecorationRange | null {
  if (validateSourceRange(document.toString(), hit.range)) return null
  const from = oneBasedOffset(document, hit.range.startLine, hit.range.startColumn)
  const to = oneBasedOffset(document, hit.range.endLine, hit.range.endColumn)
  if (from === null || to === null || to <= from) return null
  const suffix = hit.count === 1 ? 'event' : 'events'
  return {
    from,
    to,
    className: 'cm-execution-flow-range',
    title: `${hit.eventKind}: ${hit.count} ${suffix}`,
  }
}

function sourceAssociationOffsets(
  document: Text,
  association: SourceAssociation,
): Pick<CodeMirrorDecorationRange, 'from' | 'to'> | null {
  if (validateSourceRange(document.toString(), association.range)) return null
  const from = oneBasedOffset(document, association.range.startLine, association.range.startColumn)
  const to = oneBasedOffset(document, association.range.endLine, association.range.endColumn)
  if (from === null || to === null || to <= from) return null
  return { from, to }
}

function sourceAssociationLineRange(
  document: Text,
  lineNumber: number,
  association: SourceAssociation,
  active: boolean,
): CodeMirrorDecorationRange {
  const line = document.line(lineNumber)
  return {
    from: line.from,
    to: line.to,
    isLine: true,
    className: [
      'cm-source-association-line',
      active ? 'cm-source-association-line-active' : '',
      sourceAssociationClass(association.colorIndex),
    ]
      .filter(Boolean)
      .join(' '),
  }
}

function signatureTrigger(update: ViewUpdate): '(' | ',' | ')' | null {
  let trigger: '(' | ',' | ')' | null = null
  for (const transaction of update.transactions) {
    transaction.changes.iterChanges((_fromA, _toA, _fromB, _toB, inserted) => {
      for (const character of inserted.toString()) {
        if (character === '(' || character === ',' || character === ')') trigger = character
      }
    })
  }
  return trigger
}

export function signaturePresentation(
  help: CodeMirrorLspSignatureHelp,
  position: number,
): CodeMirrorSignaturePresentation | null {
  const signature = help.signatures[help.activeSignature]
  if (!signature) return null
  const parameter = signature.parameters[help.activeParameter]
  const activeParameterLabel = parameter
    ? typeof parameter.label === 'string'
      ? parameter.label
      : signature.label.slice(parameter.label[0], parameter.label[1])
    : undefined
  const documentation = documentationText(parameter?.documentation ?? signature.documentation)
  return {
    position,
    label: signature.label,
    ...(activeParameterLabel ? { activeParameterLabel } : {}),
    ...(documentation ? { documentation } : {}),
    activeSignature: help.activeSignature,
    signatureCount: help.signatures.length,
  }
}

export function codeMirrorFoldingRanges(
  document: Text,
  ranges: readonly CodeMirrorLspFoldingRange[],
): CodeMirrorFoldingRange[] {
  return ranges.flatMap((range) => {
    if (range.startLine >= document.lines || range.endLine >= document.lines) return []
    const startLine = document.line(range.startLine + 1)
    const endLine = document.line(range.endLine + 1)
    const from =
      range.startCharacter === undefined
        ? startLine.to
        : offsetForPosition(document, {
            line: range.startLine,
            character: range.startCharacter,
          })
    const to =
      range.endCharacter === undefined
        ? endLine.to
        : offsetForPosition(document, {
            line: range.endLine,
            character: range.endCharacter,
          })
    return from !== null && to !== null && to > from ? [{ from, to }] : []
  })
}

export function codeMirrorTextChanges(
  document: Text,
  edits: readonly { range: LspRange; newText: string }[],
): ChangeSpec[] | null {
  const changes = edits.flatMap((edit) => {
    const range = offsetsForRange(document, edit.range)
    return range ? [{ from: range.from, to: range.to, insert: edit.newText }] : []
  })
  if (changes.length !== edits.length) return null
  changes.sort((left, right) => left.from - right.from || left.to - right.to)
  for (let index = 1; index < changes.length; index += 1) {
    const previous = changes[index - 1]
    const current = changes[index]
    if (!previous || !current || current.from < previous.to) return null
  }
  return changes
}

function diagnosticSeverity(severity: number | undefined): Diagnostic['severity'] {
  switch (severity) {
    case 1:
      return 'error'
    case 2:
      return 'warning'
    case 4:
      return 'hint'
    default:
      return 'info'
  }
}

function completionType(kind: number | undefined): string | undefined {
  switch (kind) {
    case 2:
      return 'method'
    case 3:
      return 'function'
    case 4:
      return 'class'
    case 5:
      return 'property'
    case 6:
      return 'variable'
    case 7:
      return 'class'
    case 8:
      return 'interface'
    case 9:
      return 'namespace'
    case 10:
      return 'property'
    case 13:
      return 'enum'
    case 14:
      return 'keyword'
    case 20:
      return 'enum'
    case 21:
      return 'constant'
    case 22:
      return 'struct'
    case 23:
      return 'event'
    case 24:
      return 'operator'
    case 25:
      return 'type'
    default:
      return undefined
  }
}

function plainCompletionText(value: string): string {
  return value
    .replace(/\$\{\d+:([^}]*)\}/g, '$1')
    .replace(/\$\{\d+\}/g, '')
    .replace(/\$\d+/g, '')
}

function documentationText(value: unknown): string | null {
  if (typeof value === 'string') return value
  if (isRecord(value) && typeof value.value === 'string') return value.value
  return null
}

export interface CodeMirrorHoverSection {
  kind: 'code' | 'documentation'
  text: string
  language?: string
}

export function codeMirrorHoverSections(
  hover: CodeMirrorLspHover | null,
): CodeMirrorHoverSection[] {
  return hover ? markedContentSections(hover.contents) : []
}

function markedContentSections(value: unknown): CodeMirrorHoverSection[] {
  if (typeof value === 'string') return markdownHoverSections(value)
  if (Array.isArray(value)) {
    return value.flatMap(markedContentSections)
  }
  if (!isRecord(value) || typeof value.value !== 'string') return []
  if (typeof value.language === 'string') {
    const text = value.value.trim()
    return text ? [{ kind: 'code', text, language: value.language.trim() }] : []
  }
  return value.kind === 'markdown'
    ? markdownHoverSections(value.value)
    : documentationHoverSection(value.value)
}

function markdownHoverSections(value: string): CodeMirrorHoverSection[] {
  const source = value.replace(/\r\n?/g, '\n')
  const sections: CodeMirrorHoverSection[] = []
  const fence = /```([^\n`]*)\n([\s\S]*?)```/g
  let offset = 0
  for (let match = fence.exec(source); match; match = fence.exec(source)) {
    sections.push(...documentationHoverSection(source.slice(offset, match.index)))
    const text = (match[2] ?? '').replace(/\n$/, '').trimEnd()
    if (text) {
      const language = (match[1] ?? '').trim().split(/\s+/, 1)[0]
      sections.push({ kind: 'code', text, ...(language ? { language } : {}) })
    }
    offset = match.index + match[0].length
  }
  sections.push(...documentationHoverSection(source.slice(offset)))
  return sections
}

function documentationHoverSection(value: string): CodeMirrorHoverSection[] {
  const text = cleanMarkdownDocumentation(value)
  return text ? [{ kind: 'documentation', text }] : []
}

function cleanMarkdownDocumentation(value: string): string {
  return value
    .replace(/^\s{0,3}#{1,6}\s+/gm, '')
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/`([^`\n]+)`/g, '$1')
    .replace(/\*\*([^*\n]+)\*\*/g, '$1')
    .replace(/__([^_\n]+)__/g, '$1')
    .trim()
}

export function appendCodeMirrorHoverSections(
  parent: HTMLElement,
  sections: readonly CodeMirrorHoverSection[],
): void {
  for (const section of sections) {
    if (section.kind === 'code') {
      const code = document.createElement('code')
      const assemblyNameRange =
        section.language?.toLowerCase() === 'il'
          ? ilStandaloneAssemblyIdentityNameRange(section.text)
          : null
      if (assemblyNameRange) {
        code.append(section.text.slice(0, assemblyNameRange.from))
        const assemblyName = document.createElement('span')
        assemblyName.className = 'cm-lsp-hover-assembly'
        assemblyName.textContent = section.text.slice(assemblyNameRange.from, assemblyNameRange.to)
        code.append(assemblyName, section.text.slice(assemblyNameRange.to))
      } else {
        code.textContent = section.text
      }
      if (section.language) code.dataset.language = section.language
      const pre = document.createElement('pre')
      pre.className = 'cm-lsp-hover-code'
      pre.append(code)
      parent.append(pre)
      continue
    }
    const documentation = document.createElement('div')
    documentation.className = 'cm-lsp-hover-documentation'
    documentation.textContent = section.text
    parent.append(documentation)
  }
}

function executionFlowAriaLabel(model: ExecutionFlowSourceModel | null): string {
  if (!model || model.hits.length === 0) return 'Source editor'
  const count = model.hits.reduce((total, hit) => total + hit.count, 0)
  return `Source editor. Execution flow shows ${count} events across ${model.hits.length} source ranges.`
}

function createLanguageSessionRequest(
  state: LatestEditorState,
  models: ReadonlyMap<string, CodeMirrorFileModel>,
) {
  const resolution = state.languageSession.resolution
  const toolchainId = state.languageSession.toolchainId
  const referenceSetId = state.languageSession.referenceSetId
  if (!resolution || !toolchainId || !referenceSetId) {
    throw new Error('Language session selection is no longer resolved.')
  }
  const buildOptions = createWorkbenchBuildOptions(
    state.languageSession.languageId,
    state.languageSession.buildMode,
    resolution.pipelinePlan.stages,
  )
  return {
    requestId: `lsp_${globalThis.crypto?.randomUUID?.() ?? Date.now().toString(36)}`,
    pipelineResolutionId: resolution.pipelineResolutionId,
    languageId: state.languageSession.languageId,
    toolchainId,
    referenceSetId,
    workspace: {
      schemaVersion: 1,
      revision: state.languageSession.workspaceRevision,
      selectionRevision: state.languageSession.selectionRevision,
      languageId: state.languageSession.languageId,
      files: state.files.map((file) => {
        const model = models.get(file.path)
        return {
          path: file.path,
          version: model?.version ?? 1,
          text: model?.state.doc.toString() ?? file.text,
        }
      }),
      activeFile: state.activeFile,
      sourceOrder: [...state.languageSession.sourceOrder],
      referenceSetId,
      buildOptions,
    },
    lspVersion: '3.17' as const,
  }
}

function createWorkspaceId(): string {
  const value = globalThis.crypto?.randomUUID?.() ?? Math.random().toString(36).slice(2)
  return `workspace-${value.toLowerCase()}`
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
