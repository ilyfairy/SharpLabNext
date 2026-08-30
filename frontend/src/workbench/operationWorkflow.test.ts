import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { PipelineStageKind, ResolveSelectionResponse } from '../api/types'
import { createFollowupOperation, createInitialPipelineOperation } from './operationWorkflow'

function resolution(outputId: string, kind: PipelineStageKind, providerId: string, runtimeId: string | null = null, securityPolicyId = runtimeId ? 'runtime-job-default' : 'compiler-default'): ResolveSelectionResponse {
  return {
    effectiveSelection: {
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      outputId,
      runtimeId,
    },
    selectionChanges: [],
    effectiveCapabilities: {
      languageServerCapabilities: [],
      buildCapabilities: ['managed-pe'],
      outputCapabilities: [outputId],
      runtimeCapabilities: runtimeId ? [kind] : [],
    },
    pipelineResolutionId: `pipeline-${outputId}`,
    pipelinePlan: {
      releaseId: 'release-test',
      languageWorkerId: 'roslyn-stable',
      compilerWorkerId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      stages: [
        {
          id: 'build',
          kind: 'build',
          providerId: 'roslyn-stable',
          outputArtifactFormat: 'dotnet-managed-pe-v1',
        },
        {
          id: outputId,
          kind,
          providerId,
          inputArtifactFormat: 'dotnet-managed-pe-v1',
        },
      ],
      runtimeId,
      securityPolicyId,
      workerImageIds: [],
    },
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
  }
}

describe('createFollowupOperation', () => {
  beforeEach(() => {
    vi.spyOn(globalThis.crypto, 'randomUUID').mockReturnValue('00000000-0000-4000-8000-000000000001')
  })

  it('starts Explain directly without an artifact build', () => {
    const resolved = resolution('explain', 'explain', 'roslyn-stable')
    resolved.pipelinePlan.stages = [
      {
        id: 'explain',
        kind: 'explain',
        providerId: 'roslyn-stable',
        outputArtifactFormat: 'explanation-document-v1',
      },
    ]

    const operation = createInitialPipelineOperation(resolved, {
      source: 'class Program { }',
      fileName: 'Program.cs',
      files: [{ path: 'Program.cs', text: 'class Program { }' }],
      activeFile: 'Program.cs',
      sourceOrder: ['Program.cs'],
      buildMode: 'release',
      workspaceRevision: 4,
      selectionRevision: 3,
    })

    expect(operation.kind).toBe('explain')
    if (operation.kind !== 'explain') throw new Error('Expected Explain request.')
    expect(operation.request.workspace).toMatchObject({
      languageId: 'csharp',
      revision: 4,
      selectionRevision: 3,
    })
    expect(operation.request.idempotencyKey).toContain('explain:')
  })

  it('builds an artifact render request from the resolved stage', () => {
    const followup = createFollowupOperation(resolution('decompiled-csharp', 'render', 'artifacts-default'), 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')

    expect(followup?.start.kind).toBe('render')
    if (followup?.start.kind !== 'render') throw new Error('Expected render request.')
    expect(followup.start.request).toMatchObject({
      requestId: 'req_00000000-0000-4000-8000-000000000001',
      idempotencyKey: 'render:req_00000000-0000-4000-8000-000000000001',
      pipelineResolutionId: 'pipeline-decompiled-csharp',
      artifactRef: 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
      processorId: 'artifacts-default',
      outputId: 'decompiled-csharp',
      options: {
        includeSequencePoints: true,
        includeCompilerGeneratedMembers: true,
        maxCharacters: 1_000_000,
      },
    })
  })

  it('creates a transform request and advances to the stage after it', () => {
    const value = resolution('execution-flow', 'run', 'dotnet-10-linux-x64', 'dotnet-10-linux-x64')
    value.pipelinePlan.stages.splice(1, 0, {
      id: 'runtime-instrumentation-v1',
      kind: 'transform',
      providerId: 'artifacts-default',
      inputArtifactFormat: 'dotnet-managed-pe-v1',
      outputArtifactFormat: 'dotnet-managed-pe-v1',
    })

    const transform = createFollowupOperation(value, 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
    expect(transform?.start.kind).toBe('transform')
    if (transform?.start.kind !== 'transform') throw new Error('Expected transform request.')
    expect(transform.start.request).toMatchObject({
      processorId: 'artifacts-default',
      transformId: 'runtime-instrumentation-v1',
      options: { rewriterProfileId: 'execution-flow-v1' },
    })

    const run = createFollowupOperation(value, 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 2)
    expect(run?.start.kind).toBe('run')
    if (run?.start.kind !== 'run') throw new Error('Expected run request.')
    expect(run.start.request.artifactRef).toBe('sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb')
    expect(run.start.request.options.instrumentation).toBe('execution-flow')
  })

  it('renders run-il from the derived artifact after runtime instrumentation', () => {
    const value = resolution('run-il', 'render', 'artifacts-default')
    value.pipelinePlan.stages.splice(1, 0, {
      id: 'runtime-instrumentation-v1',
      kind: 'transform',
      providerId: 'artifacts-default',
      inputArtifactFormat: 'dotnet-managed-pe-v1',
      outputArtifactFormat: 'dotnet-managed-pe-v1',
    })

    const transform = createFollowupOperation(value, 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
    expect(transform?.start.kind).toBe('transform')

    const render = createFollowupOperation(value, 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 2)
    expect(render?.start.kind).toBe('render')
    if (render?.start.kind !== 'render') throw new Error('Expected run-il render request.')
    expect(render.start.request).toMatchObject({
      artifactRef: 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
      outputId: 'run-il',
      processorId: 'artifacts-default',
    })
  })

  it('does not apply runtime rewriting options to an unrelated transform stage', () => {
    const value = resolution('il', 'render', 'artifacts-default')
    value.pipelinePlan.stages.splice(1, 0, {
      id: 'cil-to-managed-pe',
      kind: 'transform',
      providerId: 'il-assembler',
      inputArtifactFormat: 'cil-text-v1',
      outputArtifactFormat: 'dotnet-managed-pe-v1',
    })

    const transform = createFollowupOperation(value, 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
    expect(transform?.start.kind).toBe('transform')
    if (transform?.start.kind !== 'transform') throw new Error('Expected transform request.')
    expect(transform.start.request.options.rewriterProfileId).toBeNull()
  })

  it('uses the worker-supported IL verification profile and limits', () => {
    const followup = createFollowupOperation(resolution('il-verify', 'verify', 'artifacts-default'), 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')

    expect(followup?.start.kind).toBe('verify')
    if (followup?.start.kind !== 'verify') throw new Error('Expected verify request.')
    expect(followup.start.request.options).toEqual({
      verificationProfileId: 'default',
      includeMetadataTokens: true,
      maxFindings: 1_000,
    })
  })

  it('uses the resolved runtime and security policy for Run', () => {
    const followup = createFollowupOperation(resolution('run', 'run', 'dotnet-10-linux-x64', 'dotnet-10-linux-x64'), 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')

    expect(followup?.start.kind).toBe('run')
    if (followup?.start.kind !== 'run') throw new Error('Expected run request.')
    expect(followup.start.request).toMatchObject({
      runtimeProfileId: 'dotnet-10-linux-x64',
      options: {
        arguments: [],
        stdin: null,
        instrumentation: 'none',
        securityPolicyId: 'runtime-job-default',
      },
    })
  })

  it('passes the dedicated Wine security policy through to Run', () => {
    const followup = createFollowupOperation(resolution('run', 'run', 'wine-netfx48-linux-x64', 'wine-netfx48-linux-x64', 'runtime-job-wine-netfx'), 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')

    expect(followup?.start.kind).toBe('run')
    if (followup?.start.kind !== 'run') throw new Error('Expected run request.')
    expect(followup.start.request).toMatchObject({
      runtimeProfileId: 'wine-netfx48-linux-x64',
      options: {
        securityPolicyId: 'runtime-job-wine-netfx',
      },
    })
  })

  it('uses deterministic JIT settings for comparable assembly', () => {
    const followup = createFollowupOperation(resolution('jit-asm', 'jit', 'dotnet-11-preview-linux-x64', 'dotnet-11-preview-linux-x64'), 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')

    expect(followup?.start.kind).toBe('jit')
    if (followup?.start.kind !== 'jit') throw new Error('Expected JIT request.')
    expect(followup.start.request.options).toEqual({
      methodFilter: null,
      tieringPolicyId: 'tier0-diffable',
      pgoPolicyId: 'disabled',
      providerId: 'coreclr-jitdisasm',
      securityPolicyId: 'runtime-job-default',
    })
  })

  it('always inspects all user methods for the compact JIT document', () => {
    const followup = createFollowupOperation(resolution('jit-asm', 'jit', 'dotnet-10-linux-x64', 'dotnet-10-linux-x64'), 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 1)

    expect(followup?.start.kind).toBe('jit')
    if (followup?.start.kind !== 'jit') throw new Error('Expected JIT request.')
    expect(followup.start.request.options.methodFilter).toBeNull()
  })
})
