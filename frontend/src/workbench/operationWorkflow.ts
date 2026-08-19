import {
  startArtifactRender,
  startArtifactTransform,
  startBuild,
  startExplain,
  startJit,
  startRun,
  startVerification,
} from '../api/client'
import type {
  ArtifactRef,
  BuildRequest,
  ExplainRequest,
  JitRequest,
  OperationHandle,
  PipelineStageDescriptor,
  RenderArtifactRequest,
  ResolveSelectionResponse,
  RunRequest,
  TransformArtifactRequest,
  VerifyArtifactRequest,
} from '../api/types'
import { createBuildRequest } from './buildRequest'
import type { WorkbenchSnapshot } from './storeSnapshot'

export type PipelineOperationKind =
  | 'build'
  | 'transform'
  | 'render'
  | 'verify'
  | 'run'
  | 'jit'
  | 'explain'

export type PipelineOperationStart =
  | { kind: 'build'; request: BuildRequest }
  | { kind: 'transform'; request: TransformArtifactRequest }
  | { kind: 'render'; request: RenderArtifactRequest }
  | { kind: 'verify'; request: VerifyArtifactRequest }
  | { kind: 'run'; request: RunRequest }
  | { kind: 'jit'; request: JitRequest }
  | { kind: 'explain'; request: ExplainRequest }

export interface FollowupOperation {
  stage: PipelineStageDescriptor
  start: Exclude<PipelineOperationStart, { kind: 'build' }>
}

function requestIdentity(kind: PipelineOperationKind): {
  requestId: string
  idempotencyKey: string
} {
  const uuid =
    globalThis.crypto?.randomUUID?.() ??
    `${Date.now().toString(36)}_${Math.random().toString(36).slice(2)}`
  const requestId = `req_${uuid}`
  return { requestId, idempotencyKey: `${kind}:${requestId}` }
}

function deadline(seconds: number): string {
  return new Date(Date.now() + seconds * 1_000).toISOString()
}

function runtimeId(resolution: ResolveSelectionResponse): string {
  const id =
    resolution.effectiveSelection.runtimeId ?? resolution.pipelinePlan.runtimeId ?? undefined
  if (!id) throw new Error('The resolved pipeline does not identify a runtime.')
  return id
}

export function createFollowupOperation(
  resolution: ResolveSelectionResponse,
  artifactRef: ArtifactRef,
  stageIndex = 1,
): FollowupOperation | null {
  const stage = resolution.pipelinePlan.stages[stageIndex]
  if (!stage) return null

  const common = {
    pipelineResolutionId: resolution.pipelineResolutionId,
    artifactRef,
  }
  switch (stage.kind) {
    case 'transform': {
      const identity = requestIdentity('transform')
      return {
        stage,
        start: {
          kind: 'transform',
          request: {
            ...identity,
            ...common,
            processorId: stage.providerId,
            transformId: stage.id,
            options: {
              preservePortablePdb: true,
              preserveSequencePoints: true,
              rewriterProfileId:
                stage.id === 'runtime-instrumentation-v1' ? 'execution-flow-v1' : null,
            },
            deadlineUtc: deadline(30),
          },
        },
      }
    }
    case 'render': {
      const identity = requestIdentity('render')
      return {
        stage,
        start: {
          kind: 'render',
          request: {
            ...identity,
            ...common,
            processorId: stage.providerId,
            outputId: stage.id,
            options: {
              includeSequencePoints: true,
              includeCompilerGeneratedMembers: true,
              maxCharacters: 1_000_000,
            },
            deadlineUtc: deadline(30),
          },
        },
      }
    }
    case 'verify': {
      const identity = requestIdentity('verify')
      return {
        stage,
        start: {
          kind: 'verify',
          request: {
            ...identity,
            ...common,
            processorId: stage.providerId,
            options: {
              verificationProfileId: 'default',
              includeMetadataTokens: true,
              maxFindings: 1_000,
            },
            deadlineUtc: deadline(30),
          },
        },
      }
    }
    case 'run': {
      const identity = requestIdentity('run')
      return {
        stage,
        start: {
          kind: 'run',
          request: {
            ...identity,
            ...common,
            runtimeProfileId: runtimeId(resolution),
            options: {
              arguments: [],
              stdin: null,
              instrumentation:
                resolution.effectiveSelection.outputId === 'execution-flow'
                  ? 'execution-flow'
                  : 'none',
              securityPolicyId: resolution.pipelinePlan.securityPolicyId,
            },
            deadlineUtc: deadline(30),
          },
        },
      }
    }
    case 'jit': {
      const identity = requestIdentity('jit')
      return {
        stage,
        start: {
          kind: 'jit',
          request: {
            ...identity,
            ...common,
            runtimeProfileId: runtimeId(resolution),
            options: {
              // The inspector returns every user method once for one compact JIT document.
              methodFilter: null,
              tieringPolicyId: 'tier0-diffable',
              pgoPolicyId: 'disabled',
              providerId: 'coreclr-jitdisasm',
              securityPolicyId: resolution.pipelinePlan.securityPolicyId,
            },
            deadlineUtc: deadline(30),
          },
        },
      }
    }
    default:
      throw new Error(`Pipeline stage '${stage.kind}' is not supported by the workbench.`)
  }
}

export function createInitialPipelineOperation(
  resolution: ResolveSelectionResponse,
  workspace: WorkbenchSnapshot,
): PipelineOperationStart {
  const build = createBuildRequest(resolution, workspace)
  const firstStage = resolution.pipelinePlan.stages[0]
  if (firstStage?.kind !== 'explain') return { kind: 'build', request: build }
  if (resolution.effectiveSelection.outputId !== 'explain') {
    throw new Error('The resolved Explain stage does not match the effective output.')
  }
  return {
    kind: 'explain',
    request: {
      requestId: build.requestId,
      idempotencyKey: `explain:${build.requestId}`,
      pipelineResolutionId: build.pipelineResolutionId,
      workspace: build.workspace,
      deadlineUtc: build.deadlineUtc,
    },
  }
}

export function startPipelineOperation(
  operation: PipelineOperationStart,
  signal?: AbortSignal,
): Promise<OperationHandle> {
  switch (operation.kind) {
    case 'build':
      return startBuild(operation.request, signal)
    case 'transform':
      return startArtifactTransform(operation.request, signal)
    case 'render':
      return startArtifactRender(operation.request, signal)
    case 'verify':
      return startVerification(operation.request, signal)
    case 'run':
      return startRun(operation.request, signal)
    case 'jit':
      return startJit(operation.request, signal)
    case 'explain':
      return startExplain(operation.request, signal)
  }
}
