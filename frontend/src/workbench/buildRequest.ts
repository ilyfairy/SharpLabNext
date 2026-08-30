import type { BuildRequest, BuildTarget, ResolveSelectionResponse } from '../api/types'
import { createWorkbenchBuildOptions } from './buildOptions'
import type { WorkbenchSnapshot } from './storeSnapshot'

export function buildTargetForOutput(outputId: string): BuildTarget {
  if (outputId === 'compile-check') return 'compile-check';
  if (outputId === 'ast') return 'ast';
  if (outputId === 'generated-source') return 'generated-source';
  return 'artifact';
}

function createRequestId(): string {
  const randomUuid = globalThis.crypto?.randomUUID
  if (randomUuid) return `req_${randomUuid.call(globalThis.crypto)}`;
  return `req_${Date.now().toString(36)}_${Math.random().toString(36).slice(2)}`;
}

export function createBuildRequest(resolution: ResolveSelectionResponse, workspace: WorkbenchSnapshot): BuildRequest {
  const requestId = createRequestId()
  const buildOptions = createWorkbenchBuildOptions(resolution.effectiveSelection.languageId, workspace.buildMode, resolution.pipelinePlan.stages)

  return {
    requestId,
    idempotencyKey: `build:${requestId}`,
    pipelineResolutionId: resolution.pipelineResolutionId,
    toolchainId: resolution.effectiveSelection.toolchainId,
    referenceSetId: resolution.effectiveSelection.referenceSetId,
    workspace: {
      schemaVersion: 1,
      revision: workspace.workspaceRevision,
      selectionRevision: workspace.selectionRevision,
      languageId: resolution.effectiveSelection.languageId,
      files: workspace.files.map((file) => ({
        path: file.path,
        version: workspace.workspaceRevision,
        text: file.text,
      })),
      activeFile: workspace.activeFile,
      sourceOrder: workspace.sourceOrder,
      referenceSetId: resolution.effectiveSelection.referenceSetId,
      buildOptions,
    },
    deadlineUtc: new Date(Date.now() + 15_000).toISOString(),
    target: buildTargetForOutput(resolution.effectiveSelection.outputId),
  }
}
