import { describe, expect, it } from 'vitest'
import type { PipelineStageDescriptor, ResolveSelectionResponse } from '../api/types'
import { buildTargetForOutput, createBuildRequest } from './buildRequest'
import type { WorkbenchSnapshot } from './storeSnapshot'

describe('buildTargetForOutput', () => {
  it.each([
    ['compile-check', 'compile-check'],
    ['ast', 'ast'],
    ['generated-source', 'generated-source'],
    ['il', 'artifact'],
    ['decompiled-csharp', 'artifact'],
    ['il-verify', 'artifact'],
    ['run', 'artifact'],
    ['jit-asm', 'artifact'],
  ] as const)('maps %s to the %s compiler target', (outputId, target) => {
    expect(buildTargetForOutput(outputId)).toBe(target)
  })
})

describe('createBuildRequest', () => {
  it('uses auto for a non-Run C# pipeline so a class library does not require Main', () => {
    const request = createBuildRequest(
      resolution('il', [
        { id: 'build', kind: 'build', providerId: 'roslyn-stable' },
        { id: 'il', kind: 'render', providerId: 'artifacts-default' },
      ]),
      workspace(),
    )

    expect(request.workspace.buildOptions.outputKind).toBe('auto')
  })

  it('uses console when the resolved pipeline contains a Run stage', () => {
    const request = createBuildRequest(
      resolution('run', [
        { id: 'build', kind: 'build', providerId: 'roslyn-stable' },
        { id: 'run', kind: 'run', providerId: 'dotnet-10-linux-x64' },
      ]),
      workspace(),
    )

    expect(request.workspace.buildOptions.outputKind).toBe('console')
  })

  it('uses library for non-Run IL so decompilation does not require an entry point', () => {
    const request = createBuildRequest(
      resolution(
        'decompiled-csharp',
        [
          { id: 'build', kind: 'build', providerId: 'mobius-ilasm-stable' },
          { id: 'decompiled-csharp', kind: 'render', providerId: 'artifacts-default' },
        ],
        'il',
      ),
      workspace(),
    )

    expect(request.workspace.buildOptions.outputKind).toBe('library')
  })

  it('uses console for IL only when the resolved pipeline contains a Run stage', () => {
    const request = createBuildRequest(
      resolution(
        'run',
        [
          { id: 'build', kind: 'build', providerId: 'mobius-ilasm-stable' },
          { id: 'run', kind: 'run', providerId: 'dotnet-10-linux-x64' },
        ],
        'il',
      ),
      workspace(),
    )

    expect(request.workspace.buildOptions.outputKind).toBe('console')
  })

  it('uses auto for non-Run G# so its worker can distinguish top-level code from a library', () => {
    const request = createBuildRequest(
      resolution(
        'decompiled-csharp',
        [
          { id: 'build', kind: 'build', providerId: 'gsharp-stable' },
          { id: 'decompiled-csharp', kind: 'render', providerId: 'artifacts-default' },
        ],
        'gsharp',
      ),
      workspace(),
    )

    expect(request.workspace.buildOptions.outputKind).toBe('auto')
  })

  it('keeps JIT on auto because inspection does not execute an entry point', () => {
    const request = createBuildRequest(
      resolution('jit-asm', [
        { id: 'build', kind: 'build', providerId: 'roslyn-stable' },
        { id: 'jit-asm', kind: 'jit', providerId: 'dotnet-10-linux-x64' },
      ]),
      workspace(),
    )

    expect(request.workspace.buildOptions.outputKind).toBe('auto')
  })

  it('uses console for Execution Flow because its resolved pipeline contains a Run stage', () => {
    const request = createBuildRequest(
      resolution('execution-flow', [
        { id: 'build', kind: 'build', providerId: 'roslyn-stable' },
        {
          id: 'runtime-instrumentation-v1',
          kind: 'transform',
          providerId: 'artifacts-default',
        },
        { id: 'run', kind: 'run', providerId: 'dotnet-10-linux-x64' },
      ]),
      workspace(),
    )

    expect(request.workspace.buildOptions.outputKind).toBe('console')
  })

  it('keeps Run IL on auto because transform and render stages do not execute an entry point', () => {
    const request = createBuildRequest(
      resolution('run-il', [
        { id: 'build', kind: 'build', providerId: 'roslyn-stable' },
        {
          id: 'runtime-instrumentation-v1',
          kind: 'transform',
          providerId: 'artifacts-default',
        },
        { id: 'run-il', kind: 'render', providerId: 'artifacts-default' },
      ]),
      workspace(),
    )

    expect(request.workspace.buildOptions.outputKind).toBe('auto')
  })
})

function resolution(
  outputId: string,
  stages: PipelineStageDescriptor[],
  languageId = 'csharp',
): ResolveSelectionResponse {
  const runtimeId = stages.some((stage) => stage.kind === 'run' || stage.kind === 'jit')
    ? 'dotnet-10-linux-x64'
    : null
  const toolchainId =
    languageId === 'il'
      ? 'mobius-ilasm-stable'
      : languageId === 'gsharp'
        ? 'gsharp-stable'
        : 'roslyn-stable'
  return {
    effectiveSelection: {
      languageId,
      toolchainId,
      referenceSetId: 'net10-ref',
      outputId,
      runtimeId,
    },
    selectionChanges: [],
    effectiveCapabilities: {
      languageServerCapabilities: [],
      buildCapabilities: ['managed-pe'],
      outputCapabilities: [outputId],
      runtimeCapabilities: [],
    },
    pipelineResolutionId: `pipeline-${outputId}`,
    pipelinePlan: {
      releaseId: 'test-release',
      languageWorkerId: toolchainId,
      compilerWorkerId: toolchainId,
      referenceSetId: 'net10-ref',
      stages,
      runtimeId,
      securityPolicyId: runtimeId ? 'runtime-job-default' : 'compiler-default',
      workerImageIds: [],
    },
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
  }
}

function workspace(): WorkbenchSnapshot {
  return {
    source: 'class Utility { }',
    fileName: 'Program.cs',
    files: [{ path: 'Program.cs', text: 'class Utility { }' }],
    activeFile: 'Program.cs',
    sourceOrder: ['Program.cs'],
    buildMode: 'release',
    workspaceRevision: 2,
    selectionRevision: 3,
  }
}
