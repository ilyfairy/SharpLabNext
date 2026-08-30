import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { type ReactNode, StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App, { isSourceAssociationInteractionTarget } from './App'
import { resetOperationCommandConnectionForTests } from './api/client'
import type { BuildRequest, JitRequest, OperationEvent, OperationState, RenderArtifactRequest, ResolveSelectionRequest, ResolveSelectionResponse, RunRequest } from './api/types'
import { decodeWire, stringifyWire } from './api/wire'
import * as shareCodec from './share'
import { createCatalogFixture } from './test/catalogFixture'
import { paneSplitPreferenceStorageKey } from './workbench/paneSplitPreference'
import { resetWorkbenchStore, useWorkbenchStore } from './workbench/store'

vi.mock('./editor/MonacoEditor', () => ({
  MonacoEditor: ({ files, activeFile, onChange }: { files: ReadonlyArray<{ path: string; text: string }>; activeFile: string; onChange: (path: string, source: string) => void }) => (
    <textarea aria-label="Source editor" value={files.find((file) => file.path === activeFile)?.text ?? ''} onChange={(event) => onChange(activeFile, event.target.value)} />
  ),
}))

vi.mock('./results/MonacoCodeDocumentView', () => ({
  MonacoCodeDocumentView: ({ text, ariaLabel }: { text: string; ariaLabel: string }) => <textarea readOnly aria-label={ariaLabel} value={text} />,
}))

class MockWebSocket {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSED = 3
  static instances: MockWebSocket[] = []

  readonly url: string
  readyState = MockWebSocket.CONNECTING
  onopen: (() => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onerror: (() => void) | null = null
  onclose: (() => void) | null = null

  constructor(url: string | URL) {
    this.url = url.toString()
    MockWebSocket.instances.push(this)
    queueMicrotask(() => {
      if (this.readyState !== MockWebSocket.CONNECTING) return
      this.readyState = MockWebSocket.OPEN
      this.onopen?.()
    })
  }

  send(data: string): void {
    if (!this.url.endsWith('/api/v1/operations/ws')) return
    const command = decodeWire<{
      type: 'resolve-selection' | 'start' | 'state' | 'cancel' | 'subscribe'
      commandId: string
      operation?: string
      operationId?: string
      fromSequence?: number
      reason?: string
      request?: object
    }>(JSON.parse(data))
    void this.handleOperationCommand(command)
  }

  close(_code?: number, _reason?: string): void {
    this.readyState = MockWebSocket.CLOSED
    this.onclose?.()
  }

  emitOperation(event: OperationEvent): void {
    this.onmessage?.(
      new MessageEvent('message', {
        data: stringifyWire({
          type: 'event',
          operationId: event.operationId,
          event,
        }),
      }),
    )
  }

  private async handleOperationCommand(command: {
    type: 'resolve-selection' | 'start' | 'state' | 'cancel' | 'subscribe'
    commandId: string
    operation?: string
    operationId?: string
    fromSequence?: number
    reason?: string
    request?: object
  }): Promise<void> {
    if (command.type === 'subscribe') {
      this.emitResponse(command.commandId, {
        operationId: command.operationId,
        fromSequence: command.fromSequence,
      })
      return
    }
    if (command.type === 'resolve-selection') {
      const response = await fetch('/api/v1/selections/resolve', {
        method: 'POST',
        body: stringifyWire(command.request),
      })
      const payload = (await response.json()) as unknown
      this.emitResponse(command.commandId, payload, response.status, response.ok)
      return
    }
    const startPaths: Record<string, string> = {
      build: '/api/v1/builds',
      explain: '/api/v1/explanations',
      'artifact-transform': '/api/v1/artifact-transforms',
      'artifact-render': '/api/v1/artifact-renders',
      verification: '/api/v1/verifications',
      run: '/api/v1/runs',
      jit: '/api/v1/jit',
    }
    const path = command.type === 'start' ? startPaths[command.operation ?? ''] : command.type === 'state' ? `/api/v1/operations/${command.operationId}` : `/api/v1/operations/${command.operationId}/cancel`
    const init: RequestInit = {
      method: command.type === 'state' ? 'GET' : 'POST',
    }
    if (command.type !== 'state') {
      init.body = stringifyWire(command.type === 'start' ? command.request : { operationId: command.operationId, reason: command.reason })
    }
    const response = await fetch(path ?? '/invalid-operation-command', init)
    const payload = (await response.json()) as unknown
    this.emitResponse(command.commandId, payload, response.status, response.ok)
  }

  private emitResponse(commandId: string, payload: unknown, status = 200, ok = true): void {
    this.onmessage?.(
      new MessageEvent('message', {
        data: stringifyWire({
          type: 'response',
          commandId,
          ok,
          status,
          ...(ok ? { payload } : { error: payload }),
        }),
      }),
    )
  }
}

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(stringifyWire(value), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

function mockGateway(selectionChanges: ResolveSelectionResponse['selectionChanges'] = []): void {
  const catalog = createCatalogFixture()
  vi.stubGlobal('WebSocket', MockWebSocket)
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (url === '/api/v1/catalog') return jsonResponse(catalog)
      if (url === '/api/v1/selections/resolve') {
        const request = decodeWire<ResolveSelectionRequest>(JSON.parse(String(init?.body)))
        const output = catalog.outputs.find((candidate) => candidate.id === request.outputId)
        const response: ResolveSelectionResponse = {
          effectiveSelection: {
            languageId: request.languageId,
            toolchainId: request.toolchainId ?? 'roslyn-stable',
            referenceSetId: request.referenceSetId ?? 'net10-ref',
            outputId: request.outputId,
            runtimeId: output?.requiresRuntime ? request.runtimeId : null,
          },
          selectionChanges,
          effectiveCapabilities: {
            languageServerCapabilities: [],
            buildCapabilities: ['compile-check', 'ast'],
            outputCapabilities: ['compile-check', 'ast', 'il', 'run'],
            runtimeCapabilities: [],
          },
          pipelineResolutionId: `pipeline-${request.outputId}`,
          pipelinePlan: {
            releaseId: catalog.releaseId,
            languageWorkerId: request.toolchainId ?? 'roslyn-stable',
            compilerWorkerId: request.toolchainId ?? 'roslyn-stable',
            referenceSetId: request.referenceSetId ?? 'net10-ref',
            stages: [],
            runtimeId: output?.requiresRuntime ? request.runtimeId : null,
            securityPolicyId: 'compiler-default',
            workerImageIds: [],
          },
          expiresAt: new Date(Date.now() + 60_000).toISOString(),
        }
        return jsonResponse(response)
      }
      return jsonResponse({ message: `Unexpected request ${url}` }, 500)
    }),
  )
}

interface LiveCompilationGateway {
  buildRequests: BuildRequest[]
  jitRequests: JitRequest[]
  runRequests: RunRequest[]
  operationIds: string[]
  cancelledOperationIds: string[]
  operationStatuses: Map<string, OperationState['status']>
  resolveRequests: ResolveSelectionRequest[]
  resetRecordedRequests: () => void
  setCatalogStatus: (status: number) => void
}

function mockLiveCompilationGateway(): LiveCompilationGateway {
  const catalog = createCatalogFixture()
  const runtime = catalog.runtimes.find((candidate) => candidate.id === 'dotnet-10-linux-x64')
  const compiler = catalog.toolchains.find((candidate) => candidate.id === 'roslyn-stable')
  const processor = catalog.artifactProcessors.find((candidate) => candidate.id === 'artifacts-default')
  if (!runtime || !compiler || !processor) {
    throw new Error('Expected the live-compilation catalog fixtures.')
  }
  compiler.capabilities = [...new Set([...compiler.capabilities, 'generated-source'])]
  runtime.capabilities = [...new Set([...runtime.capabilities, 'execution-flow'])]
  processor.capabilities = [...new Set([...processor.capabilities, 'execution-flow', 'run-il'])]
  processor.transformations = [
    ...(processor.transformations ?? []),
    {
      id: 'runtime-instrumentation-v1',
      inputArtifactFormat: 'dotnet-managed-pe-v1',
      outputArtifactFormat: 'dotnet-managed-pe-v1',
    },
  ]
  catalog.artifactProcessors.push({
    id: 'artifacts-jsil',
    displayName: 'JSIL',
    resolvedVersion: '0.8.2',
    workerId: 'artifacts-jsil',
    acceptsArtifactFormats: ['dotnet-managed-pe-v1'],
    producesArtifactFormats: ['javascript-v1'],
    capabilities: ['javascript'],
    transformations: [],
    acceptedMetadataFeatureTags: [],
    availability: { installed: true, health: 'healthy' },
  })
  catalog.compatibility.push({
    id: 'managed-jsil',
    kind: 'artifact-processor',
    fromId: 'dotnet-managed-pe-v1',
    toId: 'artifacts-jsil',
    allowed: true,
    requiredMetadataFeatureTags: [],
  })
  catalog.outputs.push(
    {
      id: 'generated-source',
      displayName: 'Generated Source',
      renderer: 'source',
      requiresRuntime: false,
      requiredCapabilities: ['generated-source'],
      acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
    },
    {
      id: 'jit-asm',
      displayName: 'JIT Assembly',
      renderer: 'jit-assembly',
      requiresRuntime: true,
      requiredCapabilities: ['managed-pe', 'jit-asm'],
      acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
    },
    {
      id: 'execution-flow',
      displayName: 'Execution Flow',
      renderer: 'flow',
      requiresRuntime: true,
      requiredCapabilities: ['managed-pe', 'execution-flow', 'run'],
      acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
    },
    {
      id: 'run-il',
      displayName: 'Rewritten Run IL',
      renderer: 'il',
      requiresRuntime: false,
      requiredCapabilities: ['managed-pe', 'run-il'],
      acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
    },
    {
      id: 'javascript',
      displayName: 'JavaScript (JSIL)',
      renderer: 'javascript',
      requiresRuntime: false,
      requiredCapabilities: ['managed-pe', 'javascript'],
      acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
      outputArtifactFormat: 'javascript-v1',
    },
  )

  const buildRequests: BuildRequest[] = []
  const jitRequests: JitRequest[] = []
  const runRequests: RunRequest[] = []
  const operationIds: string[] = []
  const cancelledOperationIds: string[] = []
  const operationKinds = new Map<string, 'build' | 'jit' | 'run'>()
  const operationStatuses = new Map<string, OperationState['status']>()
  const resolveRequests: ResolveSelectionRequest[] = []
  let nextOperationSequence = 1
  let catalogStatus = 200
  vi.stubGlobal('WebSocket', MockWebSocket)
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (url === '/api/v1/catalog') {
        return catalogStatus === 200 ? jsonResponse(catalog) : jsonResponse({ message: 'Gateway upstream is unavailable.' }, catalogStatus)
      }
      if (url === '/api/v1/selections/resolve') {
        const request = decodeWire<ResolveSelectionRequest>(JSON.parse(String(init?.body)))
        resolveRequests.push(request)
        const output = catalog.outputs.find((candidate) => candidate.id === request.outputId)
        const effectiveRuntimeId = output?.requiresRuntime ? (request.runtimeId ?? 'dotnet-10-linux-x64') : null
        const stages: ResolveSelectionResponse['pipelinePlan']['stages'] = [
          {
            id: 'build',
            kind: 'build',
            providerId: request.toolchainId ?? 'roslyn-stable',
            outputArtifactFormat: 'dotnet-managed-pe-v1',
          },
        ]
        if (request.outputId === 'jit-asm') {
          stages.push({
            id: 'jit-asm',
            kind: 'jit',
            providerId: effectiveRuntimeId ?? '',
          })
        } else if (request.outputId === 'run') {
          stages.push({
            id: 'run',
            kind: 'run',
            providerId: effectiveRuntimeId ?? '',
          })
        } else if (request.outputId === 'execution-flow') {
          stages.push(
            {
              id: 'runtime-instrumentation-v1',
              kind: 'transform',
              providerId: 'artifacts-default',
              inputArtifactFormat: 'dotnet-managed-pe-v1',
              outputArtifactFormat: 'dotnet-managed-pe-v1',
            },
            { id: 'run', kind: 'run', providerId: effectiveRuntimeId ?? '' },
          )
        } else if (request.outputId === 'run-il') {
          stages.push(
            {
              id: 'runtime-instrumentation-v1',
              kind: 'transform',
              providerId: 'artifacts-default',
              inputArtifactFormat: 'dotnet-managed-pe-v1',
              outputArtifactFormat: 'dotnet-managed-pe-v1',
            },
            {
              id: 'run-il',
              kind: 'render',
              providerId: 'artifacts-default',
              inputArtifactFormat: 'dotnet-managed-pe-v1',
              outputArtifactFormat: 'il-text-v1',
            },
          )
        } else if (request.outputId === 'javascript') {
          stages.push({
            id: 'javascript',
            kind: 'transform',
            providerId: 'artifacts-jsil',
            inputArtifactFormat: 'dotnet-managed-pe-v1',
            outputArtifactFormat: 'javascript-v1',
          })
        }
        const response: ResolveSelectionResponse = {
          effectiveSelection: {
            languageId: request.languageId,
            toolchainId: request.toolchainId ?? 'roslyn-stable',
            referenceSetId: request.referenceSetId ?? 'net10-ref',
            outputId: request.outputId,
            runtimeId: effectiveRuntimeId,
          },
          selectionChanges: [],
          effectiveCapabilities: {
            languageServerCapabilities: [],
            buildCapabilities: ['compile-check', 'ast', 'generated-source', 'managed-pe'],
            outputCapabilities: catalog.outputs.map((candidate) => candidate.id),
            runtimeCapabilities: output?.requiresRuntime ? ['run', 'jit-asm', 'execution-flow'] : [],
          },
          pipelineResolutionId: `pipeline-${request.outputId}-${request.workspaceRevision}`,
          pipelinePlan: {
            releaseId: catalog.releaseId,
            languageWorkerId: request.toolchainId ?? 'roslyn-stable',
            compilerWorkerId: request.toolchainId ?? 'roslyn-stable',
            referenceSetId: request.referenceSetId ?? 'net10-ref',
            stages,
            runtimeId: effectiveRuntimeId,
            securityPolicyId: 'compiler-default',
            workerImageIds: [],
          },
          expiresAt: new Date(Date.now() + 60_000).toISOString(),
        }
        return jsonResponse(response)
      }
      if (url === '/api/v1/builds') {
        buildRequests.push(decodeWire<BuildRequest>(JSON.parse(String(init?.body))))
        const operationId = `op_${nextOperationSequence.toString(16).padStart(32, '0')}`
        nextOperationSequence += 1
        operationIds.push(operationId)
        operationKinds.set(operationId, 'build')
        operationStatuses.set(operationId, 'accepted')
        return jsonResponse(
          {
            operationId,
            requestId: `build-request-${operationIds.length}`,
            createdAtUtc: new Date().toISOString(),
            isExisting: false,
          },
          202,
        )
      }
      if (url === '/api/v1/runs') {
        runRequests.push(decodeWire<RunRequest>(JSON.parse(String(init?.body))))
        const operationId = `op_${nextOperationSequence.toString(16).padStart(32, '0')}`
        nextOperationSequence += 1
        operationIds.push(operationId)
        operationKinds.set(operationId, 'run')
        operationStatuses.set(operationId, 'accepted')
        return jsonResponse(
          {
            operationId,
            requestId: `run-request-${operationIds.length}`,
            createdAtUtc: new Date().toISOString(),
            isExisting: false,
          },
          202,
        )
      }
      if (url === '/api/v1/jit') {
        jitRequests.push(decodeWire<JitRequest>(JSON.parse(String(init?.body))))
        const operationId = `op_${nextOperationSequence.toString(16).padStart(32, '0')}`
        nextOperationSequence += 1
        operationIds.push(operationId)
        operationKinds.set(operationId, 'jit')
        operationStatuses.set(operationId, 'accepted')
        return jsonResponse(
          {
            operationId,
            requestId: `jit-request-${operationIds.length}`,
            createdAtUtc: new Date().toISOString(),
            isExisting: false,
          },
          202,
        )
      }
      if (/^\/api\/v1\/operations\/op_[0-9a-f]{32}\/cancel$/.test(url)) {
        const operationId = url.slice('/api/v1/operations/'.length, -'/cancel'.length)
        cancelledOperationIds.push(operationId)
        return jsonResponse({
          operationId,
          disposition: 'accepted',
          lastSequence: 0,
        })
      }
      if (/^\/api\/v1\/operations\/op_[0-9a-f]{32}$/.test(url)) {
        const operationId = url.slice('/api/v1/operations/'.length)
        return jsonResponse({
          operationId,
          requestId: `request-${operationId}`,
          kind: operationKinds.get(operationId) ?? 'build',
          status: operationStatuses.get(operationId) ?? 'accepted',
          lastSequence: 0,
          createdAtUtc: new Date().toISOString(),
          updatedAtUtc: new Date().toISOString(),
          traceId: `trace-${operationId}`,
        })
      }
      return jsonResponse({ message: `Unexpected request ${url}` }, 500)
    }),
  )
  return {
    buildRequests,
    jitRequests,
    runRequests,
    operationIds,
    cancelledOperationIds,
    operationStatuses,
    resolveRequests,
    resetRecordedRequests: () => {
      buildRequests.splice(0)
      jitRequests.splice(0)
      runRequests.splice(0)
      operationIds.splice(0)
      cancelledOperationIds.splice(0)
      operationKinds.clear()
      operationStatuses.clear()
      resolveRequests.splice(0)
    },
    setCatalogStatus: (status) => {
      catalogStatus = status
    },
  }
}

async function advanceTime(milliseconds: number): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(milliseconds)
  })
}

async function flushReact(): Promise<void> {
  await act(async () => {
    await Promise.resolve()
    await vi.advanceTimersByTimeAsync(0)
    await Promise.resolve()
  })
}

async function flushMicrotasks(): Promise<void> {
  await act(async () => {
    await Promise.resolve()
    await Promise.resolve()
  })
}

async function renderResolvedApp(gateway: LiveCompilationGateway): Promise<void> {
  const initial = useWorkbenchStore.getState()
  initial.setSelectionIntent({
    languageId: initial.languageId,
    toolchainId: initial.toolchainId,
    referenceSetId: initial.referenceSetId,
    outputId: 'jit-asm',
    runtimeId: 'dotnet-10-linux-x64',
  })
  renderApp()
  await flushReact()
  await advanceTime(1)
  await flushReact()

  const buildOperationId = gateway.operationIds[0]
  const buildRequest = gateway.buildRequests[0]
  if (!buildOperationId || !buildRequest) throw new Error('Expected the initial JIT Build.')
  await completeArtifactBuild(operationSocket(buildOperationId), buildOperationId, buildRequest, `sha256:${'f'.repeat(64)}`)
  const jitOperationId = gateway.operationIds[1]
  if (!jitOperationId) throw new Error('Expected the initial JIT inspection.')
  await completeJitOperation(operationSocket(jitOperationId), jitOperationId)
  gateway.resetRecordedRequests()

  await flushReact()
  expect(screen.getByLabelText('Output')).toHaveValue('jit-asm')
  fireEvent.change(screen.getByLabelText('Output'), {
    target: { value: 'ast' },
  })
  await advanceTime(250)
  await flushReact()
  expect(screen.getByLabelText('Output')).toHaveValue('ast')
  expect(screen.getByRole('button', { name: 'Build AST' })).toBeEnabled()
}

function operationSocket(operationId: string): MockWebSocket {
  const socket = MockWebSocket.instances.findLast((candidate) => candidate.url.endsWith('/api/v1/operations/ws') && candidate.readyState !== MockWebSocket.CLOSED)
  if (!socket) throw new Error(`Expected a WebSocket for ${operationId}.`)
  return socket
}

async function completeAstOperation(socket: MockWebSocket, operationId: string, workspaceRevision: number, rootKind: string): Promise<void> {
  await act(async () => {
    socket.emitOperation({
      operationId,
      sequence: 1,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'typed-result',
        result: {
          resultType: 'ast',
          document: {
            languageId: 'csharp',
            toolchainId: 'roslyn-stable',
            workspaceRevision,
            root: {
              kind: rootKind,
              range: {
                startLine: 0,
                startCharacter: 0,
                endLine: 0,
                endCharacter: 1,
              },
              properties: {},
              children: [],
            },
            truncated: false,
          },
        },
      },
    })
    socket.emitOperation({
      operationId,
      sequence: 2,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'completed',
        status: 'completed',
        elapsed: '00:00:00.0100000',
      },
    })
  })
  await flushReact()
}

async function completeArtifactBuild(socket: MockWebSocket, operationId: string, request: BuildRequest, artifactRef: string): Promise<void> {
  await act(async () => {
    socket.emitOperation({
      operationId,
      sequence: 1,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'typed-result',
        result: {
          resultType: 'build',
          outcome: 'succeeded',
          artifactRef,
          diagnostics: [],
          identity: {
            releaseId: 'test-release',
            languageId: 'csharp',
            toolchainId: 'roslyn-stable',
            compilerVersion: '5.6.0',
            compilerCommit: 'abcdef0123456789abcdef0123456789abcdef01',
            referenceSetId: 'net10-ref',
            workerImageId: `sha256:${'c'.repeat(64)}`,
          },
          workspaceRevision: request.workspace.revision,
          selectionRevision: request.workspace.selectionRevision,
        },
      },
    })
    socket.emitOperation({
      operationId,
      sequence: 2,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'completed',
        status: 'completed',
        elapsed: '00:00:00.0100000',
      },
    })
  })
  await flushReact()
}

async function completeRunOperation(socket: MockWebSocket, operationId: string, output: string): Promise<void> {
  await act(async () => {
    socket.emitOperation({
      operationId,
      sequence: 1,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'output-chunk',
        chunk: {
          channel: 'stdout',
          encoding: 'utf-8',
          data: btoa(output),
          truncated: false,
        },
      },
    })
    socket.emitOperation({
      operationId,
      sequence: 2,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'typed-result',
        result: {
          resultType: 'run',
          status: 'completed',
          exitCode: 0,
          exception: null,
          elapsed: '00:00:00.0200000',
          outputTruncated: false,
          identity: {
            runtimeVersion: '10.0.9',
            runtimeCommit: 'runtime-commit',
            runtimeImageId: `sha256:${'d'.repeat(64)}`,
            rid: 'linux-x64',
            architecture: 'x64',
          },
        },
      },
    })
    socket.emitOperation({
      operationId,
      sequence: 3,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'completed',
        status: 'completed',
        elapsed: '00:00:00.0200000',
      },
    })
  })
  await flushReact()
}

async function completeJitOperation(socket: MockWebSocket, operationId: string): Promise<void> {
  await act(async () => {
    socket.emitOperation({
      operationId,
      sequence: 1,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'output-chunk',
        chunk: {
          channel: 'jit',
          encoding: 'utf-8',
          data: btoa('Program:Main():void:\n       ret\n'),
          truncated: false,
        },
      },
    })
    socket.emitOperation({
      operationId,
      sequence: 2,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'typed-result',
        result: {
          resultType: 'jit',
          status: 'completed',
          methods: [],
          elapsed: '00:00:00.0100000',
          identity: {
            runtimeVersion: '10.0.9',
            runtimeCommit: 'runtime-commit',
            runtimeImageId: `sha256:${'d'.repeat(64)}`,
            rid: 'linux-x64',
            architecture: 'x64',
            jitVersion: '10.0.9',
            jitCommit: 'jit-commit',
            cpuFeatureProfile: 'x64-v2',
            tieringPolicy: 'tier0-diffable',
            pgoPolicy: 'disabled',
            jitProvider: 'coreclr-jitdisasm',
            inspectionMethod: 'prepare-method',
          },
        },
      },
    })
    socket.emitOperation({
      operationId,
      sequence: 3,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'completed',
        status: 'completed',
        elapsed: '00:00:00.0100000',
      },
    })
  })
  await flushReact()
}

async function completeGeneratedSourceOperation(socket: MockWebSocket, operationId: string, workspaceRevision: number, selectionRevision: number, contentRef: string, path: string): Promise<void> {
  await act(async () => {
    socket.emitOperation({
      operationId,
      sequence: 1,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'typed-result',
        result: {
          resultType: 'generated-source',
          documents: [
            {
              path,
              contentRef,
              languageId: 'csharp',
              generatorId: 'test-generator',
            },
          ],
          identity: {
            releaseId: 'test-release',
            languageId: 'csharp',
            toolchainId: 'roslyn-stable',
            compilerVersion: '5.6.0',
            compilerCommit: 'abcdef0123456789abcdef0123456789abcdef01',
            referenceSetId: 'net10-ref',
            workerImageId: `sha256:${'c'.repeat(64)}`,
          },
          workspaceRevision,
          selectionRevision,
        },
      },
    })
    socket.emitOperation({
      operationId,
      sequence: 2,
      timestampUtc: new Date().toISOString(),
      traceId: `trace-${operationId}`,
      payload: {
        kind: 'completed',
        status: 'completed',
        elapsed: '00:00:00.0100000',
      },
    })
  })
  await flushReact()
}

function renderApp(options: { strict?: boolean } = {}): void {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const Wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  render(
    options.strict ? (
      <StrictMode>
        <App />
      </StrictMode>
    ) : (
      <App />
    ),
    { wrapper: Wrapper },
  )
}

describe('SharpLabNext workbench', () => {
  beforeEach(() => {
    window.history.replaceState(null, '', '/')
    window.localStorage.clear()
    resetWorkbenchStore()
    MockWebSocket.instances = []
    mockGateway()
  })

  afterEach(() => {
    cleanup()
    resetOperationCommandConnectionForTests()
    vi.unstubAllGlobals()
    window.history.replaceState(null, '', '/')
  })

  it('renders the server catalog and only shows Runtime for runtime outputs', async () => {
    renderApp()

    expect(screen.queryByText('SharpLabNext')).not.toBeInTheDocument()
    expect(screen.queryByText('Connected')).not.toBeInTheDocument()
    expect(screen.queryByRole('status', { name: 'Connected' })).not.toBeInTheDocument()
    await waitFor(() => expect(screen.getByLabelText('Output')).toHaveValue('decompiled-csharp'))
    for (const label of ['Language', 'Toolchain', 'Reference set', 'Output']) {
      const select = screen.getByLabelText(label)
      expect(select).toBeVisible()
      expect(select.nextElementSibling).toHaveClass('select-field__chevron')
      expect(screen.getByText(label, { selector: '.select-field > span' })).toHaveClass('visually-hidden')
    }
    expect(screen.getByLabelText('Language')).toHaveAttribute('title', 'Source language')
    expect(screen.getByLabelText('Toolchain')).toHaveAttribute('title', 'Compiler toolchain')
    expect(screen.getByLabelText('Reference set')).toHaveAttribute('title', 'Reference set used for compilation')
    expect(screen.getByLabelText('Reference set')).toHaveTextContent('.NET 10')
    expect(screen.getByLabelText('Output')).toHaveAttribute('title', 'Output view')
    expect(document.querySelector('.identity-strip')).toHaveClass('identity-strip--hidden')
    expect(screen.queryByLabelText('Runtime')).not.toBeInTheDocument()
    expect(document.querySelector('.selector-bar')?.parentElement).toHaveClass('app-bar')
    const statusBar = document.querySelector('.status-bar')
    expect(statusBar).not.toHaveTextContent(/Connected|Catalog|LSP|Workspace r|Selection r/)
    const monacoToggle = screen.getByRole('button', {
      name: 'Editor: Monaco. Click to switch to CodeMirror',
    })
    expect(monacoToggle).toBeVisible()
    expect(statusBar).toHaveTextContent('Editor:Monaco')
    expect(screen.queryByRole('toolbar', { name: 'Editor' })).not.toBeInTheDocument()

    fireEvent.click(monacoToggle)
    const codeMirrorToggle = screen.getByRole('button', {
      name: 'Editor: CodeMirror. Click to switch to Monaco',
    })
    expect(codeMirrorToggle).toHaveTextContent('Editor:CodeMirror')
    expect(window.localStorage.getItem('sharplabnext.editor')).toBe('codemirror')
    fireEvent.click(codeMirrorToggle)
    expect(screen.getByRole('button', { name: 'Editor: Monaco. Click to switch to CodeMirror' })).toBeInTheDocument()

    const releaseMode = screen.getByRole('button', {
      name: 'Build mode: Release. Click to switch to Debug',
    })
    expect(releaseMode).toHaveTextContent('Release')
    fireEvent.click(releaseMode)
    const debugMode = screen.getByRole('button', {
      name: 'Build mode: Debug. Click to switch to Release',
    })
    expect(debugMode).toHaveTextContent('Debug')
    expect(useWorkbenchStore.getState().buildMode).toBe('debug')
    fireEvent.click(debugMode)
    expect(screen.getByRole('button', { name: 'Build mode: Release. Click to switch to Debug' })).toBeInTheDocument()
    expect(useWorkbenchStore.getState().buildMode).toBe('release')

    fireEvent.change(screen.getByLabelText('Output'), {
      target: { value: 'run' },
    })

    await waitFor(() => expect(screen.getByLabelText('Runtime')).toHaveValue('dotnet-10-linux-x64'))
    expect(screen.getByLabelText('Runtime')).toHaveTextContent('.NET 10')
    expect(screen.getByLabelText('Runtime')).toHaveAttribute('title', 'Runtime used for Run and JIT')
    const runtimeOptions = Array.from((screen.getByLabelText('Runtime') as HTMLSelectElement).options)
    expect(runtimeOptions.every((option) => option.text.endsWith('\u00a0\u00a0'))).toBe(true)

    fireEvent.change(screen.getByLabelText('Reference set'), {
      target: { value: 'net11-ref' },
    })
    await waitFor(() => expect(screen.getByLabelText('Runtime')).toHaveValue('dotnet-11-linux-x64'))
    fireEvent.change(screen.getByLabelText('Reference set'), {
      target: { value: 'net10-ref' },
    })
    await waitFor(() => expect(screen.getByLabelText('Runtime')).toHaveValue('dotnet-10-linux-x64'))
  })

  it('does not expose a JIT method-scope filter', async () => {
    mockLiveCompilationGateway()
    renderApp()
    await waitFor(() => expect(screen.getByLabelText('Output')).toHaveValue('decompiled-csharp'))

    fireEvent.change(screen.getByLabelText('Output'), {
      target: { value: 'jit-asm' },
    })

    await waitFor(() => expect(screen.getByLabelText('Output')).toHaveValue('jit-asm'))
    expect(screen.queryByRole('button', { name: 'All' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Current/ })).not.toBeInTheDocument()
  })

  it('applies and persists the accessible pane split without changing workspace state', async () => {
    renderApp()

    await waitFor(() => expect(screen.getByLabelText('Output')).toHaveValue('decompiled-csharp'))
    const initial = useWorkbenchStore.getState()
    const initialWorkspaceRevision = initial.workspaceRevision
    const initialSelectionRevision = initial.selectionRevision
    const separator = screen.getByRole('separator', {
      name: 'Resize source and result panes',
    })
    const grid = document.querySelector<HTMLElement>('.pane-grid')
    if (!grid) throw new Error('The workbench pane grid was not rendered.')

    expect(separator).toHaveAttribute('aria-orientation', 'vertical')
    expect(grid.style.getPropertyValue('--source-pane-track')).toBe('50fr')
    fireEvent.keyDown(separator, { key: 'ArrowRight', shiftKey: true })
    await waitFor(() => expect(separator).toHaveAttribute('aria-valuenow', '55'))
    expect(grid.style.getPropertyValue('--source-pane-track')).toBe('55fr')
    expect(grid.style.getPropertyValue('--result-pane-track')).toBe('45fr')
    expect(localStorage.getItem(paneSplitPreferenceStorageKey)).toBe('55')
    expect(useWorkbenchStore.getState().workspaceRevision).toBe(initialWorkspaceRevision)
    expect(useWorkbenchStore.getState().selectionRevision).toBe(initialSelectionRevision)

    fireEvent.doubleClick(separator)
    await waitFor(() => expect(separator).toHaveAttribute('aria-valuenow', '50'))
    expect(localStorage.getItem(paneSplitPreferenceStorageKey)).toBe('50')
  })

  it('keeps the result tabs mounted when selection resolution has a transport failure', async () => {
    const gatewayFetch = globalThis.fetch
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
        const url = typeof input === 'string' ? input : input.toString()
        if (url === '/api/v1/selections/resolve') {
          return jsonResponse({ message: 'Selection temporarily unavailable.' }, 503)
        }
        return gatewayFetch(input, init)
      }),
    )

    renderApp()

    await waitFor(() => expect(screen.getByRole('status', { name: 'Gateway unavailable' })).toBeVisible())
    expect(document.querySelector('.app-bar')).toHaveAttribute('data-health-state', 'error')
    expect(screen.queryByText('Selection temporarily unavailable.')).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Diagnostics' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Decompiled C#' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.queryByText('Selection could not be resolved')).not.toBeInTheDocument()
  })

  it('keeps selection notices mounted while the next workspace revision is debouncing', async () => {
    mockGateway([
      {
        field: 'output',
        requestedValue: 'ast',
        effectiveValue: 'decompiled-csharp',
        reason: 'unsupported-by-language',
        message: 'AST is unavailable for the selected profile.',
      },
    ])
    renderApp()

    await screen.findByText('AST is unavailable for the selected profile.')
    fireEvent.change(screen.getByLabelText('Source editor'), {
      target: { value: 'class Program { static void Main() {} }' },
    })

    expect(screen.getByText('AST is unavailable for the selected profile.')).toBeInTheDocument()
  })

  it('filters toolchains and replaces an untouched template when the language changes', async () => {
    renderApp()
    await waitFor(() => expect(screen.getByLabelText('Language')).not.toBeDisabled())

    fireEvent.change(screen.getByLabelText('Language'), {
      target: { value: 'fsharp' },
    })

    await waitFor(() => expect(screen.getByLabelText('Toolchain')).toHaveValue('fsharp-stable'))
    expect(screen.getByRole('tab', { name: /Program\.fs/ })).toBeInTheDocument()
    expect((screen.getByLabelText('Source editor') as HTMLTextAreaElement).value).toContain('printfn')
  })

  it('keeps the mobile source file row at zero height until the app-bar file entry is opened', async () => {
    vi.stubGlobal(
      'matchMedia',
      vi.fn(
        () =>
          ({
            matches: true,
            media: '(max-width: 860px)',
            onchange: null,
            addEventListener: vi.fn(),
            removeEventListener: vi.fn(),
            addListener: vi.fn(),
            removeListener: vi.fn(),
            dispatchEvent: vi.fn(),
          }) as MediaQueryList,
      ),
    )
    window.localStorage.setItem('sharplabnext.editor', 'monaco')
    renderApp()
    await waitFor(() => expect(screen.getByLabelText('Language')).not.toBeDisabled())

    const fileEntry = document.querySelector('.mobile-files-button')
    expect(fileEntry).toBeInstanceOf(HTMLButtonElement)
    if (!(fileEntry instanceof HTMLButtonElement)) return
    expect(fileEntry.closest('.app-bar-actions')).not.toBeNull()
    expect(document.querySelector('.source-pane .mobile-file-toggle')).not.toBeInTheDocument()
    expect(fileEntry).toHaveAttribute('aria-label', 'Workspace files, current Program.cs')
    expect(fileEntry).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('tablist', { name: 'Workspace files' })).not.toBeInTheDocument()

    fireEvent.click(fileEntry)
    expect(fileEntry).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('tablist', { name: 'Workspace files' })).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }))

    expect(fileEntry).toHaveAttribute('aria-label', 'Workspace files, current File2.cs')
    expect(fileEntry).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('tablist', { name: 'Workspace files' })).not.toBeInTheDocument()
    expect(useWorkbenchStore.getState().files.map((file) => file.path)).toEqual(['Program.cs', 'File2.cs'])

    fireEvent.click(fileEntry)
    expect(screen.getByRole('tab', { name: /Program\.cs/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /File2\.cs/ })).toHaveAttribute('aria-selected', 'true')
  })

  it('keeps mobile editor controls collapsed and persists font size locally', async () => {
    vi.stubGlobal(
      'matchMedia',
      vi.fn(
        () =>
          ({
            matches: true,
            media: '(max-width: 860px)',
            onchange: null,
            addEventListener: vi.fn(),
            removeEventListener: vi.fn(),
            addListener: vi.fn(),
            removeListener: vi.fn(),
            dispatchEvent: vi.fn(),
          }) as MediaQueryList,
      ),
    )
    window.localStorage.setItem('sharplabnext.editor', 'monaco')
    renderApp()
    await waitFor(() => expect(screen.getByLabelText('Language')).not.toBeDisabled())

    const settings = document.querySelector('.status-editor-settings-toggle')
    expect(settings).toBeInstanceOf(HTMLButtonElement)
    if (!(settings instanceof HTMLButtonElement)) return
    const editorToggle = screen.getByRole('button', {
      name: 'Editor: Monaco. Click to switch to CodeMirror',
      hidden: true,
    })
    const settingsPanel = editorToggle.closest('.status-editor-settings-panel')
    expect(settings).toHaveAttribute('aria-expanded', 'false')
    expect(settingsPanel).toHaveAttribute('data-mobile-open', 'false')

    fireEvent.click(settings)

    expect(settings).toHaveAttribute('aria-expanded', 'true')
    expect(settingsPanel).toHaveAttribute('data-mobile-open', 'true')
    const workbench = document.querySelector('.workbench')
    expect(workbench).toHaveStyle({ '--code-font-size': '14px' })
    expect(screen.getByLabelText('Current code font size')).toHaveTextContent('14px')

    fireEvent.click(screen.getByRole('button', { name: 'Increase code font size' }))

    expect(screen.getByLabelText('Current code font size')).toHaveTextContent('16px')
    expect(workbench).toHaveStyle({ '--code-font-size': '16px' })
    expect(window.localStorage.getItem('sharplabnext.editor-font-size')).toBe('16')
  })

  it('reorders capable multi-file workspaces while preserving the active file and URL state', async () => {
    renderApp()
    await waitFor(() => expect(screen.getByLabelText('Language')).not.toBeDisabled())
    expect(screen.queryByRole('group', { name: 'Source order' })).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Language'), {
      target: { value: 'fsharp' },
    })
    await waitFor(() => expect(screen.getByRole('tab', { name: /Program\.fs/ })).toBeVisible())
    expect(screen.queryByRole('group', { name: 'Source order' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Add file' }))
    const moveEarlier = screen.getByRole('button', {
      name: 'Move File2.fs earlier in source order',
    })
    const moveLater = screen.getByRole('button', {
      name: 'Move File2.fs later in source order',
    })
    expect(moveEarlier).toBeEnabled()
    expect(moveLater).toBeDisabled()

    fireEvent.click(moveEarlier)

    expect(moveEarlier).toBeDisabled()
    expect(moveLater).toBeEnabled()
    expect(Array.from(screen.getByRole('tablist', { name: 'Workspace files' }).querySelectorAll('[role="tab"]')).map((tab) => tab.textContent)).toEqual(['File2.fs', 'Program.fs'])
    expect(screen.getByRole('tab', { name: /File2\.fs/ })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByLabelText('Source editor')).toHaveValue('')
    expect(useWorkbenchStore.getState().sourceOrder).toEqual(['File2.fs', 'Program.fs'])

    await waitFor(
      async () => {
        expect(window.location.hash).toMatch(/^#v3:/)
        const decoded = await shareCodec.decodeShareFragment(window.location.hash)
        expect(decoded.sourceFormat).toBe('v3')
        if (decoded.sourceFormat === 'v3') {
          expect(decoded.state.sourceOrder).toEqual(['File2.fs', 'Program.fs'])
        }
      },
      { timeout: 2_000 },
    )

    fireEvent.change(screen.getByLabelText('Language'), {
      target: { value: 'csharp' },
    })
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Source order' })).not.toBeInTheDocument())
  })

  it('closes the final file by restoring the language sample workspace', async () => {
    const catalog = createCatalogFixture()
    const csharp = catalog.languages.find((candidate) => candidate.id === 'csharp')
    expect(csharp).toBeDefined()
    if (!csharp) return

    renderApp()
    await waitFor(() => expect(screen.getByLabelText('Language')).not.toBeDisabled())
    fireEvent.change(screen.getByLabelText('Source editor'), {
      target: { value: 'public static class EditedSample { }' },
    })

    const close = screen.getByRole('button', { name: 'Close Program.cs' })
    expect(close).toBeVisible()
    fireEvent.click(close)

    expect(screen.getByLabelText('Source editor')).toHaveValue(csharp.defaultSource)
    expect(screen.getByRole('tab', { name: /Program\.cs/ })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('button', { name: 'Close Program.cs' })).toBeVisible()
    expect(useWorkbenchStore.getState()).toMatchObject({
      files: [{ path: 'Program.cs', text: csharp.defaultSource }],
      sourceIsTemplate: true,
    })
  })

  it('replaces a default template restored from a URL when the language changes', async () => {
    const catalog = createCatalogFixture()
    const fsharp = catalog.languages.find((language) => language.id === 'fsharp')
    expect(fsharp).toBeDefined()
    if (!fsharp) return
    const encoded = await shareCodec.encodeV3({
      languageId: 'fsharp',
      toolchainId: 'fsharp-stable',
      referenceSetId: 'net10-ref',
      outputId: 'ast',
      runtimeId: 'not-required',
      buildMode: 'release',
      releaseVersion: catalog.releaseId,
      activeFile: fsharp.defaultFileName,
      sourceOrder: [fsharp.defaultFileName],
      files: [{ path: fsharp.defaultFileName, text: fsharp.defaultSource }],
    })
    window.history.replaceState(null, '', `/${encoded.fragment}`)

    renderApp()
    await waitFor(() => expect(screen.getByLabelText('Language')).toHaveValue('fsharp'))
    expect(screen.getByRole('tab', { name: /Program\.fs/ })).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Language'), {
      target: { value: 'csharp' },
    })

    await waitFor(() => expect(screen.getByLabelText('Toolchain')).toHaveValue('roslyn-stable'))
    expect(screen.getByRole('tab', { name: /Program\.cs/ })).toBeInTheDocument()
    expect(screen.getByLabelText('Source editor')).toHaveValue('Console.WriteLine("C#");\n')
  })

  it('uses the command bar for an offline state without replacing the result surface', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('offline'))))
    renderApp()

    expect(await screen.findByRole('status', { name: 'Gateway unavailable' })).toBeVisible()
    expect(document.querySelector('.app-bar')).toHaveAttribute('data-health-state', 'error')
    expect(screen.queryByText('Gateway unavailable')).not.toBeInTheDocument()
    expect(screen.queryByText(/Gateway request failed/)).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Diagnostics' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Build' })).toBeDisabled()
  })

  it('keeps routine selection resolution silent in the fixed-width app actions', async () => {
    const baseFetch = vi.mocked(fetch)
    let selectionRequestCount = 0
    let releaseSelection: (() => void) | null = null
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
        const url = typeof input === 'string' ? input : input.toString()
        const response = await baseFetch(input, init)
        if (url !== '/api/v1/selections/resolve') return response

        selectionRequestCount += 1
        if (selectionRequestCount !== 2) return response
        return new Promise<Response>((resolve) => {
          releaseSelection = () => resolve(response)
        })
      }),
    )

    renderApp()
    const decompile = await screen.findByRole('button', { name: 'Decompile' })
    await waitFor(() => expect(decompile).toBeEnabled())

    fireEvent.change(screen.getByLabelText('Source editor'), {
      target: { value: 'public static class Edited { }' },
    })
    await waitFor(() => expect(selectionRequestCount).toBe(2), {
      timeout: 2_000,
    })

    expect(decompile).toBeDisabled()
    expect(screen.queryByRole('status', { name: 'Resolving' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Save to GitHub Gist')).toBeInTheDocument()
    expect(screen.getByLabelText('Copy share URL')).toBeInTheDocument()

    act(() => releaseSelection?.())
    await waitFor(() => expect(decompile).toBeEnabled())
  })

  it('disables operations for a resolved but unavailable profile', async () => {
    mockGateway([
      {
        field: 'toolchain',
        requestedValue: 'roslyn-stable',
        effectiveValue: 'roslyn-stable',
        reason: 'profile-unavailable',
        message: 'The selected compiler worker is unavailable.',
      },
    ])
    renderApp()

    const runButton = await screen.findByRole('button', { name: 'Decompile' })
    await waitFor(() => expect(screen.getByRole('status', { name: 'The selected compiler worker is unavailable.' })).toBeVisible())
    expect(runButton).toBeDisabled()
  })

  it('keeps an initial share decode failure on the restoration error surface', async () => {
    window.history.replaceState(null, '', '/#v3:invalid-test-fragment')
    const decodeSpy = vi.spyOn(shareCodec, 'decodeShareFragment').mockRejectedValue(new Error('The initial URL decode timed out.'))

    try {
      renderApp()

      expect(await screen.findByText('Share URL could not be restored')).toBeVisible()
      expect(screen.getByText('The initial URL decode timed out.')).toBeVisible()
      expect(document.querySelector('.result-error')).toBeInTheDocument()
    } finally {
      decodeSpy.mockRestore()
    }
  })

  it('never renders a locally stored workspace while restoring an initial share URL', async () => {
    useWorkbenchStore.getState().setSource('class LocallyStoredWorkspace {}')
    window.history.replaceState(null, '', '/#v3:delayed-test-fragment')
    let finishDecode: ((decoded: shareCodec.DecodedShare) => void) | undefined
    const decodeSpy = vi.spyOn(shareCodec, 'decodeShareFragment').mockReturnValue(
      new Promise((resolve) => {
        finishDecode = resolve
      }),
    )

    try {
      renderApp()

      expect(await screen.findByRole('status', { name: 'Restoring shared workspace' })).toBeVisible()
      expect(screen.queryByLabelText('Source editor')).not.toBeInTheDocument()
      expect(screen.queryByDisplayValue('class LocallyStoredWorkspace {}')).not.toBeInTheDocument()

      await act(async () => {
        finishDecode?.({
          sourceFormat: 'v3',
          codecId: 1,
          state: {
            languageId: 'csharp',
            toolchainId: 'roslyn-stable',
            referenceSetId: 'net10-ref',
            outputId: 'decompiled-csharp',
            runtimeId: 'not-required',
            buildMode: 'release',
            releaseVersion: 'test-release',
            activeFile: 'Program.cs',
            sourceOrder: ['Program.cs'],
            files: [{ path: 'Program.cs', text: 'class SharedWorkspace {}' }],
          },
        })
      })

      await waitFor(() => expect(screen.getByLabelText('Source editor')).toHaveValue('class SharedWorkspace {}'))
      expect(screen.queryByRole('status', { name: 'Restoring shared workspace' })).not.toBeInTheDocument()
      expect(screen.queryByDisplayValue('class LocallyStoredWorkspace {}')).not.toBeInTheDocument()
    } finally {
      decodeSpy.mockRestore()
    }
  })

  it('keeps a background URL synchronization failure out of the restoration error surface', async () => {
    const encodeSpy = vi.spyOn(shareCodec, 'encodeV3').mockRejectedValue(new Error('The background URL encode timed out.'))

    try {
      renderApp()

      await waitFor(() => expect(encodeSpy).toHaveBeenCalled(), {
        timeout: 2_000,
      })
      expect(screen.queryByText('Share URL could not be restored')).not.toBeInTheDocument()
      expect(screen.queryByText('The background URL encode timed out.')).not.toBeInTheDocument()
      expect(document.querySelector('.result-error')).not.toBeInTheDocument()
    } finally {
      encodeSpy.mockRestore()
    }
  })

  it('imports a SharpLab Gist fragment and preserves it until the workspace changes', async () => {
    window.history.replaceState(null, '', '/#gist:abcdef')
    const gatewayFetch = vi.mocked(fetch)
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
        const url = typeof input === 'string' ? input : input.toString()
        if (url === '/api/v1/shares/gists/abcdef') {
          return jsonResponse({
            id: 'abcdef',
            htmlUrl: 'https://gist.github.com/abcdef',
            ownerLogin: 'owner',
            isPublic: true,
            canUpdate: false,
            description: 'legacy',
            sourceFormat: 'sharplab-v1',
            workspace: {
              schemaVersion: 1,
              languageId: 'csharp',
              toolchainId: 'roslyn-stable',
              referenceSetId: 'net10-ref',
              outputId: 'il',
              runtimeId: null,
              buildMode: 'release',
              activeFile: 'Program.cs',
              sourceOrder: ['Program.cs'],
              files: [{ path: 'Program.cs', text: 'class ImportedFromGist {}' }],
            },
            warnings: ['Imported legacy Gist.'],
          })
        }
        return gatewayFetch(input, init)
      }),
    )

    renderApp()

    await waitFor(() => expect(screen.getByLabelText('Source editor')).toHaveValue('class ImportedFromGist {}'))
    expect(screen.getByLabelText('Output')).toHaveValue('il')
    await new Promise((resolve) => window.setTimeout(resolve, 500))
    expect(window.location.hash).toBe('#gist:abcdef')
  })

  it.each(['resolve', 'reject'] as const)('keeps a saved Gist fragment when an older URL encode finishes with %s', async (outcome) => {
    const gatewayFetch = vi.mocked(fetch)
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
        const url = typeof input === 'string' ? input : input.toString()
        if (url === '/api/v1/auth/github/status') {
          return jsonResponse({
            available: true,
            authenticated: true,
            login: 'owner',
            csrfToken: 'csrf',
          })
        }
        if (url === '/api/v1/shares/gists') {
          const request = decodeWire<{
            description: string
            isPublic: boolean
            workspace: object
          }>(JSON.parse(String(init?.body)))
          return jsonResponse(
            {
              id: 'c0ffee',
              htmlUrl: 'https://gist.github.com/owner/c0ffee',
              ownerLogin: 'owner',
              isPublic: request.isPublic,
              canUpdate: true,
              description: request.description,
              sourceFormat: 'sharplabnext-v1',
              workspace: request.workspace,
              warnings: [],
            },
            201,
          )
        }
        return gatewayFetch(input, init)
      }),
    )

    const staleEncoded: Awaited<ReturnType<typeof shareCodec.encodeV3>> = {
      fragment: '#v3:stale-encode',
      codecId: 0,
      compressionLevel: null,
      payloadLength: 1,
      encodedPayloadLength: 1,
      envelopeLength: 1,
      urlLength: 16,
      lengthDisposition: 'live',
    }
    const staleError = new Error('The stale URL encode failed.')
    let completeStaleEncode = (): void => undefined
    const staleEncoding = {
      // biome-ignore lint/suspicious/noThenProperty: A controlled thenable fixes the stale completion at the Gist write boundary.
      then(onFulfilled: (value: typeof staleEncoded) => unknown) {
        return {
          catch(onRejected: (error: unknown) => unknown) {
            completeStaleEncode = () => {
              if (outcome === 'resolve') onFulfilled(staleEncoded)
              else onRejected(staleError)
            }
            return Promise.resolve()
          },
        }
      },
    } as unknown as Promise<typeof staleEncoded>
    const encodeSpy = vi.spyOn(shareCodec, 'encodeV3').mockReturnValueOnce(staleEncoding)

    renderApp()
    await waitFor(() => expect(screen.getByLabelText('Output')).toHaveValue('decompiled-csharp'))
    await waitFor(() => expect(encodeSpy).toHaveBeenCalled())

    const nativeReplaceState = window.history.replaceState.bind(window.history)
    const replaceStateSpy = vi.spyOn(window.history, 'replaceState').mockImplementation((data, unused, url) => {
      nativeReplaceState(data, unused, url)
      if (url === '#gist:c0ffee') completeStaleEncode()
    })

    try {
      fireEvent.click(screen.getByLabelText('Save to GitHub Gist'))
      await screen.findByText('owner')
      fireEvent.click(screen.getByRole('button', { name: 'New Gist' }))

      await waitFor(() => expect(window.location.hash).toBe('#gist:c0ffee'))
      expect(screen.queryByText(staleError.message)).not.toBeInTheDocument()
    } finally {
      replaceStateSpy.mockRestore()
      encodeSpy.mockRestore()
    }
  })

  it('chains Build to artifact rendering and displays operation-scoped content', async () => {
    const buildOperationId = `op_${'1'.repeat(32)}`
    const renderOperationId = `op_${'2'.repeat(32)}`
    const catalog = createCatalogFixture()
    const referenceSetDigest = `sha256:${'d'.repeat(64)}`
    const compilerCommit = 'abcdef0123456789abcdef0123456789abcdef01'
    const compilerImage = `sha256:${'c'.repeat(64)}`
    const processorImage = `sha256:${'e'.repeat(64)}`
    const writeClipboard = vi.fn(async (_value: string) => {})
    const net10ReferenceSet = catalog.referenceSets.find((candidate) => candidate.id === 'net10-ref')
    if (!net10ReferenceSet) throw new Error('Expected the .NET 10 reference-set fixture.')
    net10ReferenceSet.digest = referenceSetDigest
    const requests: Array<BuildRequest | RenderArtifactRequest> = []
    const artifactRef = `sha256:${'a'.repeat(64)}`
    const contentRef = `sha256:${'b'.repeat(64)}`
    vi.stubGlobal('WebSocket', MockWebSocket)
    vi.stubGlobal('navigator', { clipboard: { writeText: writeClipboard } })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
        const url = typeof input === 'string' ? input : input.toString()
        if (url === '/api/v1/catalog') return jsonResponse(catalog)
        if (url === '/api/v1/selections/resolve') {
          const request = decodeWire<ResolveSelectionRequest>(JSON.parse(String(init?.body)))
          const response: ResolveSelectionResponse = {
            effectiveSelection: {
              languageId: request.languageId,
              toolchainId: request.toolchainId ?? 'roslyn-stable',
              referenceSetId: request.referenceSetId ?? 'net10-ref',
              outputId: request.outputId,
              runtimeId: null,
            },
            selectionChanges: [],
            effectiveCapabilities: {
              languageServerCapabilities: [],
              buildCapabilities: ['managed-pe'],
              outputCapabilities: [request.outputId],
              runtimeCapabilities: [],
            },
            pipelineResolutionId: `pipeline-${request.outputId}`,
            pipelinePlan: {
              releaseId: catalog.releaseId,
              languageWorkerId: 'roslyn-stable',
              compilerWorkerId: 'roslyn-stable',
              referenceSetId: request.referenceSetId ?? 'net10-ref',
              stages:
                request.outputId === 'il'
                  ? [
                      {
                        id: 'build',
                        kind: 'build',
                        providerId: 'roslyn-stable',
                        outputArtifactFormat: 'dotnet-managed-pe-v1',
                      },
                      {
                        id: 'il',
                        kind: 'render',
                        providerId: 'artifacts-default',
                        inputArtifactFormat: 'dotnet-managed-pe-v1',
                        outputArtifactFormat: 'il-text-v1',
                      },
                    ]
                  : [],
              runtimeId: null,
              securityPolicyId: 'compiler-default',
              workerImageIds: [],
            },
            expiresAt: new Date(Date.now() + 60_000).toISOString(),
          }
          return jsonResponse(response)
        }
        if (url === '/api/v1/builds') {
          requests.push(decodeWire<BuildRequest>(JSON.parse(String(init?.body))))
          return jsonResponse(
            {
              operationId: buildOperationId,
              requestId: 'build-request',
              createdAtUtc: new Date().toISOString(),
              isExisting: false,
            },
            202,
          )
        }
        if (url === '/api/v1/artifact-renders') {
          requests.push(decodeWire<RenderArtifactRequest>(JSON.parse(String(init?.body))))
          return jsonResponse(
            {
              operationId: renderOperationId,
              requestId: 'render-request',
              createdAtUtc: new Date().toISOString(),
              isExisting: false,
            },
            202,
          )
        }
        if (url.startsWith('/api/v1/operations/') && url.endsWith(`/contents/sha256/${'b'.repeat(64)}`)) {
          return new Response('.method public static void Main() cil managed')
        }
        if (url === `/api/v1/operations/${buildOperationId}` || url === `/api/v1/operations/${renderOperationId}`) {
          const isBuild = url.endsWith(buildOperationId)
          return jsonResponse({
            operationId: isBuild ? buildOperationId : renderOperationId,
            requestId: 'request',
            kind: isBuild ? 'build' : 'render-artifact',
            status: 'accepted',
            lastSequence: 0,
            createdAtUtc: new Date().toISOString(),
            updatedAtUtc: new Date().toISOString(),
            traceId: 'trace',
          })
        }
        return jsonResponse({ message: `Unexpected request ${url}` }, 500)
      }),
    )

    renderApp()
    await waitFor(() => expect(screen.getByLabelText('Output')).toHaveValue('decompiled-csharp'))
    fireEvent.change(screen.getByLabelText('Output'), {
      target: { value: 'il' },
    })
    const runButton = await screen.findByRole('button', { name: 'Render IL' })
    await waitFor(() => expect(runButton).not.toBeDisabled())
    fireEvent.click(runButton)

    await waitFor(() => expect(requests).toHaveLength(1))
    expect(requests[0]).toMatchObject({ target: 'artifact' })
    const buildSource = await waitFor(() => operationSocket(buildOperationId))
    buildSource.emitOperation({
      operationId: buildOperationId,
      sequence: 1,
      timestampUtc: new Date().toISOString(),
      traceId: 'trace-build',
      payload: {
        kind: 'typed-result',
        result: {
          resultType: 'build',
          outcome: 'succeeded',
          artifactRef,
          diagnostics: [],
          identity: {
            releaseId: catalog.releaseId,
            languageId: 'csharp',
            toolchainId: 'roslyn-stable',
            compilerVersion: '5.6.0',
            compilerCommit,
            referenceSetId: 'net10-ref',
            workerImageId: compilerImage,
          },
          workspaceRevision: 1,
          selectionRevision: 2,
        },
      },
    })
    buildSource.emitOperation({
      operationId: buildOperationId,
      sequence: 2,
      timestampUtc: new Date().toISOString(),
      traceId: 'trace-build',
      payload: {
        kind: 'completed',
        status: 'completed',
        elapsed: '00:00:00.0100000',
      },
    })

    await waitFor(() => expect(requests).toHaveLength(2))
    expect(requests[1]).toMatchObject({
      artifactRef,
      processorId: 'artifacts-default',
      outputId: 'il',
    })
    const renderSource = await waitFor(() => operationSocket(renderOperationId))
    renderSource.emitOperation({
      operationId: renderOperationId,
      sequence: 1,
      timestampUtc: new Date().toISOString(),
      traceId: 'trace-render',
      payload: {
        kind: 'content-produced',
        contentRef,
        mediaType: 'text/plain',
        size: 45,
      },
    })
    renderSource.emitOperation({
      operationId: renderOperationId,
      sequence: 2,
      timestampUtc: new Date().toISOString(),
      traceId: 'trace-render',
      payload: {
        kind: 'typed-result',
        result: {
          resultType: 'artifact-render',
          outcome: 'succeeded',
          contentRef,
          mediaType: 'text/plain',
          linkedRanges: [],
          diagnostics: [],
          identity: {
            releaseId: catalog.releaseId,
            processorId: 'artifacts-default',
            processorVersion: '1.0.0',
            workerImageId: processorImage,
          },
        },
      },
    })
    renderSource.emitOperation({
      operationId: renderOperationId,
      sequence: 3,
      timestampUtc: new Date().toISOString(),
      traceId: 'trace-render',
      payload: {
        kind: 'completed',
        status: 'completed',
        elapsed: '00:00:00.0100000',
      },
    })

    expect(await screen.findByRole('textbox', { name: 'Intermediate language' })).toHaveTextContent('.method public static void Main() cil managed')
    expect(screen.getByRole('tab', { name: 'IL' })).toHaveAttribute('aria-selected', 'true')

    const compiler = document.querySelector<HTMLElement>('[data-identity="compiler"] dd')
    const referenceSet = document.querySelector<HTMLElement>('[data-identity="reference-set"] dd')
    const processor = document.querySelector<HTMLElement>('[data-identity="processor"] dd')
    expect(compiler).toHaveTextContent('5.6.0 @ abcdef012345')
    expect(compiler).toHaveAttribute('title', expect.stringContaining(compilerCommit))
    expect(compiler).toHaveAttribute('title', expect.stringContaining(compilerImage))
    expect(referenceSet?.previousElementSibling).toHaveTextContent('Reference set')
    expect(referenceSet).toHaveTextContent('.NET 10 · sha256:dddddddddddd')
    expect(referenceSet).toHaveAttribute('title', expect.stringContaining(referenceSetDigest))
    expect(processor).toHaveTextContent('Artifacts 1.0.0')
    expect(processor).toHaveAttribute('title', expect.stringContaining(processorImage))
    expect(document.querySelector('[data-identity="runtime"] dd')).toHaveTextContent('Not required')
    expect(document.querySelector('[data-identity="images"] dd')).toHaveTextContent('2 images')

    const copyOutputButton = screen.getByRole('button', {
      name: 'Copy output',
    })
    const resultControls = screen.getByRole('toolbar', {
      name: 'Result controls',
    })
    expect(resultControls).toContainElement(copyOutputButton)
    expect(resultControls.querySelector('.result-state-slot')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancel operation' })).not.toBeInTheDocument()
    expect(resultControls.querySelectorAll('button')).toHaveLength(1)
    expect(resultControls.parentElement).toHaveClass('result-tabs-toolbar')
    expect(document.querySelector('.result-header')).not.toBeInTheDocument()

    fireEvent.click(copyOutputButton)
    await waitFor(() => expect(writeClipboard).toHaveBeenCalledOnce())
    expect(writeClipboard).toHaveBeenCalledWith('.method public static void Main() cil managed')
  })

  describe('live compilation', () => {
    beforeEach(() => {
      vi.useFakeTimers()
    })

    afterEach(() => {
      vi.clearAllTimers()
      vi.useRealTimers()
    })

    it('keeps the successful output and selected tab while Catalog returns 502', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)
      await advanceTime(450)
      await flushReact()

      const operationId = gateway.operationIds[0]
      const workspaceRevision = gateway.buildRequests[0]?.workspace.revision
      if (!operationId || workspaceRevision === undefined) {
        throw new Error('Expected the initial live AST operation.')
      }
      await completeAstOperation(operationSocket(operationId), operationId, workspaceRevision, 'RetainedRoot')
      const astTab = screen.getByRole('tab', { name: 'AST' })
      expect(astTab).toHaveAttribute('aria-selected', 'true')

      gateway.setCatalogStatus(502)
      await advanceTime(5_000)
      await flushReact()

      expect(document.querySelector('.app-bar')).toHaveAttribute('data-health-state', 'error')
      expect(screen.getByRole('status', { name: 'Gateway unavailable' })).toBeVisible()
      expect(screen.queryByText('Gateway unavailable')).not.toBeInTheDocument()
      expect(screen.queryByText(/Gateway request failed/)).not.toBeInTheDocument()
      expect(screen.getAllByText('RetainedRoot').length).toBeGreaterThan(0)
      expect(astTab).toHaveAttribute('aria-selected', 'true')

      gateway.setCatalogStatus(200)
      await advanceTime(5_000)
      await flushReact()

      expect(document.querySelector('.app-bar')).toHaveAttribute('data-health-state', 'ready')
      expect(screen.queryByRole('status', { name: 'Gateway unavailable' })).not.toBeInTheDocument()
      expect(screen.getAllByText('RetainedRoot').length).toBeGreaterThan(0)
      expect(astTab).toHaveAttribute('aria-selected', 'true')
    })

    it('keeps the successful output through an operation WebSocket reconnect', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)
      await advanceTime(450)
      await flushReact()

      const retainedOperationId = gateway.operationIds[0]
      const retainedRevision = gateway.buildRequests[0]?.workspace.revision
      if (!retainedOperationId || retainedRevision === undefined) {
        throw new Error('Expected the retained live AST operation.')
      }
      await completeAstOperation(operationSocket(retainedOperationId), retainedOperationId, retainedRevision, 'BeforeDisconnectRoot')
      const astTab = screen.getByRole('tab', { name: 'AST' })

      fireEvent.change(screen.getByLabelText('Source editor'), {
        target: { value: 'public static class AfterReconnect {}' },
      })
      await advanceTime(250)
      await flushReact()
      await advanceTime(450)
      await flushReact()

      const activeOperationId = gateway.operationIds[1]
      const activeRevision = gateway.buildRequests[1]?.workspace.revision
      if (!activeOperationId || activeRevision === undefined) {
        throw new Error('Expected the active live AST operation.')
      }
      operationSocket(activeOperationId).close(1006, 'Simulated network loss')
      await flushReact()

      expect(document.querySelector('.app-bar')).toHaveAttribute('data-health-state', 'error')
      expect(screen.getByRole('status', { name: 'Gateway unavailable' })).toBeVisible()
      expect(screen.getAllByText('BeforeDisconnectRoot').length).toBeGreaterThan(0)
      expect(astTab).toHaveAttribute('aria-selected', 'true')
      expect(screen.queryByText(/WebSocket disconnected|Gateway unavailable/)).not.toBeInTheDocument()

      await advanceTime(250)
      await flushReact()

      expect(document.querySelector('.app-bar')).toHaveAttribute('data-health-state', 'ready')
      expect(gateway.buildRequests).toHaveLength(2)
      await completeAstOperation(operationSocket(activeOperationId), activeOperationId, activeRevision, 'AfterReconnectRoot')

      expect(screen.getAllByText('AfterReconnectRoot').length).toBeGreaterThan(0)
      expect(screen.queryAllByText('BeforeDisconnectRoot')).toHaveLength(0)
      expect(astTab).toHaveAttribute('aria-selected', 'true')
    })

    it('drops results from the previous language as soon as the selection changes', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)
      await advanceTime(450)
      await flushReact()

      const operationId = gateway.operationIds[0]
      const workspaceRevision = gateway.buildRequests[0]?.workspace.revision
      if (!operationId || workspaceRevision === undefined) {
        throw new Error('Expected the initial live AST operation.')
      }
      await completeAstOperation(operationSocket(operationId), operationId, workspaceRevision, 'CSharpSelectionRoot')
      expect(screen.getAllByText('CSharpSelectionRoot').length).toBeGreaterThan(0)

      fireEvent.change(screen.getByLabelText('Language'), {
        target: { value: 'fsharp' },
      })
      await flushReact()

      expect(screen.getByLabelText('Language')).toHaveValue('fsharp')
      expect(screen.queryAllByText('CSharpSelectionRoot')).toHaveLength(0)
    })

    it('starts the initial safe output without the recurring live-build debounce', async () => {
      const gateway = mockLiveCompilationGateway()
      const initial = useWorkbenchStore.getState()
      initial.setSelectionIntent({
        languageId: initial.languageId,
        toolchainId: initial.toolchainId,
        referenceSetId: initial.referenceSetId,
        outputId: 'ast',
        runtimeId: null,
      })
      renderApp()

      expect(gateway.buildRequests).toHaveLength(0)
      await flushReact()

      expect(gateway.resolveRequests.at(-1)?.outputId).toBe('ast')
      expect(gateway.buildRequests).toHaveLength(0)
      await advanceTime(1)
      await flushReact()
      expect(gateway.buildRequests).toHaveLength(1)
      const resolvedState = useWorkbenchStore.getState()
      expect(gateway.buildRequests[0]).toMatchObject({
        target: 'ast',
        workspace: {
          revision: resolvedState.workspaceRevision,
          selectionRevision: resolvedState.selectionRevision,
          files: [{ path: 'Program.cs', text: 'Console.WriteLine("C#");\n' }],
        },
      })
    })

    it('starts the initial Run build without the recurring 900ms debounce', async () => {
      const gateway = mockLiveCompilationGateway()
      const initial = useWorkbenchStore.getState()
      initial.setSelectionIntent({
        languageId: initial.languageId,
        toolchainId: initial.toolchainId,
        referenceSetId: initial.referenceSetId,
        outputId: 'run',
        runtimeId: 'dotnet-10-linux-x64',
      })
      renderApp()

      await flushReact()

      expect(gateway.resolveRequests.at(-1)?.outputId).toBe('run')
      expect(gateway.buildRequests).toHaveLength(0)
      await advanceTime(1)
      await flushReact()
      expect(gateway.buildRequests).toHaveLength(1)
      expect(gateway.buildRequests[0]).toMatchObject({
        target: 'artifact',
      })
    })

    it('restores JIT once on bootstrap and live-runs it again after an edit', async () => {
      const restoredSource = [
        'using System.Runtime.CompilerServices;',
        'public static class Program',
        '{',
        '    [MethodImpl(MethodImplOptions.NoInlining)]',
        '    public static int Value() => 42;',
        '    public static void Main() => System.Console.WriteLine(Value());',
        '}',
        '',
      ].join('\n')
      const encoded = await shareCodec.encodeV3({
        languageId: 'csharp',
        toolchainId: 'roslyn-stable',
        referenceSetId: 'net10-ref',
        outputId: 'jit-asm',
        runtimeId: 'dotnet-10-linux-x64',
        buildMode: 'release',
        releaseVersion: 'test-release',
        activeFile: 'Program.cs',
        sourceOrder: ['Program.cs'],
        files: [{ path: 'Program.cs', text: restoredSource }],
      })
      window.history.replaceState(null, '', `/${encoded.fragment}`)
      const gateway = mockLiveCompilationGateway()

      renderApp({ strict: true })
      await flushMicrotasks()
      await flushReact()
      await flushMicrotasks()

      expect(screen.getByLabelText('Output')).toHaveValue('jit-asm')
      expect(screen.getByLabelText('Source editor')).toHaveValue(restoredSource)
      expect(gateway.resolveRequests).toHaveLength(1)
      expect(gateway.resolveRequests[0]).toMatchObject({
        outputId: 'jit-asm',
        runtimeId: 'dotnet-10-linux-x64',
      })
      expect(gateway.buildRequests).toHaveLength(0)

      await advanceTime(1)
      await flushReact()

      expect(gateway.buildRequests).toHaveLength(1)
      expect(gateway.buildRequests[0]).toMatchObject({
        target: 'artifact',
        workspace: {
          files: [{ path: 'Program.cs', text: restoredSource }],
        },
      })
      const buildOperationId = gateway.operationIds[0]
      const buildRequest = gateway.buildRequests[0]
      if (!buildOperationId || !buildRequest) throw new Error('Expected the restored JIT Build.')
      const artifactRef = `sha256:${'b'.repeat(64)}`
      await completeArtifactBuild(operationSocket(buildOperationId), buildOperationId, buildRequest, artifactRef)

      expect(gateway.jitRequests).toHaveLength(1)
      expect(gateway.jitRequests[0]).toMatchObject({
        artifactRef,
        runtimeProfileId: 'dotnet-10-linux-x64',
        options: { methodFilter: null },
      })
      const jitOperationId = gateway.operationIds[1]
      if (!jitOperationId) throw new Error('Expected the restored JIT inspection.')
      await completeJitOperation(operationSocket(jitOperationId), jitOperationId)

      await advanceTime(5_000)
      await flushReact()
      expect(gateway.buildRequests).toHaveLength(1)
      expect(gateway.jitRequests).toHaveLength(1)

      fireEvent.change(screen.getByLabelText('Source editor'), {
        target: { value: `${restoredSource}\n// ordinary edit` },
      })
      await advanceTime(250)
      await flushReact()
      await advanceTime(450)
      await flushReact()
      expect(gateway.buildRequests).toHaveLength(2)
      expect(gateway.jitRequests).toHaveLength(1)
      expect(gateway.buildRequests[1]?.workspace.files).toEqual([
        expect.objectContaining({
          text: `${restoredSource}\n// ordinary edit`,
        }),
      ])
    })

    it('starts JIT when switching from another output', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)

      await advanceTime(450)
      await flushReact()
      const astOperationId = gateway.operationIds[0]
      const astRevision = gateway.buildRequests[0]?.workspace.revision
      if (!astOperationId || astRevision === undefined) throw new Error('Expected live AST.')
      await completeAstOperation(operationSocket(astOperationId), astOperationId, astRevision, 'BeforeJitRoot')

      fireEvent.change(screen.getByLabelText('Output'), {
        target: { value: 'jit-asm' },
      })
      await advanceTime(250)
      await flushReact()
      await advanceTime(450)
      await flushReact()

      expect(gateway.buildRequests).toHaveLength(2)
      expect(gateway.buildRequests[1]).toMatchObject({ target: 'artifact' })
      expect(screen.getByRole('tab', { name: 'JIT' })).toHaveAttribute('aria-selected', 'true')
    })

    it('starts JSIL and selects its result tab when switching from another output', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)

      await advanceTime(450)
      await flushReact()
      const astOperationId = gateway.operationIds[0]
      const astRevision = gateway.buildRequests[0]?.workspace.revision
      if (!astOperationId || astRevision === undefined) throw new Error('Expected live AST.')
      await completeAstOperation(operationSocket(astOperationId), astOperationId, astRevision, 'BeforeJsilRoot')

      fireEvent.change(screen.getByLabelText('Output'), {
        target: { value: 'javascript' },
      })
      await advanceTime(250)
      await flushReact()
      await advanceTime(450)
      await flushReact()

      expect(gateway.buildRequests).toHaveLength(2)
      expect(gateway.buildRequests[1]).toMatchObject({ target: 'artifact' })
      expect(screen.getByRole('tab', { name: 'JavaScript (JSIL)' })).toHaveAttribute('aria-selected', 'true')
    })

    it('cancels the initial zero-delay callback when its workspace revision changes', async () => {
      const gateway = mockLiveCompilationGateway()
      const initial = useWorkbenchStore.getState()
      initial.setSelectionIntent({
        languageId: initial.languageId,
        toolchainId: initial.toolchainId,
        referenceSetId: initial.referenceSetId,
        outputId: 'ast',
        runtimeId: null,
      })
      renderApp()

      await flushReact()
      expect(gateway.resolveRequests.at(-1)?.outputId).toBe('ast')
      expect(gateway.buildRequests).toHaveLength(0)
      await flushMicrotasks()

      fireEvent.change(screen.getByLabelText('Source editor'), {
        target: { value: 'class LatestBeforeBootstrap {}' },
      })
      await advanceTime(1)
      expect(gateway.buildRequests).toHaveLength(0)

      await advanceTime(249)
      await flushReact()
      await advanceTime(449)
      expect(gateway.buildRequests).toHaveLength(0)

      await advanceTime(1)
      await flushReact()
      expect(gateway.buildRequests).toHaveLength(1)
      expect(gateway.buildRequests[0]?.workspace.files).toEqual([expect.objectContaining({ text: 'class LatestBeforeBootstrap {}' })])
    })

    it('resolves only the workspace restored from an older share URL', async () => {
      const encoded = await shareCodec.encodeV3({
        languageId: 'fsharp',
        toolchainId: 'fsharp-stable',
        referenceSetId: 'net10-ref',
        outputId: 'ast',
        runtimeId: 'not-required',
        buildMode: 'release',
        releaseVersion: 'older-release',
        activeFile: 'Program.fs',
        sourceOrder: ['Program.fs'],
        files: [{ path: 'Program.fs', text: 'printfn "Restored before selection"\n' }],
      })
      window.history.replaceState(null, '', `/${encoded.fragment}`)
      const gateway = mockLiveCompilationGateway()

      renderApp()
      await flushMicrotasks()
      await flushReact()
      await flushMicrotasks()

      expect(screen.getByLabelText('Source editor')).toHaveValue('printfn "Restored before selection"\n')
      expect(gateway.resolveRequests).toHaveLength(1)
      expect(gateway.resolveRequests[0]).toMatchObject({
        languageId: 'fsharp',
        toolchainId: 'fsharp-stable',
        outputId: 'ast',
        workspaceRevision: useWorkbenchStore.getState().workspaceRevision,
      })
      expect(gateway.resolveRequests).not.toContainEqual(
        expect.objectContaining({
          languageId: 'csharp',
          outputId: 'decompiled-csharp',
        }),
      )
      expect(gateway.buildRequests).toHaveLength(0)
      await advanceTime(1)
      await flushReact()
      expect(gateway.buildRequests).toEqual([
        expect.objectContaining({
          toolchainId: 'fsharp-stable',
          target: 'ast',
          workspace: expect.objectContaining({
            files: [expect.objectContaining({ path: 'Program.fs' })],
          }),
        }),
      ])
    })

    it('loads generated source documents and ignores late content from an older workflow', async () => {
      const gateway = mockLiveCompilationGateway()
      const gatewayFetch = vi.mocked(fetch)
      const oldContentRef = `sha256:${'a'.repeat(64)}`
      const newContentRef = `sha256:${'b'.repeat(64)}`
      let oldContentRequested = false
      let resolveOldContent = (_response: Response): void => {
        throw new Error('The old generated source request did not start.')
      }
      const oldContent = new Promise<Response>((resolve) => {
        resolveOldContent = resolve
      })
      vi.stubGlobal(
        'fetch',
        vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
          const url = typeof input === 'string' ? input : input.toString()
          if (url.endsWith(`/contents/sha256/${'a'.repeat(64)}`)) {
            oldContentRequested = true
            return oldContent
          }
          if (url.endsWith(`/contents/sha256/${'b'.repeat(64)}`)) {
            return new Response('public static class LatestGenerated {}')
          }
          return gatewayFetch(input, init)
        }),
      )

      await renderResolvedApp(gateway)
      fireEvent.change(screen.getByLabelText('Output'), {
        target: { value: 'generated-source' },
      })
      await advanceTime(250)
      await flushReact()
      await advanceTime(450)
      await flushReact()

      const oldRequest = gateway.buildRequests[0]
      const oldOperationId = gateway.operationIds[0]
      if (!oldRequest || !oldOperationId) throw new Error('Expected generated-source Build.')
      await completeGeneratedSourceOperation(operationSocket(oldOperationId), oldOperationId, oldRequest.workspace.revision, oldRequest.workspace.selectionRevision, oldContentRef, 'Generated/Old.g.cs')
      expect(oldContentRequested).toBe(true)

      fireEvent.change(screen.getByLabelText('Source editor'), {
        target: { value: 'public static class LatestInput {}' },
      })
      await advanceTime(250)
      await flushReact()
      await advanceTime(450)
      await flushReact()

      const newRequest = gateway.buildRequests[1]
      const newOperationId = gateway.operationIds[1]
      if (!newRequest || !newOperationId) throw new Error('Expected the latest generated-source Build.')
      await completeGeneratedSourceOperation(operationSocket(newOperationId), newOperationId, newRequest.workspace.revision, newRequest.workspace.selectionRevision, newContentRef, 'Generated/Latest.g.cs')

      expect(screen.getByRole('textbox', { name: 'Generated source Generated/Latest.g.cs' })).toHaveTextContent('LatestGenerated')
      resolveOldContent(new Response('public static class StaleGenerated {}'))
      await flushReact()

      expect(screen.queryByText('StaleGenerated')).not.toBeInTheDocument()
      expect(screen.getByRole('textbox', { name: 'Generated source Generated/Latest.g.cs' })).toHaveTextContent('LatestGenerated')
    })

    it('coalesces rapid edits and builds only the latest immutable snapshot', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)
      const editor = screen.getByLabelText('Source editor')
      const initialRevision = useWorkbenchStore.getState().workspaceRevision

      fireEvent.change(editor, { target: { value: 'class First {}' } })
      await advanceTime(100)
      fireEvent.change(editor, { target: { value: 'class Second {}' } })
      await advanceTime(100)
      fireEvent.change(editor, { target: { value: 'class Latest {}' } })

      await advanceTime(250)
      await flushReact()
      await advanceTime(449)
      expect(gateway.buildRequests).toHaveLength(0)

      await advanceTime(1)
      await flushReact()

      expect(gateway.buildRequests).toHaveLength(1)
      expect(gateway.buildRequests[0]?.workspace).toMatchObject({
        revision: initialRevision + 3,
        files: [
          {
            path: 'Program.cs',
            version: initialRevision + 3,
            text: 'class Latest {}',
          },
        ],
      })
      expect(gateway.buildRequests[0]?.workspace.revision).toBe(useWorkbenchStore.getState().workspaceRevision)

      await advanceTime(1_000)
      expect(gateway.buildRequests).toHaveLength(1)
    })

    it('debounces Run, completes its pipeline over operation WebSockets, and shows stdout', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)
      fireEvent.change(screen.getByLabelText('Output'), {
        target: { value: 'run' },
      })
      await advanceTime(250)
      await flushReact()

      const latestSource = 'System.Console.WriteLine("live run output");'
      fireEvent.change(screen.getByLabelText('Source editor'), {
        target: { value: latestSource },
      })
      await advanceTime(250)
      await flushReact()
      await advanceTime(899)
      expect(gateway.buildRequests).toHaveLength(0)

      await advanceTime(1)
      await flushReact()

      expect(gateway.buildRequests).toHaveLength(1)
      expect(gateway.buildRequests[0]?.workspace.files).toEqual([expect.objectContaining({ path: 'Program.cs', text: latestSource })])
      const buildOperationId = gateway.operationIds[0]
      const buildRequest = gateway.buildRequests[0]
      if (!buildOperationId || !buildRequest) throw new Error('Expected a live Run Build.')
      const artifactRef = `sha256:${'a'.repeat(64)}`
      await completeArtifactBuild(operationSocket(buildOperationId), buildOperationId, buildRequest, artifactRef)

      expect(gateway.runRequests).toHaveLength(1)
      expect(gateway.runRequests[0]).toMatchObject({
        artifactRef,
        runtimeProfileId: 'dotnet-10-linux-x64',
      })
      const runOperationId = gateway.operationIds[1]
      if (!runOperationId) throw new Error('Expected the live Run operation.')
      await completeRunOperation(operationSocket(runOperationId), runOperationId, 'live run output\n')

      expect(screen.getByText('live run output')).toBeVisible()
      const statusBar = document.querySelector('.status-bar')
      expect(statusBar).toContainElement(screen.getByRole('status', { name: 'Run status' }))
      expect(statusBar).toHaveTextContent('Exit 0')
      expect(statusBar).toHaveTextContent('20 ms')
      expect(document.querySelector('.terminal-view .run-status')).not.toBeInTheDocument()
    })

    it('cancels one superseded live operation and recovers when its terminal WebSocket event is lost', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)
      fireEvent.change(screen.getByLabelText('Output'), {
        target: { value: 'run' },
      })
      await advanceTime(250)
      await flushReact()
      await advanceTime(900)
      await flushReact()

      expect(gateway.buildRequests).toHaveLength(1)
      const oldOperationId = gateway.operationIds[0]
      if (!oldOperationId) throw new Error('Expected the superseded live Build.')

      const editor = screen.getByLabelText('Source editor')
      fireEvent.change(editor, {
        target: { value: 'class FirstReplacement {}' },
      })
      fireEvent.change(editor, {
        target: { value: 'class SecondReplacement {}' },
      })
      fireEvent.change(editor, {
        target: { value: 'class LatestReplacement {}' },
      })
      await advanceTime(250)
      await flushReact()

      expect(gateway.cancelledOperationIds).toEqual([oldOperationId])
      expect(gateway.buildRequests).toHaveLength(1)

      gateway.operationStatuses.set(oldOperationId, 'cancelled')
      operationSocket(oldOperationId).close(1006, 'Simulated network loss')
      await advanceTime(250)
      await flushReact()
      operationSocket(oldOperationId).emitOperation({
        operationId: oldOperationId,
        sequence: 1,
        timestampUtc: new Date().toISOString(),
        traceId: `trace-${oldOperationId}`,
        payload: {
          kind: 'completed',
          status: 'cancelled',
          elapsed: '00:00:00.0100000',
        },
      })
      await flushReact()
      await advanceTime(899)
      expect(gateway.buildRequests).toHaveLength(1)

      await advanceTime(1)
      await flushReact()

      expect(gateway.buildRequests).toHaveLength(2)
      expect(gateway.buildRequests[1]?.workspace.files).toEqual([expect.objectContaining({ text: 'class LatestReplacement {}' })])
      expect(gateway.cancelledOperationIds).toEqual([oldOperationId])
    })

    it('does not let a late result from an older revision replace the latest result', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)
      await advanceTime(450)
      await flushReact()

      const oldOperationId = gateway.operationIds[0]
      if (!oldOperationId) throw new Error('Expected the first live Build operation.')
      const oldSocket = operationSocket(oldOperationId)
      const oldRevision = gateway.buildRequests[0]?.workspace.revision
      if (oldRevision === undefined) throw new Error('Expected the first workspace revision.')

      fireEvent.change(screen.getByLabelText('Source editor'), {
        target: { value: 'class LatestRevision {}' },
      })
      const latestRevision = useWorkbenchStore.getState().workspaceRevision
      await completeAstOperation(oldSocket, oldOperationId, oldRevision, 'OldRevisionRoot')

      expect(screen.getByRole('status', { name: 'Result stale' })).toBeVisible()
      expect(screen.getAllByText('OldRevisionRoot').length).toBeGreaterThan(0)

      await advanceTime(250)
      await flushReact()
      await advanceTime(450)
      await flushReact()

      expect(gateway.buildRequests).toHaveLength(2)
      expect(gateway.buildRequests[1]?.workspace).toMatchObject({
        revision: latestRevision,
        files: [{ text: 'class LatestRevision {}' }],
      })
      const latestOperationId = gateway.operationIds[1]
      if (!latestOperationId) throw new Error('Expected the latest live Build operation.')
      await completeAstOperation(operationSocket(latestOperationId), latestOperationId, latestRevision, 'LatestRevisionRoot')

      expect(screen.getAllByText('LatestRevisionRoot').length).toBeGreaterThan(0)
      expect(screen.queryAllByText('OldRevisionRoot')).toHaveLength(0)
      expect(screen.queryByRole('status', { name: 'Result stale' })).not.toBeInTheDocument()

      await act(async () => {
        oldSocket.emitOperation({
          operationId: oldOperationId,
          sequence: 3,
          timestampUtc: new Date().toISOString(),
          traceId: `trace-${oldOperationId}`,
          payload: {
            kind: 'progress',
            stage: 'late-old-revision',
            fraction: 1,
            message: 'Late old revision event',
          },
        })
      })
      await flushReact()

      expect(screen.getAllByText('LatestRevisionRoot').length).toBeGreaterThan(0)
      expect(screen.queryByText('Late old revision event')).not.toBeInTheDocument()
    })

    it('retains the last successful output and selects diagnostics when the latest live build fails', async () => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)
      await advanceTime(450)
      await flushReact()

      const stableOperationId = gateway.operationIds[0]
      const stableRevision = gateway.buildRequests[0]?.workspace.revision
      if (!stableOperationId || stableRevision === undefined) {
        throw new Error('Expected the first live AST operation.')
      }
      await completeAstOperation(operationSocket(stableOperationId), stableOperationId, stableRevision, 'StableRoot')
      expect(screen.getAllByText('StableRoot').length).toBeGreaterThan(0)
      const resultTabs = screen.getByRole('tablist', { name: 'Result views' })
      expect(Array.from(resultTabs.querySelectorAll('[role="tab"]')).map((tab) => tab.textContent?.replace(/ \(\d+\)$/, ''))).toEqual(['Diagnostics', 'AST'])

      fireEvent.change(screen.getByLabelText('Source editor'), {
        target: { value: 'public class Broken { this is invalid }' },
      })
      expect(screen.getAllByText('StableRoot').length).toBeGreaterThan(0)
      expect(screen.getByRole('status', { name: 'Result stale' })).toBeVisible()

      await advanceTime(250)
      await flushReact()
      await advanceTime(450)
      await flushReact()
      expect(screen.getAllByText('StableRoot').length).toBeGreaterThan(0)

      const failedOperationId = gateway.operationIds[1]
      const failedRequest = gateway.buildRequests[1]
      if (!failedOperationId || !failedRequest) {
        throw new Error('Expected the failing live AST operation.')
      }
      await act(async () => {
        const socket = operationSocket(failedOperationId)
        socket.emitOperation({
          operationId: failedOperationId,
          sequence: 1,
          timestampUtc: new Date().toISOString(),
          traceId: `trace-${failedOperationId}`,
          payload: {
            kind: 'typed-result',
            result: {
              resultType: 'build',
              outcome: 'compilation-failed',
              artifactRef: null,
              diagnostics: [
                {
                  source: 'roslyn',
                  code: 'CS1002',
                  severity: 'error',
                  message: '; expected',
                  filePath: 'Program.cs',
                  range: {
                    startLine: 0,
                    startCharacter: 20,
                    endLine: 0,
                    endCharacter: 20,
                  },
                  relatedInformation: [],
                  tags: [],
                  workspaceRevision: failedRequest.workspace.revision,
                  selectionRevision: failedRequest.workspace.selectionRevision,
                },
              ],
              identity: {
                releaseId: 'release-live',
                languageId: 'csharp',
                toolchainId: 'roslyn-stable',
                compilerVersion: '5.6.0',
                compilerCommit: 'compiler-commit',
                referenceSetId: 'net10-ref',
                workerImageId: `sha256:${'a'.repeat(64)}`,
              },
              workspaceRevision: failedRequest.workspace.revision,
              selectionRevision: failedRequest.workspace.selectionRevision,
            },
          },
        })
        socket.emitOperation({
          operationId: failedOperationId,
          sequence: 2,
          timestampUtc: new Date().toISOString(),
          traceId: `trace-${failedOperationId}`,
          payload: {
            kind: 'completed',
            status: 'completed',
            elapsed: '00:00:00.0100000',
          },
        })
      })
      await flushReact()

      expect(screen.getByRole('tab', { name: 'Diagnostics (1)' })).toHaveAttribute('aria-selected', 'true')
      expect(screen.getByText('; expected')).toBeVisible()
      expect(screen.getByText(/Compilation failed/)).toBeVisible()
      fireEvent.click(screen.getByRole('tab', { name: 'AST' }))
      expect(screen.getAllByText('StableRoot').length).toBeGreaterThan(0)
      expect(Array.from(resultTabs.querySelectorAll('[role="tab"]')).map((tab) => tab.textContent?.replace(/ \(\d+\)$/, ''))).toEqual(['Diagnostics', 'AST'])
    })

    it.each([
      { outputId: 'execution-flow', action: 'Run' },
      { outputId: 'run-il', action: 'Render IL' },
    ])('keeps $outputId explicit while allowing a manual operation with the latest source', async ({ outputId, action }) => {
      const gateway = mockLiveCompilationGateway()
      await renderResolvedApp(gateway)

      fireEvent.change(screen.getByLabelText('Output'), {
        target: { value: outputId },
      })
      await advanceTime(250)
      await flushReact()
      expect(screen.getByLabelText('Output')).toHaveValue(outputId)

      const latestSource = `Console.WriteLine("${outputId}");`
      fireEvent.change(screen.getByLabelText('Source editor'), {
        target: { value: latestSource },
      })
      await advanceTime(250)
      await flushReact()
      await advanceTime(1_000)

      expect(gateway.buildRequests).toHaveLength(0)
      const manualButton = screen.getAllByRole('button', { name: action }).find((candidate) => !candidate.hasAttribute('disabled'))
      expect(manualButton).toBeDefined()
      fireEvent.click(manualButton as HTMLButtonElement)
      await flushReact()

      expect(gateway.buildRequests).toHaveLength(1)
      expect(gateway.buildRequests[0]).toMatchObject({
        target: 'artifact',
        workspace: { files: [{ text: latestSource }] },
      })
    })
  })

  it('recognizes only exact source associations and mapped output lines as active clicks', () => {
    const root = document.createElement('div')
    root.innerHTML = [
      '<span class="cm-source-association-range"><i data-target="source"></i></span>',
      '<div class="cm-line cm-source-navigable source-association"><i data-target="output"></i></div>',
      '<span data-target="other"></span>',
    ].join('')
    expect(isSourceAssociationInteractionTarget(root.querySelector('[data-target="source"]'))).toBe(true)
    expect(isSourceAssociationInteractionTarget(root.querySelector('[data-target="output"]'))).toBe(true)
    expect(isSourceAssociationInteractionTarget(root.querySelector('[data-target="other"]'))).toBe(false)
  })
})
