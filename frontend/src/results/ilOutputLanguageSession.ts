import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, openLanguageSessionWithResolution, resolveSelectionHttp } from '../api/client'
import type {
  BuildConfiguration,
  ResolveSelectionRequest,
  ResolveSelectionResponse,
} from '../api/types'
import {
  CodeMirrorLanguageBridge,
  type CodeMirrorLspHover,
  type CodeMirrorSemanticToken,
  createCodeMirrorLanguageSessionDependencies,
  type LspPosition,
  readOnlyIlOutputLanguageClientFeatureProfile,
} from '../lsp/codeMirrorLanguageClient'
import { createLanguageWorkspaceUri } from '../lsp/languageDocumentUri'
import {
  type LanguageSessionInitialRetryContext,
  type LanguageSessionInitialRetryPolicy,
  LanguageSessionLifecycle,
  type LanguageSessionStatus,
  LanguageSessionTransportError,
} from '../lsp/languageSessionLifecycle'
import { createWorkbenchBuildOptions } from '../workbench/buildOptions'

const outputPath = 'Output.il'
const selectionResolutionRetryInitialDelayMs = 250
const selectionResolutionRetryMaximumDelayMs = 4_000

const ilOutputLanguageSessionInitialRetryPolicy: LanguageSessionInitialRetryPolicy = {
  initialDelayMs: 250,
  maximumDelayMs: 4_000,
  shouldRetry: isTransientIlOutputLanguageSessionFailure,
}

export interface IlOutputLanguageSessionOptions {
  catalogRevision: string
  referenceSetId: string
  buildMode: BuildConfiguration
  workspaceRevision: number
  selectionRevision: number
}

export interface IlOutputLanguageSession {
  semanticTokens: readonly CodeMirrorSemanticToken[]
  status: LanguageSessionStatus
  hover: (position: LspPosition) => Promise<CodeMirrorLspHover | null>
}

/**
 * Opens a short-lived, read-only IL language session for a result document.
 * This is deliberately separate from the source editor session: a C# source
 * session cannot be used to analyze its generated IL, and changing the shared
 * selection while recovering this session would invalidate source LSP state.
 */
export function useIlOutputLanguageSession(
  text: string,
  generationKey: string | null,
  options: IlOutputLanguageSessionOptions | null | undefined,
): IlOutputLanguageSession {
  const bridgeRef = useRef<CodeMirrorLanguageBridge | null>(null)
  const lifecycleRef = useRef<LanguageSessionLifecycle | null>(null)
  const workspaceIdRef = useRef<string | null>(null)
  const resolutionRequestRef = useRef<ResolveSelectionRequest | null>(null)
  const epochRef = useRef(0)
  const [semanticTokens, setSemanticTokens] = useState<readonly CodeMirrorSemanticToken[]>([])
  const [status, setStatus] = useState<LanguageSessionStatus>('disabled')

  if (!workspaceIdRef.current) {
    const id = globalThis.crypto?.randomUUID?.() ?? Math.random().toString(36).slice(2)
    workspaceIdRef.current = `il-output-${id.toLowerCase()}`
  }
  const workspaceId = workspaceIdRef.current
  useEffect(() => {
    const bridge = new CodeMirrorLanguageBridge()
    bridgeRef.current = bridge
    let lifecycle: LanguageSessionLifecycle
    const isCurrent = () => lifecycleRef.current === lifecycle
    const baseDependencies = createCodeMirrorLanguageSessionDependencies(
      bridge,
      {
        publishDiagnostics: () => {},
        publishSemanticTokens: (path, _version, tokens) => {
          if (!isCurrent() || path !== outputPath) return
          setSemanticTokens(tokens)
        },
        publishDocumentSymbols: () => {},
        publishFoldingRanges: () => {},
        clearDocument: (path) => {
          if (isCurrent() && path === outputPath) setSemanticTokens([])
        },
      },
      readOnlyIlOutputLanguageClientFeatureProfile,
    )
    lifecycle = new LanguageSessionLifecycle(
      (change) => {
        if (!isCurrent()) return
        setStatus(change.status)
        bridge.setSessionStatus(change.status)
        if (change.status !== 'ready') setSemanticTokens([])
      },
      {
        ...baseDependencies,
        open: (request, signal) => {
          const resolutionRequest = resolutionRequestRef.current
          if (!resolutionRequest) {
            throw new Error('The IL output language-session resolution is unavailable.')
          }
          return openLanguageSessionWithResolution(request, resolutionRequest, signal)
        },
      },
      ilOutputLanguageSessionInitialRetryPolicy,
    )
    lifecycleRef.current = lifecycle

    return () => {
      epochRef.current += 1
      resolutionRequestRef.current = null
      if (lifecycleRef.current === lifecycle) lifecycleRef.current = null
      if (bridgeRef.current === bridge) bridgeRef.current = null
      void lifecycle.dispose()
    }
  }, [])

  const enabled = options !== null && options !== undefined
  const catalogRevision = options?.catalogRevision ?? null
  const referenceSetId = options?.referenceSetId ?? null
  const buildMode = options?.buildMode ?? null
  const workspaceRevision = options?.workspaceRevision ?? null
  const selectionRevision = options?.selectionRevision ?? null

  useEffect(() => {
    const lifecycle = lifecycleRef.current
    if (!lifecycle) return
    const epoch = ++epochRef.current
    const abort = new AbortController()
    setSemanticTokens([])
    resolutionRequestRef.current = null

    if (
      !enabled ||
      catalogRevision === null ||
      referenceSetId === null ||
      buildMode === null ||
      workspaceRevision === null ||
      selectionRevision === null ||
      text.length === 0
    ) {
      lifecycle.update(null)
      setStatus('disabled')
      return () => abort.abort()
    }

    const resolutionRequest: ResolveSelectionRequest = {
      languageId: 'il',
      toolchainId: 'mobius-ilasm-stable',
      referenceSetId,
      outputId: 'il',
      runtimeId: null,
      buildMode,
      catalogRevision,
      workspaceRevision,
    }
    resolutionRequestRef.current = resolutionRequest
    setStatus('connecting')

    void resolveSelectionWithRetry(resolutionRequest, abort.signal)
      .then((resolution) => {
        if (abort.signal.aborted || epoch !== epochRef.current) return
        const selection = resolution.effectiveSelection
        if (
          selection.languageId !== 'il' ||
          selection.toolchainId !== 'mobius-ilasm-stable' ||
          selection.referenceSetId !== referenceSetId ||
          resolution.pipelinePlan.referenceSetId !== referenceSetId
        ) {
          throw new Error('The selected reference set does not support IL language services.')
        }
        const key = JSON.stringify([epoch, generationKey, resolution.pipelineResolutionId])
        lifecycle.update({
          key,
          plan: {
            key,
            languageId: 'il',
            modelLanguageId: 'il',
            workspaceUri: createLanguageWorkspaceUri('il', workspaceId),
            selectionRevision,
            createRequest: () => ({
              requestId: `lsp_output_${globalThis.crypto?.randomUUID?.() ?? Date.now().toString(36)}`,
              pipelineResolutionId: resolution.pipelineResolutionId,
              languageId: 'il',
              toolchainId: selection.toolchainId,
              referenceSetId: selection.referenceSetId,
              workspace: {
                schemaVersion: 1,
                revision: workspaceRevision,
                selectionRevision,
                languageId: 'il',
                files: [{ path: outputPath, version: 1, text }],
                activeFile: outputPath,
                sourceOrder: [outputPath],
                referenceSetId: selection.referenceSetId,
                buildOptions: createWorkbenchBuildOptions(
                  'il',
                  buildMode,
                  resolution.pipelinePlan.stages,
                ),
              },
              lspVersion: '3.17' as const,
            }),
          },
        })
      })
      .catch((error: unknown) => {
        if (abort.signal.aborted || epoch !== epochRef.current) return
        lifecycle.update(null)
        setStatus('error')
        // Unsupported reference sets are an expected graceful fallback for
        // output views; retain the local lexer instead of showing a failure.
        void error
      })

    return () => {
      abort.abort()
      lifecycle.update(null)
    }
  }, [
    buildMode,
    catalogRevision,
    enabled,
    generationKey,
    referenceSetId,
    selectionRevision,
    text,
    workspaceId,
    workspaceRevision,
  ])

  const hover = useCallback(
    (position: LspPosition) =>
      bridgeRef.current?.hover(outputPath, position) ?? Promise.resolve(null),
    [],
  )

  return { semanticTokens, status, hover }
}

async function resolveSelectionWithRetry(
  request: ResolveSelectionRequest,
  signal: AbortSignal,
): Promise<ResolveSelectionResponse> {
  let retryAttempt = 0
  while (true) {
    if (signal.aborted) {
      throw (
        signal.reason ??
        new DOMException('IL output selection resolution was aborted.', 'AbortError')
      )
    }
    try {
      return await resolveSelectionHttp(request, signal)
    } catch (error) {
      if (signal.aborted || !isTransientSelectionResolutionError(error)) throw error

      const delay = Math.min(
        selectionResolutionRetryInitialDelayMs * 2 ** Math.min(retryAttempt, 4),
        selectionResolutionRetryMaximumDelayMs,
      )
      retryAttempt += 1
      await waitForSelectionResolutionRetry(delay, signal)
    }
  }
}

function isTransientSelectionResolutionError(error: unknown): boolean {
  return (
    error instanceof TypeError ||
    (error instanceof ApiError &&
      (error.status === 408 || error.status === 429 || (error.status >= 500 && error.status < 600)))
  )
}

function isTransientIlOutputLanguageSessionFailure(
  error: unknown,
  context: LanguageSessionInitialRetryContext,
): boolean {
  if (error instanceof LanguageSessionTransportError) return true
  if (context.phase !== 'open') return false
  return isTransientSelectionResolutionError(error)
}

function waitForSelectionResolutionRetry(delay: number, signal: AbortSignal): Promise<void> {
  if (signal.aborted) {
    return Promise.reject(
      signal.reason ??
        new DOMException('IL output selection resolution was aborted.', 'AbortError'),
    )
  }

  return new Promise<void>((resolve, reject) => {
    const timeout = globalThis.setTimeout(() => {
      signal.removeEventListener('abort', abort)
      resolve()
    }, delay)
    const abort = () => {
      globalThis.clearTimeout(timeout)
      signal.removeEventListener('abort', abort)
      reject(
        signal.reason ??
          new DOMException('IL output selection resolution was aborted.', 'AbortError'),
      )
    }
    signal.addEventListener('abort', abort, { once: true })
    if (signal.aborted) abort()
  })
}
