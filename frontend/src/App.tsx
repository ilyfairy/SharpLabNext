import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, ArrowLeft, ArrowRight, Check, ChevronDown, FileCode2, FilePlus2, GitFork, Link2, LoaderCircle, Minus, Pencil, Play, Plus, Settings, Square, WifiOff, X, XCircle } from 'lucide-react'
import { type CSSProperties, lazy, type ReactNode, type PointerEvent as ReactPointerEvent, Suspense, useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { ApiError, cancelOperation, getGist, getOperationContent } from './api/client'
import { isOperationTerminal, operationQueryKeys, useCatalogQuery, useGatewayConnectionStatus, useOperationEvents, useOperationState } from './api/queries'
import type { AstResult, BuildConfiguration, GistDocument, OperationEvent, OperationResult, OutputManifest, ResolveSelectionResponse, RunResult, SelectionChange } from './api/types'
import { CodeMirrorEditor } from './editor/CodeMirrorEditor'
import { editorFontSizeOptions, useEditorPreference } from './editor/editorPreference'
import { createAstSourceMap } from './results/astSourceMapModel'
import { createExecutionFlowSourceModel, currentExecutionFlowSourceModel, type ExecutionFlowNavigationRequest, type ExecutionFlowSourceTarget, validateSourceRange } from './results/executionFlowModel'
import { AstStatus, findTypedResult, type GeneratedSourceContentView, JitStatus, type OperationContentView, OperationResults, RunStatus } from './results/OperationResults'
import { summarizeResultIdentities } from './results/resultIdentity'
import { createResultIdentityPresentation } from './results/resultIdentityPresentation'
import { type SourceAssociation, type SourceAssociationActivation, sourceAssociationActivationKey, sourceAssociationKey } from './results/sourceAssociationModel'
import { decodeShareFragment, encodeV3 } from './share'
import { GistDialog } from './share/GistDialog'
import { gistFragment, parseGistFragment } from './share/gist'
import {
  availabilityLabel,
  fallbackLanguage,
  languageById,
  normalizeSelectionIntent,
  outputById,
  outputOptionsFor,
  referenceSetDisplayName,
  referenceSetOptionsFor,
  runtimeOptionsFor,
  type SelectionIntent,
  toolchainById,
  toolchainOptionsFor,
} from './workbench/catalog'
import { createGistWorkspaceState, decodeWorkbenchGist } from './workbench/gistState'
import { createFollowupOperation, createInitialPipelineOperation, type PipelineOperationKind, type PipelineOperationStart, startPipelineOperation } from './workbench/operationWorkflow'
import { PaneSplitSeparator } from './workbench/PaneSplitSeparator'
import { usePaneSplitPreference } from './workbench/paneSplitPreference'
import { createShareWorkspaceState, decodeWorkbenchShare } from './workbench/shareState'
import { useWorkbenchStore, type WorkspaceFileState } from './workbench/store'
import { getWorkbenchSnapshot } from './workbench/storeSnapshot'
import { useSelectionResolution } from './workbench/useSelectionResolution'
import { UrlCodecWorkerClient } from './workers'
import './App.css'

type MonacoEditorComponent = typeof import('./editor/MonacoEditor')['MonacoEditor']

const nativeRuntimeOptionEndSpacing = '\u00a0\u00a0'

interface ShareErrorState {
  action: 'restore' | 'create'
  error: Error
}

const normalizeError = (error: unknown, fallback: string): Error => (error instanceof Error ? error : new Error(fallback))

function isGatewayTransportFailure(error: unknown): error is Error {
  if (error instanceof ApiError) return error.status >= 500
  if (error instanceof TypeError) return true
  return error instanceof Error && /(?:gateway (?:request|command) failed \(5\d\d\)|websocket (?:disconnected|closed before opening|failed to open|is not open)|network error|failed to fetch)/i.test(error.message)
}

function firstVisibleFailure(...failures: Array<Error | null | undefined>): Error | null {
  return failures.find((failure) => failure && !isGatewayTransportFailure(failure)) ?? null
}

const MonacoEditor = lazy(async (): Promise<{ default: MonacoEditorComponent }> => {
  try {
    const module = await import('./editor/MonacoEditor')
    return { default: module.MonacoEditor }
  } catch (error: unknown) {
    const message = error instanceof Error ? error.message : 'Unknown Monaco initialization error.'
    return {
      default: () => (
        <div className="editor-runtime-state editor-runtime-state--error" role="alert">
          <strong>Monaco could not start.</strong>
          <span>{message}</span>
          <span>Select CodeMirror in the status bar to continue.</span>
        </div>
      ),
    }
  }
})

interface SelectFieldProps {
  label: string
  description?: string
  value: string
  children: ReactNode
  onChange: (value: string) => void
  className?: string
  compact?: boolean
  disabled?: boolean
}

function SelectField({ label, description, value, children, onChange, className, compact = false, disabled = false }: SelectFieldProps) {
  const id = useId()
  const classes = ['select-field', compact ? 'select-field--compact' : null, className].filter(Boolean).join(' ')
  return (
    <label className={classes} htmlFor={id}>
      <span className="visually-hidden">{label}</span>
      <select id={id} aria-label={label} title={description} value={value} disabled={disabled} onChange={(event) => onChange(event.target.value)}>
        {children}
      </select>
      <ChevronDown className="select-field__chevron" aria-hidden="true" />
    </label>
  )
}

function currentIntent(): SelectionIntent {
  const state = useWorkbenchStore.getState()
  return {
    languageId: state.languageId,
    toolchainId: state.toolchainId,
    referenceSetId: state.referenceSetId,
    outputId: state.outputId,
    runtimeId: state.runtimeId,
  }
}

function nextFileName(defaultFileName: string, existingPaths: readonly string[]): string {
  const extensionIndex = defaultFileName.lastIndexOf('.')
  const extension = extensionIndex >= 0 ? defaultFileName.slice(extensionIndex) : ''
  const existing = new Set(existingPaths)
  for (let index = 2; index < 10_000; index += 1) {
    const candidate = `File${index}${extension}`
    if (!existing.has(candidate)) return candidate
  }
  return `File-${crypto.randomUUID()}${extension}`
}

function currentBaseUrl(): string {
  const hashIndex = window.location.href.indexOf('#')
  return hashIndex < 0 ? window.location.href : window.location.href.slice(0, hashIndex)
}

function actionLabel(output: OutputManifest | undefined): string {
  if (output?.id === 'compile-check') return 'Check'
  if (output?.id === 'ast') return 'Build AST'
  if (output?.id === 'generated-source') return 'Generate'
  if (output?.id === 'il-verify') return 'Verify'
  if (output?.id === 'run' || output?.id === 'execution-flow') return 'Run'
  if (output?.id === 'jit-asm') return 'JIT'
  if (output?.id === 'il' || output?.id === 'run-il') return 'Render IL'
  if (output?.id === 'decompiled-csharp') return 'Decompile'
  if (output?.id === 'explain') return 'Explain'
  return 'Build'
}

const liveCompilationOutputs = new Set(['compile-check', 'ast', 'generated-source', 'generated-il', 'il', 'decompiled-csharp', 'il-verify', 'javascript', 'jit-asm', 'explain'])

const sourceAssociationOutputs = new Set(['execution-flow', 'ast', 'jit-asm', 'il', 'run-il', 'generated-il'])

function supportsSourceAssociations(outputId: string | null | undefined): boolean {
  return outputId !== null && outputId !== undefined && sourceAssociationOutputs.has(outputId)
}

const sourceAssociationInteractionSelector = [
  '[data-source-association-interaction="true"]',
  '.cm-source-association-range',
  '.monaco-source-association-range',
  '.cm-source-navigable.source-association',
  '.monaco-output-source-navigable.source-association',
].join(',')

export function isSourceAssociationInteractionTarget(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest(sourceAssociationInteractionSelector) !== null
}

function liveOperationDebounceMs(outputId: string): number | null {
  if (outputId === 'run') return 900
  return liveCompilationOutputs.has(outputId) ? 450 : null
}

type WorkflowStatus = 'starting' | 'accepted' | 'running' | 'cancelling' | 'completed' | 'failed' | 'cancelled' | 'stale'

interface WorkflowStageResult {
  operationId: string
  kind: PipelineOperationKind
  label: string
  events: OperationEvent[]
  result: OperationResult | null
}

interface ActiveWorkflowStage {
  operationId: string
  kind: PipelineOperationKind
  label: string
}

interface OperationWorkflow {
  id: string
  trigger: 'live' | 'manual'
  resolution: ResolveSelectionResponse
  outputId: string
  buildMode: BuildConfiguration
  catalogRevision: string
  referenceSetSnapshot: {
    id: string
    displayName: string
    digest: string
  } | null
  workspaceRevision: number
  selectionRevision: number
  workspaceFiles: WorkspaceFileState[]
  status: WorkflowStatus
  active: ActiveWorkflowStage | null
  completedStages: WorkflowStageResult[]
  artifactRef: string | null
  content: OperationContentView | null
  generatedSourceContents: GeneratedSourceContentView[]
  error: Error | null
}

function workflowEventsFrom(workflow: OperationWorkflow | null): OperationEvent[] {
  if (!workflow) return []
  return workflow.completedStages.flatMap((stage) => stage.events)
}

function workflowResultsFrom(workflow: OperationWorkflow | null): OperationResult[] {
  if (!workflow) return []
  return workflow.completedStages.flatMap((stage) => (stage.result ? [stage.result] : []))
}

function outputChunkExists(events: readonly OperationEvent[], channel: string): boolean {
  return events.some((event) => event.payload.kind === 'output-chunk' && event.payload.chunk.channel === channel && event.payload.chunk.data.length > 0)
}

function workflowHasStableTarget(workflow: OperationWorkflow | null, results: readonly OperationResult[], events: readonly OperationEvent[]): boolean {
  if (!workflow || workflow.error || !['completed', 'stale'].includes(workflow.status)) return false
  switch (workflow.outputId) {
    case 'ast':
      return results.some((result) => result.resultType === 'ast')
    case 'generated-source':
      return (
        results.some((result) => result.resultType === 'generated-source') &&
        workflow.generatedSourceContents.length > 0 &&
        workflow.generatedSourceContents.every((document) => !document.loading && !document.error) &&
        workflow.generatedSourceContents.some((document) => document.text !== null)
      )
    case 'explain':
      return results.some((result) => result.resultType === 'explain')
    case 'il':
    case 'run-il':
    case 'generated-il':
    case 'decompiled-csharp':
      return workflow.content?.loading === false && !workflow.content.error && workflow.content.text !== null
    case 'il-verify':
      return results.some((result) => result.resultType === 'artifact-verification' && (result.outcome === 'valid' || result.outcome === 'findings'))
    case 'run':
      return results.some((result) => result.resultType === 'run' && result.status === 'completed')
    case 'execution-flow':
      return results.some((result) => result.resultType === 'run' && result.status === 'completed') && outputChunkExists(events, 'flow')
    case 'jit-asm':
      return results.some((result) => result.resultType === 'jit' && result.status === 'completed') && (workflow.content?.text != null || outputChunkExists(events, 'jit'))
    default:
      return results.length > 0
  }
}

function unsuccessfulResultMessage(result: OperationResult): string | null {
  switch (result.resultType) {
    case 'build':
      return result.outcome === 'succeeded' ? null : 'Compilation failed. Fix the reported errors and try again.'
    case 'compile-check':
      return result.compilationSucceeded ? null : 'Compilation failed. Fix the reported errors and try again.'
    case 'artifact-transform':
    case 'artifact-render':
      return result.outcome === 'succeeded' ? null : `The artifact stage finished with ${result.outcome}.`
    case 'artifact-verification':
      return result.outcome === 'valid' || result.outcome === 'findings' ? null : `Verification finished with ${result.outcome}.`
    case 'run':
      return result.status === 'completed' ? null : `Run finished with ${result.status}.`
    case 'jit':
      return result.status === 'completed' ? null : `JIT inspection finished with ${result.status}.`
    default:
      return null
  }
}

function workflowFailure(workflow: OperationWorkflow | null, results: readonly OperationResult[], events: readonly OperationEvent[], targetReady: boolean): Error | null {
  if (!workflow) return null
  if (workflow.error) return workflow.error
  if (workflow.content?.error) return workflow.content.error
  const generatedSourceError = workflow.generatedSourceContents.find((document) => document.error)?.error
  if (generatedSourceError) return generatedSourceError
  const resultMessage = results.map(unsuccessfulResultMessage).find((message) => message !== null)
  if (resultMessage) return new Error(resultMessage)
  if (workflow.status === 'failed') return new Error('The operation failed.')
  if (workflow.content?.loading || workflow.generatedSourceContents.some((document) => document.loading)) {
    return null
  }
  if (workflow.status === 'completed' && !workflow.active && !targetReady) {
    return new Error('The selected output was not produced. See the diagnostics below.')
  }
  const failedEvent = [...events].reverse().find((event) => event.payload.kind === 'failed')
  return failedEvent?.payload.kind === 'failed' ? new Error(failedEvent.payload.error.publicMessage) : null
}

interface StartWorkflowOperation {
  workflowId: string
  label: string
  operation: PipelineOperationStart
}

function SelectionNotices({ changes }: { changes: readonly SelectionChange[] }) {
  if (changes.length === 0) return null
  return (
    <section className="selection-notices" aria-label="Selection notices">
      {changes.map((change) => (
        <div key={`${change.field}:${change.reason}:${change.effectiveValue}`}>
          <AlertTriangle aria-hidden="true" size={13} />
          <span>{change.message}</span>
        </div>
      ))}
    </section>
  )
}

function unavailableReason(changes: readonly SelectionChange[]): string | null {
  return changes.find((change) => change.reason === 'profile-unavailable')?.message ?? null
}

function App() {
  const initialShareFragment = useRef(window.location.hash).current
  const queryClient = useQueryClient()
  const catalogQuery = useCatalogQuery()
  const gatewayConnectionStatus = useGatewayConnectionStatus()
  const catalog = catalogQuery.data
  const {
    languageId,
    toolchainId,
    referenceSetId,
    outputId,
    runtimeId,
    buildMode,
    mobilePane,
    files,
    activeFile,
    sourceOrder,
    workspaceRevision,
    selectionRevision,
    setSelectionIntent,
    selectLanguage,
    setBuildMode,
    setMobilePane,
    setFileSource,
    selectFile,
    addFile,
    removeFile,
    renameFile,
    moveFileInSourceOrder,
    replaceWorkspace,
  } = useWorkbenchStore()
  const editorPreference = useEditorPreference()
  const paneSplitPreference = usePaneSplitPreference()
  const [workflow, setWorkflow] = useState<OperationWorkflow | null>(null)
  const [stableWorkflow, setStableWorkflow] = useState<OperationWorkflow | null>(null)
  const [mobileFilesExpanded, setMobileFilesExpanded] = useState(false)
  const [renamingPath, setRenamingPath] = useState<string | null>(null)
  const [renameDraft, setRenameDraft] = useState('')
  const [shareReady, setShareReady] = useState(false)
  const resolutionState = useSelectionResolution(catalog, shareReady)
  const [shareCopied, setShareCopied] = useState(false)
  const [shareWarnings, setShareWarnings] = useState<string[]>([])
  const [shareError, setShareError] = useState<ShareErrorState | null>(null)
  const [gistDialogOpen, setGistDialogOpen] = useState(false)
  const [mobileSettingsOpen, setMobileSettingsOpen] = useState(false)
  const [editorSettingsOpen, setEditorSettingsOpen] = useState(false)
  const [currentGist, setCurrentGist] = useState<GistDocument | null>(null)
  const [sourceNavigation, setSourceNavigation] = useState<ExecutionFlowNavigationRequest | null>(null)
  const [sourceAssociations, setSourceAssociations] = useState<readonly SourceAssociation[]>([])
  const [activeSourceAssociation, setActiveSourceAssociation] = useState<SourceAssociationActivation | null>(null)
  const [activeSourceAssociationRevision, setActiveSourceAssociationRevision] = useState(0)
  const [hoveredSourceAssociation, setHoveredSourceAssociation] = useState<SourceAssociationActivation | null>(null)
  const activeSourceAssociationKey = sourceAssociationActivationKey(activeSourceAssociation, workflow?.id ?? null)
  const hoveredSourceAssociationKey = sourceAssociationActivationKey(hoveredSourceAssociation, workflow?.id ?? null)
  const shareDecodeStarted = useRef(false)
  const gistBaseline = useRef<{
    id: string
    workspaceRevision: number
    selectionRevision: number
  } | null>(null)
  const shareUrlWriteGeneration = useRef(0)
  const urlCodec = useRef<UrlCodecWorkerClient | null>(null)
  const renameInput = useRef<HTMLInputElement | null>(null)
  const paneGrid = useRef<HTMLElement | null>(null)
  const hydratedCatalogRevision = useRef<string | null>(null)
  const workflowRef = useRef<OperationWorkflow | null>(null)
  const processedOperations = useRef(new Set<string>())
  const contentRequest = useRef<AbortController | null>(null)
  const sourceNavigationRevision = useRef(0)
  const runBuildRef = useRef<(trigger?: 'live' | 'manual') => void>(() => {})
  const supersededLiveOperation = useRef<string | null>(null)
  const updateWorkflow = useCallback((update: (current: OperationWorkflow | null) => OperationWorkflow | null) => {
    const next = update(workflowRef.current)
    workflowRef.current = next
    setWorkflow(next)
  }, [])
  const activeOperationId = workflow?.active?.operationId ?? null
  const operationStateQuery = useOperationState(activeOperationId)
  const operationEvents = useOperationEvents(activeOperationId)

  useEffect(
    () => () => {
      urlCodec.current?.dispose()
      contentRequest.current?.abort()
    },
    [],
  )

  useEffect(() => {
    if (!catalog || shareDecodeStarted.current) return
    shareDecodeStarted.current = true
    if (!initialShareFragment) {
      setShareReady(true)
      return
    }

    let parsedGist: ReturnType<typeof parseGistFragment>
    try {
      parsedGist = parseGistFragment(initialShareFragment)
    } catch (error) {
      setShareError({
        action: 'restore',
        error: normalizeError(error, 'The Gist URL is invalid.'),
      })
      setShareReady(true)
      return
    }
    if (parsedGist) {
      void getGist(parsedGist.id, parsedGist.options)
        .then((document) => {
          const restored = decodeWorkbenchGist(document, catalog)
          replaceWorkspace(restored.replacement)
          const state = useWorkbenchStore.getState()
          gistBaseline.current = {
            id: document.id,
            workspaceRevision: state.workspaceRevision,
            selectionRevision: state.selectionRevision,
          }
          setCurrentGist(document)
          setShareWarnings(restored.warnings)
        })
        .catch((error: unknown) => {
          setShareError({
            action: 'restore',
            error: normalizeError(error, 'The Gist could not be loaded.'),
          })
        })
        .finally(() => setShareReady(true))
      return
    }

    let decode: ReturnType<typeof decodeShareFragment>
    if (typeof Worker === 'undefined') {
      decode = decodeShareFragment(initialShareFragment)
    } else {
      const codec = urlCodec.current ?? new UrlCodecWorkerClient()
      urlCodec.current = codec
      decode = codec.decode(initialShareFragment)
    }
    void decode
      .then((decoded) => {
        const restored = decodeWorkbenchShare(decoded, catalog)
        replaceWorkspace(restored.replacement)
        setShareWarnings(restored.warnings)
      })
      .catch((error: unknown) => {
        setShareError({
          action: 'restore',
          error: normalizeError(error, 'The share URL is invalid.'),
        })
      })
      .finally(() => setShareReady(true))
  }, [catalog, initialShareFragment, replaceWorkspace])

  useEffect(() => {
    if (!renamingPath) return
    renameInput.current?.focus()
    renameInput.current?.select()
  }, [renamingPath])

  useEffect(() => {
    if (!catalog || hydratedCatalogRevision.current === catalog.revision) return
    const normalized = normalizeSelectionIntent(catalog, currentIntent())
    const language = languageById(catalog, normalized.languageId)
    if (language) selectLanguage(language, normalized)
    hydratedCatalogRevision.current = catalog.revision
  }, [catalog, selectLanguage])

  useEffect(() => {
    if (!catalog || !shareReady) return
    const baseline = gistBaseline.current
    if (currentGist && baseline?.id === currentGist.id && baseline.workspaceRevision === workspaceRevision && baseline.selectionRevision === selectionRevision) {
      return
    }
    const writeGeneration = ++shareUrlWriteGeneration.current
    let cancelled = false
    const timeout = window.setTimeout(() => {
      const state = createShareWorkspaceState(catalog, {
        languageId,
        toolchainId,
        referenceSetId,
        outputId,
        runtimeId,
        buildMode,
        files,
        activeFile,
        sourceOrder: useWorkbenchStore.getState().sourceOrder,
      })
      let encode: ReturnType<typeof encodeV3>
      if (typeof Worker === 'undefined') {
        encode = encodeV3(state, {
          profile: 'live',
          baseUrl: currentBaseUrl(),
        })
      } else {
        const codec = urlCodec.current ?? new UrlCodecWorkerClient()
        urlCodec.current = codec
        encode = codec.encodeV3(state, {
          profile: 'live',
          baseUrl: currentBaseUrl(),
        })
      }
      void encode
        .then((encoded) => {
          if (!cancelled && writeGeneration === shareUrlWriteGeneration.current) {
            window.history.replaceState(window.history.state, '', encoded.fragment)
          }
        })
        // Address-bar synchronization is best-effort. A cold or busy codec worker
        // must not turn a successfully restored and compiled workspace into a restore error.
        .catch(() => undefined)
    }, 400)
    return () => {
      cancelled = true
      window.clearTimeout(timeout)
    }
  }, [activeFile, buildMode, catalog, currentGist, files, languageId, outputId, referenceSetId, runtimeId, shareReady, selectionRevision, toolchainId, workspaceRevision])

  const startOperationMutation = useMutation({
    mutationFn: async (input: StartWorkflowOperation) => ({
      input,
      handle: await startPipelineOperation(input.operation),
    }),
    onSuccess: ({ input, handle }) => {
      updateWorkflow((current) =>
        current?.id === input.workflowId
          ? {
              ...current,
              active: {
                operationId: handle.operationId,
                kind: input.operation.kind,
                label: input.label,
              },
              status: 'accepted',
              error: null,
            }
          : current,
      )
      setMobilePane('result')
    },
    onError: (error, input) => {
      updateWorkflow((current) => (current?.id === input.workflowId ? { ...current, active: null, status: 'failed', error } : current))
    },
  })
  const cancelMutation = useMutation({
    mutationFn: async () => {
      if (!activeOperationId) throw new Error('There is no active operation.')
      return cancelOperation(activeOperationId)
    },
    onSuccess: () => {
      if (activeOperationId) {
        void queryClient.invalidateQueries({
          queryKey: operationQueryKeys.state(activeOperationId),
        })
      }
    },
  })

  const language = (catalog && languageById(catalog, languageId)) ?? fallbackLanguage
  const filesByPath = new Map(files.map((file) => [file.path, file]))
  const sourceOrderedFiles = sourceOrder.flatMap((path) => {
    const file = filesByPath.get(path)
    return file ? [file] : []
  })
  const displayedFiles = sourceOrderedFiles.length === files.length ? sourceOrderedFiles : files
  const sourceOrderIndex = sourceOrder.indexOf(activeFile)
  const showSourceOrderControls = language.capabilities.includes('source-order') && files.length > 1
  const canMoveSourceEarlier = showSourceOrderControls && sourceOrderIndex > 0
  const canMoveSourceLater = showSourceOrderControls && sourceOrderIndex >= 0 && sourceOrderIndex < sourceOrder.length - 1
  const toolchain = catalog ? toolchainById(catalog, toolchainId) : undefined
  const output = catalog ? outputById(catalog, outputId) : undefined
  const availableToolchains = catalog ? toolchainOptionsFor(catalog, languageId) : []
  const availableReferenceSets = catalog ? referenceSetOptionsFor(catalog, toolchainId) : []
  const availableOutputs = catalog ? outputOptionsFor(catalog, languageId, toolchainId, referenceSetId) : []
  const availableRuntimes = catalog ? runtimeOptionsFor(catalog, toolchainId, referenceSetId, outputId) : []
  const languageServerEnabled = toolchain?.availability.installed === true && toolchain.availability.health === 'healthy' && toolchain.capabilities.includes('lsp')
  const editorLanguageSession = {
    enabled: languageServerEnabled,
    resolution: resolutionState.resolution,
    languageId,
    toolchainId,
    referenceSetId,
    buildMode,
    workspaceRevision,
    selectionRevision,
    sourceOrder,
  }

  const updateSelection = (patch: Partial<SelectionIntent>) => {
    if (!catalog) return
    setSelectionIntent(normalizeSelectionIntent(catalog, { ...currentIntent(), ...patch }))
  }

  const updateLanguage = (nextLanguageId: string) => {
    if (!catalog) return
    const nextLanguage = languageById(catalog, nextLanguageId)
    if (!nextLanguage) return
    const selection = normalizeSelectionIntent(catalog, {
      ...currentIntent(),
      languageId: nextLanguageId,
      runtimeId: null,
    })
    selectLanguage(nextLanguage, selection)
  }

  const typedResult = findTypedResult(operationEvents.events)
  const workflowIsStale = Boolean(workflow && (workflow.workspaceRevision !== workspaceRevision || workflow.selectionRevision !== selectionRevision))
  const operationStatus = workflow?.active && operationStateQuery.data?.status ? operationStateQuery.data.status : startOperationMutation.isPending ? 'starting' : (workflow?.status ?? 'idle')
  const operationIsTerminal = isOperationTerminal(operationStateQuery.data?.status)
  const profileUnavailable = unavailableReason(resolutionState.selectionChanges)
  const runDisabled = !resolutionState.resolution || resolutionState.isResolving || profileUnavailable !== null || startOperationMutation.isPending || workflow?.active != null

  const workflowEvents = useMemo(() => [...(workflow?.completedStages.flatMap((stage) => stage.events) ?? []), ...(workflow?.active ? operationEvents.events : [])], [operationEvents.events, workflow?.active, workflow?.completedStages])
  const executionFlowModel = useMemo(
    () => (workflow?.outputId === 'execution-flow' ? createExecutionFlowSourceModel(workflowEvents, workflow.workspaceFiles) : createExecutionFlowSourceModel([], [])),
    [workflow?.outputId, workflow?.workspaceFiles, workflowEvents],
  )
  const activeExecutionFlow = currentExecutionFlowSourceModel(
    executionFlowModel,
    workflow
      ? {
          outputId: workflow.outputId,
          workspaceRevision: workflow.workspaceRevision,
          selectionRevision: workflow.selectionRevision,
        }
      : null,
    { outputId, workspaceRevision, selectionRevision },
  )
  const workflowResults = useMemo(
    () => [...(workflow?.completedStages.flatMap((stage) => (stage.result ? [stage.result] : [])) ?? []), ...(workflow?.active && typedResult ? [typedResult] : [])],
    [typedResult, workflow?.active, workflow?.completedStages],
  )
  const workflowTargetReady = workflowHasStableTarget(workflow, workflowResults, workflowEvents)
  useEffect(() => {
    if (workflow && workflowTargetReady) setStableWorkflow(workflow)
  }, [workflow, workflowTargetReady])

  const currentWorkflowMatchesSelection = workflow?.selectionRevision === selectionRevision
  const retainedWorkflow = stableWorkflow?.selectionRevision === selectionRevision && stableWorkflow.outputId === (workflow?.outputId ?? outputId) ? stableWorkflow : null
  const presentationWorkflow = workflowTargetReady && currentWorkflowMatchesSelection ? workflow : (retainedWorkflow ?? (currentWorkflowMatchesSelection ? workflow : null))
  const presentationMatchesCurrentWorkspace = Boolean(
    presentationWorkflow && presentationWorkflow === workflow && presentationWorkflow.workspaceRevision === workspaceRevision && presentationWorkflow.selectionRevision === selectionRevision && presentationWorkflow.outputId === outputId,
  )
  const presentationSourceNavigationEnabled = Boolean(presentationMatchesCurrentWorkspace && supportsSourceAssociations(presentationWorkflow?.outputId))
  const presentationSourceAssociationsRetained = Boolean(!presentationSourceNavigationEnabled && presentationWorkflow && presentationWorkflow !== workflow && presentationWorkflow.outputId === outputId && supportsSourceAssociations(presentationWorkflow.outputId));
  const presentationResults = useMemo(() => (presentationWorkflow === workflow ? workflowResults : workflowResultsFrom(presentationWorkflow)), [presentationWorkflow, workflow, workflowResults])
  const presentationAstResult = presentationResults.find((result): result is AstResult => result.resultType === 'ast')
  const presentationAstSourceMap = useMemo(() => (presentationAstResult ? createAstSourceMap(presentationAstResult.document) : null), [presentationAstResult])
  const presentationRunResult = presentationResults.find((result): result is RunResult => result.resultType === 'run')
  const presentationJitResult = presentationResults.find((result) => result.resultType === 'jit')
  const presentationEvents = useMemo(() => (presentationWorkflow === workflow ? workflowEvents : workflowEventsFrom(presentationWorkflow)), [presentationWorkflow, workflow, workflowEvents])
  const presentationExecutionFlowModel = useMemo(
    () => (presentationWorkflow?.outputId === 'execution-flow' ? createExecutionFlowSourceModel(presentationEvents, presentationWorkflow.workspaceFiles) : createExecutionFlowSourceModel([], [])),
    [presentationEvents, presentationWorkflow],
  )
  const resultIdentitySummary = useMemo(() => summarizeResultIdentities(presentationResults), [presentationResults])
  const workflowOutput = catalog && workflow ? outputById(catalog, workflow.outputId) : output
  const resultSelection = presentationWorkflow?.resolution.effectiveSelection
  const workflowOperationIds = useMemo(() => [...(presentationWorkflow?.completedStages.map((stage) => stage.operationId) ?? []), ...(presentationWorkflow?.active ? [presentationWorkflow.active.operationId] : [])], [presentationWorkflow])
  const identityPresentation = useMemo(
    () =>
      createResultIdentityPresentation({
        summary: resultIdentitySummary,
        catalog,
        catalogRevision: presentationWorkflow?.catalogRevision ?? catalog?.revision,
        referenceSetSnapshot: presentationWorkflow?.referenceSetSnapshot,
        resolution: presentationWorkflow?.resolution ?? resolutionState.resolution ?? undefined,
        selection: resultSelection,
        output: workflowOutput,
        fallback: {
          languageId,
          toolchainId,
          referenceSetId,
          outputId,
          runtimeId,
        },
        buildMode: presentationWorkflow?.buildMode ?? buildMode,
        operationIds: workflowOperationIds,
      }),
    [
      buildMode,
      catalog,
      languageId,
      outputId,
      referenceSetId,
      resultIdentitySummary,
      resultSelection,
      resolutionState.resolution,
      runtimeId,
      toolchainId,
      presentationWorkflow?.buildMode,
      presentationWorkflow?.catalogRevision,
      presentationWorkflow?.referenceSetSnapshot,
      presentationWorkflow?.resolution,
      workflowOperationIds,
      workflowOutput,
    ],
  )
  const workflowError = workflowFailure(workflow, workflowResults, workflowEvents, workflowTargetReady)
  const currentFailure = firstVisibleFailure(resolutionState.error, ...(workflowIsStale ? [] : [workflowError, startOperationMutation.error, operationStateQuery.error, operationEvents.streamError]))
  const operationTransportDisconnected = gatewayConnectionStatus === 'reconnecting' || gatewayConnectionStatus === 'closed' || (activeOperationId !== null && operationEvents.streamStatus === 'error')
  const gatewayTransportFailure = [catalogQuery.error, resolutionState.error, workflowError, startOperationMutation.error, operationStateQuery.error, operationEvents.streamError].find(isGatewayTransportFailure)
  const currentHasErrorDiagnostic =
    workflowResults.some((result) => 'diagnostics' in result && Array.isArray(result.diagnostics) && result.diagnostics.some((diagnostic) => diagnostic.severity === 'error')) ||
    workflowEvents.some((event) => event.payload.kind === 'diagnostic' && event.payload.diagnostic.severity === 'error')
  const resultAttentionKey = resolutionState.error ? `selection-resolution:${selectionRevision}:${resolutionState.error.message}` : !workflowIsStale && workflow && (currentFailure || currentHasErrorDiagnostic) ? workflow.id : null
  const resultRecoveryKey =
    !resolutionState.error && resolutionState.resolution
      ? `selection-resolution:${resolutionState.resolution.pipelineResolutionId}`
      : !workflowIsStale && workflow && workflowTargetReady && !currentFailure && !currentHasErrorDiagnostic
        ? workflow.id
        : null
  const resultPending = workflow?.active != null || startOperationMutation.isPending
  const resultVisualStatus = workflowIsStale ? 'stale' : currentFailure ? 'failed' : operationStatus

  const navigateToResultSource = useCallback((target: ExecutionFlowSourceTarget) => {
    const current = workflowRef.current
    const state = useWorkbenchStore.getState()
    const supportedOutput = current?.outputId === 'execution-flow' || current?.outputId === 'ast' || current?.outputId === 'jit-asm' || current?.outputId === 'il' || current?.outputId === 'run-il' || current?.outputId === 'generated-il'
    const sourceFile = state.files.find((file) => file.path === target.documentPath)
    if (
      !current ||
      !supportedOutput ||
      state.outputId !== current.outputId ||
      current.workspaceRevision !== state.workspaceRevision ||
      current.selectionRevision !== state.selectionRevision ||
      !sourceFile ||
      validateSourceRange(sourceFile.text, target.range)
    ) {
      return
    }

    state.selectFile(target.documentPath)
    state.setMobilePane('code')
    setActiveSourceAssociation({
      associationKey: sourceAssociationKey(target),
      generationId: current.id,
    })
    setActiveSourceAssociationRevision((revision) => revision + 1)
    sourceNavigationRevision.current += 1
    setSourceNavigation({
      ...target,
      revision: sourceNavigationRevision.current,
    })
  }, [])

  const handleSourceAssociationsChange = useCallback((associations: readonly SourceAssociation[]) => setSourceAssociations(associations), [])

  const updateAssociatedOutput = useCallback(
    (associationKey: string, revealResult: boolean) => {
      const current = workflowRef.current
      const state = useWorkbenchStore.getState()
      if (!current || current.workspaceRevision !== state.workspaceRevision || current.selectionRevision !== state.selectionRevision || !sourceAssociations.some((association) => association.key === associationKey)) {
        return
      }
      setActiveSourceAssociation({ associationKey, generationId: current.id })
      setActiveSourceAssociationRevision((revision) => revision + 1)
      if (revealResult) state.setMobilePane('result')
    },
    [sourceAssociations],
  )
  const navigateToAssociatedOutput = useCallback((associationKey: string) => updateAssociatedOutput(associationKey, true), [updateAssociatedOutput])
  const previewAssociatedOutput = useCallback((associationKey: string) => updateAssociatedOutput(associationKey, false), [updateAssociatedOutput])

  useEffect(() => {
    if (presentationSourceNavigationEnabled || presentationSourceAssociationsRetained) return
    setSourceNavigation(null)
    setSourceAssociations([])
    setActiveSourceAssociation(null)
    setHoveredSourceAssociation(null)
  }, [presentationSourceAssociationsRetained, presentationSourceNavigationEnabled])

  useEffect(() => {
    if (activeSourceAssociation && (activeSourceAssociation.generationId !== workflow?.id || !sourceAssociations.some((association) => association.key === activeSourceAssociation.associationKey))) {
      setActiveSourceAssociation(null)
    }
  }, [activeSourceAssociation, sourceAssociations, workflow?.id])

  useEffect(() => {
    if (hoveredSourceAssociation && (hoveredSourceAssociation.generationId !== workflow?.id || !sourceAssociations.some((association) => association.key === hoveredSourceAssociation.associationKey))) {
      setHoveredSourceAssociation(null)
    }
  }, [hoveredSourceAssociation, sourceAssociations, workflow?.id])

  const handleSourceAssociationHover = useCallback((associationKey: string | null) => {
    const generationId = workflowRef.current?.id
    setHoveredSourceAssociation(associationKey && generationId ? { associationKey, generationId } : null)
  }, [])

  const handleWorkbenchPointerDownCapture = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    if (isSourceAssociationInteractionTarget(event.target)) return
    setActiveSourceAssociation(null)
    setHoveredSourceAssociation(null)
  }, [])

  useEffect(() => {
    const current = workflowRef.current
    const active = current?.active
    if (!current || !active || processedOperations.current.has(active.operationId)) return

    const terminalEvent = [...operationEvents.events].reverse().find((event) => event.payload.kind === 'completed' || event.payload.kind === 'failed')
    const stateStatus = operationStateQuery.data?.status
    const stateIsTerminal = isOperationTerminal(stateStatus)
    if (!terminalEvent && !stateIsTerminal) return

    const terminalStatus: WorkflowStatus =
      stateStatus === 'failed' || terminalEvent?.payload.kind === 'failed' ? 'failed' : stateStatus === 'cancelled' || (terminalEvent?.payload.kind === 'completed' && terminalEvent.payload.status === 'cancelled') ? 'cancelled' : 'completed'
    if (terminalStatus === 'completed' && !typedResult && operationEvents.streamStatus !== 'closed' && operationEvents.streamStatus !== 'error') {
      return
    }

    processedOperations.current.add(active.operationId)
    const stageResult: WorkflowStageResult = {
      ...active,
      events: [...operationEvents.events],
      result: typedResult,
    }
    const store = useWorkbenchStore.getState()
    const stale = store.workspaceRevision !== current.workspaceRevision || store.selectionRevision !== current.selectionRevision
    const missingTypedResult = terminalStatus === 'completed' && !typedResult
    const effectiveTerminalStatus = missingTypedResult ? 'failed' : terminalStatus
    const terminalError = missingTypedResult
      ? new Error('The operation completed without its required typed result.')
      : terminalStatus === 'failed'
        ? new Error(operationStateQuery.data?.error?.publicMessage ?? (terminalEvent?.payload.kind === 'failed' ? terminalEvent.payload.error.publicMessage : 'The operation failed.'))
        : null

    const producedArtifactRef =
      active.kind === 'build' && typedResult?.resultType === 'build' && typedResult.outcome === 'succeeded'
        ? typedResult.artifactRef
        : active.kind === 'transform' && typedResult?.resultType === 'artifact-transform' && typedResult.outcome === 'succeeded'
          ? typedResult.artifactRef
          : null
    if (terminalStatus === 'completed' && producedArtifactRef) {
      let followup: ReturnType<typeof createFollowupOperation>
      try {
        followup = createFollowupOperation(current.resolution, producedArtifactRef, current.completedStages.length + 1)
      } catch (error) {
        updateWorkflow((candidate) =>
          candidate?.id === current.id
            ? {
                ...candidate,
                active: null,
                completedStages: [...candidate.completedStages, stageResult],
                artifactRef: producedArtifactRef,
                status: 'failed',
                error: error instanceof Error ? error : new Error('Invalid pipeline plan.'),
              }
            : candidate,
        )
        return
      }

      if (followup && !stale) {
        updateWorkflow((candidate) =>
          candidate?.id === current.id
            ? {
                ...candidate,
                active: null,
                completedStages: [...candidate.completedStages, stageResult],
                artifactRef: producedArtifactRef,
                status: 'starting',
                error: null,
              }
            : candidate,
        )
        startOperationMutation.mutate({
          workflowId: current.id,
          label: followup.stage.id,
          operation: followup.start,
        })
        return
      }
    }

    let contentDescriptor: { contentRef: string; mediaType: string } | null = null
    if (typedResult?.resultType === 'artifact-render' && typedResult.contentRef) {
      contentDescriptor = {
        contentRef: typedResult.contentRef,
        mediaType: typedResult.mediaType,
      }
    } else if (typedResult?.resultType === 'jit' && typedResult.rawTextRef) {
      contentDescriptor = {
        contentRef: typedResult.rawTextRef,
        mediaType: 'text/x-asm',
      }
    }

    const missingRenderedContent = typedResult?.resultType === 'artifact-render' && typedResult.outcome === 'succeeded' && !typedResult.contentRef
    const content: OperationContentView | null = contentDescriptor
      ? {
          ...contentDescriptor,
          text: null,
          loading: true,
          error: null,
        }
      : missingRenderedContent
        ? {
            contentRef: '',
            mediaType: typedResult.mediaType,
            text: null,
            loading: false,
            error: new Error('The renderer completed without a content reference.'),
          }
        : current.content
    const generatedSourceContents: GeneratedSourceContentView[] =
      typedResult?.resultType === 'generated-source'
        ? typedResult.documents.map((document) => ({
            ...document,
            text: null,
            loading: true,
            error: null,
          }))
        : current.generatedSourceContents

    updateWorkflow((candidate) =>
      candidate?.id === current.id
        ? {
            ...candidate,
            active: null,
            completedStages: [...candidate.completedStages, stageResult],
            status: stale ? 'stale' : effectiveTerminalStatus,
            content,
            generatedSourceContents,
            error: terminalError,
          }
        : candidate,
    )

    if (contentDescriptor || generatedSourceContents.length > 0) {
      contentRequest.current?.abort()
      const controller = new AbortController()
      contentRequest.current = controller
      if (contentDescriptor) {
        void getOperationContent(active.operationId, contentDescriptor.contentRef, controller.signal)
          .then((text) => {
            updateWorkflow((candidate) =>
              candidate?.id === current.id && candidate.content?.contentRef === contentDescriptor.contentRef
                ? {
                    ...candidate,
                    content: {
                      ...candidate.content,
                      text,
                      loading: false,
                      error: null,
                    },
                  }
                : candidate,
            )
          })
          .catch((error: unknown) => {
            if (controller.signal.aborted) return
            updateWorkflow((candidate) =>
              candidate?.id === current.id && candidate.content?.contentRef === contentDescriptor.contentRef
                ? {
                    ...candidate,
                    content: {
                      ...candidate.content,
                      loading: false,
                      error: error instanceof Error ? error : new Error('The generated content could not be loaded.'),
                    },
                  }
                : candidate,
            )
          })
      }
      for (const document of generatedSourceContents) {
        void getOperationContent(active.operationId, document.contentRef, controller.signal)
          .then((text) => {
            updateWorkflow((candidate) =>
              candidate?.id === current.id && candidate.generatedSourceContents.some((candidateDocument) => candidateDocument.contentRef === document.contentRef && candidateDocument.path === document.path)
                ? {
                    ...candidate,
                    generatedSourceContents: candidate.generatedSourceContents.map((candidateDocument) =>
                      candidateDocument.contentRef === document.contentRef && candidateDocument.path === document.path
                        ? {
                            ...candidateDocument,
                            text,
                            loading: false,
                            error: null,
                          }
                        : candidateDocument,
                    ),
                  }
                : candidate,
            )
          })
          .catch((error: unknown) => {
            if (controller.signal.aborted) return
            updateWorkflow((candidate) =>
              candidate?.id === current.id && candidate.generatedSourceContents.some((candidateDocument) => candidateDocument.contentRef === document.contentRef && candidateDocument.path === document.path)
                ? {
                    ...candidate,
                    generatedSourceContents: candidate.generatedSourceContents.map((candidateDocument) =>
                      candidateDocument.contentRef === document.contentRef && candidateDocument.path === document.path
                        ? {
                            ...candidateDocument,
                            loading: false,
                            error: error instanceof Error ? error : new Error('The generated source could not be loaded.'),
                          }
                        : candidateDocument,
                    ),
                  }
                : candidate,
            )
          })
      }
    }
  }, [operationEvents.events, operationEvents.streamStatus, operationStateQuery.data, startOperationMutation, typedResult, updateWorkflow])

  const copyShareUrl = async () => {
    if (!catalog || !navigator.clipboard) return
    const state = createShareWorkspaceState(catalog, {
      languageId,
      toolchainId,
      referenceSetId,
      outputId,
      runtimeId,
      buildMode,
      files,
      activeFile,
      sourceOrder: useWorkbenchStore.getState().sourceOrder,
    })
    try {
      let encoded: Awaited<ReturnType<typeof encodeV3>>
      if (typeof Worker === 'undefined') {
        encoded = await encodeV3(state, {
          profile: 'share',
          baseUrl: currentBaseUrl(),
        })
      } else {
        const codec = urlCodec.current ?? new UrlCodecWorkerClient()
        urlCodec.current = codec
        encoded = await codec.encodeV3(state, {
          profile: 'share',
          baseUrl: currentBaseUrl(),
        })
      }
      const url = `${currentBaseUrl()}${encoded.fragment}`
      await navigator.clipboard.writeText(url)
      setShareWarnings(encoded.lengthDisposition === 'explicit-warning' ? ['This workspace exceeds the preferred URL length; use a Gist for reliable sharing.'] : [])
      setShareCopied(true)
      window.setTimeout(() => setShareCopied(false), 1_500)
    } catch (error) {
      setShareError({
        action: 'create',
        error: normalizeError(error, 'The share URL could not be created.'),
      })
    }
  }

  const runBuild = (trigger: 'live' | 'manual' = 'manual') => {
    if (!resolutionState.resolution) return
    const snapshot = getWorkbenchSnapshot()
    const workflowId = `workflow_${globalThis.crypto?.randomUUID?.() ?? Date.now().toString(36)}`
    const selectedReferenceSet = catalog?.referenceSets.find((candidate) => candidate.id === resolutionState.resolution?.effectiveSelection.referenceSetId)
    const next: OperationWorkflow = {
      id: workflowId,
      trigger,
      resolution: resolutionState.resolution,
      outputId: resolutionState.resolution.effectiveSelection.outputId,
      buildMode: snapshot.buildMode,
      catalogRevision: catalog?.revision ?? 'unavailable',
      referenceSetSnapshot: selectedReferenceSet
        ? {
            id: selectedReferenceSet.id,
            displayName: referenceSetDisplayName(selectedReferenceSet),
            digest: selectedReferenceSet.digest,
          }
        : null,
      workspaceRevision: snapshot.workspaceRevision,
      selectionRevision: snapshot.selectionRevision,
      workspaceFiles: snapshot.files,
      status: 'starting',
      active: null,
      completedStages: [],
      artifactRef: null,
      content: null,
      generatedSourceContents: [],
      error: null,
    }
    contentRequest.current?.abort()
    processedOperations.current.clear()
    startOperationMutation.reset()
    workflowRef.current = next
    setWorkflow(next)
    let operation: PipelineOperationStart
    try {
      operation = createInitialPipelineOperation(resolutionState.resolution, snapshot)
    } catch (error) {
      updateWorkflow((current) =>
        current?.id === workflowId
          ? {
              ...current,
              status: 'failed',
              error: error instanceof Error ? error : new Error('Invalid initial pipeline stage.'),
            }
          : current,
      )
      return
    }
    startOperationMutation.mutate({
      workflowId,
      label: operation.kind,
      operation,
    })
  }
  runBuildRef.current = runBuild

  useEffect(() => {
    const current = workflowRef.current
    const active = current?.active
    if (current?.trigger !== 'live' || !active || active.operationId !== activeOperationId) return
    const stale = current.workspaceRevision !== workspaceRevision || current.selectionRevision !== selectionRevision
    if (!stale || supersededLiveOperation.current === active.operationId) return

    supersededLiveOperation.current = active.operationId
    updateWorkflow((candidate) => (candidate?.id === current.id ? { ...candidate, status: 'cancelling' } : candidate))
    void cancelOperation(active.operationId).catch(() => {
      // A failed cancellation still remains revision-guarded and cannot publish a follow-up Run.
    })
  }, [activeOperationId, selectionRevision, updateWorkflow, workspaceRevision])

  useEffect(() => {
    if (supersededLiveOperation.current !== activeOperationId) {
      supersededLiveOperation.current = null
    }
  }, [activeOperationId])

  useEffect(() => {
    const resolution = resolutionState.resolution
    const debounceMs = resolution ? liveOperationDebounceMs(resolution.effectiveSelection.outputId) : null
    if (!shareReady || !catalog || !resolution || resolutionState.isResolving) {
      return
    }

    const isBootstrapResolution = resolutionState.isInitialSnapshot
    const operationDelayMs = isBootstrapResolution ? 0 : debounceMs
    if (profileUnavailable !== null || startOperationMutation.isPending || operationDelayMs === null) {
      return
    }

    const current = workflow
    const currentRevisionAlreadyRequested = Boolean(current && current.workspaceRevision === workspaceRevision && current.selectionRevision === selectionRevision && current.outputId === resolution.effectiveSelection.outputId)
    if (currentRevisionAlreadyRequested || current?.active) return

    const timer = window.setTimeout(() => {
      const state = useWorkbenchStore.getState()
      if (state.workspaceRevision !== workspaceRevision || state.selectionRevision !== selectionRevision) {
        return
      }
      const currentWorkflow = workflowRef.current
      if (currentWorkflow?.workspaceRevision === workspaceRevision && currentWorkflow.selectionRevision === selectionRevision && currentWorkflow.outputId === resolution.effectiveSelection.outputId) {
        return
      }
      runBuildRef.current('live')
    }, operationDelayMs)
    return () => window.clearTimeout(timer)
  }, [catalog, profileUnavailable, resolutionState.isResolving, resolutionState.isInitialSnapshot, resolutionState.resolution, selectionRevision, shareReady, startOperationMutation.isPending, workflow, workspaceRevision])

  const gistWorkspace = useMemo(
    () =>
      catalog
        ? createGistWorkspaceState(catalog, {
            languageId,
            toolchainId,
            referenceSetId,
            outputId,
            runtimeId,
            buildMode,
            files,
            activeFile,
            sourceOrder,
          })
        : null,
    [activeFile, buildMode, catalog, files, languageId, outputId, referenceSetId, runtimeId, sourceOrder, toolchainId],
  )

  const onGistSaved = (gist: GistDocument) => {
    shareUrlWriteGeneration.current += 1
    const state = useWorkbenchStore.getState()
    gistBaseline.current = {
      id: gist.id,
      workspaceRevision: state.workspaceRevision,
      selectionRevision: state.selectionRevision,
    }
    setCurrentGist(gist)
    setShareWarnings(gist.warnings)
    setGistDialogOpen(false)
    window.history.replaceState(window.history.state, '', gistFragment(gist.id))
  }

  const createFile = () => {
    const path = nextFileName(language.defaultFileName, files.map((file) => file.path))
    addFile(path)
  }

  const beginRename = (path: string) => {
    setRenamingPath(path)
    setRenameDraft(path)
  }

  const commitRename = () => {
    if (renamingPath && renameDraft !== renamingPath) {
      renameFile(renamingPath, renameDraft.trim())
    }
    setRenamingPath(null)
  }

  let healthLabel = 'Connected'
  let healthState = 'ready'
  if (catalogQuery.isPending) {
    healthLabel = 'Connecting'
    healthState = 'pending'
  } else if (catalogQuery.error || gatewayTransportFailure || operationTransportDisconnected) {
    healthLabel = 'Gateway unavailable'
    healthState = 'error'
  } else if (resolutionState.error) {
    healthLabel = 'Selection unavailable'
    healthState = 'error'
  } else if (profileUnavailable) {
    healthLabel = 'Development profile'
    healthState = 'warning'
  }

  if (!shareReady && initialShareFragment) {
    return (
      <div className="workbench workbench--restoring" aria-busy="true">
        <header className="app-bar">
          <div className="brand">
            <img className="brand-mark" src="/logo-mark.svg" alt="" aria-hidden="true" />
          </div>
        </header>
        <main className="share-restore-stage" role="status" aria-label="Restoring shared workspace">
          <LoaderCircle className="share-restore-spinner" aria-hidden="true" size={18} />
        </main>
        <footer className="status-bar" aria-hidden="true" />
      </div>
    )
  }

  return (
    <div
      className="workbench"
      style={
        {
          '--code-font-size': `${editorPreference.fontSize}px`,
        } as CSSProperties
      }
      onPointerDownCapture={handleWorkbenchPointerDownCapture}
    >
      <header className="app-bar" data-health-state={healthState}>
        <div className="brand">
          <img className="brand-mark" src="/logo-mark.svg" alt="" aria-hidden="true" />
        </div>
        <div className="mobile-command-bar">
          <SelectField label="View" description="Output view" value={outputId} compact disabled={!catalog} onChange={(value) => updateSelection({ outputId: value })}>
            {availableOutputs.map((option) => (
              <option key={option.id} value={option.id}>
                {option.displayName}
              </option>
            ))}
          </SelectField>
          <button className="run-button" type="button" disabled={runDisabled} title={resolutionState.error?.message} onClick={() => runBuild('manual')}>
            {startOperationMutation.isPending ? <LoaderCircle className="spin" aria-hidden="true" size={15} /> : <Play aria-hidden="true" size={15} fill="currentColor" />}
            <span>{actionLabel(output)}</span>
          </button>
        </div>
        <div className="app-bar-actions">
          <button className="app-bar-button" type="button" title="Save to GitHub Gist" aria-label="Save to GitHub Gist" disabled={!catalog || !shareReady} onClick={() => setGistDialogOpen(true)}>
            <GitFork aria-hidden="true" size={15} />
          </button>
          <button className="app-bar-button" type="button" title="Copy share URL" aria-label="Copy share URL" disabled={!catalog || !shareReady} onClick={() => void copyShareUrl()}>
            {shareCopied ? <Check aria-hidden="true" size={15} /> : <Link2 aria-hidden="true" size={15} />}
          </button>
          {editorPreference.isMobileViewport && (
            <button
              className="app-bar-button mobile-files-button"
              type="button"
              title={`Workspace files, current ${activeFile}`}
              aria-label={`Workspace files, current ${activeFile}`}
              aria-expanded={mobileFilesExpanded}
              aria-controls="workspace-file-tabs"
              onClick={() => {
                setMobileFilesExpanded((expanded) => !expanded)
                setMobileSettingsOpen(false)
                setEditorSettingsOpen(false)
              }}
            >
              <FileCode2 aria-hidden="true" size={15} />
            </button>
          )}
          <button
            className="app-bar-button mobile-settings-button"
            type="button"
            title="Workbench settings"
            aria-label="Workbench settings"
            aria-expanded={mobileSettingsOpen}
            onClick={() => {
              setMobileSettingsOpen((open) => !open)
              setMobileFilesExpanded(false)
              setEditorSettingsOpen(false)
            }}
          >
            <Settings aria-hidden="true" size={16} />
          </button>
          {healthState !== 'ready' && (
            <div
              className="app-health"
              data-state={healthState}
              role="status"
              aria-label={healthState === 'warning' ? (profileUnavailable ?? healthLabel) : healthLabel}
              title={healthState === 'warning' ? (profileUnavailable ?? healthLabel) : healthLabel}
            >
              {healthState === 'error' ? <WifiOff aria-hidden="true" size={14} /> : healthState === 'pending' ? <LoaderCircle className="spin" aria-hidden="true" size={14} /> : <AlertTriangle aria-hidden="true" size={14} />}
            </div>
          )}
        </div>

        <div className="selector-bar" data-mobile-open={mobileSettingsOpen}>
          <div className="selector-group selector-group--source">
            <SelectField label="Language" description="Source language" value={languageId} disabled={!catalog} onChange={updateLanguage}>
              {catalog ? (
                catalog.languages.map((option) => (
                  <option key={option.id} value={option.id}>
                    {option.displayName}
                  </option>
                ))
              ) : (
                <option value={languageId}>Catalog unavailable</option>
              )}
            </SelectField>
            <SelectField label="Toolchain" description="Compiler toolchain" className="select-field--toolchain" value={toolchainId ?? ''} disabled={!catalog} onChange={(value) => updateSelection({ toolchainId: value, runtimeId: null })}>
              {availableToolchains.map((option) => (
                <option key={option.id} value={option.id} disabled={!option.availability.installed} title={option.availability.reason}>
                  {option.displayName}
                  {availabilityLabel(option.availability.health)}
                </option>
              ))}
            </SelectField>
            <SelectField
              label="Reference set"
              description="Reference set used for compilation"
              className="select-field--api"
              value={referenceSetId ?? ''}
              compact
              disabled={!catalog}
              onChange={(value) => updateSelection({ referenceSetId: value, runtimeId: null })}
            >
              {availableReferenceSets.map((option) => (
                <option key={option.id} value={option.id} disabled={!option.availability.installed} title={option.availability.reason}>
                  {referenceSetDisplayName(option)}
                  {availabilityLabel(option.availability.health)}
                </option>
              ))}
            </SelectField>
          </div>

          <div className="selector-divider" aria-hidden="true" />

          <div className={`selector-group selector-group--result${output?.requiresRuntime ? ' selector-group--result-with-runtime' : ''}`}>
            <SelectField label="Output" description="Output view" value={outputId} disabled={!catalog} onChange={(value) => updateSelection({ outputId: value })}>
              {availableOutputs.map((option) => (
                <option key={option.id} value={option.id}>
                  {option.displayName}
                </option>
              ))}
            </SelectField>
            {output?.requiresRuntime && (
              <SelectField label="Runtime" description="Runtime used for Run and JIT" className="select-field--runtime" value={runtimeId ?? ''} disabled={!catalog} onChange={(value) => updateSelection({ runtimeId: value })}>
                {availableRuntimes.map((option) => (
                  <option key={option.id} value={option.id} disabled={!option.availability.installed} title={option.availability.reason}>
                    {option.displayName}
                    {availabilityLabel(option.availability.health)}
                    {nativeRuntimeOptionEndSpacing}
                  </option>
                ))}
              </SelectField>
            )}
            <fieldset className="mode-field" disabled={!catalog}>
              <legend className="visually-hidden">Mode</legend>
              <button
                className="mode-toggle"
                type="button"
                aria-label={`Build mode: ${buildMode === 'debug' ? 'Debug' : 'Release'}. Click to switch to ${buildMode === 'debug' ? 'Release' : 'Debug'}`}
                title={`Build mode: ${buildMode === 'debug' ? 'Debug' : 'Release'}. Click to switch to ${buildMode === 'debug' ? 'Release' : 'Debug'}`}
                onClick={() => setBuildMode(buildMode === 'debug' ? 'release' : 'debug')}
              >
                {buildMode === 'debug' ? 'Debug' : 'Release'}
              </button>
            </fieldset>
            <button className="run-button" type="button" disabled={runDisabled} title={resolutionState.error?.message} onClick={() => runBuild('manual')}>
              {startOperationMutation.isPending ? <LoaderCircle className="spin" aria-hidden="true" size={15} /> : <Play aria-hidden="true" size={15} fill="currentColor" />}
              <span>{actionLabel(output)}</span>
            </button>
          </div>
        </div>
      </header>

      <main
        ref={paneGrid}
        className="pane-grid"
        style={
          {
            '--source-pane-track': `${paneSplitPreference.sourcePercent}fr`,
            '--result-pane-track': `${100 - paneSplitPreference.sourcePercent}fr`,
          } as CSSProperties
        }
      >
        <section className="pane source-pane" data-active={mobilePane === 'code'} data-workbench-pane="source" aria-label="Source pane">
          <div className="file-tabs" data-mobile-expanded={mobileFilesExpanded}>
            <div id="workspace-file-tabs" className="file-tabs-list" role="tablist" aria-label="Workspace files" hidden={editorPreference.isMobileViewport && !mobileFilesExpanded}>
              {showSourceOrderControls && (
                <fieldset className="source-order-actions">
                  <legend className="visually-hidden">Source order</legend>
                  <button
                    type="button"
                    title={`Move ${activeFile} earlier in source order`}
                    aria-label={`Move ${activeFile} earlier in source order`}
                    disabled={!canMoveSourceEarlier}
                    onClick={() => moveFileInSourceOrder(activeFile, 'earlier')}
                  >
                    <ArrowLeft aria-hidden="true" size={13} />
                  </button>
                  <button type="button" title={`Move ${activeFile} later in source order`} aria-label={`Move ${activeFile} later in source order`} disabled={!canMoveSourceLater} onClick={() => moveFileInSourceOrder(activeFile, 'later')}>
                    <ArrowRight aria-hidden="true" size={13} />
                  </button>
                </fieldset>
              )}
              {displayedFiles.map((file) => (
                <div className="file-tab-group" key={file.path}>
                  {renamingPath === file.path ? (
                    <input
                      className="file-rename-input"
                      ref={renameInput}
                      aria-label="Rename file"
                      value={renameDraft}
                      onChange={(event) => setRenameDraft(event.target.value)}
                      onBlur={commitRename}
                      onKeyDown={(event) => {
                        if (event.key === 'Enter') commitRename()
                        if (event.key === 'Escape') setRenamingPath(null)
                      }}
                    />
                  ) : (
                    <button
                      className="file-tab"
                      type="button"
                      role="tab"
                      aria-selected={activeFile === file.path}
                      onClick={() => {
                        selectFile(file.path)
                        setMobileFilesExpanded(false)
                      }}
                      onDoubleClick={() => beginRename(file.path)}
                    >
                      <FileCode2 aria-hidden="true" size={14} />
                      <span>{file.path}</span>
                      <span className="dirty-indicator" aria-hidden="true" />
                    </button>
                  )}
                  {activeFile === file.path && renamingPath !== file.path && (
                    <button className="file-tab-action" type="button" title="Rename file" aria-label={`Rename ${file.path}`} onClick={() => beginRename(file.path)}>
                      <Pencil aria-hidden="true" size={12} />
                    </button>
                  )}
                  {renamingPath !== file.path && (
                    <button
                      className="file-tab-action"
                      type="button"
                      title="Close file"
                      aria-label={`Close ${file.path}`}
                      onClick={() => {
                        if (removeFile(file.path)) setMobileFilesExpanded(false)
                      }}
                    >
                      <X aria-hidden="true" size={13} />
                    </button>
                  )}
                </div>
              ))}
              <button
                className="file-add-button"
                type="button"
                title="Add file"
                aria-label="Add file"
                onClick={() => {
                  createFile()
                  setMobileFilesExpanded(false)
                }}
              >
                <FilePlus2 aria-hidden="true" size={14} />
              </button>
            </div>
          </div>
          <div className="editor-region">
            <Suspense
              fallback={
                <div className="editor-runtime-state" role="status">
                  Loading Monaco...
                </div>
              }
            >
              {editorPreference.editor === 'monaco' ? (
                <MonacoEditor
                  files={files}
                  activeFile={activeFile}
                  monacoLanguageId={language.monacoLanguageId}
                  languageSession={editorLanguageSession}
                  executionFlow={activeExecutionFlow}
                  sourceAssociations={sourceAssociations}
                  activeSourceAssociationKey={hoveredSourceAssociationKey ?? activeSourceAssociationKey}
                  sourceNavigation={sourceNavigation}
                  fontSize={editorPreference.fontSize}
                  onChange={setFileSource}
                  onSourceAssociationActivate={navigateToAssociatedOutput}
                  onSourceAssociationPreview={previewAssociatedOutput}
                />
              ) : (
                <CodeMirrorEditor
                  files={files}
                  activeFile={activeFile}
                  languageSession={editorLanguageSession}
                  executionFlow={activeExecutionFlow}
                  sourceAssociations={sourceAssociations}
                  activeSourceAssociationKey={hoveredSourceAssociationKey ?? activeSourceAssociationKey}
                  sourceNavigation={sourceNavigation}
                  fontSize={editorPreference.fontSize}
                  onChange={setFileSource}
                  onSourceAssociationActivate={navigateToAssociatedOutput}
                  onSourceAssociationPreview={previewAssociatedOutput}
                />
              )}
            </Suspense>
          </div>
        </section>

        <PaneSplitSeparator
          containerRef={paneGrid}
          isMobile={editorPreference.isMobileViewport}
          sourcePercent={paneSplitPreference.sourcePercent}
          onChange={paneSplitPreference.selectSourcePercent}
          onReset={paneSplitPreference.resetSourcePercent}
        />

        <section className="pane result-pane" data-active={mobilePane === 'result'} data-workbench-pane="result" aria-label="Result pane">
          <dl className="identity-strip identity-strip--hidden">
            {identityPresentation.items.map((item) => (
              <div key={item.id} data-identity={item.id}>
                <dt>{item.label}</dt>
                <dd title={item.title}>{item.value}</dd>
              </div>
            ))}
          </dl>

          <div className="result-body">
            <SelectionNotices changes={resolutionState.selectionChanges} />
            {shareWarnings.length > 0 && (
              <section className="selection-notices" aria-label="Share notices">
                {shareWarnings.map((warning) => (
                  <div key={warning}>
                    <AlertTriangle aria-hidden="true" size={13} />
                    <span>{warning}</span>
                  </div>
                ))}
              </section>
            )}
            {shareError && (
              <div className="result-error" role="alert">
                <XCircle aria-hidden="true" size={20} />
                <strong>{shareError.action === 'restore' ? 'Share URL could not be restored' : 'Share URL could not be created'}</strong>
                <span>{shareError.error.message}</span>
              </div>
            )}
            <OperationResults
              output={workflowOutput}
              results={presentationResults}
              events={presentationEvents}
              activityResults={workflowResults}
              activityEvents={workflowEvents}
              content={presentationWorkflow?.content ?? null}
              generatedSourceContents={presentationWorkflow?.generatedSourceContents ?? []}
              pending={resultPending}
              resultGenerationKey={presentationWorkflow?.id ?? null}
              failure={currentFailure}
              attentionKey={resultAttentionKey}
              recoveryKey={resultRecoveryKey}
              executionFlow={presentationExecutionFlowModel}
              sourceFiles={presentationWorkflow?.workspaceFiles ?? []}
              codeFontSize={editorPreference.fontSize}
              editorKind={editorPreference.editor}
              activeSourceAssociationKey={activeSourceAssociationKey}
              activeSourceAssociationRevision={activeSourceAssociationRevision}
              ilOutputLanguageSessionOptions={
                presentationWorkflow
                  ? {
                      catalogRevision: presentationWorkflow.catalogRevision,
                      referenceSetId: presentationWorkflow.resolution.effectiveSelection.referenceSetId,
                      buildMode: presentationWorkflow.buildMode,
                      workspaceRevision: presentationWorkflow.workspaceRevision,
                      selectionRevision: presentationWorkflow.selectionRevision,
                    }
                  : null
              }
              toolbarActions={
                <>
                  {(resultPending || resultVisualStatus === 'stale' || resultVisualStatus === 'failed') && (
                    <span
                      className="result-state-slot"
                      role="status"
                      aria-label={`Result ${resultVisualStatus}`}
                      title={resultVisualStatus === 'stale' ? 'Showing the previous result while the current revision updates' : resultVisualStatus === 'failed' ? 'The latest operation failed; diagnostics are selected' : 'Updating result'}
                      data-state={resultVisualStatus}
                    >
                      {resultPending ? (
                        <LoaderCircle className="result-state-spinner" aria-hidden="true" size={14} />
                      ) : resultVisualStatus === 'stale' ? (
                        <AlertTriangle aria-hidden="true" size={14} />
                      ) : (
                        <XCircle aria-hidden="true" size={14} />
                      )}
                    </span>
                  )}
                  <span className="operation-state visually-hidden" data-state={operationStatus}>
                    {operationStatus}
                  </span>
                  {activeOperationId && operationEvents.streamStatus !== 'idle' && <span className="stream-state visually-hidden">WebSocket {operationEvents.streamStatus}</span>}
                  {activeOperationId && !operationIsTerminal && (
                    <button className="icon-button result-stop-button" type="button" title="Cancel operation" aria-label="Cancel operation" disabled={cancelMutation.isPending} onClick={() => cancelMutation.mutate()}>
                      <Square aria-hidden="true" size={13} fill="currentColor" />
                    </button>
                  )}
                </>
              }
              onNavigateToSource={presentationSourceNavigationEnabled ? navigateToResultSource : undefined}
              onSourceAssociationsChange={presentationSourceNavigationEnabled ? handleSourceAssociationsChange : undefined}
              onSourceAssociationHover={presentationSourceNavigationEnabled ? handleSourceAssociationHover : undefined}
            />
          </div>
        </section>
      </main>

      <footer className="status-bar">
        <div className="status-result-bar">
          {outputId === 'ast' && presentationAstResult && <AstStatus document={presentationAstResult.document} nodeCount={presentationAstSourceMap?.nodeCount ?? 0} />}
          <RunStatus result={presentationRunResult} />
          <JitStatus result={presentationJitResult} />
          <div className="status-editor-settings">
            <button
              className="status-editor-settings-toggle"
              type="button"
              title="Editor settings"
              aria-label="Editor settings"
              aria-expanded={editorSettingsOpen}
              onClick={() => {
                setEditorSettingsOpen((open) => !open)
                setMobileSettingsOpen(false)
              }}
            >
              <Settings aria-hidden="true" size={13} />
              <span>{editorPreference.fontSize}px</span>
            </button>
            <div className="status-editor-settings-panel" data-mobile-open={editorSettingsOpen}>
              <button
                className="status-editor-switch"
                type="button"
                aria-label={`Editor: ${editorPreference.editor === 'monaco' ? 'Monaco' : 'CodeMirror'}. Click to switch to ${editorPreference.editor === 'monaco' ? 'CodeMirror' : 'Monaco'}`}
                title={`Editor: ${editorPreference.editor === 'monaco' ? 'Monaco' : 'CodeMirror'}. Click to switch to ${editorPreference.editor === 'monaco' ? 'CodeMirror' : 'Monaco'}`}
                onClick={() => editorPreference.selectEditor(editorPreference.editor === 'monaco' ? 'codemirror' : 'monaco')}
              >
                <span className="status-editor-switch-label">Editor:</span>
                <span>{editorPreference.editor === 'monaco' ? 'Monaco' : 'CodeMirror'}</span>
              </button>
              <fieldset className="status-font-size">
                <legend className="visually-hidden">Code font size</legend>
                <span aria-hidden="true">Font</span>
                <button
                  type="button"
                  title="Decrease code font size"
                  aria-label="Decrease code font size"
                  disabled={editorPreference.fontSize === editorFontSizeOptions[0]}
                  onClick={() => {
                    const index = editorFontSizeOptions.indexOf(editorPreference.fontSize)
                    const next = editorFontSizeOptions[index - 1]
                    if (next !== undefined) editorPreference.selectFontSize(next)
                  }}
                >
                  <Minus aria-hidden="true" size={12} />
                </button>
                <output aria-label="Current code font size">{editorPreference.fontSize}px</output>
                <button
                  type="button"
                  title="Increase code font size"
                  aria-label="Increase code font size"
                  disabled={editorPreference.fontSize === editorFontSizeOptions[editorFontSizeOptions.length - 1]}
                  onClick={() => {
                    const index = editorFontSizeOptions.indexOf(editorPreference.fontSize)
                    const next = editorFontSizeOptions[index + 1]
                    if (next !== undefined) editorPreference.selectFontSize(next)
                  }}
                >
                  <Plus aria-hidden="true" size={12} />
                </button>
              </fieldset>
            </div>
          </div>
        </div>
      </footer>
      <GistDialog open={gistDialogOpen} workspace={gistWorkspace} currentGist={currentGist} onClose={() => setGistDialogOpen(false)} onSaved={onGistSaved} />
    </div>
  )
}

export default App
