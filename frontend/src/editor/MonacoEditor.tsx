import { useCallback, useEffect, useRef } from 'react'
import type { BuildConfiguration, ResolveSelectionResponse } from '../api/types'
import { createLanguageWorkspaceUri } from '../lsp/languageDocumentUri'
import {
  editorLanguageId,
  registerSourceLanguages,
  sourceEditorTheme,
} from '../lsp/languageRegistration'
import {
  createLanguageSessionKey,
  LanguageSessionLifecycle,
  type LanguageSessionStatusChange,
} from '../lsp/languageSessionLifecycle'
import {
  createMonacoLanguageSessionDependencies,
  MonacoLanguageBridge,
} from '../lsp/monacoLanguageClient'
import {
  type ExecutionFlowNavigationRequest,
  type ExecutionFlowSourceHit,
  type ExecutionFlowSourceModel,
  toEditorRange,
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
import { type EditorFontSize, mobileEditorMediaQuery } from './editorPreference'
import { type LspDocumentSymbol, sourceMethodFromDocumentSymbols } from './lspDocumentSymbols'
import * as monaco from './monacoCore'
import { findSourceMethodAtLine, type SourceMethodSelection } from './sourceMethod'
import './monacoEnvironment'

registerSourceLanguages()

export interface MonacoWorkspaceFile {
  path: string
  text: string
}

export interface MonacoLanguageSessionOptions {
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

export interface MonacoEditorProps {
  files: readonly MonacoWorkspaceFile[]
  activeFile: string
  monacoLanguageId: string
  languageSession: MonacoLanguageSessionOptions
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

interface LatestEditorState extends MonacoEditorProps {
  modelLanguageId: string
}

type DocumentSymbolCacheEntry =
  | { version: number; status: 'unsupported' }
  | { version: number; status: 'ready'; symbols: readonly LspDocumentSymbol[] }

export function MonacoEditor(props: MonacoEditorProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<monaco.editor.IStandaloneCodeEditor>(null)
  const modelsRef = useRef(new Map<string, monaco.editor.ITextModel>())
  const subscriptionsRef = useRef(new Map<string, monaco.IDisposable>())
  const viewStatesRef = useRef(new Map<string, monaco.editor.ICodeEditorViewState | null>())
  const synchronizingRef = useRef(new Set<string>())
  // Monaco is a controlled editor, but its model event can reach the parent
  // one render later. Keep the local value alive until that parent echo arrives
  // so a stale render cannot overwrite the first character of an edit.
  const pendingLocalEchoesRef = useRef(new Map<string, string>())
  const executionFlowDecorationsRef = useRef(new Map<string, string[]>())
  const sourceAssociationDecorationsRef = useRef(new Map<string, string[]>())
  const sourceAssociationMouseUpTimerRef = useRef<number | null>(null)
  const sourceAssociationActivationTimerRef = useRef<number | null>(null)
  const executionFlowStyleRef = useRef<HTMLStyleElement | null>(null)
  const controllerRef = useRef<LanguageSessionLifecycle | null>(null)
  const bridgeRef = useRef<MonacoLanguageBridge | null>(null)
  const languageStatusRef = useRef<LanguageSessionStatusChange['status']>('disabled')
  const languageSessionKeyRef = useRef<string | null>(null)
  const outputKindRef = useRef<RememberedWorkbenchOutputKind | null>(null)
  const documentSymbolsRef = useRef(new Map<string, DocumentSymbolCacheEntry>())
  const previewedSourceAssociationKeyRef = useRef<string | null>(null)
  const onChangeRef = useRef(props.onChange)
  const onStatusRef = useRef(props.onLanguageSessionStatus)
  const onCursorMethodChangeRef = useRef(props.onCursorMethodChange)
  const emitCursorMethodRef = useRef<
    (model: monaco.editor.ITextModel | null, position: monaco.Position) => void
  >(() => {})
  const workspaceIdRef = useRef(createWorkspaceId())
  const latestRef = useRef<LatestEditorState>({
    ...props,
    modelLanguageId: editorLanguageId(props.languageSession.languageId, props.monacoLanguageId),
  })

  const clearSourceAssociationActivation = useCallback(() => {
    if (sourceAssociationActivationTimerRef.current === null) return
    window.clearTimeout(sourceAssociationActivationTimerRef.current)
    sourceAssociationActivationTimerRef.current = null
  }, [])
  const clearSourceAssociationMouseUp = useCallback(() => {
    if (sourceAssociationMouseUpTimerRef.current === null) return
    window.clearTimeout(sourceAssociationMouseUpTimerRef.current)
    sourceAssociationMouseUpTimerRef.current = null
  }, [])

  onChangeRef.current = props.onChange
  onStatusRef.current = props.onLanguageSessionStatus
  onCursorMethodChangeRef.current = props.onCursorMethodChange
  latestRef.current = {
    ...props,
    modelLanguageId: editorLanguageId(props.languageSession.languageId, props.monacoLanguageId),
  }

  // The class identity changes during Vite HMR; remount so refs never retain an older bridge API.
  // biome-ignore lint/correctness/useExhaustiveDependencies: MonacoLanguageBridge is an intentional HMR dependency.
  useEffect(() => {
    const container = containerRef.current
    if (!container) return

    const mobileViewport =
      typeof matchMedia === 'function' ? matchMedia(mobileEditorMediaQuery) : null
    const editor = monaco.editor.create(container, {
      model: null,
      theme: sourceEditorTheme,
      automaticLayout: false,
      // Monaco's textarea fallback can lag one native mouse-drag selection
      // behind the model and consume the first direct replacement character.
      editContext: true,
      fontFamily: "'Cascadia Code', 'Cascadia Mono', Consolas, monospace",
      fontSize: latestRef.current.fontSize,
      lineHeight: editorLineHeight(latestRef.current.fontSize),
      minimap: { enabled: false },
      padding: { top: 12, bottom: 12 },
      scrollBeyondLastLine: false,
      smoothScrolling: true,
      stickyScroll: { enabled: false },
      fixedOverflowWidgets: true,
      renderWhitespace: 'selection',
      wordBasedSuggestions: 'off',
      'semanticHighlighting.enabled': true,
      ...monacoGutterOptions(mobileViewport?.matches ?? false),
      overviewRulerBorder: false,
      bracketPairColorization: { enabled: true },
      guides: { bracketPairs: true, indentation: true },
      lightbulb: { enabled: monaco.editor.ShowLightbulbIconMode.On },
      ariaLabel: 'Source editor',
    })
    const jumpToNextSnippetPlaceholder = () =>
      editor.trigger('keyboard', 'jumpToNextSnippetPlaceholder', null)
    const hasNextSnippetPlaceholder = 'inSnippetMode && hasNextTabstop'
    editor.addCommand(monaco.KeyCode.Enter, jumpToNextSnippetPlaceholder, hasNextSnippetPlaceholder)
    editor.addCommand(monaco.KeyCode.Tab, jumpToNextSnippetPlaceholder, hasNextSnippetPlaceholder)
    const bridge = new MonacoLanguageBridge()
    bridgeRef.current = bridge
    const controller = new LanguageSessionLifecycle((change) => {
      bridge.setSessionStatus?.(change.status)
      languageStatusRef.current = change.status
      if (containerRef.current) {
        containerRef.current.dataset.languageServiceStatus = change.status
      }
      if (change.status !== 'ready') documentSymbolsRef.current.clear()
      if (change.status === 'ready') {
        const position = editor.getPosition()
        if (position) emitCursorMethodRef.current(editor.getModel(), position)
      }
      onStatusRef.current?.(change)
    }, createMonacoLanguageSessionDependencies(bridge))
    editorRef.current = editor
    controllerRef.current = controller
    const cursorSubscription = editor.onDidChangeCursorPosition((event) => {
      emitCursorMethodRef.current(editor.getModel(), event.position)
    })
    const cursorSelectionSubscription = editor.onDidChangeCursorSelection((event) => {
      if (event.source !== 'mouse' && event.source !== 'keyboard') {
        return
      }
      if (event.selection.isEmpty()) {
        previewedSourceAssociationKeyRef.current = null
        return
      }
      const state = latestRef.current
      if (!state.onSourceAssociationPreview) return
      const associations = state.sourceAssociations ?? []
      if (!associations.some((association) => association.presentation === 'active-range')) return
      const association = sourceAssociationForSelection(associations, state.activeFile, {
        startLine: event.selection.startLineNumber,
        startColumn: event.selection.startColumn,
        endLine: event.selection.endLineNumber,
        endColumn: event.selection.endColumn,
      })
      if (!association || association.key === previewedSourceAssociationKeyRef.current) return
      previewedSourceAssociationKeyRef.current = association.key
      state.onSourceAssociationPreview(association.key)
    })
    const keyboardSubscription = editor.onKeyDown(() => {
      // A pending source-association click must never steal focus after typing
      // starts. Model changes below provide the same protection for paste/drop.
      clearSourceAssociationMouseUp()
      clearSourceAssociationActivation()
    })
    let sourceAssociationPointerDown: {
      position: monaco.Position
      selection: monaco.Selection
    } | null = null
    const sourceAssociationMouseDownSubscription = editor.onMouseDown((event) => {
      sourceAssociationPointerDown = null
      const position = event.target.position
      const selection = editor.getSelection()
      if (
        !event.event.leftButton ||
        event.event.browserEvent.detail > 1 ||
        !position ||
        !selection ||
        selection.isEmpty() ||
        !sourcePositionInSelection(position, selection)
      ) {
        return
      }
      sourceAssociationPointerDown = { position, selection }
    })
    const sourceAssociationSubscription = editor.onMouseUp((event) => {
      const detail = event.event.browserEvent.detail
      const position = event.target.position
      if (!event.event.leftButton) return
      clearSourceAssociationActivation()
      const pointerDown = sourceAssociationPointerDown
      sourceAssociationPointerDown = null
      const currentSelection = editor.getSelection()
      if (
        detail <= 1 &&
        position &&
        pointerDown &&
        positionsEqual(position, pointerDown.position) &&
        currentSelection &&
        !currentSelection.isEmpty() &&
        selectionsEqual(currentSelection, pointerDown.selection)
      ) {
        editor.setPosition(position)
      }
      const eventPath = latestRef.current.activeFile
      clearSourceAssociationMouseUp()
      sourceAssociationMouseUpTimerRef.current = window.setTimeout(() => {
        sourceAssociationMouseUpTimerRef.current = null
        const state = latestRef.current
        if (state.activeFile !== eventPath) return
        const selection = editor.getSelection()
        if (!selection) return

        const associations = state.sourceAssociations ?? []
        const hasActiveRangeAssociations = associations.some(
          (association) => association.presentation === 'active-range',
        )
        if (hasActiveRangeAssociations) {
          // Selection changes already preview the matching AST node while the
          // pointer is moving. Activating the same non-empty selection again
          // on mouseup schedules a result reveal after Monaco has finalized
          // its native drag selection, which can consume the first direct key
          // used to replace that selection. Keep activation for collapsed
          // source clicks only.
          if (detail > 1 || !selection.isEmpty()) return
          const finalPosition = editor.getPosition() ?? position
          if (!finalPosition) return
          const sourceRange = {
            startLine: finalPosition.lineNumber,
            startColumn: finalPosition.column,
            endLine: finalPosition.lineNumber,
            endColumn: finalPosition.column,
          }
          const association = sourceAssociationForSelection(associations, eventPath, sourceRange)
          if (!association) return
          clearSourceAssociationActivation()
          sourceAssociationActivationTimerRef.current = window.setTimeout(() => {
            sourceAssociationActivationTimerRef.current = null
            latestRef.current.onSourceAssociationActivate?.(association.key)
          }, 400)
          return
        }

        if (!selection.isEmpty() || detail > 1) return
        const finalPosition = editor.getPosition() ?? position
        if (!finalPosition) return
        const association = associations.find(
          (candidate) =>
            candidate.documentPath === eventPath &&
            sourcePositionInRange(finalPosition, candidate.range),
        )
        if (!association) return
        clearSourceAssociationActivation()
        sourceAssociationActivationTimerRef.current = window.setTimeout(() => {
          sourceAssociationActivationTimerRef.current = null
          latestRef.current.onSourceAssociationActivate?.(association.key)
        }, 400)
      }, 0)
    })
    const symbolsSubscription = bridge.onDidChangeDocumentSymbols(({ path, version, symbols }) => {
      const model = modelsRef.current.get(path)
      if (!model || model.getVersionId() !== version) return
      documentSymbolsRef.current.set(
        model.uri.toString(),
        symbols ? { version, status: 'ready', symbols } : { version, status: 'unsupported' },
      )
      const position = editor.getPosition()
      if (editor.getModel() === model && position) emitCursorMethodRef.current(model, position)
    })
    const executionFlowStyle = document.createElement('style')
    executionFlowStyle.dataset.sharplabnextExecutionFlow = workspaceIdRef.current
    document.head.append(executionFlowStyle)
    executionFlowStyleRef.current = executionFlowStyle

    const resizeObserver = new ResizeObserver(() => editor.layout())
    resizeObserver.observe(container)
    const updateGutter = () =>
      editor.updateOptions(monacoGutterOptions(mobileViewport?.matches ?? false))
    mobileViewport?.addEventListener('change', updateGutter)
    return () => {
      resizeObserver.disconnect()
      mobileViewport?.removeEventListener('change', updateGutter)
      cursorSubscription.dispose()
      cursorSelectionSubscription.dispose()
      keyboardSubscription.dispose()
      sourceAssociationMouseDownSubscription.dispose()
      sourceAssociationSubscription.dispose()
      clearSourceAssociationMouseUp()
      clearSourceAssociationActivation()
      symbolsSubscription.dispose()
      void controller.dispose()
      controllerRef.current = null
      languageStatusRef.current = 'disabled'
      documentSymbolsRef.current.clear()
      for (const subscription of subscriptionsRef.current.values()) subscription.dispose()
      subscriptionsRef.current.clear()
      for (const [path, model] of modelsRef.current) {
        bridge.unregisterDocument(path)
        model.dispose()
      }
      modelsRef.current.clear()
      pendingLocalEchoesRef.current.clear()
      executionFlowDecorationsRef.current.clear()
      sourceAssociationDecorationsRef.current.clear()
      executionFlowStyle.remove()
      executionFlowStyleRef.current = null
      viewStatesRef.current.clear()
      editor.dispose()
      editorRef.current = null
      bridge.dispose()
      if (bridgeRef.current === bridge) bridgeRef.current = null
    }
  }, [clearSourceAssociationActivation, clearSourceAssociationMouseUp, MonacoLanguageBridge])

  useEffect(() => {
    editorRef.current?.updateOptions({
      fontSize: props.fontSize,
      lineHeight: editorLineHeight(props.fontSize),
    })
  }, [props.fontSize])

  useEffect(() => {
    const editor = editorRef.current
    if (!editor) return
    const modelLanguageId = editorLanguageId(
      props.languageSession.languageId,
      props.monacoLanguageId,
    )
    const bridge = bridgeRef.current
    if (!bridge) return
    bridge.setLanguage(modelLanguageId)
    const nextPaths = new Set(props.files.map((file) => file.path))
    for (const [path, model] of modelsRef.current) {
      if (nextPaths.has(path)) continue
      subscriptionsRef.current.get(path)?.dispose()
      subscriptionsRef.current.delete(path)
      executionFlowDecorationsRef.current.delete(path)
      sourceAssociationDecorationsRef.current.delete(path)
      viewStatesRef.current.delete(model.uri.toString())
      modelsRef.current.delete(path)
      pendingLocalEchoesRef.current.delete(path)
      bridge.unregisterDocument(path)
      model.dispose()
    }

    for (const file of props.files) {
      let model = modelsRef.current.get(file.path)
      if (!model) {
        model = monaco.editor.createModel(
          file.text,
          modelLanguageId,
          createDocumentUri(workspaceIdRef.current, file.path),
        )
        modelsRef.current.set(file.path, model)
        bridge.registerDocument(file.path, model)
        subscriptionsRef.current.set(
          file.path,
          model.onDidChangeContent((event) => {
            documentSymbolsRef.current.delete(model?.uri.toString() ?? '')
            const text = model?.getValue() ?? ''
            let retryCompletion = false
            if (!synchronizingRef.current.has(file.path)) {
              pendingLocalEchoesRef.current.set(file.path, text)
              clearSourceAssociationMouseUp()
              clearSourceAssociationActivation()
              onChangeRef.current(file.path, text)
              retryCompletion =
                bridge.consumeEmptyCompletionRetry?.(
                  file.path,
                  model?.getVersionId() ?? 1,
                  event.changes,
                ) ?? false
            } else {
              // A controlled/external replacement is unrelated to the prefix
              // that produced the empty completion result.
              bridge.clearEmptyCompletionRetry?.(file.path)
            }
            bridge.changeDocument(file.path, text, model?.getVersionId() ?? 1)
            const editor = editorRef.current
            const position = editor?.getPosition()
            if (editor?.getModel() === model && position)
              emitCursorMethodRef.current(model ?? null, position)

            // Monaco does not requery an empty provider even when its result
            // is marked incomplete. Once the local edit has been delivered,
            // explicitly reopen suggestions exactly once for the next edit.
            if (retryCompletion) {
              const retryVersion = model?.getVersionId() ?? 1
              Promise.resolve().then(() => {
                const currentEditor = editorRef.current
                if (
                  !currentEditor ||
                  currentEditor.getModel() !== model ||
                  model?.getVersionId() !== retryVersion
                ) {
                  return
                }
                currentEditor.trigger('keyboard', 'editor.action.triggerSuggest', null)
              })
            }
          }),
        )
      } else {
        if (model.getLanguageId() !== modelLanguageId) {
          monaco.editor.setModelLanguage(model, modelLanguageId)
        }
        const modelText = model.getValue()
        const pendingLocalEcho = pendingLocalEchoesRef.current.get(file.path)
        if (pendingLocalEcho !== undefined) {
          if (file.text === pendingLocalEcho) {
            // The parent has accepted the local edit; future mismatches are
            // external updates and may be applied normally.
            pendingLocalEchoesRef.current.delete(file.path)
          } else if (modelText === pendingLocalEcho) {
            // This render still carries the old parent snapshot. Preserve the
            // text currently visible in Monaco until the echo catches up.
            continue
          } else {
            pendingLocalEchoesRef.current.delete(file.path)
          }
        }
        if (modelText !== file.text) {
          synchronizingRef.current.add(file.path)
          try {
            model.setValue(file.text)
          } finally {
            synchronizingRef.current.delete(file.path)
          }
        }
      }
    }

    const activeModel = modelsRef.current.get(props.activeFile) ?? null
    if (editor.getModel() !== activeModel) {
      const previous = editor.getModel()
      if (previous) {
        viewStatesRef.current.set(previous.uri.toString(), editor.saveViewState())
      }
      editor.setModel(activeModel)
      if (activeModel) {
        editor.restoreViewState(viewStatesRef.current.get(activeModel.uri.toString()) ?? null)
      }
    }
    const position = editor.getPosition()
    if (position) emitCursorMethodRef.current(activeModel, position)
    else onCursorMethodChangeRef.current?.(null)
  }, [
    clearSourceAssociationActivation,
    clearSourceAssociationMouseUp,
    props.activeFile,
    props.files,
    props.languageSession.languageId,
    props.monacoLanguageId,
  ])

  useEffect(() => {
    const editor = editorRef.current
    if (!editor) return
    const hits = props.executionFlow?.hits ?? []
    const hitsByPath = new Map<string, ExecutionFlowSourceHit[]>()
    for (const hit of hits) {
      const existing = hitsByPath.get(hit.documentPath)
      if (existing) existing.push(hit)
      else hitsByPath.set(hit.documentPath, [hit])
    }

    for (const [path, model] of modelsRef.current) {
      const decorations = (hitsByPath.get(path) ?? []).flatMap((hit) => {
        if (validateSourceRange(model.getValue(), hit.range)) return []
        const message = executionFlowHitMessage(hit)
        return [
          {
            range: toEditorRange(hit.range),
            options: {
              className: 'execution-flow-range',
              afterContentClassName: `execution-flow-count execution-flow-count-${hit.count}`,
              hoverMessage: { value: escapeMarkdown(message), isTrusted: false },
              linesDecorationsTooltip: message,
              overviewRuler: {
                color: 'rgba(0, 122, 112, 0.72)',
                position: monaco.editor.OverviewRulerLane.Center,
              },
              stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
            },
          } satisfies monaco.editor.IModelDeltaDecoration,
        ]
      })
      const nextIds = model.deltaDecorations(
        executionFlowDecorationsRef.current.get(path) ?? [],
        decorations,
      )
      if (nextIds.length > 0) executionFlowDecorationsRef.current.set(path, nextIds)
      else executionFlowDecorationsRef.current.delete(path)
    }

    const style = executionFlowStyleRef.current
    if (style) style.textContent = executionFlowCountStyles(hits)
    editor.updateOptions({ ariaLabel: executionFlowAriaLabel(props.executionFlow) })
  }, [props.executionFlow])

  useEffect(() => {
    const linkedAssociations = (props.sourceAssociations ?? []).filter(
      isLinkedLineSourceAssociation,
    )
    if (linkedAssociations.length === 0 && sourceAssociationDecorationsRef.current.size === 0) {
      return
    }

    const associationsByPath = new Map<string, SourceAssociation[]>()
    for (const association of linkedAssociations) {
      const existing = associationsByPath.get(association.documentPath)
      if (existing) existing.push(association)
      else associationsByPath.set(association.documentPath, [association])
    }

    for (const [path, model] of modelsRef.current) {
      const associations = (associationsByPath.get(path) ?? []).filter(
        (association) => !validateSourceRange(model.getValue(), association.range),
      )
      const lineDecorations = sourceAssociationLines(
        associations,
        props.activeSourceAssociationKey,
      ).map(
        ({ lineNumber, association, active }) =>
          ({
            range: new monaco.Range(lineNumber, 1, lineNumber, 1),
            options: {
              isWholeLine: true,
              className: [
                'monaco-source-association-line',
                active ? 'monaco-source-association-line-active' : '',
                sourceAssociationClass(association.colorIndex),
              ]
                .filter(Boolean)
                .join(' '),
            },
          }) satisfies monaco.editor.IModelDeltaDecoration,
      )
      const exactRangeTooltips = associations.map((association) => {
        const active = association.key === props.activeSourceAssociationKey
        return {
          range: toEditorRange(association.range),
          options: {
            inlineClassName: [
              'monaco-source-association-range',
              active ? 'monaco-source-association-exact-active' : '',
              active ? sourceAssociationClass(association.colorIndex) : '',
            ]
              .filter(Boolean)
              .join(' '),
            stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
          },
        } satisfies monaco.editor.IModelDeltaDecoration
      })
      const decorations = [...lineDecorations, ...exactRangeTooltips]
      const nextIds = model.deltaDecorations(
        sourceAssociationDecorationsRef.current.get(path) ?? [],
        decorations,
      )
      if (nextIds.length > 0) sourceAssociationDecorationsRef.current.set(path, nextIds)
      else sourceAssociationDecorationsRef.current.delete(path)
    }
  }, [props.activeSourceAssociationKey, props.sourceAssociations])

  // biome-ignore lint/correctness/useExhaustiveDependencies: association identity resets preview state.
  useEffect(() => {
    previewedSourceAssociationKeyRef.current = null
  }, [props.sourceAssociations])

  useEffect(() => {
    const editor = editorRef.current
    const navigation = props.sourceNavigation
    if (!editor || !navigation || props.activeFile !== navigation.documentPath) return
    const model = modelsRef.current.get(navigation.documentPath)
    if (
      !model ||
      editor.getModel() !== model ||
      validateSourceRange(model.getValue(), navigation.range)
    ) {
      return
    }
    const range = toEditorRange(navigation.range)
    editor.setSelection(range)
    editor.revealRangeInCenter(range, monaco.editor.ScrollType.Smooth)
    editor.focus()
    const position = editor.getPosition()
    if (position) emitCursorMethodRef.current(model, position)
  }, [props.activeFile, props.sourceNavigation])

  useEffect(() => {
    const controller = controllerRef.current
    if (!controller) return
    const session = props.languageSession
    if (!session.enabled || !session.toolchainId || !session.referenceSetId) {
      languageSessionKeyRef.current = null
      outputKindRef.current = null
      bridgeRef.current?.setSessionStatus?.('disabled')
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
    const modelLanguageId = editorLanguageId(session.languageId, props.monacoLanguageId)
    if (languageSessionKeyRef.current !== key || languageStatusRef.current !== 'ready') {
      bridgeRef.current?.setSessionStatus?.('connecting')
    }
    languageSessionKeyRef.current = key
    controller.update({
      key,
      plan:
        resolution && resolutionMatches
          ? {
              key,
              languageId: session.languageId,
              modelLanguageId,
              workspaceUri: createLanguageWorkspaceUri(session.languageId, workspaceIdRef.current),
              selectionRevision: session.selectionRevision,
              createRequest: () =>
                createLanguageSessionRequest(latestRef.current, modelsRef.current),
            }
          : null,
    })
  }, [props.files, props.languageSession, props.monacoLanguageId])

  emitCursorMethodRef.current = (
    model: monaco.editor.ITextModel | null,
    position: monaco.Position,
  ): void => {
    if (!model) {
      onCursorMethodChangeRef.current?.(null)
      return
    }

    const state = latestRef.current
    const syntaxSelection = findSourceMethodAtLine(
      model.getValue(),
      state.languageSession.languageId,
      position.lineNumber,
      state.activeFile,
    )
    if (languageStatusRef.current !== 'ready') {
      onCursorMethodChangeRef.current?.(syntaxSelection)
      return
    }

    const topLevelSelection = syntaxSelection?.name === '<Main>$' ? syntaxSelection : null
    const cacheKey = model.uri.toString()
    const version = model.getVersionId()
    const cached = documentSymbolsRef.current.get(cacheKey)
    if (cached?.version === version) {
      if (cached.status === 'unsupported') {
        onCursorMethodChangeRef.current?.(syntaxSelection)
      } else if (cached.status === 'ready') {
        onCursorMethodChangeRef.current?.(
          sourceMethodFromDocumentSymbols(
            cached.symbols,
            { line: position.lineNumber - 1, character: position.column - 1 },
            state.languageSession.languageId,
          ) ?? topLevelSelection,
        )
      }
      return
    }

    onCursorMethodChangeRef.current?.(topLevelSelection)
  }

  return (
    <section
      ref={containerRef}
      className="monaco-host"
      data-editor="monaco"
      data-language-service-status="disabled"
      aria-label={executionFlowAriaLabel(props.executionFlow)}
    />
  )
}

export function editorLineHeight(fontSize: EditorFontSize): number {
  return Math.round(fontSize * 1.5)
}

export function monacoGutterOptions(isMobileViewport: boolean) {
  return {
    // SharpLabNext has no breakpoint/debugger surface, so this lane only wastes source width.
    glyphMargin: false,
    lineNumbersMinChars: isMobileViewport ? 2 : 3,
    folding: true,
    // Monaco adds its own 16px folding lane. Mobile does not need the default extra 10px.
    lineDecorationsWidth: isMobileViewport ? 0 : 10,
    showFoldingControls: isMobileViewport ? ('always' as const) : ('mouseover' as const),
  }
}

export function createDocumentUri(workspaceId: string, path: string): monaco.Uri {
  const encodedPath = path
    .split('/')
    .map((segment) => encodeURIComponent(segment))
    .join('/')
  return monaco.Uri.parse(`sharplabnext://${workspaceId}/${encodedPath}`)
}

function createWorkspaceId(): string {
  const value = globalThis.crypto?.randomUUID?.() ?? Math.random().toString(36).slice(2)
  return `workspace-${value.toLowerCase()}`
}

function executionFlowAriaLabel(model: ExecutionFlowSourceModel | null): string {
  if (!model || model.hits.length === 0) return 'Source editor'
  const count = model.hits.reduce((total, hit) => total + hit.count, 0)
  return `Source editor. Execution flow shows ${count} events across ${model.hits.length} source ranges.`
}

function executionFlowHitMessage(hit: ExecutionFlowSourceHit): string {
  const suffix = hit.count === 1 ? 'event' : 'events'
  return `${hit.eventKind}: ${hit.count} ${suffix} at ${hit.documentPath}:${hit.range.startLine}:${hit.range.startColumn}`
}

function executionFlowCountStyles(hits: readonly ExecutionFlowSourceHit[]): string {
  const counts = [...new Set(hits.map((hit) => hit.count))]
  return counts
    .map((count) => {
      const fontSize = String(count).length <= 2 ? 9 : String(count).length === 3 ? 7 : 6
      return `.monaco-editor .execution-flow-count-${count}::before { content: "${count}"; font-size: ${fontSize}px; }`
    })
    .join('\n')
}

function escapeMarkdown(value: string): string {
  const punctuation = '\\`*_{}[]()#+-.!'
  return [...value]
    .map((character) => (punctuation.includes(character) ? `\\${character}` : character))
    .join('')
}

function sourcePositionInRange(
  position: monaco.Position,
  range: SourceAssociation['range'],
): boolean {
  const afterStart =
    position.lineNumber > range.startLine ||
    (position.lineNumber === range.startLine && position.column >= range.startColumn)
  const beforeEnd =
    position.lineNumber < range.endLine ||
    (position.lineNumber === range.endLine && position.column < range.endColumn)
  return afterStart && beforeEnd
}

function sourcePositionInSelection(
  position: monaco.Position,
  selection: monaco.Selection,
): boolean {
  const afterStart =
    position.lineNumber > selection.startLineNumber ||
    (position.lineNumber === selection.startLineNumber && position.column >= selection.startColumn)
  const beforeEnd =
    position.lineNumber < selection.endLineNumber ||
    (position.lineNumber === selection.endLineNumber && position.column <= selection.endColumn)
  return afterStart && beforeEnd
}

function positionsEqual(left: monaco.Position, right: monaco.Position): boolean {
  return left.lineNumber === right.lineNumber && left.column === right.column
}

function selectionsEqual(left: monaco.Selection, right: monaco.Selection): boolean {
  return (
    left.startLineNumber === right.startLineNumber &&
    left.startColumn === right.startColumn &&
    left.endLineNumber === right.endLineNumber &&
    left.endColumn === right.endColumn
  )
}

function createLanguageSessionRequest(
  state: LatestEditorState,
  models: ReadonlyMap<string, monaco.editor.ITextModel>,
) {
  const resolution = state.languageSession.resolution
  const toolchainId = state.languageSession.toolchainId
  const referenceSetId = state.languageSession.referenceSetId
  if (!resolution || !toolchainId || !referenceSetId) {
    throw new Error('Language session selection is no longer resolved.')
  }

  const requestId = `lsp_${globalThis.crypto?.randomUUID?.() ?? Date.now().toString(36)}`
  const buildOptions = createWorkbenchBuildOptions(
    state.languageSession.languageId,
    state.languageSession.buildMode,
    resolution.pipelinePlan.stages,
  )
  return {
    requestId,
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
          version: model?.getVersionId() ?? 1,
          text: model?.getValue() ?? file.text,
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
